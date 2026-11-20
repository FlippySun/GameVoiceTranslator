using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GVT.Core.Config;

namespace GVT.App;

/// <summary>浮窗编辑模式下贴在浮窗旁边的样式工具栏：背景不透明度、字号、三类文字颜色。</summary>
public partial class OverlayToolbar : Window
{
    /// <summary>为游戏画面挑选的高对比色：都偏亮，便于在复杂场景上辨认。</summary>
    private static readonly string[] Palette =
    {
        "#FFFFFF", "#A6E6FF", "#7FB8FF", "#C9A8FF", "#FF9ED8", "#FFE2A8", "#FFB36B", "#9EF0B8", "#E8F57A", "#B8BEC9", "#7C8494",
    };

    private readonly AppConfig _cfg;
    private bool _loading = true; // InitializeComponent 期间滑块会触发 ValueChanged

    /// <summary>任意样式改动后触发（浮窗与设置页同步）。</summary>
    public event Action? StyleChanged;
    public event Action? DoneRequested;

    public OverlayToolbar(AppConfig cfg)
    {
        InitializeComponent();
        _cfg = cfg;
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is Border or StackPanel or Grid) DragMove(); };
        LoadFromConfig();
    }

    public void LoadFromConfig()
    {
        _loading = true;
        var o = _cfg.Overlay;
        OpacitySlider.Value = o.Opacity;
        FontSlider.Value = o.FontSize;
        ShowOriginalCheck.IsChecked = o.ShowOriginal;
        DoneText.Text = $"完成并锁定（{_cfg.Hotkeys.OverlayEdit}）";
        BuildSwatches(MeSwatches, o.MeTextColor, c => o.MeTextColor = c);
        BuildSwatches(MateSwatches, o.MateTextColor, c => o.MateTextColor = c);
        BuildSwatches(OrigSwatches, o.OriginalTextColor, c => o.OriginalTextColor = c);
        UpdateTexts();
        _loading = false;
    }

    private void BuildSwatches(WrapPanel panel, string selected, Action<string> apply)
    {
        panel.Children.Clear();
        foreach (var hex in Palette)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            bool isSel = string.Equals(hex, selected, StringComparison.OrdinalIgnoreCase);
            var swatch = new Border
            {
                Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Margin = new Thickness(0, 0, 8, 0),
                Background = new SolidColorBrush(color),
                BorderBrush = isSel ? Brushes.White : new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)),
                BorderThickness = new Thickness(isSel ? 2.5 : 1),
                Cursor = Cursors.Hand,
                ToolTip = hex,
            };
            if (isSel)
                swatch.Child = new TextBlock
                {
                    Text = "✓", FontSize = 11, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0x12, 0x18)),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                };
            swatch.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                apply(hex);
                BuildSwatches(panel, hex, apply);
                Notify();
            };
            panel.Children.Add(swatch);
        }
    }

    private void Changed(object sender, RoutedEventArgs e)
    {
        UpdateTexts();
        if (_loading || ShowOriginalCheck == null) return;
        var o = _cfg.Overlay;
        o.Opacity = Math.Round(OpacitySlider.Value, 2);
        o.FontSize = Math.Round(FontSlider.Value);
        o.ShowOriginal = ShowOriginalCheck.IsChecked == true;
        Notify();
    }

    private void UpdateTexts()
    {
        if (OpacityText == null || FontText == null) return;
        OpacityText.Text = $"{OpacitySlider.Value * 100:F0}%";
        FontText.Text = $"{FontSlider.Value:F0}";
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        var d = new OverlayConfig();
        var o = _cfg.Overlay;
        o.Opacity = d.Opacity;
        o.FontSize = d.FontSize;
        o.ShowOriginal = d.ShowOriginal;
        o.MeTextColor = d.MeTextColor;
        o.MateTextColor = d.MateTextColor;
        o.OriginalTextColor = d.OriginalTextColor;
        LoadFromConfig();
        Notify();
    }

    private void Done_Click(object sender, RoutedEventArgs e) => DoneRequested?.Invoke();

    private void Notify() => StyleChanged?.Invoke();

    /// <summary>贴在浮窗下方；下方放不下时放到上方。</summary>
    public void PlaceNear(Window overlay)
    {
        UpdateLayout();
        var area = SystemParameters.WorkArea;
        double w = ActualWidth > 0 ? ActualWidth : 540, h = ActualHeight > 0 ? ActualHeight : 230;
        double left = Math.Clamp(overlay.Left, area.Left, Math.Max(area.Left, area.Right - w));
        double below = overlay.Top + overlay.ActualHeight + 4;
        Left = left;
        Top = below + h <= area.Bottom ? below : Math.Max(area.Top, overlay.Top - h - 4);
    }
}
