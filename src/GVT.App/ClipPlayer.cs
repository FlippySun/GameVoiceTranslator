using GVT.App.Models;
using GVT.Core.Audio;
using GVT.Core.Config;

namespace GVT.App;

/// <summary>
/// 在自己的耳机里重听某一句的合成语音。单独开一路播放，不经过翻译队列，
/// 不会发给队友，也不会被队友原声压低/排队影响。同一时间只播一句，再点同一句即停止。
/// </summary>
public static class ClipPlayer
{
    private static CancellationTokenSource? _cts;
    private static TimelineItem? _current;

    public static async Task ToggleAsync(AppConfig cfg, TimelineItem item)
    {
        var wasCurrent = _current == item;
        Stop();
        if (wasCurrent || item.Audio == null) return;

        var cts = new CancellationTokenSource();
        _cts = cts;
        _current = item;
        item.IsPlaying = true;
        try
        {
            using var mixer = new OutputMixer(DeviceCatalog.FindRender(cfg.Receive.HeadphoneDevice));
            var player = new SpeechQueuePlayer(mixer.SampleRate, mixer.Channels, item.AudioRate);
            mixer.AddInput(player);
            var slot = player.Enqueue(1, 0, item.Primary);
            player.Append(slot, item.Audio);
            player.Complete(slot);
            mixer.Start();
            await Task.Delay(150, cts.Token);
            while (player.PendingCount > 0) await Task.Delay(40, cts.Token);
            await Task.Delay(120, cts.Token);
        }
        catch (OperationCanceledException) { }
        finally
        {
            item.IsPlaying = false;
            if (_current == item) { _current = null; _cts = null; }
        }
    }

    public static void Stop()
    {
        _cts?.Cancel();
        if (_current != null) _current.IsPlaying = false;
        _current = null;
        _cts = null;
    }
}
