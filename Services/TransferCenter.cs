using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
namespace DriveLink.Services;

public enum TransferDirection { Upload, Download }
public enum TransferState { Queued, Preparing, Running, Retrying, Done, Failed, Canceled, Interrupted }

/// <summary>Một lượt gửi hoặc nhận trong phiên làm việc hiện tại (hiển thị ở màn Hàng đợi).</summary>
public sealed class TransferItem : INotifyPropertyChanged
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public TransferDirection Direction { get; init; }
    public string Name { get => name; set => Set(ref name, value); }
    public string Space { get; init; } = "";
    public string AccountEmail { get; init; } = "";
    public string LocalPath { get; set; } = "";
    public string? FileId { get; set; }
    public TransferState State { get => state; set { if (Set(ref state, value)) { Raise(nameof(StateLabel)); Raise(nameof(IsActive)); Raise(nameof(CanCancel)); } } }
    public long Bytes { get => bytes; set { if (Set(ref bytes, value)) { Raise(nameof(Percent)); Raise(nameof(AmountLabel)); } } }
    public long? Total { get => total; set { if (Set(ref total, value)) { Raise(nameof(Percent)); Raise(nameof(AmountLabel)); } } }
    public double Speed { get => speed; set { if (Set(ref speed, value)) Raise(nameof(SpeedLabel)); } }
    public string Note { get => note; set => Set(ref note, value); }
    public DateTimeOffset QueuedAt { get; } = DateTimeOffset.Now;
    public double Percent => Total is > 0 ? Math.Clamp(Bytes * 100d / Total.Value, 0, 100) : State == TransferState.Done ? 100 : 0;
    public string AmountLabel => DriveItem.FormatSize(Bytes) + (Total is { } t ? " / " + DriveItem.FormatSize(t) : "");
    public string SpeedLabel => State is TransferState.Running && Speed > 0 ? DriveItem.FormatSize((long)Speed) + "/s" : "";
    public string DirectionLabel => Direction == TransferDirection.Upload ? "Gửi" : "Nhận";
    public bool IsActive => State is TransferState.Queued or TransferState.Preparing or TransferState.Running or TransferState.Retrying;
    public bool CanCancel => IsActive;
    public string StateLabel => State switch
    {
        TransferState.Queued => "Đang chờ",
        TransferState.Preparing => "Đang chuẩn bị",
        TransferState.Running => "Đang truyền",
        TransferState.Retrying => "Đang thử lại",
        TransferState.Done => "Xong",
        TransferState.Failed => "Lỗi",
        TransferState.Canceled => "Đã hủy",
        _ => "Tạm dừng",
    };
    internal Action? Cancel { get; set; }
    internal DateTimeOffset LastProgress { get; set; }

    private string name = "", note = "";
    private TransferState state;
    private long bytes; private long? total; private double speed;
    public event PropertyChangedEventHandler? PropertyChanged;
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; Raise(property!); return true;
    }
    private void Raise(string property) => PropertyChanged?.Invoke(this, new(property));
}

/// <summary>Lượt truyền chưa xong được lưu xuống đĩa để tiếp tục sau khi tắt app.</summary>
public sealed class TransferRecord
{
    public string Id { get; set; } = "";
    public TransferDirection Direction { get; set; }
    public string AccountId { get; set; } = "";
    public string SpaceId { get; set; } = "";
    public string SpaceName { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public UploadCheckpoint? Checkpoint { get; set; }
    public string FileId { get; set; } = "";
    public string FileName { get; set; } = "";
    public string FileMd5 { get; set; } = "";
    public long FileSize { get; set; }
    public string MimeType { get; set; } = "";
    public string Destination { get; set; } = "";
    /// <summary>Không gian có khóa mã hóa đầu cuối dùng cho tệp này (rỗng = không mã hóa).</summary>
    public string E2eSpaceId { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; set; }
}

/// <summary>Một dòng trong nhật ký Hoạt động (lượt gửi/nhận đã kết thúc).</summary>
public sealed class ActivityEntry
{
    public DateTimeOffset Time { get; set; }
    public TransferDirection Direction { get; set; }
    public string Name { get; set; } = "";
    public string Space { get; set; } = "";
    public string Account { get; set; } = "";
    public TransferState Result { get; set; }
    public long Bytes { get; set; }
    public double Seconds { get; set; }
    public string Note { get; set; } = "";
    public string LocalPath { get; set; } = "";
    public string FileId { get; set; } = "";
    public string TimeLabel => Time.LocalDateTime.ToString("dd/MM HH:mm");
    public string DirectionLabel => Direction == TransferDirection.Upload ? "Gửi" : "Nhận";
    public string ResultLabel => Result switch { TransferState.Done => "Xong", TransferState.Failed => "Lỗi", TransferState.Canceled => "Đã hủy", _ => "Tạm dừng" };
    public string SizeLabel => Bytes > 0 ? DriveItem.FormatSize(Bytes) : "";
}

/// <summary>Danh sách JSON lưu mã hóa DPAPI theo người dùng Windows (tên tệp, đường dẫn không lộ khi máy bị sao chép dữ liệu).</summary>
internal sealed class ProtectedList<T>(string file, string entropy)
{
    private readonly byte[] key = System.Text.Encoding.UTF8.GetBytes(entropy);
    private readonly object gate = new();
    public List<T> Load()
    {
        lock (gate)
        {
            if (!File.Exists(file)) return [];
            try
            {
                var clear = ProtectedData.Unprotect(File.ReadAllBytes(file), key, DataProtectionScope.CurrentUser);
                try { return JsonSerializer.Deserialize<List<T>>(clear) ?? []; } finally { CryptographicOperations.ZeroMemory(clear); }
            }
            catch (Exception ex) when (ex is CryptographicException or JsonException or IOException) { return []; }
        }
    }
    public void Save(List<T> items)
    {
        lock (gate)
        {
            try
            {
                if (items.Count == 0) { if (File.Exists(file)) File.Delete(file); return; }
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                var clear = JsonSerializer.SerializeToUtf8Bytes(items);
                try { File.WriteAllBytes(file + ".tmp", ProtectedData.Protect(clear, key, DataProtectionScope.CurrentUser)); File.Move(file + ".tmp", file, true); }
                finally { CryptographicOperations.ZeroMemory(clear); }
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}

/// <summary>
/// Điều phối mọi lượt gửi/nhận: hàng đợi riêng cho từng chiều, số tệp song song tự điều chỉnh 2–6,
/// tự thử lại khi mạng chập chờn, lưu lượt chưa xong để tiếp tục sau khi tắt app, và ghi nhật ký Hoạt động.
/// </summary>
public sealed class TransferCenter
{
    public const int ActivityLimit = 300;
    public static readonly IReadOnlyDictionary<string, string> SenderProperties = new Dictionary<string, string>
    {
        ["dlSenderMachine"] = Environment.MachineName.Length > 60 ? Environment.MachineName[..60] : Environment.MachineName,
        ["dlApp"] = "DriveLink",
    };
    private readonly DriveService drive;
    private readonly FolderCompressor compressor;
    private readonly Func<UserSettings> settings;
    private readonly ProtectedList<TransferRecord> journal;
    private readonly ProtectedList<ActivityEntry> activityStore;
    private readonly List<TransferRecord> records;
    private readonly List<ActivityEntry> activity;
    public AdaptiveConcurrency UploadLimit { get; } = new();
    public AdaptiveConcurrency DownloadLimit { get; } = new();
    public TransferLane Uploads { get; }
    public TransferLane Downloads { get; }
    /// <summary>Khóa mã hóa đầu cuối theo không gian; null = tắt tính năng.</summary>
    public E2eKeys? Keys { get; init; }
    private readonly string workRoot;
    public List<TransferItem> Items { get; } = [];
    public bool ShuttingDown { get; private set; }
    public event Action? Changed;
    public event Action<TransferItem>? Completed;
    public Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.UtcNow;

    public TransferCenter(DriveService drive, FolderCompressor compressor, Func<UserSettings> settings, string? folder = null)
    {
        this.drive = drive; this.compressor = compressor; this.settings = settings;
        var root = folder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DriveLink");
        workRoot = Path.Combine(root, "Encrypting");
        journal = new(Path.Combine(root, "transfers.dat"), "DriveLink.Transfers.v1");
        activityStore = new(Path.Combine(root, "activity.dat"), "DriveLink.Activity.v1");
        records = journal.Load();
        activity = activityStore.Load();
        Uploads = new(() => UploadLimit.Current);
        Downloads = new(() => DownloadLimit.Current);
        Uploads.Changed += () => Changed?.Invoke();
        Downloads.Changed += () => Changed?.Invoke();
        UploadLimit.Changed += Uploads.Pump;
        DownloadLimit.Changed += Downloads.Pump;
    }

    public IReadOnlyList<ActivityEntry> Activity { get { lock (activity) return activity.ToList(); } }
    /// <summary>Lượt truyền chưa xong từ lần chạy trước (chỉ đọc lúc mở app, trước khi có lượt mới).</summary>
    public IReadOnlyList<TransferRecord> Unfinished() { lock (records) return records.Where(r => !Items.Any(i => i.Id == r.Id)).ToList(); }

    public TransferItem EnqueueUpload(Account account, Space space, string path, TransferRecord? resume = null, string? keySpaceId = null)
    {
        var record = resume ?? new TransferRecord { Id = Guid.NewGuid().ToString("N"), Direction = TransferDirection.Upload, AccountId = account.Id, SpaceId = space.Id, SpaceName = space.ToString(), SourcePath = path, CreatedUtc = DateTimeOffset.UtcNow, E2eSpaceId = keySpaceId ?? space.Id };
        var item = new TransferItem { Id = record.Id, Direction = TransferDirection.Upload, Name = Path.GetFileName(path.TrimEnd('\\', '/')), Space = record.SpaceName, AccountEmail = account.Email, LocalPath = path };
        Persist(record);
        Start(item, Uploads, async ct =>
        {
            PackedFolder? packed = null;
            string? encryptedFolder = null;
            try
            {
                var source = path;
                if (Directory.Exists(path))
                {
                    item.State = TransferState.Preparing; item.Note = "Đang nén thư mục…";
                    packed = await compressor.PackAsync(path, settings(), new Progress<string>(text => item.Note = text), ct);
                    source = packed.ArchivePath; item.Name = Path.GetFileName(source);
                    record.Checkpoint = null; // Tệp nén mới khác tệp cũ: không dùng lại phiên.
                }
                var properties = SenderProperties;
                if (Keys?.KeyFor(record.E2eSpaceId) is { } key)
                {
                    // Mã hóa trên máy này trước khi tải lên: Google chỉ nhận dữ liệu đã mã hóa.
                    item.State = TransferState.Preparing; item.Note = "Đang mã hóa…";
                    encryptedFolder = Path.Combine(workRoot, record.Id);
                    Directory.CreateDirectory(encryptedFolder);
                    var encrypted = Path.Combine(encryptedFolder, Path.GetFileName(source) + E2eCrypto.Extension);
                    try { await E2eCrypto.EncryptFileAsync(source, encrypted, key, ct); } finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(key); }
                    source = encrypted; record.Checkpoint = null; // Bản mã mới mỗi lần nên không dùng lại phiên cũ.
                    properties = new Dictionary<string, string>(SenderProperties) { ["dlE2e"] = "1", ["dlE2eSpace"] = record.E2eSpaceId };
                }
                item.State = TransferState.Running; item.Note = "";
                var id = await drive.UploadAsync(account, space.Id, source, Meter(item), ct, new UploadOptions
                {
                    Resume = packed is null && encryptedFolder is null ? record.Checkpoint : null,
                    Checkpoint = checkpoint => { record.Checkpoint = checkpoint; Persist(record); },
                    Properties = properties,
                    Retrying = (attempt, error) => OnRetry(item, UploadLimit, attempt),
                });
                item.FileId = id;
                if (packed is not null) item.Note = $"{DriveItem.FormatSize(packed.OriginalBytes)} → {DriveItem.FormatSize(packed.ArchiveBytes)}";
            }
            finally
            {
                packed?.Dispose();
                if (encryptedFolder is not null) try { Directory.Delete(encryptedFolder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }, record);
        return item;
    }

    /// <summary>
    /// Gửi nguyên cây thư mục: tạo thư mục tương ứng trên Drive (theo thứ tự cha → con), rồi đưa mọi tệp vào hàng gửi,
    /// mỗi tệp vào đúng thư mục của nó. Tệp/thư mục ẩn hệ thống và lối tắt liên kết thư mục bị bỏ qua. Trả về số tệp đã đưa vào hàng.
    /// </summary>
    public async Task<int> EnqueueTreeAsync(Account account, Space space, string directory, CancellationToken ct)
    {
        var root = await drive.CreateFolderAsync(account, Path.GetFileName(directory.TrimEnd('\\', '/')), space.Id, ct);
        var count = 0;
        var pending = new Queue<(string Local, string FolderId, string Label)>();
        pending.Enqueue((directory, root.Id, space + "/" + root.Name));
        while (pending.Count > 0)
        {
            var (local, folderId, label) = pending.Dequeue();
            var info = new DirectoryInfo(local);
            foreach (var entry in info.EnumerateFileSystemInfos())
            {
                ct.ThrowIfCancellationRequested();
                if (entry.Attributes.HasFlag(FileAttributes.System) || entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                if (entry is DirectoryInfo sub)
                {
                    var created = await drive.CreateFolderAsync(account, sub.Name, folderId, ct);
                    pending.Enqueue((sub.FullName, created.Id, label + "/" + sub.Name));
                }
                else { EnqueueUpload(account, new Space { Id = folderId, Name = label }, entry.FullName, keySpaceId: space.Id); count++; }
            }
        }
        return count;
    }

    public TransferItem EnqueueDownload(Account account, DriveItem file, string destination, string spaceName, TransferRecord? resume = null)
    {
        var record = resume ?? new TransferRecord { Id = Guid.NewGuid().ToString("N"), Direction = TransferDirection.Download, AccountId = account.Id, SpaceName = spaceName, FileId = file.Id, FileName = file.Name, FileMd5 = file.Md5, FileSize = file.Size, MimeType = file.MimeType, Destination = destination, CreatedUtc = DateTimeOffset.UtcNow, E2eSpaceId = file.IsEncrypted ? file.E2eSpace : "" };
        var item = new TransferItem { Id = record.Id, Direction = TransferDirection.Download, Name = file.PlainName, Space = spaceName, AccountEmail = account.Email, LocalPath = destination, FileId = file.Id, Total = file.Size > 0 ? file.Size : null };
        Persist(record);
        Start(item, Downloads, async ct =>
        {
            item.State = TransferState.Running;
            var options = new DownloadOptions { KeepPartialOnCancel = true, Retrying = (attempt, _) => OnRetry(item, DownloadLimit, attempt) };
            if (record.E2eSpaceId.Length == 0) { await drive.DownloadAsync(account, file, destination, ct, Meter(item), options); return; }
            var key = Keys?.KeyFor(record.E2eSpaceId) ?? throw new InvalidOperationException("Tệp được mã hóa đầu cuối. Hãy nhập mật khẩu của không gian (chuột phải không gian → Mã hóa đầu cuối) rồi tải lại.");
            var cipher = destination + E2eCrypto.Extension;
            try
            {
                await drive.DownloadAsync(account, file, cipher, ct, Meter(item), options);
                item.Note = "Đang giải mã…";
                await E2eCrypto.DecryptFileAsync(cipher, destination, key, ct);
                item.Note = "Đã giải mã";
                File.Delete(cipher);
            }
            catch (System.Security.Cryptography.CryptographicException ex) { try { File.Delete(cipher); } catch (IOException) { } throw new InvalidOperationException(ex.Message); }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(key); }
        }, record);
        return item;
    }

    public static DriveItem ToDriveItem(TransferRecord record) => new() { Id = record.FileId, Name = record.FileName, Md5 = record.FileMd5, Size = record.FileSize, MimeType = record.MimeType,
        AppProperties = record.E2eSpaceId.Length > 0 ? new() { ["dlE2e"] = "1", ["dlE2eSpace"] = record.E2eSpaceId } : null };

    /// <summary>Bỏ lượt chưa xong từ lần trước: xóa phần đã tải dở.</summary>
    public void Discard(TransferRecord record)
    {
        if (record.Direction == TransferDirection.Download && record.Destination.Length > 0)
            try { File.Delete(DriveService.PartialPath(record.Destination, ToDriveItem(record))); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        Forget(record.Id);
    }

    public void CancelAll(TransferDirection direction) => (direction == TransferDirection.Upload ? Uploads : Downloads).CancelAll();
    public void Cancel(TransferItem item) => item.Cancel?.Invoke();

    /// <summary>Tắt app: dừng mọi lượt nhưng giữ điểm tiếp tục để lần mở sau làm tiếp.</summary>
    public void Shutdown() { ShuttingDown = true; Uploads.CancelAll(); Downloads.CancelAll(); }

    /// <summary>Gọi mỗi ~10 giây: tổng tốc độ từng chiều để điều chỉnh số tệp song song.</summary>
    public void Tune()
    {
        var now = Clock();
        UploadLimit.Sample(Items.Where(i => i.Direction == TransferDirection.Upload && i.State == TransferState.Running).Sum(i => i.Speed), Uploads.Waiting > 0, now);
        DownloadLimit.Sample(Items.Where(i => i.Direction == TransferDirection.Download && i.State == TransferState.Running).Sum(i => i.Speed), Downloads.Waiting > 0, now);
    }

    public (int Active, int Waiting, long Bytes, long Total, double Speed, bool Retrying) Summary(TransferDirection direction)
    {
        var lane = direction == TransferDirection.Upload ? Uploads : Downloads;
        var active = Items.Where(i => i.Direction == direction && i.IsActive).ToList();
        var stale = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(2);
        return (lane.Running, lane.Waiting, active.Sum(i => i.Bytes), active.Sum(i => i.Total ?? 0), active.Where(i => i.LastProgress > stale).Sum(i => i.Speed), active.Any(i => i.State == TransferState.Retrying));
    }

    private void Start(TransferItem item, TransferLane lane, Func<CancellationToken, Task> work, TransferRecord record)
    {
        Items.Insert(0, item);
        for (var i = Items.Count - 1; i >= 0 && Items.Count > 200; i--) if (!Items[i].IsActive) Items.RemoveAt(i);
        var started = Clock();
        var task = lane.Enqueue(async ct =>
        {
            await work(ct);
            item.State = TransferState.Done; item.Bytes = item.Total ?? item.Bytes;
        }, out var cancel);
        item.Cancel = cancel;
        _ = Observe();
        Changed?.Invoke();

        async Task Observe()
        {
            try { await task; Forget(record.Id); }
            catch (OperationCanceledException)
            {
                if (ShuttingDown) { item.State = TransferState.Interrupted; return; } // Giữ điểm tiếp tục cho lần mở sau.
                item.State = TransferState.Canceled;
                Discard(record);
            }
            catch (Exception ex) { item.State = TransferState.Failed; item.Note = ex.Message; Discard(record); }
            Log(item, started);
            Completed?.Invoke(item);
            Changed?.Invoke();
        }
    }

    private IProgress<TransferProgress> Meter(TransferItem item) => new InlineProgress(value =>
    {
        item.Bytes = value.BytesTransferred; item.Total = value.TotalBytes ?? item.Total; item.Speed = value.BytesPerSecond; item.LastProgress = DateTimeOffset.UtcNow;
        if (item.State == TransferState.Retrying) { item.State = TransferState.Running; item.Note = ""; }
    });

    private void OnRetry(TransferItem item, AdaptiveConcurrency limit, int attempt)
    {
        item.State = TransferState.Retrying;
        item.Note = $"Mạng chập chờn, đang thử lại (lần {attempt}/{DriveService.RetryDelays.Length})…";
        limit.ReportRetry(Clock());
    }

    private void Log(TransferItem item, DateTimeOffset started)
    {
        var entry = new ActivityEntry { Time = DateTimeOffset.Now, Direction = item.Direction, Name = item.Name, Space = item.Space, Account = DisplayPrivacy.MaskEmail(item.AccountEmail), Result = item.State, Bytes = item.State == TransferState.Done ? item.Total ?? item.Bytes : item.Bytes, Seconds = (Clock() - started).TotalSeconds, Note = item.Note, LocalPath = item.LocalPath, FileId = item.FileId ?? "" };
        lock (activity)
        {
            activity.Insert(0, entry);
            if (activity.Count > ActivityLimit) activity.RemoveRange(ActivityLimit, activity.Count - ActivityLimit);
            activityStore.Save(activity);
        }
    }

    /// <summary>Ghi một lượt truyền không qua hàng đợi (ví dụ gửi/nhận thẳng trong LAN) vào nhật ký Hoạt động.</summary>
    public void AddActivity(ActivityEntry entry)
    {
        lock (activity)
        {
            activity.Insert(0, entry);
            if (activity.Count > ActivityLimit) activity.RemoveRange(ActivityLimit, activity.Count - ActivityLimit);
            activityStore.Save(activity);
        }
        Changed?.Invoke();
    }

    public void ClearActivity() { lock (activity) { activity.Clear(); activityStore.Save(activity); } Changed?.Invoke(); }

    private void Persist(TransferRecord record) { lock (records) { if (!records.Contains(record)) { records.RemoveAll(r => r.Id == record.Id); records.Add(record); } journal.Save(records); } }
    private void Forget(string id) { lock (records) { if (records.RemoveAll(r => r.Id == id) > 0) journal.Save(records); } }

    private sealed class InlineProgress(Action<TransferProgress> report) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value) => report(value);
    }
}
