using GVT.Core.Aliyun;
using GVT.Core.Config;
using GVT.Core.Pipeline;
using Xunit;

namespace GVT.Tests;

/// <summary>识别模型的选择与 qwen3-asr 的输出格式。</summary>
public class AsrModelTests
{
    [Theory]
    [InlineData("qwen3-asr-flash-realtime", true, false)]
    [InlineData("qwen3-asr-flash-realtime-2026-02-10", true, false)]
    [InlineData("qwen3-asr-flash", false, true)]
    [InlineData("qwen3-asr-flash-2026-02-10", false, true)]
    [InlineData("fun-asr-realtime", false, false)]
    [InlineData("qwen-audio-3.0-asr-flash-streaming", false, false)]
    public void ModelNames_PickTheRightProtocol(string model, bool realtime, bool oneShot)
    {
        Assert.Equal(realtime, Qwen3AsrRealtimeSession.Handles(model));
        Assert.Equal(oneShot, Qwen3AsrFlashSession.Handles(model));
    }

    [Fact]
    public void RealtimeUrl_UsesRealtimePathWithModel()
    {
        var cfg = new DashScopeConfig();
        Assert.Equal("wss://dashscope.aliyuncs.com/api-ws/v1/realtime?model=qwen3-asr-flash-realtime",
            Qwen3AsrRealtimeSession.RealtimeUrl(cfg, "qwen3-asr-flash-realtime"));
        cfg.WorkspaceId = "ws1";
        Assert.StartsWith("wss://ws1.cn-beijing.maas.aliyuncs.com/api-ws/v1/realtime?model=", Qwen3AsrRealtimeSession.RealtimeUrl(cfg, "qwen3-asr-flash-realtime"));
    }

    [Fact]
    public void Defaults_TeammatesUseQwen3_MeUsesFunAsr()
    {
        var cfg = new AppConfig();
        Assert.Equal("qwen3-asr-flash-realtime", cfg.Receive.AsrModel);
        Assert.Equal("fun-asr-realtime", cfg.Send.AsrModel);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("en", false)]
    [InlineData("zh", false)]
    [InlineData("yue", false)]
    [InlineData("sv", true)]
    [InlineData("ja", true)]
    public void OtherLanguage_IsDroppedOnReceive(string? lang, bool other)
        => Assert.Equal(other, TranslationChannel.IsOtherLanguage(lang));

    /// <summary>qwen3-asr 的写法（句号、大写、拆开的笑声）也要被当成附和 / 噪声。</summary>
    [Theory]
    [InlineData("Okay.")]
    [InlineData("Yeah. Yeah. Okay.")]
    [InlineData("Uh huh.")]
    [InlineData("Mm-hmm.")]
    public void Qwen3StyleBackchannel_IsNotSpoken(string text)
        => Assert.True(TranslationChannel.IsBackchannel(text) || TranslationChannel.IsNoise(text), text);
}

/// <summary>英译中时译文凭空多出原句没有的游戏词（把半句话脑补完整）。</summary>
public class InventedTermTests
{
    private static readonly Glossary G = Glossary.Load(Path.Combine(AudioTests.FindRepoRoot(), "src", "GVT.App", "glossary.default.json"));

    [Theory]
    // 2026-11-23 实际出现的：原句没说 exfil，译文却补出了“撤离点”
    [InlineData("I think Ryan and Talia are headed to get.", "我觉得 Ryan 和 Talia 正往撤离点去。", "撤离点")]
    [InlineData("Let's go.", "我们撤离吧。", "撤离")]
    [InlineData("He's over there.", "他在保险箱那边。", "保险箱")]
    public void FlagsTermsTheSpeakerNeverSaid(string src, string tr, string term)
        => Assert.Equal(term, G.InventedTerm(src, tr));

    [Theory]
    [InlineData("Let's head to the exfil point.", "我们去撤离点。")]
    [InlineData("Head to exfil.", "去撤离点。")]
    [InlineData("I'm extracting now.", "我现在撤离。")]
    [InlineData("There's a safe on the second floor.", "二楼有个保险箱。")]
    [InlineData("Out of here.", "撤了。")]
    [InlineData("The guys didn't make it.", "那几个人没撤出来。")]
    [InlineData("Was there only five items?", "只有五个物资吗？")]
    [InlineData("Put a claymore down.", "放个阔剑地雷。")]
    public void AcceptsTermsThatHaveASource(string src, string tr)
        => Assert.Null(G.InventedTerm(src, tr));
}
