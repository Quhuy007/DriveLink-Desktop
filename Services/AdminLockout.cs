using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;
namespace DriveLink.Services;

/// <summary>
/// Giới hạn số lần nhập sai mật khẩu quản trị Cloud trên máy này:
/// sau mỗi 3 lần sai phải chờ 15 phút; tổng cộng 9 lần sai thì máy bị khóa hẳn, không thử lại được nữa.
/// Trạng thái lưu ở hai nơi (tệp mã hóa DPAPI và Registry); lấy giá trị nghiêm ngặt hơn nên xóa một nơi không mở khóa được.
/// Đăng nhập quản trị thành công (khi chưa bị khóa hẳn) sẽ đặt lại bộ đếm.
/// </summary>
public sealed class AdminLockout
{
    public const int AttemptsPerRound = 3, MaximumAttempts = 9;
    public static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(15);
    public static string StorageFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DriveLink");
    public static string RegistryPath { get; set; } = @"Software\QuangHuy\DriveLink";
    private const string ValueName = "AdminGuard";
    private static readonly byte[] Entropy = "DriveLink.AdminGuard.v1"u8.ToArray();

    public sealed class State
    {
        public int RoundFailures { get; set; }
        public int TotalFailures { get; set; }
        public DateTimeOffset? CooldownUntil { get; set; }
        public bool Locked { get; set; }
    }

    private readonly Func<DateTimeOffset> now;
    public State Current { get; private set; }
    private string FilePath => Path.Combine(StorageFolder, "admin-guard.dat");

    public AdminLockout(Func<DateTimeOffset>? clock = null)
    {
        now = clock ?? (() => DateTimeOffset.UtcNow);
        Current = Merge(ReadFile(), ReadRegistry());
    }

    /// <summary>Có được thử mật khẩu lúc này không; nếu không, kèm lời giải thích cho người dùng.</summary>
    public (bool Allowed, string Message) Check()
    {
        if (Current.Locked) return (false, "Máy này đã bị khóa mở quản trị sau 9 lần nhập sai mật khẩu. Liên hệ chủ ứng dụng để được mở lại.");
        if (Current.CooldownUntil is { } until && until > now())
        {
            var minutes = Math.Max(1, (int)Math.Ceiling((until - now()).TotalMinutes));
            return (false, $"Đã nhập sai {AttemptsPerRound} lần. Chờ khoảng {minutes} phút rồi thử lại (còn {MaximumAttempts - Current.TotalFailures} lần trước khi máy bị khóa hẳn).");
        }
        return (true, "");
    }

    /// <summary>Ghi nhận một lần sai; trả về lời nhắc số lần còn lại.</summary>
    public string RecordFailure()
    {
        Current.TotalFailures++;
        Current.RoundFailures++;
        string message;
        if (Current.TotalFailures >= MaximumAttempts) { Current.Locked = true; message = "Đã nhập sai 9 lần. Máy này bị khóa mở quản trị."; }
        else if (Current.RoundFailures >= AttemptsPerRound) { Current.RoundFailures = 0; Current.CooldownUntil = now() + Cooldown; message = $"Đã nhập sai {AttemptsPerRound} lần liên tiếp. Chờ 15 phút rồi thử lại."; }
        else message = $"Còn {AttemptsPerRound - Current.RoundFailures} lần thử trước khi phải chờ 15 phút.";
        Save();
        SecurityLog.Write("admin-password-failed", $"Nhập sai mật khẩu quản trị (tổng {Current.TotalFailures}/{MaximumAttempts}){(Current.Locked ? " · máy bị khóa" : "")}");
        return message;
    }

    public void RecordSuccess()
    {
        if (Current.Locked) return;
        Current = new State();
        Save();
    }

    /// <summary>Cloud báo địa chỉ này đã bị chặn: khóa luôn trên máy.</summary>
    public void Lock(string reason)
    {
        Current.Locked = true;
        Save();
        SecurityLog.Write("admin-locked", reason);
    }

    private void Save()
    {
        var clear = JsonSerializer.SerializeToUtf8Bytes(Current);
        try
        {
            var protectedData = ProtectedData.Protect(clear, Entropy, DataProtectionScope.CurrentUser);
            try { Directory.CreateDirectory(StorageFolder); File.WriteAllBytes(FilePath, protectedData); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            try { using var key = Registry.CurrentUser.CreateSubKey(RegistryPath, true); key.SetValue(ValueName, protectedData, RegistryValueKind.Binary); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    private State? ReadFile()
    {
        try { return File.Exists(FilePath) ? Decode(File.ReadAllBytes(FilePath)) : null; }
        catch (IOException) { return null; }
    }
    private static State? ReadRegistry()
    {
        try { using var key = Registry.CurrentUser.OpenSubKey(RegistryPath); return key?.GetValue(ValueName) is byte[] data ? Decode(data) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
    }
    private static State? Decode(byte[] data)
    {
        try
        {
            var clear = ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
            try { return JsonSerializer.Deserialize<State>(clear); } finally { CryptographicOperations.ZeroMemory(clear); }
        }
        // Dữ liệu bị sửa hoặc hỏng: coi như có người can thiệp, khóa luôn.
        catch (Exception ex) when (ex is CryptographicException or JsonException) { return new State { Locked = true, TotalFailures = MaximumAttempts }; }
    }
    private static State Merge(State? a, State? b)
    {
        if (a is null) return b ?? new State();
        if (b is null) return a;
        return new State
        {
            Locked = a.Locked || b.Locked,
            TotalFailures = Math.Max(a.TotalFailures, b.TotalFailures),
            RoundFailures = Math.Max(a.RoundFailures, b.RoundFailures),
            CooldownUntil = a.CooldownUntil > b.CooldownUntil ? a.CooldownUntil : b.CooldownUntil ?? a.CooldownUntil,
        };
    }
}
