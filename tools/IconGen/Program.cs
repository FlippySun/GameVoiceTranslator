using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// 生成应用图标：品牌渐变底 + 两个错开的对话气泡（白色 “A” = 我说的英文，琥珀色 “文” = 我听到的中文）。
// 输出多尺寸 app.ico（PNG 压缩条目）和 256px 预览 app.png。
// 用法：IconGen <输出目录>

internal static class Program
{
    private static readonly int[] Sizes = { 16, 20, 24, 32, 40, 48, 64, 96, 128, 256 };

    [STAThread]
    private static int Main(string[] args)
    {
        var dir = args.Length > 0 ? args[0] : ".";
        Directory.CreateDirectory(dir);
        var pngs = Sizes.Select(s => (Size: s, Png: Render(s))).ToList();
        File.WriteAllBytes(Path.Combine(dir, "app.png"), pngs.Last().Png);
        WriteIco(Path.Combine(dir, "app.ico"), pngs);
        Console.WriteLine($"已生成 {Path.GetFullPath(Path.Combine(dir, "app.ico"))}（{string.Join(",", Sizes)}）");
        return 0;
    }

    private static byte[] Render(int size)
    {
        double k = size / 256.0;
        bool small = size <= 32;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(k, k));

            // 圆角渐变底
            var bg = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0), EndPoint = new Point(1, 1),
                GradientStops =
                {
                    new GradientStop(Color.FromRgb(0x38, 0xC6, 0xF4), 0),
                    new GradientStop(Color.FromRgb(0x6C, 0x7C, 0xFF), 0.55),
                    new GradientStop(Color.FromRgb(0xB0, 0x6C, 0xFF), 1),
                },
            };
            double inset = small ? 4 : 10, radius = small ? 52 : 58;
            dc.DrawRoundedRectangle(bg, null, new Rect(inset, inset, 256 - 2 * inset, 256 - 2 * inset), radius, radius);
            // 顶部高光
            var gloss = new LinearGradientBrush(Color.FromArgb(0x38, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90);
            dc.DrawRoundedRectangle(gloss, null, new Rect(inset, inset, 256 - 2 * inset, 120), radius, radius);

            // 后面的琥珀气泡（右上，尾巴朝右下）——队友 / 中文
            var amber = new SolidColorBrush(Color.FromRgb(0xFF, 0xB5, 0x47));
            dc.DrawGeometry(amber, null, Bubble(new Rect(small ? 102 : 108, small ? 40 : 46, small ? 118 : 106, small ? 96 : 86), tailRight: true));

            // 前面的白色气泡（左下，尾巴朝左下）——我 / 英文
            var shadow = new SolidColorBrush(Color.FromArgb(0x40, 0x1A, 0x10, 0x50));
            var front = new Rect(small ? 34 : 42, small ? 104 : 104, small ? 132 : 120, small ? 100 : 92);
            dc.DrawGeometry(shadow, null, Bubble(Rect.Offset(front, 0, 6), tailRight: false));
            dc.DrawGeometry(Brushes.White, null, Bubble(front, tailRight: false));

            if (!small)
            {
                Text(dc, "文", new Rect(108, 46, 106, 86), 58, Color.FromRgb(0x2A, 0x1C, 0x05), "Microsoft YaHei UI");
                Text(dc, "A", new Rect(42, 104, 120, 92), 66, Color.FromRgb(0x4B, 0x4F, 0xD8), "Segoe UI Variable Display");
            }
            dc.Pop();
        }

        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>圆角矩形 + 一个小尾巴的对话气泡。</summary>
    private static Geometry Bubble(Rect r, bool tailRight)
    {
        var body = new RectangleGeometry(r, r.Height * 0.3, r.Height * 0.3);
        var tail = new StreamGeometry();
        using (var g = tail.Open())
        {
            if (tailRight)
            {
                g.BeginFigure(new Point(r.Right - r.Width * 0.34, r.Bottom - 4), true, true);
                g.LineTo(new Point(r.Right - 4, r.Bottom + r.Height * 0.26), true, true);
                g.LineTo(new Point(r.Right - r.Width * 0.12, r.Bottom - 4), true, true);
            }
            else
            {
                g.BeginFigure(new Point(r.Left + r.Width * 0.14, r.Bottom - 4), true, true);
                g.LineTo(new Point(r.Left + 2, r.Bottom + r.Height * 0.26), true, true);
                g.LineTo(new Point(r.Left + r.Width * 0.38, r.Bottom - 4), true, true);
            }
        }
        return new CombinedGeometry(GeometryCombineMode.Union, body, tail);
    }

    private static void Text(DrawingContext dc, string s, Rect box, double em, Color color, string font)
    {
        var ft = new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily(font), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            em, new SolidColorBrush(color), 1.0);
        dc.DrawText(ft, new Point(box.X + (box.Width - ft.WidthIncludingTrailingWhitespace) / 2, box.Y + (box.Height - ft.Height) / 2));
    }

    private static void WriteIco(string path, List<(int Size, byte[] Png)> images)
    {
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write((short)0);
        w.Write((short)1);
        w.Write((short)images.Count);
        int offset = 6 + 16 * images.Count;
        foreach (var (size, png) in images)
        {
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0);
            w.Write((byte)0);
            w.Write((short)1);
            w.Write((short)32);
            w.Write(png.Length);
            w.Write(offset);
            offset += png.Length;
        }
        foreach (var (_, png) in images) w.Write(png);
    }
}
