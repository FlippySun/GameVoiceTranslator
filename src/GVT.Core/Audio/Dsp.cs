namespace GVT.Core.Audio;

/// <summary>流式线性插值重采样（多声道交错）。降采样前请先低通。</summary>
public sealed class LinearResampler
{
    private readonly int _channels;
    private readonly double _step; // 每个输出样本前进多少个输入样本
    private double _pos;           // 相对于“上一块最后一个样本”的位置，-1 表示上一块的最后一帧
    private readonly float[] _last;
    private bool _hasLast;

    public int InRate { get; }
    public int OutRate { get; }

    public LinearResampler(int inRate, int outRate, int channels)
    {
        InRate = inRate;
        OutRate = outRate;
        _channels = channels;
        _step = (double)inRate / outRate;
        _last = new float[channels];
        _pos = 0;
    }

    public bool IsPassthrough => InRate == OutRate;

    /// <summary>输入交错样本（frames 帧），返回交错输出。</summary>
    public float[] Process(ReadOnlySpan<float> input)
    {
        int frames = input.Length / _channels;
        if (frames == 0) return Array.Empty<float>();
        if (IsPassthrough) return input.ToArray();

        // 位置坐标系：索引 -1 是 _last，0..frames-1 是本块
        double start = _hasLast ? _pos : Math.Max(_pos, 0);
        var output = new List<float>((int)(frames / _step + 4) * _channels);
        double p = start;
        while (p <= frames - 1)
        {
            int i0 = (int)Math.Floor(p);
            double frac = p - i0;
            for (int c = 0; c < _channels; c++)
            {
                float a = i0 < 0 ? _last[c] : input[i0 * _channels + c];
                float b = i0 + 1 >= frames ? a : input[(i0 + 1) * _channels + c];
                output.Add((float)(a + (b - a) * frac));
            }
            p += _step;
        }
        // 下一块的坐标原点是本块末尾之后，所以本块最后一帧变成 -1
        _pos = p - frames;
        for (int c = 0; c < _channels; c++) _last[c] = input[(frames - 1) * _channels + c];
        _hasLast = true;
        return output.ToArray();
    }
}

/// <summary>流式 FIR 低通（单声道），窗函数法设计。</summary>
public sealed class FirLowpass
{
    private readonly float[] _taps;
    private readonly float[] _history;
    private int _idx;

    public FirLowpass(int sampleRate, double cutoffHz, int numTaps = 47)
    {
        if (numTaps % 2 == 0) numTaps++;
        _taps = new float[numTaps];
        _history = new float[numTaps];
        double fc = cutoffHz / sampleRate;
        int m = numTaps - 1;
        double sum = 0;
        for (int n = 0; n < numTaps; n++)
        {
            double x = n - m / 2.0;
            double sinc = x == 0 ? 2 * fc : Math.Sin(2 * Math.PI * fc * x) / (Math.PI * x);
            double w = 0.42 - 0.5 * Math.Cos(2 * Math.PI * n / m) + 0.08 * Math.Cos(4 * Math.PI * n / m); // Blackman
            _taps[n] = (float)(sinc * w);
            sum += _taps[n];
        }
        for (int n = 0; n < numTaps; n++) _taps[n] = (float)(_taps[n] / sum);
    }

    public void ProcessInPlace(Span<float> data)
    {
        int len = _taps.Length;
        for (int i = 0; i < data.Length; i++)
        {
            _history[_idx] = data[i];
            double acc = 0;
            int h = _idx;
            for (int t = 0; t < len; t++)
            {
                acc += _taps[t] * _history[h];
                h = h == 0 ? len - 1 : h - 1;
            }
            data[i] = (float)acc;
            _idx = (_idx + 1) % len;
        }
    }
}

/// <summary>任意格式交错浮点 → 16 kHz 单声道（识别/VAD 用），同时给出左右声道能量比。</summary>
public sealed class MonoDownmixer
{
    public const int TargetRate = 16000;
    private readonly int _channels;
    private readonly FirLowpass? _lp;
    private readonly LinearResampler _rs;

    public MonoDownmixer(int inRate, int channels)
    {
        _channels = channels;
        _lp = inRate > TargetRate ? new FirLowpass(inRate, TargetRate * 0.45) : null;
        _rs = new LinearResampler(inRate, TargetRate, 1);
    }

    /// <param name="pan">-1 = 全在左，+1 = 全在右（基于本块能量）。</param>
    public float[] Process(ReadOnlySpan<float> interleaved, out float pan)
    {
        int frames = interleaved.Length / _channels;
        var mono = new float[frames];
        double el = 0, er = 0;
        for (int f = 0; f < frames; f++)
        {
            float s = 0;
            for (int c = 0; c < _channels; c++) s += interleaved[f * _channels + c];
            mono[f] = s / _channels;
            if (_channels >= 2)
            {
                float l = interleaved[f * _channels], r = interleaved[f * _channels + 1];
                el += l * l; er += r * r;
            }
        }
        pan = (el + er) > 1e-9 ? (float)((er - el) / (er + el)) : 0f;
        _lp?.ProcessInPlace(mono);
        return _rs.Process(mono);
    }
}

public static class PcmConvert
{
    public static byte[] FloatToPcm16(ReadOnlySpan<float> samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            float v = Math.Clamp(samples[i], -1f, 1f);
            short s = (short)Math.Round(v * 32767f);
            bytes[2 * i] = (byte)(s & 0xFF);
            bytes[2 * i + 1] = (byte)((s >> 8) & 0xFF);
        }
        return bytes;
    }

    public static float[] Pcm16ToFloat(ReadOnlySpan<byte> bytes)
    {
        int n = bytes.Length / 2;
        var f = new float[n];
        for (int i = 0; i < n; i++)
            f[i] = (short)(bytes[2 * i] | (bytes[2 * i + 1] << 8)) / 32768f;
        return f;
    }

    public static float PeakDb(ReadOnlySpan<float> samples)
    {
        float peak = 0;
        foreach (var s in samples) { var a = Math.Abs(s); if (a > peak) peak = a; }
        return peak <= 1e-6f ? -120f : 20f * MathF.Log10(peak);
    }

    public static float RmsDb(ReadOnlySpan<float> samples)
    {
        if (samples.Length == 0) return -120f;
        double acc = 0;
        foreach (var s in samples) acc += s * s;
        double rms = Math.Sqrt(acc / samples.Length);
        return rms <= 1e-6 ? -120f : (float)(20 * Math.Log10(rms));
    }
}
