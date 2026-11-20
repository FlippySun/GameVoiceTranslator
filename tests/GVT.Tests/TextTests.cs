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
