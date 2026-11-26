using DriveLink;
using DriveLink.Services;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

/// <summary>Mã hóa đầu cuối: đúng khóa mới mở được, phát hiện sửa/cắt/đảo khối, và Google chỉ nhận dữ liệu đã mã hóa.</summary>
static class E2eChecks
{
    private static async Task<byte[]> Encrypt(byte[] plain, byte[] key) { using var input = new MemoryStream(plain); using var output = new MemoryStream(); await E2eCrypto.EncryptAsync(input, output, key, CancellationToken.None); return output.ToArray(); }
    private static async Task<byte[]> Decrypt(byte[] cipher, byte[] key) { using var input = new MemoryStream(cipher); using var output = new MemoryStream(); await E2eCrypto.DecryptAsync(input, output, key, CancellationToken.None); return output.ToArray(); }
    private static async Task<bool> Rejected(byte[] cipher, byte[] key) { try { await Decrypt(cipher, key); return false; } catch (CryptographicException) { return true; } }

    public static async Task Run(Action<bool, string> check, string root)
    {
        var key = E2eCrypto.DeriveSpaceKey("mật khẩu không gian chung", "SPACE-1");
        var other = E2eCrypto.DeriveSpaceKey("mật khẩu không gian chung", "SPACE-2");
        var wrong = E2eCrypto.DeriveSpaceKey("mat khau khac 123", "SPACE-1");
        var data = new byte[E2eCrypto.ChunkSize * 2 + 12345]; Random.Shared.NextBytes(data);
        var cipher = await Encrypt(data, key);
        check((await Decrypt(cipher, key)).SequenceEqual(data) && (await Decrypt(await Encrypt([], key), key)).Length == 0, "End-to-end encryption round-trips multi-chunk and empty files");
        check(cipher.AsSpan().IndexOf(data.AsSpan(0, 64)) < 0 && !(await Encrypt(data, key)).SequenceEqual(cipher), "Ciphertext hides content and differs every time");
        check(await Rejected(cipher, wrong) && await Rejected(cipher, other), "A wrong space password or another space's key cannot decrypt");
        var tampered = (byte[])cipher.Clone(); tampered[100_000] ^= 1;
        check(await Rejected(tampered, key), "Any modified byte is detected");
        var lastRecord = cipher.Length - (12345 + 16 + 5);
        check(await Rejected(cipher[..lastRecord], key), "Dropping the final chunk is detected");
        var firstLen = 27 + 5 + E2eCrypto.ChunkSize + 16;
        var swapped = cipher[..27].Concat(cipher[firstLen..(2 * firstLen - 27)]).Concat(cipher[27..firstLen]).Concat(cipher[(2 * firstLen - 27)..]).ToArray();
        check(swapped.Length == cipher.Length && await Rejected(swapped, key), "Reordered chunks are detected");
        check(E2eCrypto.Fingerprint(key) == E2eCrypto.Fingerprint(E2eCrypto.DeriveSpaceKey("mật khẩu không gian chung", "SPACE-1")) && E2eCrypto.Fingerprint(key) != E2eCrypto.Fingerprint(wrong),
            "Members with the same password see the same check code");

        var folder = Path.Combine(root, "e2e"); Directory.CreateDirectory(folder);
        var keys = new E2eKeys(folder);
        keys.Set("SPACE-1", "Làm việc", "mật khẩu không gian chung");
        var stored = File.ReadAllBytes(Path.Combine(folder, "e2e-keys.dat"));
        check(keys.KeyFor("SPACE-1")!.SequenceEqual(key) && keys.KeyFor("SPACE-2") is null && stored.AsSpan().IndexOf(Encoding.UTF8.GetBytes("mật khẩu")) < 0 && stored.AsSpan().IndexOf(key) < 0,
            "Space keys are stored DPAPI-encrypted and the password itself is never stored");

        // Gửi rồi nhận qua TransferCenter: Google chỉ nhận bản mã, máy nhận giải mã đúng tên và nội dung gốc.
        var source = Path.Combine(folder, "hop-dong.pdf"); var plain = Encoding.UTF8.GetBytes("Nội dung bí mật của hợp đồng"); File.WriteAllBytes(source, plain);
        byte[]? uploaded = null; string? startBody = null;
        using var http = new HttpClient(new ActionHandler(async request =>
        {
            if (request.Method == HttpMethod.Post) { startBody = await request.Content!.ReadAsStringAsync(); var r = new HttpResponseMessage(HttpStatusCode.OK); r.Headers.Location = new Uri("https://www.googleapis.com/upload/e2e"); return r; }
            if (request.Method == HttpMethod.Put) { uploaded = await request.Content!.ReadAsByteArrayAsync(); return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"id\":\"E1\"}") }; }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(uploaded!) };
        }));
        var center = new TransferCenter(new DriveService(http, (_, _) => Task.FromResult("t")) { Delay = (_, _) => Task.CompletedTask }, new FolderCompressor(), () => new UserSettings(), folder) { Keys = keys };
        var finished = new TaskCompletionSource<TransferItem>(TaskCreationOptions.RunContinuationsAsynchronously);
        center.Completed += item => finished.TrySetResult(item);
        var account = new Account { Id = "a", Email = "a@example.com" };
        center.EnqueueUpload(account, new Space { Id = "SPACE-1", Name = "[DriveLink] Làm việc" }, source);
        var sent = await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        check(sent.State == TransferState.Done && uploaded is not null && uploaded.AsSpan().IndexOf(plain) < 0 && startBody!.Contains("hop-dong.pdf.dlenc") && startBody.Contains("\"dlE2e\":\"1\""),
            "Upload to an encrypted space sends only ciphertext, named .dlenc and tagged for decryption");
        check(!Directory.Exists(Path.Combine(folder, "Encrypting")) || Directory.GetFileSystemEntries(Path.Combine(folder, "Encrypting")).Length == 0, "Temporary encrypted copies are removed after upload");

        var remote = new DriveItem { Id = "E1", Name = "hop-dong.pdf.dlenc", MimeType = "application/octet-stream", Size = uploaded!.Length, AppProperties = new() { ["dlE2e"] = "1", ["dlE2eSpace"] = "SPACE-1" } };
        check(remote.IsEncrypted && remote.PlainName == "hop-dong.pdf" && remote.NameLabel == "🔐 hop-dong.pdf" && remote.DetailLabel.StartsWith("PDF · mã hóa đầu cuối"), "Encrypted files show their original name with a lock");
        finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var target = Path.Combine(folder, "nhan", "hop-dong.pdf"); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        center.EnqueueDownload(account, remote, target, "Làm việc");
        var received = await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        check(received.State == TransferState.Done && File.ReadAllBytes(target).SequenceEqual(plain) && !File.Exists(target + E2eCrypto.Extension), "Receiving decrypts to the original file and removes the encrypted copy");

        var noKeys = new TransferCenter(new DriveService(http, (_, _) => Task.FromResult("t")), new FolderCompressor(), () => new UserSettings(), Path.Combine(folder, "nokey")) { Keys = new E2eKeys(Path.Combine(folder, "nokey")) };
        finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        noKeys.Completed += item => finished.TrySetResult(item);
        noKeys.EnqueueDownload(account, remote, Path.Combine(folder, "nhan", "khong-khoa.pdf"), "Làm việc");
        var refused = await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        check(refused.State == TransferState.Failed && refused.Note.Contains("mật khẩu") && !File.Exists(Path.Combine(folder, "nhan", "khong-khoa.pdf")), "Without the space password an encrypted file is not saved");
    }
}
