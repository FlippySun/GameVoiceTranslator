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
                list.AddRange(e.ZhAliases.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => (a, e.En)));
            }
            else
            {
                list.Add((e.En, e.Zh));
                list.AddRange(e.EnAliases.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => (a, e.Zh)));
            }
        }
        return list;
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
