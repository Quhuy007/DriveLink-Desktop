using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
namespace DriveLink.Ui;

/// <summary>Màu dùng chung cho các cửa sổ phụ dựng bằng code, khớp với giao diện kính tối của cửa sổ chính.</summary>
public static class Theme
{
    private static SolidColorBrush Solid(uint argb)
    {
        argb = ThemeChoice.Map(argb);
        var brush = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        brush.Freeze(); return brush;
    }
    public static readonly SolidColorBrush Ink = Solid(0xFFF2F5FB);
    public static readonly SolidColorBrush Muted = Solid(0xFFA6B0C4);
    public static readonly SolidColorBrush Faint = Solid(0xFF717B93);
    public static readonly SolidColorBrush Accent = Solid(0xFF5B8CFF);
    public static readonly SolidColorBrush Success = Solid(0xFF4ADE80);
    public static readonly SolidColorBrush Danger = Solid(0xFFF87171);
    public static readonly SolidColorBrush Warning = Solid(0xFFFBBF24);
    public static readonly SolidColorBrush Info = Solid(0xFF9DB8FF);
    public static readonly SolidColorBrush InfoSoft = Solid(0x295B8CFF);
    public static readonly SolidColorBrush InfoBorder = Solid(0x665B8CFF);
    public static readonly SolidColorBrush DangerSoft = Solid(0x2EF87171);
    public static readonly SolidColorBrush Field = Solid(0x14FFFFFF);
    public static readonly SolidColorBrush FieldBorder = Solid(0x33FFFFFF);

    /// <summary>Nền cửa sổ: xanh đêm pha ánh tím nhẹ, cùng tông với nền cực quang của cửa sổ chính.</summary>
    public static readonly Brush WindowBackground = CreateBackground();
    private static Brush CreateBackground()
    {
        var brush = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(1, 1) };
        brush.GradientStops.Add(new(Rgb(0xFF131B33), 0));
        brush.GradientStops.Add(new(Rgb(0xFF0B101D), 0.55));
        brush.GradientStops.Add(new(Rgb(0xFF16102B), 1));
        brush.Freeze(); return brush;
    }
    public static Color Rgb(uint argb) { argb = ThemeChoice.Map(argb); return Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb); }

    /// <summary>Áp nền tối, chữ sáng và thanh tiêu đề tối của Windows cho một cửa sổ phụ.</summary>
    public static void Apply(Window window)
    {
        window.Background = WindowBackground;
        window.Foreground = Ink;
        DarkTitleBar(window);
    }

    private const int DwmUseImmersiveDarkMode = 20, DwmBorderColor = 34, DwmCaptionColor = 35, DwmTextColor = 36;
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    /// <summary>Windows 11 cho đổi màu thanh tiêu đề; Windows cũ hơn bỏ qua các lệnh này mà không lỗi.</summary>
    public static void DarkTitleBar(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0) { window.SourceInitialized += (_, _) => DarkTitleBar(window); return; }
        int on = ThemeChoice.IsLight ? 0 : 1, caption = ThemeChoice.IsLight ? 0x00FBF7F4 : 0x00321B13, text = ThemeChoice.IsLight ? 0x00261912 : 0x00FBF5F2, border = ThemeChoice.IsLight ? 0x00E2DAD4 : 0x00503A2E; // COLORREF dạng 0x00BBGGRR.
        DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref on, sizeof(int));
        DwmSetWindowAttribute(handle, DwmCaptionColor, ref caption, sizeof(int));
        DwmSetWindowAttribute(handle, DwmTextColor, ref text, sizeof(int));
        DwmSetWindowAttribute(handle, DwmBorderColor, ref border, sizeof(int));
    }
}
