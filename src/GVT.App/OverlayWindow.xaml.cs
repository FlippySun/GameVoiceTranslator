using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using GVT.Core.Config;
using GVT.Core.Pipeline;

namespace GVT.App;

public sealed class OverlayLine
{
    public string Text { get; init; } = "";
    public Brush Brush { get; init; } = Brushes.White;
    public double FontSize { get; init; }
    public string Key { get; init; } = "";
}

/// <summary>游戏内字幕小窗：置顶、鼠标穿透、不抢焦点。需要游戏使用“无边框全屏”。</summary>
public partial class OverlayWindow : Window
{
    private readonly OverlayConfig _cfg;
    private readonly ObservableCollection<OverlayLine> _lines = new();

    public OverlayWindow(OverlayConfig cfg)
    {
        InitializeComponent();
        _cfg = cfg;
        Left = cfg.Left;
        Top = cfg.Top;
        Width = cfg.Width;
        Opacity = cfg.Opacity;
        Lines.ItemsSource = _lines;
        SourceInitialized += (_, _) => MakeClickThrough();
    }

    private void MakeClickThrough()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        const int GWL_EXSTYLE = -20;
        const long WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
        var ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));
    }

    public void SetStatus(string text) => StatusLine.Text = text;

    public void Add(SubtitleEvent e)
    {
        if (e.Kind == SubtitleKind.Partial && string.IsNullOrWhiteSpace(e.Text)) return;
        var (prefix, brush) = (e.Channel, e.Kind) switch
        {
            (_, SubtitleKind.Error) => ("⚠ ", (Brush)new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x80))),
            (_, SubtitleKind.Dropped) => ("· ", (Brush)new SolidColorBrush(Color.FromRgb(0x9A, 0xA4, 0xAA))),
            ("发送", SubtitleKind.Translation) => ("我 → ", (Brush)new SolidColorBrush(Color.FromRgb(0x8F, 0xD3, 0xFF))),
            ("发送", _) => ("我：", (Brush)new SolidColorBrush(Color.FromRgb(0xC8, 0xD6, 0xE0))),
            ("接收", SubtitleKind.Translation) => ("收到 → ", (Brush)new SolidColorBrush(Color.FromRgb(0xFF, 0xE0, 0x8A))),
            ("接收", _) => ("收到：", (Brush)new SolidColorBrush(Color.FromRgb(0xD8, 0xD0, 0xB8))),
            _ => ("", (Brush)Brushes.White),
        };
        var key = $"{e.Channel}#{e.UtteranceId}#{(e.Kind is SubtitleKind.Partial or SubtitleKind.Source ? "src" : e.Kind.ToString())}";
        var size = e.Kind == SubtitleKind.Translation ? _cfg.FontSize : _cfg.FontSize * 0.8;
        var line = new OverlayLine { Text = prefix + e.Text, Brush = brush, FontSize = size, Key = key };

        // 同一句的中间结果原地更新
        var existing = _lines.FirstOrDefault(l => l.Key == key && e.UtteranceId > 0);
        if (existing != null) _lines[_lines.IndexOf(existing)] = line;
        else _lines.Add(line);
        while (_lines.Count > _cfg.MaxLines * 2) _lines.RemoveAt(0);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr value);
}
