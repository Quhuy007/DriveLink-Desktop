using System.Text.Json;
using System.Text;
namespace DriveLink.Services;

public static class OAuthConfigParser
{
    public const int MaximumBytes = 65536;
    public static OAuthConfig Parse(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaximumBytes) throw new InvalidOperationException("Tệp cấu hình vượt giới hạn 64 KB.");
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("private_key", out _) || !root.TryGetProperty("installed", out var installed) || installed.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("Cần JSON OAuth loại Desktop app (installed), không dùng Web application hoặc Service account.");
            var id = installed.GetProperty("client_id").GetString() ?? "";
            var secret = installed.TryGetProperty("client_secret", out var value) ? value.GetString() ?? "" : "";
            if (id.Length > 512 || id.Length <= 27 || !id.EndsWith(".apps.googleusercontent.com", StringComparison.Ordinal) || id.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')))
                throw new InvalidOperationException("Client ID Google không hợp lệ.");
            if (secret.Length > 4096 || secret.Any(char.IsControl)) throw new InvalidOperationException("Client secret không hợp lệ.");
            return new OAuthConfig { ClientId = id, ClientSecret = secret };
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // Do not include raw JSON or a secret in diagnostic messages.
            if (ex is InvalidOperationException && ex.Message.StartsWith("Cần JSON")) throw;
            if (ex is InvalidOperationException && (ex.Message.StartsWith("Client ID") || ex.Message.StartsWith("Client secret"))) throw;
            throw new InvalidOperationException("Tệp JSON OAuth Desktop không hợp lệ.");
        }
    }
}
