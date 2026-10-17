using System.Net.Http.Json;
namespace DriveLink.Services;

public sealed partial class DriveService
{
    private const string FileFields = "id,name,mimeType,size,createdTime,modifiedTime,description,writersCanShare,copyRequiresWriterPermission,webViewLink,owners(displayName,emailAddress,me),capabilities(canTrash,canRename,canCopy,canShare,canDownload,canChangeCopyRequiresWriterPermission,canModifyContentRestriction,canModifyEditorContentRestriction,canRemoveContentRestriction),contentRestrictions(readOnly,reason,ownerRestricted)";
    public async Task<DriveItem> GetFileAsync(Account account, string id, CancellationToken ct)
    {
        using var request = await RequestAsync(account, HttpMethod.Get, Api + $"files/{Uri.EscapeDataString(id)}?supportsAllDrives=true&fields={FileFields}", ct);
        using var response = await http.SendAsync(request, ct); await EnsureAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<DriveItem>(ct) ?? throw new InvalidDataException("Chưa đọc được thông tin file.");
    }
    private async Task PatchFileAsync(Account account, string id, object changes, CancellationToken ct)
    {
        using var request = await RequestAsync(account, HttpMethod.Patch, Api + $"files/{Uri.EscapeDataString(id)}?supportsAllDrives=true&fields=id", ct);
        request.Content = JsonContent.Create(changes);
        using var response = await http.SendAsync(request, ct); await EnsureAsync(response, ct);
    }
    public Task RenameAsync(Account account, string id, string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 255) throw new ArgumentException("Tên file cần từ 1 đến 255 ký tự.");
        return PatchFileAsync(account, id, new { name = name.Trim() }, ct);
    }
    public Task TrashFileAsync(Account account, string id, CancellationToken ct) => PatchFileAsync(account, id, new { trashed = true }, ct);
    public Task SetLockedAsync(Account account, string id, bool locked, CancellationToken ct) => PatchFileAsync(account, id, new { contentRestrictions = new[] { new { readOnly = locked } } }, ct);
    public async Task<DriveItem> CopyAsync(Account account, string id, string name, string folder, CancellationToken ct)
    {
        using var request = await RequestAsync(account, HttpMethod.Post, Api + $"files/{Uri.EscapeDataString(id)}/copy?supportsAllDrives=true&fields={FileFields}", ct);
        request.Content = JsonContent.Create(new { name, parents = new[] { folder } });
        using var response = await http.SendAsync(request, ct); await EnsureAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<DriveItem>(ct) ?? throw new InvalidDataException("Chưa nhận được bản sao. Hãy làm mới Drive trước khi thử lại.");
    }
    private static void ValidateRole(string role)
    {
        if (role is not ("reader" or "commenter" or "writer")) throw new ArgumentException("Quyền truy cập không hợp lệ.");
    }
    public async Task GrantPermissionAsync(Account account, string file, string type, string recipient, string role, bool notify, string message, CancellationToken ct)
    {
        ValidateRole(role);
        if (type is not ("user" or "group" or "anyone" or "domain")) throw new ArgumentException("Loại chia sẻ không hợp lệ.");
        var body = new Dictionary<string, object> { ["type"] = type, ["role"] = role };
        if (type is "user" or "group")
        {
            if (!System.Net.Mail.MailAddress.TryCreate(recipient, out var address) || address.Address != recipient) throw new ArgumentException("Email không hợp lệ.");
            body["emailAddress"] = recipient;
        }
        if (type == "domain")
        {
            if (Uri.CheckHostName(recipient) != UriHostNameType.Dns || !recipient.Contains('.')) throw new ArgumentException("Tên miền không hợp lệ.");
            body["domain"] = recipient; body["allowFileDiscovery"] = false;
        }
        if (type == "anyone") body["allowFileDiscovery"] = false;
        var send = notify && type is "user" or "group";
        var url = Api + $"files/{Uri.EscapeDataString(file)}/permissions?supportsAllDrives=true&sendNotificationEmail={send.ToString().ToLowerInvariant()}";
        if (send && message.Length > 0) url += "&emailMessage=" + Uri.EscapeDataString(message);
        using var request = await RequestAsync(account, HttpMethod.Post, url, ct); request.Content = JsonContent.Create(body);
        using var response = await http.SendAsync(request, ct); await EnsureAsync(response, ct);
    }
    public async Task ChangePermissionAsync(Account account, string file, string permission, string role, CancellationToken ct, bool linkOnly = false)
    {
        ValidateRole(role);
        using var request = await RequestAsync(account, HttpMethod.Patch, Api + $"files/{Uri.EscapeDataString(file)}/permissions/{Uri.EscapeDataString(permission)}?supportsAllDrives=true&fields=id,role", ct);
        request.Content = linkOnly ? JsonContent.Create(new { role, allowFileDiscovery = false }) : JsonContent.Create(new { role });
        using var response = await http.SendAsync(request, ct); await EnsureAsync(response, ct);
    }
    public Task SetSharingOptionsAsync(Account account, string file, bool? writersCanShare, bool? restrictCopy, CancellationToken ct)
    {
        var body = new Dictionary<string, object>();
        if (writersCanShare is { } writers) body["writersCanShare"] = writers;
        if (restrictCopy is { } copy) body["copyRequiresWriterPermission"] = copy;
        if (body.Count == 0) throw new ArgumentException("Không có tùy chọn được phép thay đổi.");
        return PatchFileAsync(account, file, body, ct);
    }
}

