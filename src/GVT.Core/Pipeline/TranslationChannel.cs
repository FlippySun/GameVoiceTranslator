using System.Collections.Concurrent;
using System.Diagnostics;
using GVT.Core.Aliyun;
using GVT.Core.Audio;
using GVT.Core.Config;

namespace GVT.Core.Pipeline;

public sealed class ChannelOptions
{
    public string Name { get; init; } = "";
    public Direction Direction { get; init; }
    public string? AsrLanguageHint { get; init; }
    public string? AsrContext { get; init; }
    /// <summary>可在运行中替换（下一句生效）。</summary>
    public TtsVoice Tts { get; set; } = new("", "", 1.0, null, null);
    public int LeadInMs { get; init; }
    public float LeadInLevel { get; init; } = 0.01f;
    public bool FollowPan { get; init; }
    /// <summary>识别结果已经是目标语言（例如队友里有人说中文）时，只显示字幕、不播报。</summary>
    public bool SkipIfAlreadyTarget { get; init; }
    /// <summary>只是附和的语气词（yeah/okay/yep…）和不成句的单个词只显示字幕、不播报。</summary>
    public bool FilterBackchannel { get; init; }
    /// <summary>翻译模型自评“没把握”时，播报前加的前缀（如“可能是，”）。空 = 不加。</summary>
    public string LowConfidencePrefix { get; init; } = "";
    /// <summary>非空时把每句识别用的音频和识别结果保存到这个目录（调试用）。</summary>
    public string? SaveAudioDir { get; init; }
}

public enum ChannelStage { Off, Idle, Listening, Processing, Speaking, Error }

/// <summary>
/// 单个翻译方向：一句话 = 识别 → 翻译 → 合成 → 按顺序播放。
/// 取消靠 epoch：Cancel() 让 epoch 加一，之前所有句子的迟到结果都会在每一步被丢弃，播放队列里的也会被移除。
/// </summary>
public sealed class TranslationChannel
{
    private readonly ChannelOptions _opt;
    private readonly ISpeechServices _services;
    private readonly SpeechQueuePlayer _player;
    private readonly UsageMeter _usage;
    private readonly object _lock = new();
    private readonly ConcurrentDictionary<long, (Utterance U, LatencyRecord R)> _inFlight = new();
    private readonly List<string> _recentSource = new();
    private int _epoch;
    private CancellationTokenSource _epochCts = new();
    private Utterance? _current;
    private long _nextId;
    private volatile bool _enabled = true;
    private volatile ChannelStage _stage = ChannelStage.Idle;
    private int _processing;

    public string Name => _opt.Name;
    public int Epoch => Volatile.Read(ref _epoch);
    public ChannelStage Stage => _enabled ? _stage : ChannelStage.Off;
    public bool IsCapturing { get { lock (_lock) return _current != null; } }
    public ChannelOptions Options => _opt;
    public SpeechQueuePlayer Player => _player;

    /// <summary>最近一句成功播放的译音（本地重播用）。</summary>
    public (string Text, List<float[]> Audio)? LastPlayed { get; private set; }

    public Func<string, bool>? ShouldDropSource { get; set; }
    public event Action<string>? Translated;
    public event Action<SubtitleEvent>? Subtitle;
    public event Action<LatencyRecord>? Completed;
    public event Action<ChannelStage>? StageChanged;
    /// <summary>一句话的合成语音完整播完（参数：句子 id、TtsSampleRate 下的单声道音频）。用于在界面上重听。</summary>
    public event Action<long, float[]>? AudioReady;

    public TranslationChannel(ChannelOptions opt, ISpeechServices services, SpeechQueuePlayer player, UsageMeter usage)
    {
        _opt = opt;
        _services = services;
        _player = player;
        _usage = usage;
        _player.SlotStarted += OnSlotStarted;
        _player.SlotEnded += OnSlotEnded;
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            if (!value) Cancel("通道已关闭");
            SetStage(value ? ChannelStage.Idle : ChannelStage.Off);
        }
    }

    // ---------- 采集侧 ----------

    public void BeginUtterance(ReadOnlySpan<float> preRoll16k)
    {
        if (!_enabled) return;
        Utterance u;
        lock (_lock)
        {
            if (_current != null) EndUtteranceLocked();
            u = new Utterance(Interlocked.Increment(ref _nextId), Epoch, _epochCts.Token);
            if (_opt.SaveAudioDir != null) u.Recorded = new List<float>();
            try
            {
                u.Asr = _services.StartAsr(_opt.AsrLanguageHint, _opt.AsrContext);
            }
            catch (Exception ex)
            {
                Emit(u.Id, SubtitleKind.Error, "无法开始识别：" + ex.Message);
                return;
            }
            u.Asr.Partial += t => Emit(u.Id, SubtitleKind.Partial, t);
            u.Asr.Push(preRoll16k);
            u.Recorded?.AddRange(preRoll16k.ToArray());
            _current = u;
        }
        SetStage(ChannelStage.Listening);
    }

    public void PushAudio(ReadOnlySpan<float> samples16k, float pan = 0)
    {
        lock (_lock)
        {
            var u = _current;
            if (u == null) return;
            u.Asr?.Push(samples16k);
            u.Recorded?.AddRange(samples16k.ToArray());
            float energy = 0;
            foreach (var s in samples16k) energy += s * s;
            u.PanSum += pan * energy;
            u.EnergySum += energy;
        }
    }

    public void EndUtterance()
    {
        lock (_lock) EndUtteranceLocked();
    }

    private void EndUtteranceLocked()
    {
        var u = _current;
        _current = null;
        if (u == null) return;
        u.EndTicks = Stopwatch.GetTimestamp();
        // 在句末就占好播放顺序，后面的句子即使先翻译完也要排在它后面
        u.Slot = _player.Enqueue(u.Id, u.Epoch);
        var rec = new LatencyRecord { Channel = _opt.Name, Id = u.Id };
        _inFlight[u.Id] = (u, rec);
        Interlocked.Increment(ref _processing);
        SetStage(ChannelStage.Processing);
        _ = Task.Run(() => ProcessAsync(u, rec));
    }

    /// <summary>
    /// 取消：正在录的这句不再识别，所有未播完的句子（包括正在播的）立刻停止，迟到结果全部丢弃。
    /// </summary>
    public void Cancel(string reason)
    {
        CancellationTokenSource old;
        Utterance? cur;
        lock (_lock)
        {
            Interlocked.Increment(ref _epoch);
            old = _epochCts;
            _epochCts = new CancellationTokenSource();
            cur = _current;
            _current = null;
        }
        cur?.Asr?.Abort();
        if (cur?.Asr != null) _ = cur.Asr.DisposeAsync().AsTask();
        _player.CancelBelowEpoch(Epoch, reason);
        // 不 Dispose：在途任务可能还会对它的 Token 注册回调
        try { old.Cancel(); } catch { }
        Emit(0, SubtitleKind.Info, $"已取消（{reason}）");
        SetStage(_enabled ? ChannelStage.Idle : ChannelStage.Off);
    }

    // ---------- 处理侧 ----------

    private bool IsStale(Utterance u) => u.Epoch != Epoch || u.Token.IsCancellationRequested;

    private double MsSinceEnd(Utterance u) => Stopwatch.GetElapsedTime(u.EndTicks).TotalMilliseconds;

    private async Task ProcessAsync(Utterance u, LatencyRecord rec)
    {
        var slot = u.Slot!;
        ITtsSession? tts = null;
        try
        {
            // 合成连接与识别收尾、翻译并行预热
            tts = _services.PrepareTts(_opt.Tts);

            var asr = u.Asr ?? throw new InvalidOperationException("没有识别会话");
            string source;
            try { source = (await asr.FinishAsync(u.Token).ConfigureAwait(false)).Trim(); }
            finally
            {
                rec.AudioSeconds = asr.AudioSeconds;
                _usage.AddAsr(asr.AudioSeconds);
                _ = asr.DisposeAsync().AsTask();
            }
            rec.EndToAsrMs = MsSinceEnd(u);
            rec.Source = source;
            if (u.Recorded != null) SaveRecording(u, source);
            if (IsStale(u)) { Drop(slot, rec, "cancelled"); return; }

            if (IsNoise(source)) { Drop(slot, rec, "empty"); return; }
            Emit(u.Id, SubtitleKind.Source, source);

            if (ShouldDropSource?.Invoke(source) == true)
            {
                Emit(u.Id, SubtitleKind.Dropped, "疑似回声（与刚发出的译音相同），已忽略");
                Drop(slot, rec, "echo");
                return;
            }
            if (_opt.SkipIfAlreadyTarget && IsAlreadyTarget(source))
            {
                Emit(u.Id, SubtitleKind.Info, _opt.Direction == Direction.EnToZh ? "对方说的是中文，不播报" : "已是英文，不翻译");
                Drop(slot, rec, "already-target");
                return;
            }
            if (_opt.FilterBackchannel && IsBackchannel(source))
            {
                Emit(u.Id, SubtitleKind.Info, "语气词，不播报");
                Drop(slot, rec, "backchannel");
                return;
            }

            var context = RecentContext(source);
            var tr = await _services.Translator.TranslateAsync(source, _opt.Direction, context, u.Token).ConfigureAwait(false);
            _usage.AddMt(tr.InputTokens, tr.OutputTokens);
            rec.EndToMtMs = MsSinceEnd(u);
            rec.Translation = tr.Text;
            if (IsStale(u)) { Drop(slot, rec, "cancelled"); return; }
            if (tr.Confidence == TranslationConfidence.Garbled)
            {
                Emit(u.Id, SubtitleKind.Dropped, _opt.Direction == Direction.EnToZh ? "没听清，未播报" : "没听清，未发送，请再说一遍");
                Drop(slot, rec, "garbled");
                return;
            }
            if (string.IsNullOrWhiteSpace(tr.Text)) { Drop(slot, rec, "empty-translation"); return; }
            var spoken = tr.Confidence == TranslationConfidence.Low && !string.IsNullOrEmpty(_opt.LowConfidencePrefix)
                ? _opt.LowConfidencePrefix + tr.Text : tr.Text;

            slot.Text = spoken;
            if (_opt.FollowPan && u.EnergySum > 1e-6f) slot.Pan = Math.Clamp(u.PanSum / u.EnergySum, -0.8f, 0.8f);
            Emit(u.Id, SubtitleKind.Translation, spoken);
            Translated?.Invoke(tr.Text);

            if (_opt.LeadInMs > 0) _player.Append(slot, LeadIn(_services.TtsSampleRate));

            bool first = true;
            int chars = await tts.SpeakAsync(spoken, pcm =>
            {
                if (IsStale(u)) return;
                if (first) { first = false; rec.EndToTtsFirstMs = MsSinceEnd(u); }
                _player.Append(slot, pcm);
            }, u.Token).ConfigureAwait(false);
            _usage.AddTts(chars > 0 ? chars : TextUtil.TtsChars(spoken));

            if (IsStale(u)) { Drop(slot, rec, "cancelled"); return; }
            _player.Complete(slot);
        }
        catch (OperationCanceledException)
        {
            Drop(slot, rec, "cancelled");
        }
        catch (Exception ex)
        {
            if (IsStale(u)) { Drop(slot, rec, "cancelled"); }
            else
            {
                Emit(u.Id, SubtitleKind.Error, ex is TimeoutException ? "处理超时，本句已放弃" : "本句失败：" + ex.Message);
                Log.Warn($"{_opt.Name}#{u.Id} 失败：{ex}");
                Drop(slot, rec, "error");
            }
        }
        finally
        {
            if (tts != null) _ = tts.DisposeAsync().AsTask();
            if (Interlocked.Decrement(ref _processing) == 0 && Stage == ChannelStage.Processing)
                SetStage(_player.IsActive ? ChannelStage.Speaking : ChannelStage.Idle);
        }
    }

    private void Drop(SpeechSlot slot, LatencyRecord rec, string reason)
    {
        _player.Cancel(slot, reason);
        if (string.IsNullOrEmpty(rec.Outcome)) rec.Outcome = reason;
    }

    private void OnSlotStarted(SpeechSlot slot)
    {
        if (_inFlight.TryGetValue(slot.Id, out var e))
            e.R.EndToPlayMs = Stopwatch.GetElapsedTime(e.U.EndTicks).TotalMilliseconds;
        SetStage(ChannelStage.Speaking);
    }

    private void OnSlotEnded(SpeechSlot slot)
    {
        if (_inFlight.TryRemove(slot.Id, out var e))
        {
            if (string.IsNullOrEmpty(e.R.Outcome)) e.R.Outcome = slot.EndReason ?? "done";
            if (slot.EndReason == "stale") Emit(slot.Id, SubtitleKind.Dropped, "排队太久，已跳过过时内容：" + slot.Text);
            if (slot.EndReason == "done" && slot.History.Count > 0)
            {
                LastPlayed = (slot.Text, slot.History);
                AudioReady?.Invoke(slot.Id, slot.History.SelectMany(c => c).ToArray());
            }
            Completed?.Invoke(e.R);
        }
        if (_player.PendingCount == 0 && Volatile.Read(ref _processing) == 0 && _enabled)
            SetStage(IsCapturing ? ChannelStage.Listening : ChannelStage.Idle);
    }

    private void SaveRecording(Utterance u, string recognized)
    {
        var samples = u.Recorded!.ToArray();
        var dir = _opt.SaveAudioDir!;
        _ = Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(dir);
                var name = $"{DateTime.Now:yyyyMMdd-HHmmss}-{(_opt.Direction == Direction.EnToZh ? "recv" : "send")}-{u.Id}";
                using (var w = new NAudio.Wave.WaveFileWriter(Path.Combine(dir, name + ".wav"), new NAudio.Wave.WaveFormat(16000, 16, 1)))
                    w.Write(PcmConvert.FloatToPcm16(samples));
                File.WriteAllText(Path.Combine(dir, name + ".txt"), recognized);
            }
            catch (Exception ex) { Log.Warn("保存识别音频失败：" + ex.Message); }
        });
    }

    private List<string> RecentContext(string source)
    {
        lock (_recentSource)
        {
            var ctx = _recentSource.ToList();
            _recentSource.Add(source);
            if (_recentSource.Count > 6) _recentSource.RemoveAt(0);
            return ctx;
        }
    }

    private bool IsAlreadyTarget(string text)
    {
        var r = TextUtil.CjkRatio(text);
        return _opt.Direction == Direction.EnToZh ? r > 0.6 : r < 0.1 && text.Any(char.IsLetter);
    }

    private static readonly HashSet<string> Fillers = new(StringComparer.OrdinalIgnoreCase)
    {
        "hmm", "hm", "mm", "mmm", "uh", "um", "uhm", "er", "erm", "ah", "eh", "oh", "huh", "ha",
        "嗯", "啊", "呃", "哦", "额", "唉", "哈", "嗯嗯", "啊啊",
    };

    private static readonly HashSet<string> Backchannel = new(StringComparer.OrdinalIgnoreCase)
    {
        "yeah", "yea", "ya", "yep", "yup", "yes", "yeh", "ok", "okay", "alright", "right", "sure", "cool", "nice", "good", "great",
        "awesome", "perfect", "excellent", "sweet", "thanks", "thank", "you", "sir", "no", "nope", "nah", "oh", "ah", "well", "so",
        "but", "the", "and", "a", "um", "uh", "em", "er", "hmm", "mm", "wow", "what", "huh", "haha", "lol", "bro", "dude", "man",
        "damn", "gg", "exactly", "true", "copy", "that", "roger", "got", "it", "my", "god", "gosh", "lets", "let's", "go", "hey",
        "hi", "hello", "yo", "hell", "jesus", "okay.", "mhm", "uhh", "ohh", "hmm.", "is", "this", "thing",
    };

    /// <summary>单个词时只有这些战术词才值得播报（其余单词多半是半截话或听错）。</summary>
    private static readonly HashSet<string> Tactical = new(StringComparer.OrdinalIgnoreCase)
    {
        "behind", "left", "right", "front", "back", "up", "down", "upstairs", "downstairs", "roof", "rooftop", "sniper", "snipers",
        "enemy", "enemies", "contact", "reloading", "reload", "cracked", "help", "revive", "wait", "stop", "go", "push", "pushing",
        "run", "hold", "cover", "here", "there", "north", "south", "east", "west", "grenade", "nade", "uav", "chopper", "helicopter",
        "exfil", "extract", "plates", "ammo", "medic", "car", "truck", "boat", "gas", "mine", "mines", "trap", "careful", "watch",
        "moving", "coming", "rushing", "flank", "flanking", "hurry", "fall", "retreat", "downed", "dead", "two", "three", "four",
        "five", "drone", "bomb", "boss", "juggernaut", "hostile", "hostiles", "friendly", "friendlies", "don't", "shoot",
    };

    /// <summary>附和类语气词（yeah / okay / thank you sir / oh my god…）或不成句的单个词：只显示字幕，不播报。</summary>
    internal static bool IsBackchannel(string text)
    {
        var words = System.Text.RegularExpressions.Regex.Matches(text.ToLowerInvariant(), @"[a-z']+").Select(m => m.Value).ToList();
        if (words.Count == 0) return false;
        if (words.Count == 1) return !Tactical.Contains(words[0]);
        // “go right”“yeah behind”之类含方向/战术词的不算语气词（“let's go”除外）
        return words.Count <= 4 && words.All(Backchannel.Contains) && !words.Any(w => w != "go" && Tactical.Contains(w));
    }

    /// <summary>空内容、纯标点或纯语气词（hmm、uh、嗯、呃……）不翻译也不播报。</summary>
    internal static bool IsNoise(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.All(c => !char.IsLetterOrDigit(c))) return true;
        var parts = text.Split(new[] { ' ', ',', '.', '!', '?', '，', '。', '！', '？', '…', '、' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 && parts.All(Fillers.Contains);
    }

    private float[] LeadIn(int rate)
    {
        var n = rate * _opt.LeadInMs / 1000;
        var rnd = new Random(1);
        var buf = new float[n];
        for (int i = 0; i < n; i++) buf[i] = (float)(rnd.NextDouble() * 2 - 1) * _opt.LeadInLevel;
        return buf;
    }

    private void Emit(long id, SubtitleKind kind, string text)
        => Subtitle?.Invoke(new SubtitleEvent(_opt.Name, id, kind, text, DateTime.Now));

    private void SetStage(ChannelStage s)
    {
        if (_stage == s) return;
        _stage = s;
        StageChanged?.Invoke(Stage);
    }

    private sealed class Utterance(long id, int epoch, CancellationToken token)
    {
        public long Id { get; } = id;
        public int Epoch { get; } = epoch;
        public CancellationToken Token { get; } = token;
        public IAsrSession? Asr { get; set; }
        public SpeechSlot? Slot { get; set; }
        public long EndTicks { get; set; }
        public float PanSum { get; set; }
        public float EnergySum { get; set; }
        public List<float>? Recorded { get; set; }
    }
}
