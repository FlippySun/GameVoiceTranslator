using GVT.Core.Pipeline;

namespace GVT.Tests;

public class PauseRestorerTests
{
    /// <summary>按 (起始ms, 结束ms, 是否有声) 生成 16 kHz 测试音频。</summary>
    private static float[] Audio(int totalMs, params (int From, int To)[] voiced)
    {
        var a = new float[totalMs * 16];
        foreach (var (from, to) in voiced)
            for (int i = from * 16; i < to * 16; i++) a[i] = 0.3f * MathF.Sin(i * 0.2f);
        return a;
    }

    [Fact]
    public void InsertsEllipsis_WhenPauseIsHiddenInsidePreviousWord()
    {
        // 和真实识别结果一样：left 的时长吞掉了后面 500ms 的停顿
        var words = new List<AsrWord>
        {
            new(0, 900, "left", ""),
            new(900, 1200, " no", ""),
            new(1200, 1500, " right", ""),
            new(1500, 1900, " building", ". "),
        };
        var audio = Audio(2000, (0, 400), (900, 1900));
        Assert.Equal("left… no right building.", PauseRestorer.Restore(words, audio));
    }

    [Fact]
    public void LeavesFluentSpeechUntouched()
    {
        var words = new List<AsrWord> { new(0, 300, "左边", ""), new(300, 600, "二楼", "。") };
        Assert.Equal("左边二楼。", PauseRestorer.Restore(words, Audio(700, (0, 600))));
    }

    [Fact]
    public void DoesNotDoubleUpExistingPunctuation()
    {
        var words = new List<AsrWord> { new(0, 900, "左边", "，"), new(900, 1200, "不对", "。") };
        Assert.Equal("左边，不对。", PauseRestorer.Restore(words, Audio(1300, (0, 300), (900, 1200))));
    }
}
