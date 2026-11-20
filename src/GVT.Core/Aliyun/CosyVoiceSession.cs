using System.Text.Json;
using GVT.Core.Audio;
using GVT.Core.Config;

namespace GVT.Core.Aliyun;

/// <summary>
/// CosyVoice 流式合成：Prepare 时就建连并发送 run-task（与识别收尾、翻译并行），
/// 拿到译文后只需 continue-task + finish-task，首包延迟里不含建连时间。
/// </summary>
public sealed class CosyVoiceSession : ITtsSession
{
    private readonly DashScopeConfig _cfg;
    private readonly string _apiKey;
    private readonly string _taskId = DashScopeSocket.NewTaskId();
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<int> _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _cts = new();
    private Task? _connect;
    private DashScopeSocket? _socket;
    private Action<float[]>? _onAudio;
    private byte? _oddByte;
    private volatile bool _cancelled;

    public int SampleRate => _cfg.TtsSampleRate;

    private CosyVoiceSession(DashScopeConfig cfg, string apiKey)
    {
        _cfg = cfg;
        _apiKey = apiKey;
    }

    public static CosyVoiceSession Prepare(DashScopeConfig cfg, string apiKey, TtsVoice voice)
    {
        var s = new CosyVoiceSession(cfg, apiKey);
        s._connect = Task.Run(() => s.ConnectAsync(voice));
        return s;
    }

    private async Task ConnectAsync(TtsVoice v)
    {
        try
        {
            var ct = _cts.Token;
            _socket = await DashScopeSocket.ConnectAsync(_cfg.WebSocketUrl, _apiKey, ct).ConfigureAwait(false);
            _socket.JsonMessage += OnJson;
            _socket.BinaryMessage += OnBinary;
            _socket.Closed += ex =>
            {
                var err = new DashScopeException("合成连接已断开" + (ex != null ? $"：{ex.Message}" : ""));
                _started.TrySetException(err);
                _finished.TrySetException(err);
            };

            var parameters = new Dictionary<string, object>
            {
                ["text_type"] = "PlainText",
                ["voice"] = v.Voice,
                ["format"] = "pcm",
                ["sample_rate"] = _cfg.TtsSampleRate,
                ["volume"] = 50,
                ["rate"] = Math.Clamp(v.Rate, 0.5, 2.0),
                ["pitch"] = 1.0,
            };
            if (!string.IsNullOrWhiteSpace(v.LanguageHint)) parameters["language_hints"] = new[] { v.LanguageHint };
            if (!string.IsNullOrWhiteSpace(v.Instruction)) parameters["instruction"] = v.Instruction;

            await _socket.SendJsonAsync(new
            {
                header = new { action = "run-task", task_id = _taskId, streaming = "duplex" },
                payload = new
                {
                    task_group = "audio",
                    task = "tts",
                    function = "SpeechSynthesizer",
                    model = v.Model,
                    parameters,
                    input = new { },
                },
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _started.TrySetException(ex);
            _finished.TrySetException(ex);
        }
    }

    private void OnJson(JsonElement msg)
    {
        switch (DashScopeSocket.EventOf(msg))
        {
            case "task-started":
                _started.TrySetResult();
                break;
            case "task-finished":
                int chars = 0;
                if (msg.TryGetProperty("payload", out var p) && p.TryGetProperty("usage", out var u)
                    && u.ValueKind == JsonValueKind.Object && u.TryGetProperty("characters", out var c) && c.ValueKind == JsonValueKind.Number)
                    chars = c.GetInt32();
                _finished.TrySetResult(chars);
                break;
            case "task-failed":
                var err = new DashScopeException("合成失败 " + DashScopeSocket.ErrorOf(msg));
                _started.TrySetException(err);
                _finished.TrySetException(err);
                break;
        }
    }

    private void OnBinary(byte[] data)
    {
        if (_cancelled || data.Length == 0) return;
        // PCM16 可能在消息边界被拆开，保留奇数字节
        ReadOnlySpan<byte> span = data;
        byte[]? joined = null;
        if (_oddByte.HasValue)
        {
            joined = new byte[data.Length + 1];
            joined[0] = _oddByte.Value;
            data.CopyTo(joined, 1);
            span = joined;
            _oddByte = null;
        }
        if (span.Length % 2 == 1)
        {
            _oddByte = span[^1];
            span = span[..^1];
        }
        if (span.Length > 0) _onAudio?.Invoke(PcmConvert.Pcm16ToFloat(span));
    }

    public async Task<int> SpeakAsync(string text, Action<float[]> onAudio, CancellationToken ct)
    {
        _onAudio = onAudio;
        using var reg = ct.Register(() => { _ = CancelAsync(); });
        var timeout = TimeSpan.FromMilliseconds(_cfg.RequestTimeoutMs);
        await _started.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
        var socket = _socket ?? throw new DashScopeException("合成连接未建立");
        await socket.SendJsonAsync(new
        {
            header = new { action = "continue-task", task_id = _taskId, streaming = "duplex" },
            payload = new { input = new { text } },
        }, ct).ConfigureAwait(false);
        await socket.SendJsonAsync(new
        {
            header = new { action = "finish-task", task_id = _taskId, streaming = "duplex" },
            payload = new { input = new { } },
        }, ct).ConfigureAwait(false);
        // 合成时长与文本长度有关，给足余量
        return await _finished.Task.WaitAsync(timeout + TimeSpan.FromSeconds(text.Length / 8.0), ct).ConfigureAwait(false);
    }

    public async Task CancelAsync()
    {
        if (_cancelled) return;
        _cancelled = true;
        _onAudio = null;
        try
        {
            if (_socket is { IsOpen: true } socket && _started.Task.IsCompletedSuccessfully)
            {
                using var t = new CancellationTokenSource(1000);
                await socket.SendJsonAsync(new
                {
                    header = new { action = "finish-task", task_id = _taskId, streaming = "duplex" },
                    payload = new { input = new { directive = "cancel" } },
                }, t.Token).ConfigureAwait(false);
            }
        }
        catch { }
        _finished.TrySetCanceled();
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_connect != null) { try { await _connect.ConfigureAwait(false); } catch { } }
        if (_socket != null) await _socket.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}
