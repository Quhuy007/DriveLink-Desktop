using System.Windows;
namespace DriveLink.Ui;

/// <summary>
/// Giao diện sáng/tối, chọn một lần lúc mở app (đổi trong Cài đặt, áp dụng sau khi mở lại).
/// Bảng màu sáng (Ui/Palette.Light.xaml) và màu dùng trong code đều đi qua cùng quy tắc <see cref="Map"/>:
/// nền tối thành nền sáng, chữ sáng thành chữ tối, lớp kính trắng mờ thành lớp mực mờ, màu nhấn đậm lên để dễ đọc trên nền trắng.
/// </summary>
public static class ThemeChoice
{
    public const string Dark = "dark", Light = "light";
    public static bool IsLight { get; private set; }

    /// <summary>Đọc lựa chọn trước khi nạp tài nguyên giao diện. Tham số --light/--dark dùng cho bản xem thử.</summary>
    public static void Load(string[] args)
    {
        if (args.Contains("--light")) { IsLight = true; return; }
        if (args.Contains("--dark") || args.Any(a => a.StartsWith("--ui-preview", StringComparison.OrdinalIgnoreCase))) { IsLight = false; return; }
        try { IsLight = new Services.SecureStore().Load().Settings.Theme == Light; }
        catch (Exception) { IsLight = false; }
    }

    public static void Set(bool light) => IsLight = light;

    /// <summary>Màu (ARGB) của giao diện tối → màu tương ứng của giao diện sáng. Giống hàm tạo Palette.Light.xaml.</summary>
    public static uint Map(uint argb)
    {
        if (!IsLight) return argb;
        int a = (int)(argb >> 24), r = (int)(argb >> 16) & 255, g = (int)(argb >> 8) & 255, b = (int)argb & 255;
        int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        var chroma = (max - min) / 255d;
        var (h, l, s) = ToHls(r / 255d, g / 255d, b / 255d);
        if (max < 16) return Pack((int)Math.Round(a * 0.35), 0, 0, 0);
        if (chroma < 0.25)
        {
            if (a < 0xF0) return l > 0.5 ? Pack((int)Math.Round(a * 0.30), 0x10, 0x18, 0x28) : Pack(Math.Min(a, 0xD9), 255, 255, 255);
            var l2 = l < 0.2 ? 0.955 + (0.2 - l) * 0.15 : l > 0.8 ? 0.10 + (1 - l) * 0.3 : 1 - l;
            return FromHls(a, h, Math.Min(l2, 0.985), Math.Min(s, 0.35));
        }
        if (a < 0xF0) return FromHls((int)Math.Round(a * 0.6), h, Math.Min(l, 0.5), s);
        return FromHls(a, h, l > 0.45 ? 0.45 : l, s);
    }

    private static uint Pack(int a, int r, int g, int b) => (uint)(a << 24 | r << 16 | g << 8 | b);
    private static uint FromHls(int a, double h, double l, double s)
    {
        var (r, g, b) = FromHlsRgb(h, l, s);
        return Pack(a, (int)Math.Round(r * 255), (int)Math.Round(g * 255), (int)Math.Round(b * 255));
    }

    // Cùng công thức với colorsys của Python để màu trong code khớp bảng màu sáng.
    private static (double H, double L, double S) ToHls(double r, double g, double b)
    {
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        var l = (min + max) / 2;
        if (min == max) return (0, l, 0);
        var s = l <= 0.5 ? (max - min) / (max + min) : (max - min) / (2 - max - min);
        double rc = (max - r) / (max - min), gc = (max - g) / (max - min), bc = (max - b) / (max - min);
        var h = r == max ? bc - gc : g == max ? 2 + rc - bc : 4 + gc - rc;
        h = (h / 6) % 1; if (h < 0) h += 1;
        return (h, l, s);
    }
    private static (double, double, double) FromHlsRgb(double h, double l, double s)
    {
        if (s == 0) return (l, l, l);
        var m2 = l <= 0.5 ? l * (1 + s) : l + s - l * s;
        var m1 = 2 * l - m2;
        return (V(m1, m2, h + 1 / 3d), V(m1, m2, h), V(m1, m2, h - 1 / 3d));
        static double V(double m1, double m2, double hue)
        {
            hue %= 1; if (hue < 0) hue += 1;
            if (hue < 1 / 6d) return m1 + (m2 - m1) * hue * 6;
            if (hue < 0.5) return m2;
            if (hue < 2 / 3d) return m1 + (m2 - m1) * (2 / 3d - hue) * 6;
            return m1;
        }
    }
}
