using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace DriveLink.Services;

/// <summary>Quyền Google của tài khoản đã hết hạn hoặc bị thu hồi (Google trả invalid_grant): chỉ đăng nhập lại mới sửa được.</summary>
public sealed class ReauthRequiredException(Account account) : InvalidOperationException(
    $"Phiên đăng nhập Google của {DisplayPrivacy.MaskEmail(account.Email)} đã hết hạn hoặc bị thu hồi. Bấm Đăng nhập lại để tiếp tục (không cần xóa tài khoản).")
{
    public Account Account { get; } = account;
}

/// <summary>Google từ chối yêu cầu lấy token; <see cref="Error"/> là mã lỗi OAuth (ví dụ invalid_grant, invalid_client).</summary>
public sealed class OAuthTokenException(string error, string message) : InvalidOperationException(message)
{
    public string Error { get; } = error;
}

public sealed class GoogleAuth(HttpClient http, SavedState state, SecureStore store, TokenVault? tokenVault = null)
{
    private readonly SemaphoreSlim refreshLock = new(1);
    private readonly TokenVault vault = tokenVault ?? TokenVault.Shared;
    /// <summary>Một tài khoản vừa chuyển sang trạng thái cần đăng nhập lại (báo một lần cho mỗi lần chuyển).</summary>
    public event Action<Account>? ReauthRequired;
    private static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    /// <summary>
    /// Đăng nhập Google. Với <paramref name="reconnect"/>: đăng nhập lại đúng tài khoản đó (trình duyệt điền sẵn email),
    /// thay token mới vào tài khoản cũ; chọn nhầm tài khoản khác thì báo lỗi và không lưu gì.
    /// </summary>
    public async Task<Account> ConnectAsync(CancellationToken ct, Account? reconnect = null)
    {
        if (string.IsNullOrWhiteSpace(state.Config.ClientId)) throw new InvalidOperationException("Hãy nhập cấu hình OAuth Desktop của Google trước.");
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var nonce = Base64Url(RandomNumberGenerator.GetBytes(32));
        using var server = new OAuthCallbackServer();
        var redirect = server.RedirectUri;
        var args = new Dictionary<string, string>
        {
            ["client_id"] = state.Config.ClientId, ["redirect_uri"] = redirect,
            ["response_type"] = "code", ["scope"] = "openid email https://www.googleapis.com/auth/drive",
            ["access_type"] = "offline", ["prompt"] = "consent select_account", ["state"] = nonce,
            ["code_challenge"] = challenge, ["code_challenge_method"] = "S256"
        };
        if (reconnect is not null) { args["login_hint"] = reconnect.Email; args["prompt"] = "consent"; }
        var query = string.Join("&", args.Select(x => $"{x.Key}={Uri.EscapeDataString(x.Value)}"));
        Process.Start(new ProcessStartInfo("https://accounts.google.com/o/oauth2/v2/auth?" + query) { UseShellExecute = true });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        using var callback = await server.WaitAsync(nonce, timeout.Token);
        try
        {
        if (callback.Denied) throw new InvalidOperationException("Bạn chưa cấp quyền kết nối Google.");
        var code = callback.Code ?? throw new InvalidOperationException("Google không trả về mã đăng nhập.");
        using var tokens = await ExchangeAsync(new()
        {
            ["code"] = code, ["client_id"] = state.Config.ClientId, ["client_secret"] = state.Config.ClientSecret,
            ["redirect_uri"] = redirect, ["grant_type"] = "authorization_code", ["code_verifier"] = verifier
        }, timeout.Token);
        var access = tokens.RootElement.GetProperty("access_token").GetString()!;
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://openidconnect.googleapis.com/v1/userinfo");
        request.Headers.Authorization = new("Bearer", access);
        using var response = await http.SendAsync(request, timeout.Token);
        response.EnsureSuccessStatusCode();
        using var info = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        var id = info.RootElement.GetProperty("sub").GetString()!;
        if (reconnect is not null && id != reconnect.Id)
            throw new InvalidOperationException($"Bạn đã đăng nhập tài khoản khác. Hãy chọn đúng {DisplayPrivacy.MaskEmail(reconnect.Email)} để đăng nhập lại tài khoản này.");
        var account = state.Accounts.FirstOrDefault(x => x.Id == id) ?? new Account { Id = id };
        account.Email = info.RootElement.GetProperty("email").GetString()!;
        account.Name = info.RootElement.TryGetProperty("name", out var name) ? name.GetString() ?? account.Email : account.Email;
        await ApplyTokensAsync(account, tokens.RootElement, timeout.Token);
        if (string.IsNullOrEmpty(account.RefreshToken)) throw new InvalidOperationException("Chưa nhận được quyền truy cập lâu dài. Hãy kết nối lại và cấp quyền.");
        account.NeedsReauth = false;
        if (!state.Accounts.Contains(account)) state.Accounts.Add(account);
        state.SelectedAccountId = account.Id;
        store.Save(state);
        await RespondSafelyAsync(callback, true, "Tài khoản " + account.Email + " đã được liên kết. Bạn có thể bắt đầu gửi và nhận file trong ứng dụng.");
        return account;
        }
        catch (Exception ex)
        {
            var detail = ex is OperationCanceledException ? "Yêu cầu đã bị hủy hoặc quá thời gian chờ. Hãy quay lại ứng dụng và thử lại." : "Không thể hoàn tất kết nối Google. Hãy quay lại DriveLink để xem lỗi, kiểm tra quyền được cấp và thử lại.";
            if (callback.Denied) detail = "Bạn chưa đồng ý cấp quyền truy cập. Hãy quay lại ứng dụng nếu muốn kết nối lại.";
            await RespondSafelyAsync(callback, false, detail);
            throw;
        }
    }
    private static async Task RespondSafelyAsync(OAuthCallback callback, bool success, string detail)
    {
        // Browser disconnects must not turn an already saved account into a failed login.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await callback.RespondAsync(success, detail, timeout.Token); }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or OperationCanceledException or ObjectDisposedException) { }
    }
    public async Task<string> GetTokenAsync(Account account, CancellationToken ct)
    {
        await refreshLock.WaitAsync(ct);
        try
        {
            if (account.NeedsReauth) throw new ReauthRequiredException(account);
            if (account.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2)) return await vault.UnprotectAsync(account.AccessToken, ct);
            if (string.IsNullOrEmpty(account.RefreshToken)) throw MarkReauth(account);
            JsonDocument tokens;
            try
            {
                tokens = await ExchangeAsync(new()
                {
                    ["client_id"] = state.Config.ClientId, ["client_secret"] = state.Config.ClientSecret,
                    ["refresh_token"] = await vault.UnprotectAsync(account.RefreshToken, ct), ["grant_type"] = "refresh_token"
                }, ct);
            }
            // invalid_grant: token hết hạn (app ở chế độ Testing quá 7 ngày, lâu không dùng, đổi mật khẩu) hoặc bị thu hồi.
            catch (OAuthTokenException ex) when (ex.Error == "invalid_grant") { throw MarkReauth(account); }
            using var _ = tokens;
            var access = await ApplyTokensAsync(account, tokens.RootElement, ct);
            store.Save(state);
            return access;
        }
        finally { refreshLock.Release(); }
    }
    private ReauthRequiredException MarkReauth(Account account)
    {
        if (!account.NeedsReauth)
        {
            account.NeedsReauth = true;
            try { store.Save(state); } catch (IOException) { }
            SecurityLog.Write("token-expired", "Google báo quyền của " + DisplayPrivacy.MaskEmail(account.Email) + " đã hết hạn hoặc bị thu hồi");
            ReauthRequired?.Invoke(account);
        }
        return new ReauthRequiredException(account);
    }
    public async Task RevokeAsync(Account account, CancellationToken ct)
    {
        var refresh = await vault.UnprotectAsync(account.RefreshToken, ct);
        using var response = await http.PostAsync("https://oauth2.googleapis.com/revoke", new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = refresh }), ct);
        response.EnsureSuccessStatusCode();
        SecurityLog.Write("token-revoked", "Đã thu hồi quyền của DriveLink trên tài khoản " + DisplayPrivacy.MaskEmail(account.Email));
    }
    /// <summary>Mã hóa lại các token cũ (lưu trước bản 1.2.0) bằng kho token. Trả về số tài khoản đã chuyển.</summary>
    public async Task<int> ProtectStoredTokensAsync(CancellationToken ct)
    {
        var changed = 0;
        foreach (var account in state.Accounts.Where(x => !TokenVault.IsProtected(x.RefreshToken) || !TokenVault.IsProtected(x.AccessToken)).ToList())
        {
            account.RefreshToken = await vault.ProtectAsync(account.RefreshToken, ct);
            account.AccessToken = await vault.ProtectAsync(account.AccessToken, ct);
            changed++;
        }
        if (changed > 0) { store.Save(state); SecurityLog.Write("token-protection", $"Đã mã hóa lại token của {changed} tài khoản bằng {await vault.DescribeAsync()}"); }
        return changed;
    }
    private async Task<JsonDocument> ExchangeAsync(Dictionary<string, string> values, CancellationToken ct)
    {
        using var response = await http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(values), ct);
        if (!response.IsSuccessStatusCode)
        {
            var error = "";
            try { using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)); error = json.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : ""; }
            catch (JsonException) { }
            throw new OAuthTokenException(error, error switch
            {
                "invalid_grant" => "Google báo quyền đăng nhập đã hết hạn hoặc bị thu hồi. Hãy đăng nhập lại tài khoản.",
                "invalid_client" or "unauthorized_client" => "Google không nhận OAuth Client hiện tại. Kiểm tra cấu hình OAuth trong Cài đặt.",
                _ => "Không thể xác thực Google. Kiểm tra cấu hình OAuth hoặc kết nối lại tài khoản.",
            });
        }
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }
    /// <summary>Lưu token mới ở dạng đã mã hóa; trả về access token dạng rõ để dùng ngay, không lưu dạng rõ.</summary>
    private async Task<string> ApplyTokensAsync(Account account, JsonElement data, CancellationToken ct)
    {
        var access = data.GetProperty("access_token").GetString()!;
        account.AccessToken = await vault.ProtectAsync(access, ct);
        account.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(data.GetProperty("expires_in").GetInt32());
        if (data.TryGetProperty("refresh_token", out var refresh)) account.RefreshToken = await vault.ProtectAsync(refresh.GetString()!, ct);
        return access;
    }
    public static Dictionary<string, string> ParseQuery(string query) => query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(x => x.Split('=', 2)).GroupBy(x => Uri.UnescapeDataString(x[0])).ToDictionary(x => x.Key, x => Uri.UnescapeDataString(x.Last().ElementAtOrDefault(1)?.Replace('+', ' ') ?? ""));
}
