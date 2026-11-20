using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using GVT.Core.Config;

namespace GVT.Core.Audio;

/// <summary>输入 16 kHz 单声道、每帧 512 个样本（32 ms），输出语音概率 0..1。</summary>
public interface IVoiceDetector : IDisposable
{
    string Name { get; }
    float Process(ReadOnlySpan<float> frame512);
    void Reset();
}

/// <summary>Silero VAD v5 ONNX（输入需拼接前一帧末尾 64 个样本作为上下文）。</summary>
public sealed class SileroVad : IVoiceDetector
{
    private const int FrameSize = 512, ContextSize = 64;
    private readonly InferenceSession _session;
    private float[] _state = new float[2 * 1 * 128];
    private readonly float[] _context = new float[ContextSize];
    private readonly DenseTensor<long> _sr = new(new long[] { 16000 }, new[] { 1 });

    public string Name => "silero";

    public SileroVad(string modelPath)
    {
        var opts = new SessionOptions { IntraOpNumThreads = 1, InterOpNumThreads = 1 };
        opts.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
        _session = new InferenceSession(modelPath, opts);
        if (!_session.InputMetadata.ContainsKey("state"))
            throw new NotSupportedException("仅支持 Silero VAD v5（输入含 state）。");
    }

    public float Process(ReadOnlySpan<float> frame)
    {
        if (frame.Length != FrameSize) throw new ArgumentException("需要 512 个样本");
        var input = new float[ContextSize + FrameSize];
        _context.CopyTo(input, 0);
        frame.CopyTo(input.AsSpan(ContextSize));
        frame.Slice(FrameSize - ContextSize).CopyTo(_context);

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input", new DenseTensor<float>(input, new[] { 1, input.Length })),
            NamedOnnxValue.CreateFromTensor("state", new DenseTensor<float>(_state, new[] { 2, 1, 128 })),
            NamedOnnxValue.CreateFromTensor("sr", _sr),
        };
        using var results = _session.Run(inputs);
        float prob = 0;
        foreach (var r in results)
        {
            if (r.Name == "output") prob = r.AsTensor<float>().GetValue(0);
            else if (r.Name == "stateN") _state = r.AsTensor<float>().ToArray();
        }
        return prob;
    }

    public void Reset()
    {
        _state = new float[2 * 1 * 128];
        Array.Clear(_context);
    }

    public void Dispose() => _session.Dispose();
}

/// <summary>简单能量门限，Silero 不可用时的退路。</summary>
public sealed class EnergyVad(float thresholdDb) : IVoiceDetector
{
    public string Name => "energy";
    public float Process(ReadOnlySpan<float> frame)
    {
        var db = PcmConvert.RmsDb(frame);
        return Math.Clamp((db - thresholdDb) / 12f + 0.5f, 0f, 1f);
    }
    public void Reset() { }
    public void Dispose() { }
}

public static class VoiceDetectorFactory
{
    public static IVoiceDetector Create(VadConfig cfg, string baseDir, Action<string>? log = null)
    {
        var path = Path.IsPathRooted(cfg.SileroModelPath) ? cfg.SileroModelPath : Path.Combine(baseDir, cfg.SileroModelPath);
        try
        {
            if (File.Exists(path)) return new SileroVad(path);
            log?.Invoke($"未找到 Silero 模型 {path}，改用能量检测。");
        }
        catch (Exception ex)
        {
            log?.Invoke($"Silero 加载失败（{ex.Message}），改用能量检测。");
        }
        return new EnergyVad(cfg.EnergyThresholdDb);
    }
}

/// <summary>
/// 把连续音频切成一句一句：开始时带上预缓冲，静音超过阈值或超长时结束。
/// 所有回调都在调用 Push 的线程上同步触发。
/// </summary>
public sealed class VadSegmenter
{
    private const int Frame = 512;
    private readonly IVoiceDetector _vad;
    private readonly VadConfig _cfg;
    private readonly List<float> _pending = new();
    private readonly Queue<float[]> _preRoll = new();
    private int _preRollFrames;
    private bool _inSpeech;
    private int _speechFrames, _silenceFrames, _utteranceFrames, _candidateFrames;
    private readonly List<float[]> _candidate = new();

    public event Action<float[]>? SpeechStart; // 参数：预缓冲 + 已确认的开头
    public event Action<float[]>? SpeechAudio;
    public event Action? SpeechEnd;

    public bool InSpeech => _inSpeech;
    public float LastProbability { get; private set; }

    public VadSegmenter(IVoiceDetector vad, VadConfig cfg)
    {
        _vad = vad;
        _cfg = cfg;
    }

    private static int MsToFrames(int ms) => Math.Max(1, ms * 16 / Frame);

    public void Push(ReadOnlySpan<float> samples16k)
    {
        foreach (var s in samples16k) _pending.Add(s);
        while (_pending.Count >= Frame)
        {
            var frame = _pending.GetRange(0, Frame).ToArray();
            _pending.RemoveRange(0, Frame);
            ProcessFrame(frame);
        }
    }

    private void ProcessFrame(float[] frame)
    {
        float p = _vad.Process(frame);
        LastProbability = p;

        if (!_inSpeech)
        {
            if (p >= _cfg.StartThreshold)
            {
                _candidate.Add(frame);
                _candidateFrames++;
                if (_candidateFrames >= MsToFrames(_cfg.MinSpeechMs))
                {
                    _inSpeech = true;
                    _silenceFrames = 0;
                    _speechFrames = _candidateFrames;
                    _utteranceFrames = _candidateFrames;
                    var start = _preRoll.SelectMany(f => f).Concat(_candidate.SelectMany(f => f)).ToArray();
                    _preRoll.Clear(); _preRollFrames = 0;
                    _candidate.Clear(); _candidateFrames = 0;
                    SpeechStart?.Invoke(start);
                }
            }
            else
            {
                // 候选段没撑够最短时长：并入预缓冲
                foreach (var c in _candidate) AddPreRoll(c);
                _candidate.Clear(); _candidateFrames = 0;
                AddPreRoll(frame);
            }
            return;
        }

        SpeechAudio?.Invoke(frame);
        _utteranceFrames++;
        if (p < _cfg.EndThreshold) _silenceFrames++;
        else { _silenceFrames = 0; _speechFrames++; }

        if (_silenceFrames >= MsToFrames(_cfg.EndSilenceMs) || _utteranceFrames >= MsToFrames(_cfg.MaxUtteranceMs))
        {
            _inSpeech = false;
            _silenceFrames = 0;
            SpeechEnd?.Invoke();
        }
    }

    private void AddPreRoll(float[] frame)
    {
        _preRoll.Enqueue(frame);
        _preRollFrames++;
        while (_preRollFrames > MsToFrames(_cfg.PreRollMs)) { _preRoll.Dequeue(); _preRollFrames--; }
    }

    /// <summary>强制结束当前句（例如关闭通道时）。</summary>
    public void ForceEnd()
    {
        if (_inSpeech) { _inSpeech = false; SpeechEnd?.Invoke(); }
        _candidate.Clear(); _candidateFrames = 0;
    }
}

/// <summary>固定时长的环形预缓冲（按键说话用）。</summary>
public sealed class PreRollBuffer(int capacitySamples)
{
    private readonly Queue<float[]> _chunks = new();
    private int _total;

    public void Push(float[] chunk)
    {
        _chunks.Enqueue(chunk);
        _total += chunk.Length;
        while (_chunks.Count > 1 && _total - _chunks.Peek().Length >= capacitySamples)
            _total -= _chunks.Dequeue().Length;
    }

    public float[] Drain()
    {
        var all = _chunks.SelectMany(c => c).ToArray();
        _chunks.Clear();
        _total = 0;
        return all.Length > capacitySamples ? all[^capacitySamples..] : all;
    }
}
