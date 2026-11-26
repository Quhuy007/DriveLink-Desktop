using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
namespace DriveLink.Ui;

/// <summary>
/// Chế độ nhẹ cho máy ít RAM: tắt hiệu ứng mờ/phát sáng, nền chuyển động và hiệu ứng động, giảm tốc độ khung hình,
/// và trả lại bộ nhớ cho Windows khi app ẩn vào khay hoặc thu nhỏ. Bố cục và chức năng giữ nguyên.
/// "auto" bật chế độ nhẹ khi máy có từ 4 GB RAM trở xuống.
/// </summary>
public static class PerformanceMode
{
    public const string Auto = "auto", Lite = "lite", Full = "full";
    public const long AutoLiteThresholdBytes = 4L * 1024 * 1024 * 1024 + 512L * 1024 * 1024; // Máy "4 GB" thường báo hơi dưới hoặc trên 4 GB.
    private static bool installed;

    public static bool IsLite { get; private set; }

    /// <summary>Quyết định chế độ từ cài đặt. Phải gọi trước khi mở cửa sổ đầu tiên.</summary>
    public static bool Resolve(string? setting, long? physicalMemory = null) => setting switch
    {
        Lite => true,
        Full => false,
        _ => (physicalMemory ?? TotalPhysicalMemory()) is > 0 and var total && total <= AutoLiteThresholdBytes,
    };

    public static void Configure(string? setting)
    {
        IsLite = Resolve(setting);
        if (!IsLite || installed) return;
        installed = true;
        // Vẽ bằng CPU: không nạp trình điều khiển đồ họa (Direct3D) vào app, tiết kiệm khoảng 80–150 MB tùy card đồ họa.
        // Không còn hiệu ứng mờ hay chuyển động nền nên vẽ bằng CPU vẫn mượt.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        // Hiệu ứng động còn lại (vòng tiến độ, đèn trạng thái) chạy tối đa 20 khung hình/giây.
        Timeline.DesiredFrameRateProperty.OverrideMetadata(typeof(Timeline), new FrameworkPropertyMetadata { DefaultValue = 20 });
        // Mọi phần tử, kể cả trong menu và cửa sổ phụ: bỏ lớp mờ trang trí (bóng/viền sáng) và hiệu ứng đổ bóng.
        EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) => Strip(sender as FrameworkElement)), true);
    }

    /// <summary>Lớp có BlurEffect chỉ để trang trí (bóng mờ, quầng sáng) nên ẩn hẳn; DropShadowEffect thì bỏ hiệu ứng, giữ phần tử.</summary>
    public static void Strip(FrameworkElement? element)
    {
        if (element?.Effect is null) return;
        if (element.Effect is BlurEffect) element.Visibility = Visibility.Collapsed;
        else element.Effect = null;
    }

    /// <summary>Dọn rác và trả bộ nhớ chưa dùng cho Windows (gọi khi cửa sổ ẩn/thu nhỏ hoặc vừa tải xong).</summary>
    public static void TrimMemory()
    {
        try
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true, true);
            GC.WaitForPendingFinalizers();
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            SetProcessWorkingSetSize(process.Handle, -1, -1);
        }
        catch (Exception) { /* Chỉ là tối ưu; lỗi không ảnh hưởng chức năng. */ }
    }

    public static long TotalPhysicalMemory()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) ? (long)status.TotalPhys : 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetProcessWorkingSetSize(nint process, nint minimum, nint maximum);
}
