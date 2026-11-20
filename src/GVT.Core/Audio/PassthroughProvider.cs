using NAudio.Wave;

namespace GVT.Core.Audio;

/// <summary>
/// 把采集到的原声（队友语音）低延迟转发到耳机，可平滑调节增益（用于压低原声 / 临时听原声）。
/// 缓冲超过上限时丢弃最旧数据，防止两块声卡时钟漂移导致延迟越积越大。
/// </summary>
public sealed class PassthroughProvider : ISampleProvider
{
    private readonly object _lock = new();
    private readonly float[] _ring;
    private int _head, _count;
    private bool _prefilling = true;
    private LinearResampler? _rs;
    private int _rsInRate, _rsInCh;
    private float _gain;

    public WaveFormat WaveFormat { get; }
    public float TargetGain { get; set; } = 1f;
    public int PrefillMs { get; set; } = 40;
    public int MaxBufferMs { get; set; } = 160;

    public PassthroughProvider(int outRate, int outChannels)
    {
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(outRate, outChannels);
        _ring = new float[outRate * outChannels]; // 1 秒容量
        _gain = TargetGain;
    }

    /// <summary>采集线程调用。</summary>
    public void Write(ReadOnlySpan<float> interleaved, int inChannels, int inRate)
    {
        int outCh = WaveFormat.Channels;
        int frames = interleaved.Length / inChannels;
        var mapped = new float[frames * outCh];
        for (int f = 0; f < frames; f++)
        {
            for (int c = 0; c < outCh; c++)
            {
                float v;
                if (outCh == 1)
                {
                    v = 0;
                    for (int k = 0; k < inChannels; k++) v += interleaved[f * inChannels + k];
                    v /= inChannels;
                }
                else if (c < 2) v = interleaved[f * inChannels + Math.Min(c, inChannels - 1)];
                else v = 0; // 多声道设备只用前左/前右
                mapped[f * outCh + c] = v;
            }
        }

        float[] data = mapped;
        if (inRate != WaveFormat.SampleRate)
        {
            if (_rs == null || _rsInRate != inRate || _rsInCh != outCh)
            {
                _rs = new LinearResampler(inRate, WaveFormat.SampleRate, outCh);
                _rsInRate = inRate; _rsInCh = outCh;
            }
            data = _rs.Process(mapped);
        }

        lock (_lock)
        {
            int max = WaveFormat.SampleRate * outCh * MaxBufferMs / 1000;
            foreach (var v in data)
            {
                if (_count == _ring.Length) { _head = (_head + 1) % _ring.Length; _count--; }
                _ring[(_head + _count) % _ring.Length] = v;
                _count++;
            }
            while (_count > max) { _head = (_head + outCh) % _ring.Length; _count -= outCh; }
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int outCh = WaveFormat.Channels;
        float target = TargetGain;
        // 约 30 ms 的平滑时间常数
        float coeff = 1f - MathF.Exp(-1f / (0.03f * WaveFormat.SampleRate));
        lock (_lock)
        {
            int prefill = WaveFormat.SampleRate * outCh * PrefillMs / 1000;
            if (_prefilling && _count >= prefill) _prefilling = false;
            for (int i = 0; i < count; i += outCh)
            {
                if (i % outCh == 0) _gain += (target - _gain) * coeff;
                for (int c = 0; c < outCh && i + c < count; c++)
                {
                    float v = 0;
                    if (!_prefilling && _count > 0)
                    {
                        v = _ring[_head];
                        _head = (_head + 1) % _ring.Length;
                        _count--;
                    }
                    buffer[offset + i + c] = v * _gain;
                }
                if (!_prefilling && _count == 0) _prefilling = true;
            }
        }
        return count;
    }
}
