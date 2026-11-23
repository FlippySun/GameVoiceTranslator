using System.Globalization;
using System.Text;
using GVT.Core.Config;

namespace GVT.Core.Pipeline;

public static class Log
{
    private static readonly object Lock = new();
    private static string? _file;

    public static event Action<string>? Line;

    public static string LogDirectory
    {
        get
        {
            var d = Path.Combine(AppConfig.ConfigDirectory, "logs");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}";
        lock (Lock)
        {
            _file ??= Path.Combine(LogDirectory, $"app-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            try { File.AppendAllText(_file, line + Environment.NewLine); } catch { }
        }
        Line?.Invoke(line);
    }
}

public static class TextUtil
{
    public static bool IsCjk(char c) => c >= 0x4E00 && c <= 0x9FFF || c >= 0x3400 && c <= 0x4DBF;

    /// <summary>字母类字符里汉字的占比（忽略空格标点数字）。</summary>
    public static double CjkRatio(string s)
    {
        int cjk = 0, letters = 0;
        foreach (var c in s)
        {
            if (IsCjk(c)) { cjk++; letters++; }
            else if (char.IsLetter(c)) letters++;
        }
        // 英文按“词”算更公平：大约 4 个字母折一个汉字
        int latin = letters - cjk;
        double weighted = cjk + latin / 4.0;
        return weighted <= 0 ? 0 : cjk / weighted;
    }

    public static string[] Tokens(string s)
        => s.ToLowerInvariant()
            .Split(new[] { ' ', ',', '.', '!', '?', ';', ':', '"', '\'', '-', '…' }, StringSplitOptions.RemoveEmptyEntries);

    public static double Jaccard(string a, string b)
    {
        var ta = Tokens(a).ToHashSet();
        var tb = Tokens(b).ToHashSet();
        if (ta.Count == 0 || tb.Count == 0) return 0;
        int inter = ta.Count(tb.Contains);
        return inter / (double)(ta.Count + tb.Count - inter);
    }

    /// <summary>CosyVoice 计费字符：汉字算 2，其他算 1。</summary>
    public static int TtsChars(string s) => s.Sum(c => IsCjk(c) ? 2 : 1);
}

/// <summary>
/// 回声防护：对方开外放时，我们发出的英文会被对方麦克风收回来。
/// 接收端识别出的英文如果和最近发出的英文高度相似，就丢弃。
/// </summary>
public sealed class EchoGuard(double threshold, int windowSeconds)
{
    private readonly object _lock = new();
    private readonly List<(DateTime At, string Text)> _recent = new();
    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    public void AddOutgoing(string text)
    {
        lock (_lock)
        {
            _recent.Add((Clock(), text));
            Prune();
        }
    }

    public bool IsEcho(string incoming)
    {
        lock (_lock)
        {
            Prune();
            return _recent.Any(r => TextUtil.Jaccard(r.Text, incoming) >= threshold);
        }
    }

    private void Prune()
    {
        var cutoff = Clock() - TimeSpan.FromSeconds(windowSeconds);
        _recent.RemoveAll(r => r.At < cutoff);
    }
}

public sealed class UsageMeter(BudgetConfig budget)
{
    private readonly object _lock = new();
    public double AsrSeconds { get; private set; }
    public long TtsChars { get; private set; }
    public long MtInputTokens { get; private set; }
    public long MtOutputTokens { get; private set; }
    public long AsrInputTokens { get; private set; }
    public long AsrOutputTokens { get; private set; }
    /// <summary>到达预算后用户点“再加一些继续”追加的额度。</summary>
    public double ExtraBudgetYuan { get; set; }
    public double BudgetYuan => budget.SessionBudgetYuan + ExtraBudgetYuan;

    public event Action? Changed;

    public double CostYuan
    {
        get
        {
            lock (_lock)
                return AsrSeconds * budget.AsrYuanPerSecond
                       + TtsChars / 10000.0 * budget.TtsYuanPer10kChars
                       + MtInputTokens / 1e6 * budget.MtYuanPerMInputTokens
                       + MtOutputTokens / 1e6 * budget.MtYuanPerMOutputTokens
                       + AsrInputTokens / 1e6 * budget.AsrYuanPerMInputTokens
                       + AsrOutputTokens / 1e6 * budget.AsrYuanPerMOutputTokens;
        }
    }

    public bool OverBudget => BudgetYuan > 0 && CostYuan >= BudgetYuan;

    public void AddAsr(double seconds) { lock (_lock) AsrSeconds += Math.Ceiling(seconds); Changed?.Invoke(); }
    /// <summary>qwen-audio 系列识别按 token 计费。</summary>
    public void AddAsrTokens(int input, int output) { lock (_lock) { AsrInputTokens += input; AsrOutputTokens += output; } Changed?.Invoke(); }
    public void AddTts(int chars) { lock (_lock) TtsChars += chars; Changed?.Invoke(); }
    public void AddMt(int inTok, int outTok) { lock (_lock) { MtInputTokens += inTok; MtOutputTokens += outTok; } Changed?.Invoke(); }

    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture,
            $"估算 ¥{CostYuan:F3} / 预算 ¥{BudgetYuan:F2}（识别 {AsrSeconds:F0}s，合成 {TtsChars} 字符，翻译 {MtInputTokens}+{MtOutputTokens} tokens）");
}

public enum SubtitleKind { Partial, Source, Translation, Info, Dropped, Error }

public sealed record SubtitleEvent(string Channel, long UtteranceId, SubtitleKind Kind, string Text, DateTime At);

public sealed class LatencyRecord
{
    public string Channel { get; init; } = "";
    public long Id { get; init; }
    public string Source { get; set; } = "";
    public string Translation { get; set; } = "";
    public double AudioSeconds { get; set; }
    public double? EndToAsrMs { get; set; }
    public double? EndToMtMs { get; set; }
    public double? EndToTtsFirstMs { get; set; }
    public double? EndToPlayMs { get; set; }
    public string Outcome { get; set; } = "";

    public const string CsvHeader = "time,channel,id,audio_s,end_to_asr_ms,end_to_mt_ms,end_to_tts_first_ms,end_to_play_ms,outcome,source,translation";

    public string ToCsv()
    {
        static string F(double? v) => v.HasValue ? v.Value.ToString("F0", CultureInfo.InvariantCulture) : "";
        static string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
        return string.Join(',', DateTime.Now.ToString("HH:mm:ss.fff"), Channel, Id,
            AudioSeconds.ToString("F2", CultureInfo.InvariantCulture),
            F(EndToAsrMs), F(EndToMtMs), F(EndToTtsFirstMs), F(EndToPlayMs), Outcome, Q(Source), Q(Translation));
    }

    public override string ToString()
    {
        static string F(double? v) => v.HasValue ? $"{v.Value:F0}" : "-";
        return $"[{Channel}#{Id}] 识别 {F(EndToAsrMs)} / 翻译 {F(EndToMtMs)} / 合成首包 {F(EndToTtsFirstMs)} / 开播 {F(EndToPlayMs)} ms · {Outcome}";
    }
}

public sealed class LatencyCsv
{
    private readonly object _lock = new();
    private readonly string _path;

    public LatencyCsv(string? path = null)
    {
        _path = path ?? Path.Combine(Log.LogDirectory, $"latency-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        File.WriteAllText(_path, LatencyRecord.CsvHeader + Environment.NewLine, new UTF8Encoding(true));
    }

    public string FilePath => _path;

    public void Append(LatencyRecord r)
    {
        lock (_lock) File.AppendAllText(_path, r.ToCsv() + Environment.NewLine);
    }
}
