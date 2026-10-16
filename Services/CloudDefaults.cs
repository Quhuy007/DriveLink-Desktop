using System.Security.Cryptography;
using System.Text.Json;
namespace DriveLink.Services;

public static class CloudDefaults
{
    // Public account identity. The administrator password is never bundled.
    public const string AdminEmail = "admin_account_verify@drivelink.private.com";
    // This packing hides plaintext strings, not a secret vault: the public bootstrap
    // identity must be recoverable on a new computer. Auth/RLS protect cloud access.
    public static CloudConnection? Read()
    {
        if (string.IsNullOrEmpty(CloudBuildSettings.Payload)) return null;
        using var aes = Aes.Create(); aes.Key = Convert.FromBase64String(CloudBuildSettings.Key); aes.IV = Convert.FromBase64String(CloudBuildSettings.IV);
        var bytes = Convert.FromBase64String(CloudBuildSettings.Payload);
        var clear = aes.DecryptCbc(bytes, aes.IV);
        try
        {
            var profile = JsonSerializer.Deserialize<CloudConnection>(clear) ?? throw new InvalidOperationException("Cloud mặc định không hợp lệ.");
            return SupabaseConfigService.ValidateConnection(profile.ProjectUrl, profile.PublishableKey);
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }
}
