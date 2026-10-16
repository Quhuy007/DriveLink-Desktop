using System.Security.Cryptography;
using System.Text.Json;
namespace DriveLink.Services;

public sealed class SecureStore
{
    private readonly string file;
    private readonly string preferencesFile;
    public SecureStore(string? directory = null)
    {
        var root = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DriveLink");
        Directory.CreateDirectory(root);
        file = Path.Combine(root, "accounts.dat");
        preferencesFile = Path.Combine(root, "preferences.dat");
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
        var clear = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
        try { return JsonSerializer.Deserialize<T>(clear); }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }
    private static void Write<T>(string path, T value)
    {
        var clear = JsonSerializer.SerializeToUtf8Bytes(value);
        try
        {
            var encrypted = ProtectedData.Protect(clear, null, DataProtectionScope.CurrentUser);
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, encrypted);
            File.Move(temp, path, true);
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }
}
