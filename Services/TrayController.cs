using System.Drawing;
using Forms = System.Windows.Forms;
namespace DriveLink.Services;

/// <summary>Biểu tượng khay hệ thống: menu kiểu tối cùng tông với app, và thông báo Windows (bấm vào để mở app).</summary>
public sealed class TrayController : IDisposable
{
    private readonly Forms.NotifyIcon icon;
    private readonly Icon image;
    private Action? balloonClick;

    public TrayController(Action show, Action settings, Action exit, Action? queue = null)
    {
        using var source = typeof(AppBrand).Assembly.GetManifestResourceStream("DriveLink.WinIcon")!;
        using var original = new Icon(source);
        image = (Icon)original.Clone();
        var menu = new Forms.ContextMenuStrip
        {
            Renderer = new DarkRenderer(),
            BackColor = DarkColors.Menu, ForeColor = DarkColors.Text,
            ShowImageMargin = false, Padding = new Forms.Padding(4),
            Font = new Font("Segoe UI", 9.5f),
        };
        Add(menu, "Mở DriveLink", show, bold: true);
        if (queue is not null) Add(menu, "Hàng đợi & Hoạt động", queue);
        Add(menu, "Cài đặt", settings);
        menu.Items.Add(new Forms.ToolStripSeparator());
        Add(menu, "Thoát hoàn toàn", exit);
        icon = new Forms.NotifyIcon { Icon = image, Text = AppBrand.WindowTitle, ContextMenuStrip = menu };
        icon.DoubleClick += (_, _) => show();
        icon.BalloonTipClicked += (_, _) => balloonClick?.Invoke();
    }

    private static void Add(Forms.ContextMenuStrip menu, string text, Action action, bool bold = false)
    {
        var item = new Forms.ToolStripMenuItem(text, null, (_, _) => action()) { ForeColor = DarkColors.Text, Padding = new Forms.Padding(6, 5, 18, 5) };
        if (bold) item.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        menu.Items.Add(item);
    }

    public bool Visible { get => icon.Visible; set => icon.Visible = value; }

    /// <summary>Thông báo Windows ở góc màn hình (vào Trung tâm thông báo). Hiện biểu tượng khay tạm thời nếu đang ẩn.</summary>
    public void Notify(string title, string text, Action? onClick = null)
    {
        balloonClick = onClick;
        if (!icon.Visible) icon.Visible = true;
        icon.ShowBalloonTip(5000, title, text.Length > 240 ? text[..240] + "…" : text, Forms.ToolTipIcon.Info);
    }

    public void Dispose() { icon.Visible = false; icon.ContextMenuStrip?.Dispose(); icon.Dispose(); image.Dispose(); }

    private static class DarkColors
    {
        public static readonly Color Menu = Color.FromArgb(0x16, 0x1B, 0x2C);
        public static readonly Color Hover = Color.FromArgb(0x2A, 0x3A, 0x6B);
        public static readonly Color Border = Color.FromArgb(0x33, 0x3D, 0x5C);
        public static readonly Color Text = Color.FromArgb(0xF2, 0xF5, 0xFB);
        public static readonly Color Separator = Color.FromArgb(0x2C, 0x34, 0x4D);
    }

    private sealed class DarkRenderer() : Forms.ToolStripProfessionalRenderer(new DarkTable())
    {
        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e) { e.TextColor = DarkColors.Text; base.OnRenderItemText(e); }
        protected override void OnRenderSeparator(Forms.ToolStripSeparatorRenderEventArgs e)
        {
            using var pen = new Pen(DarkColors.Separator);
            var y = e.Item.Height / 2;
            e.Graphics.DrawLine(pen, 8, y, e.Item.Width - 8, y);
        }
    }

    private sealed class DarkTable : Forms.ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => DarkColors.Menu;
        public override Color MenuBorder => DarkColors.Border;
        public override Color MenuItemBorder => DarkColors.Hover;
        public override Color MenuItemSelected => DarkColors.Hover;
        public override Color MenuItemSelectedGradientBegin => DarkColors.Hover;
        public override Color MenuItemSelectedGradientEnd => DarkColors.Hover;
        public override Color ImageMarginGradientBegin => DarkColors.Menu;
        public override Color ImageMarginGradientMiddle => DarkColors.Menu;
        public override Color ImageMarginGradientEnd => DarkColors.Menu;
        public override Color SeparatorDark => DarkColors.Separator;
        public override Color SeparatorLight => DarkColors.Separator;
    }
}
