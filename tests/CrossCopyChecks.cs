using DriveLink;
using DriveLink.Services;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;

/// <summary>Kiểm tra gửi chéo tài khoản bằng HTTP giả lập: đúng token cho từng bước, quyền tạm luôn được thu hồi, bản sao được đối chiếu MD5.</summary>
static class CrossCopyChecks
{
    private sealed record Call(string Method, string Path, string Query, string Token, string Body);

    private sealed class FakeDrive
    {
        public List<Call> Calls { get; } = [];
        public bool TargetSeesFile { get; init; }
        public HttpStatusCode GrantStatus { get; init; } = HttpStatusCode.OK;
        public int CopyNotFoundTimes { get; set; }
        public string CopyMd5 { get; init; } = "abc123";
        public bool CopyFails { get; init; }
        public string Source => "{\"id\":\"F\",\"name\":\"Video quay dựng.mp4\",\"mimeType\":\"video/mp4\",\"size\":\"1048576\",\"md5Checksum\":\"abc123\",\"capabilities\":{\"canShare\":true,\"canCopy\":true,\"canDownload\":true}}";

        public HttpClient Client() => new(new ActionHandler(async request =>
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync();
            var path = request.RequestUri!.AbsolutePath;
            var token = request.Headers.Authorization?.Parameter ?? "";
            Calls.Add(new(request.Method.Method, path, request.RequestUri.Query, token, body));
            HttpResponseMessage Json(string text, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(text, System.Text.Encoding.UTF8, "application/json") };
            if (request.Method == HttpMethod.Get && path.EndsWith("/about")) return Json("{\"storageQuota\":{\"limit\":\"2199023255552\",\"usage\":\"835628007219\"}}");
            if (request.Method == HttpMethod.Get && path.EndsWith("/files/F"))
                return token == "token-A" || TargetSeesFile ? Json(Source) : Json("{\"error\":{\"message\":\"File not found\"}}", HttpStatusCode.NotFound);
            if (request.Method == HttpMethod.Post && path.EndsWith("/files/F/permissions"))
                return GrantStatus == HttpStatusCode.OK ? Json("{\"id\":\"perm-temp\"}") : Json("{\"error\":{\"message\":\"Sharing blocked\"}}", GrantStatus);
            if (request.Method == HttpMethod.Post && path.EndsWith("/files/F/copy"))
            {
                if (CopyFails) return Json("{\"error\":{\"message\":\"Quota\"}}", HttpStatusCode.Forbidden);
                if (CopyNotFoundTimes-- > 0) return Json("{\"error\":{\"message\":\"Not yet\"}}", HttpStatusCode.NotFound);
                return Json("{\"id\":\"COPY\",\"name\":\"Video quay dựng.mp4\",\"size\":\"1048576\",\"md5Checksum\":\"" + CopyMd5 + "\"}");
            }
            if (request.Method == HttpMethod.Delete && path.EndsWith("/permissions/perm-temp")) return new HttpResponseMessage(HttpStatusCode.NoContent);
            return Json("{\"error\":{\"message\":\"Unexpected\"}}", HttpStatusCode.BadRequest);
        }));
    }

    public static async Task Run(Action<bool, string> check)
    {
        var a = new Account { Id = "A", Email = "nguon@example.com" };
        var b = new Account { Id = "B", Email = "dich@example.com" };
        var item = new DriveItem { Id = "F", Name = "Video quay dựng.mp4", MimeType = "video/mp4" };
        var phases = new List<string>();
        var phase = new SyncProgress<string>(phases.Add);
        var delays = new List<TimeSpan>();
        Func<long, Task<bool>> noRelay = _ => Task.FromResult(false);
        CrossAccountCopier Copier(HttpClient http) => new(new DriveService(http, (account, _) => Task.FromResult("token-" + account.Id))) { Delay = (d, _) => { delays.Add(d); return Task.CompletedTask; } };

        // 1. Đường chính: cấp quyền tạm → tài khoản đích sao chép → thu hồi.
        var fake = new FakeDrive { CopyNotFoundTimes = 1 };
        using (var http = fake.Client())
        {
            var result = await Copier(http).CopyAsync(a, b, item, phase, noRelay, null, CancellationToken.None);
            var grant = fake.Calls.Single(x => x.Method == "POST" && x.Path.EndsWith("/permissions"));
            using var grantBody = JsonDocument.Parse(grant.Body);
            check(grant.Token == "token-A" && grantBody.RootElement.GetProperty("emailAddress").GetString() == "dich@example.com" && grantBody.RootElement.GetProperty("role").GetString() == "reader" && grant.Query.Contains("sendNotificationEmail=false"),
                "Cross copy grants a silent temporary reader permission from the source account to the target email only");
            var copies = fake.Calls.Where(x => x.Path.EndsWith("/copy")).ToList();
            using var copyBody = JsonDocument.Parse(copies[^1].Body);
            check(copies.All(x => x.Token == "token-B") && copyBody.RootElement.GetProperty("parents")[0].GetString() == "root",
                "Server-side copy is made by the target account into its own My Drive");
            check(copies.Count == 2 && delays.Count == 1, "Copy retries once while the new permission propagates");
            var revoke = fake.Calls.Where(x => x.Method == "DELETE").ToList();
            check(revoke.Count == 1 && revoke[0].Token == "token-A" && revoke[0].Path.EndsWith("/files/F/permissions/perm-temp"), "Temporary permission is revoked by the source account after copying");
            check(result.Mode == CrossCopyMode.TemporaryShare && result.Verified && !result.RevokeFailed && result.BytesThroughMachine == 0 && result.Copy.Id == "COPY",
                "Cross copy reports a verified server-side copy with zero bytes through this machine");
            check(fake.Calls.Count(x => x.Method != "GET") == 4 && phases.Contains("Đang thu hồi quyền tạm…"), "Cross copy sends no other changing requests");
        }

        // 2. Sao chép lỗi: vẫn phải thu hồi quyền tạm.
        fake = new FakeDrive { CopyFails = true };
        using (var http = fake.Client())
        {
            var failed = false;
            try { await Copier(http).CopyAsync(a, b, item, phase, noRelay, null, CancellationToken.None); } catch (DriveApiException) { failed = true; }
            check(failed && fake.Calls.Count(x => x.Method == "DELETE") == 1, "Temporary permission is revoked even when the copy fails");
        }

        // 3. MD5 không khớp: báo lỗi, quyền tạm đã được thu hồi.
        fake = new FakeDrive { CopyMd5 = "different" };
        using (var http = fake.Client())
        {
            var mismatch = false;
            try { await Copier(http).CopyAsync(a, b, item, phase, noRelay, null, CancellationToken.None); } catch (InvalidDataException) { mismatch = true; }
            check(mismatch && fake.Calls.Count(x => x.Method == "DELETE") == 1, "A copy whose MD5 differs from the original is reported as an error after revoking access");
        }

        // 4. Tài khoản đích đã xem được tệp: sao chép thẳng, không cấp quyền.
        fake = new FakeDrive { TargetSeesFile = true };
        using (var http = fake.Client())
        {
            var result = await Copier(http).CopyAsync(a, b, item, phase, noRelay, null, CancellationToken.None);
            check(result.Mode == CrossCopyMode.DirectAccess && !fake.Calls.Any(x => x.Path.Contains("/permissions")), "Target account that can already read the file copies it without any new permission");
        }

        // 5. Google chặn chia sẻ và người dùng từ chối tải qua máy: không sao chép, không thay đổi gì.
        fake = new FakeDrive { GrantStatus = HttpStatusCode.Forbidden };
        long askedSize = -1;
        using (var http = fake.Client())
        {
            var canceled = false;
            try { await Copier(http).CopyAsync(a, b, item, phase, size => { askedSize = size; return Task.FromResult(false); }, null, CancellationToken.None); }
            catch (OperationCanceledException) { canceled = true; }
            check(canceled && askedSize == 1048576 && !fake.Calls.Any(x => x.Path.EndsWith("/copy") || x.Method == "DELETE"), "Blocked sharing asks before relaying through this machine and changes nothing when declined");
        }

        // 6. Không gửi chéo sang chính tài khoản đang dùng, không hỗ trợ thư mục.
        using (var http = new FakeDrive().Client())
        {
            var same = false; var folder = false;
            try { await Copier(http).CopyAsync(a, a, item, phase, noRelay, null, CancellationToken.None); } catch (ArgumentException) { same = true; }
            try { await Copier(http).CopyAsync(a, b, new DriveItem { Id = "D", MimeType = "application/vnd.google-apps.folder" }, phase, noRelay, null, CancellationToken.None); } catch (InvalidOperationException) { folder = true; }
            check(same && folder, "Cross copy refuses the same account and folders before any request");
        }

        // 7. Dung lượng tài khoản.
        using (var http = new FakeDrive().Client())
        {
            var quota = await new DriveService(http, (account, _) => Task.FromResult("token-" + account.Id)).GetStorageQuotaAsync(a, CancellationToken.None);
            check(quota.Limit == 2199023255552 && quota.Usage == 835628007219 && quota.Fraction is > 0.37 and < 0.39, "Storage quota is read from Drive about.storageQuota");
        }
    }

    /// <summary>Progress chạy ngay trên luồng gọi, để kiểm tra thứ tự bước không phụ thuộc SynchronizationContext.</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
