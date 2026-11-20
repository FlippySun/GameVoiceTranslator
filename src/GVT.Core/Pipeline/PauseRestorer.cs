using System.Text;

namespace GVT.Core.Pipeline;

public sealed record AsrWord(long BeginMs, long EndMs, string Text, string Punctuation);

/// <summary>
/// 识别服务常把“左边……不对，右边”里的停顿吞掉，输出 “left no right”，翻译就可能把改口理解反。
/// 识别结果的字级时间戳会把停顿算进前一个词的时长，所以直接看间隔找不到停顿；
/// 这里改用我们手里的原始音频：两个词交界附近如果有一段明显静音，就在中间补“……”。
/// </summary>
public static class PauseRestorer
{
    private const int FrameMs = 20;

    public static string Restore(IReadOnlyList<AsrWord> words, ReadOnlySpan<float> audio16k, int minPauseMs = 220)
    {
        if (words.Count == 0) return "";
        var silent = SilentFrames(audio16k);
        var sb = new StringBuilder();
        for (int i = 0; i < words.Count; i++)
        {
            var w = words[i];
            sb.Append(w.Text);
            sb.Append(w.Punctuation);
            if (i == words.Count - 1 || !string.IsNullOrWhiteSpace(w.Punctuation)) continue;
            // 停顿可能在前一个词的尾部，也可能在交界处：检查 [本词开始, 下一个词开始 + 100ms]
            var next = words[i + 1];
            if (LongestSilenceMs(silent, w.BeginMs, next.BeginMs + 100) >= minPauseMs) sb.Append('…');
        }
        return sb.ToString().Trim();
    }

    /// <summary>每 20 ms 一帧，比本句语音电平低 25 dB 以上（或低于 -50 dBFS）视为静音。</summary>
    private static bool[] SilentFrames(ReadOnlySpan<float> audio)
    {
        int frameLen = 16 * FrameMs;
        int n = audio.Length / frameLen;
        var db = new float[n];
        for (int f = 0; f < n; f++)
        {
            double acc = 0;
            for (int i = 0; i < frameLen; i++) { var s = audio[f * frameLen + i]; acc += s * s; }
            var rms = Math.Sqrt(acc / frameLen);
            db[f] = rms < 1e-6 ? -120f : (float)(20 * Math.Log10(rms));
        }
        var sorted = db.OrderBy(v => v).ToArray();
        float speech = sorted.Length == 0 ? -120f : sorted[(int)(sorted.Length * 0.9)];
        float threshold = Math.Max(speech - 25f, -50f);
        return db.Select(v => v < threshold).ToArray();
    }

    private static int LongestSilenceMs(bool[] silent, long fromMs, long toMs)
    {
        int a = (int)Math.Max(0, fromMs / FrameMs), b = (int)Math.Min(silent.Length, toMs / FrameMs);
        int best = 0, run = 0;
        for (int f = a; f < b; f++)
        {
            run = silent[f] ? run + 1 : 0;
            best = Math.Max(best, run);
        }
        return best * FrameMs;
    }
}
