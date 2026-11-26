using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using Windows.Security.Credentials;
using Windows.Security.Cryptography;
namespace DriveLink.Services;

/// <summary>Token Google đang khóa: người dùng chưa xác nhận Windows Hello hoặc đã bấm hủy.</summary>
public sealed class TokenLockedException(string message) : InvalidOperationException(message);

public interface ITokenKeyProvider
{
    /// <summary>Tiền tố ghi vào giá trị đã mã hóa, để biết dùng khóa nào khi giải mã.</summary>
    string Scheme { get; }
    Task<bool> IsAvailableAsync();
    Task<byte[]> GetKeyAsync(CancellationToken ct);
}

/// <summary>
/// Mã hóa token Google (AES-256-GCM) trước khi lưu vào accounts.dat. Khóa không nằm trong tệp đó:
/// với Windows Hello, khóa được tạo từ chữ ký của khóa phần cứng (TPM) chỉ dùng được sau khi người dùng xác nhận;
/// khi không có Hello, khóa ngẫu nhiên nằm riêng trong Registry, mã hóa DPAPI kèm khóa phụ.
/// Giá trị cũ chưa mã hóa (bản 1.1.x trở về trước) vẫn đọc được và được mã hóa lại ở lần lưu tiếp theo.
/// </summary>
public sealed class TokenVault(ITokenKeyProvider preferred, ITokenKeyProvider fallback)
{
    private const string Prefix = "dlv1:";
    private ITokenKeyProvider? chosen;
    public static TokenVault Shared { get; set; } = new(new HelloKeyProvider(), new LocalKeyProvider());

    public static bool IsProtected(string value) => value.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>Phương thức đang dùng để bảo vệ token mới, để hiển thị trong Cài đặt.</summary>
    public async Task<string> DescribeAsync() => (await ChooseAsync()).Scheme == HelloKeyProvider.SchemeName ? "Windows Hello" : "Khóa riêng trên máy (không có Windows Hello)";

    private async Task<ITokenKeyProvider> ChooseAsync() => chosen ??= await preferred.IsAvailableAsync() ? preferred : fallback;

    public async Task<string> ProtectAsync(string token, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token) || IsProtected(token)) return token;
        var provider = await ChooseAsync();
        var key = await provider.GetKeyAsync(ct);
        var plain = Encoding.UTF8.GetBytes(token);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(nonce, plain, cipher, tag, Encoding.ASCII.GetBytes(provider.Scheme));
            return Prefix + provider.Scheme + ":" + Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public async Task<string> UnprotectAsync(string value, CancellationToken ct)
    {
        if (!IsProtected(value)) return value;
        var rest = value[Prefix.Length..];
        var split = rest.IndexOf(':');
        if (split < 1) throw new CryptographicException("Token đã lưu bị hỏng.");
        var scheme = rest[..split];
        var provider = scheme == preferred.Scheme ? preferred : scheme == fallback.Scheme ? fallback : throw new CryptographicException("Token được bảo vệ bằng phương thức không còn trên máy này.");
        var data = Convert.FromBase64String(rest[(split + 1)..]);
        if (data.Length < 29) throw new CryptographicException("Token đã lưu bị hỏng.");
        var key = await provider.GetKeyAsync(ct);
        var plain = new byte[data.Length - 28];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(data.AsSpan(0, 12), data.AsSpan(28), data.AsSpan(12, 16), plain, Encoding.ASCII.GetBytes(scheme));
            return Encoding.UTF8.GetString(plain);
        }
        catch (AuthenticationTagMismatchException) { throw new CryptographicException("Không giải mã được token đã lưu. Hãy kết nối lại tài khoản Google."); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}

/// <summary>
/// Khóa từ Windows Hello: một khóa RSA nằm trong TPM/bộ lưu Passport của Windows, chỉ ký được sau khi người dùng
/// xác nhận PIN, vân tay hoặc khuôn mặt. Chữ ký RSA PKCS#1 trên cùng một chuỗi luôn giống nhau, nên băm chữ ký đó
/// cho ra cùng một khóa AES mỗi lần mở khóa. Khóa chỉ được giữ trong bộ nhớ cho tới khi tắt app.
/// </summary>
public sealed class HelloKeyProvider : ITokenKeyProvider
{
    public const string SchemeName = "h1";
    private const string CredentialName = "DriveLink.TokenKey.v1";
    private static readonly byte[] Challenge = SHA256.HashData("DriveLink token protection v1"u8);
    private readonly SemaphoreSlim gate = new(1, 1);
    private byte[]? key;
    public string Scheme => SchemeName;

    public async Task<bool> IsAvailableAsync()
    {
        try { return OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240) && await KeyCredentialManager.IsSupportedAsync(); }
        catch (Exception) { return false; }
    }

    public async Task<byte[]> GetKeyAsync(CancellationToken ct)
    {
        if (key is not null) return key;
        await gate.WaitAsync(ct);
        try
        {
            if (key is not null) return key;
            var opened = await KeyCredentialManager.OpenAsync(CredentialName);
            var credential = opened.Status == KeyCredentialStatus.Success ? opened.Credential : null;
            if (opened.Status == KeyCredentialStatus.NotFound)
            {
                var created = await KeyCredentialManager.RequestCreateAsync(CredentialName, KeyCredentialCreationOption.FailIfExists);
                if (created.Status != KeyCredentialStatus.Success) throw Locked(created.Status);
                credential = created.Credential;
                SecurityLog.Write("token-protection", "Tạo khóa Windows Hello để bảo vệ token Google");
            }
            if (credential is null) throw Locked(opened.Status);
            var signed = await credential.RequestSignAsync(CryptographicBuffer.CreateFromByteArray(Challenge));
            if (signed.Status != KeyCredentialStatus.Success) { SecurityLog.Write("token-unlock-failed", "Windows Hello không xác nhận mở khóa token: " + signed.Status); throw Locked(signed.Status); }
            var signature = signed.Result.ToArray();
            key = SHA256.HashData(signature);
            CryptographicOperations.ZeroMemory(signature);
            return key;
        }
        finally { gate.Release(); }
    }

    private static TokenLockedException Locked(KeyCredentialStatus status) => new(status == KeyCredentialStatus.UserCanceled
        ? "Bạn đã hủy xác nhận Windows Hello nên DriveLink chưa mở được tài khoản Google. Bấm ↻ để thử lại."
        : "Windows Hello chưa mở khóa được tài khoản Google (" + status + "). Kiểm tra PIN/vân tay trong Cài đặt Windows rồi thử lại.");
}

/// <summary>Khóa dự phòng khi máy không có Windows Hello: 32 byte ngẫu nhiên, mã hóa DPAPI kèm khóa phụ, lưu trong Registry, tách khỏi accounts.dat.</summary>
public sealed class LocalKeyProvider(string registryPath = @"Software\QuangHuy\DriveLink") : ITokenKeyProvider
{
    public const string SchemeName = "l1";
    private const string ValueName = "TokenKey";
    private static readonly byte[] Entropy = "DriveLink.TokenKey.Local.v1"u8.ToArray();
    private byte[]? key;
    public string Scheme => SchemeName;
    public Task<bool> IsAvailableAsync() => Task.FromResult(true);

    public Task<byte[]> GetKeyAsync(CancellationToken ct)
    {
        if (key is not null) return Task.FromResult(key);
        using var registry = Registry.CurrentUser.CreateSubKey(registryPath, true);
        if (registry.GetValue(ValueName) is byte[] stored) key = ProtectedData.Unprotect(stored, Entropy, DataProtectionScope.CurrentUser);
        else
        {
            key = RandomNumberGenerator.GetBytes(32);
            registry.SetValue(ValueName, ProtectedData.Protect(key, Entropy, DataProtectionScope.CurrentUser), RegistryValueKind.Binary);
        }
        return Task.FromResult(key);
    }
}
