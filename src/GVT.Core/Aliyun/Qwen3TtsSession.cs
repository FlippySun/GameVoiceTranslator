using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GVT.Core.Audio;
using GVT.Core.Config;

namespace GVT.Core.Aliyun;

/// <summary>
/// Qwen3-TTS（qwen3-tts-flash）：HTTP + SSE 流式返回 Base64 PCM（24 kHz、16 bit、单声道）。
/// 输出统一重采样到 TtsSampleRate，和其他合成引擎对播放队列保持一致。
/// </summary>
public sealed class Qwen3TtsSession : ITtsSession
{
    private const int SourceRate = 24000;
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(10),
    }) { Timeout = Timeout.InfiniteTimeSpan };

    private readonly DashScopeConfig _cfg;
    private readonly string _apiKey;
    private readonly TtsVoice _voice;
    private readonly CancellationTokenSource _cts = new();

    public int SampleRate => _cfg.TtsSampleRate;

    public Qwen3TtsSession(DashScopeConfig cfg, string apiKey, TtsVoice voice)
    {
        _cfg = cfg;
        _apiKey = apiKey;
        _voice = voice;
    }

    public static bool Handles(string model) => model.StartsWith("qwen3-tts", StringComparison.OrdinalIgnoreCase);

    public async Task<int> SpeakAsync(string text, Action<float[]> onAudio, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        linked.CancelAfter(TimeSpan.FromMilliseconds(_cfg.RequestTimeoutMs) + TimeSpan.FromSeconds(text.Length / 8.0));

        var input = new Dictionary<string, object>
        {
            ["text"] = text,
            ["voice"] = _voice.Voice,
            ["language_type"] = _voice.LanguageHint switch { "en" => "English", "zh" => "Chinese", _ => "Auto" },
        };
        var body = new { model = _voice.Model, input };

        using var req = new HttpRequestMessage(HttpMethod.Post, _cfg.ApiBaseUrl.TrimEnd('/') + "/services/aigc/multimodal-generation/generation");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        req.Headers.Add("X-DashScope-SSE", "enable");
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
            throw new DashScopeException($"合成失败 HTTP {(int)resp.StatusCode}: {(err.Length > 300 ? err[..300] : err)}");
        }

        var resampler = new LinearResampler(SourceRate, _cfg.TtsSampleRate, 1);
        byte? odd = null;
        int chars = 0;
        using var stream = await resp.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(linked.Token).ConfigureAwait(false) is { } line)
        {
            if (!line.StartsWith("data:")) continue;
            using var doc = JsonDocument.Parse(line.AsMemory(5));
            var root = doc.RootElement;
            if (root.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(code.GetString()))
                throw new DashScopeException($"合成失败 {code.GetString()}: {(root.TryGetProperty("message", out var m) ? m.GetString() : "")}");
            if (root.TryGetProperty("usage", out var u) && u.TryGetProperty("characters", out var c) && c.ValueKind == JsonValueKind.Number)
                chars = c.GetInt32();
            if (!root.TryGetProperty("output", out var o) || !o.TryGetProperty("audio", out var a)) continue;
            if (!a.TryGetProperty("data", out var d) || d.ValueKind != JsonValueKind.String) continue;
            var b64 = d.GetString();
            if (string.IsNullOrEmpty(b64)) continue;

            var bytes = Convert.FromBase64String(b64);
            ReadOnlySpan<byte> span = bytes;
            byte[]? joined = null;
            if (odd.HasValue)
            {
                joined = new byte[bytes.Length + 1];
                joined[0] = odd.Value;
                bytes.CopyTo(joined, 1);
                span = joined;
                odd = null;
            }
            if (span.Length % 2 == 1) { odd = span[^1]; span = span[..^1]; }
            if (span.Length == 0) continue;
            var pcm = resampler.Process(PcmConvert.Pcm16ToFloat(span));
            if (pcm.Length > 0) onAudio(pcm);
        }
        return chars > 0 ? chars : text.Length;
    }

    public Task CancelAsync()
    {
        _cts.Cancel();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _cts.Dispose();
        return ValueTask.CompletedTask;
    }
}
