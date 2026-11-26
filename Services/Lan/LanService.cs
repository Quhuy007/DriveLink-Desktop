using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
namespace DriveLink.Services.Lan;

/// <summary>Tiến độ gửi một lô tệp qua LAN: tệp đang gửi (thứ tự từ 0) và tiến độ chung của cả lô.</summary>
public sealed record LanSendProgress(int Index, int Count, string Name, TransferProgress Overall);

/// <summary>Một máy DriveLink tìm thấy trong mạng LAN.</summary>
public sealed class LanDevice
{
    public string Id { get; init; } = "";
    public string Name { get; set; } = "";
    public IPEndPoint EndPoint { get; set; } = new(IPAddress.Loopback, 0);
    public DateTimeOffset LastSeen { get; set; }
    public bool Trusted { get; set; }
    public string Label => $"{Name} · {EndPoint.Address}" + (Trusted ? " · đã ghép nối" : " · chưa ghép nối");
    public override string ToString() => Label;
}

/// <summary>
/// Truyền thẳng trong mạng LAN, không qua Google:
/// - Phát hiện: mỗi 3 giây gửi gói UDP quảng bá (chỉ chứa mã thiết bị, tên máy, cổng TCP) và lắng nghe gói của máy khác.
/// - Ghép nối: hai người dùng so mã 6 số hiện trên hai máy rồi cùng xác nhận; sau đó khóa công khai của nhau được ghi nhớ.
/// - Gửi tệp: chỉ máy đã ghép nối mới gửi/nhận được; kênh mã hóa AES-GCM, kiểm SHA-256 khi nhận xong, lưu vào Hộp thư đến.
///   Cả lô tệp đi qua một kết nối; đọc ổ, mã hóa và gửi mạng chạy song song theo dây chuyền với vài bộ đệm 1 MB dùng lại.
/// Tắt mặc định; bật trong cửa sổ Thiết bị LAN. Lần đầu bật, Windows có thể hỏi cho phép DriveLink qua tường lửa (chọn mạng riêng).
/// </summary>
public sealed class LanService : IAsyncDisposable
{
    public const int DefaultDiscoveryPort = 47210, DefaultTransferPort = 47211;
    private readonly LanIdentity me;
    private readonly LanTrustStore trust;
    private readonly int discoveryPort, transferPort;
    private readonly Dictionary<string, LanDevice> devices = [];
    private CancellationTokenSource? running;
    private TcpListener? listener;
    private UdpClient? udp;
    public bool IsRunning => running is not null;
    public LanIdentity Identity => me;
    public int TransferPort => transferPort;
    public event Action? DevicesChanged;
    /// <summary>Máy khác xin ghép nối: (tên máy, mã 6 số) → người dùng có đồng ý không.</summary>
    public Func<string, string, Task<bool>> ConfirmPairing { get; set; } = (_, _) => Task.FromResult(false);
    /// <summary>Thư mục lưu tệp nhận qua LAN.</summary>
    public Func<string> ReceiveFolder { get; set; } = () => Path.GetTempPath();
    /// <summary>Đã nhận xong một tệp: (tên máy gửi, đường dẫn tệp).</summary>
    public event Action<string, string>? FileReceived;
    /// <summary>Kết thúc một lần nhận (cả lô, kể cả khi bị ngắt giữa chừng): (tên máy gửi, các tệp đã nhận đủ). Dùng để báo một lần thay vì mỗi tệp một lần.</summary>
    public event Action<string, IReadOnlyList<string>>? BatchReceived;
    /// <summary>Phiên bản giao thức máy này dùng. Chỉ đặt khác <see cref="LanChannel.CurrentVersion"/> khi kiểm thử tương thích với bản cũ.</summary>
    public int ProtocolVersion { get; init; } = LanChannel.CurrentVersion;
    private const int ChunkSize = 1 << 20, PipelineDepth = 4;
    public event Action<string>? Error;

    public LanService(LanIdentity identity, LanTrustStore trust, int discoveryPort = DefaultDiscoveryPort, int transferPort = DefaultTransferPort)
    {
        me = identity; this.trust = trust; this.discoveryPort = discoveryPort; this.transferPort = transferPort;
    }

    public IReadOnlyList<LanDevice> Devices
    {
        get
        {
            lock (devices)
            {
                var stale = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(15);
                foreach (var d in devices.Values) d.Trusted = trust.All.Any(t => t.Id == d.Id);
                return devices.Values.Where(d => d.LastSeen > stale).OrderBy(d => d.Name).ToList();
            }
        }
    }

    public void Start(bool discovery = true)
    {
        if (running is not null) return;
        running = new CancellationTokenSource();
        listener = new TcpListener(IPAddress.Any, transferPort);
        listener.Start();
        _ = AcceptLoopAsync(running.Token);
        if (discovery)
        {
            udp = new UdpClient { EnableBroadcast = true };
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, discoveryPort));
            _ = BeaconLoopAsync(running.Token);
            _ = ListenLoopAsync(running.Token);
        }
    }

    public async Task StopAsync()
    {
        running?.Cancel(); running = null;
        listener?.Stop(); listener = null;
        udp?.Dispose(); udp = null;
        lock (devices) devices.Clear();
        DevicesChanged?.Invoke();
        await Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    // ---------- Phát hiện ----------
    private byte[] Beacon() => JsonSerializer.SerializeToUtf8Bytes(new { app = "DriveLink", v = 1, id = me.Id, name = me.Name.Length > 60 ? me.Name[..60] : me.Name, port = transferPort });

    private async Task BeaconLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var beacon = Beacon();
                foreach (var address in BroadcastAddresses()) await udp!.SendAsync(beacon, new IPEndPoint(address, discoveryPort), ct);
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
            catch (OperationCanceledException) { return; }
            try { await Task.Delay(TimeSpan.FromSeconds(3), ct); } catch (OperationCanceledException) { return; }
        }
    }

    private static IEnumerable<IPAddress> BroadcastAddresses()
    {
        yield return IPAddress.Broadcast;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
            foreach (var unicast in nic.GetIPProperties().UnicastAddresses.Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork && u.IPv4Mask is not null))
            {
                var ip = unicast.Address.GetAddressBytes(); var mask = unicast.IPv4Mask.GetAddressBytes();
                yield return new IPAddress(ip.Select((b, i) => (byte)(b | ~mask[i])).ToArray());
            }
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult packet;
            try { packet = await udp!.ReceiveAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { return; }
            Observe(packet.Buffer, packet.RemoteEndPoint.Address);
        }
    }

    /// <summary>Ghi nhận gói quảng bá của máy khác (bỏ qua gói của chính máy này và gói sai định dạng).</summary>
    public void Observe(byte[] packet, IPAddress from)
    {
        try
        {
            if (packet.Length > 1024) return;
            using var json = JsonDocument.Parse(packet);
            var root = json.RootElement;
            if (root.GetProperty("app").GetString() != "DriveLink") return;
            var id = root.GetProperty("id").GetString() ?? "";
            if (id == me.Id || id.Length != 16) return;
            var port = root.GetProperty("port").GetInt32();
            if (port is < 1024 or > 65535) return;
            lock (devices)
            {
                if (!devices.TryGetValue(id, out var device)) devices[id] = device = new LanDevice { Id = id };
                device.Name = (root.GetProperty("name").GetString() ?? "?") is { Length: > 60 } n ? n[..60] : root.GetProperty("name").GetString() ?? "?";
                device.EndPoint = new IPEndPoint(from, port);
                device.LastSeen = DateTimeOffset.UtcNow;
            }
            DevicesChanged?.Invoke();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { }
    }

    // ---------- Nhận kết nối ----------
    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener!.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { return; }
            _ = HandleAsync(client, ct);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        LanChannel? channel = null;
        try
        {
            channel = await LanChannel.ConnectAsync(client, me, initiator: false, timeout.Token, ProtocolVersion);
            var request = await channel.ReceiveJsonAsync(timeout.Token);
            switch (request.GetProperty("type").GetString())
            {
                case "pair":
                    timeout.CancelAfter(TimeSpan.FromMinutes(2)); // Chờ người dùng xác nhận.
                    var accepted = await ConfirmPairing(channel.PeerName, channel.PairingCode);
                    await channel.SendJsonAsync(new { type = accepted ? "pair-ok" : "pair-no" }, timeout.Token);
                    if (accepted && (await channel.ReceiveJsonAsync(timeout.Token)).GetProperty("type").GetString() == "pair-ok")
                    {
                        trust.Add(channel.PeerName, channel.PeerPublicKey);
                        SecurityLog.Write("lan-paired", $"Ghép nối LAN với {channel.PeerName} ({channel.PeerId})");
                        DevicesChanged?.Invoke();
                    }
                    break;
                case "file": // Phiên bản 1: một tệp, mã băm báo trước.
                case "batch" when ProtocolVersion >= 2: // Phiên bản 2: cả lô, mã băm ở cuối mỗi tệp.
                    if (!trust.IsTrusted(channel.PeerPublicKey)) { await channel.SendJsonAsync(new { type = "reject", reason = "Máy này chưa ghép nối với máy nhận." }, timeout.Token); SecurityLog.Write("lan-rejected", $"Từ chối tệp LAN từ máy chưa ghép nối {channel.PeerName} ({channel.PeerId})"); break; }
                    timeout.CancelAfter(Timeout.InfiniteTimeSpan);
                    await ReceiveAsync(channel, request, ct);
                    break;
            }
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or SocketException or JsonException or KeyNotFoundException or InvalidOperationException or OperationCanceledException or UnauthorizedAccessException or ChannelClosedException)
        {
            if (ex is not OperationCanceledException || !ct.IsCancellationRequested) Error?.Invoke("Kết nối LAN bị gián đoạn: " + ex.Message);
        }
        finally { if (channel is not null) await channel.DisposeAsync(); else client.Dispose(); }
    }

    private async Task ReceiveAsync(LanChannel channel, JsonElement request, CancellationToken ct)
    {
        var received = new List<string>();
        var buffers = NewBuffers();
        try
        {
            if (request.GetProperty("type").GetString() == "file")
            {
                if (!TryReadHeader(request, out var name, out var size)) { await channel.SendJsonAsync(new { type = "reject", reason = "Thông tin tệp không hợp lệ." }, ct); return; }
                var expected = request.GetProperty("sha256").GetString() ?? "";
                await channel.SendJsonAsync(new { type = "accept" }, ct);
                received.Add(await ReceiveItemAsync(channel, name, size, () => Task.FromResult(expected), buffers, ct));
                return;
            }
            await channel.SendJsonAsync(new { type = "accept" }, ct);
            while (true)
            {
                var header = await channel.ReceiveJsonAsync(ct);
                var type = header.GetProperty("type").GetString();
                if (type == "done") break;
                if (type != "item" || !TryReadHeader(header, out var name, out var size)) { await channel.SendJsonAsync(new { type = "bad" }, ct); throw new IOException("Thông tin tệp không hợp lệ."); }
                received.Add(await ReceiveItemAsync(channel, name, size, async () => (await channel.ReceiveJsonAsync(ct)) is var end && end.GetProperty("type").GetString() == "end" ? end.GetProperty("sha256").GetString() ?? "" : "", buffers, ct));
            }
        }
        finally
        {
            if (received.Count > 0)
            {
                SecurityLog.Write("lan-received", $"Nhận {received.Count} tệp LAN ({Extensions(received)}) từ {channel.PeerName} ({channel.PeerId})");
                BatchReceived?.Invoke(channel.PeerName, received);
            }
        }
    }

    /// <summary>Tóm tắt đuôi tệp cho nhật ký bảo mật (không ghi tên tệp).</summary>
    private static string Extensions(IEnumerable<string> paths) =>
        string.Join(", ", paths.GroupBy(p => Path.GetExtension(p).ToLowerInvariant() is { Length: > 0 } e ? e : "(không đuôi)").OrderByDescending(g => g.Count()).Take(5).Select(g => $"{g.Key} ×{g.Count()}"));

    private static bool TryReadHeader(JsonElement header, out string name, out long size)
    {
        name = string.Concat((header.GetProperty("name").GetString() ?? "tep").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');
        size = header.GetProperty("size").GetInt64();
        return name.Length > 0 && size >= 0;
    }

    /// <summary>Nhận một tệp vào tệp tạm (vừa nhận vừa băm), so mã băm rồi mới đổi tên thành tệp thật và báo "ok".</summary>
    private async Task<string> ReceiveItemAsync(LanChannel channel, string name, long size, Func<Task<string>> expectedHash, Channel<byte[]> buffers, CancellationToken ct)
    {
        var folder = Path.Combine(ReceiveFolder(), "LAN"); Directory.CreateDirectory(folder);
        var target = InboxWatcher.UniquePath(folder, name);
        var temp = target + ".lan-partial";
        try
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await ReceiveDataAsync(channel, temp, size, sha, buffers, ct);
            var actual = Convert.ToHexString(sha.GetHashAndReset());
            if (!string.Equals(actual, await expectedHash(), StringComparison.OrdinalIgnoreCase)) { await channel.SendJsonAsync(new { type = "bad" }, ct); throw new IOException("Tệp nhận qua LAN không khớp SHA-256."); }
            File.Move(temp, target);
            await channel.SendJsonAsync(new { type = "ok" }, ct);
            FileReceived?.Invoke(channel.PeerName, target);
            return target;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    /// <summary>
    /// Dây chuyền nhận: luồng mạng nhận và giải mã vào bộ đệm trống, luồng ổ đĩa băm và ghi bộ đệm đầy rồi trả lại —
    /// mạng không phải đợi ổ đĩa ghi xong mới nhận tiếp. Tệp được đặt trước kích thước để ổ ít phân mảnh.
    /// </summary>
    private static async Task ReceiveDataAsync(LanChannel channel, string temp, long size, IncrementalHash sha, Channel<byte[]> buffers, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var full = Channel.CreateBounded<(byte[] Buffer, int Length)>(new BoundedChannelOptions(PipelineDepth) { SingleReader = true, SingleWriter = true });
        var disk = Task.Run(async () =>
        {
            try
            {
                await using var output = new FileStream(temp, new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous | FileOptions.SequentialScan, BufferSize = 0, PreallocationSize = size });
                await foreach (var (buffer, length) in full.Reader.ReadAllAsync(stop.Token))
                {
                    sha.AppendData(buffer, 0, length);
                    await output.WriteAsync(buffer.AsMemory(0, length), stop.Token);
                    buffers.Writer.TryWrite(buffer);
                }
            }
            catch { stop.Cancel(); throw; }
        });
        try
        {
            long total = 0;
            while (total < size)
            {
                var buffer = await buffers.Reader.ReadAsync(stop.Token);
                var length = await channel.ReceiveAsync(buffer, stop.Token);
                if (length == 0 || total + length > size) throw new IOException("Dữ liệu nhận được không khớp kích thước đã báo.");
                total += length;
                await full.Writer.WriteAsync((buffer, length), stop.Token);
            }
            full.Writer.Complete();
        }
        catch
        {
            stop.Cancel(); full.Writer.TryComplete();
            await RethrowIfFailed(disk);
            throw;
        }
        await disk;
    }

    // ---------- Chủ động: ghép nối và gửi ----------
    /// <summary>Xin ghép nối với máy <paramref name="endPoint"/>; <paramref name="confirm"/> hiện mã 6 số để người dùng so với máy kia.</summary>
    public async Task<bool> PairAsync(IPEndPoint endPoint, Func<string, string, Task<bool>> confirm, CancellationToken ct)
    {
        await using var channel = await OpenAsync(endPoint, ct);
        await channel.SendJsonAsync(new { type = "pair" }, ct);
        var mine = confirm(channel.PeerName, channel.PairingCode);
        var theirs = await channel.ReceiveJsonAsync(ct);
        var ok = await mine && theirs.GetProperty("type").GetString() == "pair-ok";
        await channel.SendJsonAsync(new { type = ok ? "pair-ok" : "pair-no" }, ct);
        if (!ok) return false;
        trust.Add(channel.PeerName, channel.PeerPublicKey);
        SecurityLog.Write("lan-paired", $"Ghép nối LAN với {channel.PeerName} ({channel.PeerId})");
        DevicesChanged?.Invoke();
        return true;
    }

    /// <summary>Gửi một tệp tới máy đã ghép nối. Báo lỗi nếu máy kia không phải thiết bị đã ghép (khóa khác).</summary>
    public Task SendFileAsync(IPEndPoint endPoint, string path, IProgress<TransferProgress>? progress, CancellationToken ct) =>
        SendFilesAsync(endPoint, [path], progress is null ? null : new InlineProgress<LanSendProgress>(p => progress.Report(p.Overall)), null, ct);

    /// <summary>
    /// Gửi nhiều tệp tới máy đã ghép nối qua một kết nối (máy nhận bản 1.3.0 thì mỗi tệp một kết nối như cũ).
    /// <paramref name="delivered"/> được gọi (trên luồng nền) khi máy nhận xác nhận đã nhận đủ một tệp: (đường dẫn, kích thước, thời gian).
    /// </summary>
    public async Task SendFilesAsync(IPEndPoint endPoint, IReadOnlyList<string> paths, IProgress<LanSendProgress>? progress, Action<string, long, TimeSpan>? delivered, CancellationToken ct)
    {
        if (paths.Count == 0) return;
        var files = paths.Select(p => new FileInfo(p)).ToList();
        foreach (var file in files) if (!file.Exists) throw new FileNotFoundException("Không tìm thấy tệp cần gửi.", file.FullName);
        var current = 0;
        var meter = new TransferMeter(progress is null ? null : new InlineProgress<TransferProgress>(p => progress.Report(new(Math.Min(current, files.Count - 1), files.Count, files[Math.Min(current, files.Count - 1)].Name, p))), files.Sum(f => f.Length));
        long sentBefore = 0;
        var buffers = NewBuffers();
        var deliveredPaths = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var channel = await OpenTrustedAsync(endPoint, ct);
        var peer = $"{channel.PeerName} ({channel.PeerId})";
        try
        {
            if (Math.Min(channel.PeerVersion, ProtocolVersion) < 2)
            {
                for (current = 0; current < files.Count; current++)
                {
                    if (current > 0) { await channel.DisposeAsync(); channel = await OpenTrustedAsync(endPoint, ct); }
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    await SendLegacyAsync(channel, files[current], buffers, n => meter.Report(sentBefore + n), ct);
                    sentBefore += files[current].Length;
                    deliveredPaths.Enqueue(files[current].FullName);
                    delivered?.Invoke(files[current].FullName, files[current].Length, watch.Elapsed);
                }
                meter.Report(sentBefore, true);
                return;
            }
            await SendBatchAsync(channel, files, buffers, index => current = index, n => meter.Report(n), deliveredPaths, delivered, ct);
            meter.Report(files.Sum(f => f.Length), true);
        }
        finally
        {
            await channel.DisposeAsync();
            if (!deliveredPaths.IsEmpty) SecurityLog.Write("lan-sent", $"Gửi {deliveredPaths.Count} tệp LAN ({Extensions(deliveredPaths)}) tới {peer}");
        }
    }

    private async Task<LanChannel> OpenTrustedAsync(IPEndPoint endPoint, CancellationToken ct)
    {
        var channel = await OpenAsync(endPoint, ct);
        if (trust.IsTrusted(channel.PeerPublicKey)) return channel;
        await channel.DisposeAsync();
        throw new InvalidOperationException("Máy ở địa chỉ này chưa được ghép nối (hoặc khóa đã đổi). Hãy ghép nối lại trước khi gửi.");
    }

    private static async Task ExpectAcceptAsync(LanChannel channel, CancellationToken ct)
    {
        var answer = await channel.ReceiveJsonAsync(ct);
        if (answer.GetProperty("type").GetString() != "accept") throw new InvalidOperationException(answer.TryGetProperty("reason", out var reason) ? reason.GetString() : "Máy nhận từ chối tệp.");
    }

    /// <summary>Giao thức 1 (máy nhận là DriveLink 1.3.0): băm trước, báo mã băm, gửi, chờ "ok" — mỗi tệp một kết nối.</summary>
    private static async Task SendLegacyAsync(LanChannel channel, FileInfo info, Channel<byte[]> buffers, Action<long> sent, CancellationToken ct)
    {
        string hash;
        await using (var read = new FileStream(info.FullName, new FileStreamOptions { Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.Read, Options = FileOptions.Asynchronous | FileOptions.SequentialScan, BufferSize = 1 << 20 }))
            hash = Convert.ToHexString(await SHA256.HashDataAsync(read, ct));
        await channel.SendJsonAsync(new { type = "file", name = info.Name, size = info.Length, sha256 = hash }, ct);
        await ExpectAcceptAsync(channel, ct);
        await SendDataAsync(channel, info.FullName, info.Length, null, buffers, sent, ct);
        if ((await channel.ReceiveJsonAsync(ct)).GetProperty("type").GetString() != "ok") throw new IOException("Máy nhận báo tệp bị hỏng khi truyền. Hãy gửi lại.");
    }

    /// <summary>
    /// Giao thức 2: "batch" → chờ "accept" một lần; mỗi tệp: "item" (tên, cỡ) → dữ liệu → "end" (SHA-256 tính trong lúc gửi); cuối cùng "done".
    /// Máy nhận trả "ok" cho từng tệp; một luồng riêng đọc các "ok" đó nên máy gửi không phải dừng chờ giữa các tệp.
    /// </summary>
    private static async Task SendBatchAsync(LanChannel channel, List<FileInfo> files, Channel<byte[]> buffers, Action<int> starting, Action<long> sent, System.Collections.Concurrent.ConcurrentQueue<string> deliveredPaths, Action<string, long, TimeSpan>? delivered, CancellationToken ct)
    {
        await channel.SendJsonAsync(new { type = "batch", count = files.Count, total = files.Sum(f => f.Length) }, ct);
        await ExpectAcceptAsync(channel, ct);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var started = new System.Diagnostics.Stopwatch[files.Count];
        var acks = Task.Run(async () =>
        {
            for (var i = 0; i < files.Count; i++)
            {
                var answer = await channel.ReceiveJsonAsync(stop.Token);
                if (answer.GetProperty("type").GetString() != "ok") throw new IOException($"Máy nhận báo tệp “{files[i].Name}” bị hỏng khi truyền. Hãy gửi lại.");
                deliveredPaths.Enqueue(files[i].FullName);
                delivered?.Invoke(files[i].FullName, files[i].Length, started[i]?.Elapsed ?? TimeSpan.Zero);
            }
        });
        try
        {
            long before = 0;
            for (var i = 0; i < files.Count; i++)
            {
                if (acks.IsFaulted) break;
                starting(i); started[i] = System.Diagnostics.Stopwatch.StartNew();
                await channel.SendJsonAsync(new { type = "item", name = files[i].Name, size = files[i].Length }, stop.Token);
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var offset = before;
                await SendDataAsync(channel, files[i].FullName, files[i].Length, sha, buffers, n => sent(offset + n), stop.Token);
                await channel.SendJsonAsync(new { type = "end", sha256 = Convert.ToHexString(sha.GetHashAndReset()) }, stop.Token);
                before += files[i].Length;
            }
            if (!acks.IsFaulted) await channel.SendJsonAsync(new { type = "done" }, stop.Token);
        }
        catch
        {
            // Máy nhận báo hỏng rồi đóng kết nối thì lỗi ghi mạng chỉ là hệ quả: ưu tiên báo lý do từ máy nhận.
            await Task.WhenAny(acks, Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None));
            stop.Cancel();
            await RethrowIfFailed(acks);
            throw;
        }
        await acks;
    }

    /// <summary>
    /// Dây chuyền gửi: luồng ổ đĩa đọc (và băm) vào bộ đệm trống, luồng mạng mã hóa và gửi bộ đệm đầy rồi trả lại —
    /// ổ đĩa đọc khối kế tiếp trong lúc mạng đang gửi khối trước.
    /// </summary>
    private static async Task SendDataAsync(LanChannel channel, string path, long size, IncrementalHash? sha, Channel<byte[]> buffers, Action<long> sent, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var full = Channel.CreateBounded<(byte[] Buffer, int Length)>(new BoundedChannelOptions(PipelineDepth) { SingleReader = true, SingleWriter = true });
        var disk = Task.Run(async () =>
        {
            try
            {
                await using var input = new FileStream(path, new FileStreamOptions { Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.Read, Options = FileOptions.Asynchronous | FileOptions.SequentialScan, BufferSize = 0 });
                for (var left = size; left > 0;)
                {
                    var buffer = await buffers.Reader.ReadAsync(stop.Token);
                    var want = (int)Math.Min(buffer.Length, left);
                    var read = await input.ReadAtLeastAsync(buffer.AsMemory(0, want), want, throwOnEndOfStream: false, stop.Token);
                    if (read < want) throw new IOException("Tệp đã bị thay đổi (ngắn đi) trong lúc gửi. Hãy gửi lại.");
                    sha?.AppendData(buffer, 0, read);
                    left -= read;
                    await full.Writer.WriteAsync((buffer, read), stop.Token);
                }
                full.Writer.Complete();
            }
            catch (Exception ex) { full.Writer.TryComplete(ex); throw; }
        });
        try
        {
            long total = 0;
            await foreach (var (buffer, length) in full.Reader.ReadAllAsync(stop.Token))
            {
                await channel.SendAsync(buffer.AsMemory(0, length), stop.Token);
                buffers.Writer.TryWrite(buffer);
                total += length; sent(total);
            }
        }
        catch
        {
            stop.Cancel();
            await RethrowIfFailed(disk);
            throw;
        }
        await disk;
    }

    /// <summary>Vài bộ đệm 1 MB dùng chung cho cả lượt truyền (không cấp phát lại mỗi khối).</summary>
    private static Channel<byte[]> NewBuffers()
    {
        var free = Channel.CreateBounded<byte[]>(PipelineDepth + 1);
        for (var i = 0; i < PipelineDepth + 1; i++) free.Writer.TryWrite(new byte[ChunkSize]);
        return free;
    }

    /// <summary>Nếu luồng phụ của dây chuyền hỏng vì lỗi thật (không phải do bị dừng), ném lỗi đó thay cho lỗi hệ quả.</summary>
    private static async Task RethrowIfFailed(Task task)
    {
        try { await task; }
        catch (Exception ex) when (ex is not OperationCanceledException and not ChannelClosedException) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(ex); }
        catch { }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }

    private async Task<LanChannel> OpenAsync(IPEndPoint endPoint, CancellationToken ct)
    {
        var client = new TcpClient();
        try
        {
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connect.CancelAfter(TimeSpan.FromSeconds(8));
            await client.ConnectAsync(endPoint, connect.Token);
            return await LanChannel.ConnectAsync(client, me, initiator: true, ct, ProtocolVersion);
        }
        catch { client.Dispose(); throw; }
    }
}
