using Velopack;
using Velopack.Sources;
namespace DriveLink.Services;

public sealed record AppUpdate(string Version, string Notes, long Bytes, object Package);
public interface IAppUpdateBackend
{
    bool CanInstall { get; }
    Task<AppUpdate?> CheckAsync(CancellationToken ct);
    Task DownloadAsync(AppUpdate update, Action<int> progress, CancellationToken ct);
    void ApplyAndRestart(AppUpdate update);
}
public sealed class VelopackUpdateBackend : IAppUpdateBackend
{
    public const string RepositoryUrl = "https://github.com/Quhuy007/DriveLink-Desktop";
    public const string ReleasesUrl = RepositoryUrl + "/releases/latest";
    private readonly UpdateManager manager;
    public VelopackUpdateBackend(UpdateManager? manager = null)
    {
        this.manager = manager ?? new UpdateManager(new GithubSource(RepositoryUrl, null, false), new UpdateOptions { AllowVersionDowngrade = false });
    }
    public bool CanInstall => manager.IsInstalled;
    public async Task<AppUpdate?> CheckAsync(CancellationToken ct)
    {
        if (!CanInstall) return null;
        var result = await manager.CheckForUpdatesAsync().WaitAsync(ct);
        if (result is null || result.IsDowngrade) return null;
        var target = result.TargetFullRelease;
        return new(target.Version.ToString(), target.NotesMarkdown ?? "", target.Size, result);
    }
    public Task DownloadAsync(AppUpdate update, Action<int> progress, CancellationToken ct)
        => manager.DownloadUpdatesAsync((UpdateInfo)update.Package, progress, ct);
    public void ApplyAndRestart(AppUpdate update) => manager.ApplyUpdatesAndRestart((UpdateInfo)update.Package);
}

// Only a completed, verified download can reach the apply step. Preparing the
// restart happens on the UI thread so new transfers cannot slip past the idle check.
public sealed class AppUpdateFlow(IAppUpdateBackend backend)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public event Action? Changed;
    public bool CanInstall => backend.CanInstall;
    public bool Busy { get; private set; }
    public bool Ready { get; private set; }
    public bool HasError { get; private set; }
    public int Progress { get; private set; }
    public AppUpdate? Available { get; private set; }
    public string Message { get; private set; } = "Sẵn sàng kiểm tra phiên bản mới.";
    private void Notify(string message, bool error = false) { Message = message; HasError = error; Changed?.Invoke(); }
    public async Task CheckAsync(CancellationToken ct)
    {
        if (!gate.Wait(0)) return;
        Busy = true;
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!CanInstall) { Notify("Bản này chưa được cài bằng bộ cập nhật. Tải Setup từ trang phát hành và cài một lần để bật cập nhật tự động."); return; }
            Notify("Đang kiểm tra phiên bản mới…");
            var next = await backend.CheckAsync(ct);
            ct.ThrowIfCancellationRequested();
            if (Available?.Version != next?.Version) Ready = false;
            Available = next;
            Notify(next is null ? "Bạn đang dùng phiên bản mới nhất." : $"Có DriveLink {next.Version}. Bạn có thể cập nhật hoặc để sau.");
        }
        catch (OperationCanceledException) { Notify("Đã hủy kiểm tra cập nhật."); }
        catch { Notify("Chưa kiểm tra được cập nhật. Kiểm tra kết nối mạng rồi thử lại.", true); }
        finally { Busy = false; gate.Release(); Changed?.Invoke(); }
    }
    public async Task DownloadAsync(CancellationToken ct)
    {
        if (!gate.Wait(0)) return;
        Busy = true; Ready = false; Progress = 0;
        try
        {
            ct.ThrowIfCancellationRequested();
            var target = Available ?? throw new InvalidOperationException();
            Notify("Đang tải bản cập nhật…");
            await backend.DownloadAsync(target, value => { Progress = Math.Clamp(value, 0, 100); Changed?.Invoke(); }, ct);
            ct.ThrowIfCancellationRequested();
            Ready = true; Progress = 100;
            Notify("Đã tải và kiểm tra gói cập nhật. Sẵn sàng cài đặt.");
        }
        catch (OperationCanceledException) { Notify("Đã hủy tải bản cập nhật. Bản ứng dụng đang dùng được giữ nguyên."); }
        catch { Notify("Chưa tải hoặc xác minh được gói cập nhật. Bản đang dùng được giữ nguyên; hãy thử lại.", true); }
        finally { Busy = false; gate.Release(); Changed?.Invoke(); }
    }
    public bool TryApply(Func<bool> prepareRestart, Action restoreAfterFailure)
    {
        if (Busy || !Ready || Available is null) return false;
        try
        {
            if (!prepareRestart()) { Notify("Đang chờ các lượt gửi/nhận và thao tác hiện tại hoàn tất…"); return false; }
            Notify("Đang cài bản mới và khởi động lại DriveLink…");
            backend.ApplyAndRestart(Available);
            return true;
        }
        catch
        {
            restoreAfterFailure(); Notify("Chưa thể cài bản mới. DriveLink tiếp tục chạy bản hiện tại; bạn có thể thử lại.", true); return false;
        }
    }
}
