using DriveLink;
using DriveLink.Services;
using System.Net;
using System.Net.Http;
using System.Text.Json;

static class FileActionChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        check(DisplayPrivacy.MaskEmail("abcdefghi@gmail.com") == "abcdef******@gmail.com", "Long email keeps only six prefix characters and six stars");
        check(DisplayPrivacy.MaskEmail("ab@gmail.com") == "ab******@gmail.com", "Short email still gets exactly six stars");
        check(DisplayPrivacy.MaskEmail("abcdef@gmail.com") == "abcdef******@gmail.com", "Six-character email receives the same fixed mask");
        check(DisplayPrivacy.ResolveScale(0, 1092, 590) == .8 && DisplayPrivacy.ResolveScale(0, 1920, 1080) == 1, "Automatic scale adapts to small logical desktop dimensions");
        check(DisplayPrivacy.ResolveScale(4, 1366, 768) == 1.5 && DisplayPrivacy.ResolveScale(.1, 1366, 768) == .6, "Saved zoom is bounded to usable limits");
        var account = new Account { Id = "fixture", Email = "fixture@example.com" };
        var requests = new List<(string Method, string Url, string Body)>();
        using var http = new HttpClient(new ActionHandler(async request =>
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync();
            requests.Add((request.Method.Method, request.RequestUri!.AbsoluteUri, body));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"id\":\"fixture-copy\",\"name\":\"Copy\",\"capabilities\":{\"canRename\":true,\"canTrash\":true,\"canDownload\":true},\"contentRestrictions\":[{\"readOnly\":true}]}" , System.Text.Encoding.UTF8, "application/json") };
        }));
        var service = new DriveService(http, (_, _) => Task.FromResult("fixture-only-token"));
        var detail = await service.GetFileAsync(account, "file/id", CancellationToken.None);
        check(detail.IsLocked && detail.Capabilities.CanRename && requests[^1].Url.Contains("file%2Fid"), "File details parse lock and actual capabilities with an encoded file ID");
        await service.RenameAsync(account, "id", "Tên mới.txt", CancellationToken.None);
        using (var doc = JsonDocument.Parse(requests[^1].Body)) check(requests[^1].Method == "PATCH" && doc.RootElement.GetProperty("name").GetString() == "Tên mới.txt", "Rename patches only file metadata");
        var copy = await service.CopyAsync(account, "id", "Bản sao", "workspace", CancellationToken.None);
        using (var doc = JsonDocument.Parse(requests[^1].Body)) check(copy.Id == "fixture-copy" && requests[^1].Method == "POST" && doc.RootElement.GetProperty("parents")[0].GetString() == "workspace", "Copy creates a server-side copy in the current workspace");
        await service.TrashFileAsync(account, "id", CancellationToken.None);
        using (var doc = JsonDocument.Parse(requests[^1].Body)) check(requests[^1].Method == "PATCH" && doc.RootElement.GetProperty("trashed").GetBoolean(), "Trash is recoverable and never calls permanent file DELETE");
        foreach (var locked in new[] { true, false })
        {
            await service.SetLockedAsync(account, "id", locked, CancellationToken.None);
            using var doc = JsonDocument.Parse(requests[^1].Body);
            check(doc.RootElement.GetProperty("contentRestrictions")[0].GetProperty("readOnly").GetBoolean() == locked, "Lock and unlock use Drive content restrictions");
        }
        await service.GrantPermissionAsync(account, "id", "user", "recipient@example.com", "commenter", true, "Xin chào & xem file", CancellationToken.None);
        using (var doc = JsonDocument.Parse(requests[^1].Body)) check(doc.RootElement.GetProperty("role").GetString() == "commenter" && requests[^1].Url.Contains("sendNotificationEmail=true") && requests[^1].Url.Contains("emailMessage="), "Sharing supports commenter role and an encoded notification message");
        await service.GrantPermissionAsync(account, "id", "anyone", "", "reader", true, "", CancellationToken.None);
        using (var doc = JsonDocument.Parse(requests[^1].Body)) check(!doc.RootElement.GetProperty("allowFileDiscovery").GetBoolean() && !doc.RootElement.TryGetProperty("emailAddress", out _) && requests[^1].Url.Contains("sendNotificationEmail=false"), "Link sharing creates a nondiscoverable anyone permission without recipient email");
        await service.GrantPermissionAsync(account, "id", "domain", "example.com", "writer", false, "", CancellationToken.None);
        using (var doc = JsonDocument.Parse(requests[^1].Body)) check(doc.RootElement.GetProperty("domain").GetString() == "example.com", "Workspace domain sharing sends the selected domain");
        await service.ChangePermissionAsync(account, "id", "p/id", "writer", CancellationToken.None);
        check(requests[^1].Method == "PATCH" && requests[^1].Url.Contains("p%2Fid"), "Changing a role targets only the selected encoded permission ID");
        await service.RemovePermissionAsync(account, "id", "p", CancellationToken.None);
        check(requests[^1].Method == "DELETE" && requests[^1].Url.Contains("/permissions/p?"), "Revoking access deletes the permission rather than the file");
        var before = requests.Count;
        try { await service.GrantPermissionAsync(account, "id", "anyone", "", "owner", false, "", CancellationToken.None); } catch (ArgumentException) { }
        check(requests.Count == before, "Ordinary sharing cannot accidentally assign ownership");
        await service.ChangePermissionAsync(account, "id", "public", "reader", CancellationToken.None, linkOnly: true);
        using (var doc = JsonDocument.Parse(requests[^1].Body)) check(!doc.RootElement.GetProperty("allowFileDiscovery").GetBoolean(), "Saving link access removes public discoverability from an existing broad grant");
        await service.SetSharingOptionsAsync(account, "id", false, true, CancellationToken.None);
        using (var doc = JsonDocument.Parse(requests[^1].Body)) check(!doc.RootElement.GetProperty("writersCanShare").GetBoolean() && doc.RootElement.GetProperty("copyRequiresWriterPermission").GetBoolean(), "Sharing options preserve the intended Drive booleans");
    }
}
sealed class ActionHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => handle(request);
}

