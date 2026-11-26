using System.Net;
using System.Net.Http.Json;
namespace DriveLink.Services;

public sealed partial class DriveService
{
    /// <summary>Tạo thư mục con trong <paramref name="parent"/> ("root" = Drive của tôi).</summary>
    public async Task<DriveItem> CreateFolderAsync(Account account, string name, string parent, CancellationToken ct)
    {
        using var request = await RequestAsync(account, HttpMethod.Post, Api + "files?supportsAllDrives=true&fields=id,name,mimeType,parents", ct);
        request.Content = JsonContent.Create(new { name, mimeType = "application/vnd.google-apps.folder", parents = new[] { parent } });
        using var response = await http.SendAsync(request, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<DriveItem>(ct))!;
    }
}

/// <summary>Kết quả gửi chéo nhiều mục: số tệp đã sao chép, thư mục đã tạo, lỗi từng tệp.</summary>
public sealed class CrossBatchResult
{
    public int Files { get; set; }
    public int Folders { get; set; }
    public int Verified { get; set; }
    public long BytesThroughMachine { get; set; }
    public bool RevokeFailed { get; set; }
    public List<string> Failures { get; } = [];
    public TimeSpan Elapsed { get; set; }
}

public sealed partial class CrossAccountCopier
{
    /// <summary>
    /// Gửi chéo nhiều tệp và thư mục (giữ nguyên cây thư mục) vào <paramref name="targetFolder"/> của tài khoản đích.
    /// Với thư mục, chỉ cấp MỘT quyền xem tạm trên thư mục gốc (tệp con thừa hưởng), sao chép từng tệp trên máy chủ Google,
    /// rồi thu hồi; tệp nào vẫn không thấy được thì gửi riêng như một tệp lẻ. Lỗi một tệp không dừng cả lượt.
    /// </summary>
    public async Task<CrossBatchResult> CopyManyAsync(Account source, Account target, IReadOnlyList<DriveItem> items, string targetFolder,
        IProgress<string> phase, Func<long, Task<bool>> confirmRelay, IProgress<TransferProgress>? relayProgress, CancellationToken ct)
    {
        if (source.Id == target.Id || string.Equals(source.Email, target.Email, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Chọn một tài khoản đích khác tài khoản đang dùng.");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = new CrossBatchResult();
        var relayAllowed = (bool?)null;
        async Task<bool> AskRelayOnce(long size) => relayAllowed ??= await confirmRelay(size);
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            if (item.MimeType == FolderType) await CopyFolderAsync(source, target, item, targetFolder, phase, AskRelayOnce, relayProgress, result, ct);
            else await CopyOneSafeAsync(source, target, item, targetFolder, phase, AskRelayOnce, relayProgress, result, ct);
        }
        result.Elapsed = watch.Elapsed;
        return result;
    }

    private async Task CopyOneSafeAsync(Account source, Account target, DriveItem item, string folder, IProgress<string> phase,
        Func<long, Task<bool>> confirmRelay, IProgress<TransferProgress>? relayProgress, CrossBatchResult result, CancellationToken ct)
    {
        try
        {
            phase.Report($"Đang gửi “{item.Name}” ({result.Files + 1})…");
            var copied = await CopyCoreAsync(source, target, item, folder, phase, confirmRelay, relayProgress, ct);
            result.Files++;
            if (copied.Verified) result.Verified++;
            result.BytesThroughMachine += copied.BytesThroughMachine;
            result.RevokeFailed |= copied.RevokeFailed;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { result.Failures.Add(item.Name + ": bỏ qua (không gửi qua máy)"); }
        catch (Exception ex) when (ex is DriveApiException or InvalidOperationException or InvalidDataException or IOException or HttpRequestException)
        {
            result.Failures.Add(item.Name + ": " + ex.Message.Split('\n')[0]);
        }
    }

    private async Task CopyFolderAsync(Account source, Account target, DriveItem folder, string parent, IProgress<string> phase,
        Func<long, Task<bool>> confirmRelay, IProgress<TransferProgress>? relayProgress, CrossBatchResult result, CancellationToken ct)
    {
        phase.Report($"Đang tạo thư mục “{folder.Name}” ở tài khoản đích…");
        var created = await drive.CreateFolderAsync(target, folder.Name, parent, ct);
        result.Folders++;
        // Một quyền xem tạm trên thư mục gốc cho cả cây, thay vì cấp/thu hồi từng tệp.
        string? permission = null; string? entry = null;
        var masked = DisplayPrivacy.MaskEmail(target.Email);
        if (await drive.TryGetFileAsync(target, folder.Id, ct) is null)
        {
            entry = journal?.Begin(source.Id, folder.Id, target.Email);
            try
            {
                permission = await drive.GrantTemporaryAccessAsync(source, folder.Id, target.Email, "reader", ct);
                if (entry is not null) journal!.Attach(entry, permission);
                SecurityLog.Write("temporary-grant", $"Cấp quyền reader tạm cho {masked} trên thư mục {folder.Id}");
            }
            catch (DriveApiException ex) when (ex.Status == HttpStatusCode.Forbidden)
            {
                if (entry is not null) journal!.Complete(entry);
                entry = null; permission = null;
            }
        }
        try { await CopyTreeAsync(source, target, folder.Id, created.Id, phase, confirmRelay, relayProgress, result, ct); }
        finally
        {
            if (permission is not null)
            {
                phase.Report("Đang thu hồi quyền tạm trên thư mục…");
                var failed = !await TryRevokeAsync(source, folder.Id, permission);
                result.RevokeFailed |= failed;
                SecurityLog.Write(failed ? "temporary-revoke-failed" : "temporary-revoke", $"{(failed ? "Chưa thu hồi được" : "Đã thu hồi")} quyền tạm của {masked} trên thư mục {folder.Id}");
                if (!failed && entry is not null) journal!.Complete(entry);
            }
        }
    }

    private async Task CopyTreeAsync(Account source, Account target, string sourceFolder, string targetFolder, IProgress<string> phase,
        Func<long, Task<bool>> confirmRelay, IProgress<TransferProgress>? relayProgress, CrossBatchResult result, CancellationToken ct)
    {
        foreach (var child in await drive.ListFilesAsync(source, sourceFolder, ct))
        {
            ct.ThrowIfCancellationRequested();
            if (child.MimeType == FolderType)
            {
                var sub = await drive.CreateFolderAsync(target, child.Name, targetFolder, ct);
                result.Folders++;
                await CopyTreeAsync(source, target, child.Id, sub.Id, phase, confirmRelay, relayProgress, result, ct);
                continue;
            }
            try
            {
                phase.Report($"Google đang sao chép “{child.Name}” ({result.Files + 1})…");
                var copy = await CopyWithRetryAsync(target, child, targetFolder, ct);
                result.Files++;
                if (Verify(child, copy)) result.Verified++;
            }
            // Tài khoản đích vẫn không thấy tệp (ví dụ tệp chặn chia sẻ): gửi riêng như một tệp lẻ.
            catch (DriveApiException ex) when (ex.Status is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
            {
                await CopyOneSafeAsync(source, target, child, targetFolder, phase, confirmRelay, relayProgress, result, ct);
            }
            catch (InvalidDataException ex) { result.Failures.Add(child.Name + ": " + ex.Message); }
        }
    }
}
