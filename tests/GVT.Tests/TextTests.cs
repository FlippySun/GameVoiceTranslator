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
