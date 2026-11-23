using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GVT.Core.Config;
using GVT.Core.Pipeline;

namespace GVT.Core.Aliyun;

/// <summary>
/// 识别热词表（预编译热词，fun-asr-realtime 用 vocabulary_id 引用）。
/// 和“上下文”不同，热词只是让发音相近时偏向这个写法，不会被原样吐出来（实测 485 段真实录音：“扩建地雷”→“阔剑地雷”，其余结果几乎不变）。
/// 表 ID 缓存在配置目录的 asr-vocabulary.json，词条变了才重建。
/// </summary>
public static class AsrVocabulary
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private sealed record Entry(string Id, string Hash, string Model);

    private static string CachePath => Path.Combine(AppConfig.ConfigDirectory, "asr-vocabulary.json");

    /// <summary>确保有一张和当前词条一致的热词表，返回 ID；失败返回 null（不影响识别，只是没有热词）。</summary>
    public static async Task<string?> EnsureAsync(DashScopeConfig cfg, string apiKey, IReadOnlyDictionary<string, int> words, string lang, CancellationToken ct = default)
    {
        if (words.Count == 0) return null;
        var model = cfg.AsrModel;
        var hash = Hash(model, lang, words);
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var cache = LoadCache();
            if (cache.TryGetValue(lang, out var old) && old.Hash == hash) return old.Id;

            var id = await CreateAsync(cfg, apiKey, model, words, lang, ct).ConfigureAwait(false);
            await DeleteOthersAsync(cfg, apiKey, "gvt" + lang, id).ConfigureAwait(false);
            cache[lang] = new Entry(id, hash, model);
            SaveCache(cache);
            Log.Info($"识别热词表已更新（{lang}，{words.Count} 个词）");
            return id;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"识别热词表创建失败，本次不用热词：{ex.Message}");
            return null;
        }
        finally { Gate.Release(); }
    }

    public static async Task<string> CreateAsync(DashScopeConfig cfg, string apiKey, string targetModel, IReadOnlyDictionary<string, int> words, string lang, CancellationToken ct = default)
    {
        var doc = await PostAsync(cfg, apiKey, new
        {
            model = "speech-biasing",
            input = new
            {
                action = "create_vocabulary",
                target_model = targetModel,
                prefix = "gvt" + lang,
                vocabulary = words.Select(w => new { text = w.Key, weight = w.Value, lang }).ToArray(),
            },
        }, ct).ConfigureAwait(false);
        var id = doc.RootElement.GetProperty("output").GetProperty("vocabulary_id").GetString()!;
        for (int i = 0; i < 30; i++)
        {
            var q = await PostAsync(cfg, apiKey, new { model = "speech-biasing", input = new { action = "query_vocabulary", vocabulary_id = id } }, ct).ConfigureAwait(false);
            if (q.RootElement.GetProperty("output").TryGetProperty("status", out var st) && st.GetString() == "OK") return id;
            await Task.Delay(1000, ct).ConfigureAwait(false);
        }
        throw new TimeoutException("热词表一直没有就绪");
    }

    /// <summary>删掉同前缀的其他热词表（旧版本或之前没记下来的），每个账号最多 50 张。</summary>
    /// <summary>调试用：列出某前缀下的热词表 ID。</summary>
    public static async Task<List<string>> ListAsync(DashScopeConfig cfg, string apiKey, string prefix)
    {
        var doc = await PostAsync(cfg, apiKey, new { model = "speech-biasing", input = new { action = "list_vocabulary", prefix, page_index = 0, page_size = 50 } }, default).ConfigureAwait(false);
        return doc.RootElement.GetProperty("output").GetProperty("vocabulary_list").EnumerateArray()
            .Select(v => v.GetProperty("vocabulary_id").GetString() ?? "").ToList();
    }

    private static async Task DeleteOthersAsync(DashScopeConfig cfg, string apiKey, string prefix, string keepId)
    {
        try
        {
            var doc = await PostAsync(cfg, apiKey, new { model = "speech-biasing", input = new { action = "list_vocabulary", prefix, page_index = 0, page_size = 50 } }, default).ConfigureAwait(false);
            if (!doc.RootElement.TryGetProperty("output", out var o) || !o.TryGetProperty("vocabulary_list", out var list))
            {
                Log.Warn("热词表列表格式不认识：" + doc.RootElement.GetRawText()[..Math.Min(200, doc.RootElement.GetRawText().Length)]);
                return;
            }
            foreach (var v in list.EnumerateArray())
            {
                var id = v.TryGetProperty("vocabulary_id", out var iv) ? iv.GetString() : null;
                if (id != null && id != keepId) await DeleteAsync(cfg, apiKey, id).ConfigureAwait(false);
            }
        }
        catch (Exception ex) { Log.Warn("清理旧热词表失败：" + ex.Message); }
    }

    public static async Task DeleteAsync(DashScopeConfig cfg, string apiKey, string id)
    {
        try { await PostAsync(cfg, apiKey, new { model = "speech-biasing", input = new { action = "delete_vocabulary", vocabulary_id = id } }, default).ConfigureAwait(false); }
        catch (Exception ex) { Log.Warn("删除旧热词表失败：" + ex.Message); }
    }

    private static async Task<JsonDocument> PostAsync(DashScopeConfig cfg, string apiKey, object body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, cfg.ApiBaseUrl.TrimEnd('/') + "/services/audio/asr/customization");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new DashScopeException($"热词接口 HTTP {(int)resp.StatusCode}: {(json.Length > 300 ? json[..300] : json)}");
        return JsonDocument.Parse(json);
    }

    private static string Hash(string model, string lang, IReadOnlyDictionary<string, int> words)
    {
        var s = model + "|" + lang + "|" + string.Join(";", words.OrderBy(w => w.Key, StringComparer.Ordinal).Select(w => $"{w.Key}={w.Value}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..16];
    }

    private static Dictionary<string, Entry> LoadCache()
    {
        try { return File.Exists(CachePath) ? JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(CachePath)) ?? new() : new(); }
        catch { return new(); }
    }

    private static void SaveCache(Dictionary<string, Entry> cache)
    {
        try { File.WriteAllText(CachePath, JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception ex) { Log.Warn("保存热词表缓存失败：" + ex.Message); }
    }
}
