using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using GVT.Core.Pipeline;

namespace GVT.App.Models;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public enum ItemState { Listening, Translating, Done, Skipped, Error, System }

/// <summary>
/// 对话里的一句话。同一句的“识别中 → 原文 → 译文”在同一张卡片里原地更新，
/// 不再拆成好几行“接收/收到”。
/// </summary>
public sealed class TimelineItem : Observable
{
    private string _source = "";
    private string _translation = "";
    private string _note = "";
    private ItemState _state;
    private double _opacity = 1;

    public TimelineItem(string channel, long id)
    {
        Channel = channel;
        Id = id;
        Time = DateTime.Now;
    }

    public string Channel { get; }
    public long Id { get; }
    public DateTime Time { get; }

    public bool IsMe => Channel == "发送";
    public bool IsMate => Channel == "接收";
    public bool IsSystem => !IsMe && !IsMate;

    public string Who => IsMe ? "我" : IsMate ? "队友" : "系统";
    public string Route => IsMe ? "中 → EN" : IsMate ? "EN → 中" : "";
    public string TimeText => Time.ToString("HH:mm:ss");

    public string Source { get => _source; set { if (Set(ref _source, value)) RaiseTexts(); } }
    public string Translation { get => _translation; set { if (Set(ref _translation, value)) RaiseTexts(); } }
    public string Note { get => _note; set { if (Set(ref _note, value)) RaiseTexts(); } }
    public ItemState State { get => _state; set { if (Set(ref _state, value)) RaiseTexts(); } }

    private float[]? _audio;
    private bool _isPlaying;

    /// <summary>这句话的合成语音（我：队友听到的英文；队友：我听到的中文），用于在本机重听。</summary>
    public float[]? Audio { get => _audio; set { _audio = value; Raise(); Raise(nameof(HasAudio)); } }
    public int AudioRate { get; set; } = 48000;
    public bool HasAudio => _audio is { Length: > 0 };
    public bool IsPlaying { get => _isPlaying; set => Set(ref _isPlaying, value); }
    public string PlayTip => IsMe ? "在耳机里听队友听到的英文（不会再发出去）" : "在耳机里重听这句中文";

    /// <summary>浮窗淡出用。</summary>
    public double Opacity { get => _opacity; set => Set(ref _opacity, value); }

    /// <summary>大字：有译文显示译文（这才是对方听到 / 我听到的内容），否则显示正在识别的原文。</summary>
    public string Primary => !string.IsNullOrEmpty(Translation) ? Translation
        : IsSystem ? Note
        : !string.IsNullOrEmpty(Source) ? Source : "…";

    /// <summary>小字：原文。</summary>
    public string Secondary => !string.IsNullOrEmpty(Translation) ? Source : "";

    public bool HasSecondary => !string.IsNullOrEmpty(Secondary);

    public string StateText => State switch
    {
        ItemState.Listening => "正在听",
        ItemState.Translating => "翻译中",
        ItemState.Done => IsMe ? "已发给队友" : "已播报",
        ItemState.Skipped => string.IsNullOrEmpty(Note) ? "已跳过" : Note,
        ItemState.Error => string.IsNullOrEmpty(Note) ? "失败" : Note,
        _ => "",
    };

    public bool IsBusy => State is ItemState.Listening or ItemState.Translating;
    public bool IsFaded => State is ItemState.Skipped;
    public bool IsError => State is ItemState.Error;

    private void RaiseTexts()
    {
        Raise(nameof(Primary));
        Raise(nameof(Secondary));
        Raise(nameof(HasSecondary));
        Raise(nameof(StateText));
        Raise(nameof(IsBusy));
        Raise(nameof(IsFaded));
        Raise(nameof(IsError));
    }
}

/// <summary>把引擎的字幕事件整理成按句子组织的对话列表（主界面）和最近几句（浮窗）。必须在 UI 线程调用。</summary>
public sealed class TimelineStore
{
    private readonly Dictionary<(string, long), TimelineItem> _index = new();

    public ObservableCollection<TimelineItem> Items { get; } = new();
    public ObservableCollection<TimelineItem> Recent { get; } = new();
    public int RecentMax { get; set; } = 4;
    public int MaxItems { get; set; } = 300;

    public event Action<TimelineItem>? Added;

    public void Apply(SubtitleEvent e)
    {
        if (e.UtteranceId <= 0 || e.Channel is not ("发送" or "接收"))
        {
            // 系统提示 / 取消提示 / 重播提示
            if (e.Kind == SubtitleKind.Info && e.Text.StartsWith("已取消")) return; // 取消由对应卡片状态体现
            var sys = new TimelineItem(e.Channel is "发送" or "接收" ? e.Channel + "·提示" : "系统", 0)
            {
                Note = e.Text,
                State = e.Kind == SubtitleKind.Error ? ItemState.Error : ItemState.System,
            };
            Add(sys);
            return;
        }

        var key = (e.Channel, e.UtteranceId);
        if (!_index.TryGetValue(key, out var item))
        {
            item = new TimelineItem(e.Channel, e.UtteranceId);
            _index[key] = item;
            Add(item);
        }

        switch (e.Kind)
        {
            case SubtitleKind.Partial:
                if (item.State is ItemState.Listening) item.Source = e.Text;
                break;
            case SubtitleKind.Source:
                item.Source = e.Text;
                if (item.State == ItemState.Listening) item.State = ItemState.Translating;
                break;
            case SubtitleKind.Translation:
                item.Translation = e.Text;
                item.State = ItemState.Done;
                break;
            case SubtitleKind.Dropped:
            case SubtitleKind.Info:
                item.Note = e.Text;
                item.State = ItemState.Skipped;
                break;
            case SubtitleKind.Error:
                item.Note = e.Text;
                item.State = ItemState.Error;
                break;
        }
    }

    /// <summary>只给最近这么多句保留音频，控制内存（每句约 0.5–1 MB）。</summary>
    public int MaxAudioItems { get; set; } = 60;

    public void AttachAudio(string channel, long id, float[] audio, int rate)
    {
        if (!_index.TryGetValue((channel, id), out var item)) return;
        item.AudioRate = rate;
        item.Audio = audio;
        var withAudio = Items.Where(i => i.HasAudio).ToList();
        foreach (var old in withAudio.Take(Math.Max(0, withAudio.Count - MaxAudioItems))) old.Audio = null;
    }

    /// <summary>识别结果为空等被静默丢弃的句子：卡片停在“正在听”，需要清理。</summary>
    public void Complete(string channel, long id, string outcome)
    {
        if (!_index.TryGetValue((channel, id), out var item)) return;
        if (item.State is ItemState.Listening or ItemState.Translating)
        {
            if (outcome is "empty" or "empty-translation") { Remove(item); return; }
            if (outcome is "cancelled" or "取消本句" or "停止" or "通道已关闭") { item.Note = "已取消"; item.State = ItemState.Skipped; }
        }
    }

    public void Clear()
    {
        Items.Clear();
        Recent.Clear();
        _index.Clear();
    }

    public void Remove(TimelineItem item)
    {
        Items.Remove(item);
        Recent.Remove(item);
        _index.Remove((item.Channel, item.Id));
    }

    private void Add(TimelineItem item)
    {
        Items.Add(item);
        while (Items.Count > MaxItems)
        {
            var old = Items[0];
            Items.RemoveAt(0);
            _index.Remove((old.Channel, old.Id));
        }
        Recent.Add(item);
        while (Recent.Count > RecentMax) Recent.RemoveAt(0);
        Added?.Invoke(item);
    }
}
