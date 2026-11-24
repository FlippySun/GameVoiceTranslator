using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GVT.Core.Audio;
using GVT.Core.Config;

namespace GVT.Core.Aliyun;

/// <summary>
/// qwen3-asr-flash（非实时）：说完后把整句录音一次性发过去识别。没有中间结果，句末要多等一次上传 + 识别；
/// 好处是模型能看到整句，并能用一段文字上下文（系统消息）提示场景和术语。
/// </summary>
public sealed class Qwen3AsrFlashSession : IAsrSession
{
    public const string ModelPrefix = "qwen3-asr-flash";

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        EnableMultipleHttp2Connections = true,
    }) { Timeout = Timeout.InfiniteTimeSpan };

    private readonly DashScopeConfig _cfg;
    private readonly string _apiKey;
    private readonly AsrRequest _req;
    private readonly List<float> _pcm = new();
    private readonly CancellationTokenSource _cts = new();

    public event Action<string>? Partial;
    public double AudioSeconds { get { lock (_pcm) return _pcm.Count / 16000.0; } }
    public float[] CapturedAudio { get { lock (_pcm) return _pcm.ToArray(); } }
    public string Model { get; }
    public string? Language { get; private set; }

    /// <summary>非实时版（不含 -realtime）。</summary>
    public static bool Handles(string? model) => model != null && model.StartsWith(ModelPrefix, StringComparison.OrdinalIgnoreCase)
                                                 && !model.Contains("realtime", StringComparison.OrdinalIgnoreCase);

    public Qwen3AsrFlashSession(DashScopeConfig cfg, string apiKey, AsrRequest req)
    {
        _cfg = cfg;
        _apiKey = apiKey;
        _req = req;
        Model = req.Model ?? ModelPrefix;
    }

    public void Push(ReadOnlySpan<float> samples16k)
    {
        lock (_pcm) foreach (var s in samples16k) _pcm.Add(s);
    }

    public async Task<string> FinishAsync(CancellationToken ct)
    {
        float[] pcm;
        lock (_pcm) pcm = _pcm.ToArray();
        if (pcm.Length < 1600) return "";

        var messages = new List<object>();
        if (!string.IsNullOrWhiteSpace(_req.Context))
            messages.Add(new { role = "system", content = new[] { new { text = _req.Context } } });
        messages.Add(new { role = "user", content = new[] { new { audio = "data:audio/wav;base64," + Convert.ToBase64String(Wav(pcm)) } } });
        var asrOptions = new Dictionary<string, object> { ["enable_itn"] = false };
        if (!string.IsNullOrWhiteSpace(_req.LanguageHint)) asrOptions["language"] = _req.LanguageHint!;

        var body = JsonSerializer.Serialize(new
        {
            model = Model,
            input = new { messages },
            parameters = new { asr_options = asrOptions },
        });
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        cts.CancelAfter(_cfg.AsrFinishTimeoutMs + 2000);
        using var req = new HttpRequestMessage(HttpMethod.Post, _cfg.ApiBaseUrl + "/services/aigc/multimodal-generation/generation")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        using var resp = await Http.SendAsync(req, cts.Token).ConfigureAwait(false);
        var json = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new DashScopeException($"识别失败 {(int)resp.StatusCode}: {json}");

        using var doc = JsonDocument.Parse(json);
        var sb = new StringBuilder();
        if (doc.RootElement.TryGetProperty("output", out var o) && o.TryGetProperty("choices", out var ch))
            foreach (var c in ch.EnumerateArray())
            {
                if (!c.TryGetProperty("message", out var m)) continue;
                if (m.TryGetProperty("annotations", out var an) && an.ValueKind == JsonValueKind.Array)
                    foreach (var a in an.EnumerateArray())
                        if (a.TryGetProperty("language", out var l)) Language = l.GetString();
                if (m.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                    foreach (var part in content.EnumerateArray())
                        if (part.TryGetProperty("text", out var t)) sb.Append(t.GetString());
            }
        var text = sb.ToString().Trim();
        Partial?.Invoke(text);
        return text;
    }

    public void Abort() => _cts.Cancel();

    public ValueTask DisposeAsync()
    {
        _cts.Dispose();
        return ValueTask.CompletedTask;
    }

    private static byte[] Wav(float[] pcm)
    {
        var data = PcmConvert.FloatToPcm16(pcm);
        using var ms = new MemoryStream(44 + data.Length);
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + data.Length); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(16000); w.Write(32000); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(data.Length); w.Write(data);
        w.Flush();
        return ms.ToArray();
    }
}
