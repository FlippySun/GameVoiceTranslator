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
