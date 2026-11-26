using DriveLink;
using DriveLink.Services;
using DriveLink.Services.Lan;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

/// <summary>Truyền thẳng trong LAN giữa hai "máy" trên cùng máy kiểm thử (khóa riêng, danh sách ghép nối riêng).</summary>
static class LanChecks
{
    private sealed class Inline<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
    private static string Write(string folder, string name, int size) { Directory.CreateDirectory(folder); var path = Path.Combine(folder, name); var data = new byte[size]; Random.Shared.NextBytes(data); File.WriteAllBytes(path, data); return path; }
    private static bool SameFiles(IEnumerable<string> sent, IEnumerable<string> got) =>
        sent.Count() == got.Count() && sent.All(s => got.Any(g => Path.GetFileName(g) == Path.GetFileName(s) && File.ReadAllBytes(g).SequenceEqual(File.ReadAllBytes(s))));

    private static int FreePort() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return port; }

    public static async Task Run(Action<bool, string> check, string root)
    {
        var dirA = Path.Combine(root, "lan-a"); var dirB = Path.Combine(root, "lan-b");
        var a = new LanService(LanIdentity.Load(dirA, "MAY-A"), new LanTrustStore(dirA), FreePort(), FreePort());
        var bTrust = new LanTrustStore(dirB);
        var b = new LanService(LanIdentity.Load(dirB, "MAY-B"), bTrust, FreePort(), FreePort());
        var inbox = Path.Combine(dirB, "inbox"); Directory.CreateDirectory(inbox);
        b.ReceiveFolder = () => inbox;
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        b.FileReceived += (_, path) => received.TrySetResult(path);
        a.Start(discovery: false); b.Start(discovery: false);
        var toB = new IPEndPoint(IPAddress.Loopback, b.TransferPort);
        try
        {
            check(LanIdentity.Load(dirA).Id == a.Identity.Id && a.Identity.Id != b.Identity.Id && !File.ReadAllText(Path.Combine(dirA, "lan-identity.dat"), Encoding.Latin1).Contains("PRIVATE"),
                "Each computer keeps a stable LAN identity key, stored encrypted");
            var file = Path.Combine(dirA, "anh-chup.png"); var data = new byte[3 * 1024 * 1024 + 77]; Random.Shared.NextBytes(data); File.WriteAllBytes(file, data);
            var refused = false;
            try { await a.SendFileAsync(toB, file, null, CancellationToken.None); } catch (InvalidOperationException) { refused = true; }
            check(refused, "Files cannot be sent to a computer that is not paired");

            // Từ chối ghép nối: không ai tin ai.
            b.ConfirmPairing = (_, _) => Task.FromResult(false);
            var declined = await a.PairAsync(toB, (_, _) => Task.FromResult(true), CancellationToken.None);
            check(!declined && bTrust.All.Count == 0, "Declining the pairing code on either computer pairs nothing");

            string? codeA = null, codeB = null, nameSeenByB = null;
            b.ConfirmPairing = (name, code) => { codeB = code; nameSeenByB = name; return Task.FromResult(true); };
            var paired = await a.PairAsync(toB, (_, code) => { codeA = code; return Task.FromResult(true); }, CancellationToken.None);
            await Task.Delay(200);
            check(paired && codeA is { Length: 6 } && codeA == codeB && nameSeenByB == "MAY-A" && bTrust.IsTrusted(a.Identity.PublicKey), "Pairing shows the same 6-digit code on both computers and remembers each other's key");

            await a.SendFileAsync(toB, file, null, CancellationToken.None);
            var path = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
            check(File.ReadAllBytes(path).SequenceEqual(data) && path.StartsWith(Path.Combine(inbox, "LAN")) && Path.GetFileName(path) == "anh-chup.png",
                "A paired computer sends a file directly over the LAN, verified by SHA-256, into the Inbox");

            // Cả lô qua một kết nối: tệp rỗng, tệp nhiều khối 1 MB, tệp nhỏ; báo nhận một lần cho cả lô.
            var batch = new[] { Write(Path.Combine(dirA, "lo"), "rong.txt", 0), Write(Path.Combine(dirA, "lo"), "video.mp4", 2_500_000), Write(Path.Combine(dirA, "lo"), "ghi-chu.txt", 1234) };
            var batchDone = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
            b.BatchReceived += (_, paths) => batchDone.TrySetResult(paths);
            var delivered = 0; var seenIndexes = new System.Collections.Concurrent.ConcurrentBag<int>(); var lastPercent = 0d;
            await a.SendFilesAsync(toB, batch, new Inline<LanSendProgress>(p => { seenIndexes.Add(p.Index); lastPercent = p.Overall.Percent; }), (_, _, _) => Interlocked.Increment(ref delivered), CancellationToken.None);
            var gotBatch = await batchDone.Task.WaitAsync(TimeSpan.FromSeconds(10));
            check(SameFiles(batch, gotBatch) && delivered == 3 && seenIndexes.Contains(2) && lastPercent == 100,
                "Several files go to a paired computer over one connection, each verified, with one notice for the whole batch");
            var log = SecurityLog.ReadRecent(20);
            check(log.Count(l => l.Contains("\"lan-sent\"") && l.Contains(" 3 t")) == 1 && log.Count(l => l.Contains("\"lan-received\"") && l.Contains(" 3 t")) == 1 && !log.Any(l => l.Contains("video") || l.Contains("ghi-chu")),
                "The security log records one line per LAN batch, without file names");

            // Tương thích với DriveLink 1.3.0 (giao thức 1) theo cả hai chiều.
            var dirC = Path.Combine(root, "lan-c"); var cTrust = new LanTrustStore(dirC); var inboxC = Path.Combine(dirC, "inbox"); Directory.CreateDirectory(inboxC);
            var c = new LanService(LanIdentity.Load(dirC, "MAY-CU"), cTrust, FreePort(), FreePort()) { ProtocolVersion = 1, ReceiveFolder = () => inboxC };
            c.Start(discovery: false);
            try
            {
                cTrust.Add("MAY-A", a.Identity.PublicKey); new LanTrustStore(dirA).Add("MAY-CU", c.Identity.PublicKey);
                cTrust.Add("MAY-B", b.Identity.PublicKey); bTrust.Add("MAY-CU", c.Identity.PublicKey);
                var toOld = new[] { Write(Path.Combine(dirA, "cu"), "bao-cao.docx", 1_200_000), Write(Path.Combine(dirA, "cu"), "anh.jpg", 4096) };
                var oldGot = new System.Collections.Concurrent.ConcurrentQueue<string>();
                c.FileReceived += (_, path) => oldGot.Enqueue(path);
                await a.SendFilesAsync(new IPEndPoint(IPAddress.Loopback, c.TransferPort), toOld, null, null, CancellationToken.None);
                await Task.Delay(200);
                check(SameFiles(toOld, oldGot), "A new computer still sends files to a computer running DriveLink 1.3.0");

                var fromOld = new[] { Write(Path.Combine(dirC, "gui"), "so-lieu.xlsx", 2_100_000), Write(Path.Combine(dirC, "gui"), "readme.txt", 10) };
                var newGot = new System.Collections.Concurrent.ConcurrentQueue<string>();
                void Collect(string _, string path) => newGot.Enqueue(path);
                b.FileReceived += Collect;
                await c.SendFilesAsync(toB, fromOld, null, null, CancellationToken.None);
                await Task.Delay(200);
                b.FileReceived -= Collect;
                check(SameFiles(fromOld, newGot), "A computer running DriveLink 1.3.0 can still send files to a new computer");
            }
            finally { await c.StopAsync(); }

            bTrust.Remove(a.Identity.Id);
            var rejected = false;
            try { await a.SendFileAsync(toB, file, null, CancellationToken.None); } catch (InvalidOperationException) { rejected = true; }
            check(rejected, "After unpairing, the receiver refuses files from that computer");

            var beacon = Encoding.UTF8.GetBytes($"{{\"app\":\"DriveLink\",\"v\":1,\"id\":\"{a.Identity.Id}\",\"name\":\"MAY-A\",\"port\":{a.TransferPort}}}");
            b.Observe(beacon, IPAddress.Parse("192.168.1.20"));
            b.Observe(Encoding.UTF8.GetBytes($"{{\"app\":\"DriveLink\",\"v\":1,\"id\":\"{b.Identity.Id}\",\"name\":\"me\",\"port\":5000}}"), IPAddress.Loopback);
            b.Observe(Encoding.UTF8.GetBytes("{\"app\":\"Other\"}"), IPAddress.Loopback);
            b.Observe(new byte[2048], IPAddress.Loopback);
            var seen = b.Devices;
            check(seen.Count == 1 && seen[0].Name == "MAY-A" && seen[0].EndPoint.Address.ToString() == "192.168.1.20" && !seen[0].Trusted, "Discovery lists other DriveLink computers and ignores itself and malformed packets");
        }
        finally { await a.StopAsync(); await b.StopAsync(); }
    }
}
