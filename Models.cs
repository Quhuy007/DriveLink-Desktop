using System.Text.Json.Serialization;
namespace DriveLink;

public sealed class Account
{
    public string Id { get; set; } = "";
    public string Email { get; set; } = "";
    public string Name { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    /// <summary>Google đã thu hồi hoặc làm hết hạn quyền (invalid_grant): cần đăng nhập lại tài khoản này. Lưu lại để nhắc cả sau khi mở lại app.</summary>
    public bool NeedsReauth { get; set; }
    public override string ToString() => DisplayPrivacy.MaskEmail(Email) + (NeedsReauth ? " · cần đăng nhập lại" : "");
}
public sealed class OAuthConfig
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
}
public sealed class SavedState
{
    public OAuthConfig Config { get; set; } = new();
    public List<Account> Accounts { get; set; } = [];
    public string? SelectedAccountId { get; set; }
    public Dictionary<string, string> LocalFiles { get; set; } = [];
    public Dictionary<string, SpacePreference> SpacePreferences { get; set; } = [];
    public UserSettings Settings { get; set; } = new();
    public CloudConnection Cloud { get; set; } = new();
}
public sealed class CloudConnection
{
    public string ProjectUrl { get; set; } = "";
    public string PublishableKey { get; set; } = "";
    public string LastAppliedRevision { get; set; } = "";
}
public sealed class CloudRevision
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("client_id")] public string ClientId { get; set; } = "";
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    [JsonPropertyName("created_at")] public DateTimeOffset CreatedAt { get; set; }
    [JsonPropertyName("config_json")] public string ConfigJson { get; set; } = "";
    [JsonPropertyName("payload_format")] public string PayloadFormat { get; set; } = "legacy";
    public override string ToString() => $"{Label} · {CreatedAt.ToLocalTime():dd/MM/yyyy HH:mm} · {Id[..Math.Min(8, Id.Length)]}";
}
public sealed class UserSettings
{
    public double UiScale { get; set; }
    public bool CheckUpdatesOnStartup { get; set; } = true;
    public bool ShowDropOverlay { get; set; } = true;
    public double DropOverlayOpacity { get; set; } = 0.65;
    public string ArchiveFormat { get; set; } = "zip";
    public string CompressionTool { get; set; } = "auto";
    public bool HighCompression { get; set; }
    public bool RunInBackground { get; set; }
    public bool StartWithWindows { get; set; }
    /// <summary>auto (nhẹ khi máy ≤ 4 GB RAM), lite hoặc full.</summary>
    public string Performance { get; set; } = "auto";
    /// <summary>dark hoặc light.</summary>
    public string Theme { get; set; } = "dark";
    /// <summary>Báo bằng thông báo Windows khi có tệp mới từ người khác.</summary>
    public bool NotifyNewFiles { get; set; } = true;
    /// <summary>Tự tải tệp mới vào thư mục Hộp thư đến (trừ tệp chạy được và tệp quá lớn).</summary>
    public bool AutoDownloadInbox { get; set; } = true;
    /// <summary>Giới hạn tự tải, MB; 0 = không giới hạn.</summary>
    public int AutoDownloadMaxMb { get; set; } = 1024;
    /// <summary>Rỗng = Downloads\DriveLink - Hộp thư đến.</summary>
    public string InboxFolder { get; set; } = "";
    /// <summary>Gửi thư mục: zip (nén thành một tệp) hoặc tree (giữ nguyên cây thư mục trên Drive).</summary>
    public string FolderUploadMode { get; set; } = "zip";
    /// <summary>Truyền thẳng trong mạng LAN (mặc định tắt: chỉ mở cổng khi người dùng bật).</summary>
    public bool LanEnabled { get; set; }
    /// <summary>Bố cục bento của màn hình chính (thứ tự, cỡ, ô ẩn, bên đặt danh sách tệp).</summary>
    public Ui.BentoLayout Layout { get; set; } = Ui.BentoLayout.Default();
    public string ResolveInboxFolder() => InboxFolder.Length > 0 ? InboxFolder : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "DriveLink - Hộp thư đến");
}
public sealed class SpacePreference
{
    public string Color { get; set; } = "#3865F3";
    public bool Pinned { get; set; }
    public bool Hidden { get; set; }
}
public sealed class DriveCapabilities
{
    [JsonPropertyName("canTrash")] public bool CanTrash { get; set; }
    [JsonPropertyName("canRename")] public bool CanRename { get; set; }
    [JsonPropertyName("canCopy")] public bool CanCopy { get; set; }
    [JsonPropertyName("canShare")] public bool CanShare { get; set; }
    [JsonPropertyName("canDownload")] public bool CanDownload { get; set; }
    [JsonPropertyName("canModifyContentRestriction")] public bool CanModifyContentRestriction { get; set; }
    [JsonPropertyName("canModifyEditorContentRestriction")] public bool CanModifyEditorContentRestriction { get; set; }
    [JsonPropertyName("canRemoveContentRestriction")] public bool CanRemoveContentRestriction { get; set; }
    [JsonPropertyName("canChangeCopyRequiresWriterPermission")] public bool CanChangeCopyRequiresWriterPermission { get; set; }
}
public sealed class Space
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("capabilities")] public DriveCapabilities Capabilities { get; set; } = new();
    [JsonIgnore] public string Color { get; set; } = "#3865F3";
    [JsonIgnore] public bool Pinned { get; set; }
    [JsonIgnore] public string PinLabel => Pinned ? "★" : "";
    [JsonIgnore] public string DisplayName => ToString();
    public override string ToString() => Name.Replace("[DriveLink] ", "");
}
public sealed class DriveOwner
{
    [JsonPropertyName("displayName")] public string Name { get; set; } = "";
    [JsonPropertyName("emailAddress")] public string Email { get; set; } = "";
    [JsonPropertyName("me")] public bool Me { get; set; }
}
public sealed class DriveItem
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("mimeType")] public string MimeType { get; set; } = "";
    [JsonPropertyName("size"), JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] public long Size { get; set; }
    [JsonPropertyName("md5Checksum")] public string Md5 { get; set; } = "";
    [JsonPropertyName("createdTime")] public DateTimeOffset CreatedAt { get; set; }
    [JsonPropertyName("webViewLink")] public string WebViewLink { get; set; } = "";
    [JsonPropertyName("owners")] public List<DriveOwner> Owners { get; set; } = [];
    [JsonPropertyName("modifiedTime")] public DateTimeOffset ModifiedAt { get; set; }
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("writersCanShare")] public bool WritersCanShare { get; set; } = true;
    [JsonPropertyName("copyRequiresWriterPermission")] public bool CopyRequiresWriterPermission { get; set; }
    [JsonPropertyName("capabilities")] public DriveCapabilities Capabilities { get; set; } = new();
    [JsonPropertyName("contentRestrictions")] public List<DriveContentRestriction> ContentRestrictions { get; set; } = [];
    public bool IsLocked => ContentRestrictions.Any(x => x.ReadOnly);
    /// <summary>Google chỉ trả danh sách quyền khi tài khoản được phép chia sẻ tệp; không có thì coi như không biết.</summary>
    [JsonPropertyName("permissions")] public List<DrivePermission>? Permissions { get; set; }
    public bool IsPublic => Permissions?.Any(x => x.Type == "anyone") == true;
    public bool IsMine => Owners.Any(x => x.Me);
    /// <summary>Thuộc tính riêng của DriveLink trên tệp: tên máy gửi (dlSenderMachine) và các máy đã nhận (dlRcv_…).</summary>
    [JsonPropertyName("appProperties")] public Dictionary<string, string>? AppProperties { get; set; }
    [JsonPropertyName("parents")] public List<string>? Parents { get; set; }
    [JsonPropertyName("trashed")] public bool Trashed { get; set; }
    public string SenderMachine => AppProperties?.GetValueOrDefault("dlSenderMachine") ?? "";
    /// <summary>Tệp đã mã hóa đầu cuối bởi DriveLink (đuôi .dlenc); cần khóa của không gian ghi trong dlE2eSpace để mở.</summary>
    public bool IsEncrypted => AppProperties?.GetValueOrDefault("dlE2e") == "1" || Name.EndsWith(Services.E2eCrypto.Extension, StringComparison.OrdinalIgnoreCase);
    public string E2eSpace => AppProperties?.GetValueOrDefault("dlE2eSpace") ?? Parents?.FirstOrDefault() ?? "";
    /// <summary>Tên tệp sau khi giải mã (bỏ đuôi .dlenc).</summary>
    public string PlainName => Name.EndsWith(Services.E2eCrypto.Extension, StringComparison.OrdinalIgnoreCase) ? Name[..^Services.E2eCrypto.Extension.Length] : Name;
    public int ReceivedCount => AppProperties?.Keys.Count(k => k.StartsWith(Services.ReceiptMarks.Prefix, StringComparison.Ordinal)) ?? 0;
    public bool ReceivedHere => AppProperties?.ContainsKey(Services.ReceiptMarks.ThisMachineKey) == true;
    /// <summary>Dòng phụ dưới tên tệp: loại tệp · máy gửi · số máy đã nhận.</summary>
    public string DetailLabel => (IsEncrypted ? (Path.GetExtension(PlainName).TrimStart('.').ToUpperInvariant() is { Length: > 0 } e ? e + " · mã hóa đầu cuối" : "Mã hóa đầu cuối") : Kind) + (SenderMachine.Length > 0 ? " · từ máy " + SenderMachine : "") + (ReceivedCount > 0 ? $" · {ReceivedCount} máy đã nhận" : "");
    public string OwnerEmail => Owners.FirstOrDefault()?.Email ?? "";
    public string OwnerLabel => OwnerEmail.Length == 0 ? "Không xác định" : DisplayPrivacy.MaskEmail(OwnerEmail);
    public string NameLabel => (IsLocked ? "🔒 " : "") + (IsEncrypted ? "🔐 " + PlainName : Name);
    public string SizeLabel => FormatSize(Size);
    public string TimeLabel => CreatedAt == default ? "Chưa có thông tin" : CreatedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
    public string Kind => MimeType == "application/vnd.google-apps.folder" ? "THƯ MỤC" : Path.GetExtension(Name).TrimStart('.').ToUpperInvariant() is { Length: > 0 } ext ? ext : "FILE";
    public string BrowserUrl => MimeType == "application/vnd.google-apps.folder"
        ? "https://drive.google.com/drive/folders/" + Uri.EscapeDataString(Id)
        : "https://drive.google.com/file/d/" + Uri.EscapeDataString(Id) + "/view";
    public static string FormatSize(long bytes) => bytes >= 1099511627776 ? $"{bytes / 1099511627776d:0.##} TB" : bytes >= 1073741824 ? $"{bytes / 1073741824d:0.#} GB" : bytes >= 1048576 ? $"{bytes / 1048576d:0.#} MB" : bytes >= 1024 ? $"{bytes / 1024d:0.#} KB" : $"{bytes} B";
}
public sealed record TransferProgress(long BytesTransferred, long? TotalBytes, double BytesPerSecond)
{
    public double Percent => TotalBytes is > 0 ? Math.Clamp(BytesTransferred * 100d / TotalBytes.Value, 0, 100) : 0;
    public string SpeedLabel => DriveItem.FormatSize((long)Math.Max(0, BytesPerSecond)) + "/s";
    public string AmountLabel => DriveItem.FormatSize(BytesTransferred) + (TotalBytes.HasValue ? " / " + DriveItem.FormatSize(TotalBytes.Value) : "");
}
public sealed class DrivePermission
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("emailAddress")] public string Email { get; set; } = "";
    [JsonPropertyName("role")] public string Role { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("displayName")] public string Name { get; set; } = "";
    [JsonPropertyName("domain")] public string Domain { get; set; } = "";
    [JsonPropertyName("permissionDetails")] public List<PermissionDetail> Details { get; set; } = [];
    public bool Inherited => Details.Any(x => x.Inherited);
    public override string ToString() => $"{(string.IsNullOrEmpty(Email) ? (Domain.Length > 0 ? Domain : Type) : DisplayPrivacy.MaskEmail(Email))}  ·  {Role}";
}
public sealed class PermissionDetail
{
    [JsonPropertyName("inherited")] public bool Inherited { get; set; }
}
public sealed class DriveContentRestriction
{
    [JsonPropertyName("readOnly")] public bool ReadOnly { get; set; }
    [JsonPropertyName("reason")] public string Reason { get; set; } = "";
    [JsonPropertyName("ownerRestricted")] public bool OwnerRestricted { get; set; }
}
public static class DisplayPrivacy
{
    public static string MaskEmail(string email)
    {
        var at = email.LastIndexOf('@');
        if (at < 1) return email;
        return email[..Math.Min(6, at)] + "******" + email[at..];
    }
    public static double ResolveScale(double saved, double workWidth, double workHeight) =>
        saved > 0 && double.IsFinite(saved) ? Math.Clamp(saved, .6, 1.5) : workWidth < 1150 || workHeight < 700 ? .8 : 1;
}

