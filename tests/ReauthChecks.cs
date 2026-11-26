using DriveLink;
using DriveLink.Services;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;

/// <summary>Token Google hết hạn/bị thu hồi (invalid_grant): đánh dấu cần đăng nhập lại, báo đúng một lần, không gọi mạng thừa.</summary>
static class ReauthChecks
{
    public static async Task Run(Action<bool, string> check, string root)
    {
        var folder = Path.Combine(root, "reauth"); Directory.CreateDirectory(folder);
        var store = new SecureStore(folder);
        var account = new Account { Id = "sub-1", Email = "nguoidung@example.com", RefreshToken = "old-refresh", AccessToken = "old-access", ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1) };
        var state = new SavedState { Config = new OAuthConfig { ClientId = "client", ClientSecret = "secret" }, Accounts = [account] };
        store.Save(state);
        var calls = 0; var answer = "invalid_grant";
        using var http = new HttpClient(new ActionHandler(_ =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent($"{{\"error\":\"{answer}\",\"error_description\":\"Token has been expired or revoked.\"}}", Encoding.UTF8, "application/json") });
        }));
        var auth = new GoogleAuth(http, state, store);
        var raised = new List<Account>();
        auth.ReauthRequired += raised.Add;

        ReauthRequiredException? first = null;
        try { await auth.GetTokenAsync(account, CancellationToken.None); } catch (ReauthRequiredException ex) { first = ex; }
        check(first is not null && first.Account == account && account.NeedsReauth && raised.SequenceEqual([account]) && first.Message.Contains("Đăng nhập lại"),
            "An expired or revoked Google grant marks the account as needing re-login and says how to fix it");
        check(store.Load().Accounts[0].NeedsReauth && store.Load().Accounts[0].ToString().EndsWith("cần đăng nhập lại"), "The re-login reminder survives restarting the app");

        var again = false;
        try { await auth.GetTokenAsync(account, CancellationToken.None); } catch (ReauthRequiredException) { again = true; }
        check(again && calls == 1 && raised.Count == 1, "While re-login is pending, background checks fail fast without calling Google or prompting again");

        var other = new Account { Id = "sub-2", Email = "khac@example.com", RefreshToken = "r", AccessToken = "a", ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1) };
        state.Accounts.Add(other);
        answer = "invalid_client";
        OAuthTokenException? config = null;
        try { await auth.GetTokenAsync(other, CancellationToken.None); } catch (OAuthTokenException ex) { config = ex; }
        check(config is { Error: "invalid_client" } && !other.NeedsReauth && raised.Count == 1 && config.Message.Contains("OAuth Client"),
            "A wrong OAuth client is reported as a configuration problem, not as an expired login");
        check(SecurityLog.ReadRecent(50).Any(l => l.Contains("token-expired")) && !SecurityLog.ReadRecent(50).Any(l => l.Contains("old-refresh")), "Expired login is logged without the token");
    }
}
