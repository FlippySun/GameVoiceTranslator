using System.Text;
using System.Text.Json;

namespace GVT.Core.Config;

public sealed class GlossaryEntry
{
    public string Zh { get; set; } = "";
    public string En { get; set; } = "";
    /// <summary>中文别名（俗称、简称），都翻成同一个英文。</summary>
    public List<string> ZhAliases { get; set; } = new();
    /// <summary>英文别名（队友可能说的其他说法），都翻成同一个中文。</summary>
    public List<string> EnAliases { get; set; } = new();
    /// <summary>识别服务常把它听成的错写法（如 阔剑 → 扩建）。只在翻译时用来纠正，不会交给识别服务。</summary>
    public List<string> Misheard { get; set; } = new();
    /// <summary>作为识别热词（只放游戏专有词：普通词当热词会把发音相近的词误改过来）。</summary>
    public bool Hotword { get; set; }
    public string Note { get; set; } = "";
}

public sealed class Glossary
{
    public List<GlossaryEntry> Entries { get; set; } = new();

    public static Glossary Load(string path)
    {
        if (!File.Exists(path)) return new Glossary();
        return JsonSerializer.Deserialize<Glossary>(File.ReadAllText(path), AppConfig.JsonOptions) ?? new Glossary();
    }

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, AppConfig.JsonOptions));

    /// <summary>qwen-mt 的 terms 列表（source→target）。</summary>
    public List<(string Source, string Target)> TermsFor(Direction direction)
    {
        var list = new List<(string, string)>();
        foreach (var e in Entries)
        {
            if (string.IsNullOrWhiteSpace(e.Zh) || string.IsNullOrWhiteSpace(e.En)) continue;
            if (direction == Direction.ZhToEn)
            {
                list.Add((e.Zh, e.En));
                list.AddRange(e.ZhAliases.Concat(e.Misheard).Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => (a, e.En)));
            }
            else
            {
                list.Add((e.En, e.Zh));
                list.AddRange(e.EnAliases.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => (a, e.Zh)));
            }
        }
        return list;
    }

    /// <summary>识别热词：标了 Hotword 的词条的正确写法（中文：本名+别名；英文：本名+别名），不含 Misheard。</summary>
    public Dictionary<string, int> HotwordsFor(Direction direction, int weight = 4)
    {
        var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in Entries.Where(e => e.Hotword))
        {
            var words = direction == Direction.ZhToEn ? new[] { e.Zh }.Concat(e.ZhAliases) : new[] { e.En }.Concat(e.EnAliases);
            foreach (var w in words.Select(w => w.Trim()).Where(w => w.Length >= 2)) d[w] = weight;
        }
        return d;
    }

    /// <summary>
    /// 英译中时译文里“凭空多出来”的游戏词：译文含某个术语的中文（如 撤离点），但原句里没有它的任何英文说法的关键词。
    /// 只查三个字以上的专有词和“撤离”（两个字的常用词如 物资、背包 太容易误报）。返回第一个这样的中文词，没有返回 null。
    /// 例：“I think Ryan and Talia are headed to get.” → “正往撤离点去”，原句没有 exfil / extraction。
    /// </summary>
    public string? InventedTerm(string source, string translation)
    {
        var srcWords = System.Text.RegularExpressions.Regex.Matches(source.ToLowerInvariant(), "[a-z]+").Select(m => m.Value).ToList();
        string? invented = null;
        foreach (var e in Entries)
        {
            var zhForms = new[] { e.Zh }.Concat(e.ZhAliases)
                .Where(z => !string.IsNullOrWhiteSpace(z) && (z.Length >= 3 || z == "撤离") && !z.Any(c => c is >= 'A' and <= 'z'));
            var hit = zhForms.Where(translation.Contains).OrderByDescending(z => z.Length).FirstOrDefault();
            if (hit == null || (invented != null && invented.Length >= hit.Length)) continue;
            // 原句里出现这个术语任何英文说法里的一个实词（4 个字母以上），就算有出处
            var keys = new[] { e.En }.Concat(e.EnAliases)
                .SelectMany(en => System.Text.RegularExpressions.Regex.Matches(en.ToLowerInvariant(), "[a-z]+").Select(m => m.Value))
                .Where(w => w.Length >= 4);
            if (!keys.Any(k => srcWords.Any(s => SameWord(s, k)))) invented = hit;
        }
        return invented;

        // 词形变化（extract / extracting、plate / plates）：前 5 个字母相同（短词整词相同）就算同一个词
        static bool SameWord(string a, string b)
        {
            int n = Math.Min(5, Math.Min(a.Length, b.Length));
            return n >= 4 && (a.Length >= 5 && b.Length >= 5 ? a[..5] == b[..5] : a == b);
        }
    }

    /// <summary>
    /// 给大模型识别（qwen3-asr / qwen-audio）的场景说明：一句话交代场景，再列出常用词。
    /// 写成自然语言而不是干巴巴的词表，降低听不清时把词表原样吐出来的风险。最多约 380 字。
    /// </summary>
    public string AsrScenePromptFor(Direction direction, int maxChars = 380)
    {
        var head = direction == Direction.ZhToEn
            ? "这是《使命召唤》DMZ 模式的组队语音，玩家用中文口语交流战术、报点和物资，句子通常很短。常用词有："
            : "Team voice chat in Call of Duty DMZ. Teammates (often Australian) speak casual, short English about tactics, callouts and loot. Common words: ";
        var words = direction == Direction.ZhToEn
            ? Entries.Where(e => e.Hotword).Select(e => e.Zh).Concat(Entries.Select(e => e.Zh))
            : Entries.Where(e => e.Hotword).Select(e => e.En).Concat(Entries.Select(e => e.En));
        var sep = direction == Direction.ZhToEn ? "、" : ", ";
        var sb = new StringBuilder(head);
        int n = 0;
        foreach (var w in words.Where(w => !string.IsNullOrWhiteSpace(w)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (sb.Length + w.Length + 2 > maxChars) break;
            if (n++ > 0) sb.Append(sep);
            sb.Append(w);
        }
        sb.Append(direction == Direction.ZhToEn ? "。" : ".");
        return sb.ToString();
    }

    /// <summary>识别上下文词表（fun-asr-realtime 的 input.context，每轮最多 400 字）。</summary>
    public string AsrContextFor(Direction direction, int maxChars = 380)
    {
        var words = direction == Direction.ZhToEn
            ? Entries.SelectMany(e => new[] { e.Zh }.Concat(e.ZhAliases)).Concat(Entries.Select(e => e.En))
            : Entries.SelectMany(e => new[] { e.En }.Concat(e.EnAliases));
        var sb = new StringBuilder();
        foreach (var w in words.Where(w => !string.IsNullOrWhiteSpace(w)).Distinct())
        {
            if (sb.Length + w.Length + 1 > maxChars) break;
            if (sb.Length > 0) sb.Append('，');
            sb.Append(w);
        }
        return sb.ToString();
    }
}

public enum Direction { ZhToEn, EnToZh }
