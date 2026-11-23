using System.Drawing;
using System.Windows;
using Forms = System.Windows.Forms;

namespace GVT.App;

/// <summary>
/// 右下角托盘图标。双击打开主窗口；右键菜单：打开主窗口、开始/停止翻译、发送/中文播报开关、浮窗显示/隐藏、退出。
/// 菜单用深色配色，和主程序一致。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    public sealed class MenuState
    {
        public bool Running { get; init; }
        public bool SendOn { get; init; }
        public bool ReceiveOn { get; init; }
        public bool OverlayOn { get; init; }
    }

    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _power, _send, _recv, _overlay;

    public event Action? OpenRequested;
    public event Action? PowerRequested;
    public event Action? SendToggleRequested;
    public event Action? ReceiveToggleRequested;
    public event Action? OverlayToggleRequested;
    public event Action? ExitRequested;
    public Func<MenuState>? GetState { get; set; }

    public TrayIcon()
    {
        var menu = new Forms.ContextMenuStrip
        {
            Renderer = new DarkRenderer(),
            ShowImageMargin = false,
            ShowCheckMargin = true,
            Font = new Font("Microsoft YaHei UI", 9.5f),
            Padding = new Forms.Padding(2, 4, 2, 4),
        };
        var open = new Forms.ToolStripMenuItem("打开主窗口") { Font = new Font("Microsoft YaHei UI", 9.5f, System.Drawing.FontStyle.Bold) };
        open.Click += (_, _) => OpenRequested?.Invoke();
        _power = new Forms.ToolStripMenuItem("开始翻译");
        _power.Click += (_, _) => PowerRequested?.Invoke();
        _send = new Forms.ToolStripMenuItem("发送英文给队友") { ShortcutKeyDisplayString = "F8" };
        _send.Click += (_, _) => SendToggleRequested?.Invoke();
        _recv = new Forms.ToolStripMenuItem("中文播报") { ShortcutKeyDisplayString = "F11" };
        _recv.Click += (_, _) => ReceiveToggleRequested?.Invoke();
        _overlay = new Forms.ToolStripMenuItem("显示游戏内浮窗");
        _overlay.Click += (_, _) => OverlayToggleRequested?.Invoke();
        var exit = new Forms.ToolStripMenuItem("退出");
        exit.Click += (_, _) => ExitRequested?.Invoke();
        menu.Items.AddRange(new Forms.ToolStripItem[] { open, new Forms.ToolStripSeparator(), _power, _send, _recv, _overlay, new Forms.ToolStripSeparator(), exit });
        menu.Opening += (_, _) => Refresh();

        _icon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "游戏双译",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseDoubleClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) OpenRequested?.Invoke(); };
        _icon.BalloonTipClicked += (_, _) => OpenRequested?.Invoke();
    }

    private static Icon LoadIcon()
    {
        var res = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
        return res != null ? new Icon(res.Stream, 32, 32) : SystemIcons.Application;
    }

    /// <summary>运行状态变化时更新托盘提示文字。</summary>
    public void SetRunning(bool running) => _icon.Text = running ? "游戏双译 · 翻译中" : "游戏双译 · 未运行";

    public void ShowHint(string title, string text) => _icon.ShowBalloonTip(4000, title, text, Forms.ToolTipIcon.None);

    private void Refresh()
    {
        var s = GetState?.Invoke() ?? new MenuState();
        _power.Text = s.Running ? "停止翻译" : "开始翻译";
        _send.Checked = s.SendOn;
        _recv.Checked = s.ReceiveOn;
        _send.Enabled = _recv.Enabled = s.Running;
        _overlay.Checked = s.OverlayOn;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
    }

    /// <summary>深色菜单，配色取自主程序主题。</summary>
    private sealed class DarkRenderer : Forms.ToolStripProfessionalRenderer
    {
        private static readonly Color Bg = Color.FromArgb(0x1B, 0x1E, 0x25);
        private static readonly Color Hover = Color.FromArgb(0x23, 0x27, 0x31);
        private static readonly Color Border = Color.FromArgb(0x33, 0x38, 0x44);
        private static readonly Color Text = Color.FromArgb(0xEE, 0xF0, 0xF4);
        private static readonly Color TextDim = Color.FromArgb(0x6B, 0x71, 0x81);
        private static readonly Color Accent = Color.FromArgb(0x38, 0xC6, 0xF4);

        public DarkRenderer() : base(new Colors()) { RoundedEdges = false; }

        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Text : TextDim;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(Forms.ToolStripItemImageRenderEventArgs e)
        {
            var r = e.ImageRectangle;
            using var pen = new Pen(Accent, 2f) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            e.Graphics.DrawLines(pen, new[] { new PointF(r.Left + r.Width * 0.22f, r.Top + r.Height * 0.52f), new PointF(r.Left + r.Width * 0.42f, r.Top + r.Height * 0.72f), new PointF(r.Left + r.Width * 0.78f, r.Top + r.Height * 0.3f) });
        }

        protected override void OnRenderSeparator(Forms.ToolStripSeparatorRenderEventArgs e)
        {
            var y = e.Item.Height / 2;
            using var pen = new Pen(Border);
            e.Graphics.DrawLine(pen, 8, y, e.Item.Width - 8, y);
        }

        private sealed class Colors : Forms.ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground => Bg;
            public override Color MenuBorder => Border;
            public override Color MenuItemBorder => Hover;
            public override Color MenuItemSelected => Hover;
            public override Color MenuItemSelectedGradientBegin => Hover;
            public override Color MenuItemSelectedGradientEnd => Hover;
            public override Color ImageMarginGradientBegin => Bg;
            public override Color ImageMarginGradientMiddle => Bg;
            public override Color ImageMarginGradientEnd => Bg;
            public override Color CheckBackground => Bg;
            public override Color CheckSelectedBackground => Hover;
            public override Color CheckPressedBackground => Hover;
            public override Color SeparatorDark => Border;
            public override Color SeparatorLight => Bg;
        }
    }
}
