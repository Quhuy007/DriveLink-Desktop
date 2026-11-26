using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace DriveLink.Services;

/// <summary>Lỗi Google Drive kèm mã HTTP, để phân biệt "không thấy tệp" hay "bị chặn chia sẻ" mà không phải đọc chuỗi thông báo.</summary>
public sealed class DriveApiException(HttpStatusCode status, string message) : InvalidOperationException(message)
{
    public HttpStatusCode Status { get; } = status;
}

public sealed class StorageQuota
{
    [JsonPropertyName("limit"), JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] public long? Limit { get; set; }
    [JsonPropertyName("usage"), JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] public long Usage { get; set; }
    public double? Fraction => Limit is > 0 ? Math.Clamp((double)Usage / Limit.Value, 0, 1) : null;
}

public enum CrossCopyMode { DirectAccess, TemporaryShare, Relay }

public sealed record CrossCopyResult(DriveItem Copy, CrossCopyMode Mode, long BytesThroughMachine, TimeSpan Elapsed, bool Verified, bool RevokeFailed);

public sealed partial class DriveService
{
    public async Task<StorageQuota> GetStorageQuotaAsync(Account account, CancellationToken ct)
    {
        using var request = await RequestAsync(account, HttpMethod.Get, Api + "about?fields=storageQuota(limit,usage)", ct);
        using var response = await http.SendAsync(request, ct); await EnsureAsync(response, ct);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return json.RootElement.TryGetProperty("storageQuota", out var quota) ? quota.Deserialize<StorageQuota>() ?? new() : new();
    }

    /// <summary>Đọc tệp bằng tài khoản khác; trả về null nếu tài khoản đó chưa thấy tệp (403/404) thay vì báo lỗi.</summary>
    public async Task<DriveItem?> TryGetFileAsync(Account account, string id, CancellationToken ct)
    {
        try { return await GetFileAsync(account, id, ct); }
        catch (DriveApiException ex) when (ex.Status is HttpStatusCode.NotFound or HttpStatusCode.Forbidden) { return null; }
    }

    /// <summary>Cấp quyền tạm cho đúng một email, không gửi thư thông báo. Trả về mã quyền để thu hồi sau.</summary>
    public async Task<string> GrantTemporaryAccessAsync(Account owner, string file, string email, string role, CancellationToken ct)
    {
        ValidateRole(role);
        if (!System.Net.Mail.MailAddress.TryCreate(email, out var address) || address.Address != email) throw new ArgumentException("Email tài khoản đích không hợp lệ.");
        using var request = await RequestAsync(owner, HttpMethod.Post, Api + $"files/{Uri.EscapeDataString(file)}/permissions?supportsAllDrives=true&sendNotificationEmail=false&fields=id", ct);
        request.Content = JsonContent.Create(new { type = "user", role, emailAddress = email });
        using var response = await http.SendAsync(request, ct); await EnsureAsync(response, ct);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return json.RootElement.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } value
            ? value : throw new InvalidDataException("Google không trả về mã quyền tạm.");
    }
}

/// <summary>
/// Gửi một tệp từ tài khoản nguồn sang tài khoản đích.
/// Ưu tiên để Google sao chép trên máy chủ (không tốn băng thông máy). Quyền tạm luôn được thu hồi, kể cả khi lỗi hoặc bị hủy.
/// Chỉ tải qua máy khi Google chặn chia sẻ và người dùng đồng ý.
/// </summary>
public sealed class CrossAccountCopier(DriveService drive)
{
    private static readonly TimeSpan[] PropagationDelays = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)];
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;
    public string RelayFolder { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DriveLink", "Relay");
    private const string FolderType = "application/vnd.google-apps.folder";

    public async Task<CrossCopyResult> CopyAsync(Account source, Account target, DriveItem item, IProgress<string> phase,
        Func<long, Task<bool>> confirmRelay, IProgress<TransferProgress>? relayProgress, CancellationToken ct)
    {
        if (source.Id == target.Id || string.Equals(source.Email, target.Email, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Chọn một tài khoản đích khác tài khoản đang dùng.");
        if (item.MimeType == FolderType) throw new InvalidOperationException("Gửi chéo chưa hỗ trợ thư mục. Hãy chọn từng tệp.");
        var watch = Stopwatch.StartNew();

        phase.Report("Đang đọc thông tin tệp gốc…");
        var original = await drive.GetFileAsync(source, item.Id, ct);
        phase.Report("Đang kiểm tra quyền của tài khoản đích…");
        if (await drive.TryGetFileAsync(target, item.Id, ct) is { Capabilities.CanCopy: true })
        {
            phase.Report("Google đang sao chép trên máy chủ…");
            var direct = await drive.CopyAsync(target, item.Id, original.Name, "root", ct);
            return new(direct, CrossCopyMode.DirectAccess, 0, watch.Elapsed, Verify(original, direct), false);
        }

        if (original.Capabilities.CanShare)
        {
            string? permission = null;
            DriveItem? copy = null;
            var revokeFailed = false;
            try
            {
                phase.Report("Đang cấp quyền xem tạm cho tài khoản đích…");
                // Tệp chặn sao chép với người xem thì cần quyền chỉnh sửa tạm để Google cho phép tạo bản sao.
                var role = original.CopyRequiresWriterPermission ? "writer" : "reader";
                try { permission = await drive.GrantTemporaryAccessAsync(source, item.Id, target.Email, role, ct); }
                catch (DriveApiException ex) when (ex.Status == HttpStatusCode.Forbidden) { permission = null; }
                if (permission is not null)
                {
                    phase.Report("Google đang sao chép trên máy chủ…");
                    copy = await CopyWithRetryAsync(target, original, ct);
                }
            }
            finally
            {
                if (permission is not null)
                {
                    phase.Report("Đang thu hồi quyền tạm…");
                    revokeFailed = !await TryRevokeAsync(source, item.Id, permission);
                }
            }
            if (copy is not null) return new(copy, CrossCopyMode.TemporaryShare, 0, watch.Elapsed, Verify(original, copy), revokeFailed);
        }

        return await RelayAsync(source, target, original, phase, confirmRelay, relayProgress, watch, ct);
    }

    private async Task<DriveItem> CopyWithRetryAsync(Account target, DriveItem original, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return await drive.CopyAsync(target, original.Id, original.Name, "root", ct); }
            // Quyền mới cấp có thể chưa lan tới mọi máy chủ Google trong vài giây đầu.
            catch (DriveApiException ex) when (ex.Status == HttpStatusCode.NotFound && attempt < PropagationDelays.Length)
            {
                await Delay(PropagationDelays[attempt], ct);
            }
        }
    }

    private async Task<bool> TryRevokeAsync(Account source, string file, string permission)
    {
        // Không dùng token của thao tác: kể cả khi người dùng bấm hủy, quyền tạm vẫn phải được thu hồi.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await drive.RemovePermissionAsync(source, file, permission, timeout.Token); return true; }
        catch (DriveApiException ex) when (ex.Status == HttpStatusCode.NotFound) { return true; }
        catch (Exception) { return false; }
    }

    private async Task<CrossCopyResult> RelayAsync(Account source, Account target, DriveItem original, IProgress<string> phase,
        Func<long, Task<bool>> confirmRelay, IProgress<TransferProgress>? relayProgress, Stopwatch watch, CancellationToken ct)
    {
        if (original.MimeType.StartsWith("application/vnd.google-apps.", StringComparison.Ordinal))
            throw new InvalidOperationException("Google không cho chia sẻ tạm tệp này, và tài liệu Google không thể tải qua máy. Hãy chia sẻ tệp cho tài khoản đích trên Drive rồi thử lại.");
        if (!await confirmRelay(original.Size)) throw new OperationCanceledException("Đã hủy gửi chéo qua máy.");
        var workspace = Path.Combine(RelayFolder, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            var safeName = string.Concat(original.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');
            if (safeName.Length == 0) safeName = "tep";
            var temp = Path.Combine(workspace, safeName);
            phase.Report("Google chặn chia sẻ tạm · đang tải tệp về máy này…");
            await drive.DownloadAsync(source, original, temp, ct, relayProgress);
            phase.Report("Đang tải lên tài khoản đích…");
            var id = await drive.UploadAsync(target, "root", temp, relayProgress ?? new Progress<TransferProgress>(), ct);
            var copy = await drive.GetFileAsync(target, id, ct);
            return new(copy, CrossCopyMode.Relay, original.Size * 2, watch.Elapsed, Verify(original, copy), false);
        }
        finally
        {
            try { Directory.Delete(workspace, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>So bản sao với tệp gốc. Trả về false khi Google không cung cấp MD5 (ví dụ tài liệu Google), báo lỗi khi không khớp.</summary>
    private static bool Verify(DriveItem original, DriveItem copy)
    {
        if (original.Md5.Length > 0 && copy.Md5.Length > 0)
            return string.Equals(original.Md5, copy.Md5, StringComparison.OrdinalIgnoreCase)
                ? true : throw new InvalidDataException("Bản sao không khớp MD5 với tệp gốc. Hãy kiểm tra tệp trong Drive đích trước khi dùng.");
        if (original.Size > 0 && copy.Size > 0 && original.Size != copy.Size)
            throw new InvalidDataException("Bản sao không khớp kích thước với tệp gốc. Hãy kiểm tra tệp trong Drive đích trước khi dùng.");
        return false;
    }
}
