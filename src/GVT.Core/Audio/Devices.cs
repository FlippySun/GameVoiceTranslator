using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace GVT.Core.Audio;

public static class DeviceCatalog
{
    public static IReadOnlyList<string> ListRender() => List(DataFlow.Render);
    public static IReadOnlyList<string> ListCapture() => List(DataFlow.Capture);

    private static IReadOnlyList<string> List(DataFlow flow)
    {
        using var en = new MMDeviceEnumerator();
        return en.EnumerateAudioEndPoints(flow, DeviceState.Active).Select(d => d.FriendlyName).ToList();
    }

    public static MMDevice FindRender(string nameContains) => Find(DataFlow.Render, nameContains);
    public static MMDevice FindCapture(string nameContains) => Find(DataFlow.Capture, nameContains);

    /// <summary>名称留空 = Windows 默认设备；否则按子串（不区分大小写）匹配。找不到就抛出带候选列表的异常，绝不偷偷换设备。</summary>
    private static MMDevice Find(DataFlow flow, string nameContains)
    {
        var en = new MMDeviceEnumerator();
        if (string.IsNullOrWhiteSpace(nameContains))
            return en.GetDefaultAudioEndpoint(flow, Role.Multimedia);

        var all = en.EnumerateAudioEndPoints(flow, DeviceState.Active).ToList();
        var hit = all.FirstOrDefault(d => d.FriendlyName.Contains(nameContains, StringComparison.OrdinalIgnoreCase));
        if (hit != null) return hit;
        var kind = flow == DataFlow.Render ? "播放" : "录音";
        throw new DeviceNotFoundException(
            $"找不到包含“{nameContains}”的{kind}设备。当前可用：{string.Join(" | ", all.Select(d => d.FriendlyName))}");
    }

    public static bool LooksVirtualCable(string friendlyName)
        => friendlyName.Contains("CABLE", StringComparison.OrdinalIgnoreCase)
        || friendlyName.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase)
        || friendlyName.Contains("Voicemeeter", StringComparison.OrdinalIgnoreCase);
}

public sealed class DeviceNotFoundException(string message) : Exception(message);

/// <summary>WASAPI 共享模式采集，统一转换成交错 float。</summary>
public sealed class CaptureSource : IDisposable
{
    private readonly WasapiCapture _capture;
    private readonly bool _isFloat;
    private readonly int _bytesPerSample;

    public string DeviceName { get; }
    public int SampleRate { get; }
    public int Channels { get; }
    public volatile float LastPeakDb = -120f;

    /// <summary>开始录音后丢弃的时长：虚拟线开始录音时会先吐出一段上次残留的缓存。</summary>
    public int SkipInitialMs { get; set; } = 300;
    private long _startTicks;

    /// <summary>采集线程上回调：交错 float 样本。</summary>
    public event Action<float[]>? Data;
    public event Action<Exception?>? Stopped;

    public CaptureSource(MMDevice device, int bufferMs = 20)
    {
        DeviceName = device.FriendlyName;
        _capture = new WasapiCapture(device, true, bufferMs);
        var wf = _capture.WaveFormat;
        SampleRate = wf.SampleRate;
        Channels = wf.Channels;
        _bytesPerSample = wf.BitsPerSample / 8;
        // 共享模式的混音格式几乎总是 32 位浮点（可能以 Extensible 形式出现）
        _isFloat = wf.Encoding == WaveFormatEncoding.IeeeFloat
                   || (wf.Encoding == WaveFormatEncoding.Extensible && wf.BitsPerSample == 32);
        _capture.DataAvailable += OnData;
        _capture.RecordingStopped += (_, e) => Stopped?.Invoke(e.Exception);
    }

    public void Start()
    {
        _startTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        _capture.StartRecording();
    }
    public void Stop() { try { _capture.StopRecording(); } catch { /* 设备可能已移除 */ } }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0) return;
        if (System.Diagnostics.Stopwatch.GetElapsedTime(_startTicks).TotalMilliseconds < SkipInitialMs) return;
        float[] samples;
        var bytes = e.Buffer.AsSpan(0, e.BytesRecorded);
        if (_isFloat)
        {
            samples = new float[e.BytesRecorded / 4];
            Buffer.BlockCopy(e.Buffer, 0, samples, 0, e.BytesRecorded);
        }
        else if (_bytesPerSample == 2)
        {
            samples = PcmConvert.Pcm16ToFloat(bytes);
        }
        else if (_bytesPerSample == 3)
        {
            int n = e.BytesRecorded / 3;
            samples = new float[n];
            for (int i = 0; i < n; i++)
            {
                int v = (bytes[3 * i] << 8) | (bytes[3 * i + 1] << 16) | (bytes[3 * i + 2] << 24);
                samples[i] = v / 2147483648f;
            }
        }
        else
        {
            int n = e.BytesRecorded / 4;
            samples = new float[n];
            for (int i = 0; i < n; i++) samples[i] = BitConverter.ToInt32(bytes.Slice(4 * i, 4)) / 2147483648f;
        }
        LastPeakDb = PcmConvert.PeakDb(samples);
        Data?.Invoke(samples);
    }

    public void Dispose()
    {
        Stop();
        _capture.Dispose();
    }
}

/// <summary>某个播放设备上的混音器：所有输入都是设备混音格式（float、设备采样率与声道数）。</summary>
public sealed class OutputMixer : IDisposable
{
    private readonly WasapiOut _out;
    private readonly NAudio.Wave.SampleProviders.MixingSampleProvider _mixer;

    public string DeviceName { get; }
    public int SampleRate { get; }
    public int Channels { get; }
    public event Action<Exception?>? Stopped;

    public OutputMixer(MMDevice device, int latencyMs = 40)
    {
        DeviceName = device.FriendlyName;
        var mix = device.AudioClient.MixFormat;
        SampleRate = mix.SampleRate;
        Channels = mix.Channels;
        _mixer = new NAudio.Wave.SampleProviders.MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels))
        {
            ReadFully = true,
        };
        _out = new WasapiOut(device, AudioClientShareMode.Shared, true, latencyMs);
        _out.Init(_mixer);
        _out.PlaybackStopped += (_, e) => Stopped?.Invoke(e.Exception);
    }

    public void AddInput(ISampleProvider input) => _mixer.AddMixerInput(input);
    public void Start() => _out.Play();

    public void Dispose()
    {
        try { _out.Stop(); } catch { }
        _out.Dispose();
    }
}
