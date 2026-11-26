using DriveLink.Services.Lan;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

/// <summary>
/// Đo tốc độ gửi/nhận LAN giữa hai "máy" chạy trên cùng máy này (qua loopback): chạy bằng `--lan-bench`.
/// Loopback nhanh hơn mạng thật nhiều, nên con số phản ánh chi phí của chính DriveLink (đọc/ghi ổ, băm, mã hóa, bắt tay),
/// không phải tốc độ cáp mạng. Tệp nguồn vừa được ghi nên thường nằm sẵn trong bộ nhớ đệm của Windows.
/// </summary>
static class LanBench
{
    private static int FreePort() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return port; }

    public static async Task Run(string root, int runs)
    {
        var dirA = Path.Combine(root, "bench-a"); var dirB = Path.Combine(root, "bench-b");
        var trustA = new LanTrustStore(dirA); var trustB = new LanTrustStore(dirB);
        var a = new LanService(LanIdentity.Load(dirA, "BENCH-A"), trustA, FreePort(), FreePort());
        var b = new LanService(LanIdentity.Load(dirB, "BENCH-B"), trustB, FreePort(), FreePort());
        var inbox = Path.Combine(dirB, "inbox"); Directory.CreateDirectory(inbox);
        b.ReceiveFolder = () => inbox;
        a.Start(discovery: false); b.Start(discovery: false);
        trustA.Add("BENCH-B", b.Identity.PublicKey); trustB.Add("BENCH-A", a.Identity.PublicKey);
        var toB = new IPEndPoint(IPAddress.Loopback, b.TransferPort);
        try
        {
            var source = Path.Combine(dirA, "src"); Directory.CreateDirectory(source);
            var big = Path.Combine(source, "lon.bin"); Fill(big, 1L << 30);
            var small = Enumerable.Range(0, 300).Select(i => { var p = Path.Combine(source, $"nho-{i:000}.bin"); Fill(p, 100 * 1024); return p; }).ToList();
            Console.WriteLine($"CPU: {Environment.ProcessorCount} luồng · .NET {Environment.Version} · {runs} lần mỗi phép đo, lấy trung vị");
            Parts(big, Path.Combine(dirB, "ghi-thu.bin"));
            await Measure("1 tệp 1 GB", 1L << 30, runs, inbox, () => Send(a, toB, [big]));
            await Measure("300 tệp × 100 KB", 300L * 100 * 1024, runs, inbox, () => Send(a, toB, small));
        }
        finally { await a.StopAsync(); await b.StopAsync(); }
    }

    /// <summary>Tốc độ từng khâu riêng lẻ trên máy này, để biết khâu nào là giới hạn.</summary>
    private static void Parts(string source, string scratch)
    {
        var buffer = new byte[1 << 20]; const int blocks = 1024;
        double Time(Action action) { var w = Stopwatch.StartNew(); action(); return blocks / w.Elapsed.TotalSeconds; }
        var read = Time(() => { using var f = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 0, FileOptions.SequentialScan); while (f.Read(buffer) > 0) { } });
        var write = Time(() => { using var f = new FileStream(scratch, FileMode.Create, FileAccess.Write, FileShare.None, 0); for (var i = 0; i < blocks; i++) f.Write(buffer); f.Flush(true); });
        File.Delete(scratch);
        var sha = Time(() => { using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); for (var i = 0; i < blocks; i++) h.AppendData(buffer); h.GetHashAndReset(); });
        var output = new byte[buffer.Length]; var tag = new byte[16]; var nonce = new byte[12];
        var aes = Time(() => { using var g = new AesGcm(new byte[32], 16); for (var i = 0; i < blocks; i++) g.Encrypt(nonce, buffer, output, tag); });
        Console.WriteLine($"PARTS đọc ổ (bộ đệm Windows) {read:0} MB/s · ghi ổ (xả xuống đĩa) {write:0} MB/s · SHA-256 {sha:0} MB/s · AES-GCM {aes:0} MB/s");
    }

    private static Task Send(LanService a, IPEndPoint to, IReadOnlyList<string> files) => a.SendFilesAsync(to, files, null, null, CancellationToken.None);

    private static void Fill(string path, long size)
    {
        var block = new byte[4 << 20]; Random.Shared.NextBytes(block);
        using var f = new FileStream(path, FileMode.Create, FileAccess.Write);
        for (long left = size; left > 0; left -= block.Length) f.Write(block, 0, (int)Math.Min(block.Length, left));
    }

    private static async Task Measure(string label, long bytes, int runs, string inbox, Func<Task> work)
    {
        await work(); Clean(inbox); // Chạy nháp một lần (JIT, bộ nhớ đệm).
        var times = new List<double>(); var allocated = new List<double>(); var gen2 = new List<int>();
        for (var i = 0; i < runs; i++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            var before = GC.GetTotalAllocatedBytes(true); var g2 = GC.CollectionCount(2);
            var watch = Stopwatch.StartNew();
            await work();
            watch.Stop();
            times.Add(watch.Elapsed.TotalSeconds); allocated.Add((GC.GetTotalAllocatedBytes(true) - before) / 1048576d); gen2.Add(GC.CollectionCount(2) - g2);
            Clean(inbox);
        }
        var t = Median(times);
        Console.WriteLine($"BENCH {label}: {t:0.000} s · {bytes / 1048576d / t:0.0} MB/s · cấp phát {Median(allocated):0} MB · GC thế hệ 2: {gen2.OrderBy(x => x).ElementAt(gen2.Count / 2)} lần · các lần: {string.Join(", ", times.Select(x => x.ToString("0.000")))} s");
    }

    private static double Median(List<double> values) { var s = values.OrderBy(x => x).ToList(); return s[s.Count / 2]; }
    private static void Clean(string inbox) { var lan = Path.Combine(inbox, "LAN"); if (Directory.Exists(lan)) Directory.Delete(lan, true); }
}
