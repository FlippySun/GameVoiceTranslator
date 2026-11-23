using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using GVT.App.Controls;
using GVT.Core.Audio;
using GVT.Core.Config;
using GVT.Core.Pipeline;
using GVT.Core.Setup;

namespace GVT.App;

/// <summary>提示卡里的一行：图标、标题、说明、可选的跳转按钮。</summary>
public sealed class NoticeRow
{
    public string Glyph { get; init; } = "";
    public Brush GlyphBrush { get; init; } = Brushes.White;
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public string ActionText { get; init; } = "";
    public Action? Action { get; init; }
    public Visibility ActionVisibility => Action == null ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>
/// 窗口外壳相关：新用户引导（开始前检查、一键安装环境、API Key 跳转）、花费上限、关闭方式、托盘。
/// </summary>
public partial class MainWindow
{
    private const string ApiKeyUrl = "https://bailian.console.aliyun.com/cn-beijing/settings/api-key";

    private TrayIcon? _tray;
    private bool _exiting;
    private bool _installing;
    /// <summary>当前提示卡是“开始前检查”时，修好一项就重新检查一次。</summary>
    private bool _noticeIsPreflight;
    private DispatcherTimerHolder? _noticeHide;

    // =====================================================================
    // 初始化
    // =====================================================================

    private void InitShell()
    {
        PopupBudget.Changed += SetBudget;
        DiagBudget.Changed += SetBudget;
        UpdateBudgetTexts();
        (_cfg.General.CloseAction switch { CloseAction.MinimizeToTray => CloseTray, CloseAction.Exit => CloseExit, _ => CloseAsk }).IsChecked = true;
        foreach (var box in new[] { MicCombo, SendOutCombo, SourceCombo, HeadphoneCombo })
            box.SelectionChanged += (_, _) => { if (!_loading && !_installing) { ApplyFormToConfig(); RecheckNotice(); } };
        PreviewKeyDown += OnShellKeyDown;
        Application.Current.SessionEnding += (_, _) => _exiting = true;
        if (!Environment.GetCommandLineArgs().Contains("--shot")) InitTray();
    }

    /// <summary>另一个实例启动时会触发这个事件：把窗口叫出来。</summary>
    public void ListenForShowRequests(string eventName)
    {
        var ev = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
        var t = new Thread(() =>
        {
            while (ev.WaitOne()) Dispatcher.BeginInvoke(ShowFromTray);
        }) { IsBackground = true, Name = "ShowRequests" };
        t.Start();
    }

    // =====================================================================
    // API Key
    // =====================================================================

    private void CreateKey_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(ApiKeyUrl) { UseShellExecute = true }); }
        catch (Exception ex) { Toast("打不开浏览器：" + ex.Message); }
    }

    private void GoToApiKey()
    {
        NavSetup.IsChecked = true;
        Dispatcher.BeginInvoke(() => { ApiKeyBox.Focus(); ApiKeyBox.BringIntoView(); }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // =====================================================================
    // 安装环境
    // =====================================================================

    private void UpdateEnvStatus(EnvironmentCheck.DriverState d)
    {
        bool missing = !d.AllInstalled;
        InstallEnvBtn.Visibility = missing || _installing ? Visibility.Visible : Visibility.Collapsed;
        RefreshDevicesBtn.Visibility = missing || _installing ? Visibility.Collapsed : Visibility.Visible;
        EnvMissingBox.Visibility = missing ? Visibility.Visible : Visibility.Collapsed;
        if (missing)
        {
            var names = new List<string>();
            if (!d.SendCable) names.Add("VB-CABLE（把英文译音送进游戏）");
            if (!d.RecvCable) names.Add("Hi-Fi Cable（接收队友声音）");
            EnvMissingText.Text = $"这台电脑还缺少虚拟声卡：{string.Join("、", names)}。\n点右上角“安装环境”自动安装并设置好，会弹出一次管理员授权，大约 1 分钟。";
        }
    }

    private async void InstallEnv_Click(object sender, RoutedEventArgs e) => await InstallEnvironmentAsync();

    private async Task InstallEnvironmentAsync()
    {
        if (_installing) return;
        if (_engine != null) { Toast("请先停止翻译，再安装环境"); return; }
        _installing = true;
        InstallEnvBtn.IsEnabled = false;
        InstallEnvText.Text = "正在安装…";
        InstallEnvIcon.Text = "";
        var spin = new RotateTransform();
        InstallEnvIcon.RenderTransformOrigin = new Point(0.5, 0.5);
        InstallEnvIcon.RenderTransform = spin;
        spin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever });
        Toast("请在弹出的窗口里点“是”，允许安装虚拟声卡");
        DriverInstaller.Result result;
        try { result = await DriverInstaller.InstallAsync(_cfg); }
        catch (Exception ex) { result = new(DriverInstaller.Outcome.Failed, ex.Message); }
        finally
        {
            spin.BeginAnimation(RotateTransform.AngleProperty, null);
            InstallEnvIcon.Text = "";
            InstallEnvText.Text = "安装环境";
            InstallEnvBtn.IsEnabled = true;
            _installing = false;
        }
        Log.Info("安装环境：" + result.Message);
        _loading = true;
        LoadDevices();
        _loading = false;

        switch (result.Outcome)
        {
            case DriverInstaller.Outcome.Cancelled:
                Toast(result.Message);
                break;
            case DriverInstaller.Outcome.Installed:
                ShowInstalledNotice(result.Message);
                break;
            case DriverInstaller.Outcome.NeedsRestart:
                ShowNotice("warn", "还差一步：重启电脑", result.Message, new List<NoticeRow>(), preflight: false);
                break;
            default:
                ShowNotice("danger", "安装没有完成", result.Message, new List<NoticeRow>
                {
                    new() { Glyph = "", GlyphBrush = Res("B.Text2"), Title = "再试一次", Detail = "会再弹出一次管理员授权。", ActionText = "重新安装", Action = () => _ = InstallEnvironmentAsync() },
                }, preflight: false);
                break;
        }
    }

    private void ShowInstalledNotice(string message)
    {
        var rows = new List<NoticeRow>
        {
            new() { Glyph = "", GlyphBrush = Res("B.Me"), Title = "游戏 / 语音软件的麦克风", Detail = "选 CABLE Output (VB-Audio Virtual Cable)" },
            new() { Glyph = "", GlyphBrush = Res("B.Mate"), Title = "游戏 / 语音软件的语音输出", Detail = "选 Hi-Fi Cable Input (VB-Audio Hi-Fi Cable)，游戏音效仍用你的耳机",
                    ActionText = "查看设置方法", Action = () => { NavSetup.IsChecked = true; Dispatcher.BeginInvoke(() => GuideCard.BringIntoView(), System.Windows.Threading.DispatcherPriority.Loaded); } },
        };
        if (!SecretStore.HasApiKey)
            rows.Add(new() { Glyph = "", GlyphBrush = Res("B.Warn"), Title = "还没有配置 API Key", Detail = "保存后就可以开始翻译了。", ActionText = "去配置", Action = GoToApiKey });
        ShowNotice("ok", "环境已装好", message + "最后在游戏和语音软件里选好下面两项：", rows, preflight: false);
    }

    // =====================================================================
    // 开始前检查
    // =====================================================================

    /// <summary>检查开始翻译的条件；有问题就弹提示卡并返回 false。</summary>
    private bool Preflight()
    {
        var issues = EnvironmentCheck.Run(_cfg, SecretStore.HasApiKey);
        if (issues.Count == 0) { if (_noticeIsPreflight) HideNotice(); return true; }
        ShowNotice("danger", "还不能开始翻译", $"发现 {issues.Count} 个问题，处理好后再点开始：", issues.Select(ToRow).ToList(), preflight: true);
        return false;
    }

    private NoticeRow ToRow(EnvIssue i) => i.Fix switch
    {
        EnvFix.ApiKey => new() { Glyph = "", GlyphBrush = Res("B.Danger"), Title = i.Title, Detail = i.Detail, ActionText = "去配置", Action = GoToApiKey },
        EnvFix.InstallDrivers => new() { Glyph = "", GlyphBrush = Res("B.Danger"), Title = i.Title, Detail = i.Detail, ActionText = "安装环境", Action = () => { GoToDevices(); _ = InstallEnvironmentAsync(); } },
        _ => new() { Glyph = "", GlyphBrush = Res("B.Warn"), Title = i.Title, Detail = i.Detail, ActionText = "去选择", Action = GoToDevices },
    };

    private void GoToDevices()
    {
        NavSetup.IsChecked = true;
        Dispatcher.BeginInvoke(() =>
        {
            DeviceCard.BringIntoView();
            DeviceCard.BeginAnimation(OpacityProperty, new DoubleAnimationUsingKeyFrames
            {
                KeyFrames = { new LinearDoubleKeyFrame(0.55, KeyTime.FromPercent(0.25)), new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.5)), new LinearDoubleKeyFrame(0.55, KeyTime.FromPercent(0.75)), new LinearDoubleKeyFrame(1, KeyTime.FromPercent(1)) },
                Duration = TimeSpan.FromMilliseconds(900),
            });
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>提示卡是开始前检查时：配置有变化就重新检查，修好的项目去掉，全部修好显示“可以开始了”。</summary>
    private void RecheckNotice()
    {
        if (!_noticeIsPreflight || NoticeBox.Visibility != Visibility.Visible) return;
        var issues = EnvironmentCheck.Run(_cfg, SecretStore.HasApiKey);
        if (issues.Count > 0)
        {
            NoticeSub.Text = $"还有 {issues.Count} 个问题，处理好后再点开始：";
            NoticeList.ItemsSource = issues.Select(ToRow).ToList();
            return;
        }
        ShowNotice("ok", "都准备好了", "可以开始翻译了。", new List<NoticeRow>
        {
            new() { Glyph = "", GlyphBrush = Res("B.Ok"), Title = "开始翻译", Detail = $"之后也可以按控制台的开始按钮。", ActionText = "开始", Action = () => { NavHome.IsChecked = true; StartEngine(); } },
        }, preflight: false);
        _noticeHide = DispatcherTimerHolder.Once(TimeSpan.FromSeconds(8), HideNotice);
    }

    // =====================================================================
    // 提示卡
    // =====================================================================

    private Brush Res(string key) => (Brush)FindResource(key);

    /// <summary>kind：danger / warn / ok。</summary>
    private void ShowNotice(string kind, string title, string sub, List<NoticeRow> rows, bool preflight)
    {
        _noticeHide?.Stop();
        _noticeIsPreflight = preflight;
        var (glyph, fg, bg) = kind switch
        {
            "ok" => ("", "B.Ok", "B.OkSoft"),
            "warn" => ("", "B.Warn", "B.MateSoft"),
            _ => ("", "B.Danger", "B.DangerSoft"),
        };
        NoticeIcon.Text = glyph;
        NoticeIcon.Foreground = Res(fg);
        NoticeIconBox.Background = Res(bg);
        NoticeTitle.Text = title;
        NoticeSub.Text = sub;
        NoticeSub.Visibility = string.IsNullOrEmpty(sub) ? Visibility.Collapsed : Visibility.Visible;
        NoticeList.ItemsSource = rows;
        NoticeList.Visibility = rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        bool wasVisible = NoticeBox.Visibility == Visibility.Visible;
        NoticeBox.Visibility = Visibility.Visible;
        if (!wasVisible)
        {
            NoticeBox.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
            NoticeSlide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(18, 0, TimeSpan.FromMilliseconds(300)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
    }

    private void HideNotice()
    {
        _noticeHide?.Stop();
        _noticeIsPreflight = false;
        if (NoticeBox.Visibility != Visibility.Visible) return;
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180));
        fade.Completed += (_, _) => { if (NoticeBox.Opacity < 0.05) NoticeBox.Visibility = Visibility.Collapsed; };
        NoticeBox.BeginAnimation(OpacityProperty, fade);
        NoticeSlide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, 12, TimeSpan.FromMilliseconds(180)));
    }

    private void NoticeClose_Click(object sender, RoutedEventArgs e) => HideNotice();

    private void NoticeAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: NoticeRow row }) row.Action?.Invoke();
    }

    // =====================================================================
    // 花费上限
    // =====================================================================

    private void SideCard_Click(object sender, MouseButtonEventArgs e)
    {
        PopupBudget.Value = _cfg.Budget.SessionBudgetYuan;
        UpdateBudgetTexts();
        BudgetPopup.IsOpen = !BudgetPopup.IsOpen;
    }

    private void SetBudget(double yuan)
    {
        _cfg.Budget.SessionBudgetYuan = yuan;
        _cfg.Save();
        PopupBudget.Value = DiagBudget.Value = yuan;
        UpdateBudgetTexts();
        _engine?.ResumeIfBudgetAllows();
        if (_engine?.BudgetPaused == false) WarnText.Text = "";
        Toast(yuan <= 0 ? "单次花费上限：不限" : $"单次花费上限：¥{yuan:0.##}");
    }

    private void UpdateBudgetTexts()
    {
        var limit = BudgetPicker.Describe(_cfg.Budget.SessionBudgetYuan);
        SideBudget.Text = $"上限 {limit} · 点击修改";
        DiagBudget.Value = _cfg.Budget.SessionBudgetYuan;
        var cost = _engine?.Usage.CostYuan ?? 0;
        PopupCost.Text = _engine == null ? "当前没有在翻译。" : $"这次已花费 ¥{cost:F3}。";
    }

    // =====================================================================
    // 关闭方式与托盘
    // =====================================================================

    private void InitTray()
    {
        _tray = new TrayIcon
        {
            GetState = () =>
            {
                var s = _engine?.GetStatus();
                return new TrayIcon.MenuState
                {
                    Running = _engine != null,
                    SendOn = s?.SendEnabled ?? _cfg.Send.Enabled,
                    ReceiveOn = s?.ReceiveEnabled ?? _cfg.Receive.Enabled,
                    OverlayOn = _overlay.IsVisible,
                };
            },
        };
        _tray.OpenRequested += () => Dispatcher.BeginInvoke(ShowFromTray);
        _tray.PowerRequested += () => Dispatcher.BeginInvoke(() =>
        {
            if (_engine != null) { StopEngine(); return; }
            StartEngine();
            if (_engine == null) ShowFromTray(); // 没能开始：把窗口叫出来看提示卡
        });
        _tray.SendToggleRequested += () => Dispatcher.BeginInvoke(() => _engine?.ToggleSend());
        _tray.ReceiveToggleRequested += () => Dispatcher.BeginInvoke(() => _engine?.ToggleReceive());
        _tray.OverlayToggleRequested += () => Dispatcher.BeginInvoke(() =>
        {
            _cfg.Overlay.Visible = !_overlay.IsVisible;
            if (_cfg.Overlay.Visible) _overlay.Show(); else _overlay.Hide();
            OverlayVisible.IsChecked = _cfg.Overlay.Visible;
            _cfg.Save();
        });
        _tray.ExitRequested += () => Dispatcher.BeginInvoke(ExitApp);
        _tray.SetRunning(false);
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_exiting || _tray == null) { Shutdown(); return; }
        e.Cancel = true;
        RequestClose();
    }

    private void RequestClose()
    {
        switch (_cfg.General.CloseAction)
        {
            case CloseAction.MinimizeToTray: HideToTray(); break;
            case CloseAction.Exit: ExitApp(); break;
            default: ShowCloseDialog(); break;
        }
    }

    private void ShowCloseDialog()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        ChoiceTray.IsChecked = true;
        ChoiceRemember.IsChecked = false;
        CloseDialog.Visibility = Visibility.Visible;
        CloseDialog.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        CloseScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        CloseScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        ChoiceTray.Focus();
    }

    private void HideCloseDialog() => CloseDialog.Visibility = Visibility.Collapsed;

    private void CloseCancel_Click(object sender, RoutedEventArgs e) => HideCloseDialog();

    private void CloseConfirm_Click(object sender, RoutedEventArgs e)
    {
        var action = ChoiceExit.IsChecked == true ? CloseAction.Exit : CloseAction.MinimizeToTray;
        if (ChoiceRemember.IsChecked == true)
        {
            _cfg.General.CloseAction = action;
            _cfg.Save();
            (action == CloseAction.Exit ? CloseExit : CloseTray).IsChecked = true;
        }
        HideCloseDialog();
        if (action == CloseAction.Exit) ExitApp(); else HideToTray();
    }

    private void OnShellKeyDown(object sender, KeyEventArgs e)
    {
        if (CloseDialog.Visibility != Visibility.Visible) return;
        if (e.Key == Key.Escape) { HideCloseDialog(); e.Handled = true; }
        else if (e.Key == Key.Enter) { CloseConfirm_Click(this, new RoutedEventArgs()); e.Handled = true; }
    }

    private void CloseAction_Click(object sender, RoutedEventArgs e)
    {
        _cfg.General.CloseAction = CloseTray.IsChecked == true ? CloseAction.MinimizeToTray : CloseExit.IsChecked == true ? CloseAction.Exit : CloseAction.Ask;
        _cfg.Save();
    }

    private void HideToTray()
    {
        if (_tray == null) { ExitApp(); return; }
        BudgetPopup.IsOpen = false;
        Hide();
        if (!_cfg.General.TrayHintShown)
        {
            _tray.ShowHint("游戏双译仍在后台运行", _engine != null ? "翻译和快捷键照常工作。双击右下角的图标可以重新打开。" : "双击右下角的图标可以重新打开，右键可以开始翻译或退出。");
            _cfg.General.TrayHintShown = true;
            _cfg.Save();
        }
    }

    private void ShowFromTray()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true; Topmost = false; // 确保来到最前面
        Focus();
    }

    private void ExitApp()
    {
        _exiting = true;
        Close();
    }

    private void DisposeTray()
    {
        _tray?.Dispose();
        _tray = null;
    }

    /// <summary>一次性计时器。</summary>
    private sealed class DispatcherTimerHolder
    {
        private readonly System.Windows.Threading.DispatcherTimer _t;
        private DispatcherTimerHolder(TimeSpan after, Action a)
        {
            _t = new System.Windows.Threading.DispatcherTimer { Interval = after };
            _t.Tick += (_, _) => { _t.Stop(); a(); };
            _t.Start();
        }
        public static DispatcherTimerHolder Once(TimeSpan after, Action a) => new(after, a);
        public void Stop() => _t.Stop();
    }
}

public partial class MainWindow
{
    /// <summary>调试截图：新用户引导相关的几个状态（模拟没装声卡、没配 Key）。</summary>
    private async Task ShootShellAsync(string dir)
    {
        string P(string n) => System.IO.Path.Combine(dir, n + ".png");
        NavSetup.IsChecked = true;
        UpdateKeyStatus(false);
        UpdateEnvStatus(new EnvironmentCheck.DriverState(false, true));
        await Task.Delay(600);
        SavePng(this, P("setup-missing"));
        UpdateKeyStatus();
        LoadDevices();

        NavHome.IsChecked = true;
        var fakeCfg = new AppConfig();
        var issues = EnvironmentCheck.Run(fakeCfg, hasApiKey: false, render: new[] { "扬声器 (USB Audio)" }, capture: new[] { "麦克风 (USB Audio)" }, defaultRender: "扬声器 (USB Audio)", defaultCapture: "麦克风 (USB Audio)");
        ShowNotice("danger", "还不能开始翻译", $"发现 {issues.Count} 个问题，处理好后再点开始：", issues.Select(ToRow).ToList(), preflight: false);
        await Task.Delay(600);
        SavePng(this, P("notice"));
        ShowInstalledNotice("虚拟声卡已装好，软件里的设备也已自动选好。");
        await Task.Delay(400);
        SavePng(this, P("notice-installed"));
        HideNotice();

        ShowCloseDialog();
        await Task.Delay(500);
        SavePng(this, P("close"));
        HideCloseDialog();

        BudgetPopup.IsOpen = true;
        await Task.Delay(500);
        if (BudgetPopup.Child is FrameworkElement pop) SaveElementPng(pop, P("budget"));
        BudgetPopup.IsOpen = false;
    }

    private static void SaveElementPng(FrameworkElement el, string path)
    {
        var dpi = VisualTreeHelper.GetDpi(el);
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(el.ActualWidth * dpi.DpiScaleX), (int)(el.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle((Brush)Application.Current.FindResource("B.Bg"), null, new Rect(0, 0, el.ActualWidth, el.ActualHeight));
            dc.DrawRectangle(new VisualBrush(el), null, new Rect(0, 0, el.ActualWidth, el.ActualHeight));
        }
        bmp.Render(dv);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var fs = System.IO.File.Create(path);
        enc.Save(fs);
    }
}
