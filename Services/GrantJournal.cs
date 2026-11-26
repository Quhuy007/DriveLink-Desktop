using System.Security.Cryptography;
using System.Text.Json;
namespace DriveLink.Services;

public sealed class PendingGrant
{
    public string Id { get; set; } = "";
    public string SourceAccountId { get; set; } = "";
    public string FileId { get; set; } = "";
    public string TargetEmail { get; set; } = "";
    public string? PermissionId { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
}

public interface IGrantJournal
{
    string Begin(string sourceAccountId, string fileId, string targetEmail);
    void Attach(string id, string permissionId);
    void Complete(string id);
    IReadOnlyList<PendingGrant> Pending();
}

/// <summary>
/// Ghi lại mỗi quyền tạm TRƯỚC khi cấp và chỉ xóa sau khi đã thu hồi. Nếu app bị tắt ngang giữa chừng,
/// lần mở sau đọc nhật ký này và thu hồi các quyền còn sót. Lưu mã hóa DPAPI theo người dùng Windows.
/// </summary>
public sealed class GrantJournal : IGrantJournal
{
    private readonly string file;
    private readonly object gate = new();
    private static readonly byte[] Entropy = "DriveLink.PendingGrants.v1"u8.ToArray();

    public GrantJournal(string? directory = null)
    {
        var root = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DriveLink");
        Directory.CreateDirectory(root);
        file = Path.Combine(root, "pending-grants.dat");
    }

    public string Begin(string sourceAccountId, string fileId, string targetEmail)
    {
        var entry = new PendingGrant { Id = Guid.NewGuid().ToString("N"), SourceAccountId = sourceAccountId, FileId = fileId, TargetEmail = targetEmail, CreatedUtc = DateTimeOffset.UtcNow };
        Update(list => list.Add(entry));
        return entry.Id;
    }
    public void Attach(string id, string permissionId) => Update(list => { foreach (var item in list.Where(x => x.Id == id)) item.PermissionId = permissionId; });
    public void Complete(string id) => Update(list => list.RemoveAll(x => x.Id == id));
    public IReadOnlyList<PendingGrant> Pending() { lock (gate) return Load(); }

    private void Update(Action<List<PendingGrant>> change)
    {
        lock (gate)
        {
            var list = Load();
            change(list);
            if (list.Count == 0) { if (File.Exists(file)) File.Delete(file); return; }
            var clear = JsonSerializer.SerializeToUtf8Bytes(list);
            try
            {
                var temp = file + ".tmp";
                File.WriteAllBytes(temp, ProtectedData.Protect(clear, Entropy, DataProtectionScope.CurrentUser));
                File.Move(temp, file, true);
            }
            finally { CryptographicOperations.ZeroMemory(clear); }
        }
    }

    private List<PendingGrant> Load()
    {
        if (!File.Exists(file)) return [];
        try
        {
            var clear = ProtectedData.Unprotect(File.ReadAllBytes(file), Entropy, DataProtectionScope.CurrentUser);
            try { return JsonSerializer.Deserialize<List<PendingGrant>>(clear) ?? []; }
            finally { CryptographicOperations.ZeroMemory(clear); }
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException) { return []; }
    }
}
