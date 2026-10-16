using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace DriveLink.Services;

public sealed class OAuthUnlockException(string message) : InvalidOperationException(message);

public sealed class LockedOAuthPayload
{
    public int Version { get; set; } = 1;
    public int Iterations { get; set; } = 600000;
    public string Salt { get; set; } = "";
    public string Nonce { get; set; } = "";
    public string Tag { get; set; } = "";
    public string Ciphertext { get; set; } = "";
}
public static class OAuthVault
{
    public const string Format = "aes256gcm-pbkdf2-v1";
    public const int MaximumEnvelopeBytes = 100000;
    public static void ValidateNewPassword(string password)
    {
        if (password.Length < 12 || password.Length > 256 || string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException("Mật khẩu cấu hình cần từ 12 đến 256 ký tự. Hãy dùng một cụm từ khó đoán.");
    }
    public static string Encrypt(string json, string password)
    {
        ValidateNewPassword(password);
        var config = OAuthConfigParser.Parse(json);
        var salt = RandomNumberGenerator.GetBytes(16); var nonce = RandomNumberGenerator.GetBytes(12);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, 600000, HashAlgorithmName.SHA256, 32);
        var clear = Encoding.UTF8.GetBytes(json); var cipher = new byte[clear.Length]; var tag = new byte[16];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(nonce, clear, cipher, tag, Encoding.UTF8.GetBytes("DriveLink/OAuth/v1/" + config.ClientId));
            return JsonSerializer.Serialize(new LockedOAuthPayload { Salt = Convert.ToBase64String(salt), Nonce = Convert.ToBase64String(nonce), Tag = Convert.ToBase64String(tag), Ciphertext = Convert.ToBase64String(cipher) });
        }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(clear); }
    }
    public static void ValidateEnvelope(string envelope)
    {
        _ = ReadEnvelope(envelope);
    }
    private static (LockedOAuthPayload Payload, byte[] Salt, byte[] Nonce, byte[] Tag, byte[] Cipher) ReadEnvelope(string envelope)
    {
        try
        {
            if (Encoding.UTF8.GetByteCount(envelope) > MaximumEnvelopeBytes) throw new FormatException();
            var payload = JsonSerializer.Deserialize<LockedOAuthPayload>(envelope) ?? throw new FormatException();
            if (payload.Version != 1 || payload.Iterations != 600000) throw new FormatException();
            var salt = Convert.FromBase64String(payload.Salt); var nonce = Convert.FromBase64String(payload.Nonce);
            var tag = Convert.FromBase64String(payload.Tag); var cipher = Convert.FromBase64String(payload.Ciphertext);
            if (salt.Length != 16 || nonce.Length != 12 || tag.Length != 16 || cipher.Length == 0 || cipher.Length > OAuthConfigParser.MaximumBytes) throw new FormatException();
            return (payload, salt, nonce, tag, cipher);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException or NullReferenceException)
        { throw new InvalidOperationException("Gói cấu hình mã hóa không hợp lệ."); }
    }
    public static OAuthConfig Decrypt(CloudRevision revision, string password)
    {
        if (revision.PayloadFormat != Format) throw new InvalidOperationException("Bản cũ chưa có mật khẩu riêng. Quản trị viên cần đăng lại JSON bằng bản app mới.");
        if (password.Length is < 1 or > 256) throw new OAuthUnlockException("Mật khẩu OAuth cần từ 1 đến 256 ký tự.");
        SupabaseConfigService.VerifyPayloadHash(revision);
        var (_, salt, nonce, tag, cipher) = ReadEnvelope(revision.ConfigJson);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, 600000, HashAlgorithmName.SHA256, 32);
        var clear = new byte[cipher.Length];
        try
        {
            using var aes = new AesGcm(key, 16);
            try { aes.Decrypt(nonce, cipher, tag, clear, Encoding.UTF8.GetBytes("DriveLink/OAuth/v1/" + revision.ClientId)); }
            catch (CryptographicException) { throw new OAuthUnlockException("Mật khẩu OAuth không đúng hoặc dữ liệu đã bị thay đổi. Hãy nhập lại."); }
            var result = OAuthConfigParser.Parse(Encoding.UTF8.GetString(clear));
            if (result.ClientId != revision.ClientId) throw new InvalidOperationException("Client ID trong cấu hình không khớp.");
            return result;
        }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(clear); }
    }
}
