using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GVT.Core.Config;

namespace GVT.Core.Aliyun;

public sealed record CustomVoiceResult(string VoiceId, byte[]? PreviewWav);

/// <summary>
/// 音色定制（voice-enrollment）：
/// 音色设计 = 用一句话描述生成新音色；声音复刻 = 用一段录音（Base64 Data URI）复刻音色。
/// 生成的音色绑定 target_model（这里用 qwen-audio-3.1-tts-flash），之后像系统音色一样使用。
/// </summary>
public sealed class VoiceCustomization(DashScopeConfig cfg, string apiKey)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(120) };

    public Task<CustomVoiceResult> DesignAsync(string prompt, string previewText, string prefix, string language, string targetModel, CancellationToken ct)
        => PostAsync(new
        {
            model = "voice-enrollment",
            input = new
            {
                action = "create_voice",
                target_model = targetModel,
                voice_prompt = prompt,
                preview_text = previewText,
                prefix,
                language_hints = new[] { language },
            },
            parameters = new { sample_rate = 24000, response_format = "wav" },
        }, ct);

    public Task<CustomVoiceResult> CloneAsync(byte[] wav, string prefix, string targetModel, CancellationToken ct)
        => PostAsync(new
        {
            model = "voice-enrollment",
            input = new
            {
                action = "create_voice",
                target_model = targetModel,
                prefix,
                audio = "data:audio/wav;base64," + Convert.ToBase64String(wav),
            },
        }, ct);

    public async Task DeleteAsync(string voiceId, CancellationToken ct)
        => await PostAsync(new { model = "voice-enrollment", input = new { action = "delete_voice", voice_id = voiceId } }, ct).ConfigureAwait(false);

    private async Task<CustomVoiceResult> PostAsync(object body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, cfg.ApiBaseUrl.TrimEnd('/') + "/services/audio/tts/customization");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new DashScopeException($"音色定制失败 HTTP {(int)resp.StatusCode}: {(json.Length > 400 ? json[..400] : json)}");
        using var doc = JsonDocument.Parse(json);
        var output = doc.RootElement.TryGetProperty("output", out var o) ? o : default;
        string id = output.ValueKind == JsonValueKind.Object && output.TryGetProperty("voice_id", out var v) ? v.GetString() ?? "" : "";
        byte[]? preview = null;
        if (output.ValueKind == JsonValueKind.Object && output.TryGetProperty("preview_audio", out var pa)
            && pa.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.String)
            preview = Convert.FromBase64String(d.GetString()!);
        return new CustomVoiceResult(id, preview);
    }
}
