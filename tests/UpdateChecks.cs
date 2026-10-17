using DriveLink;
using DriveLink.Services;
using System.IO;

static class UpdateChecks
{
    public static async Task VerifyPackage(string source)
    {
        var root = Path.Combine(Path.GetTempPath(), "DriveLink-update-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var packages = Path.Combine(root, "packages"); Directory.CreateDirectory(packages);
            var locator = new Velopack.Locators.TestVelopackLocator("QuangHuy.DriveLink", "0.0.1", packages);
            var manager = new Velopack.UpdateManager(source, new Velopack.UpdateOptions { ExplicitChannel = "win-x64" }, locator);
            var flow = new AppUpdateFlow(new VelopackUpdateBackend(manager));
            await flow.CheckAsync(CancellationToken.None);
            if (flow.Available?.Version != AppBrand.Version) throw new Exception("Real release feed was not detected: " + flow.Message);
            await flow.DownloadAsync(CancellationToken.None);
            if (!flow.Ready) throw new Exception("Real package did not verify: " + flow.Message);
            Console.WriteLine("PASS Real Velopack feed selects and verifies the complete current package from a simulated older installation.");
            if (Directory.Exists(source))
            {
                var damaged = Path.Combine(root, "damaged"); Directory.CreateDirectory(damaged);
                foreach (var file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(damaged, Path.GetFileName(file)));
                var package = Directory.GetFiles(damaged, "*-full.nupkg").Single();
                using (var stream = File.OpenWrite(package)) stream.WriteByte(0);
                var other = Path.Combine(root, "other-packages"); Directory.CreateDirectory(other);
                var badManager = new Velopack.UpdateManager(damaged, new Velopack.UpdateOptions { ExplicitChannel = "win-x64" }, new Velopack.Locators.TestVelopackLocator("QuangHuy.DriveLink", "0.0.1", other));
                var bad = new AppUpdateFlow(new VelopackUpdateBackend(badManager));
                await bad.CheckAsync(CancellationToken.None); await bad.DownloadAsync(CancellationToken.None);
                if (bad.Ready || !bad.HasError) throw new Exception("Corrupted package was accepted.");
                Console.WriteLine("PASS Real package corruption is rejected before installation.");
            }
        }
        finally { Directory.Delete(root, true); }
    }
    public static async Task Run(Action<bool, string> check, SecureStore store)
    {
        var backend = new UpdateFixture(); var flow = new AppUpdateFlow(backend);
        await flow.CheckAsync(CancellationToken.None);
        check(flow.Available?.Version == "0.9.0" && !flow.Ready && backend.Applied == 0, "Finding an update does not download or install it");
        check(!flow.TryApply(() => true, () => { }), "A package cannot be applied before verified download");
        backend.FailDownload = true;
        await flow.DownloadAsync(CancellationToken.None);
        check(flow.HasError && !flow.Ready && backend.Applied == 0, "Failed package verification blocks installation");
        backend.FailDownload = false;
        await flow.DownloadAsync(new CancellationToken(true));
        check(!flow.Busy && !flow.Ready && backend.Downloads == 1, "Canceled downloads do not become ready or leave updater busy");
        await flow.DownloadAsync(CancellationToken.None);
        check(flow.Ready && flow.Progress == 100 && backend.Applied == 0, "Verified download is ready without automatically restarting");
        check(!flow.TryApply(() => false, () => { }) && backend.Applied == 0, "Active transfers delay installation");
        var restored = false; backend.FailApply = true;
        check(!flow.TryApply(() => true, () => restored = true) && restored && flow.HasError, "Apply failure restores the current application");
        backend.FailApply = false;
        check(flow.TryApply(() => true, () => { }) && backend.Applied == 1, "Idle application applies the verified package on request");
        backend.Target = null;
        await flow.CheckAsync(CancellationToken.None);
        check(flow.Available is null && !flow.Ready, "A new check clears an obsolete prepared update");
        await flow.CheckAsync(new CancellationToken(true));
        check(!flow.Busy, "Canceling a check before entry is handled without a UI exception");
        backend.CanInstall = false;
        await flow.CheckAsync(CancellationToken.None);
        check(flow.Message.Contains("Setup"), "Legacy unpacked builds explain the one-time installer requirement");
        check(new UserSettings().CheckUpdatesOnStartup, "Startup update checks are enabled by default for older settings");
        var saved = store.Load(); saved.Settings.CheckUpdatesOnStartup = false; store.Save(saved);
        check(!store.Load().Settings.CheckUpdatesOnStartup, "User can disable automatic update checks");
    }
}
sealed class UpdateFixture : IAppUpdateBackend
{
    public bool CanInstall { get; set; } = true;
    public bool FailDownload, FailApply;
    public int Downloads, Applied;
    public AppUpdate? Target = new("0.9.0", "Fixture release notes", 100, new object());
    public Task<AppUpdate?> CheckAsync(CancellationToken ct) => Task.FromResult(Target);
    public Task DownloadAsync(AppUpdate update, Action<int> progress, CancellationToken ct)
    {
        Downloads++; progress(50);
        if (FailDownload) throw new InvalidDataException("Fixture integrity failure");
        progress(100); return Task.CompletedTask;
    }
    public void ApplyAndRestart(AppUpdate update) { if (FailApply) throw new IOException("Fixture failure"); Applied++; }
}
