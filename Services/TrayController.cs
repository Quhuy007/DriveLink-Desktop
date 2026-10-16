using Forms = System.Windows.Forms;
namespace DriveLink.Services;

public sealed class TrayController : IDisposable
{
    private readonly Forms.NotifyIcon icon;
    private readonly System.Drawing.Icon image;
    public TrayController(Action show, Action settings, Action exit)
    {
        using var source = typeof(AppBrand).Assembly.GetManifestResourceStream("DriveLink.WinIcon")!;
        using var original = new System.Drawing.Icon(source);
        image = (System.Drawing.Icon)original.Clone();
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Mở DriveLink", null, (_, _) => show());
        menu.Items.Add("Cài đặt", null, (_, _) => settings());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Thoát hoàn toàn", null, (_, _) => exit());
        icon = new Forms.NotifyIcon { Icon = image, Text = AppBrand.WindowTitle, ContextMenuStrip = menu };
        icon.DoubleClick += (_, _) => show();
    }
    public bool Visible { get => icon.Visible; set => icon.Visible = value; }
    public void Dispose() { icon.Visible = false; icon.ContextMenuStrip?.Dispose(); icon.Dispose(); image.Dispose(); }
}
