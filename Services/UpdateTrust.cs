using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace DriveLink.Services;

/// <summary>Bản cập nhật không có chữ ký hợp lệ của chủ ứng dụng: không được tải hay cài.</summary>
public sealed class UpdateTrustException(string message) : Exception(message);

/// <summary>Danh sách tệp của một bản phát hành kèm mã SHA-256, được ký bằng khóa riêng của chủ ứng dụng.</summary>
public sealed class ReleaseManifest
{
    [JsonPropertyName("product")] public string Product { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("created")] public string Created { get; set; } = "";
    [JsonPropertyName("files")] public Dictionary<string, string> Files { get; set; } = [];
}

/// <summary>
/// Chống phát hành giả khi tài khoản GitHub bị chiếm: app chỉ nhận bản cập nhật có tệp
/// DriveLink-release-manifest.json được ký (ECDSA P-256, SHA-256) bằng khóa riêng nằm trên máy phát hành.
/// Khóa riêng không bao giờ lên GitHub, nên kẻ chiếm GitHub không tạo được chữ ký hợp lệ.
/// Mã SHA-256 của gói trong danh sách đã ký phải trùng mã trong feed Velopack; Velopack lại kiểm gói tải về theo mã đó.
/// </summary>
public static class UpdateTrust
{
    public const string ProductName = "DriveLink";
    public const string ManifestName = "DriveLink-release-manifest.json";
    public const string SignatureName = ManifestName + ".sig";
    public const int MaximumManifestBytes = 65536;

    /// <summary>Khóa công khai được tin cậy (SubjectPublicKeyInfo, base64). Thêm khóa mới vào đây trước khi đổi khóa ký.</summary>
    public static readonly IReadOnlyList<string> TrustedPublicKeys =
    [
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEGcCkgUBaf6aFeerRkradAL6D3gkpwrE0kCbZw9YrLeadh/71OsrMoHgkVHumH+Ksx6CBN0l20rZgMf69/RArng==",
    ];

    public static ReleaseManifest Verify(byte[] manifest, string signatureBase64, IReadOnlyCollection<string>? trustedKeys = null)
    {
        if (manifest.Length == 0 || manifest.Length > MaximumManifestBytes) throw new UpdateTrustException("Danh sách tệp của bản cập nhật không hợp lệ.");
        byte[] signature;
        try { signature = Convert.FromBase64String(signatureBase64.Trim()); }
        catch (FormatException) { throw new UpdateTrustException("Chữ ký của bản cập nhật bị hỏng."); }
        var valid = false;
        foreach (var key in trustedKeys ?? TrustedPublicKeys)
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(key), out _);
            if (ecdsa.VerifyData(manifest, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) { valid = true; break; }
        }
        if (!valid) throw new UpdateTrustException("Chữ ký của bản cập nhật không khớp khóa của chủ ứng dụng.");
        ReleaseManifest? parsed;
        try { parsed = JsonSerializer.Deserialize<ReleaseManifest>(manifest); }
        catch (JsonException) { throw new UpdateTrustException("Danh sách tệp của bản cập nhật không đọc được."); }
        if (parsed is null || parsed.Product != ProductName || parsed.Files.Count == 0) throw new UpdateTrustException("Danh sách tệp của bản cập nhật không dành cho DriveLink.");
        return parsed;
    }

    /// <summary>Gói Velopack sắp tải phải có đúng tên và mã SHA-256 như trong danh sách đã ký, và cùng số phiên bản.</summary>
    public static void RequireFile(ReleaseManifest manifest, string version, string fileName, string sha256)
    {
        if (!string.Equals(manifest.Version, version, StringComparison.OrdinalIgnoreCase)) throw new UpdateTrustException($"Chữ ký dành cho bản {manifest.Version}, không phải bản {version}.");
        if (!manifest.Files.TryGetValue(fileName, out var expected) || string.IsNullOrEmpty(sha256) || !string.Equals(expected, sha256, StringComparison.OrdinalIgnoreCase))
            throw new UpdateTrustException("Gói cập nhật không khớp danh sách tệp đã ký.");
    }

    public static string Sign(byte[] manifest, ECDsa key) => Convert.ToBase64String(key.SignData(manifest, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
}

public interface IReleaseSignatureSource
{
    Task<(byte[] Manifest, string Signature)> FetchAsync(string version, CancellationToken ct);
}

/// <summary>Lấy danh sách tệp đã ký từ chính GitHub Release của phiên bản đó.</summary>
public sealed class GithubReleaseSignatureSource(string repositoryUrl, HttpClient? client = null) : IReleaseSignatureSource
{
    private readonly HttpClient http = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    public async Task<(byte[] Manifest, string Signature)> FetchAsync(string version, CancellationToken ct)
    {
        var baseUrl = $"{repositoryUrl}/releases/download/v{Uri.EscapeDataString(version)}/";
        try
        {
            var manifest = await ReadLimitedAsync(baseUrl + UpdateTrust.ManifestName, ct);
            var signature = System.Text.Encoding.ASCII.GetString(await ReadLimitedAsync(baseUrl + UpdateTrust.SignatureName, ct));
            return (manifest, signature);
        }
        catch (HttpRequestException) { throw new UpdateTrustException($"Bản {version} trên trang phát hành không có chữ ký số của chủ ứng dụng."); }
    }
    private async Task<byte[]> ReadLimitedAsync(string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > UpdateTrust.MaximumManifestBytes) throw new UpdateTrustException("Danh sách tệp của bản cập nhật quá lớn.");
        var data = await response.Content.ReadAsByteArrayAsync(ct);
        return data.Length > UpdateTrust.MaximumManifestBytes ? throw new UpdateTrustException("Danh sách tệp của bản cập nhật quá lớn.") : data;
    }
}

/// <summary>Đọc danh sách tệp đã ký từ thư mục gói cập nhật trên máy (dùng khi kiểm tra gói vừa build).</summary>
public sealed class DirectoryReleaseSignatureSource(string folder) : IReleaseSignatureSource
{
    public Task<(byte[] Manifest, string Signature)> FetchAsync(string version, CancellationToken ct)
    {
        var manifest = Path.Combine(folder, UpdateTrust.ManifestName);
        var signature = Path.Combine(folder, UpdateTrust.SignatureName);
        if (!File.Exists(manifest) || !File.Exists(signature)) throw new UpdateTrustException("Thư mục gói cập nhật chưa có chữ ký. Chạy scripts/Sign-Release.ps1 trước.");
        return Task.FromResult((File.ReadAllBytes(manifest), File.ReadAllText(signature)));
    }
}
