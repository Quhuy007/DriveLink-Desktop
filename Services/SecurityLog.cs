using System.Text.Json;
namespace DriveLink.Services;

/// <summary>
/// Nhật ký bảo mật trên máy (mỗi dòng một sự kiện JSON): cấp/thu hồi quyền tạm, đổi cấu hình OAuth, cài cập nhật,
/// chặn bản cập nhật sai chữ ký, khóa quản trị, chia sẻ công khai, cảnh báo tệp chạy được.
/// Không bao giờ ghi token, mật khẩu hay email đầy đủ. Lỗi ghi nhật ký không được làm hỏng thao tác chính.
/// </summary>
public static class SecurityLog
{
    private static readonly object gate = new();
    public const long MaximumBytes = 1_000_000;
    public static string Root { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DriveLink");
    public static string FilePath => Path.Combine(Root, "security-log.jsonl");

    public static void Write(string kind, string message)
    {
        try
        {
            lock (gate)
            {
                Directory.CreateDirectory(Root);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > MaximumBytes) File.Move(FilePath, FilePath + ".1", true);
                var line = JsonSerializer.Serialize(new { time = DateTimeOffset.Now.ToString("o"), kind, message });
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static IReadOnlyList<string> ReadRecent(int count)
    {
        try { lock (gate) return File.Exists(FilePath) ? File.ReadLines(FilePath).TakeLast(count).ToList() : []; }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }
}

/// <summary>Nhận diện tệp có thể chạy mã khi mở, để cảnh báo trước khi tải về hoặc mở thư mục chứa tệp do người khác gửi.</summary>
public static class RiskyFiles
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".msix", ".msixbundle", ".appx", ".appxbundle", ".appinstaller", ".bat", ".cmd", ".com", ".scr", ".pif", ".cpl",
        ".ps1", ".psm1", ".psd1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".hta", ".lnk", ".url", ".reg", ".jar", ".dll", ".sys",
        ".iso", ".img", ".vhd", ".vhdx", ".chm", ".xll", ".docm", ".xlsm", ".pptm",
    };
    public static bool IsExecutable(string name) => Extensions.Contains(Path.GetExtension(name.Trim().TrimEnd('.', ' ')));
}
