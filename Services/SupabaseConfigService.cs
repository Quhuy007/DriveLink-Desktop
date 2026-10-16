using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace DriveLink.Services;

public sealed class CloudAuthenticationException : InvalidOperationException
{
    public CloudAuthenticationException() : base("Mật khẩu quản trị không đúng.") { }
}

public sealed class SupabaseConfigService : IDisposable
{
    private readonly HttpClient http;
    private readonly bool ownsHttp;
    private readonly string baseUrl, key;
    private string accessToken = "";
    private DateTimeOffset expires;
    public string Role { get; private set; } = "";
    public bool IsAdmin => Role == "admin";
    public SupabaseConfigService(CloudConnection profile, HttpClient? client = null)
    {
        var valid = ValidateConnection(profile.ProjectUrl, profile.PublishableKey);
        baseUrl = valid.ProjectUrl; key = valid.PublishableKey;
        ownsHttp = client is null;
        http = client ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
    }
    public static CloudConnection ValidateConnection(string url, string publishableKey)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.AbsolutePath != "/" || !uri.Host.EndsWith(".supabase.co", StringComparison.OrdinalIgnoreCase) || uri.Host.Split('.').Length != 3)
            throw new InvalidOperationException("Kết nối Cloud mặc định không hợp lệ. Liên hệ người quản trị để cập nhật bản ứng dụng.");
        var value = publishableKey.Trim();
        if (!value.StartsWith("sb_publishable_", StringComparison.Ordinal) || value.Length < 20 || value.Length > 512 || value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-')))
            throw new InvalidOperationException("Dùng khóa sb_publishable_…; không nhập secret key, service_role hoặc mật khẩu cơ sở dữ liệu.");
        return new CloudConnection { ProjectUrl = uri.GetLeftPart(UriPartial.Authority), PublishableKey = value };
    }
    public async Task LoginAsync(string email, string password, CancellationToken ct)
    {
        ClearSession();
        using var data = await SendAsync(HttpMethod.Post, "auth/v1/token?grant_type=password", new { email = email.Trim(), password }, false, ct);
        accessToken = data.RootElement.GetProperty("access_token").GetString() ?? "";
        expires = DateTimeOffset.UtcNow.AddSeconds(data.RootElement.GetProperty("expires_in").GetInt32());
        var id = data.RootElement.GetProperty("user").GetProperty("id").GetString()!;
        if (!Guid.TryParse(id, out _)) { ClearSession(); throw new InvalidOperationException("Cloud trả về tài khoản không hợp lệ."); }
        try
        {
            using var membership = await SendAsync(HttpMethod.Get, "rest/v1/drivelink_members?select=role&user_id=eq." + Uri.EscapeDataString(id), null, true, ct);
            if (membership.RootElement.GetArrayLength() == 1) Role = membership.RootElement[0].GetProperty("role").GetString() ?? "";
            if (Role is not ("admin" or "reader")) throw new InvalidOperationException("Tài khoản chưa được cấp quyền đọc/quản trị DriveLink trên Cloud.");
        }
        catch { ClearSession(); throw; }
    }
    public async Task<CloudRevision?> GetStatusAsync(CancellationToken ct)
    {
        var authenticated = !string.IsNullOrEmpty(Role);
        using var data = await SendAsync(HttpMethod.Post, "rest/v1/rpc/" + (authenticated ? "drivelink_config_status" : "drivelink_public_config_status"), new { }, authenticated, ct);
        return data.RootElement.ValueKind == JsonValueKind.Null ? null : data.RootElement.Deserialize<CloudRevision>();
    }
    public async Task<List<CloudRevision>> ListVersionsAsync(CancellationToken ct, int offset = 0)
    {
        RequireAdmin();
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        using var data = await SendAsync(HttpMethod.Get, "rest/v1/drivelink_config_versions?select=id,label,client_id,sha256,created_at,payload_format&order=created_at.desc,id.desc&limit=100&offset=" + offset, null, true, ct);
        return data.RootElement.Deserialize<List<CloudRevision>>() ?? [];
    }
    public async Task<CloudRevision> DownloadActiveAsync(CancellationToken ct)
    {
        var authenticated = !string.IsNullOrEmpty(Role);
        using var data = await SendAsync(HttpMethod.Post, "rest/v1/rpc/" + (authenticated ? "drivelink_download_config" : "drivelink_public_download_config"), new { }, authenticated, ct);
        if (data.RootElement.ValueKind == JsonValueKind.Null) throw new InvalidOperationException("Quản trị viên chưa đăng cấu hình đang dùng.");
        var revision = data.RootElement.Deserialize<CloudRevision>() ?? throw new InvalidOperationException("Cấu hình cloud không hợp lệ.");
        VerifyPayloadHash(revision);
        if (!authenticated && revision.PayloadFormat != OAuthVault.Format) throw new InvalidOperationException("Cloud chưa có bản mã hóa hợp lệ. Liên hệ quản trị viên.");
        return revision;
    }
    public static OAuthConfig VerifyRevision(CloudRevision revision)
    {
        VerifyPayloadHash(revision);
        if (revision.PayloadFormat != "legacy") throw new InvalidOperationException("Cấu hình cần mật khẩu để mở khóa.");
        var parsed = OAuthConfigParser.Parse(revision.ConfigJson);
        if (parsed.ClientId != revision.ClientId) throw new InvalidOperationException("Client ID trong cấu hình và thông tin phiên bản không khớp.");
        return parsed;
    }
    public static void VerifyPayloadHash(CloudRevision revision)
    {
        if (!Guid.TryParse(revision.Id, out _) || Encoding.UTF8.GetByteCount(revision.ConfigJson) > OAuthVault.MaximumEnvelopeBytes) throw new InvalidOperationException("Bản cấu hình tải về không hợp lệ.");
        var actual = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(revision.ConfigJson)));
        if (!actual.Equals(revision.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Mã kiểm tra cấu hình không khớp. Chưa áp dụng vào máy.");
        if (revision.PayloadFormat == OAuthVault.Format) OAuthVault.ValidateEnvelope(revision.ConfigJson);
        else if (revision.PayloadFormat != "legacy") throw new InvalidOperationException("Định dạng cấu hình chưa được hỗ trợ.");
    }
    public async Task<CloudRevision> PublishLockedAsync(string label, string clientId, string envelope, string? expectedCurrent, CancellationToken ct)
    {
        RequireAdmin(); OAuthVault.ValidateEnvelope(envelope);
        if (string.IsNullOrWhiteSpace(label) || label.Trim().Length > 80 || label.Any(char.IsControl)) throw new InvalidOperationException("Tên phiên bản cần 1–80 ký tự, không chứa ký tự điều khiển.");
        OAuthConfigParser.Parse(JsonSerializer.Serialize(new { installed = new { client_id = clientId } }));
        using var data = await SendAsync(HttpMethod.Post, "rest/v1/rpc/drivelink_publish_locked_config", new { p_label = label.Trim(), p_client_id = clientId, p_envelope = envelope, p_expected_current_id = expectedCurrent }, true, ct);
        return data.RootElement.Deserialize<CloudRevision>() ?? throw new InvalidOperationException("Chưa xác nhận được bản đã đăng. Làm mới trước khi thử lại.");
    }
    public async Task<CloudRevision> ActivateAsync(string id, string? expectedCurrent, CancellationToken ct)
    {
        RequireAdmin();
        if (!Guid.TryParse(id, out _)) throw new InvalidOperationException("Mã phiên bản không hợp lệ.");
        using var data = await SendAsync(HttpMethod.Post, "rest/v1/rpc/drivelink_activate_config", new { p_version_id = id, p_expected_current_id = expectedCurrent }, true, ct);
        return data.RootElement.Deserialize<CloudRevision>()!;
    }
    private void RequireAdmin() { if (!IsAdmin) throw new InvalidOperationException("Chỉ tài khoản quản trị được đăng/chọn lại cấu hình."); }
    private async Task<JsonDocument> SendAsync(HttpMethod method, string path, object? payload, bool authenticated, CancellationToken ct)
    {
        if (authenticated && (string.IsNullOrEmpty(accessToken) || expires <= DateTimeOffset.UtcNow)) throw new InvalidOperationException("Phiên Cloud hết hạn. Hãy đăng nhập lại.");
        using var request = new HttpRequestMessage(method, baseUrl + "/" + path);
        request.Headers.Add("apikey", key);
        if (authenticated) request.Headers.Authorization = new("Bearer", accessToken);
        if (payload is not null) request.Content = JsonContent.Create(payload);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var bytes = new MemoryStream();
        var buffer = new byte[8192]; int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (bytes.Length + read > 262144) throw new InvalidOperationException("Phản hồi Cloud vượt giới hạn cho phép.");
            await bytes.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        if (!response.IsSuccessStatusCode)
        {
            string code = "";
            try
            {
                using var error = JsonDocument.Parse(bytes.ToArray());
                if (error.RootElement.ValueKind == JsonValueKind.Object)
                {
                    if (error.RootElement.TryGetProperty("error_code", out var authCode) && authCode.ValueKind == JsonValueKind.String) code = authCode.GetString() ?? "";
                    if (code.Length == 0 && error.RootElement.TryGetProperty("code", out var field))
                        code = field.ValueKind == JsonValueKind.String ? field.GetString() ?? "" : field.ValueKind == JsonValueKind.Number ? field.GetRawText() : "";
                }
            }
            catch (JsonException) { }
            if (code == "40001") throw new InvalidOperationException("Bản đang dùng đã được thay đổi bởi phiên khác. Bấm Làm mới trước khi đăng/chọn lại.");
            if (path.StartsWith("auth/v1/token", StringComparison.Ordinal) && (code == "invalid_credentials" || response.StatusCode == HttpStatusCode.Unauthorized)) throw new CloudAuthenticationException();
            if (code == "email_not_confirmed") throw new InvalidOperationException("Tài khoản quản trị chưa xác nhận email. Hoàn tất xác nhận trong phần quản lý tài khoản Cloud.");
            var explanation = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "Phiên Cloud không hợp lệ hoặc đã hết hạn. Hãy đăng nhập lại.",
                HttpStatusCode.Forbidden => "Cloud từ chối quyền truy cập. Kiểm tra vai trò được cấp.",
                HttpStatusCode.NotFound => "Chưa có cấu trúc cloud hoặc địa chỉ dự án không đúng. Hoàn tất bộ thiết lập Cloud.",
                (HttpStatusCode)429 => "Quá nhiều yêu cầu. Vui lòng chờ rồi thử lại.",
                _ when path.StartsWith("auth/v1/token", StringComparison.Ordinal) => "Không đăng nhập được Cloud. Kiểm tra mật khẩu và trạng thái tài khoản quản trị.",
                _ => $"Cloud chưa hoàn tất yêu cầu ({(int)response.StatusCode}). Kiểm tra bộ thiết lập/quyền; làm mới trước khi thử lại."
            };
            throw new InvalidOperationException(explanation);
        }
        return JsonDocument.Parse(bytes.Length == 0 ? "null" : Encoding.UTF8.GetString(bytes.ToArray()));
    }
    public void ClearSession() { accessToken = ""; expires = default; Role = ""; }
    public void Dispose() { ClearSession(); if (ownsHttp) http.Dispose(); }
}
