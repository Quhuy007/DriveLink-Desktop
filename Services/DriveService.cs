using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
namespace DriveLink.Services;

public sealed class DriveService(HttpClient http, Func<Account, CancellationToken, Task<string>> getToken)
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
        throw new InvalidOperationException(explanation + (detail.Length > 0 ? "\n" + detail : ""));
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
        Api + "files?pageSize=100&orderBy=createdTime%20desc&fields=nextPageToken,files(id,name,mimeType,size,createdTime,webViewLink,owners(displayName,emailAddress,me))&q=" + Uri.EscapeDataString($"trashed = false and '{Q(folder)}' in parents"), "files", ct);
    public async Task<Space> CreateSpaceAsync(Account account, string name, CancellationToken ct)
    {
        using var request = await RequestAsync(account, HttpMethod.Post, Api + "files?fields=id,name", ct);
        request.Content = JsonContent.Create(new { name = "[DriveLink] " + name, mimeType = "application/vnd.google-apps.folder" });
        using var response = await http.SendAsync(request, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<Space>(ct))!;
    }
    public Task<List<DrivePermission>> ListPermissionsAsync(Account account, string file, CancellationToken ct) => ListAsync<DrivePermission>(account,
        Api + $"files/{Uri.EscapeDataString(file)}/permissions?pageSize=100&fields=nextPageToken,permissions(id,emailAddress,role,type)", "permissions", ct);
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
        using var request = await RequestAsync(account, HttpMethod.Delete, Api + $"files/{Uri.EscapeDataString(folder)}/permissions/{Uri.EscapeDataString(permission)}", ct);
        using var response = await http.SendAsync(request, ct);
        await EnsureAsync(response, ct);
    }
    public async Task<string> UploadAsync(Account account, string folder, string path, IProgress<TransferProgress> progress, CancellationToken ct)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        var meter = new TransferMeter(progress, file.Length);
        meter.Report(0, true);
        using var start = await RequestAsync(account, HttpMethod.Post, "https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable&fields=id", ct);
        start.Content = JsonContent.Create(new { name = Path.GetFileName(path), parents = new[] { folder } });
        start.Headers.Add("X-Upload-Content-Type", "application/octet-stream");
        start.Headers.Add("X-Upload-Content-Length", file.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var session = await http.SendAsync(start, ct);
        await EnsureAsync(session, ct);
        var uri = session.Headers.Location ?? throw new InvalidOperationException("Google không trả về phiên tải lên.");
        if (uri.Scheme != "https" || !(uri.Host == "www.googleapis.com" || uri.Host.EndsWith(".googleapis.com", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Địa chỉ tải lên Google không hợp lệ.");
        // Fixed-size chunks keep RAM bounded even for multi-GB files. No blind retry of writes.
        var buffer = new byte[8 * 1024 * 1024];
        long offset = 0;
        do
        {
            int count = 0, read;
            while (count < buffer.Length && (read = await file.ReadAsync(buffer.AsMemory(count, buffer.Length - count), ct)) > 0) count += read;
            using var chunk = await RequestAsync(account, HttpMethod.Put, uri.ToString(), ct);
            var chunkStart = offset;
            chunk.Content = new MeasuredContent(buffer, count, transferred => meter.Report(chunkStart + transferred));
            chunk.Content.Headers.ContentType = new("application/octet-stream");
            chunk.Content.Headers.ContentRange = count == 0 ? new ContentRangeHeaderValue(file.Length) : new ContentRangeHeaderValue(offset, offset + count - 1, file.Length);
            using var result = await http.SendAsync(chunk, ct);
            if ((int)result.StatusCode != 308) await EnsureAsync(result, ct);
            offset += count;
            if ((int)result.StatusCode == 308)
            {
                var range = result.Headers.TryGetValues("Range", out var ranges) ? ranges.FirstOrDefault() : null;
                if (range != $"bytes=0-{offset - 1}") throw new InvalidOperationException("Google chưa xác nhận đầy đủ khối tải lên. Hãy kiểm tra file trên Drive trước khi gửi lại.");
            }
            if ((int)result.StatusCode == 308 && offset >= file.Length) throw new InvalidOperationException("Google chưa xác nhận file hoàn tất. Hãy làm mới danh sách trước khi thử gửi lại.");
            if (result.IsSuccessStatusCode && offset < file.Length) throw new InvalidOperationException("Google kết thúc phiên tải lên sớm. Hãy kiểm tra file trên Drive.");
            if (result.IsSuccessStatusCode)
            {
                using var metadata = JsonDocument.Parse(await result.Content.ReadAsStringAsync(ct));
                var id = metadata.RootElement.GetProperty("id").GetString();
                if (string.IsNullOrWhiteSpace(id)) throw new InvalidOperationException("File đã gửi nhưng chưa nhận được mã file. Hãy làm mới Drive trước khi gửi lại.");
                meter.Report(offset, true);
                return id;
            }
        } while (offset < file.Length);
        throw new InvalidOperationException("Chưa xác nhận được file hoàn tất trên Google Drive.");
    }
    public async Task DownloadAsync(Account account, DriveItem item, string destination, CancellationToken ct, IProgress<TransferProgress>? progress = null)
    {
        if (item.MimeType.StartsWith("application/vnd.google-apps.")) throw new InvalidOperationException("Mục Google Docs hoặc thư mục: hãy dùng nút Mở trên Drive. Bản đầu chưa hỗ trợ xuất tài liệu Google.");
        var temp = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            using var request = await RequestAsync(account, HttpMethod.Get, Api + $"files/{Uri.EscapeDataString(item.Id)}?alt=media", ct);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            await EnsureAsync(response, ct);
            var meter = new TransferMeter(progress, response.Content.Headers.ContentLength ?? (item.Size > 0 ? item.Size : null));
            meter.Report(0, true);
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                var buffer = new byte[65536];
                long transferred = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    transferred += read;
                    meter.Report(transferred);
                }
                if (response.Content.Headers.ContentLength is { } expected && output.Length != expected)
                    throw new IOException("File tải về chưa đủ dữ liệu. File đích chưa bị thay đổi.");
                meter.Report(transferred, true);
            }
            File.Move(temp, destination, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
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
