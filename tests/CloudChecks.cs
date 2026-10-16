using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DriveLink;
using DriveLink.Services;

static class CloudChecks
{
    public static async Task Run(Action<bool, string> check, SecureStore store)
    {
        var profile = new CloudConnection { ProjectUrl = "https://drivelink-test.supabase.co", PublishableKey = "sb_publishable_fixture_only" };
        var validJson = "{\"installed\":{\"client_id\":\"123-fixture.apps.googleusercontent.com\",\"client_secret\":\"fixture-only\"}}";
        var bundled = CloudDefaults.Read();
        check(bundled is not null && bundled.PublishableKey.StartsWith("sb_publishable_"), "Release cloud identity loads without any machine-local connection profile");
        check(string.IsNullOrEmpty(bundled!.LastAppliedRevision), "Bundled cloud identity contains no account token or applied-user state");
        var revision = new CloudRevision { Id = Guid.NewGuid().ToString(), Label = "Fixture", ClientId = "123-fixture.apps.googleusercontent.com", ConfigJson = validJson, Sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(validJson))), CreatedAt = DateTimeOffset.UtcNow };
        void Reject(Action work, string message)
        {
            try { work(); } catch (InvalidOperationException) { check(true, message); return; }
            throw new Exception(message);
        }
        async Task RejectAsync(Func<Task> work, string message)
        {
            try { await work(); } catch (InvalidOperationException) { check(true, message); return; }
            throw new Exception(message);
        }
        foreach (var url in new[] { "http://drivelink-test.supabase.co", "https://drivelink-test.supabase.co.evil.test", "https://drivelink-test.supabase.co/rest/v1", "https://user@drivelink-test.supabase.co", "https://drivelink-test.supabase.co?x=1" })
            Reject(() => SupabaseConfigService.ValidateConnection(url, profile.PublishableKey), "Cloud rejects untrusted project URL: " + url);
        foreach (var key in new[] { "sb_secret_fixture", "service_role", "eyJlegacy-anon" })
            Reject(() => SupabaseConfigService.ValidateConnection(profile.ProjectUrl, key), "Cloud rejects privileged or unsupported keys");
        check(OAuthConfigParser.Parse(validJson).ClientId == revision.ClientId, "OAuth Desktop JSON parses without exposing its secret");
        foreach (var json in new[] { "{\"web\":{}}", "{\"private_key\":\"fixture\"}", "[1]", "{\"installed\":{\"client_id\":42}}", new string('x', 65537) })
            Reject(() => OAuthConfigParser.Parse(json), "OAuth parser rejects wrong type, malformed or oversized configuration");
        check(SupabaseConfigService.VerifyRevision(revision).ClientId == revision.ClientId, "Downloaded cloud revision verifies checksum and Client ID");
        revision.Sha256 = "bad";
        Reject(() => SupabaseConfigService.VerifyRevision(revision), "Corrupted cloud revision cannot be applied");
        revision.Sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(validJson)));
        revision.ClientId = "other.apps.googleusercontent.com";
        Reject(() => SupabaseConfigService.VerifyRevision(revision), "Cloud metadata cannot substitute a different Client ID");
        revision.ClientId = "123-fixture.apps.googleusercontent.com";
        Reject(() => OAuthVault.Encrypt(validJson, "short"), "Publishing rejects a weak short configuration password");
        var envelope = OAuthVault.Encrypt(validJson, "fixture unlock mật khẩu 2026");
        check(!envelope.Contains("fixture-only") && !envelope.Contains("installed"), "Encrypted envelope contains neither OAuth secret nor clear JSON");
        check(envelope != OAuthVault.Encrypt(validJson, "fixture unlock mật khẩu 2026"), "Each encryption uses fresh salt and nonce");
        revision.ConfigJson = envelope; revision.PayloadFormat = OAuthVault.Format;
        revision.Sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(envelope)));
        check(OAuthVault.Decrypt(revision, "fixture unlock mật khẩu 2026").ClientSecret == "fixture-only", "Correct Unicode password unlocks exact OAuth configuration");
        Reject(() => OAuthVault.Decrypt(revision, "wrong-password"), "Wrong configuration password cannot unlock JSON");
        Reject(() => SupabaseConfigService.VerifyRevision(revision), "Legacy clear parser cannot bypass the encrypted payload password");
        revision.ClientId = "substituted.apps.googleusercontent.com";
        Reject(() => OAuthVault.Decrypt(revision, "fixture unlock mật khẩu 2026"), "AES authentication binds encrypted JSON to its Client ID");
        revision.ClientId = "123-fixture.apps.googleusercontent.com";
        Reject(() => OAuthVault.ValidateEnvelope(envelope.Replace("600000", "1")), "Untrusted KDF parameters cannot weaken or exhaust decryption");
        var broken = JsonSerializer.Deserialize<LockedOAuthPayload>(envelope)!;
        var tag = Convert.FromBase64String(broken.Tag); tag[0] ^= 1; broken.Tag = Convert.ToBase64String(tag);
        revision.ConfigJson = JsonSerializer.Serialize(broken);
        revision.Sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(revision.ConfigJson)));
        Reject(() => OAuthVault.Decrypt(revision, "fixture unlock mật khẩu 2026"), "Recomputed checksum cannot bypass ciphertext authentication");
        revision.ConfigJson = envelope; revision.Sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(envelope)));
        var state = store.Load(); state.Cloud = profile; state.Cloud.LastAppliedRevision = revision.Id; store.Save(state);
        check(store.Load().Cloud.LastAppliedRevision == revision.Id && store.Load().Cloud.ProjectUrl == profile.ProjectUrl, "Cloud connection and applied revision survive encrypted preferences roundtrip");
        var userId = Guid.NewGuid().ToString();
        HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
        async Task<HttpResponseMessage?> Auth(HttpRequestMessage request, string role)
        {
            check(request.Headers.GetValues("apikey").Single() == profile.PublishableKey, "Supabase request uses only configured publishable API key");
            if (request.RequestUri!.AbsolutePath == "/auth/v1/token")
            {
                check(request.Headers.Authorization is null && request.RequestUri.Query == "?grant_type=password", "Password login sends no stale bearer token");
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                check(body.RootElement.GetProperty("email").GetString() == "fixture@example.com" && body.RootElement.GetProperty("password").GetString() == "fixture-password", "Cloud login uses Supabase Auth credentials");
                return Json(new { access_token = "fixture-token", expires_in = 3600, user = new { id = userId } });
            }
            if (request.RequestUri.AbsolutePath.EndsWith("drivelink_public_download_config") || request.RequestUri.AbsolutePath.EndsWith("drivelink_public_config_status"))
            {
                check(request.Headers.Authorization is null, "Automatic receive mode ships no reader bearer token or password");
                return Json(revision);
            }
            check(request.Headers.Authorization?.Parameter == "fixture-token", "Protected cloud request uses authenticated user bearer token");
            return request.RequestUri.AbsolutePath == "/rest/v1/drivelink_members" ? Json(new[] { new { role } }) : null;
        }
        using (var http = new HttpClient(new FakeHandler(async request => await Auth(request, "reader") ?? Json(revision))))
        using (var cloud = new SupabaseConfigService(profile, http))
        {
            await cloud.LoginAsync("fixture@example.com", "fixture-password", default);
            check(cloud.Role == "reader" && !cloud.IsAdmin, "Reader login receives only reader role");
            var downloaded = await cloud.DownloadActiveAsync(default);
            check(downloaded.ConfigJson == envelope, "Reader downloads only encrypted active configuration");
            await RejectAsync(async () => await cloud.PublishLockedAsync("new", revision.ClientId, envelope, revision.Id, default), "Reader cannot publish even if UI is bypassed");
            await RejectAsync(async () => await cloud.ActivateAsync(revision.Id, revision.Id, default), "Reader cannot activate historical versions");
            await RejectAsync(async () => await cloud.ListVersionsAsync(default), "Reader cannot browse administration history");
            cloud.ClearSession();
            check((await cloud.DownloadActiveAsync(default)).ConfigJson == envelope, "Clearing account session returns to encrypted-only automatic receiving");
        }
        using (var http = new HttpClient(new FakeHandler(request =>
        {
            check(request.RequestUri!.AbsolutePath.EndsWith("drivelink_public_config_status") || request.RequestUri.AbsolutePath.EndsWith("drivelink_public_download_config"), "No-login receiving uses only dedicated public encrypted-package RPCs");
            check(request.Headers.Authorization is null, "No reader credential is sent in automatic receiving mode");
            return Task.FromResult(Json(revision));
        })))
        using (var cloud = new SupabaseConfigService(profile, http))
        {
            check((await cloud.GetStatusAsync(default))?.Id == revision.Id, "Automatic receiver can discover active encrypted revision");
            check((await cloud.DownloadActiveAsync(default)).ConfigJson == envelope, "Automatic receiver retrieves ciphertext without a user account login");
            await RejectAsync(async () => await cloud.ListVersionsAsync(default), "Automatic receiver cannot enumerate administration history");
            await RejectAsync(async () => await cloud.PublishLockedAsync("new", revision.ClientId, envelope, revision.Id, default), "Automatic receiver cannot publish configurations");
            await RejectAsync(async () => await cloud.ActivateAsync(revision.Id, revision.Id, default), "Automatic receiver cannot change active version");
        }
        var encryptedFormat = revision.PayloadFormat; revision.PayloadFormat = "legacy";
        using (var http = new HttpClient(new FakeHandler(_ => Task.FromResult(Json(revision)))))
        using (var cloud = new SupabaseConfigService(profile, http))
            await RejectAsync(async () => await cloud.DownloadActiveAsync(default), "Automatic receiver refuses legacy plaintext returned by an incorrect server");
        revision.PayloadFormat = encryptedFormat;
        foreach (var payload in new[] {
            "{\"code\":400,\"error_code\":\"invalid_credentials\",\"msg\":\"private-server-fixture\"}",
            "{\"code\":400,\"error_code\":\"email_not_confirmed\"}",
            "{\"code\":500}", "{\"code\":null}", "{\"code\":{\"unexpected\":true}}", "{\"code\":400,\"error_code\":123}" })
        {
            using var errorHttp = new HttpClient(new FakeHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent(payload) })));
            using var errorCloud = new SupabaseConfigService(profile, errorHttp);
            try { await errorCloud.LoginAsync("fixture@example.com", "fixture-password", default); throw new Exception("Expected login failure"); }
            catch (InvalidOperationException ex)
            {
                check(!ex.Message.Contains("target element") && !ex.Message.Contains("private-server-fixture"), "Numeric, null and object API error codes produce safe understandable messages");
                if (payload.Contains("invalid_credentials")) check(ex is CloudAuthenticationException, "Numeric HTTP code with invalid_credentials is classified as authentication failure");
                if (payload.Contains("email_not_confirmed")) check(ex.Message.Contains("chưa xác nhận email") && ex is not CloudAuthenticationException, "Unconfirmed email is distinguished from a wrong password");
                check(errorCloud.Role == "", "Failed authentication never creates an admin session");
            }
        }
        using (var http = new HttpClient(new FakeHandler(async request =>
        {
            var auth = await Auth(request, "admin"); if (auth is not null) return auth;
            if (request.RequestUri!.AbsolutePath.EndsWith("drivelink_config_versions"))
            {
                check(!request.RequestUri.Query.Contains("config_json") && request.RequestUri.Query.Contains("limit=100"), "Version history requests bounded metadata without raw JSON");
                check(request.RequestUri.Query.Contains("offset=0") || request.RequestUri.Query.Contains("offset=100"), "History supports additional pages beyond first 100 versions");
                return Json(new[] { revision });
            }
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            check(body.RootElement.GetProperty("p_expected_current_id").GetString() == revision.Id, "Cloud write carries expected active revision to prevent lost updates");
            if (request.RequestUri.AbsolutePath.EndsWith("drivelink_publish_locked_config"))
            {
                check(body.RootElement.GetProperty("p_envelope").GetString() == envelope && body.RootElement.GetProperty("p_label").GetString() == "new", "Admin sends only encrypted envelope and trimmed version label");
                check(!(await request.Content!.ReadAsStringAsync()).Contains("fixture unlock") && !(await request.Content.ReadAsStringAsync()).Contains("fixture-only"), "Configuration password and clear OAuth secret never reach the server request");
            }
            else check(body.RootElement.GetProperty("p_version_id").GetString() == revision.Id, "Rollback targets explicit historical revision");
            return Json(revision);
        })))
        using (var cloud = new SupabaseConfigService(profile, http))
        {
            await cloud.LoginAsync("fixture@example.com", "fixture-password", default);
            check(cloud.IsAdmin, "Cloud administration requires server-provided admin membership");
            await cloud.PublishLockedAsync(" new ", revision.ClientId, envelope, revision.Id, default);
            await cloud.ActivateAsync(revision.Id, revision.Id, default);
            check((await cloud.ListVersionsAsync(default)).Count == 1, "Admin can retrieve version metadata");
            check((await cloud.ListVersionsAsync(default, 100)).Count == 1, "Admin can load subsequent history pages");
        }
        using (var http = new HttpClient(new FakeHandler(async request => await Auth(request, "unassigned") ?? Json(revision))))
        using (var cloud = new SupabaseConfigService(profile, http))
        {
            await RejectAsync(() => cloud.LoginAsync("fixture@example.com", "fixture-password", default), "Unassigned cloud user cannot gain membership");
            check(cloud.Role == "", "Membership failure clears the local session");
        }
        using (var http = new HttpClient(new FakeHandler(async request => await Auth(request, "admin") ?? new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StringContent("{\"code\":\"40001\",\"message\":\"secret-server-fixture\"}") })))
        using (var cloud = new SupabaseConfigService(profile, http))
        {
            await cloud.LoginAsync("fixture@example.com", "fixture-password", default);
            try { await cloud.PublishLockedAsync("new", revision.ClientId, envelope, revision.Id, default); throw new Exception("Expected conflict"); }
            catch (InvalidOperationException ex) { check(ex.Message.Contains("Làm mới") && !ex.Message.Contains("secret-server-fixture"), "Concurrent update is explained without leaking server error bodies"); }
        }
    }
}
