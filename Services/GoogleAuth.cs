using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace DriveLink.Services;

public sealed class GoogleAuth(HttpClient http, SavedState state, SecureStore store)
{
    private readonly SemaphoreSlim refreshLock = new(1);
    private static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public async Task<Account> ConnectAsync(CancellationToken ct)
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
        var account = state.Accounts.FirstOrDefault(x => x.Id == id) ?? new Account { Id = id };
        account.Email = info.RootElement.GetProperty("email").GetString()!;
        account.Name = info.RootElement.TryGetProperty("name", out var name) ? name.GetString() ?? account.Email : account.Email;
        ApplyTokens(account, tokens.RootElement);
        if (string.IsNullOrEmpty(account.RefreshToken)) throw new InvalidOperationException("Chưa nhận được quyền truy cập lâu dài. Hãy kết nối lại và cấp quyền.");
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
            if (account.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2)) return account.AccessToken;
            using var tokens = await ExchangeAsync(new()
            {
                ["client_id"] = state.Config.ClientId, ["client_secret"] = state.Config.ClientSecret,
                ["refresh_token"] = account.RefreshToken, ["grant_type"] = "refresh_token"
            }, ct);
            ApplyTokens(account, tokens.RootElement);
            store.Save(state);
            return account.AccessToken;
        }
        finally { refreshLock.Release(); }
    }
    public async Task RevokeAsync(Account account, CancellationToken ct)
    {
        using var response = await http.PostAsync("https://oauth2.googleapis.com/revoke", new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = account.RefreshToken }), ct);
        response.EnsureSuccessStatusCode();
    }
    private async Task<JsonDocument> ExchangeAsync(Dictionary<string, string> values, CancellationToken ct)
    {
        using var response = await http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(values), ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Không thể xác thực Google. Kiểm tra cấu hình OAuth hoặc kết nối lại tài khoản (quyền có thể đã hết hạn/thu hồi).");
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }
    private static void ApplyTokens(Account account, JsonElement data)
    {
        account.AccessToken = data.GetProperty("access_token").GetString()!;
        account.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(data.GetProperty("expires_in").GetInt32());
        if (data.TryGetProperty("refresh_token", out var refresh)) account.RefreshToken = refresh.GetString()!;
    }
    public static Dictionary<string, string> ParseQuery(string query) => query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(x => x.Split('=', 2)).GroupBy(x => Uri.UnescapeDataString(x[0])).ToDictionary(x => x.Key, x => Uri.UnescapeDataString(x.Last().ElementAtOrDefault(1)?.Replace('+', ' ') ?? ""));
}
