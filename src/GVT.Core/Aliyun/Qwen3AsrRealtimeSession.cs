using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using GVT.Core.Audio;
using GVT.Core.Config;

namespace GVT.Core.Aliyun;

/// <summary>
/// qwen3-asr-flash-realtime：百炼 /api-ws/v1/realtime，OpenAI Realtime 风格的事件协议。一句话一个会话。
/// 不用服务端断句（turn_detection = null）：句子边界由本软件的按键 / VAD 决定，说完时 commit + session.finish。
/// 和 FunAsrSession 一样，创建后立刻在后台建连，期间到达的音频先缓存。
/// </summary>
public sealed class Qwen3AsrRealtimeSession : IAsrSession
{
    public const string ModelPrefix = "qwen3-asr-flash-realtime";

    private readonly DashScopeConfig _cfg;
    private readonly string _apiKey;
    private readonly Channel<byte[]> _audio = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<string> _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _cts = new();
    private readonly List<string> _finals = new();
    private readonly List<float> _pcm = new();
    private readonly object _lock = new();
    private string _partial = "";
    private long _samples;
    private int _eventId;
    private DashScopeSocket? _socket;
    private Task? _run;

    /// <summary>调试用：服务端原始 JSON。</summary>
    public event Action<string>? RawMessage;
    public event Action<string>? Partial;
    public double AudioSeconds => Interlocked.Read(ref _samples) / 16000.0;
    public float[] CapturedAudio { get { lock (_pcm) return _pcm.ToArray(); } }
    public string Model { get; }

    /// <summary>识别出的语种（zh / en / ja …），最终结果里带。</summary>
    public string? Language { get; private set; }

    public static bool Handles(string? model) => model != null && model.StartsWith(ModelPrefix, StringComparison.OrdinalIgnoreCase);

    private Qwen3AsrRealtimeSession(DashScopeConfig cfg, string apiKey, string model)
    {
        _cfg = cfg;
        _apiKey = apiKey;
        Model = model;
    }

    public static Qwen3AsrRealtimeSession Start(DashScopeConfig cfg, string apiKey, AsrRequest req)
    {
        var s = new Qwen3AsrRealtimeSession(cfg, apiKey, req.Model ?? ModelPrefix);
        s._run = Task.Run(() => s.RunAsync(req));
        return s;
    }

    public static string RealtimeUrl(DashScopeConfig cfg, string model)
        => cfg.WebSocketUrl.Replace("/api-ws/v1/inference", "/api-ws/v1/realtime") + "?model=" + Uri.EscapeDataString(model);

    public void Push(ReadOnlySpan<float> samples16k)
    {
        if (samples16k.Length == 0) return;
        Interlocked.Add(ref _samples, samples16k.Length);
        lock (_pcm) foreach (var s in samples16k) _pcm.Add(s);
        _audio.Writer.TryWrite(PcmConvert.FloatToPcm16(samples16k));
    }

    private string NextId() => "gvt_" + Interlocked.Increment(ref _eventId);

    private async Task RunAsync(AsrRequest req)
    {
        try
        {
            var ct = _cts.Token;
            _socket = await DashScopeSocket.ConnectAsync(RealtimeUrl(_cfg, Model), _apiKey, ct, [("OpenAI-Beta", "realtime=v1")]).ConfigureAwait(false);
            _socket.JsonMessage += OnJson;
            _socket.Closed += ex =>
            {
                var err = new DashScopeException("识别连接已断开" + (ex != null ? $"：{ex.Message}" : ""));
                _started.TrySetException(err);
                _finished.TrySetException(err);
            };

            var transcription = new Dictionary<string, object>();
            if (!string.IsNullOrWhiteSpace(req.LanguageHint)) transcription["language"] = req.LanguageHint!;
            if (!string.IsNullOrWhiteSpace(req.Context)) transcription["corpus"] = new { text = req.Context };
            await _socket.SendJsonAsync(new
            {
                event_id = NextId(),
                type = "session.update",
                session = new
                {
                    modalities = new[] { "text" },
                    input_audio_format = "pcm",
                    sample_rate = 16000,
                    input_audio_transcription = transcription,
                    turn_detection = (object?)null,
                },
            }, ct).ConfigureAwait(false);

            await _started.Task.WaitAsync(TimeSpan.FromMilliseconds(_cfg.RequestTimeoutMs), ct).ConfigureAwait(false);

            var reader = _audio.Reader;
            var batch = new MemoryStream();
            while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                batch.SetLength(0);
                while (batch.Length < 32000 && reader.TryRead(out var chunk)) batch.Write(chunk);
                if (batch.Length > 0)
                    await _socket.SendJsonAsync(new { event_id = NextId(), type = "input_audio_buffer.append", audio = Convert.ToBase64String(batch.GetBuffer(), 0, (int)batch.Length) }, ct).ConfigureAwait(false);
            }

            await _socket.SendJsonAsync(new { event_id = NextId(), type = "input_audio_buffer.commit" }, ct).ConfigureAwait(false);
            await _socket.SendJsonAsync(new { event_id = NextId(), type = "session.finish" }, ct).ConfigureAwait(false);
            await _finished.Task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _started.TrySetException(ex);
            _finished.TrySetException(ex);
        }
    }

    private void OnJson(JsonElement msg)
    {
        RawMessage?.Invoke(msg.GetRawText());
        var type = msg.TryGetProperty("type", out var t) ? t.GetString() : null;
        switch (type)
        {
            case "session.updated":
                _started.TrySetResult();
                break;
            case "conversation.item.input_audio_transcription.text":
                lock (_lock) _partial = Str(msg, "text") + Str(msg, "stash");
                Partial?.Invoke(CurrentText(includePartial: true));
                break;
            case "conversation.item.input_audio_transcription.completed":
                lock (_lock) { _finals.Add(Str(msg, "transcript")); _partial = ""; }
                Language = Str(msg, "language");
                Partial?.Invoke(CurrentText(includePartial: false));
                break;
            case "session.finished":
                _finished.TrySetResult(CurrentText(includePartial: false));
                break;
            case "conversation.item.input_audio_transcription.failed":
            case "error":
                var err = new DashScopeException("识别失败 " + ErrorOf(msg));
                _started.TrySetException(err);
                _finished.TrySetException(err);
                break;
        }
    }

    private static string Str(JsonElement msg, string name) => msg.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string ErrorOf(JsonElement msg)
        => msg.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object
            ? $"{Str(e, "code")}: {Str(e, "message")}"
            : msg.GetRawText();

    private string CurrentText(bool includePartial)
    {
        lock (_lock)
        {
            var sb = new StringBuilder();
            foreach (var f in _finals) sb.Append(f);
            if (includePartial) sb.Append(_partial);
            return sb.ToString().Trim();
        }
    }

    public async Task<string> FinishAsync(CancellationToken ct)
    {
        _audio.Writer.TryComplete();
        return await _finished.Task.WaitAsync(TimeSpan.FromMilliseconds(_cfg.AsrFinishTimeoutMs), ct).ConfigureAwait(false);
    }

    public void Abort()
    {
        _audio.Writer.TryComplete();
        _cts.Cancel();
        _finished.TrySetCanceled();
    }

    public async ValueTask DisposeAsync()
    {
        _audio.Writer.TryComplete();
        _cts.Cancel();
        if (_run != null) { try { await _run.ConfigureAwait(false); } catch { } }
        if (_socket != null) await _socket.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}
