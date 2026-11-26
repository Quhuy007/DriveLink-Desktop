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
public sealed partial class CrossAccountCopier(DriveService drive, IGrantJournal? journal = null)
{
    private static readonly TimeSpan[] PropagationDelays = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)];
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;
    public string RelayFolder { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DriveLink", "Relay");
    private const string FolderType = "application/vnd.google-apps.folder";

    public Task<CrossCopyResult> CopyAsync(Account source, Account target, DriveItem item, IProgress<string> phase,
        Func<long, Task<bool>> confirmRelay, IProgress<TransferProgress>? relayProgress, CancellationToken ct)
        => CopyCoreAsync(source, target, item, "root", phase, confirmRelay, relayProgress, ct);

    /// <summary>Gửi chéo một tệp vào thư mục <paramref name="targetFolder"/> của tài khoản đích ("root" = Drive của tôi).</summary>
    public async Task<CrossCopyResult> CopyCoreAsync(Account source, Account target, DriveItem item, string targetFolder, IProgress<string> phase,
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
            var direct = await drive.CopyAsync(target, item.Id, original.Name, targetFolder, ct);
            return new(direct, CrossCopyMode.DirectAccess, 0, watch.Elapsed, Verify(original, direct), false);
        }

        if (original.Capabilities.CanShare)
        {
            string? permission = null;
            DriveItem? copy = null;
            var revokeFailed = false;
            // Ghi nhật ký TRƯỚC khi cấp: nếu app bị tắt ngang, lần mở sau vẫn biết để thu hồi.
            var entry = journal?.Begin(source.Id, item.Id, target.Email);
            var masked = DisplayPrivacy.MaskEmail(target.Email);
            try
            {
                phase.Report("Đang cấp quyền xem tạm cho tài khoản đích…");
                // Tệp chặn sao chép với người xem thì cần quyền chỉnh sửa tạm để Google cho phép tạo bản sao.
                var role = original.CopyRequiresWriterPermission ? "writer" : "reader";
                try { permission = await drive.GrantTemporaryAccessAsync(source, item.Id, target.Email, role, ct); }
                catch (DriveApiException ex) when (ex.Status == HttpStatusCode.Forbidden)
                {
                    permission = null;
                    if (entry is not null) journal!.Complete(entry); // Google từ chối nên chắc chắn chưa có quyền nào được cấp.
                    entry = null;
                }
                if (permission is not null)
                {
                    if (entry is not null) journal!.Attach(entry, permission);
                    SecurityLog.Write("temporary-grant", $"Cấp quyền {role} tạm cho {masked} trên tệp {item.Id}");
                    phase.Report("Google đang sao chép trên máy chủ…");
                    copy = await CopyWithRetryAsync(target, original, targetFolder, ct);
                }
            }
            finally
            {
                if (permission is not null)
                {
                    phase.Report("Đang thu hồi quyền tạm…");
                    revokeFailed = !await TryRevokeAsync(source, item.Id, permission);
                    SecurityLog.Write(revokeFailed ? "temporary-revoke-failed" : "temporary-revoke", $"{(revokeFailed ? "Chưa thu hồi được" : "Đã thu hồi")} quyền tạm của {masked} trên tệp {item.Id}");
                    // Thu hồi thất bại thì giữ mục nhật ký để lần mở app sau thử lại.
                    if (!revokeFailed && entry is not null) journal!.Complete(entry);
                }
            }
            if (copy is not null) return new(copy, CrossCopyMode.TemporaryShare, 0, watch.Elapsed, Verify(original, copy), revokeFailed);
        }

        return await RelayAsync(source, target, original, targetFolder, phase, confirmRelay, relayProgress, watch, ct);
    }

    private async Task<DriveItem> CopyWithRetryAsync(Account target, DriveItem original, string targetFolder, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return await drive.CopyAsync(target, original.Id, original.Name, targetFolder, ct); }
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

    /// <summary>
    /// Thu hồi các quyền tạm còn sót từ lần chạy trước (app bị tắt ngang hoặc thu hồi thất bại).
    /// Mục chưa có mã quyền (tắt ngang ngay lúc cấp) được tìm theo email đích trên chính tệp đó:
    /// trước khi cấp, app đã kiểm tra tài khoản đích chưa thấy tệp nên quyền trực tiếp của email đó là quyền tạm.
    /// Trả về số quyền đã thu hồi; mục chưa xử lý được giữ lại cho lần sau.
    /// </summary>
    public async Task<int> RecoverAsync(Func<string, Account?> findAccount, CancellationToken ct)
    {
        if (journal is null) return 0;
        var recovered = 0;
        foreach (var entry in journal.Pending())
        {
            if (findAccount(entry.SourceAccountId) is not { } source) continue;
            var masked = DisplayPrivacy.MaskEmail(entry.TargetEmail);
            try
            {
                var permission = entry.PermissionId;
                if (permission is null)
                {
                    var found = (await drive.ListPermissionsAsync(source, entry.FileId, ct)).FirstOrDefault(p =>
                        p.Type == "user" && !p.Inherited && p.Role is "reader" or "writer" && string.Equals(p.Email, entry.TargetEmail, StringComparison.OrdinalIgnoreCase));
                    permission = found?.Id;
                }
                if (permission is null || await TryRevokeAsync(source, entry.FileId, permission))
                {
                    journal.Complete(entry.Id);
                    if (permission is not null) recovered++;
                    SecurityLog.Write("temporary-revoke-recovered", permission is null ? $"Không còn quyền tạm của {masked} trên tệp {entry.FileId}" : $"Đã thu hồi quyền tạm còn sót của {masked} trên tệp {entry.FileId}");
                }
            }
            catch (DriveApiException ex) when (ex.Status == HttpStatusCode.NotFound)
            {
                journal.Complete(entry.Id); // Tệp đã bị xóa: không còn quyền nào để thu hồi.
            }
            catch (Exception ex) when (ex is DriveApiException or HttpRequestException) { /* Giữ lại để thử ở lần mở sau. */ }
        }
        return recovered;
    }

    /// <summary>Xóa thư mục tạm của lần tải qua máy bị gián đoạn trước đó (gọi lúc mở app, khi chưa có lượt gửi chéo nào chạy).</summary>
    public int CleanRelayLeftovers()
    {
        if (!Directory.Exists(RelayFolder)) return 0;
        var removed = 0;
        foreach (var folder in Directory.EnumerateDirectories(RelayFolder))
        {
            try { Directory.Delete(folder, true); removed++; } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return removed;
    }

    private async Task<CrossCopyResult> RelayAsync(Account source, Account target, DriveItem original, string targetFolder, IProgress<string> phase,
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
            var id = await drive.UploadAsync(target, targetFolder, temp, relayProgress ?? new Progress<TransferProgress>(), ct);
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
