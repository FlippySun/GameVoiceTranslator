using GVT.Core.Config;

namespace GVT.Core.Aliyun;

/// <summary>一句话的流式识别会话。</summary>
/// <summary>一句话识别的参数。Model 为空时用配置里的 AsrModel。</summary>
public sealed record AsrRequest(string? LanguageHint, string? Context = null)
{
    public string? Model { get; init; }
    /// <summary>即时热词（词 → 权重 1~5）。只有 qwen-audio 系列识别支持，fun-asr 会忽略。</summary>
    public IReadOnlyDictionary<string, int>? Hotwords { get; init; }
    /// <summary>预编译热词列表 ID（fun-asr-realtime 用）。</summary>
    public string? VocabularyId { get; init; }
    /// <summary>让识别服务顺手去掉语气词、重复和改口（仅 qwen-audio-*-message）。</summary>
    public bool DisfluencyRemoval { get; init; }
}

public interface IAsrSession : IAsyncDisposable
{
    event Action<string>? Partial;
    /// <summary>推入 16 kHz 单声道样本（任意线程）。</summary>
    void Push(ReadOnlySpan<float> samples16k);
    /// <summary>音频结束，等待最终文本。</summary>
    Task<string> FinishAsync(CancellationToken ct);
    void Abort();
    double AudioSeconds { get; }
    /// <summary>本句已推入的全部音频（识别超时后用它重试）。</summary>
    float[] CapturedAudio => [];
    /// <summary>识别模型判断出的语种（zh / en / ja …）；不提供语种的模型为 null。</summary>
    string? Language => null;
    /// <summary>按 token 计费的识别模型返回的用量（输入, 输出）；按秒计费的为 (0, 0)。</summary>
    (int Input, int Output) Tokens => (0, 0);
}

/// <summary>一句话的语音合成会话。创建时即开始预连接。</summary>
public interface ITtsSession : IAsyncDisposable
{
    int SampleRate { get; }
    /// <returns>计费字符数</returns>
    Task<int> SpeakAsync(string text, Action<float[]> onAudio, CancellationToken ct);
    Task CancelAsync();
}

/// <summary>Blocked = 平台内容审核拦截了译文（多半是骂人的话）。</summary>
public enum TranslationConfidence { High, Low, Garbled, Blocked }

public sealed record TranslationResult(string Text, int InputTokens, int OutputTokens, TranslationConfidence Confidence = TranslationConfidence.High);

public interface ITranslator
{
    Task<TranslationResult> TranslateAsync(string text, Direction direction, IReadOnlyList<string> recentContext, CancellationToken ct);
}

public interface ISpeechServices
{
    IAsrSession StartAsr(AsrRequest request);
    ITtsSession PrepareTts(TtsVoice voice);
    ITranslator Translator { get; }
    int TtsSampleRate { get; }
    /// <summary>预先建立 HTTPS 连接，避免第一句多等 TLS 握手。</summary>
    Task WarmUpAsync() => Task.CompletedTask;
    /// <summary>准备识别热词表，返回 vocabulary_id（失败或不支持返回 null）。</summary>
    Task<string?> EnsureVocabularyAsync(IReadOnlyDictionary<string, int> words, string lang) => Task.FromResult<string?>(null);
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

    public IAsrSession StartAsr(AsrRequest request) => StartAsr(_cfg, _apiKey, request);

    /// <summary>按模型名选识别协议：qwen3-asr-flash-realtime / qwen3-asr-flash 各有自己的接口，其余（fun-asr、qwen-audio）走 inference WebSocket。</summary>
    public static IAsrSession StartAsr(DashScopeConfig cfg, string apiKey, AsrRequest request)
    {
        var model = string.IsNullOrWhiteSpace(request.Model) ? cfg.AsrModel : request.Model!;
        var req = request with { Model = model };
        if (Qwen3AsrRealtimeSession.Handles(model)) return Qwen3AsrRealtimeSession.Start(cfg, apiKey, req);
        if (Qwen3AsrFlashSession.Handles(model)) return new Qwen3AsrFlashSession(cfg, apiKey, req);
        return FunAsrSession.Start(cfg, apiKey, req);
    }

    public ITtsSession PrepareTts(TtsVoice voice) => Qwen3TtsSession.Handles(voice.Model)
        ? new Qwen3TtsSession(_cfg, _apiKey, voice)
        : CosyVoiceSession.Prepare(_cfg, _apiKey, voice);

    public Task WarmUpAsync() => ((QwenTranslator)Translator).WarmUpAsync();

    public Task<string?> EnsureVocabularyAsync(IReadOnlyDictionary<string, int> words, string lang)
        => AsrVocabulary.EnsureAsync(_cfg, _apiKey, words, lang);
}
