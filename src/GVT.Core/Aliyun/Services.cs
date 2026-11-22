using GVT.Core.Config;

namespace GVT.Core.Aliyun;

/// <summary>一句话的流式识别会话。</summary>
public interface IAsrSession : IAsyncDisposable
{
    event Action<string>? Partial;
    /// <summary>推入 16 kHz 单声道样本（任意线程）。</summary>
    void Push(ReadOnlySpan<float> samples16k);
    /// <summary>音频结束，等待最终文本。</summary>
    Task<string> FinishAsync(CancellationToken ct);
    void Abort();
    double AudioSeconds { get; }
}

/// <summary>一句话的语音合成会话。创建时即开始预连接。</summary>
public interface ITtsSession : IAsyncDisposable
{
    int SampleRate { get; }
    /// <returns>计费字符数</returns>
    Task<int> SpeakAsync(string text, Action<float[]> onAudio, CancellationToken ct);
    Task CancelAsync();
}

public enum TranslationConfidence { High, Low, Garbled }

public sealed record TranslationResult(string Text, int InputTokens, int OutputTokens, TranslationConfidence Confidence = TranslationConfidence.High);

public interface ITranslator
{
    Task<TranslationResult> TranslateAsync(string text, Direction direction, IReadOnlyList<string> recentContext, CancellationToken ct);
}

public interface ISpeechServices
{
    IAsrSession StartAsr(string? languageHint, string? contextText);
    ITtsSession PrepareTts(TtsVoice voice);
    ITranslator Translator { get; }
    int TtsSampleRate { get; }
    /// <summary>预先建立 HTTPS 连接，避免第一句多等 TLS 握手。</summary>
    Task WarmUpAsync() => Task.CompletedTask;
}

public sealed class DashScopeServices : ISpeechServices
{
    private readonly DashScopeConfig _cfg;
    private readonly string _apiKey;

    public ITranslator Translator { get; }
    public int TtsSampleRate => _cfg.TtsSampleRate;

    public DashScopeServices(DashScopeConfig cfg, string apiKey, Glossary glossary)
    {
        _cfg = cfg;
        _apiKey = apiKey;
        Translator = new QwenTranslator(cfg, apiKey, glossary);
    }

    public IAsrSession StartAsr(string? languageHint, string? contextText)
        => FunAsrSession.Start(_cfg, _apiKey, languageHint, contextText);

    public ITtsSession PrepareTts(TtsVoice voice) => Qwen3TtsSession.Handles(voice.Model)
        ? new Qwen3TtsSession(_cfg, _apiKey, voice)
        : CosyVoiceSession.Prepare(_cfg, _apiKey, voice);

    public Task WarmUpAsync() => ((QwenTranslator)Translator).WarmUpAsync();
}
