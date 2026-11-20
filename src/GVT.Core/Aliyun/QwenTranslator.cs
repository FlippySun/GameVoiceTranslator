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
        return new TranslationResult(Clean(content), inTok, outTok);
    }

    private object BuildMtBody(string text, Direction direction)
    {
        var terms = _glossary.TermsFor(direction).Select(t => new { source = t.Source, target = t.Target }).ToArray();
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
        var (src, tgt) = direction == Direction.ZhToEn ? ("Chinese", "English") : ("English", "Simplified Chinese");
        var terms = string.Join("; ", _glossary.TermsFor(direction).Take(60).Select(t => $"{t.Source} => {t.Target}"));
        var system = new StringBuilder()
            .AppendLine($"You are a real-time voice interpreter for teammates playing Call of Duty: Modern Warfare 4 (DMZ). Translate the user's message from {src} into {tgt}.")
            .AppendLine("Rules:")
            .AppendLine("- Output ONLY the translation. Never answer questions, never comment, never add tactical advice.")
            .AppendLine("- Keep it short and spoken, like a gamer callout.")
            .AppendLine(direction == Direction.ZhToEn
                ? "- Sound like a real American gamer on voice chat: contractions (I'm, don't, gonna, wanna), casual words, no textbook or formal phrasing."
                : "- 用口语化、自然的中文，像队友之间说话。")
            .AppendLine("- Preserve negations, directions (left/right/front/behind/floors), numbers, who-did-what, tense and conditions exactly.")
            .AppendLine("- If the speaker corrects themselves (pattern: A... no, B / A, wait, B / A 不对 B / A 算了 B), translate ONLY B and drop A entirely. Speech recognition often drops the pause, so 'A no B' is also a self-correction.")
            .AppendLine("- Keep the subject exactly: 'I' vs 'he' vs 'they'. 我破甲了 = my armor is broken (I'm cracked); 他破甲了 = his armor is broken (he's cracked).")
            .AppendLine("- Downed is not dead: 倒地/倒了 = downed (can be revived), 死/死了/击杀 = dead/killed. 还没死 = not dead yet.")
            .AppendLine("- Keep time words: already (已经), not yet (还没), still (还). 已经撤了 = already exfilled (finished, not ongoing). Keep questions as questions (where/how many/do you...?).")
            .AppendLine("- 拉我/扶我/救我 = revive me / pick me up (never 'pull me').")
            .AppendLine("- The input comes from speech recognition and may contain misheard English game words. Fix obvious ones using game context before translating (misheard forms of exfil, push, cover, UAV, plates, etc.).")
            .AppendLine($"- Keep player names and words already in {tgt} unchanged.")
            .AppendLine("- Do not add profanity or slurs that are not in the original; tone down extreme insults.")
            .AppendLine("- If the input is only noise or meaningless filler, output nothing.")
            .AppendLine($"Glossary: {terms}")
            .ToString();
        if (recentContext.Count > 0)
            system += "Recent messages (context only, do not translate): " + string.Join(" | ", recentContext.TakeLast(4));

        return new
        {
            model = _cfg.LlmModel,
            temperature = 0.0,
            enable_thinking = false,
            messages = new object[]
            {
                new { role = "system", content = system },
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
