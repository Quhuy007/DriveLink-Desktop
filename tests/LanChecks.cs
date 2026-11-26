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
