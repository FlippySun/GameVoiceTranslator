using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using GVT.Core.Audio;
using GVT.Core.Config;

namespace GVT.Core.Aliyun;

/// <summary>
/// fun-asr-realtime：一句话一个任务。创建后立刻在后台建连，期间到达的音频先缓存，
/// 连上后一次性补发，所以建连时间与用户说话时间重叠，不增加句末延迟。
/// </summary>
public sealed class FunAsrSession : IAsrSession
{
    private readonly DashScopeConfig _cfg;
    private readonly string _apiKey;
    private readonly string _taskId = DashScopeSocket.NewTaskId();
    private readonly Channel<byte[]> _audio = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<string> _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _cts = new();
    private readonly SortedDictionary<long, string> _finals = new();
    private readonly object _lock = new();
    private string _partial = "";
    private long _samples;
    private DashScopeSocket? _socket;
    private Task? _run;

    public event Action<string>? Partial;
    public double AudioSeconds => Interlocked.Read(ref _samples) / 16000.0;

    private FunAsrSession(DashScopeConfig cfg, string apiKey)
    {
        _cfg = cfg;
        _apiKey = apiKey;
    }

    public static FunAsrSession Start(DashScopeConfig cfg, string apiKey, string? languageHint, string? contextText)
    {
        var s = new FunAsrSession(cfg, apiKey);
        s._run = Task.Run(() => s.RunAsync(languageHint, contextText));
        return s;
    }

    public void Push(ReadOnlySpan<float> samples16k)
    {
        if (samples16k.Length == 0) return;
        Interlocked.Add(ref _samples, samples16k.Length);
        _audio.Writer.TryWrite(PcmConvert.FloatToPcm16(samples16k));
    }

    private async Task RunAsync(string? languageHint, string? contextText)
    {
        try
        {
            var ct = _cts.Token;
            _socket = await DashScopeSocket.ConnectAsync(_cfg.WebSocketUrl, _apiKey, ct).ConfigureAwait(false);
            _socket.JsonMessage += OnJson;
            _socket.Closed += ex =>
            {
                var err = new DashScopeException("识别连接已断开" + (ex != null ? $"：{ex.Message}" : ""));
                _started.TrySetException(err);
                _finished.TrySetException(err);
            };

            var parameters = new Dictionary<string, object> { ["format"] = "pcm", ["sample_rate"] = 16000 };
            if (!string.IsNullOrWhiteSpace(languageHint)) parameters["language_hints"] = new[] { languageHint };
            object input = string.IsNullOrWhiteSpace(contextText)
                ? new { }
                : new { context = new[] { new { role = "user", content = new[] { new { type = "input_text", text = contextText } } } } };

            await _socket.SendJsonAsync(new
            {
                header = new { action = "run-task", task_id = _taskId, streaming = "duplex" },
                payload = new
                {
                    task_group = "audio",
                    task = "asr",
                    function = "recognition",
                    model = _cfg.AsrModel,
                    parameters,
                    input,
                },
            }, ct).ConfigureAwait(false);

            await _started.Task.WaitAsync(TimeSpan.FromMilliseconds(_cfg.RequestTimeoutMs), ct).ConfigureAwait(false);

            // 发送音频：把积压的块合并成一条消息，追赶建连期间缓存的数据
            var reader = _audio.Reader;
            var batch = new MemoryStream();
            while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                batch.SetLength(0);
                while (batch.Length < 32000 && reader.TryRead(out var chunk)) batch.Write(chunk);
                if (batch.Length > 0)
                    await _socket.SendBinaryAsync(batch.ToArray(), ct).ConfigureAwait(false);
            }

            await _socket.SendJsonAsync(new
            {
                header = new { action = "finish-task", task_id = _taskId, streaming = "duplex" },
                payload = new { input = new { } },
            }, ct).ConfigureAwait(false);

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
        switch (DashScopeSocket.EventOf(msg))
        {
            case "task-started":
                _started.TrySetResult();
                break;
            case "result-generated":
                HandleResult(msg);
                break;
            case "task-finished":
                _finished.TrySetResult(CurrentText(includePartial: false));
                break;
            case "task-failed":
                var err = new DashScopeException("识别失败 " + DashScopeSocket.ErrorOf(msg));
                _started.TrySetException(err);
                _finished.TrySetException(err);
                break;
        }
    }

    private void HandleResult(JsonElement msg)
    {
        if (!msg.TryGetProperty("payload", out var p) || !p.TryGetProperty("output", out var o) || !o.TryGetProperty("sentence", out var s))
            return;
        if (s.TryGetProperty("heartbeat", out var hb) && hb.ValueKind == JsonValueKind.True) return;
        var text = s.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
        long id = s.TryGetProperty("sentence_id", out var sid) && sid.ValueKind == JsonValueKind.Number ? sid.GetInt64() : 0;
        bool end = s.TryGetProperty("sentence_end", out var se) && se.ValueKind == JsonValueKind.True;
        lock (_lock)
        {
            if (end) { _finals[id] = text; _partial = ""; }
            else _partial = text;
        }
        Partial?.Invoke(CurrentText(includePartial: true));
    }

    private string CurrentText(bool includePartial)
    {
        lock (_lock)
        {
            var sb = new StringBuilder();
            foreach (var v in _finals.Values) sb.Append(v);
            if (includePartial) sb.Append(_partial);
            return sb.ToString().Trim();
        }
    }

    public async Task<string> FinishAsync(CancellationToken ct)
    {
        _audio.Writer.TryComplete();
        return await _finished.Task.WaitAsync(TimeSpan.FromMilliseconds(_cfg.RequestTimeoutMs), ct).ConfigureAwait(false);
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
