using System.Collections.Concurrent;
using NAudio.Wave;

namespace GVT.Core.Audio;

public sealed class SpeechSlot
{
    public long Id { get; init; }
    public int Epoch { get; init; }
    public string Text { get; set; } = "";
    public float Pan { get; set; }
    public DateTime CreatedUtc { get; init; }
    public DateTime? StartedUtc { get; internal set; }
    public DateTime? EndedUtc { get; internal set; }
    public bool Started => StartedUtc != null;

    public volatile bool Completed;
    public volatile bool Cancelled;
    public string? EndReason { get; internal set; }

    internal readonly ConcurrentQueue<float[]> Chunks = new();
    internal float[]? Current;
    internal int CurrentPos;
    internal DateTime LastDataUtc;
    internal LinearResampler? Resampler;
    internal long QueuedFrames;

    /// <summary>已经交给播放器的全部源音频（用于本地重播）。</summary>
    public List<float[]> History { get; } = new();
}

/// <summary>
/// 按顺序播放译音的队列。每句话一个 slot，严格按创建顺序播放（后面的句子即使先合成好也要等前一句）。
/// 取消通过 epoch 实现：小于当前 epoch 的 slot 一律丢弃，迟到的音频也会被丢弃。
/// </summary>
public sealed class SpeechQueuePlayer : ISampleProvider
{
    private readonly object _lock = new();
    private readonly List<SpeechSlot> _slots = new();
    private readonly int _sourceRate;
    private volatile bool _active;

    public WaveFormat WaveFormat { get; }
    public float Volume { get; set; } = 1f;
    public volatile bool Paused;
    public double MaxSlotAgeSeconds { get; set; } = 10;
    public double StallTimeoutSeconds { get; set; } = 8;
    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    /// <summary>为真时同步触发事件（测试用）；否则投递到线程池，避免阻塞音频线程。</summary>
    public bool SynchronousEvents { get; set; }

    public event Action<SpeechSlot>? SlotStarted;
    public event Action<SpeechSlot>? SlotEnded;

    public SpeechQueuePlayer(int deviceRate, int deviceChannels, int sourceRate)
    {
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(deviceRate, deviceChannels);
        _sourceRate = sourceRate;
    }

    /// <summary>当前是否正在输出译音（用于压低原声）。</summary>
    public bool IsActive => _active;

    public int PendingCount { get { lock (_lock) return _slots.Count; } }

    public double BacklogSeconds
    {
        get
        {
            lock (_lock)
                return _slots.Sum(s => Interlocked.Read(ref s.QueuedFrames)) / (double)WaveFormat.SampleRate;
        }
    }

    public SpeechSlot Enqueue(long id, int epoch, string text = "", float pan = 0)
    {
        var slot = new SpeechSlot { Id = id, Epoch = epoch, Text = text, Pan = pan, CreatedUtc = Clock() };
        slot.LastDataUtc = slot.CreatedUtc;
        lock (_lock) _slots.Add(slot);
        return slot;
    }

    /// <summary>追加源采样率的单声道音频（任意线程）。已取消的 slot 直接丢弃。</summary>
    public void Append(SpeechSlot slot, ReadOnlySpan<float> monoSource)
    {
        if (slot.Cancelled || monoSource.Length == 0) return;
        slot.History.Add(monoSource.ToArray());
        slot.Resampler ??= new LinearResampler(_sourceRate, WaveFormat.SampleRate, 1);
        var data = slot.Resampler.Process(monoSource);
        if (data.Length == 0) return;
        Interlocked.Add(ref slot.QueuedFrames, data.Length);
        slot.Chunks.Enqueue(data);
    }

    public void Complete(SpeechSlot slot) => slot.Completed = true;

    public void Cancel(SpeechSlot slot, string reason)
    {
        if (slot.Cancelled) return;
        slot.EndReason ??= reason;
        slot.Cancelled = true;
    }

    /// <summary>取消所有 epoch 小于给定值的 slot（包括正在播放的那一句）。</summary>
    public void CancelBelowEpoch(int epoch, string reason)
    {
        lock (_lock)
            foreach (var s in _slots.Where(s => s.Epoch < epoch)) Cancel(s, reason);
    }

    public void CancelAll(string reason)
    {
        lock (_lock)
            foreach (var s in _slots) Cancel(s, reason);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        Array.Clear(buffer, offset, count);
        if (Paused) { _active = false; return count; }

        int ch = WaveFormat.Channels;
        int frames = count / ch;
        int written = 0;
        bool active = false;
        float vol = Volume;
        List<SpeechSlot>? started = null, ended = null;

        lock (_lock)
        {
            while (written < frames && _slots.Count > 0)
            {
                var s = _slots[0];
                var now = Clock();

                if (s.Cancelled)
                {
                    End(s, s.EndReason ?? "cancelled", now, ref ended);
                    continue;
                }
                if (!s.Started && (now - s.CreatedUtc).TotalSeconds > MaxSlotAgeSeconds)
                {
                    s.Cancelled = true;
                    End(s, "stale", now, ref ended);
                    continue;
                }
                if (s.Current == null || s.CurrentPos >= s.Current.Length)
                {
                    if (s.Chunks.TryDequeue(out var chunk))
                    {
                        s.Current = chunk;
                        s.CurrentPos = 0;
                        s.LastDataUtc = now;
                    }
                    else if (s.Completed)
                    {
                        End(s, "done", now, ref ended);
                        continue;
                    }
                    else
                    {
                        if (s.Started && (now - s.LastDataUtc).TotalSeconds > StallTimeoutSeconds)
                        {
                            s.Cancelled = true;
                            End(s, "stalled", now, ref ended);
                            continue;
                        }
                        break; // 等待当前句的数据，保持顺序
                    }
                }

                if (!s.Started)
                {
                    s.StartedUtc = now;
                    (started ??= new()).Add(s);
                }

                int n = Math.Min(frames - written, s.Current.Length - s.CurrentPos);
                float gl = Math.Min(1f, 1f - s.Pan), gr = Math.Min(1f, 1f + s.Pan);
                for (int i = 0; i < n; i++)
                {
                    float v = s.Current[s.CurrentPos + i] * vol;
                    int o = offset + (written + i) * ch;
                    if (ch == 1) buffer[o] = v;
                    else { buffer[o] = v * gl; buffer[o + 1] = v * gr; }
                }
                s.CurrentPos += n;
                written += n;
                Interlocked.Add(ref s.QueuedFrames, -n);
                active = true;
            }
        }

        _active = active;
        if (started != null) foreach (var s in started) Raise(SlotStarted, s);
        if (ended != null) foreach (var s in ended) Raise(SlotEnded, s);
        return count;
    }

    private void End(SpeechSlot s, string reason, DateTime now, ref List<SpeechSlot>? ended)
    {
        _slots.RemoveAt(0);
        s.EndReason ??= reason;
        s.EndedUtc = now;
        (ended ??= new()).Add(s);
    }

    private void Raise(Action<SpeechSlot>? handler, SpeechSlot s)
    {
        if (handler == null) return;
        if (SynchronousEvents) handler(s);
        else ThreadPool.QueueUserWorkItem(_ => handler(s));
    }
}
