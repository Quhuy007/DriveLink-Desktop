using System.Security.Cryptography;
using System.Text.Json;
namespace DriveLink.Services;

public sealed class SecureStore
{
    private readonly string file;
    private readonly string preferencesFile;
    // Khóa phụ riêng của DriveLink cho DPAPI: công cụ đánh cắp phổ thông gọi DPAPI không kèm khóa này sẽ không giải mã được.
    // Không thay thế được lớp bảo vệ token bằng Windows Hello (TokenVault), chỉ nâng thêm một bậc.
    private static readonly byte[] Entropy = "DriveLink.SecureStore.v2"u8.ToArray();
    public string Folder { get; }
    public SecureStore(string? directory = null)
    {
        Folder = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DriveLink");
        Directory.CreateDirectory(Folder);
        file = Path.Combine(Folder, "accounts.dat");
        preferencesFile = Path.Combine(Folder, "preferences.dat");
    }
    public SavedState Load()
    {
        var state = Read<SavedState>(file) ?? new();
        var preferences = Read<Preferences>(preferencesFile);
        if (preferences is not null) { state.Settings = preferences.Settings; state.SpacePreferences = preferences.Spaces; state.Cloud = preferences.Cloud; }
        return state;
    }
    public void Save(SavedState state)
    {
        Write(file, state);
        Write(preferencesFile, new Preferences { Settings = state.Settings, Spaces = state.SpacePreferences, Cloud = state.Cloud });
    }
    private sealed class Preferences
    {
        public UserSettings Settings { get; set; } = new();
        public Dictionary<string, SpacePreference> Spaces { get; set; } = [];
        public CloudConnection Cloud { get; set; } = new();
    }
    private static T? Read<T>(string path)
    {
        if (!File.Exists(path)) return default;
        var data = File.ReadAllBytes(path);
        byte[] clear;
        // Tệp do bản cũ ghi không có khóa phụ: vẫn đọc được, lần lưu tiếp theo sẽ ghi lại kèm khóa phụ.
        try { clear = ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser); }
        catch (CryptographicException) { clear = ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser); }
        try { return JsonSerializer.Deserialize<T>(clear); }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }
    private static void Write<T>(string path, T value)
    {
        var clear = JsonSerializer.SerializeToUtf8Bytes(value);
        try
        {
            var encrypted = ProtectedData.Protect(clear, Entropy, DataProtectionScope.CurrentUser);
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, encrypted);
            File.Move(temp, path, true);
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }
}
