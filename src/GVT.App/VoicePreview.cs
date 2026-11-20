using GVT.Core.Aliyun;
using GVT.Core.Audio;
using GVT.Core.Config;

namespace GVT.App;

/// <summary>在自己的耳机里试听音色（不会发给队友）。</summary>
public static class VoicePreview
{
    private static CancellationTokenSource? _current;

    public const string EnglishSample = "Yo, two on the left, second floor. Don't push yet, wait for me.";
    public const string ChineseSample = "左边二楼有两个人，先别冲，等我一下。";

    public static async Task PlayAsync(AppConfig cfg, string apiKey, TtsVoice voice, string text)
    {
        _current?.Cancel();
        var cts = new CancellationTokenSource();
        _current = cts;

        using var mixer = new OutputMixer(DeviceCatalog.FindRender(cfg.Receive.HeadphoneDevice));
        var player = new SpeechQueuePlayer(mixer.SampleRate, mixer.Channels, cfg.DashScope.TtsSampleRate);
        mixer.AddInput(player);
        mixer.Start();
        var slot = player.Enqueue(1, 0, text);
        await using (var tts = CosyVoiceSession.Prepare(cfg.DashScope, apiKey, voice))
        {
            await tts.SpeakAsync(text, pcm => player.Append(slot, pcm), cts.Token);
        }
        player.Complete(slot);
        // 等播放完
        while (player.PendingCount > 0 && !cts.IsCancellationRequested) await Task.Delay(50);
        await Task.Delay(150);
    }

    public static void Stop() => _current?.Cancel();
}
