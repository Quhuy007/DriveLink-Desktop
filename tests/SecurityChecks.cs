using DriveLink;
using DriveLink.Services;
using Microsoft.Win32;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

/// <summary>Kiểm tra các lớp bảo mật: mã hóa token, khóa quản trị, nhật ký quyền tạm, chữ ký bản cập nhật, cảnh báo tệp chạy được.</summary>
static class SecurityChecks
{
    private static string? registryPath;

    /// <summary>Khóa AES cố định trong bộ nhớ: kiểm thử không bao giờ mở hộp thoại Windows Hello.</summary>
    public sealed class MemoryKeyProvider(string scheme, bool available = true) : ITokenKeyProvider
    {
        private readonly byte[] key = RandomNumberGenerator.GetBytes(32);
        public int Requests { get; private set; }
        public string Scheme => scheme;
        public Task<bool> IsAvailableAsync() => Task.FromResult(available);
        public Task<byte[]> GetKeyAsync(CancellationToken ct) { Requests++; return Task.FromResult(key); }
    }

    /// <summary>Gọi trước mọi kiểm thử: đưa nhật ký, trạng thái khóa quản trị và khóa token vào thư mục/khóa Registry tạm.</summary>
    public static void Isolate(string root)
    {
        registryPath = @"Software\QuangHuy\DriveLink-Checks-" + Guid.NewGuid().ToString("N");
        SecurityLog.Root = root;
        AdminLockout.StorageFolder = Path.Combine(root, "admin");
        AdminLockout.RegistryPath = registryPath;
        TokenVault.Shared = new TokenVault(new MemoryKeyProvider(HelloKeyProvider.SchemeName), new MemoryKeyProvider(LocalKeyProvider.SchemeName));
    }

    public static void Cleanup()
    {
        if (registryPath is not null) Registry.CurrentUser.DeleteSubKeyTree(registryPath, false);
    }

    public static async Task Run(Action<bool, string> check, string root)
    {
        await TokenVaultChecks(check);
        AdminLockoutChecks(check, root);
        await GrantRecoveryChecks(check, root);
        UpdateTrustChecks(check);
        check(RiskyFiles.IsExecutable("cai-dat.EXE") && RiskyFiles.IsExecutable("script.ps1") && RiskyFiles.IsExecutable("loi-tat.lnk ") && RiskyFiles.IsExecutable("bao-cao.docm")
            && !RiskyFiles.IsExecutable("anh.jpg") && !RiskyFiles.IsExecutable("tai-lieu.pdf") && !RiskyFiles.IsExecutable("ghi-chu.txt"), "Executable and macro files are recognized for a warning; documents and media are not");
        var log = File.ReadAllText(SecurityLog.FilePath);
        check(log.Contains("admin-password-failed") && log.Contains("temporary-revoke-recovered") && !log.Contains("secret-refresh") && !log.Contains("target@example.com"),
            "Security log records events without tokens or full email addresses");
    }

    private static async Task TokenVaultChecks(Action<bool, string> check)
    {
        var hello = new MemoryKeyProvider(HelloKeyProvider.SchemeName);
        var local = new MemoryKeyProvider(LocalKeyProvider.SchemeName);
        var vault = new TokenVault(hello, local);
        var wrapped = await vault.ProtectAsync("secret-refresh-token", CancellationToken.None);
        check(TokenVault.IsProtected(wrapped) && wrapped.StartsWith("dlv1:h1:") && !wrapped.Contains("secret-refresh"), "Token is wrapped with the Windows Hello key when Hello is available");
        check(await vault.UnprotectAsync(wrapped, CancellationToken.None) == "secret-refresh-token", "Wrapped token round-trips");
        check(await vault.UnprotectAsync("legacy-plain-token", CancellationToken.None) == "legacy-plain-token" && await vault.ProtectAsync(wrapped, CancellationToken.None) == wrapped,
            "Legacy plaintext tokens still load and protected tokens are not wrapped twice");
        var raw = Convert.FromBase64String(wrapped["dlv1:h1:".Length..]);
        raw[^1] ^= 0x55;
        var tampered = "dlv1:h1:" + Convert.ToBase64String(raw);
        var rejected = false;
        try { await vault.UnprotectAsync(tampered, CancellationToken.None); } catch (CryptographicException) { rejected = true; }
        var schemeSwapped = false;
        try { await vault.UnprotectAsync("dlv1:l1:" + wrapped["dlv1:h1:".Length..], CancellationToken.None); } catch (CryptographicException) { schemeSwapped = true; }
        check(rejected && schemeSwapped, "Tampered or relabeled token is rejected instead of being used");
        var otherMachine = false;
        try { await new TokenVault(new MemoryKeyProvider("h1"), local).UnprotectAsync(wrapped, CancellationToken.None); } catch (CryptographicException) { otherMachine = true; }
        check(otherMachine, "A copied accounts.dat cannot be opened without this machine's key");

        var noHello = new TokenVault(new MemoryKeyProvider(HelloKeyProvider.SchemeName, available: false), local);
        var fallback = await noHello.ProtectAsync("secret-refresh-2", CancellationToken.None);
        check(fallback.StartsWith("dlv1:l1:") && await noHello.DescribeAsync() != await vault.DescribeAsync(), "Without Windows Hello the local key is used and Settings shows which protection is active");

        var path = @"Software\QuangHuy\DriveLink-Checks-" + Guid.NewGuid().ToString("N");
        try
        {
            var first = await new LocalKeyProvider(path).GetKeyAsync(CancellationToken.None);
            var second = await new LocalKeyProvider(path).GetKeyAsync(CancellationToken.None);
            using var key = Registry.CurrentUser.OpenSubKey(path);
            var stored = (byte[])key!.GetValue("TokenKey")!;
            check(first.Length == 32 && first.SequenceEqual(second) && !stored.AsSpan().ToArray().SequenceEqual(first), "Local token key persists across restarts and is stored only DPAPI-encrypted");
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(path, false); }
    }

    private static void AdminLockoutChecks(Action<bool, string> check, string root)
    {
        var clock = DateTimeOffset.Parse("2026-11-26T08:00:00Z");
        Func<DateTimeOffset> now = () => clock;
        var guard = new AdminLockout(now);
        check(guard.Check().Allowed, "Admin sign-in is allowed on a fresh machine");
        guard.RecordFailure();
        var second = guard.RecordFailure();
        check(second.Contains("1 lần") && guard.Check().Allowed, "Two wrong admin passwords leave one more try in the round");
        guard.RecordFailure();
        check(!guard.Check().Allowed, "Third wrong admin password starts a cooldown");
        check(!new AdminLockout(now).Check().Allowed, "Cooldown survives restarting the app");
        clock += AdminLockout.Cooldown + TimeSpan.FromSeconds(1);
        check(new AdminLockout(now).Check().Allowed, "Admin sign-in is allowed again after the cooldown");

        guard = new AdminLockout(now);
        for (var i = 0; i < 6; i++) { guard.RecordFailure(); clock += AdminLockout.Cooldown + TimeSpan.FromSeconds(1); }
        var reopened = new AdminLockout(now);
        check(reopened.Current.Locked && !reopened.Check().Allowed && reopened.Current.TotalFailures == AdminLockout.MaximumAttempts, "Ninth wrong admin password locks this machine permanently");
        reopened.RecordSuccess();
        check(!new AdminLockout(now).Check().Allowed, "A permanent lock is not cleared by a later success");

        // Xóa một trong hai nơi lưu không mở khóa được; sửa dữ liệu thì bị khóa luôn.
        File.Delete(Path.Combine(AdminLockout.StorageFolder, "admin-guard.dat"));
        check(!new AdminLockout(now).Check().Allowed, "Deleting the lockout file does not unlock the machine (registry copy remains)");
        Registry.CurrentUser.DeleteSubKeyTree(AdminLockout.RegistryPath, false);
        check(new AdminLockout(now).Check().Allowed, "Clearing both copies resets the counter (test reset only)");
        guard = new AdminLockout(now);
        guard.RecordFailure();
        var file = Path.Combine(AdminLockout.StorageFolder, "admin-guard.dat");
        var bytes = File.ReadAllBytes(file); bytes[^1] ^= 0x01; File.WriteAllBytes(file, bytes);
        check(new AdminLockout(now).Current.Locked, "A tampered lockout record locks the machine");
        Registry.CurrentUser.DeleteSubKeyTree(AdminLockout.RegistryPath, false);
        File.Delete(file);

        guard = new AdminLockout(now);
        guard.RecordFailure(); guard.RecordFailure(); guard.RecordSuccess();
        check(new AdminLockout(now).Current.TotalFailures == 0, "Successful admin sign-in resets the counter");
        guard.Lock("Cloud chặn địa chỉ");
        check(!new AdminLockout(now).Check().Allowed, "A cloud IP block is mirrored as a local lock");
        Registry.CurrentUser.DeleteSubKeyTree(AdminLockout.RegistryPath, false);
        File.Delete(file);
    }

    private static async Task GrantRecoveryChecks(Action<bool, string> check, string root)
    {
        var folder = Path.Combine(root, "grants");
        var journal = new GrantJournal(folder);
        var withId = journal.Begin("A", "F1", "target@example.com");
        journal.Attach(withId, "perm-1");
        journal.Begin("A", "F2", "target@example.com"); // Tắt ngang trước khi biết mã quyền.
        journal.Begin("A", "GONE", "target@example.com"); // Tệp đã bị xóa.
        journal.Begin("missing-account", "F3", "target@example.com");
        check(!Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(folder, "pending-grants.dat"))).Contains("target@example.com"), "Pending grant journal is encrypted on disk");
        check(new GrantJournal(folder).Pending().Count == 4, "Pending temporary grants survive an app crash");

        var deleted = new List<string>();
        var listed = new List<string>();
        using var http = new HttpClient(new ActionHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            HttpResponseMessage Json(string text, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
            if (request.Method == HttpMethod.Get && path.EndsWith("/files/F2/permissions"))
            {
                listed.Add("F2");
                return Task.FromResult(Json("{\"permissions\":[{\"id\":\"owner\",\"type\":\"user\",\"role\":\"owner\",\"emailAddress\":\"a@example.com\"},{\"id\":\"inherited\",\"type\":\"user\",\"role\":\"reader\",\"emailAddress\":\"TARGET@example.com\",\"permissionDetails\":[{\"inherited\":true}]},{\"id\":\"perm-2\",\"type\":\"user\",\"role\":\"reader\",\"emailAddress\":\"Target@Example.com\"}]}"));
            }
            if (request.Method == HttpMethod.Get && path.EndsWith("/files/GONE/permissions")) return Task.FromResult(Json("{\"error\":{\"message\":\"File not found\"}}", HttpStatusCode.NotFound));
            if (request.Method == HttpMethod.Delete) { deleted.Add(path[(path.LastIndexOf('/') + 1)..]); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)); }
            return Task.FromResult(Json("{\"error\":{\"message\":\"Unexpected\"}}", HttpStatusCode.BadRequest));
        }));
        var source = new Account { Id = "A", Email = "a@example.com" };
        var copier = new CrossAccountCopier(new DriveService(http, (_, _) => Task.FromResult("token-A")), journal);
        var recovered = await copier.RecoverAsync(id => id == "A" ? source : null, CancellationToken.None);
        check(recovered == 2 && deleted.Order().SequenceEqual(["perm-1", "perm-2"]), "Startup revokes leftover temporary grants by permission id or by the target's direct permission");
        var left = journal.Pending();
        check(left.Count == 1 && left[0].SourceAccountId == "missing-account", "Recovered and deleted-file grants leave the journal; grants of a removed account wait");

        // Thu hồi thất bại khi gửi chéo: mục nhật ký phải được giữ lại để thử ở lần mở sau.
        var failFolder = Path.Combine(root, "grants-fail");
        var failJournal = new GrantJournal(failFolder);
        using var failing = new HttpClient(new ActionHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            HttpResponseMessage Json(string text, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
            var token = request.Headers.Authorization?.Parameter;
            if (request.Method == HttpMethod.Get && path.EndsWith("/files/F"))
                return Task.FromResult(token == "token-A" ? Json("{\"id\":\"F\",\"name\":\"a.bin\",\"mimeType\":\"application/octet-stream\",\"size\":\"10\",\"md5Checksum\":\"m\",\"capabilities\":{\"canShare\":true,\"canCopy\":true,\"canDownload\":true}}") : Json("{\"error\":{\"message\":\"nf\"}}", HttpStatusCode.NotFound));
            if (request.Method == HttpMethod.Post && path.EndsWith("/permissions")) return Task.FromResult(Json("{\"id\":\"perm-x\"}"));
            if (request.Method == HttpMethod.Post && path.EndsWith("/copy")) return Task.FromResult(Json("{\"id\":\"C\",\"name\":\"a.bin\",\"size\":\"10\",\"md5Checksum\":\"m\"}"));
            if (request.Method == HttpMethod.Delete) return Task.FromResult(Json("{\"error\":{\"message\":\"backend\"}}", HttpStatusCode.InternalServerError));
            return Task.FromResult(Json("{\"error\":{\"message\":\"Unexpected\"}}", HttpStatusCode.BadRequest));
        }));
        var target = new Account { Id = "B", Email = "target@example.com" };
        var result = await new CrossAccountCopier(new DriveService(failing, (account, _) => Task.FromResult("token-" + account.Id)), failJournal) { Delay = (_, _) => Task.CompletedTask }
            .CopyAsync(source, target, new DriveItem { Id = "F", Name = "a.bin", MimeType = "application/octet-stream" }, new Progress<string>(), _ => Task.FromResult(false), null, CancellationToken.None);
        var kept = failJournal.Pending();
        check(result.RevokeFailed && kept.Count == 1 && kept[0].PermissionId == "perm-x", "A failed revoke keeps the grant in the journal for the next start");
    }

    private static void UpdateTrustChecks(Action<bool, string> check)
    {
        using var owner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string[] trusted = [Convert.ToBase64String(owner.ExportSubjectPublicKeyInfo())];
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new ReleaseManifest { Product = "DriveLink", Version = "1.2.0", Created = "2026-11-26T00:00:00Z", Files = new() { ["DriveLink-1.2.0-full.nupkg"] = "abcdef" } });
        var parsed = UpdateTrust.Verify(manifest, UpdateTrust.Sign(manifest, owner), trusted);
        UpdateTrust.RequireFile(parsed, "1.2.0", "DriveLink-1.2.0-full.nupkg", "ABCDEF");
        check(parsed.Version == "1.2.0", "Update signed by the owner's key is accepted");

        bool Blocked(Action action) { try { action(); return false; } catch (UpdateTrustException) { return true; } }
        check(Blocked(() => UpdateTrust.Verify(manifest, UpdateTrust.Sign(manifest, attacker), trusted)), "Update signed with another key (stolen GitHub account) is blocked");
        var edited = (byte[])manifest.Clone(); edited[^3] ^= 0x01;
        check(Blocked(() => UpdateTrust.Verify(edited, UpdateTrust.Sign(manifest, owner), trusted)), "Edited file list is blocked");
        check(Blocked(() => UpdateTrust.Verify(manifest, "not base64!", trusted)) && Blocked(() => UpdateTrust.Verify([], "", trusted)), "Missing or broken signature is blocked");
        check(Blocked(() => UpdateTrust.RequireFile(parsed, "1.2.0", "DriveLink-1.2.0-full.nupkg", "000000"))
            && Blocked(() => UpdateTrust.RequireFile(parsed, "1.2.0", "Other.nupkg", "abcdef"))
            && Blocked(() => UpdateTrust.RequireFile(parsed, "1.3.0", "DriveLink-1.2.0-full.nupkg", "abcdef")), "Package hash, name or version that differs from the signed list is blocked");
        var other = JsonSerializer.SerializeToUtf8Bytes(new ReleaseManifest { Product = "Other", Version = "1.2.0", Files = new() { ["x"] = "y" } });
        check(Blocked(() => UpdateTrust.Verify(other, UpdateTrust.Sign(other, owner), trusted)), "A signed list for another product is blocked");
        check(UpdateTrust.TrustedPublicKeys.Count >= 1 && UpdateTrust.TrustedPublicKeys.All(k => { using var e = ECDsa.Create(); e.ImportSubjectPublicKeyInfo(Convert.FromBase64String(k), out _); return e.KeySize == 256; }),
            "The app pins a valid P-256 release public key");
    }
}
