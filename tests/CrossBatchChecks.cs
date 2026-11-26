using DriveLink;
using DriveLink.Services;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

/// <summary>Gửi chéo thư mục/nhiều tệp vào nơi nhận đã chọn, và gửi nguyên cây thư mục từ máy.</summary>
static class CrossBatchChecks
{
    private static HttpResponseMessage Json(string text, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };

    public static async Task Run(Action<bool, string> check, string root)
    {
        var a = new Account { Id = "A", Email = "nguon@example.com" };
        var b = new Account { Id = "B", Email = "dich@example.com" };
        var calls = new List<string>();
        var created = 0;
        var grantedOnFolder = false;
        using var http = new HttpClient(new ActionHandler(async request =>
        {
            var path = request.RequestUri!.AbsolutePath; var query = Uri.UnescapeDataString(request.RequestUri.Query);
            var token = request.Headers.Authorization!.Parameter;
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync();
            calls.Add($"{request.Method} {path} {token}");
            if (request.Method == HttpMethod.Get && path.EndsWith("/files/DIR")) return token == "token-A" ? Json("{\"id\":\"DIR\",\"name\":\"Dự án\",\"mimeType\":\"application/vnd.google-apps.folder\"}") : Json("{}", HttpStatusCode.NotFound);
            if (request.Method == HttpMethod.Post && path.EndsWith("/files/DIR/permissions")) { grantedOnFolder = true; return Json("{\"id\":\"perm-dir\"}"); }
            if (request.Method == HttpMethod.Delete && path.EndsWith("/files/DIR/permissions/perm-dir")) return new HttpResponseMessage(HttpStatusCode.NoContent);
            if (request.Method == HttpMethod.Post && path.EndsWith("/files") && body.Contains("vnd.google-apps.folder"))
            {
                using var doc = JsonDocument.Parse(body);
                created++;
                return Json($"{{\"id\":\"NEW{created}\",\"name\":\"{doc.RootElement.GetProperty("name").GetString()}\"}}");
            }
            if (request.Method == HttpMethod.Get && path.EndsWith("/files") && query.Contains("'DIR' in parents"))
                return Json("{\"files\":[{\"id\":\"F1\",\"name\":\"a.txt\",\"mimeType\":\"text/plain\",\"size\":\"10\",\"md5Checksum\":\"m1\"},{\"id\":\"SUB\",\"name\":\"Ảnh\",\"mimeType\":\"application/vnd.google-apps.folder\"}]}");
            if (request.Method == HttpMethod.Get && path.EndsWith("/files") && query.Contains("'SUB' in parents"))
                return Json("{\"files\":[{\"id\":\"F2\",\"name\":\"b.jpg\",\"mimeType\":\"image/jpeg\",\"size\":\"20\",\"md5Checksum\":\"m2\"}]}");
            if (request.Method == HttpMethod.Post && path.EndsWith("/copy"))
            {
                using var doc = JsonDocument.Parse(body);
                var id = path.Split('/')[^2];
                calls.Add($"COPY {id} -> {doc.RootElement.GetProperty("parents")[0].GetString()}");
                return Json($"{{\"id\":\"C-{id}\",\"name\":\"x\",\"md5Checksum\":\"{(id == "F1" ? "m1" : "m2")}\"}}");
            }
            return Json("{\"error\":{\"message\":\"Unexpected\"}}", HttpStatusCode.BadRequest);
        }));
        var drive = new DriveService(http, (account, _) => Task.FromResult("token-" + account.Id));
        var journalFolder = Path.Combine(root, "batch-grants");
        var copier = new CrossAccountCopier(drive, new GrantJournal(journalFolder)) { Delay = (_, _) => Task.CompletedTask };
        var result = await copier.CopyManyAsync(a, b, [new DriveItem { Id = "DIR", Name = "Dự án", MimeType = "application/vnd.google-apps.folder" }], "SPACE-B",
            new Progress<string>(), _ => Task.FromResult(false), null, CancellationToken.None);
        check(result.Files == 2 && result.Folders == 2 && result.Verified == 2 && result.Failures.Count == 0, "Cross send copies a whole folder tree on Google's servers and verifies each file");
        check(calls.Contains("COPY F1 -> NEW1") && calls.Contains("COPY F2 -> NEW2"), "Folder structure is recreated in the chosen destination of the target account");
        var permissionCalls = calls.Where(c => c.Contains("/permissions")).ToList();
        check(grantedOnFolder && permissionCalls.Count == 2 && permissionCalls[1].StartsWith("DELETE"), "A folder needs only one temporary grant on the folder itself, revoked afterwards");
        check(new GrantJournal(journalFolder).Pending().Count == 0, "Folder grant leaves nothing pending after revoke");

        // Gửi nguyên cây thư mục từ máy: tạo đúng thư mục con rồi đưa từng tệp vào hàng gửi.
        var local = Path.Combine(root, "tree-src"); Directory.CreateDirectory(Path.Combine(local, "con", "chau"));
        File.WriteAllText(Path.Combine(local, "1.txt"), "1"); File.WriteAllText(Path.Combine(local, "con", "2.txt"), "2"); File.WriteAllText(Path.Combine(local, "con", "chau", "3.txt"), "3");
        var folders = new List<(string Name, string Parent)>(); var uploads = new List<string>();
        using var treeHttp = new HttpClient(new ActionHandler(async request =>
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync();
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/drive/v3/files") && body.Contains("vnd.google-apps.folder"))
            {
                using var doc = JsonDocument.Parse(body);
                lock (folders) folders.Add((doc.RootElement.GetProperty("name").GetString()!, doc.RootElement.GetProperty("parents")[0].GetString()!));
                return Json($"{{\"id\":\"D{folders.Count}\",\"name\":\"x\"}}");
            }
            if (request.Method == HttpMethod.Post)
            {
                using var doc = JsonDocument.Parse(body);
                lock (uploads) uploads.Add(doc.RootElement.GetProperty("name").GetString() + "@" + doc.RootElement.GetProperty("parents")[0].GetString());
                var r = Json("{}"); r.Headers.Location = new Uri("https://www.googleapis.com/upload/t" + Guid.NewGuid().ToString("N")); return r;
            }
            return Json("{\"id\":\"up\"}");
        }));
        var center = new TransferCenter(new DriveService(treeHttp, (_, _) => Task.FromResult("t")) { Delay = (_, _) => Task.CompletedTask }, new FolderCompressor(), () => new UserSettings(), Path.Combine(root, "tree-center"));
        var done = 0; var all = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        center.Completed += _ => { if (Interlocked.Increment(ref done) == 3) all.TrySetResult(); };
        var count = await center.EnqueueTreeAsync(a, new Space { Id = "SPACE", Name = "[DriveLink] Làm việc" }, local, CancellationToken.None);
        await all.Task.WaitAsync(TimeSpan.FromSeconds(10));
        check(count == 3 && folders.SequenceEqual([("tree-src", "SPACE"), ("con", "D1"), ("chau", "D2")]) && uploads.OrderBy(x => x).SequenceEqual(["1.txt@D1", "2.txt@D2", "3.txt@D3"]),
            "Sending a folder tree recreates every subfolder on Drive and uploads each file into its own folder");
    }
}
