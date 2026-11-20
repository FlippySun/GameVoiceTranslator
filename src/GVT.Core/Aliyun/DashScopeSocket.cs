using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace GVT.Core.Aliyun;

/// <summary>百炼 /api-ws/v1/inference 的通用 WebSocket 封装：带鉴权连接、串行发送、文本/二进制分发。</summary>
public sealed class DashScopeSocket : IAsyncDisposable
{
    private readonly ClientWebSocket _ws = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private Task? _receiveLoop;

    public event Action<JsonElement>? JsonMessage;
    public event Action<byte[]>? BinaryMessage;
    public event Action<Exception?>? Closed;

    public static async Task<DashScopeSocket> ConnectAsync(string url, string apiKey, CancellationToken ct)
    {
        var s = new DashScopeSocket();
        s._ws.Options.SetRequestHeader("Authorization", $"bearer {apiKey}");
        s._ws.Options.SetRequestHeader("user-agent", "GameVoiceTranslator/0.1");
        s._ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        await s._ws.ConnectAsync(new Uri(url), ct).ConfigureAwait(false);
        s._receiveLoop = Task.Run(s.ReceiveLoopAsync);
        return s;
    }

    public bool IsOpen => _ws.State == WebSocketState.Open;

    public async Task SendJsonAsync(object payload, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try { await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false); }
        finally { _sendLock.Release(); }
    }

    public async Task SendBinaryAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try { await _ws.SendAsync(data, WebSocketMessageType.Binary, true, ct).ConfigureAwait(false); }
        finally { _sendLock.Release(); }
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        var ms = new MemoryStream();
        Exception? error = null;
        try
        {
            while (_ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                var r = await _ws.ReceiveAsync(buffer, _cts.Token).ConfigureAwait(false);
                if (r.MessageType == WebSocketMessageType.Close) break;
                ms.Write(buffer, 0, r.Count);
                if (!r.EndOfMessage) continue;

                var data = ms.ToArray();
                ms.SetLength(0);
                if (r.MessageType == WebSocketMessageType.Text)
                {
                    using var doc = JsonDocument.Parse(data);
                    JsonMessage?.Invoke(doc.RootElement.Clone());
                }
                else
                {
                    BinaryMessage?.Invoke(data);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { error = ex; }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            Closed?.Invoke(error);
        }
    }

    public static string NewTaskId() => Guid.NewGuid().ToString("N");

    public static string EventOf(JsonElement msg)
        => msg.TryGetProperty("header", out var h) && h.TryGetProperty("event", out var e) ? e.GetString() ?? "" : "";

    public static string ErrorOf(JsonElement msg)
    {
        if (!msg.TryGetProperty("header", out var h)) return "未知错误";
        var code = h.TryGetProperty("error_code", out var c) ? c.GetString() : null;
        var text = h.TryGetProperty("error_message", out var m) ? m.GetString() : null;
        return $"{code}: {text}";
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_ws.State == WebSocketState.Open)
            {
                using var t = new CancellationTokenSource(500);
                await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", t.Token).ConfigureAwait(false);
            }
        }
        catch { }
        _cts.Cancel();
        _ws.Abort();
        _ws.Dispose();
        if (_receiveLoop != null) { try { await _receiveLoop.ConfigureAwait(false); } catch { } }
        _cts.Dispose();
    }
}

public sealed class DashScopeException(string message) : Exception(message);
