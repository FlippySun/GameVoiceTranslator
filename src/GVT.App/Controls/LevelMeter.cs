using System.Windows;
using System.Windows.Media;

namespace GVT.App.Controls;

/// <summary>分段电平条：Level 0..1，带峰值保持的平滑下落。</summary>
public sealed class LevelMeter : FrameworkElement
{
    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level), typeof(double), typeof(LevelMeter), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Brush), typeof(LevelMeter), new FrameworkPropertyMetadata(Brushes.DeepSkyBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush Off = new SolidColorBrush(Color.FromRgb(0x23, 0x27, 0x31));

    public double Level { get => (double)GetValue(LevelProperty); set => SetValue(LevelProperty, value); }
    public Brush Accent { get => (Brush)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    public int Bars { get; set; } = 28;

    private double _shown;

    /// <summary>由外部定时器调用：dBFS → 0..1，上升立即、下降平滑。</summary>
    public void Push(float db)
    {
        double v = Math.Clamp((db + 60) / 60.0, 0, 1);
        _shown = v > _shown ? v : _shown * 0.82 + v * 0.18;
        Level = _shown;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        const double gap = 3;
        double bw = (w - gap * (Bars - 1)) / Bars;
        int lit = (int)Math.Round(Level * Bars);
        for (int i = 0; i < Bars; i++)
        {
            // 中间高两边低的柱形，看起来像声波
            double t = Math.Abs(i - (Bars - 1) / 2.0) / (Bars / 2.0);
            double bh = h * (0.45 + 0.55 * (1 - t * t));
            var rect = new Rect(i * (bw + gap), (h - bh) / 2, bw, bh);
            dc.DrawRoundedRectangle(i < lit ? Accent : Off, null, rect, bw / 2, bw / 2);
        }
    }
}
