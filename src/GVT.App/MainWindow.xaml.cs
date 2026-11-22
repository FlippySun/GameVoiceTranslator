using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using GVT.App.Models;
using GVT.Core.Aliyun;
using GVT.Core.Audio;
using GVT.Core.Config;
using GVT.Core.Pipeline;

namespace GVT.App;

public sealed class HotkeyRow : Observable
{
    private string _display = "";
    public required string Key { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public string Display { get => _display; set => Set(ref _display, value); }
}

public partial class MainWindow : Window
{
    private const string DefaultItem = "（系统默认设备）";
    private readonly AppConfig _cfg;
    private readonly OverlayWindow _overlay;
    private readonly TimelineStore _timeline = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly List<HotkeyRow> _hotkeyRows = new();
    private readonly List<string> _logLines = new();
    private readonly List<double> _sendLat = new(), _recvLat = new();
    private RawInputHotkeys? _hotkeys;
    private Engine? _engine;
    private Process? _watchdog;
    private EventWaitHandle? _watchdogGraceful;
    private HotkeyRow? _rebinding;
    private bool _loading = true;

    public MainWindow(AppConfig cfg)
    {
        InitializeComponent();
        _cfg = cfg;
        _timeline.RecentMax = cfg.Overlay.MaxLines;
        _overlay = new OverlayWindow(cfg, _timeline);
        _overlay.StyleChanged += () => Dispatcher.BeginInvoke(SyncOverlayStyle);
        _overlay.EditModeChanged += editing => Dispatcher.BeginInvoke(() =>
        {
            OverlayEditBtn.Content = editing ? "完成并锁定" : "调整位置和大小";
            if (!editing) { _cfg.Save(); Toast("浮窗位置和大小已保存"); }
        });

        TimelineList.ItemsSource = _timeline.Items;
        _timeline.Items.CollectionChanged += (_, _) =>
        {
            TimelineEmpty.Visibility = _timeline.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            Dispatcher.BeginInvoke(() => TimelineScroll.ScrollToEnd(), DispatcherPriority.Background);
        };

        Log.Line += line => Dispatcher.BeginInvoke(() => AddLog(line));
        Loaded += OnLoaded;
        Closing += (_, _) => Shutdown();
        StateChanged += (_, _) =>
        {
            MaxBtn.Content = WindowState == WindowState.Maximized ? "" : "";
            // 最大化时 WindowChrome 会让窗口超出屏幕边缘一点
            BorderThickness = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        };
        _timer.Tick += (_, _) => RefreshStatus();
    }

    // =====================================================================
    // 初始化
    // =====================================================================

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateKeyStatus();
        LoadDevices();
        ShowGuide("MW4");
        (_cfg.Send.Gate == GateMode.PushToTalk ? GatePtt : GateVad).IsChecked = true;
        (_cfg.DashScope.Translator == TranslatorKind.Mt ? TrMt : _cfg.DashScope.LlmModel == "qwen3.8-flash" ? TrLlmPlus : TrLlm).IsChecked = true;
        SaveAudioCheck.IsChecked = _cfg.Debug.SaveUtteranceAudio;
        (_cfg.Receive.ListenMode == ReceiveListenMode.Both ? ListenBoth : ListenFirst).IsChecked = true;
        LoadVoices();
        LoadOverlaySettings();
        LoadHotkeys();
        UpdateHero();
        _loading = false;

        try
        {
            _hotkeys = new RawInputHotkeys();
            _hotkeys.KeyChanged += OnKey;
        }
        catch (Exception ex)
        {
            Log.Error("快捷键初始化失败：" + ex.Message);
        }
        if (_cfg.Overlay.Visible) _overlay.Show();
        _timer.Start();

        // 调试截图用：--page voice / --demo
        var args = Environment.GetCommandLineArgs();
        var pi = Array.IndexOf(args, "--page");
        if (pi >= 0 && pi + 1 < args.Length)
            (args[pi + 1] switch { "setup" => NavSetup, "voice" => NavVoice, "overlay" => NavOverlay, "diag" => NavDiag, _ => NavHome }).IsChecked = true;
        if (args.Contains("--demo")) FillDemo();
        if (args.Contains("--autostart")) StartEngine();
        var si = Array.IndexOf(args, "--shot");
        if (si >= 0 && si + 1 < args.Length) _ = ShootAllAsync(args[si + 1]).ContinueWith(t => { if (t.Exception != null) { File.WriteAllText(System.IO.Path.Combine(args[si + 1], "error.txt"), t.Exception.ToString()); Dispatcher.Invoke(Close); } });
    }

    /// <summary>调试用：把每个页面和浮窗渲染成 PNG（只渲染本程序自己的界面，不截屏），然后退出。</summary>
    private async Task ShootAllAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        var pages = new (RadioButton Nav, string Name)[] { (NavHome, "home"), (NavSetup, "setup"), (NavVoice, "voice"), (NavOverlay, "overlay"), (NavDiag, "diag") };
        foreach (var (nav, name) in pages)
        {
            nav.IsChecked = true;
            await Task.Delay(900);
            SavePng(this, System.IO.Path.Combine(dir, name + ".png"));
        }
        if (args_EditOverlay()) { _overlay.SetEditMode(true); await Task.Delay(400); }
        SavePng(_overlay, System.IO.Path.Combine(dir, "float.png"));
        if (_overlay.Toolbar is { IsVisible: true } tb) SavePng(tb, System.IO.Path.Combine(dir, "toolbar.png"));
        Close();
    }

    private static bool args_EditOverlay() => Environment.GetCommandLineArgs().Contains("--edit");

    private static void SavePng(Window w, string path)
    {
        var root = (FrameworkElement)w.Content;
        var dpi = VisualTreeHelper.GetDpi(w);
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(root.ActualWidth * dpi.DpiScaleX), (int)(root.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(w == Application.Current.MainWindow ? (Brush)Application.Current.FindResource("B.Bg") : new SolidColorBrush(Color.FromRgb(0x3A, 0x44, 0x38)), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        }
        bmp.Render(dv);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    // =====================================================================
    // 窗口与导航
    // =====================================================================

    private void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Max_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PageHost == null) return;
        var (page, title) = sender == NavSetup ? ((FrameworkElement)PageSetup, "设备与接入")
            : sender == NavVoice ? (PageVoice, "声音")
            : sender == NavOverlay ? (PageOverlay, "浮窗与快捷键")
            : sender == NavDiag ? (PageDiag, "诊断与日志")
            : (PageHome, "控制台");
        foreach (FrameworkElement p in PageHost.Children) p.Visibility = p == page ? Visibility.Visible : Visibility.Collapsed;
        PageTitle.Text = title;
        if (page == PageDiag) { LogList.ItemsSource = null; LogList.ItemsSource = _logLines; LogScroll.ScrollToEnd(); }
        var slide = new TranslateTransform(0, 10);
        page.RenderTransform = slide;
        page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
        slide.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    private void Toast(string text)
    {
        ToastText.Text = text;
        var fade = new DoubleAnimationUsingKeyFrames();
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(180))));
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2200))));
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2500))));
        ToastBox.BeginAnimation(OpacityProperty, fade);
        ToastSlide.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    // =====================================================================
    // 设备与接入
    // =====================================================================

    private void LoadDevices()
    {
        try
        {
            var capture = DeviceCatalog.ListCapture();
            var render = DeviceCatalog.ListRender();
            Fill(MicCombo, capture, _cfg.Send.MicDevice);
            Fill(SendOutCombo, render, _cfg.Send.OutputDevice);
            Fill(SourceCombo, capture, _cfg.Receive.SourceDevice);
            Fill(HeadphoneCombo, render, _cfg.Receive.HeadphoneDevice);
            bool a = render.Any(n => n.Contains("VB-Audio Virtual Cable", StringComparison.OrdinalIgnoreCase));
            bool b = capture.Any(n => n.Contains("VB-Audio Hi-Fi Cable", StringComparison.OrdinalIgnoreCase));
            SetChip(CableAChip, CableAText, a, "VB-CABLE（发送）已安装", "未检测到 VB-CABLE");
            SetChip(CableBChip, CableBText, b, "Hi-Fi Cable（接收）已安装", "未检测到 Hi-Fi Cable");
        }
        catch (Exception ex)
        {
            Log.Error("读取设备失败：" + ex.Message);
        }
    }

    private void SetChip(Border chip, TextBlock text, bool ok, string okText, string badText)
    {
        chip.Background = (Brush)FindResource(ok ? "B.OkSoft" : "B.DangerSoft");
        text.Foreground = (Brush)FindResource(ok ? "B.Ok" : "B.Danger");
        text.Text = (ok ? "✓  " : "!  ") + (ok ? okText : badText);
    }

    private static void Fill(ComboBox box, IReadOnlyList<string> names, string configured)
    {
        var items = new List<string> { DefaultItem };
        items.AddRange(names);
        if (!string.IsNullOrWhiteSpace(configured) && !names.Any(n => n.Contains(configured, StringComparison.OrdinalIgnoreCase)))
            items.Add(configured + "（未找到）");
        box.ItemsSource = items;
        box.SelectedItem = string.IsNullOrWhiteSpace(configured) ? DefaultItem
            : items.FirstOrDefault(n => n.Contains(configured, StringComparison.OrdinalIgnoreCase)) ?? DefaultItem;
    }

    private static string Picked(ComboBox box)
    {
        var s = box.SelectedItem as string ?? "";
        if (s == DefaultItem) return "";
        return s.EndsWith("（未找到）") ? s[..^"（未找到）".Length] : s;
    }

    private void RefreshDevices_Click(object sender, RoutedEventArgs e) { LoadDevices(); Toast("设备列表已刷新"); }

    private void SaveKey_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ApiKeyBox.Password)) return;
        SecretStore.SaveApiKey(ApiKeyBox.Password);
        ApiKeyBox.Clear();
        UpdateKeyStatus();
        Toast("API Key 已加密保存");
    }

    private void UpdateKeyStatus()
    {
        bool has = SecretStore.HasApiKey;
        KeyChip.Background = (Brush)FindResource(has ? "B.OkSoft" : "B.DangerSoft");
        KeyChipText.Foreground = (Brush)FindResource(has ? "B.Ok" : "B.Danger");
        KeyChipText.Text = has ? "✓  已保存" : "未设置";
    }

    private static readonly Dictionary<string, (string, string)[]> Guides = new()
    {
        ["MW4"] = new[]
        {
            ("设置 → 音频 → 麦克风输入设备", "CABLE Output (VB-Audio Virtual Cable)"),
            ("语音聊天输出设备", "Hi-Fi Cable Input (VB-Audio Hi-Fi Cable)"),
            ("游戏音效设备", "保持你的耳机，不要改"),
            ("语音录制方式", "开放麦克风（按住说话由本软件负责）"),
            ("显示模式", "无边框全屏（否则看不到浮窗）"),
        },
        ["KOOK"] = new[]
        {
            ("语音设置 → 输入设备", "CABLE Output (VB-Audio Virtual Cable)"),
            ("输出设备", "Hi-Fi Cable Input (VB-Audio Hi-Fi Cable)"),
            ("输入模式", "自由麦，手动调低灵敏度门限"),
            ("降噪 / 回声消除 / 自动增益", "全部关闭"),
        },
        ["Discord"] = new[]
        {
            ("语音与视频 → 输入设备", "CABLE Output (VB-Audio Virtual Cable)"),
            ("输出设备", "Hi-Fi Cable Input (VB-Audio Hi-Fi Cable)"),
            ("输入模式", "语音激活，关闭“自动确定输入灵敏度”"),
            ("噪声抑制 / 回声消除 / 自动增益", "全部关闭"),
            ("衰减", "关闭"),
        },
    };

    private void Guide_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string key } && GuideList != null) ShowGuide(key);
    }

    private void ShowGuide(string key) => GuideList.ItemsSource = Guides[key].Select(t => Tuple.Create(t.Item1, t.Item2)).ToList();

    private void ApplyFormToConfig()
    {
        _cfg.Send.MicDevice = Picked(MicCombo);
        _cfg.Send.OutputDevice = Picked(SendOutCombo);
        _cfg.Receive.SourceDevice = Picked(SourceCombo);
        _cfg.Receive.HeadphoneDevice = Picked(HeadphoneCombo);
        _cfg.Send.Gate = GateVad.IsChecked == true ? GateMode.VoiceActivity : GateMode.PushToTalk;
        _cfg.DashScope.Translator = TrMt.IsChecked == true ? TranslatorKind.Mt : TranslatorKind.Llm;
        _cfg.DashScope.LlmModel = TrLlmPlus.IsChecked == true ? "qwen3.8-flash" : "qwen-flash";
        _cfg.Save();
    }

    // =====================================================================
    // 启停
    // =====================================================================

    private void Power_Click(object sender, RoutedEventArgs e)
    {
        if (_engine != null) StopEngine();
        else StartEngine();
    }

    private void StartEngine()
    {
        var key = SecretStore.GetApiKey();
        if (string.IsNullOrWhiteSpace(key))
        {
            NavSetup.IsChecked = true;
            Toast("请先在“设备与接入”里保存 API Key");
            return;
        }
        ApplyFormToConfig();
        WarnText.Text = "";
        try
        {
            var glossary = Glossary.Load(_cfg.ResolveGlossaryPath());
            var services = new DashScopeServices(_cfg.DashScope, key, glossary);
            _engine = new Engine(_cfg, services, glossary, AppContext.BaseDirectory);
            _engine.Subtitle += ev => Dispatcher.BeginInvoke(() => _timeline.Apply(ev));
            _engine.Latency += r => Dispatcher.BeginInvoke(() => OnLatency(r));
            var rate = _engine.TtsSampleRate;
            _engine.UtteranceAudio += (ch, id, audio) => Dispatcher.BeginInvoke(() => _timeline.AttachAudio(ch, id, audio, rate));
            _engine.Start();
            StartWatchdog();
            Toast("翻译已开始");
        }
        catch (Exception ex)
        {
            _engine?.Dispose();
            _engine = null;
            Log.Error("启动失败：" + ex.Message);
            WarnText.Text = "启动失败：" + ex.Message;
        }
        UpdateHero();
    }

    private void StopEngine()
    {
        _engine?.Dispose();
        _engine = null;
        StopWatchdog();
        UpdateHero();
        Toast("翻译已停止");
    }

    private void Shutdown()
    {
        _timer.Stop();
        _engine?.Dispose();
        _engine = null;
        StopWatchdog();
        _hotkeys?.Dispose();
        _overlay.Close();
        _cfg.Save();
    }

    private void UpdateHero()
    {
        bool running = _engine?.Running == true;
        PowerBtn.Tag = running;
        HeroTitle.Text = running ? "翻译进行中" : "准备就绪";
        var send = VoiceCatalog.Find(_cfg.Send.Voice)?.Name ?? _cfg.Send.Voice;
        var recv = VoiceCatalog.Find(_cfg.Receive.Voice)?.Name ?? _cfg.Receive.Voice;
        HeroSub.Text = running
            ? $"按住 {KeyName(_cfg.Hotkeys.PushToTalk)} 说话。队友听到 {send} 的英文，你听到 {recv} 的中文。"
            : "点击左侧按钮开始。开始前确认游戏的麦克风是 CABLE Output、语音输出是 Hi-Fi Cable Input。";
        SendSub.Text = $"中文 → 英文 · {send}";
        RecvSub.Text = $"英文 → 中文 · {recv}";
        SendHint.Text = _cfg.Send.Gate == GateMode.PushToTalk ? $"按住 {KeyName(_cfg.Hotkeys.PushToTalk)} 说话" : "语音激活：直接说话";
        RecvHint.Text = $"按住 {KeyName(_cfg.Hotkeys.HoldOriginal)} 临时听原声";
        SideDot.Tag = running;
        SideDot.Fill = (Brush)FindResource(running ? "B.Ok" : "B.Text3");
        SideStatus.Text = running ? "翻译运行中" : "未运行";
        WarnBox.Visibility = string.IsNullOrEmpty(WarnText.Text) ? Visibility.Collapsed : Visibility.Visible;
    }

    // =====================================================================
    // 控制台开关
    // =====================================================================

    private void SendToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_engine?.Send != null) { if (_engine.Send.Enabled != SendToggle.IsChecked) _engine.ToggleSend(); }
        else _cfg.Send.Enabled = SendToggle.IsChecked == true;
    }

    private void ReceiveToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_engine?.Receive != null) { if (_engine.Receive.Enabled != ReceiveToggle.IsChecked) _engine.ToggleReceive(); }
        else _cfg.Receive.Enabled = ReceiveToggle.IsChecked == true;
    }

    private void Listen_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var mode = ListenBoth.IsChecked == true ? ReceiveListenMode.Both : ReceiveListenMode.TranslationFirst;
        _cfg.Receive.ListenMode = mode;
        _engine?.SetListenMode(mode);
        _cfg.Save();
    }

    private async void PlayClip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TimelineItem item }) return;
        try { await ClipPlayer.ToggleAsync(_cfg, item); }
        catch (Exception ex) { Toast("播放失败：" + ex.Message); }
    }

    private void ClearTimeline_Click(object sender, RoutedEventArgs e) { ClipPlayer.Stop(); _timeline.Clear(); }

    // =====================================================================
    // 声音
    // =====================================================================

    private void LoadVoices()
    {
        SendVoiceList.ItemsSource = VoiceCatalog.EnglishAll.ToList();
        RecvVoiceList.ItemsSource = VoiceCatalog.Chinese;
        SendVoiceList.SelectedItem = VoiceCatalog.EnglishAll.FirstOrDefault(v => v.Id == _cfg.Send.Voice);
        RecvVoiceList.SelectedItem = VoiceCatalog.Chinese.FirstOrDefault(v => v.Id == _cfg.Receive.Voice);
        StyleRadio(_cfg.Send.Style, SendStyleCasual, SendStyleCallout, SendStyleStandard).IsChecked = true;
        StyleRadio(_cfg.Receive.Style, RecvStyleCasual, RecvStyleCallout, RecvStyleStandard).IsChecked = true;
        SendRate.Value = _cfg.Send.TtsRate;
        RecvRate.Value = _cfg.Receive.TtsRate;
        SendInstruction.Text = _cfg.Send.CustomInstruction;
        UpdateRateTexts();
    }

    private static RadioButton StyleRadio(VoiceStyle s, RadioButton casual, RadioButton callout, RadioButton standard)
        => s switch { VoiceStyle.Callout => callout, VoiceStyle.Standard => standard, _ => casual };

    private static VoiceStyle StyleOf(RadioButton callout, RadioButton standard)
        => callout.IsChecked == true ? VoiceStyle.Callout : standard.IsChecked == true ? VoiceStyle.Standard : VoiceStyle.Casual;

    private void Voice_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (SendVoiceList.SelectedItem is VoiceInfo sv) _cfg.Send.Voice = sv.Id;
        if (RecvVoiceList.SelectedItem is VoiceInfo rv) _cfg.Receive.Voice = rv.Id;
        _cfg.Send.Style = StyleOf(SendStyleCallout, SendStyleStandard);
        _cfg.Receive.Style = StyleOf(RecvStyleCallout, RecvStyleStandard);
        ApplyVoices();
    }

    private void Rate_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateRateTexts();
        if (_loading) return;
        _cfg.Send.TtsRate = Math.Round(SendRate.Value, 2);
        _cfg.Receive.TtsRate = Math.Round(RecvRate.Value, 2);
        ApplyVoices();
    }

    private void UpdateRateTexts()
    {
        if (SendRateText == null || RecvRateText == null) return;
        SendRateText.Text = $"{SendRate.Value:F2}×";
        RecvRateText.Text = $"{RecvRate.Value:F2}×";
    }

    private void Instruction_LostFocus(object sender, RoutedEventArgs e)
    {
        _cfg.Send.CustomInstruction = SendInstruction.Text.Trim();
        ApplyVoices();
    }

    private void ApplyVoices()
    {
        _cfg.Save();
        _engine?.ApplyVoiceSettings();
        UpdateHero();
    }

    private async void PreviewVoice_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { Tag: VoiceInfo v } btn) return;
        var key = SecretStore.GetApiKey();
        if (key == null) { Toast("请先保存 API Key"); return; }
        bool en = v.Language == "en";
        var style = en ? _cfg.Send.Style : _cfg.Receive.Style;
        var tts = en
            ? VoiceCatalog.Resolve(v.Id, _cfg.Send.TtsRate, _cfg.Send.Style, _cfg.Send.CustomInstruction, _cfg.DashScope.TtsModel, "en")
            : VoiceCatalog.Resolve(v.Id, _cfg.Receive.TtsRate, _cfg.Receive.Style, _cfg.Receive.CustomInstruction, _cfg.DashScope.TtsModel, "zh");
        btn.IsEnabled = false;
        try
        {
            Toast($"试听 {v.Name} · {(tts.Instruction != null ? StyleName(style) : "标准")}");
            await VoicePreview.PlayAsync(_cfg, key, tts, en ? VoicePreview.EnglishSample : VoicePreview.ChineseSample);
        }
        catch (Exception ex)
        {
            Toast("试听失败：" + ex.Message);
        }
        finally
        {
            btn.IsEnabled = true;
        }
    }

    private async void DesignVoice_Click(object sender, RoutedEventArgs e)
    {
        var prompt = DesignPrompt.Text.Trim();
        if (prompt.Length < 6) { Toast("先写一句声音描述"); return; }
        var key = SecretStore.GetApiKey();
        if (key == null) { Toast("请先保存 API Key"); return; }
        DesignBtn.IsEnabled = false;
        DesignBtn.Content = "生成中…";
        try
        {
            var custom = new VoiceCustomization(_cfg.DashScope, key);
            var prefix = "c" + Guid.NewGuid().ToString("N")[..6];
            var r = await custom.DesignAsync(prompt, VoicePreview.EnglishSample, prefix, "en", VoiceCatalog.QwenAudio, CancellationToken.None);
            int n = _cfg.CustomVoices.Count + 1;
            _cfg.CustomVoices.Insert(0, new CustomVoiceConfig
            {
                Id = r.VoiceId, Model = VoiceCatalog.QwenAudio, Name = $"自定义 {n}",
                Description = prompt.Length > 18 ? prompt[..18] + "…" : prompt, Language = "en",
            });
            VoiceCatalog.LoadCustom(_cfg);
            _cfg.Send.Voice = r.VoiceId;
            _cfg.Save();
            _loading = true;
            SendVoiceList.ItemsSource = VoiceCatalog.EnglishAll.ToList();
            SendVoiceList.SelectedItem = VoiceCatalog.EnglishAll.FirstOrDefault(v => v.Id == r.VoiceId);
            _loading = false;
            ApplyVoices();
            Toast($"已生成“自定义 {n}”并设为当前英文声音，正在试听");
            var tts = VoiceCatalog.Resolve(r.VoiceId, _cfg.Send.TtsRate, _cfg.Send.Style, _cfg.Send.CustomInstruction, _cfg.DashScope.TtsModel, "en");
            await VoicePreview.PlayAsync(_cfg, key, tts, VoicePreview.EnglishSample);
        }
        catch (Exception ex)
        {
            Toast("生成失败：" + ex.Message);
        }
        finally
        {
            DesignBtn.IsEnabled = true;
            DesignBtn.Content = "生成新音色";
        }
    }

    private static string StyleName(VoiceStyle s) => s switch { VoiceStyle.Callout => "冷静报点", VoiceStyle.Standard => "标准", _ => "开黑口语" };

    // =====================================================================
    // 浮窗
    // =====================================================================

    private void LoadOverlaySettings()
    {
        var o = _cfg.Overlay;
        OverlayVisible.IsChecked = o.Visible;
        OvFont.Value = o.FontSize;
        OvOpacity.Value = o.Opacity;
        OvLines.Value = o.MaxLines;
        OvHold.Value = o.HoldSeconds;
        OvShowOriginal.IsChecked = o.ShowOriginal;
        OverlayEditHint.Text = $"按 {KeyName(_cfg.Hotkeys.OverlayEdit)} 进入编辑：拖动移动、拖右下角改大小，旁边的工具栏可调背景透明度、字号和各类文字颜色；再按一次锁定。";
        UpdateOverlayTexts();
    }

    private void UpdateOverlayTexts()
    {
        if (OvFontText == null || OvOpacityText == null || OvLinesText == null || OvHoldText == null) return;
        OvFontText.Text = $"{OvFont.Value:F0}";
        OvOpacityText.Text = $"{OvOpacity.Value * 100:F0}%";
        OvLinesText.Text = $"{OvLines.Value:F0} 句";
        OvHoldText.Text = $"{OvHold.Value:F0} 秒";
    }

    private void Overlay_Changed(object sender, RoutedEventArgs e)
    {
        UpdateOverlayTexts();
        if (_loading) return;
        var o = _cfg.Overlay;
        o.FontSize = Math.Round(OvFont.Value);
        o.Opacity = Math.Round(OvOpacity.Value, 2);
        o.MaxLines = (int)OvLines.Value;
        o.HoldSeconds = OvHold.Value;
        o.ShowOriginal = OvShowOriginal.IsChecked == true;
        _timeline.RecentMax = o.MaxLines;
        _overlay.ApplyStyle();
        _cfg.Save();
    }

    private void OverlayVisible_Click(object sender, RoutedEventArgs e)
    {
        _cfg.Overlay.Visible = OverlayVisible.IsChecked == true;
        if (_cfg.Overlay.Visible) _overlay.Show(); else _overlay.Hide();
        _cfg.Save();
    }

    private void OverlayEdit_Click(object sender, RoutedEventArgs e)
    {
        if (!_overlay.IsVisible) { _overlay.Show(); OverlayVisible.IsChecked = _cfg.Overlay.Visible = true; }
        _overlay.ToggleEditMode();
    }

    /// <summary>浮窗编辑工具栏 / 滚轮改了样式，同步到设置页并保存。</summary>
    public void SyncOverlayStyle()
    {
        _loading = true;
        OvFont.Value = _cfg.Overlay.FontSize;
        OvOpacity.Value = _cfg.Overlay.Opacity;
        OvShowOriginal.IsChecked = _cfg.Overlay.ShowOriginal;
        _cfg.Save();
        UpdateOverlayTexts();
        _loading = false;
    }

    // =====================================================================
    // 快捷键
    // =====================================================================

    private void LoadHotkeys()
    {
        _hotkeyRows.Clear();
        void Row(string key, string title, string desc) => _hotkeyRows.Add(new HotkeyRow { Key = key, Title = title, Description = desc });
        Row(nameof(HotkeyConfig.PushToTalk), "按住说话", "按住时录音，松开后这句会完整翻译并发出");
        Row(nameof(HotkeyConfig.CancelSentence), "取消本句", "丢弃正在录的和还没发出的英文（已播出的无法撤回）");
        Row(nameof(HotkeyConfig.ToggleSend), "停止 / 恢复发送", "立即静音虚拟麦克风，清空待发内容");
        Row(nameof(HotkeyConfig.HoldOriginal), "按住听原声", "暂停中文播报，队友原声恢复正常音量");
        Row(nameof(HotkeyConfig.ReplayLast), "重播上一句中文", "只在自己耳机里重播，不会发给队友");
        Row(nameof(HotkeyConfig.ToggleReceive), "停止 / 恢复中文播报", "原声照常");
        Row(nameof(HotkeyConfig.OverlayEdit), "编辑浮窗", "进入后可拖动、调整大小，并调背景透明度、字号、文字颜色；再按一次锁定");
        foreach (var r in _hotkeyRows) r.Display = KeyName(GetHotkey(r.Key));
        HotkeyList.ItemsSource = null;
        HotkeyList.ItemsSource = _hotkeyRows;
    }

    private string GetHotkey(string prop) => (string)typeof(HotkeyConfig).GetProperty(prop)!.GetValue(_cfg.Hotkeys)!;
    private void SetHotkey(string prop, string value) => typeof(HotkeyConfig).GetProperty(prop)!.SetValue(_cfg.Hotkeys, value);

    private static string KeyName(string key) => key switch
    {
        "XButton1" => "鼠标后侧键",
        "XButton2" => "鼠标前侧键",
        "MiddleButton" => "鼠标中键",
        "" => "未设置",
        _ => key,
    };

    private void RebindHotkey_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: HotkeyRow row }) return;
        if (_rebinding != null) _rebinding.Display = KeyName(GetHotkey(_rebinding.Key));
        _rebinding = row;
        row.Display = "请按下新按键…";
    }

    private void ResetHotkeys_Click(object sender, RoutedEventArgs e)
    {
        _cfg.Hotkeys = new HotkeyConfig();
        _cfg.Save();
        LoadHotkeys();
        LoadOverlaySettings();
        UpdateHero();
        Toast("快捷键已恢复默认");
    }

    private void OnKey(string name, bool down)
    {
        if (_rebinding != null)
        {
            if (!down) return;
            if (name == "Escape") { _rebinding.Display = KeyName(GetHotkey(_rebinding.Key)); _rebinding = null; return; }
            // 同一个键不能绑两个动作
            foreach (var r in _hotkeyRows.Where(r => r != _rebinding && RawInputHotkeys.Matches(GetHotkey(r.Key), name)))
            {
                SetHotkey(r.Key, "");
                r.Display = KeyName("");
            }
            SetHotkey(_rebinding.Key, name);
            _rebinding.Display = KeyName(name);
            _rebinding = null;
            _cfg.Save();
            UpdateHero();
            LoadOverlaySettings();
            return;
        }

        var h = _cfg.Hotkeys;
        if (RawInputHotkeys.Matches(h.OverlayEdit, name))
        {
            if (down)
            {
                if (!_overlay.IsVisible) { _overlay.Show(); OverlayVisible.IsChecked = _cfg.Overlay.Visible = true; }
                _overlay.ToggleEditMode();
            }
            return;
        }
        var engine = _engine;
        if (engine == null) return;
        if (RawInputHotkeys.Matches(h.PushToTalk, name)) engine.PushToTalk(down);
        else if (RawInputHotkeys.Matches(h.HoldOriginal, name)) engine.HoldOriginal(down);
        else if (!down) return;
        else if (RawInputHotkeys.Matches(h.CancelSentence, name)) engine.CancelSentence();
        else if (RawInputHotkeys.Matches(h.ToggleSend, name)) engine.ToggleSend();
        else if (RawInputHotkeys.Matches(h.ReplayLast, name)) engine.ReplayLast();
        else if (RawInputHotkeys.Matches(h.ToggleReceive, name)) engine.ToggleReceive();
    }

    // =====================================================================
    // 崩溃守护
    // =====================================================================

    private void StartWatchdog()
    {
        try
        {
            var exe = System.IO.Path.Combine(AppContext.BaseDirectory, "GVT.Watchdog.exe");
            if (!File.Exists(exe)) { Log.Warn("未找到 GVT.Watchdog.exe，崩溃时无法自动恢复原声。"); return; }
            var pid = Environment.ProcessId;
            _watchdogGraceful = new EventWaitHandle(false, EventResetMode.ManualReset, $"GVT_Watchdog_Graceful_{pid}");
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("--parent"); psi.ArgumentList.Add(pid.ToString());
            psi.ArgumentList.Add("--capture"); psi.ArgumentList.Add(_cfg.Receive.SourceDevice);
            psi.ArgumentList.Add("--render"); psi.ArgumentList.Add(_cfg.Receive.HeadphoneDevice);
            _watchdog = Process.Start(psi);
        }
        catch (Exception ex)
        {
            Log.Warn("守护进程启动失败：" + ex.Message);
        }
    }

    private void StopWatchdog()
    {
        try { _watchdogGraceful?.Set(); } catch { }
        try { _watchdog?.WaitForExit(1000); } catch { }
        _watchdogGraceful?.Dispose();
        _watchdogGraceful = null;
        _watchdog = null;
    }

    // =====================================================================
    // 状态刷新 / 日志
    // =====================================================================

    private void OnLatency(LatencyRecord r)
    {
        _timeline.Complete(r.Channel, r.Id, r.Outcome);
        AddLog(r.ToString());
        if (r.Outcome != "done" || r.EndToPlayMs == null) return;
        var list = r.Channel == "发送" ? _sendLat : _recvLat;
        list.Add(r.EndToPlayMs.Value);
        if (list.Count > 50) list.RemoveAt(0);
        var text = $"上一句 {r.EndToPlayMs.Value / 1000:F2}s · 平均 {list.Average() / 1000:F2}s";
        if (r.Channel == "发送") SendLatency.Text = text; else RecvLatency.Text = text;
    }

    private void AddLog(string line)
    {
        _logLines.Add(line);
        if (_logLines.Count > 500) _logLines.RemoveAt(0);
        LogEmpty.Visibility = Visibility.Collapsed;
        if (PageDiag.Visibility == Visibility.Visible)
        {
            LogList.ItemsSource = null;
            LogList.ItemsSource = _logLines;
            LogScroll.ScrollToEnd();
        }
    }

    private void SaveAudio_Click(object sender, RoutedEventArgs e)
    {
        _cfg.Debug.SaveUtteranceAudio = SaveAudioCheck.IsChecked == true;
        _cfg.Save();
        Toast(_cfg.Debug.SaveUtteranceAudio ? "下次开始翻译时会保存识别音频" : "已关闭保存识别音频");
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
        => Process.Start(new ProcessStartInfo("explorer.exe", Log.LogDirectory) { UseShellExecute = true });

    private static string StageText(ChannelStage s, bool enabled) => !enabled ? "已关闭" : s switch
    {
        ChannelStage.Idle => "待机",
        ChannelStage.Listening => "正在听",
        ChannelStage.Processing => "翻译中",
        ChannelStage.Speaking => "正在播放",
        ChannelStage.Error => "出错",
        _ => "已关闭",
    };

    private void SetStage(Ellipse dot, TextBlock text, ChannelStage stage, bool enabled, string activeBrush)
    {
        text.Text = StageText(stage, enabled);
        bool busy = enabled && stage is ChannelStage.Listening or ChannelStage.Processing or ChannelStage.Speaking;
        dot.Tag = busy;
        dot.Fill = (Brush)FindResource(!enabled ? "B.Text3" : busy ? activeBrush : "B.Ok");
    }

    private void RefreshStatus()
    {
        var s = _engine?.GetStatus();
        if (s == null || !s.Running)
        {
            SendStage.Text = RecvStage.Text = "未运行";
            SendDot.Tag = RecvDot.Tag = false;
            SendDot.Fill = RecvDot.Fill = (Brush)FindResource("B.Text3");
            MicMeter.Push(-120);
            SrcMeter.Push(-120);
            _overlay.SetStatus(false, false, false, false, false);
            return;
        }
        SetStage(SendDot, SendStage, s.PttDown ? ChannelStage.Listening : s.SendStage, s.SendEnabled, "B.Me");
        SetStage(RecvDot, RecvStage, s.ReceiveStage, s.ReceiveEnabled, "B.Mate");
        if (s.PttDown) SendStage.Text = "录音中";
        if (s.HoldOriginal) RecvStage.Text = "正在听原声";
        MicMeter.Push(s.MicDb);
        SrcMeter.Push(s.SourceDb);
        SendToggle.IsChecked = s.SendEnabled;
        ReceiveToggle.IsChecked = s.ReceiveEnabled;
        var usage = _engine!.Usage;
        SideCost.Text = $"本次 ¥{usage.CostYuan:F3} / 预算 ¥{usage.BudgetYuan:F0}";
        if (!string.IsNullOrEmpty(s.Warning)) WarnText.Text = s.Warning;
        WarnBox.Visibility = string.IsNullOrEmpty(WarnText.Text) ? Visibility.Collapsed : Visibility.Visible;
        _overlay.SetStatus(true, s.SendEnabled, s.PttDown, s.ReceiveEnabled, s.HoldOriginal);
    }

    // 演示数据：用于截图检查界面（--demo）
    private void FillDemo()
    {
        void Say(string ch, long id, string src, string tr, string? note = null)
        {
            _timeline.Apply(new SubtitleEvent(ch, id, SubtitleKind.Source, src, DateTime.Now));
            if (tr != "") _timeline.Apply(new SubtitleEvent(ch, id, SubtitleKind.Translation, tr, DateTime.Now));
            if (note != null) _timeline.Apply(new SubtitleEvent(ch, id, SubtitleKind.Info, note, DateTime.Now));
        }
        Say("发送", 1, "左边二楼有两个人，先别冲", "Two on the left, second floor. Don't push yet.");
        Say("接收", 1, "Copy that, I'm holding the stairs. One's cracked.", "收到，我守楼梯。有一个破甲了。");
        Say("接收", 2, "兄弟我在B点", "", "对方说的是中文，不播报");
        Say("发送", 2, "我们只做任务，不打人", "We're only doing contracts, not fighting.");
        _timeline.Apply(new SubtitleEvent("接收", 3, SubtitleKind.Partial, "where is the exfil", DateTime.Now));
        var tone = Enumerable.Range(0, 24000).Select(i => 0.1f * MathF.Sin(i * 0.05f)).ToArray();
        _timeline.AttachAudio("发送", 2, tone, 48000);
        _timeline.AttachAudio("接收", 1, tone, 48000);
        _timeline.Items.First(i => i.IsMe && i.Id == 2).IsPlaying = true;
    }
}
