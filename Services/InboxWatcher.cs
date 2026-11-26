using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace DriveLink.Services;

/// <summary>Dấu "đã nhận" ghi lên tệp: mỗi máy một khóa riêng (dlRcv_ + mã máy), giá trị là thời điểm và tên máy.</summary>
public static class ReceiptMarks
{
    public const string Prefix = "dlRcv_";
    public static string ThisMachineKey { get; } = KeyFor(Environment.MachineName + "|" + Environment.UserName);
    public static string KeyFor(string machine) => Prefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(machine)))[..10].ToLowerInvariant();
    /// <summary>Google giới hạn khóa + giá trị 124 byte: tên máy được cắt ngắn.</summary>
    public static string Value(DateTimeOffset when) => when.UtcDateTime.ToString("yyyyMMddHHmm") + "|" + (Environment.MachineName.Length > 40 ? Environment.MachineName[..40] : Environment.MachineName);
}

public sealed partial class DriveService
{
    public async Task<string> GetStartPageTokenAsync(Account account, CancellationToken ct)
    {
        using var request = await RequestAsync(account, HttpMethod.Get, Api + "changes/startPageToken?supportsAllDrives=true", ct);
        using var response = await http.SendAsync(request, ct);
        await EnsureAsync(response, ct);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return json.RootElement.GetProperty("startPageToken").GetString() ?? throw new InvalidOperationException("Google không trả mã theo dõi thay đổi.");
    }

    /// <summary>Các tệp vừa thay đổi kể từ mã <paramref name="token"/>; trả kèm mã mới để lần sau hỏi tiếp.</summary>
    public async Task<(List<DriveItem> Files, string NextToken)> ListChangesAsync(Account account, string token, CancellationToken ct)
    {
        List<DriveItem> files = [];
        var page = token;
        while (true)
        {
            using var request = await RequestAsync(account, HttpMethod.Get, Api + $"changes?pageToken={Uri.EscapeDataString(page)}&pageSize=200&includeRemoved=false&includeItemsFromAllDrives=true&supportsAllDrives=true&spaces=drive&fields=nextPageToken,newStartPageToken,changes(fileId,removed,file({FileFields},trashed))", ct);
            using var response = await http.SendAsync(request, ct);
            await EnsureAsync(response, ct);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            foreach (var change in json.RootElement.GetProperty("changes").EnumerateArray())
                if (change.TryGetProperty("file", out var file) && file.Deserialize<DriveItem>() is { } item) files.Add(item);
            if (json.RootElement.TryGetProperty("newStartPageToken", out var next)) return (files, next.GetString()!);
            page = json.RootElement.GetProperty("nextPageToken").GetString()!;
        }
    }

    /// <summary>Ghi dấu "máy này đã nhận" lên tệp. Cần quyền sửa; không có quyền thì trả false (chỉ ghi nhận trên máy).</summary>
    public async Task<bool> MarkReceivedAsync(Account account, string fileId, CancellationToken ct)
    {
        using var request = await RequestAsync(account, HttpMethod.Patch, Api + $"files/{Uri.EscapeDataString(fileId)}?supportsAllDrives=true&fields=id", ct);
        request.Content = JsonContent.Create(new { appProperties = new Dictionary<string, string> { [ReceiptMarks.ThisMachineKey] = ReceiptMarks.Value(DateTimeOffset.UtcNow) } });
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound) return false;
        await EnsureAsync(response, ct);
        return true;
    }
}

/// <summary>Một tệp mới do người khác gửi vào không gian chung.</summary>
public sealed record IncomingFile(Account Account, Space Space, DriveItem File);

/// <summary>
/// Phát hiện tệp mới gần như ngay lập tức bằng nguồn thay đổi của Google Drive (changes API), hỏi mỗi vài giây:
/// nhẹ hơn nhiều so với tải lại danh sách. Mã theo dõi được lưu (DPAPI) nên tệp đến khi app đang tắt cũng được báo ở lần mở sau.
/// Chỉ báo tệp nằm trong không gian [DriveLink], không phải của chính tài khoản này, không phải thư mục, chưa bị xóa.
/// </summary>
public sealed class InboxWatcher
{
    private readonly DriveService drive;
    private readonly ProtectedList<WatchToken> store;
    private readonly List<WatchToken> tokens;
    private readonly HashSet<string> seen = [];
    public event Action<IncomingFile>? Arrived;
    /// <summary>Các không gian của từng tài khoản (cập nhật khi danh sách không gian đổi).</summary>
    public Func<Account, IReadOnlyList<Space>> SpacesOf { get; set; } = _ => [];

    public sealed class WatchToken
    {
        public string AccountId { get; set; } = "";
        public string Token { get; set; } = "";
        public DateTimeOffset SavedUtc { get; set; }
    }

    public InboxWatcher(DriveService drive, string folder)
    {
        this.drive = drive;
        store = new(Path.Combine(folder, "watch.dat"), "DriveLink.Watch.v1");
        tokens = store.Load();
    }

    /// <summary>Một lượt kiểm tra cho một tài khoản. Lần đầu chỉ lấy mã bắt đầu (không báo tệp cũ).</summary>
    public async Task<int> CheckAsync(Account account, CancellationToken ct)
    {
        var entry = tokens.FirstOrDefault(t => t.AccountId == account.Id);
        if (entry is null || string.IsNullOrEmpty(entry.Token) || DateTimeOffset.UtcNow - entry.SavedUtc > TimeSpan.FromDays(14))
        {
            var start = await drive.GetStartPageTokenAsync(account, ct);
            Save(account.Id, start);
            return 0;
        }
        var (files, next) = await drive.ListChangesAsync(account, entry.Token, ct);
        var spaces = SpacesOf(account).ToDictionary(s => s.Id);
        var found = 0;
        foreach (var file in files)
        {
            if (file.Trashed || file.IsMine || file.MimeType.StartsWith("application/vnd.google-apps.", StringComparison.Ordinal)) continue;
            if (file.ReceivedHere || file.Parents?.FirstOrDefault(spaces.ContainsKey) is not { } parent) continue;
            if (!seen.Add(account.Id + "/" + file.Id)) continue; // Một tệp có thể đổi nhiều lần (tải lên xong, đổi tên…): chỉ báo một lần.
            found++;
            Arrived?.Invoke(new IncomingFile(account, spaces[parent], file));
        }
        Save(account.Id, next);
        return found;
    }

    private void Save(string accountId, string token)
    {
        tokens.RemoveAll(t => t.AccountId == accountId);
        tokens.Add(new WatchToken { AccountId = accountId, Token = token, SavedUtc = DateTimeOffset.UtcNow });
        store.Save(tokens);
    }

    public void Forget(string accountId) { tokens.RemoveAll(t => t.AccountId == accountId); store.Save(tokens); }

    /// <summary>Tên tệp không trùng trong thư mục Hộp thư đến: "báo cáo.pdf" → "báo cáo (1).pdf".</summary>
    public static string UniquePath(string folder, string name)
    {
        var safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');
        if (safe.Length == 0) safe = "tệp";
        var path = Path.Combine(folder, safe);
        for (var i = 1; File.Exists(path) || Directory.Exists(path); i++)
            path = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(safe)} ({i}){Path.GetExtension(safe)}");
        return path;
    }
}
