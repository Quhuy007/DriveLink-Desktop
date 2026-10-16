using Microsoft.Win32;
namespace DriveLink.Services;

public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Entry = "DriveLink.QuangHuy";
    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(Entry) is string text && !string.IsNullOrWhiteSpace(text);
    }
    public static string BuildCommand(string exe) => "\"" + Path.GetFullPath(exe) + "\" --background";
    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
        if (enabled)
        {
            var path = Environment.ProcessPath ?? throw new InvalidOperationException("Không xác định được vị trí ứng dụng.");
            if (!Path.GetFileName(path).Equals("DriveLink.exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Hãy chạy DriveLink.exe để bật khởi động cùng Windows.");
            var command = BuildCommand(path);
            if (command.Length > 260) throw new InvalidOperationException("Đường dẫn ứng dụng quá dài để đăng ký khởi động cùng Windows.");
            key.SetValue(Entry, command, RegistryValueKind.String);
        }
        else key.DeleteValue(Entry, false);
    }
}
