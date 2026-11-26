using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace DriveLink.Services;

/// <summary>Điểm tiếp tục của một lượt gửi: phiên tải lên của Google (hiệu lực khoảng 1 tuần) và dấu vết tệp để biết tệp chưa bị sửa.</summary>
public sealed class UploadCheckpoint
{
    public string SessionUri { get; set; } = "";
    public long Length { get; set; }
    public DateTime LastWriteUtc { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
}

public sealed class UploadOptions
{
    /// <summary>Phiên đã lưu từ lần chạy trước; dùng lại nếu tệp chưa đổi và phiên còn hiệu lực.</summary>
    public UploadCheckpoint? Resume { get; init; }
    /// <summary>Gọi khi có phiên mới, để lưu lại và tiếp tục được sau khi tắt app.</summary>
    public Action<UploadCheckpoint>? Checkpoint { get; init; }
    /// <summary>Thuộc tính riêng của DriveLink ghi lên tệp (ví dụ tên máy gửi). Chỉ ứng dụng này đọc.</summary>
    public IReadOnlyDictionary<string, string>? Properties { get; init; }
    /// <summary>Báo lần thử lại (số lần, lỗi) để hiển thị "Mạng chập chờn, đang thử lại…".</summary>
    public Action<int, Exception>? Retrying { get; init; }
}

public sealed class DownloadOptions
{
    /// <summary>Giữ phần đã tải khi bị hủy (ví dụ tắt app) để lần sau tải tiếp; lỗi thật thì luôn xóa.</summary>
    public bool KeepPartialOnCancel { get; init; }
    public Action<int, Exception>? Retrying { get; init; }
}

/// <summary>Dữ liệu nhận được chưa đủ hoặc mạng ngắt giữa chừng: thử lại được.</summary>
public sealed class IncompleteTransferException(string message) : IOException(message);

public sealed partial class DriveService
{
    /// <summary>Khoảng chờ giữa các lần thử lại khi mạng chập chờn (tổng khoảng 2 phút); hết lượt thì báo lỗi.</summary>
    public static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30)];
    public const int ChunkSize = 8 * 1024 * 1024;
    private const int StalledChunkLimit = 3;
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    /// <summary>Lỗi tạm thời: mạng, hết thời gian chờ, máy chủ Google bận (5xx) hoặc giới hạn tốc độ (429).</summary>
    public static bool IsTransient(Exception ex, CancellationToken ct) => ex switch
    {
        OperationCanceledException => !ct.IsCancellationRequested,
        HttpRequestException => true,
        HttpIOException or IncompleteTransferException => true,
        DriveApiException api => api.Status is HttpStatusCode.RequestTimeout or (HttpStatusCode)429 or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout,
        _ => false,
    };

    private static bool CanRetry(Exception error, int attemptsSoFar, CancellationToken ct) => attemptsSoFar < RetryDelays.Length && IsTransient(error, ct);

    private async Task PauseBeforeRetryAsync(int attempt, Exception error, Action<int, Exception>? report, CancellationToken ct)
    {
        report?.Invoke(attempt, error);
        await Delay(RetryDelays[attempt - 1] + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 250)), ct);
    }

    private static bool IsGoogleUploadUri(Uri uri) => uri.Scheme == "https" && (uri.Host == "www.googleapis.com" || uri.Host.EndsWith(".googleapis.com", StringComparison.OrdinalIgnoreCase));

    public async Task<string> UploadAsync(Account account, string folder, string path, IProgress<TransferProgress> progress, CancellationToken ct, UploadOptions? options = null)
    {
        options ??= new();
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        var length = file.Length;
        var lastWrite = File.GetLastWriteTimeUtc(path);
        var meter = new TransferMeter(progress, length);
        meter.Report(0, true);
        Uri? session = null;
        long offset = 0;
        var attempt = 0;

        // 1. Dùng lại phiên cũ nếu tệp chưa đổi: hỏi Google đã nhận tới đâu.
        if (options.Resume is { } resume && resume.Length == length && resume.LastWriteUtc == lastWrite && Uri.TryCreate(resume.SessionUri, UriKind.Absolute, out var saved) && IsGoogleUploadUri(saved))
        {
            while (true)
            {
                try
                {
                    var status = await QueryUploadAsync(account, saved, length, ct);
                    if (status.FileId is { } done) { meter.Report(length, true); return done; }
                    if (status.Offset is { } confirmed) { session = saved; offset = confirmed; }
                    break;
                }
                catch (Exception ex) when (CanRetry(ex, attempt, ct)) { await PauseBeforeRetryAsync(++attempt, ex, options.Retrying, ct); }
            }
        }

        var restarts = 0;
        var stalled = 0;
        while (true)
        {
            try
            {
                if (session is null)
                {
                    session = await StartUploadAsync(account, folder, path, length, options.Properties, ct);
                    offset = 0;
                    options.Checkpoint?.Invoke(new UploadCheckpoint { SessionUri = session.ToString(), Length = length, LastWriteUtc = lastWrite, CreatedUtc = DateTimeOffset.UtcNow });
                }
                // Khối cố định giữ RAM thấp với tệp nhiều GB. Mỗi khối bắt đầu từ đúng chỗ Google đã xác nhận.
                var buffer = new byte[(int)Math.Min(ChunkSize, Math.Max(0, length - offset))];
                file.Seek(offset, SeekOrigin.Begin);
                int count = 0, read;
                while (count < buffer.Length && (read = await file.ReadAsync(buffer.AsMemory(count, buffer.Length - count), ct)) > 0) count += read;
                using var chunk = await RequestAsync(account, HttpMethod.Put, session.ToString(), ct);
                var chunkStart = offset;
                chunk.Content = new MeasuredContent(buffer, count, sent => meter.Report(chunkStart + sent));
                chunk.Content.Headers.ContentType = new("application/octet-stream");
                chunk.Content.Headers.ContentRange = count == 0 ? new ContentRangeHeaderValue(length) : new ContentRangeHeaderValue(offset, offset + count - 1, length);
                using var result = await http.SendAsync(chunk, ct);
                if (result.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                {
                    // Phiên hết hạn: tạo phiên mới một lần và gửi lại từ đầu.
                    if (++restarts > 1) await EnsureAsync(result, ct);
                    session = null; continue;
                }
                if ((int)result.StatusCode == 308)
                {
                    var confirmed = ConfirmedOffset(result);
                    stalled = confirmed > offset ? 0 : stalled + 1;
                    if (stalled >= StalledChunkLimit) throw new InvalidOperationException("Google không xác nhận thêm dữ liệu đã gửi. Hãy kiểm tra tệp trên Drive trước khi gửi lại.");
                    if (confirmed >= length) throw new InvalidOperationException("Google chưa xác nhận tệp hoàn tất. Hãy làm mới danh sách trước khi gửi lại.");
                    offset = confirmed; attempt = 0;
                    continue;
                }
                await EnsureAsync(result, ct);
                if (offset + count < length) throw new InvalidOperationException("Google kết thúc phiên tải lên sớm. Hãy kiểm tra tệp trên Drive.");
                var id = await ReadIdAsync(result, ct);
                meter.Report(length, true);
                return id;
            }
            catch (Exception ex) when (CanRetry(ex, attempt, ct))
            {
                await PauseBeforeRetryAsync(++attempt, ex, options.Retrying, ct);
                if (session is null) continue;
                // Sau lỗi mạng, hỏi lại Google đã nhận tới đâu rồi gửi tiếp từ đó (không gửi trùng, không bỏ sót).
                try
                {
                    var status = await QueryUploadAsync(account, session, length, ct);
                    if (status.FileId is { } done) { meter.Report(length, true); return done; }
                    if (status.Offset is { } confirmed) offset = confirmed; else session = null;
                }
                catch (Exception again) when (IsTransient(again, ct)) { /* Lần lặp sau thử lại. */ }
            }
        }
    }

    private async Task<Uri> StartUploadAsync(Account account, string folder, string path, long length, IReadOnlyDictionary<string, string>? properties, CancellationToken ct)
    {
        using var start = await RequestAsync(account, HttpMethod.Post, "https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable&fields=id", ct);
        start.Content = properties is { Count: > 0 }
            ? JsonContent.Create(new { name = Path.GetFileName(path), parents = new[] { folder }, appProperties = properties })
            : JsonContent.Create(new { name = Path.GetFileName(path), parents = new[] { folder } });
        start.Headers.Add("X-Upload-Content-Type", "application/octet-stream");
        start.Headers.Add("X-Upload-Content-Length", length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var response = await http.SendAsync(start, ct);
        await EnsureAsync(response, ct);
        var uri = response.Headers.Location ?? throw new InvalidOperationException("Google không trả về phiên tải lên.");
        if (!IsGoogleUploadUri(uri)) throw new InvalidOperationException("Địa chỉ tải lên Google không hợp lệ.");
        return uri;
    }

    private sealed record UploadStatus(long? Offset, string? FileId);

    /// <summary>Hỏi trạng thái phiên tải lên (PUT rỗng, Content-Range: bytes */tổng). Offset null nghĩa là phiên đã hết hạn.</summary>
    private async Task<UploadStatus> QueryUploadAsync(Account account, Uri session, long length, CancellationToken ct)
    {
        using var request = await RequestAsync(account, HttpMethod.Put, session.ToString(), ct);
        request.Content = new ByteArrayContent([]);
        request.Content.Headers.ContentRange = new ContentRangeHeaderValue(length);
        using var response = await http.SendAsync(request, ct);
        if ((int)response.StatusCode == 308) return new(ConfirmedOffset(response), null);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) return new(null, null);
        await EnsureAsync(response, ct);
        return new(length, await ReadIdAsync(response, ct));
    }

    private static long ConfirmedOffset(HttpResponseMessage response)
    {
        var range = response.Headers.TryGetValues("Range", out var values) ? values.FirstOrDefault() : null;
        if (range is null || !range.StartsWith("bytes=0-", StringComparison.Ordinal)) return 0;
        return long.TryParse(range["bytes=0-".Length..], out var last) ? last + 1 : 0;
    }

    private static async Task<string> ReadIdAsync(HttpResponseMessage response, CancellationToken ct)
    {
        using var metadata = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var id = metadata.RootElement.TryGetProperty("id", out var value) ? value.GetString() : null;
        if (string.IsNullOrWhiteSpace(id)) throw new InvalidOperationException("Tệp đã gửi nhưng chưa nhận được mã tệp. Hãy làm mới Drive trước khi gửi lại.");
        return id;
    }

    /// <summary>Tệp tạm của một lượt tải, gắn với đúng phiên bản tệp trên Drive (mã, MD5, kích thước) để không ghép nhầm dữ liệu cũ.</summary>
    public static string PartialPath(string destination, DriveItem item)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(item.Id + "|" + item.Md5 + "|" + item.Size)))[..12].ToLowerInvariant();
        return destination + "." + key + ".partial";
    }

    public async Task DownloadAsync(Account account, DriveItem item, string destination, CancellationToken ct, IProgress<TransferProgress>? progress = null, DownloadOptions? options = null)
    {
        if (item.MimeType.StartsWith("application/vnd.google-apps.")) throw new InvalidOperationException("Mục Google Docs hoặc thư mục: hãy dùng nút Mở trên Drive. Bản đầu chưa hỗ trợ xuất tài liệu Google.");
        options ??= new();
        var partial = PartialPath(destination, item);
        var keep = false;
        try
        {
            var attempt = 0;
            var restartedAfterMismatch = false;
            long? total = item.Size > 0 ? item.Size : null;
            var meter = new TransferMeter(progress, total);
            while (true)
            {
                var existing = File.Exists(partial) ? new FileInfo(partial).Length : 0;
                if (total is { } known && existing > known) { File.Delete(partial); existing = 0; }
                try
                {
                    if (total is not { } size || existing < size)
                    {
                        using var request = await RequestAsync(account, HttpMethod.Get, Api + $"files/{Uri.EscapeDataString(item.Id)}?alt=media", ct);
                        if (existing > 0) request.Headers.Range = new RangeHeaderValue(existing, null);
                        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable) { File.Delete(partial); continue; }
                        await EnsureAsync(response, ct);
                        var resumed = response.StatusCode == HttpStatusCode.PartialContent && existing > 0;
                        if (!resumed) existing = 0; // Máy chủ gửi lại từ đầu.
                        total = response.Content.Headers.ContentRange?.Length ?? (item.Size > 0 ? item.Size : response.Content.Headers.ContentLength is { } part ? existing + part : null);
                        meter = new TransferMeter(progress, total);
                        meter.Report(existing, true);
                        await using (var input = await response.Content.ReadAsStreamAsync(ct))
                        await using (var output = new FileStream(partial, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
                        {
                            var buffer = new byte[65536];
                            long written = existing;
                            int read;
                            while ((read = await input.ReadAsync(buffer, ct)) > 0)
                            {
                                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                                written += read;
                                meter.Report(written);
                                if (written > existing + ChunkSize) { existing = written; attempt = 0; } // Có tiến triển thì đặt lại số lần thử.
                            }
                        }
                        var length = new FileInfo(partial).Length;
                        if (total is { } expected && length < expected) throw new IncompleteTransferException("Tệp tải về chưa đủ dữ liệu. Tệp đích chưa bị thay đổi.");
                    }
                    // Đối chiếu MD5 với Google trước khi thay tệp đích.
                    if (!string.IsNullOrEmpty(item.Md5) && !await Md5MatchesAsync(partial, item.Md5, ct))
                    {
                        File.Delete(partial);
                        if (restartedAfterMismatch) throw new IOException("Tệp tải về không khớp mã MD5 của Google Drive. Tệp đích chưa bị thay đổi.");
                        restartedAfterMismatch = true; continue;
                    }
                    meter.Report(new FileInfo(partial).Length, true);
                    File.Move(partial, destination, true);
                    return;
                }
                catch (Exception ex) when (CanRetry(ex, attempt, ct)) { await PauseBeforeRetryAsync(++attempt, ex, options.Retrying, ct); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested && options.KeepPartialOnCancel) { keep = true; throw; }
        finally { if (!keep && File.Exists(partial)) File.Delete(partial); }
    }

    public static async Task<bool> Md5MatchesAsync(string path, string expected, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, true);
        var hash = Convert.ToHexString(await MD5.HashDataAsync(stream, ct));
        return string.Equals(hash, expected, StringComparison.OrdinalIgnoreCase);
    }
}
