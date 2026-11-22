using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GVT.Core.Config;

namespace GVT.Core.Aliyun;

/// <summary>
/// 两种翻译方式，可在配置里切换做 A/B：
/// Mt  = qwen-mt-flash 专用翻译模型，原生术语干预，不会“回答”问题；
/// Llm = qwen-flash 等通用模型 + 规则提示词，可带最近几句上下文。
/// </summary>
public sealed class QwenTranslator : ITranslator
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(10),
    });

    public const string Domain =
        "Real-time voice chat between teammates in the multiplayer FPS game Call of Duty: Modern Warfare 4 (DMZ mode). " +
        "Translate into short, natural, spoken gamer callouts. Preserve negations, directions (left/right/front/behind), floors, " +
        "numbers, who did what, tense and conditions exactly. If the speaker corrects themselves, keep only the corrected meaning.";

    private readonly DashScopeConfig _cfg;
    private readonly string _apiKey;
    private readonly Glossary _glossary;

    public QwenTranslator(DashScopeConfig cfg, string apiKey, Glossary glossary)
    {
        _cfg = cfg;
        _apiKey = apiKey;
        _glossary = glossary;
    }

    /// <summary>请求一次免费的模型列表接口，只为建立并保持连接。失败无所谓。</summary>
    public async Task WarmUpAsync()
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, _cfg.CompatibleBaseUrl.TrimEnd('/') + "/models");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            using var cts = new CancellationTokenSource(5000);
            using var resp = await Http.SendAsync(req, cts.Token).ConfigureAwait(false);
        }
        catch { }
    }

    public async Task<TranslationResult> TranslateAsync(string text, Direction direction, IReadOnlyList<string> recentContext, CancellationToken ct)
    {
        object body = _cfg.Translator == TranslatorKind.Mt ? BuildMtBody(text, direction) : BuildLlmBody(text, direction, recentContext);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_cfg.RequestTimeoutMs);

        using var req = new HttpRequestMessage(HttpMethod.Post, _cfg.CompatibleBaseUrl.TrimEnd('/') + "/chat/completions");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var resp = await Http.SendAsync(req, timeout.Token).ConfigureAwait(false);
        var json = await resp.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new DashScopeException($"翻译请求失败 HTTP {(int)resp.StatusCode}: {Truncate(json, 300)}");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var content = root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
        int inTok = 0, outTok = 0;
        if (root.TryGetProperty("usage", out var u))
        {
            if (u.TryGetProperty("prompt_tokens", out var pt)) inTok = pt.GetInt32();
            if (u.TryGetProperty("completion_tokens", out var ctk)) outTok = ctk.GetInt32();
        }
        if (_cfg.Translator == TranslatorKind.Llm)
        {
            var (t, c) = ParseJson(content);
            return new TranslationResult(Clean(t), inTok, outTok, c);
        }
        return new TranslationResult(Clean(content), inTok, outTok);
    }

    /// <summary>解析 {"t": 译文, "c": "high|low|garbled"}；模型偶尔不按格式输出时按普通文本处理。</summary>
    internal static (string Text, TranslationConfidence Confidence) ParseJson(string content)
    {
        var s = content.Trim();
        if (s.StartsWith("```")) s = s.Trim('`').Replace("json", "", StringComparison.OrdinalIgnoreCase).Trim();
        try
        {
            using var doc = JsonDocument.Parse(s);
            var r = doc.RootElement;
            // 模型偶尔把整个结果包进数组，或者把 t 写成数组/对象：都尽量取出文字
            if (r.ValueKind == JsonValueKind.Array && r.GetArrayLength() > 0) r = r[0];
            if (r.ValueKind != JsonValueKind.Object) return (content, TranslationConfidence.High);
            static string Flatten(JsonElement e) => e.ValueKind switch
            {
                JsonValueKind.String => e.GetString() ?? "",
                JsonValueKind.Array => string.Join("", e.EnumerateArray().Select(Flatten)),
                JsonValueKind.Object => e.TryGetProperty("t", out var inner) ? Flatten(inner) : string.Join("", e.EnumerateObject().Select(p => Flatten(p.Value))),
                _ => "",
            };
            var text = r.TryGetProperty("t", out var t) ? Flatten(t) : "";
            var c = r.TryGetProperty("c", out var cv) && cv.ValueKind == JsonValueKind.String ? cv.GetString() ?? "high" : "high";
            var conf = c.ToLowerInvariant() switch
            {
                "garbled" => TranslationConfidence.Garbled,
                "low" => TranslationConfidence.Low,
                _ => TranslationConfidence.High,
            };
            return (text, string.IsNullOrWhiteSpace(text) ? TranslationConfidence.Garbled : conf);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return (content, TranslationConfidence.High);
        }
    }

    /// <summary>只挑出这句话里真正出现的术语，避免整张术语表把模型往“撤离”之类的词上带。</summary>
    internal static List<(string Source, string Target)> RelevantTerms(Glossary glossary, Direction direction, string text)
        => glossary.TermsFor(direction)
            .Where(t => direction == Direction.ZhToEn
                ? text.Contains(t.Source, StringComparison.Ordinal)
                : System.Text.RegularExpressions.Regex.IsMatch(text, @"\b" + System.Text.RegularExpressions.Regex.Escape(t.Source) + @"\b",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            .ToList();

    private object BuildMtBody(string text, Direction direction)
    {
        var terms = RelevantTerms(_glossary, direction, text).Select(t => new { source = t.Source, target = t.Target }).ToArray();
        var options = new Dictionary<string, object>
        {
            ["source_lang"] = "auto",
            ["target_lang"] = direction == Direction.ZhToEn ? "English" : "Chinese",
            ["domains"] = Domain,
        };
        if (terms.Length > 0) options["terms"] = terms;
        return new
        {
            model = _cfg.MtModel,
            messages = new[] { new { role = "user", content = text } },
            translation_options = options,
        };
    }

    private object BuildLlmBody(string text, Direction direction, IReadOnlyList<string> recentContext)
    {
        bool toEn = direction == Direction.ZhToEn;
        var (src, tgt) = toEn ? ("Chinese", "English") : ("English", "Simplified Chinese");
        var relevant = RelevantTerms(_glossary, direction, text);
        var sb = new StringBuilder()
            .AppendLine($"You are a real-time voice interpreter for teammates playing Call of Duty: Modern Warfare 4 (DMZ). Translate the message from {src} into {tgt}.")
            .AppendLine("The message comes from speech recognition of noisy in-game voice chat, so some words may be misheard.")
            .AppendLine("Rules:")
            .AppendLine("- Translate faithfully. Never answer questions, never comment, never add advice, emotions, idioms or details that are not in the original.")
            .AppendLine("- Keep it short and spoken.")
            .AppendLine(toEn
                ? "- Sound like a real American gamer on voice chat: contractions (I'm, don't, gonna, wanna), casual words, no textbook phrasing."
                : "- 输出自然口语化的简体中文，必须全部是中文，不要夹带英文单词（玩家名字和枪械型号除外）。")
            .AppendLine("- Preserve negations, directions (left/right/front/behind/floors), numbers, who-did-what, tense and conditions exactly.")
            .AppendLine("- If the speaker corrects themselves (pattern: A... no, B / A, wait, B / A 不对 B / A 算了 B), translate ONLY B. Speech recognition often drops the pause, so 'A no B' is also a self-correction.");
        if (toEn)
        {
            sb.AppendLine("- Keep the subject exactly. 我破甲了 = my armor is broken; 他破甲了 = he's cracked (his armor is broken).")
              .AppendLine("- 倒地/倒了 = downed (can be revived); 死/击杀 = dead/killed. 已经 = already, 还没 = not yet.")
              .AppendLine("- 拉我/扶我/救我 = revive me / pick me up. 跑/快跑/跑路 = run / get out of here (not exfil unless 撤离 is said).");
        }
        else
        {
            sb.AppendLine("- down/downed = 倒地 (can be revived); dead/killed = 死了/击杀. already = 已经, not yet = 还没.")
              .AppendLine("- Do not turn ordinary words into game jargon: only say 撤离 when the speaker clearly says exfil / extract.");
        }
        sb.AppendLine("- Misheard words: fix a word only when the intended word is obvious from the sentence and the game context; otherwise translate what is there literally and lower the confidence.")
          .AppendLine("- Do not add profanity or slurs that are not in the original; tone down extreme insults.");
        if (relevant.Count > 0)
            sb.AppendLine("Glossary (use only if the word appears): " + string.Join("; ", relevant.Select(t => $"{t.Source} => {t.Target}")));
        if (recentContext.Count > 0)
            sb.AppendLine("Recent messages from the same speaker (context only, do not translate): " + string.Join(" | ", recentContext.TakeLast(4)));
        sb.AppendLine("Return JSON only: {\"t\": \"<translation>\", \"c\": \"high\" | \"low\" | \"garbled\"}")
          .AppendLine("c = high: the message is clear and the translation is reliable.")
          .AppendLine("c = low: some words were probably misheard, the translation is a best guess.")
          .AppendLine("c = garbled: the message is mostly unintelligible, a broken fragment, or not a real sentence (then t can be empty).");

        return new
        {
            model = _cfg.LlmModel,
            temperature = 0.0,
            enable_thinking = false,
            response_format = new { type = "json_object" },
            messages = new object[]
            {
                new { role = "system", content = sb.ToString() },
                new { role = "user", content = text },
            },
        };
    }

    private static string Clean(string s)
    {
        s = s.Trim();
        if (s.Length >= 2 && (s[0] == '"' && s[^1] == '"' || s[0] == '“' && s[^1] == '”')) s = s[1..^1].Trim();
        return s;
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
