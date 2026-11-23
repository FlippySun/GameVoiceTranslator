using GVT.Core.Config;
using GVT.Core.Pipeline;

namespace GVT.Tests;

public class TextTests
{
    [Theory]
    [InlineData("左边二楼有两个人", true)]
    [InlineData("先别 push，等我", true)]
    [InlineData("Two on the left", false)]
    [InlineData("I'm going to exfil", false)]
    public void CjkRatio_ClassifiesLanguage(string text, bool chinese)
        => Assert.Equal(chinese, TextUtil.CjkRatio(text) > 0.5);

    [Fact]
    public void EchoGuard_ExpiresOldEntries()
    {
        var now = DateTime.UtcNow;
        var g = new EchoGuard(0.6, 10) { Clock = () => now };
        g.AddOutgoing("cover me I'm reloading");
        Assert.True(g.IsEcho("Cover me, I'm reloading."));
        Assert.False(g.IsEcho("enemy behind you"));
        now = now.AddSeconds(11);
        Assert.False(g.IsEcho("Cover me, I'm reloading."));
    }

    [Fact]
    public void TtsChars_CountsChineseAsTwo() => Assert.Equal(2 * 2 + 3, TextUtil.TtsChars("你好abc"));

    [Fact]
    public void Glossary_BuildsTermsBothWays()
    {
        var g = new Glossary
        {
            Entries =
            {
                new GlossaryEntry { Zh = "撤离", En = "exfil", ZhAliases = { "撤" }, EnAliases = { "extract" } },
            },
        };
        Assert.Contains(("撤", "exfil"), g.TermsFor(Direction.ZhToEn));
        Assert.Contains(("extract", "撤离"), g.TermsFor(Direction.EnToZh));
        Assert.Contains("撤离", g.AsrContextFor(Direction.ZhToEn));
    }

    [Fact]
    public void DefaultGlossaryFile_IsValid()
    {
        var path = Path.Combine(AudioTests.FindRepoRoot(), "src", "GVT.App", "glossary.default.json");
        var g = Glossary.Load(path);
        Assert.True(g.Entries.Count > 20);
        Assert.All(g.Entries, e => { Assert.False(string.IsNullOrWhiteSpace(e.Zh)); Assert.False(string.IsNullOrWhiteSpace(e.En)); });
        Assert.True(g.AsrContextFor(Direction.ZhToEn).Length <= 400);
    }

    [Fact]
    public void UsageMeter_EstimatesCost()
    {
        var u = new UsageMeter(new BudgetConfig());
        u.AddAsr(1000);
        u.AddTts(10000);
        Assert.Equal(0.33 + 1.0, u.CostYuan, 3);
    }
}

public class FillerTests
{
    [Theory]
    [InlineData("hmm.", true)]
    [InlineData("Uh, um...", true)]
    [InlineData("嗯。", true)]
    [InlineData("呃，啊", true)]
    [InlineData("Yeah.", false)]
    [InlineData("Uh, enemy behind you", false)]
    [InlineData("嗯，左边", false)]
    [InlineData("。", true)]
    public void SkipsFillerOnlyUtterances(string text, bool noise)
        => Assert.Equal(noise, GVT.Core.Pipeline.TranslationChannel.IsNoise(text));
}

public class RecognitionQualityTests
{
    [Theory]
    [InlineData("yeah", true)]
    [InlineData("Okay.", true)]
    [InlineData("thank you sir", true)]
    [InlineData("oh my god", true)]
    [InlineData("let's go", true)]
    [InlineData("the", true)]
    [InlineData("prison", true)]
    [InlineData("behind", false)]
    [InlineData("Sniper!", false)]
    [InlineData("go right", false)]
    [InlineData("yeah, behind you", false)]
    [InlineData("two on the left", false)]
    [InlineData("where is he", false)]
    public void Backchannel_SkipsFillersAndFragments_KeepsCallouts(string text, bool skip)
        => Assert.Equal(skip, GVT.Core.Pipeline.TranslationChannel.IsBackchannel(text));

    [Fact]
    public void ParseJson_ReadsConfidence()
    {
        var (t, c) = GVT.Core.Aliyun.QwenTranslator.ParseJson("{\"t\":\"可能是钥匙\",\"c\":\"low\"}");
        Assert.Equal("可能是钥匙", t);
        Assert.Equal(GVT.Core.Aliyun.TranslationConfidence.Low, c);
        Assert.Equal(GVT.Core.Aliyun.TranslationConfidence.Garbled, GVT.Core.Aliyun.QwenTranslator.ParseJson("{\"t\":\"\",\"c\":\"high\"}").Confidence);
        Assert.Equal(("plain text", GVT.Core.Aliyun.TranslationConfidence.High), GVT.Core.Aliyun.QwenTranslator.ParseJson("plain text"));
    }

    [Fact]
    public void RelevantTerms_OnlyIncludesWordsInTheSentence()
    {
        var g = new Glossary { Entries = { new GlossaryEntry { Zh = "撤离", En = "exfil" }, new GlossaryEntry { Zh = "破甲", En = "armor broken", EnAliases = { "cracked" } } } };
        Assert.Empty(GVT.Core.Aliyun.QwenTranslator.RelevantTerms(g, Direction.ZhToEn, "左边二楼有两个人"));
        Assert.Single(GVT.Core.Aliyun.QwenTranslator.RelevantTerms(g, Direction.ZhToEn, "他破甲了"));
        Assert.Single(GVT.Core.Aliyun.QwenTranslator.RelevantTerms(g, Direction.EnToZh, "He's cracked, push"));
        Assert.Empty(GVT.Core.Aliyun.QwenTranslator.RelevantTerms(g, Direction.EnToZh, "exfiltration"));
    }
}

public class Round3Tests
{
    [Theory]
    [InlineData("they're upstairs, right?", "2103.547", Direction.EnToZh, true)]
    [InlineData("the same thing.", "-1", Direction.EnToZh, true)]
    [InlineData("26", "26", Direction.EnToZh, false)]
    [InlineData("three three", "33", Direction.EnToZh, false)]
    [InlineData("Oh, definitely.", "嗯， definitely。", Direction.EnToZh, true)]
    [InlineData("Right.", "right", Direction.EnToZh, true)]
    [InlineData("Oh shit.", "哦 shit。", Direction.EnToZh, true)]
    [InlineData("We need a UAV over at Florio's place", "我们需要在 Florio 那边放个UAV", Direction.EnToZh, false)]
    [InlineData("DMZ orders for it.", "DMZ的任务是这个。", Direction.EnToZh, false)]
    [InlineData("因为他们有卡，所以他们也能刷卡进来。", "They got cards, so they can刷 too.", Direction.ZhToEn, true)]
    [InlineData("你来守着", "you守 it", Direction.ZhToEn, true)]
    [InlineData("走吧", "Let's go.", Direction.ZhToEn, false)]
    public void Problem_DetectsMixedLanguageAndNumberGarbage(string src, string tr, Direction d, bool bad)
        => Assert.Equal(bad, GVT.Core.Aliyun.QwenTranslator.Problem(src, tr, d) != null);

    [Theory]
    [InlineData("going with.", true)]
    [InlineData("stock up on.", true)]
    [InlineData("code of.", true)]
    [InlineData("Can you?", false)]
    [InlineData("the case.", false)]
    [InlineData("Okay, we'll connect to.", false)]
    [InlineData("sniper.", false)]
    public void DanglingFragment_ShortAndUnfinished(string text, bool frag)
        => Assert.Equal(frag, GVT.Core.Pipeline.TranslationChannel.IsDanglingFragment(text));

    [Fact]
    public void Glossary_MisheardOnlyForTranslation_NotHotwords()
    {
        var g = new Glossary { Entries = { new GlossaryEntry { Zh = "阔剑地雷", En = "claymore", ZhAliases = { "阔剑" }, Misheard = { "扩建地雷" }, Hotword = true }, new GlossaryEntry { Zh = "撤离", En = "exfil" } } };
        var hot = g.HotwordsFor(Direction.ZhToEn);
        Assert.Contains("阔剑地雷", hot.Keys);
        Assert.Contains("阔剑", hot.Keys);
        Assert.DoesNotContain("扩建地雷", hot.Keys);
        Assert.DoesNotContain("撤离", hot.Keys);
        Assert.Single(GVT.Core.Aliyun.QwenTranslator.RelevantTerms(g, Direction.ZhToEn, "门口放点扩建地雷"));
    }

    [Fact]
    public void DefaultGlossary_HasNoSingleCharPush()
    {
        var g = Glossary.Load(Path.Combine(AudioTests.FindRepoRoot(), "src", "GVT.App", "glossary.default.json"));
        Assert.Empty(GVT.Core.Aliyun.QwenTranslator.RelevantTerms(g, Direction.ZhToEn, "我推荐你带烟雾弹"));
        Assert.Contains(("扩肩", "claymore"), GVT.Core.Aliyun.QwenTranslator.RelevantTerms(g, Direction.ZhToEn, "顺便多放点扩肩肌肋"));
        Assert.DoesNotContain("撤离", g.HotwordsFor(Direction.ZhToEn).Keys);
    }

    [Fact]
    public void Vad_WaitsLongerWhenSentenceLooksUnfinished()
    {
        int ends = 0;
        var cfg = new VadConfig { EndSilenceMs = 300, UnfinishedEndSilenceMs = 900, MinSpeechMs = 64 };
        var seg = new GVT.Core.Audio.VadSegmenter(new StubVad(), cfg) { LooksUnfinished = () => true };
        seg.SpeechEnd += () => ends++;
        StubVad.P = 0.9f; seg.Push(new float[16000]);
        StubVad.P = 0.0f; seg.Push(new float[16 * 500]);
        Assert.Equal(0, ends); // 500 ms 静音：普通情况早该断句了，但这句没说完
        seg.Push(new float[16 * 500]);
        Assert.Equal(1, ends);
    }

    private sealed class StubVad : GVT.Core.Audio.IVoiceDetector
    {
        public static float P;
        public string Name => "stub";
        public float Process(ReadOnlySpan<float> frame) => P;
        public void Reset() { }
        public void Dispose() { }
    }
}

public class Round3NumberTests
{
    [Theory]
    [InlineData("and 301 inside.", "301.201", true)]
    [InlineData("Oh four six.", "0.46", true)]
    [InlineData("200.", "200", false)]
    [InlineData("26", "26", false)]
    [InlineData("three three", "33", false)]
    public void Problem_CatchesNumberGarbage(string src, string tr, bool bad)
        => Assert.Equal(bad, GVT.Core.Aliyun.QwenTranslator.Problem(src, tr, Direction.EnToZh) != null);

    [Fact]
    public void LoneRight_IsBackchannel() => Assert.True(GVT.Core.Pipeline.TranslationChannel.IsBackchannel("Right."));
}

public class EnvironmentCheckTests
{
    private static readonly string[] Render = { "扬声器 (USB Audio)", GVT.Core.Setup.EnvironmentCheck.SendCableRender, GVT.Core.Setup.EnvironmentCheck.RecvCableRender };
    private static readonly string[] Capture = { "麦克风 (USB Audio)", GVT.Core.Setup.EnvironmentCheck.SendCableCapture, GVT.Core.Setup.EnvironmentCheck.RecvCableCapture };

    private static List<GVT.Core.Setup.EnvIssue> Run(AppConfig cfg, bool key = true, string[]? r = null, string[]? c = null, string defR = "扬声器 (USB Audio)", string defC = "麦克风 (USB Audio)")
        => GVT.Core.Setup.EnvironmentCheck.Run(cfg, key, r ?? Render, c ?? Capture, defR, defC);

    [Fact]
    public void AllGood_NoIssues() => Assert.Empty(Run(new AppConfig()));

    [Fact]
    public void MissingKeyAndBothCables_ListsTwoIssues()
    {
        var issues = Run(new AppConfig(), key: false, r: new[] { "扬声器 (USB Audio)" }, c: new[] { "麦克风 (USB Audio)" });
        Assert.Equal(new[] { GVT.Core.Setup.EnvFix.ApiKey, GVT.Core.Setup.EnvFix.InstallDrivers }, issues.Select(i => i.Fix));
    }

    [Fact]
    public void HeadphonesDefaultIsVirtualCable_AsksToPickDevice()
    {
        var issues = Run(new AppConfig(), defR: GVT.Core.Setup.EnvironmentCheck.SendCableRender);
        Assert.Single(issues);
        Assert.Equal(GVT.Core.Setup.EnvFix.Devices, issues[0].Fix);
        Assert.Contains("耳机", issues[0].Title);
    }

    [Fact]
    public void UnpluggedMic_AsksToPickDevice()
    {
        var cfg = new AppConfig();
        cfg.Send.MicDevice = "Blue Yeti";
        var issues = Run(cfg);
        Assert.Single(issues);
        Assert.Contains("我的麦克风", issues[0].Title);
    }
}
