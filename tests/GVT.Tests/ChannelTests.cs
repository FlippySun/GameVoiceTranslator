using GVT.Core.Aliyun;
using GVT.Core.Audio;
using GVT.Core.Config;
using GVT.Core.Pipeline;

namespace GVT.Tests;

/// <summary>用假服务验证调度逻辑：顺序、取消、迟到结果、回声过滤。不需要网络和声卡。</summary>
public class ChannelTests
{
    private sealed class FakeAsr(Func<Task<string>> result) : IAsrSession
    {
        public event Action<string>? Partial;
        public double AudioSeconds { get; private set; }
        public bool Aborted { get; private set; }
        public void Push(ReadOnlySpan<float> s) { AudioSeconds += s.Length / 16000.0; Partial?.Invoke("..."); }
        public async Task<string> FinishAsync(CancellationToken ct) => await result().WaitAsync(ct);
        public void Abort() => Aborted = true;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeTts(float value, int chunks) : ITtsSession
    {
        public int SampleRate => 16000;
        public async Task<int> SpeakAsync(string text, Action<float[]> onAudio, CancellationToken ct)
        {
            for (int i = 0; i < chunks; i++)
            {
                await Task.Delay(5, ct);
                onAudio(Enumerable.Repeat(value, 160).ToArray());
            }
            return text.Length;
        }
        public Task CancelAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeTranslator : ITranslator
    {
        public Func<string, Task>? Gate;
        public async Task<TranslationResult> TranslateAsync(string text, Direction d, IReadOnlyList<string> ctx, CancellationToken ct)
        {
            if (Gate != null) await Gate(text).WaitAsync(ct);
            return new TranslationResult("T:" + text, 10, 5);
        }
    }

    private sealed class FakeServices : ISpeechServices
    {
        public readonly Queue<Func<Task<string>>> AsrResults = new();
        public readonly Queue<float> TtsValues = new();
        public readonly List<FakeAsr> Asrs = new();
        public FakeTranslator Fake { get; } = new();
        public ITranslator Translator => Fake;
        public int TtsSampleRate => 16000;
        public IAsrSession StartAsr(string? h, string? c)
        {
            var a = new FakeAsr(AsrResults.Dequeue());
            Asrs.Add(a);
            return a;
        }
        public ITtsSession PrepareTts(GVT.Core.Config.TtsVoice v) => new FakeTts(TtsValues.Dequeue(), 3);
    }

    private static (TranslationChannel ch, SpeechQueuePlayer player, FakeServices svc, List<LatencyRecord> done) Make(
        Direction dir = Direction.ZhToEn, bool skipIfTarget = false)
    {
        var svc = new FakeServices();
        var player = new SpeechQueuePlayer(16000, 1, 16000) { SynchronousEvents = true };
        var ch = new TranslationChannel(new ChannelOptions { Name = "t", Direction = dir, SkipIfAlreadyTarget = skipIfTarget },
            svc, player, new UsageMeter(new BudgetConfig()));
        var done = new List<LatencyRecord>();
        ch.Completed += r => { lock (done) done.Add(r); };
        return (ch, player, svc, done);
    }

    private static async Task<List<float>> DrainAsync(SpeechQueuePlayer p, int maxMs = 2000)
    {
        var all = new List<float>();
        var buf = new float[160];
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < maxMs)
        {
            p.Read(buf, 0, buf.Length);
            all.AddRange(buf.Where(v => v != 0));
            if (p.PendingCount == 0 && all.Count > 0) break;
            await Task.Delay(2);
        }
        return all;
    }

    [Fact]
    public async Task FullSentence_IsTranslatedAndPlayed()
    {
        var (ch, player, svc, done) = Make();
        svc.AsrResults.Enqueue(() => Task.FromResult("左边二楼"));
        svc.TtsValues.Enqueue(0.3f);
        string? translated = null;
        ch.Translated += t => translated = t;

        ch.BeginUtterance(new float[1600]);
        ch.PushAudio(new float[3200]);
        ch.EndUtterance();

        var audio = await DrainAsync(player);
        Assert.Equal("T:左边二楼", translated);
        Assert.Equal(480, audio.Count);
        await WaitFor(() => done.Count == 1, player);
        Assert.Equal("done", done[0].Outcome);
        Assert.NotNull(done[0].EndToPlayMs);
    }

    [Fact]
    public async Task Cancel_DropsLateAsrResult_NothingIsPlayed()
    {
        var (ch, player, svc, done) = Make();
        var asrGate = new TaskCompletionSource<string>();
        svc.AsrResults.Enqueue(() => asrGate.Task);
        svc.TtsValues.Enqueue(0.3f);

        ch.BeginUtterance(new float[160]);
        ch.EndUtterance();
        ch.Cancel("取消本句");
        asrGate.SetResult("冲进去"); // 迟到的结果

        var audio = await DrainAsync(player, 300);
        Assert.Empty(audio);
        await WaitFor(() => done.Count == 1, player);
        Assert.NotEqual("done", done[0].Outcome);
    }

    [Fact]
    public async Task Cancel_DuringTranslation_DropsResult_ButNextSentenceStillWorks()
    {
        var (ch, player, svc, done) = Make();
        var trGate = new TaskCompletionSource();
        svc.Fake.Gate = t => t == "第一句" ? trGate.Task : Task.CompletedTask;
        svc.AsrResults.Enqueue(() => Task.FromResult("第一句"));
        svc.AsrResults.Enqueue(() => Task.FromResult("第二句"));
        svc.TtsValues.Enqueue(0.1f);
        svc.TtsValues.Enqueue(0.2f);

        ch.BeginUtterance(new float[160]);
        ch.EndUtterance();
        await Task.Delay(30);
        ch.Cancel("取消本句");
        trGate.SetResult();

        ch.BeginUtterance(new float[160]);
        ch.EndUtterance();
        var audio = await DrainAsync(player);
        Assert.NotEmpty(audio);
        Assert.All(audio, v => Assert.Equal(0.2f, v, 4));
    }

    [Fact]
    public async Task SecondSentence_WaitsForFirst_KeepsOrder()
    {
        var (ch, player, svc, _) = Make();
        var slowAsr = new TaskCompletionSource<string>();
        svc.AsrResults.Enqueue(() => slowAsr.Task);
        svc.AsrResults.Enqueue(() => Task.FromResult("第二句"));
        svc.TtsValues.Enqueue(0.1f);
        svc.TtsValues.Enqueue(0.2f);

        ch.BeginUtterance(new float[160]);
        ch.EndUtterance();
        ch.BeginUtterance(new float[160]);
        ch.EndUtterance();
        await Task.Delay(100); // 第二句已经合成完
        slowAsr.SetResult("第一句");

        var audio = await DrainAsync(player);
        Assert.Equal(960, audio.Count);
        Assert.All(audio.Take(480), v => Assert.Equal(0.1f, v, 4));
        Assert.All(audio.Skip(480), v => Assert.Equal(0.2f, v, 4));
    }

    [Fact]
    public async Task EchoFilter_DropsOwnTranslationComingBack()
    {
        var (ch, player, svc, done) = Make(Direction.EnToZh);
        var echo = new EchoGuard(0.6, 10);
        echo.AddOutgoing("Two enemies on the left, second floor.");
        ch.ShouldDropSource = echo.IsEcho;
        svc.AsrResults.Enqueue(() => Task.FromResult("two enemies on the left second floor"));
        svc.TtsValues.Enqueue(0.3f);

        ch.BeginUtterance(new float[160]);
        ch.EndUtterance();
        await WaitFor(() => done.Count == 1, player);
        Assert.Equal("echo", done[0].Outcome);
        Assert.Empty(await DrainAsync(player, 100));
    }

    [Fact]
    public async Task Receive_ChineseSpeech_IsNotRetranslated()
    {
        var (ch, player, svc, done) = Make(Direction.EnToZh, skipIfTarget: true);
        svc.AsrResults.Enqueue(() => Task.FromResult("兄弟们我在B点"));
        svc.TtsValues.Enqueue(0.3f);
        ch.BeginUtterance(new float[160]);
        ch.EndUtterance();
        await WaitFor(() => done.Count == 1, player);
        Assert.Equal("already-target", done[0].Outcome);
    }

    [Fact]
    public async Task EmptyRecognition_IsDroppedQuietly()
    {
        var (ch, player, svc, done) = Make();
        svc.AsrResults.Enqueue(() => Task.FromResult("。"));
        svc.TtsValues.Enqueue(0.3f);
        ch.BeginUtterance(new float[160]);
        ch.EndUtterance();
        await WaitFor(() => done.Count == 1, player);
        Assert.Equal("empty", done[0].Outcome);
    }

    [Fact]
    public void DisabledChannel_IgnoresAudio()
    {
        var (ch, _, svc, _) = Make();
        ch.Enabled = false;
        ch.BeginUtterance(new float[160]);
        Assert.Empty(svc.Asrs);
        Assert.Equal(ChannelStage.Off, ch.Stage);
    }

    /// <summary>等待条件成立；期间像真实音频线程一样持续读取播放队列。</summary>
    private static async Task WaitFor(Func<bool> cond, SpeechQueuePlayer player, int ms = 2000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var buf = new float[160];
        while (!cond() && sw.ElapsedMilliseconds < ms) { player.Read(buf, 0, buf.Length); await Task.Delay(5); }
        Assert.True(cond(), "等待超时");
    }
}
