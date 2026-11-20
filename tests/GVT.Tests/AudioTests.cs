using GVT.Core.Audio;
using GVT.Core.Config;

namespace GVT.Tests;

public class AudioTests
{
    private static float[] Sine(double freq, int rate, int samples, int channels = 1, float amp = 0.5f)
    {
        var s = new float[samples * channels];
        for (int i = 0; i < samples; i++)
            for (int c = 0; c < channels; c++)
                s[i * channels + c] = amp * (float)Math.Sin(2 * Math.PI * freq * i / rate);
        return s;
    }

    private static int ZeroCrossings(float[] s)
    {
        int z = 0;
        for (int i = 1; i < s.Length; i++) if (s[i - 1] < 0 != s[i] < 0) z++;
        return z;
    }

    [Fact]
    public void Resampler_KeepsFrequencyAndLength_AcrossChunks()
    {
        var input = Sine(440, 48000, 48000);
        var rs = new LinearResampler(48000, 16000, 1);
        var output = new List<float>();
        for (int off = 0; off < input.Length; off += 480) // 10ms 块
            output.AddRange(rs.Process(input.AsSpan(off, Math.Min(480, input.Length - off))));

        Assert.InRange(output.Count, 15990, 16010);
        Assert.InRange(ZeroCrossings(output.ToArray()), 875, 885); // 440 Hz × 2 次/周期
    }

    [Fact]
    public void Downmixer_StereoToMono16k_AndReportsPan()
    {
        // 只有右声道有声音
        var stereo = new float[48000 * 2];
        var mono = Sine(1000, 48000, 48000);
        for (int i = 0; i < 48000; i++) stereo[2 * i + 1] = mono[i];

        var dm = new MonoDownmixer(48000, 2);
        var outList = new List<float>();
        float pan = 0;
        for (int off = 0; off < stereo.Length; off += 960)
            outList.AddRange(dm.Process(stereo.AsSpan(off, 960), out pan));

        Assert.InRange(outList.Count, 15990, 16010);
        Assert.True(pan > 0.9f, $"pan={pan}");
        Assert.InRange(ZeroCrossings(outList.ToArray()), 1990, 2010);
    }

    [Fact]
    public void Downmixer_FiltersContentAboveNyquist()
    {
        // 12 kHz 在 16 kHz 采样下会混叠，低通后应基本消失
        var dm = new MonoDownmixer(48000, 1);
        var outp = dm.Process(Sine(12000, 48000, 48000), out _);
        Assert.True(PcmConvert.RmsDb(outp.AsSpan(2000)) < -30, $"rms={PcmConvert.RmsDb(outp.AsSpan(2000))}");
    }

    [Fact]
    public void Pcm16_RoundTrip()
    {
        var f = new[] { 0f, 0.5f, -0.5f, 0.999f, -1f };
        var back = PcmConvert.Pcm16ToFloat(PcmConvert.FloatToPcm16(f));
        for (int i = 0; i < f.Length; i++) Assert.Equal(f[i], back[i], 3);
    }

    private static SpeechQueuePlayer NewPlayer(Func<DateTime>? clock = null)
        => new(16000, 1, 16000) { SynchronousEvents = true, Clock = clock ?? (() => DateTime.UtcNow) };

    private static float[] Const(float v, int n) => Enumerable.Repeat(v, n).ToArray();

    private static float[] ReadN(SpeechQueuePlayer p, int n)
    {
        var buf = new float[n];
        p.Read(buf, 0, n);
        return buf;
    }

    [Fact]
    public void Player_PlaysInCreationOrder_EvenIfLaterSlotIsReadyFirst()
    {
        var p = NewPlayer();
        var s1 = p.Enqueue(1, 0);
        var s2 = p.Enqueue(2, 0);
        p.Append(s2, Const(0.2f, 100));
        p.Complete(s2);

        Assert.All(ReadN(p, 100), v => Assert.Equal(0f, v)); // 第一句还没好：等待，不跳过

        p.Append(s1, Const(0.1f, 100));
        p.Complete(s1);
        var out1 = ReadN(p, 200);
        Assert.All(out1.Take(100), v => Assert.Equal(0.1f, v, 4));
        Assert.All(out1.Skip(100), v => Assert.Equal(0.2f, v, 4));
    }

    [Fact]
    public void Player_CancelBelowEpoch_StopsCurrentAndQueued_AndIgnoresLateAudio()
    {
        var p = NewPlayer();
        var ended = new List<string>();
        p.SlotEnded += s => ended.Add($"{s.Id}:{s.EndReason}");
        var s1 = p.Enqueue(1, 0);
        var s2 = p.Enqueue(2, 0);
        var s3 = p.Enqueue(3, 1);
        p.Append(s1, Const(0.1f, 1000));
        ReadN(p, 100); // 第一句已经开始播

        p.CancelBelowEpoch(1, "取消");
        p.Append(s2, Const(0.2f, 100)); // 迟到的音频
        p.Append(s3, Const(0.3f, 50));
        p.Complete(s3);

        var o = ReadN(p, 100);
        Assert.All(o.Take(50), v => Assert.Equal(0.3f, v, 4));
        Assert.All(o.Skip(50), v => Assert.Equal(0f, v));
        Assert.Contains("1:取消", ended);
        Assert.Contains("2:取消", ended);
        Assert.Contains("3:done", ended);
    }

    [Fact]
    public void Player_DropsStaleSlotThatNeverStarted()
    {
        var now = DateTime.UtcNow;
        var p = NewPlayer(() => now);
        p.MaxSlotAgeSeconds = 5;
        string? reason = null;
        p.SlotEnded += s => reason = s.EndReason;
        var s1 = p.Enqueue(1, 0);
        now = now.AddSeconds(6);
        p.Append(s1, Const(0.5f, 100));
        Assert.All(ReadN(p, 100), v => Assert.Equal(0f, v));
        Assert.Equal("stale", reason);
    }

    [Fact]
    public void Player_PausedOutputsSilenceAndKeepsAudio()
    {
        var p = NewPlayer();
        var s = p.Enqueue(1, 0);
        p.Append(s, Const(0.4f, 100));
        p.Complete(s);
        p.Paused = true;
        Assert.All(ReadN(p, 100), v => Assert.Equal(0f, v));
        p.Paused = false;
        Assert.All(ReadN(p, 100), v => Assert.Equal(0.4f, v, 4));
    }

    [Fact]
    public void Player_StereoPanLeansToSide()
    {
        var p = new SpeechQueuePlayer(16000, 2, 16000) { SynchronousEvents = true };
        var s = p.Enqueue(1, 0, pan: 0.8f);
        p.Append(s, Const(0.5f, 10));
        p.Complete(s);
        var buf = new float[20];
        p.Read(buf, 0, 20);
        Assert.True(buf[1] > buf[0] * 3, $"L={buf[0]} R={buf[1]}");
    }

    [Fact]
    public void Passthrough_CapsLatency()
    {
        var pt = new PassthroughProvider(48000, 2) { MaxBufferMs = 100, PrefillMs = 0, TargetGain = 1 };
        pt.Write(Sine(500, 48000, 48000, 2), 2, 48000); // 一次写 1 秒
        var buf = new float[48000 * 2];
        pt.Read(buf, 0, buf.Length);
        int nonZero = buf.Count(v => Math.Abs(v) > 1e-4);
        Assert.InRange(nonZero, 1, 48000 * 2 * 110 / 1000); // 最多保留约 100 ms
    }

    private sealed class FakeVad(float[] probs) : IVoiceDetector
    {
        private int _i;
        public string Name => "fake";
        public float Process(ReadOnlySpan<float> frame) => _i < probs.Length ? probs[_i++] : 0f;
        public void Reset() { }
        public void Dispose() { }
    }

    [Fact]
    public void Segmenter_DetectsStartWithPreRoll_AndEndAfterSilence()
    {
        // 帧 = 32ms。10 帧静音，20 帧语音，20 帧静音
        var probs = Enumerable.Repeat(0f, 10).Concat(Enumerable.Repeat(0.9f, 20)).Concat(Enumerable.Repeat(0f, 20)).ToArray();
        var cfg = new VadConfig { MinSpeechMs = 96, EndSilenceMs = 320, PreRollMs = 192 };
        var seg = new VadSegmenter(new FakeVad(probs), cfg);
        int starts = 0, ends = 0, preRollLen = 0;
        seg.SpeechStart += pre => { starts++; preRollLen = pre.Length; };
        seg.SpeechEnd += () => ends++;
        seg.Push(new float[512 * probs.Length]);
        Assert.Equal(1, starts);
        Assert.Equal(1, ends);
        Assert.Equal(512 * (6 + 3), preRollLen); // 6 帧预缓冲 + 3 帧确认段
    }

    [Fact]
    public void Silero_LoadsAndReturnsLowProbabilityForSilence()
    {
        var path = Path.Combine(FindRepoRoot(), "models", "silero_vad.onnx");
        if (!File.Exists(path)) return;
        using var vad = new SileroVad(path);
        float p = 0;
        for (int i = 0; i < 20; i++) p = vad.Process(new float[512]);
        Assert.InRange(p, 0f, 0.2f);
    }

    internal static string FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "GameVoiceTranslator.sln"))) d = d.Parent;
        return d?.FullName ?? AppContext.BaseDirectory;
    }
}
