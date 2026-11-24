using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GVT.Core.Config;
using GVT.Core.Pipeline;

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
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_cfg.RequestTimeoutMs);
        try
        {
            if (_cfg.Translator == TranslatorKind.Mt)
            {
                var (content, i, o) = await CompleteAsync(BuildMtBody(text, direction), timeout.Token).ConfigureAwait(false);
                return Finish(text, Clean(content), TranslationConfidence.High, direction, i, o);
            }

            var (system, user) = BuildLlmMessages(text, direction, recentContext);
            var messages = new List<object> { new { role = "system", content = system }, new { role = "user", content = user } };
            var (c1, in1, out1) = await CompleteAsync(LlmBody(messages), timeout.Token).ConfigureAwait(false);
            var (t1, conf1) = ParseJson(c1);
            t1 = Clean(t1);
            var problem = conf1 is TranslationConfidence.Garbled ? null : Problem(text, t1, direction) ?? InventedProblem(text, t1, direction);
            if (problem == null) return new TranslationResult(t1, in1, out1, conf1);

            // 译文混进了另一种语言、只剩一串数字、或者凭空多出原句没有的游戏词：指出问题让模型重翻一次
            Log.Info($"译文不合格（{problem}），重翻：{text} → {t1}");
            messages.Add(new { role = "assistant", content = c1 });
            messages.Add(new { role = "user", content = "Your translation is invalid: " + problem + " Return the JSON again." });
            var (c2, in2, out2) = await CompleteAsync(LlmBody(messages), timeout.Token).ConfigureAwait(false);
            var (t2, conf2) = ParseJson(c2);
            var result = Finish(text, Clean(t2), conf2, direction, in1 + in2, out1 + out2);
            // 重翻后还是多出了原句没有的词：照常播报，但标成“可能是……”
            if (result.Confidence == TranslationConfidence.High && InventedProblem(text, result.Text, direction) != null)
                result = result with { Confidence = TranslationConfidence.Low };
            return result;
        }
        catch (DashScopeException ex) when (ex.Message.Contains("data_inspection_failed"))
        {
            // 平台审核拦截（多半是骂人的话）：不播报，也不当成故障
            return new TranslationResult("", 0, 0, TranslationConfidence.Blocked);
        }
    }

    /// <summary>重翻后仍不合格时的兜底：英文里的中文去掉；只剩数字的当没听清；中文里剩几个英文词可以接受。</summary>
    private static TranslationResult Finish(string source, string t, TranslationConfidence conf, Direction d, int inTok, int outTok)
    {
        var problem = conf is TranslationConfidence.Garbled ? null : Problem(source, t, d);
        if (problem == null) return new TranslationResult(t, inTok, outTok, conf);
        if (d == Direction.ZhToEn && t.Any(TextUtil.IsCjk))
        {
            var stripped = System.Text.RegularExpressions.Regex.Replace(new string(t.Select(ch => TextUtil.IsCjk(ch) ? ' ' : ch).ToArray()), @"\s{2,}", " ").Trim(' ', ',', '，');
            // 去掉中文后句子断了（“short on the .”）就别发了，让用户再说一遍
            return stripped.Any(char.IsLetter) && !TranslationChannel.EndsWithFunctionWord(stripped)
                ? new TranslationResult(stripped, inTok, outTok, TranslationConfidence.Low)
                : new TranslationResult("", inTok, outTok, TranslationConfidence.Garbled);
        }
        if (IsOnlyNumber(t)) return new TranslationResult("", inTok, outTok, TranslationConfidence.Garbled);
        return new TranslationResult(t, inTok, outTok, conf);
    }

    private static readonly System.Text.RegularExpressions.Regex NumberWords = new(
        @"\b(zero|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|thirteen|fourteen|fifteen|sixteen|seventeen|eighteen|nineteen|twenty|thirty|forty|fifty|sixty|seventy|eighty|ninety|hundred|thousand|oh|o)\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static bool IsOnlyNumber(string t) => t.Any(char.IsDigit) && !t.Any(char.IsLetter);

    /// <summary>英译中时译文里凭空多出原句没有的游戏词（多半是把半句话脑补完整了）：返回给模型看的问题说明。</summary>
    internal string? InventedProblem(string source, string translation, Direction d)
    {
        if (d != Direction.EnToZh || string.IsNullOrWhiteSpace(translation)) return null;
        var term = _glossary.InventedTerm(source, translation);
        return term == null ? null
            : $"it adds \"{term}\", which the original never says. Translate only the words that are there and never complete a cut-off sentence; if it is cut off, translate the part that was said and end with \"……\".";
    }

    /// <summary>检查译文是否合格；合格返回 null，否则返回给模型看的问题说明。</summary>
    internal static string? Problem(string source, string translation, Direction d)
    {
        if (string.IsNullOrWhiteSpace(translation)) return null;
        if (IsOnlyNumber(translation))
        {
            // 原句里除了数字还有别的词，译文却只剩数字；或者凭空多出小数点、冒号（“301”→“301.201”，“oh four six”→“0.46”）
            var otherWords = System.Text.RegularExpressions.Regex.Matches(source, @"[A-Za-z']+").Count(m => !NumberWords.IsMatch(m.Value));
            bool sourceHasNumber = source.Any(char.IsDigit) || NumberWords.IsMatch(source) || source.Any(c => "零一二三四五六七八九十百千两".Contains(c));
            var srcCore = source.TrimEnd('.', '!', '?', '。', '！', '？', '…', ' ');
            bool inventedPunct = translation.Count(c => c is '.' or ':') > srcCore.Count(c => c is '.' or ':');
            if (!sourceHasNumber || otherWords > 0 || inventedPunct)
                return "it is only a number, which is not what the message says. Translate the words, and keep numbers exactly as spoken.";
        }
        if (d == Direction.ZhToEn)
            return translation.Any(TextUtil.IsCjk) ? "it still contains Chinese characters. Output natural English only." : null;
        var leftovers = System.Text.RegularExpressions.Regex.Matches(translation, @"[A-Za-z][A-Za-z']+")
            .Select(m => m.Value)
            .Where(w => !IsAllowedLatin(w, source))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return leftovers.Count > 0 ? $"it still contains English words ({string.Join(", ", leftovers)}). Output Simplified Chinese only." : null;
    }

    /// <summary>中文译文里允许保留的拉丁字母：缩写（DMZ、UAV、AI、OK）和原句里大写开头的专名（玩家名、枪名）。</summary>
    private static bool IsAllowedLatin(string w, string source)
    {
        if (w.All(c => !char.IsLetter(c) || char.IsUpper(c))) return true;
        if (!char.IsUpper(w[0])) return false;
        var idx = source.IndexOf(w, StringComparison.Ordinal);
        // 句首的大写词不算专名（Okay / Right / Jesus 之类）
        return idx > 0 && source[..idx].TrimEnd().Length > 0 && !".!?。！？…".Contains(source[..idx].TrimEnd()[^1]);
    }

    private async Task<(string Content, int In, int Out)> CompleteAsync(object body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, _cfg.CompatibleBaseUrl.TrimEnd('/') + "/chat/completions");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
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
        return (content, inTok, outTok);
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

    internal (string System, string User) BuildLlmMessages(string text, Direction direction, IReadOnlyList<string> recentContext)
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
              .AppendLine("- 拉我/扶我/救我 = revive me / pick me up. 跑/快跑/跑路 = run / get out of here (not exfil unless 撤离 is said).")
              .AppendLine("- 我去 + a place or action = I'm going (to ...). Only a standalone 我去！ is an exclamation.")
              .AppendLine("- Gamer slang: 吃 / 舔 / 舔包 / 摸 (吃东西, 去吃, 把他吃了, 吃完) mean loot / grab the loot, never eat (only 吃饭 is real food). 肥 = loaded with loot. 人机 = AI bots.");
        }
        else
        {
            sb.AppendLine("- down/downed = 倒地 (can be revived); dead/killed = 死了/击杀. already = 已经, not yet = 还没.")
              .AppendLine("- Do not turn ordinary words into game jargon: only say 撤离 when the speaker clearly says exfil / extract.")
              .AppendLine("- Translate idioms, slang and exclamations by meaning, not word by word (an exclamation like \"Jesus\" or \"oh my god\" is 天哪 / 我的天).");
        }
        sb.AppendLine("- If the message is an incomplete fragment, translate only the words that are there. Never invent the missing part or add a subject, place or action that was not said; a fragment that means nothing on its own is garbled.")
          .AppendLine(toEn
              ? "  Example: 他们正要去 → They're heading to… (not 'heading to exfil')."
              : "  Example: \"They're headed to get.\" → 他们正要去拿…… (not 去撤离点). A cut-off sentence ends with ……");
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

        return (sb.ToString(), text);
    }

    private object LlmBody(List<object> messages) => new
    {
        model = _cfg.LlmModel,
        temperature = 0.0,
        enable_thinking = false,
        response_format = new { type = "json_object" },
        messages,
    };

    private static string Clean(string s)
    {
        s = s.Trim();
        if (s.Length >= 2 && (s[0] == '"' && s[^1] == '"' || s[0] == '“' && s[^1] == '”')) s = s[1..^1].Trim();
        return s;
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
