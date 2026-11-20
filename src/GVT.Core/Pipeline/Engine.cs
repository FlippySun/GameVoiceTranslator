using GVT.Core.Aliyun;
using GVT.Core.Audio;
using GVT.Core.Config;

namespace GVT.Core.Pipeline;

public sealed record EngineStatus(
    bool Running,
    bool SendEnabled, ChannelStage SendStage, bool PttDown, float MicDb, double SendBacklog,
    bool ReceiveEnabled, ChannelStage ReceiveStage, float SourceDb, double ReceiveBacklog,
    bool HoldOriginal, string Usage, string? Warning);

/// <summary>
/// 把设备、两条翻译通道、混音和快捷键动作组装在一起。
///
/// 发送：真实麦克风 → (按键/VAD) → 中文识别 → 英文 → 英文合成 → 虚拟线 A（游戏里选它当麦克风）
/// 接收：虚拟线 B（游戏语音输出） → VAD → 英文识别 → 中文 → 中文合成 ┐
///                              └→ 原声（可压低 / 临时放大） ──────────┴→ 耳机
/// 发送侧故障时永远保持静音，不会把中文原声放出去。
/// </summary>
public sealed class Engine : IDisposable
{
    private readonly AppConfig _cfg;
    private readonly ISpeechServices _services;
    private readonly Glossary _glossary;
    private readonly string _baseDir;
    private readonly object _pttLock = new();
    private readonly List<IDisposable> _disposables = new();
    private readonly UsageMeter _usage;
    private readonly EchoGuard _echo;
    private readonly LatencyCsv _csv = new();

    private CaptureSource? _mic, _source;
    private OutputMixer? _sendOut, _hpOut;
    private MonoDownmixer? _micDown, _srcDown;
    private PreRollBuffer? _preRoll;
    private VadSegmenter? _sendVad, _recvVad;
    private PassthroughProvider? _passthrough;
    private Timer? _duckTimer;
    private volatile bool _pttDown;
    private volatile bool _holdOriginal;
    private float _lastPan;
    private string? _warning;

    public TranslationChannel? Send { get; private set; }
    public TranslationChannel? Receive { get; private set; }
    public bool Running { get; private set; }
    public UsageMeter Usage => _usage;
    public string LatencyCsvPath => _csv.FilePath;

    public event Action<SubtitleEvent>? Subtitle;
    public event Action<LatencyRecord>? Latency;
    public event Action? StatusChanged;

    public Engine(AppConfig cfg, ISpeechServices services, Glossary glossary, string baseDir)
    {
        _cfg = cfg;
        _services = services;
        _glossary = glossary;
        _baseDir = baseDir;
        _usage = new UsageMeter(cfg.Budget);
        _echo = new EchoGuard(cfg.Receive.EchoSimilarityThreshold, cfg.Receive.EchoWindowSeconds);
        _usage.Changed += OnUsageChanged;
    }

    public void Start()
    {
        if (Running) return;
        try
        {
            StartSend();
            StartReceive();
            _duckTimer = new Timer(_ => UpdateDucking(), null, 0, 20);
            Running = true;
            Log.Info($"已启动。延迟记录：{_csv.FilePath}");
        }
        catch
        {
            Stop();
            throw;
        }
        StatusChanged?.Invoke();
    }

    private void StartSend()
    {
        var c = _cfg.Send;
        var micDev = DeviceCatalog.FindCapture(c.MicDevice);
        var outDev = DeviceCatalog.FindRender(c.OutputDevice);
        if (DeviceCatalog.LooksVirtualCable(micDev.FriendlyName))
            Warn($"“我的麦克风”是虚拟设备（{micDev.FriendlyName}），请确认这不是游戏用的那条虚拟线，否则会回环。");
        if (!DeviceCatalog.LooksVirtualCable(outDev.FriendlyName))
            Warn($"译音输出设备“{outDev.FriendlyName}”不像虚拟线。英文译音会直接从这个设备放出来。");

        _sendOut = Track(new OutputMixer(outDev));
        var player = new SpeechQueuePlayer(_sendOut.SampleRate, _sendOut.Channels, _services.TtsSampleRate)
        {
            Volume = c.OutputVolume,
            MaxSlotAgeSeconds = _cfg.Budget.MaxSlotAgeSeconds,
        };
        _sendOut.AddInput(player);
        _sendOut.Stopped += ex => DeviceLost("译音输出设备", ex, isSend: true);

        Send = new TranslationChannel(new ChannelOptions
        {
            Name = "发送",
            Direction = Direction.ZhToEn,
            AsrLanguageHint = c.AsrLanguageHint,
            AsrContext = _glossary.AsrContextFor(Direction.ZhToEn),
            Voice = c.Voice,
            TtsRate = c.TtsRate,
            TtsLanguageHint = c.TtsLanguageHint,
            LeadInMs = c.LeadInMs,
            LeadInLevel = c.LeadInLevel,
        }, _services, player, _usage) { Enabled = c.Enabled };
        Wire(Send);
        Send.Translated += _echo.AddOutgoing;

        _mic = Track(new CaptureSource(micDev));
        _micDown = new MonoDownmixer(_mic.SampleRate, _mic.Channels);
        _preRoll = new PreRollBuffer(16 * c.PreRollMs);
        if (c.Gate == GateMode.VoiceActivity)
        {
            var vad = Track(VoiceDetectorFactory.Create(_cfg.Vad, _baseDir, Log.Warn));
            _sendVad = new VadSegmenter(vad, _cfg.Vad);
            _sendVad.SpeechStart += pre => Send.BeginUtterance(pre);
            _sendVad.SpeechAudio += a => Send.PushAudio(a);
            _sendVad.SpeechEnd += () => Send.EndUtterance();
            Log.Info($"发送侧使用语音激活（{vad.Name}）");
        }
        _mic.Data += OnMicData;
        _mic.Stopped += ex => DeviceLost("我的麦克风", ex, isSend: true);

        _sendOut.Start();
        _mic.Start();
        Log.Info($"发送：{micDev.FriendlyName} → {outDev.FriendlyName}（{_sendOut.SampleRate} Hz/{_sendOut.Channels}ch）");
    }

    private void StartReceive()
    {
        var c = _cfg.Receive;
        var srcDev = DeviceCatalog.FindCapture(c.SourceDevice);
        var hpDev = DeviceCatalog.FindRender(c.HeadphoneDevice);
        if (DeviceCatalog.LooksVirtualCable(hpDev.FriendlyName))
            throw new InvalidOperationException($"“我的耳机”解析成了虚拟设备 {hpDev.FriendlyName}。请在设置里指定真实耳机，否则会产生回环。");
        if (string.Equals(srcDev.FriendlyName, _mic?.DeviceName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("“队友声音来源”和“我的麦克风”是同一个设备，会把自己的话当成队友的话翻译。");

        _hpOut = Track(new OutputMixer(hpDev));
        _passthrough = new PassthroughProvider(_hpOut.SampleRate, _hpOut.Channels) { TargetGain = OriginalGain(false) };
        var player = new SpeechQueuePlayer(_hpOut.SampleRate, _hpOut.Channels, _services.TtsSampleRate)
        {
            Volume = c.TranslationVolume,
            MaxSlotAgeSeconds = _cfg.Budget.MaxSlotAgeSeconds,
        };
        _hpOut.AddInput(_passthrough);
        _hpOut.AddInput(player);
        _hpOut.Stopped += ex => DeviceLost("我的耳机", ex, isSend: false);

        Receive = new TranslationChannel(new ChannelOptions
        {
            Name = "接收",
            Direction = Direction.EnToZh,
            AsrLanguageHint = c.AsrLanguageHint,
            AsrContext = _glossary.AsrContextFor(Direction.EnToZh),
            Voice = c.Voice,
            TtsRate = c.TtsRate,
            TtsLanguageHint = c.TtsLanguageHint,
            FollowPan = c.FollowSourcePan,
            SkipIfAlreadyTarget = true,
        }, _services, player, _usage) { Enabled = c.Enabled };
        Wire(Receive);
        Receive.ShouldDropSource = _echo.IsEcho;

        var vad = Track(VoiceDetectorFactory.Create(_cfg.Vad, _baseDir, Log.Warn));
        _recvVad = new VadSegmenter(vad, _cfg.Vad);
        _recvVad.SpeechStart += pre => Receive.BeginUtterance(pre);
        _recvVad.SpeechAudio += a => Receive.PushAudio(a, _lastPan);
        _recvVad.SpeechEnd += () => Receive.EndUtterance();

        _source = Track(new CaptureSource(srcDev));
        _srcDown = new MonoDownmixer(_source.SampleRate, _source.Channels);
        _source.Data += OnSourceData;
        _source.Stopped += ex => DeviceLost("队友声音来源", ex, isSend: false);

        _hpOut.Start();
        _source.Start();
        Log.Info($"接收：{srcDev.FriendlyName} → {hpDev.FriendlyName}（VAD：{vad.Name}）");
    }

    private void Wire(TranslationChannel ch)
    {
        ch.Subtitle += e => Subtitle?.Invoke(e);
        ch.Completed += r => { _csv.Append(r); Latency?.Invoke(r); };
        ch.StageChanged += _ => StatusChanged?.Invoke();
    }

    // ---------- 音频回调（采集线程） ----------

    private void OnMicData(float[] samples)
    {
        var m = _micDown!.Process(samples, out _);
        if (m.Length == 0) return;
        if (_sendVad != null) { _sendVad.Push(m); return; }
        lock (_pttLock)
        {
            if (_pttDown) Send?.PushAudio(m);
            else _preRoll!.Push(m);
        }
    }

    private void OnSourceData(float[] samples)
    {
        _passthrough?.Write(samples, _source!.Channels, _source.SampleRate);
        var m = _srcDown!.Process(samples, out var pan);
        _lastPan = pan;
        if (m.Length > 0 && Receive?.Enabled == true) _recvVad!.Push(m);
    }

    // ---------- 快捷键动作 ----------

    public void PushToTalk(bool down)
    {
        if (Send == null || _sendVad != null) return;
        lock (_pttLock)
        {
            if (down == _pttDown) return;
            _pttDown = down;
            if (down) Send.BeginUtterance(_preRoll!.Drain());
            else Send.EndUtterance();
        }
        StatusChanged?.Invoke();
    }

    /// <summary>取消本句：丢弃正在录的和尚未发完的英文译音。已经播出去的无法撤回。</summary>
    public void CancelSentence()
    {
        lock (_pttLock) _pttDown = false;
        _sendVad?.ForceEnd();
        Send?.Cancel("取消本句");
    }

    /// <summary>停止发送 / 恢复发送。停止时立即静音虚拟麦克风并丢弃所有待发内容。</summary>
    public void ToggleSend()
    {
        if (Send == null) return;
        lock (_pttLock) _pttDown = false;
        Send.Enabled = !Send.Enabled;
        Log.Info(Send.Enabled ? "发送已恢复" : "发送已停止（虚拟麦克风静音）");
        StatusChanged?.Invoke();
    }

    public void ToggleReceive()
    {
        if (Receive == null) return;
        Receive.Enabled = !Receive.Enabled;
        _recvVad?.ForceEnd();
        Log.Info(Receive.Enabled ? "中文播报已恢复" : "中文播报已停止（原声照常）");
        StatusChanged?.Invoke();
    }

    /// <summary>按住时暂停中文播报、原声放大；松开恢复。</summary>
    public void HoldOriginal(bool down)
    {
        _holdOriginal = down;
        if (Receive != null) Receive.Player.Paused = down;
        StatusChanged?.Invoke();
    }

    /// <summary>在自己耳机里重播上一句中文（不会发给队友）。</summary>
    public void ReplayLast()
    {
        var last = Receive?.LastPlayed;
        if (Receive == null || last == null) return;
        var slot = Receive.Player.Enqueue(-1, Receive.Epoch, last.Value.Text);
        foreach (var chunk in last.Value.Audio.ToList()) Receive.Player.Append(slot, chunk);
        Receive.Player.Complete(slot);
        Subtitle?.Invoke(new SubtitleEvent("接收", -1, SubtitleKind.Info, "重播：" + last.Value.Text, DateTime.Now));
    }

    public void SetListenMode(ReceiveListenMode mode)
    {
        _cfg.Receive.ListenMode = mode;
        StatusChanged?.Invoke();
    }

    private float OriginalGain(bool ttsActive)
    {
        var c = _cfg.Receive;
        if (_holdOriginal || Receive is { Enabled: false }) return 1f;
        return c.ListenMode switch
        {
            ReceiveListenMode.Both => ttsActive ? c.OriginalGainBothDucked : c.OriginalGainBoth,
            _ => ttsActive ? c.OriginalGainTranslationFirstDucked : c.OriginalGainTranslationFirst,
        };
    }

    private void UpdateDucking()
    {
        if (_passthrough == null || Receive == null) return;
        _passthrough.TargetGain = OriginalGain(Receive.Player.IsActive);
    }

    // ---------- 状态与异常 ----------

    public EngineStatus GetStatus() => new(
        Running,
        Send?.Enabled ?? false, Send?.Stage ?? ChannelStage.Off, _pttDown, _mic?.LastPeakDb ?? -120, Send?.Player.BacklogSeconds ?? 0,
        Receive?.Enabled ?? false, Receive?.Stage ?? ChannelStage.Off, _source?.LastPeakDb ?? -120, Receive?.Player.BacklogSeconds ?? 0,
        _holdOriginal, _usage.ToString(), _warning);

    private void OnUsageChanged()
    {
        if (!_usage.OverBudget) return;
        if (Send?.Enabled == true || Receive?.Enabled == true)
        {
            if (Send != null) Send.Enabled = false;
            if (Receive != null) Receive.Enabled = false;
            Warn($"已达到本次预算 ¥{_usage.BudgetYuan:F2}，两个方向都已暂停。可在设置里调高预算后重启。");
        }
    }

    private void DeviceLost(string what, Exception? ex, bool isSend)
    {
        if (!Running) return;
        if (isSend && Send != null) Send.Enabled = false;
        Warn($"{what}已停止工作{(ex != null ? $"：{ex.Message}" : "")}。" +
             (isSend ? "发送已暂停，不会改用其他设备。" : "中文播报已中断；如果原声也听不到，请把游戏语音输出改回耳机。"));
    }

    private void Warn(string msg)
    {
        _warning = msg;
        Log.Warn(msg);
        Subtitle?.Invoke(new SubtitleEvent("系统", 0, SubtitleKind.Error, msg, DateTime.Now));
        StatusChanged?.Invoke();
    }

    private T Track<T>(T d) where T : IDisposable { _disposables.Add(d); return d; }

    public void Stop()
    {
        Running = false;
        _duckTimer?.Dispose();
        _duckTimer = null;
        try { Send?.Cancel("停止"); } catch { }
        try { Receive?.Cancel("停止"); } catch { }
        foreach (var d in Enumerable.Reverse(_disposables))
        {
            try { d.Dispose(); } catch (Exception ex) { Log.Warn("释放失败：" + ex.Message); }
        }
        _disposables.Clear();
        Send = null;
        Receive = null;
        _mic = _source = null;
        StatusChanged?.Invoke();
    }

    public void Dispose() => Stop();
}
