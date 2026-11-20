using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using GVT.App.Models;
using GVT.Core.Config;

namespace GVT.App;

/// <summary>
/// 游戏内字幕浮窗。平时鼠标穿透、不抢焦点；按编辑快捷键（默认 F6）后可拖动、拖右下角改大小、滚轮改字号，
/// 再按一次锁定并保存位置。需要游戏使用“无边框全屏”。
/// </summary>
public partial class OverlayWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;

    private readonly AppConfig _cfg;
    private readonly TimelineStore _timeline;
    private readonly OverlayView _view = new();
    private readonly DispatcherTimer _fadeTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private IntPtr _hwnd;

    public bool EditMode { get; private set; }
    public event Action<bool>? EditModeChanged;

    public OverlayWindow(AppConfig cfg, TimelineStore timeline)
    {
        InitializeComponent();
        _cfg = cfg;
        _timeline = timeline;
        DataContext = _view;
        Lines.ItemsSource = timeline.Recent;
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            ApplyClickThrough(true);
        };
        MouseLeftButtonDown += (_, e) => { if (EditMode && e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        MouseWheel += OnWheel;
        _fadeTimer.Tick += (_, _) => FadeOld();
        _fadeTimer.Start();
        ApplySettings();
    }

    public void ApplySettings()
    {
        var o = _cfg.Overlay;
        Left = o.Left;
        Top = o.Top;
        Width = Math.Max(MinWidth, o.Width);
        Height = Math.Max(MinHeight, o.Height);
        _view.FontSize = o.FontSize;
        _view.ShowOriginal = o.ShowOriginal;
        Panel.Background = new SolidColorBrush(Color.FromArgb((byte)(Math.Clamp(o.Opacity, 0, 1) * 255), 0x0C, 0x0D, 0x11));
    }

    /// <summary>内容超出时始终贴底显示最新的句子，最旧的从顶部被挤掉。</summary>
    private void Lines_ScrollChanged(object sender, System.Windows.Controls.ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0) LinesScroll.ScrollToEnd();
    }

    // ---------------- 编辑模式 ----------------

    public void ToggleEditMode() => SetEditMode(!EditMode);

    public void SetEditMode(bool on)
    {
        if (EditMode == on) return;
        EditMode = on;
        ApplyClickThrough(!on);
        EditFrame.Visibility = Grip.Visibility = EditHint.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        EditHintText.Text = $"拖动移动 · 右下角改大小 · 滚轮改字号（{_cfg.Overlay.FontSize:F0}） · 再按 {_cfg.Hotkeys.OverlayEdit} 锁定";
        if (on)
        {
            Activate();
        }
        else
        {
            _cfg.Overlay.Left = Math.Round(Left);
            _cfg.Overlay.Top = Math.Round(Top);
            _cfg.Overlay.Width = Math.Round(Width);
            _cfg.Overlay.Height = Math.Round(Height);
        }
        EditModeChanged?.Invoke(on);
    }

    private void Grip_DragDelta(object sender, DragDeltaEventArgs e)
    {
        Width = Math.Max(MinWidth, Width + e.HorizontalChange);
        Height = Math.Max(MinHeight, Height + e.VerticalChange);
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (!EditMode) return;
        _cfg.Overlay.FontSize = Math.Clamp(_cfg.Overlay.FontSize + (e.Delta > 0 ? 1 : -1), 12, 40);
        _view.FontSize = _cfg.Overlay.FontSize;
        EditHintText.Text = $"拖动移动 · 右下角改大小 · 滚轮改字号（{_cfg.Overlay.FontSize:F0}） · 再按 {_cfg.Hotkeys.OverlayEdit} 锁定";
        (Owner as MainWindow ?? Application.Current.MainWindow as MainWindow)?.SyncOverlayFont();
    }

    private void ApplyClickThrough(bool passThrough)
    {
        if (_hwnd == IntPtr.Zero) return;
        var ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64() | WS_EX_LAYERED | WS_EX_TOOLWINDOW;
        ex = passThrough ? ex | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE : ex & ~WS_EX_TRANSPARENT & ~WS_EX_NOACTIVATE;
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex));
    }

    // ---------------- 状态与淡出 ----------------

    public void SetStatus(bool running, bool sendOn, bool recording, bool recvOn, bool holdOriginal)
    {
        MeDot.Tag = recording;
        MeDot.Fill = recording ? (Brush)FindResource("B.Me") : running && sendOn ? (Brush)FindResource("B.Ok") : new SolidColorBrush(Color.FromArgb(0x55, 255, 255, 255));
        MeState.Text = !running ? "未开始" : recording ? "我 · 录音中" : sendOn ? "我 · 发送开" : "我 · 发送关";
        MateDot.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        MateDot.Fill = holdOriginal ? (Brush)FindResource("B.Mate") : running && recvOn ? (Brush)FindResource("B.Ok") : new SolidColorBrush(Color.FromArgb(0x55, 255, 255, 255));
        MateState.Text = !running ? "" : holdOriginal ? "队友 · 听原声" : recvOn ? "队友 · 播报开" : "队友 · 播报关";
    }

    /// <summary>超过停留时间的字幕在 1 秒内淡出并移除；编辑模式下保留，方便看效果。</summary>
    private void FadeOld()
    {
        if (EditMode) { foreach (var i in _timeline.Recent) i.Opacity = 1; return; }
        var hold = _cfg.Overlay.HoldSeconds;
        foreach (var item in _timeline.Recent.ToList())
        {
            if (item.IsBusy) continue;
            var age = (DateTime.Now - item.Time).TotalSeconds;
            if (age > hold + 1) _timeline.Recent.Remove(item);
            else if (age > hold) item.Opacity = Math.Max(0, hold + 1 - age);
        }
        Panel.Visibility = Visibility.Visible;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr value);

    private sealed class OverlayView : INotifyPropertyChanged
    {
        private double _fontSize = 18;
        private bool _showOriginal = true;
        public double FontSize { get => _fontSize; set { _fontSize = value; Raise(nameof(FontSize)); Raise(nameof(SmallFontSize)); } }
        public double SmallFontSize => Math.Max(11, Math.Round(_fontSize * 0.68));
        public bool ShowOriginal { get => _showOriginal; set { _showOriginal = value; Raise(nameof(ShowOriginal)); } }
        public event PropertyChangedEventHandler? PropertyChanged;
        private void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
