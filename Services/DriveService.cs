using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
namespace DriveLink.Services;

public sealed partial class DriveService(HttpClient http, Func<Account, CancellationToken, Task<string>> getToken)
{
    private const string Api = "https://www.googleapis.com/drive/v3/";
    private static string Q(string value) => value.Replace("\\", "\\\\").Replace("'", "\\'");
    private async Task<HttpRequestMessage> RequestAsync(Account account, HttpMethod method, string url, CancellationToken ct)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await getToken(account, ct));
        return request;
    }
    private static async Task EnsureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var detail = "";
        try
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            detail = json.RootElement.GetProperty("error").GetProperty("message").GetString() ?? "";
        }
        catch (JsonException) { }
        catch (KeyNotFoundException) { }
        var explanation = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "Phiên Google không còn hợp lệ. Hãy kết nối lại tài khoản.",
            HttpStatusCode.Forbidden => "Google từ chối thao tác. Kiểm tra quyền truy cập, dung lượng và chính sách chia sẻ.",
            HttpStatusCode.NotFound => "Không tìm thấy mục hoặc tài khoản này chưa được cấp quyền.",
            (HttpStatusCode)429 => "Google đang giới hạn yêu cầu. Vui lòng thử lại sau.",
            _ => $"Google Drive trả về lỗi {(int)response.StatusCode}."
        };
        throw new DriveApiException(response.StatusCode, explanation + (detail.Length > 0 ? "\n" + detail : ""));
    }
    private async Task<List<T>> ListAsync<T>(Account account, string url, string key, CancellationToken ct)
    {
        List<T> all = [];
        string? next = null;
        do
        {
            using var request = await RequestAsync(account, HttpMethod.Get, url + (next is null ? "" : "&pageToken=" + Uri.EscapeDataString(next)), ct);
            using var response = await http.SendAsync(request, ct);
            await EnsureAsync(response, ct);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (json.RootElement.TryGetProperty(key, out var items)) all.AddRange(items.Deserialize<List<T>>() ?? []);
            next = json.RootElement.TryGetProperty("nextPageToken", out var token) ? token.GetString() : null;
        } while (!string.IsNullOrEmpty(next));
        return all;
    }
    public Task<List<Space>> ListSpacesAsync(Account account, CancellationToken ct) => ListAsync<Space>(account,
        Api + "files?pageSize=100&fields=nextPageToken,files(id,name,capabilities(canTrash))&q=" + Uri.EscapeDataString("trashed = false and mimeType = 'application/vnd.google-apps.folder' and name contains '[DriveLink]'"), "files", ct);
    public async Task TrashSpaceAsync(Account account, string folder, CancellationToken ct)
    {
        using var request = await RequestAsync(account, HttpMethod.Patch, Api + $"files/{Uri.EscapeDataString(folder)}?fields=id,trashed", ct);
        request.Content = JsonContent.Create(new { trashed = true });
        using var response = await http.SendAsync(request, ct);
        await EnsureAsync(response, ct);
    }
    public Task<List<DriveItem>> ListFilesAsync(Account account, string folder, CancellationToken ct) => ListAsync<DriveItem>(account,
        Api + $"files?pageSize=100&orderBy=createdTime%20desc&fields=nextPageToken,files({FileFields})&q=" + Uri.EscapeDataString($"trashed = false and '{Q(folder)}' in parents"), "files", ct);
    public async Task<Space> CreateSpaceAsync(Account account, string name, CancellationToken ct)
    {
        using var request = await RequestAsync(account, HttpMethod.Post, Api + "files?fields=id,name", ct);
        request.Content = JsonContent.Create(new { name = "[DriveLink] " + name, mimeType = "application/vnd.google-apps.folder" });
        using var response = await http.SendAsync(request, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<Space>(ct))!;
    }
    public Task<List<DrivePermission>> ListPermissionsAsync(Account account, string file, CancellationToken ct) => ListAsync<DrivePermission>(account,
        Api + $"files/{Uri.EscapeDataString(file)}/permissions?supportsAllDrives=true&pageSize=100&fields=nextPageToken,permissions(id,emailAddress,displayName,domain,role,type,permissionDetails(inherited))", "permissions", ct);
    public async Task InviteAsync(Account account, string folder, string email, string role, CancellationToken ct)
    {
        if (role is not ("reader" or "writer")) throw new ArgumentException("Quyền không hợp lệ.");
        using var request = await RequestAsync(account, HttpMethod.Post, Api + $"files/{Uri.EscapeDataString(folder)}/permissions?sendNotificationEmail=true", ct);
        request.Content = JsonContent.Create(new { type = "user", role, emailAddress = email });
        using var response = await http.SendAsync(request, ct);
        await EnsureAsync(response, ct);
    }
    public async Task RemovePermissionAsync(Account account, string folder, string permission, CancellationToken ct)
    {
        using var request = await RequestAsync(account, HttpMethod.Delete, Api + $"files/{Uri.EscapeDataString(folder)}/permissions/{Uri.EscapeDataString(permission)}?supportsAllDrives=true", ct);
        using var response = await http.SendAsync(request, ct);
        await EnsureAsync(response, ct);
    }
    private sealed class MeasuredContent(byte[] buffer, int count, Action<long> report) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = count; return true; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => WriteAsync(stream, CancellationToken.None);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct) => WriteAsync(stream, ct);
        private async Task WriteAsync(Stream stream, CancellationToken ct)
        {
            for (int offset = 0; offset < count;)
            {
                var size = Math.Min(65536, count - offset);
                await stream.WriteAsync(buffer.AsMemory(offset, size), ct);
                offset += size;
                report(offset);
            }
        }
    }
}
