using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GVT.Core.Aliyun;
using GVT.Core.Audio;
using GVT.Core.Config;
using GVT.Core.Pipeline;

namespace GVT.App;

public partial class MainWindow : Window
{
    private const string DefaultItem = "（系统默认设备）";
    private readonly AppConfig _cfg;
    private readonly OverlayWindow _overlay;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private RawInputHotkeys? _hotkeys;
    private Engine? _engine;
    private Process? _watchdog;
    private EventWaitHandle? _watchdogGraceful;

    public MainWindow(AppConfig cfg)
    {
        InitializeComponent();
        _cfg = cfg;
        _overlay = new OverlayWindow(cfg.Overlay);

        Log.Line += line => Dispatcher.BeginInvoke(() => AddLog(line));
        Loaded += OnLoaded;
        Closing += (_, _) => Shutdown();
        _timer.Tick += (_, _) => RefreshStatus();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateKeyStatus();
        LoadDevices();
        Select(GateCombo, _cfg.Send.Gate.ToString());
        Select(ListenCombo, _cfg.Receive.ListenMode.ToString());
        Select(TranslatorCombo, _cfg.DashScope.Translator.ToString());
        var h = _cfg.Hotkeys;
        HotkeyHelp.Text =
            $"按住说话：{h.PushToTalk}（XButton2 = 鼠标前侧键）\n" +
            $"取消本句：{h.CancelSentence}　停止/恢复发送：{h.ToggleSend}\n" +
            $"按住听原声：{h.HoldOriginal}　重播上一句中文：{h.ReplayLast}\n" +
            $"停止/恢复中文播报：{h.ToggleReceive}\n" +
            "可在配置文件 config.json 的 Hotkeys 中修改。";

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
        _overlay.SetStatus("未开始 · 在主窗口点击“开始翻译”");
        _timer.Start();
    }

    // ---------- 设备 ----------

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

            bool hasA = render.Any(n => n.Contains("CABLE-A", StringComparison.OrdinalIgnoreCase));
            bool hasB = capture.Any(n => n.Contains("CABLE-B", StringComparison.OrdinalIgnoreCase));
            SetupHint.Text = hasA && hasB
                ? "已检测到 VB-CABLE A+B。MW4 设置 → 音频：麦克风输入设备选 “CABLE-A Output”，语音聊天输出设备选 “CABLE-B Input”，语音录制方式用开放麦克风。"
                : "未检测到 VB-CABLE A+B 虚拟声卡。请先安装（见 README），重启电脑后点“刷新设备”。";
        }
        catch (Exception ex)
        {
            SetupHint.Text = "读取设备失败：" + ex.Message;
        }
    }

    private static void Fill(ComboBox box, IReadOnlyList<string> names, string configured)
    {
        var items = new List<string> { DefaultItem };
        items.AddRange(names);
        box.ItemsSource = items;
        if (string.IsNullOrWhiteSpace(configured)) box.SelectedIndex = 0;
        else
        {
            var hit = names.FirstOrDefault(n => n.Contains(configured, StringComparison.OrdinalIgnoreCase));
            if (hit != null) box.SelectedItem = hit;
            else { items.Add(configured + "（未找到）"); box.ItemsSource = items.ToList(); box.SelectedIndex = items.Count - 1; }
        }
    }

    private static string Picked(ComboBox box)
    {
        var s = box.SelectedItem as string ?? "";
        if (s == DefaultItem) return "";
        return s.EndsWith("（未找到）") ? s[..^"（未找到）".Length] : s;
    }

    private static void Select(ComboBox box, string tag)
    {
        foreach (ComboBoxItem item in box.Items)
            if ((string)item.Tag == tag) { box.SelectedItem = item; return; }
        box.SelectedIndex = 0;
    }

    private static string TagOf(ComboBox box) => (string)((ComboBoxItem)box.SelectedItem).Tag;

    private void RefreshDevices_Click(object sender, RoutedEventArgs e) => LoadDevices();

    private void OpenConfig_Click(object sender, RoutedEventArgs e)
        => Process.Start(new ProcessStartInfo("explorer.exe", AppConfig.ConfigDirectory) { UseShellExecute = true });

    // ---------- API Key ----------

    private void SaveKey_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ApiKeyBox.Password)) return;
        SecretStore.SaveApiKey(ApiKeyBox.Password);
        ApiKeyBox.Clear();
        UpdateKeyStatus();
    }

    private void UpdateKeyStatus()
        => KeyStatus.Text = SecretStore.HasApiKey
            ? "已保存（用 Windows 当前用户加密存储，不以明文落盘）。"
            : "尚未设置。粘贴百炼北京地域的 API Key 后点保存。";

    // ---------- 启停 ----------

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        var key = SecretStore.GetApiKey();
        if (string.IsNullOrWhiteSpace(key))
        {
            MessageBox.Show(this, "请先填写并保存 API Key。", "缺少 API Key");
            return;
        }

        _cfg.Send.MicDevice = Picked(MicCombo);
        _cfg.Send.OutputDevice = Picked(SendOutCombo);
        _cfg.Receive.SourceDevice = Picked(SourceCombo);
        _cfg.Receive.HeadphoneDevice = Picked(HeadphoneCombo);
        _cfg.Send.Gate = Enum.Parse<GateMode>(TagOf(GateCombo));
        _cfg.Receive.ListenMode = Enum.Parse<ReceiveListenMode>(TagOf(ListenCombo));
        _cfg.DashScope.Translator = Enum.Parse<TranslatorKind>(TagOf(TranslatorCombo));
        _cfg.Save();

        try
        {
            var glossary = Glossary.Load(_cfg.ResolveGlossaryPath());
            var services = new DashScopeServices(_cfg.DashScope, key, glossary);
            _engine = new Engine(_cfg, services, glossary, AppContext.BaseDirectory);
            _engine.Subtitle += ev => Dispatcher.BeginInvoke(() => OnSubtitle(ev));
            _engine.Latency += r => Dispatcher.BeginInvoke(() => AddLog(r.ToString()));
            _engine.Start();
            StartWatchdog();
            StartButton.IsEnabled = false;
            StopButton.IsEnabled = true;
            SubtitleList.Items.Clear();
        }
        catch (Exception ex)
        {
            _engine?.Dispose();
            _engine = null;
            Log.Error("启动失败：" + ex.Message);
            MessageBox.Show(this, ex.Message, "启动失败");
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => StopEngine();

    private void StopEngine()
    {
        _engine?.Dispose();
        _engine = null;
        StopWatchdog();
        StartButton.IsEnabled = true;
        StopButton.IsEnabled = false;
        _overlay.SetStatus("已停止 · 请把游戏的麦克风/语音输出改回真实设备，或重新开始");
    }

    private void Shutdown()
    {
        _timer.Stop();
        StopEngine();
        _hotkeys?.Dispose();
        _overlay.Close();
    }

    // ---------- 崩溃守护：主程序意外退出时恢复队友原声 ----------

    private void StartWatchdog()
    {
        try
        {
            var exe = Path.Combine(AppContext.BaseDirectory, "GVT.Watchdog.exe");
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

    // ---------- 快捷键 ----------

    private void OnKey(string name, bool down)
    {
        var h = _cfg.Hotkeys;
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

    private void SendEnabled_Click(object sender, RoutedEventArgs e)
    {
        if (_engine?.Send != null && _engine.Send.Enabled != SendEnabledCheck.IsChecked) _engine.ToggleSend();
    }

    private void ReceiveEnabled_Click(object sender, RoutedEventArgs e)
    {
        if (_engine?.Receive != null && _engine.Receive.Enabled != ReceiveEnabledCheck.IsChecked) _engine.ToggleReceive();
    }

    private void ListenCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ListenCombo.SelectedItem is ComboBoxItem && _engine != null)
            _engine.SetListenMode(Enum.Parse<ReceiveListenMode>(TagOf(ListenCombo)));
    }

    // ---------- 显示 ----------

    private void OnSubtitle(SubtitleEvent e)
    {
        _overlay.Add(e);
        if (e.Kind == SubtitleKind.Partial) return;
        var label = e.Kind switch
        {
            SubtitleKind.Source => "原文",
            SubtitleKind.Translation => "译文",
            SubtitleKind.Dropped => "跳过",
            SubtitleKind.Error => "错误",
            _ => "提示",
        };
        SubtitleList.Items.Add($"{e.At:HH:mm:ss} [{e.Channel}·{label}] {e.Text}");
        while (SubtitleList.Items.Count > 300) SubtitleList.Items.RemoveAt(0);
        SubtitleList.ScrollIntoView(SubtitleList.Items[^1]);
    }

    private void AddLog(string line)
    {
        LogList.Items.Add(line);
        while (LogList.Items.Count > 300) LogList.Items.RemoveAt(0);
        LogList.ScrollIntoView(LogList.Items[^1]);
    }

    private static string StageText(ChannelStage s, bool enabled) => !enabled ? "已关闭" : s switch
    {
        ChannelStage.Idle => "待机",
        ChannelStage.Listening => "正在听",
        ChannelStage.Processing => "识别/翻译中",
        ChannelStage.Speaking => "正在播报",
        ChannelStage.Error => "出错",
        _ => "已关闭",
    };

    private void RefreshStatus()
    {
        var s = _engine?.GetStatus();
        if (s == null || !s.Running)
        {
            SendState.Text = ReceiveState.Text = "未运行";
            MicLevel.Value = SourceLevel.Value = -60;
            return;
        }
        var send = (s.PttDown ? "● 录音中 · " : "") + StageText(s.SendStage, s.SendEnabled)
                   + (s.SendBacklog > 0.05 ? $" · 待发 {s.SendBacklog:F1}s" : "");
        var recv = StageText(s.ReceiveStage, s.ReceiveEnabled) + (s.HoldOriginal ? " · 正在听原声" : "")
                   + (s.ReceiveBacklog > 0.05 ? $" · 待播 {s.ReceiveBacklog:F1}s" : "");
        SendState.Text = send;
        ReceiveState.Text = recv;
        MicLevel.Value = Math.Max(-60, s.MicDb);
        SourceLevel.Value = Math.Max(-60, s.SourceDb);
        SendEnabledCheck.IsChecked = s.SendEnabled;
        ReceiveEnabledCheck.IsChecked = s.ReceiveEnabled;
        UsageText.Text = s.Usage;
        WarningText.Text = s.Warning ?? "";
        _overlay.SetStatus($"发送：{send}　|　接收：{recv}");
    }
}
