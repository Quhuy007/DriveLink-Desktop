using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace DriveLink.Services.Lan;

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
            channel = await LanChannel.ConnectAsync(client, me, initiator: false, timeout.Token);
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
                case "file":
                    if (!trust.IsTrusted(channel.PeerPublicKey)) { await channel.SendJsonAsync(new { type = "reject", reason = "Máy này chưa ghép nối với máy nhận." }, timeout.Token); SecurityLog.Write("lan-rejected", $"Từ chối tệp LAN từ máy chưa ghép nối {channel.PeerName} ({channel.PeerId})"); break; }
                    timeout.CancelAfter(Timeout.InfiniteTimeSpan);
                    await ReceiveFileAsync(channel, request, ct);
                    break;
            }
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or SocketException or JsonException or KeyNotFoundException or InvalidOperationException or OperationCanceledException)
        {
            if (ex is not OperationCanceledException || !ct.IsCancellationRequested) Error?.Invoke("Kết nối LAN bị gián đoạn: " + ex.Message);
        }
        finally { if (channel is not null) await channel.DisposeAsync(); else client.Dispose(); }
    }

    private async Task ReceiveFileAsync(LanChannel channel, JsonElement offer, CancellationToken ct)
    {
        var name = string.Concat((offer.GetProperty("name").GetString() ?? "tep").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');
        var size = offer.GetProperty("size").GetInt64();
        var expected = offer.GetProperty("sha256").GetString() ?? "";
        if (name.Length == 0 || size < 0) { await channel.SendJsonAsync(new { type = "reject", reason = "Thông tin tệp không hợp lệ." }, ct); return; }
        var folder = Path.Combine(ReceiveFolder(), "LAN"); Directory.CreateDirectory(folder);
        var target = InboxWatcher.UniquePath(folder, name);
        var temp = target + ".lan-partial";
        await channel.SendJsonAsync(new { type = "accept" }, ct);
        try
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true))
            {
                long total = 0;
                while (total < size)
                {
                    var chunk = await channel.ReceiveAsync(ct);
                    if (chunk.Length == 0 || total + chunk.Length > size) throw new IOException("Dữ liệu nhận được không khớp kích thước đã báo.");
                    await output.WriteAsync(chunk, ct); sha.AppendData(chunk); total += chunk.Length;
                }
            }
            var actual = Convert.ToHexString(sha.GetHashAndReset());
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) { await channel.SendJsonAsync(new { type = "bad" }, ct); throw new IOException("Tệp nhận qua LAN không khớp SHA-256."); }
            File.Move(temp, target);
            await channel.SendJsonAsync(new { type = "ok" }, ct);
            SecurityLog.Write("lan-received", $"Nhận tệp LAN {Path.GetExtension(target)} từ {channel.PeerName} ({channel.PeerId})");
            FileReceived?.Invoke(channel.PeerName, target);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
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
    public async Task SendFileAsync(IPEndPoint endPoint, string path, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        await using var channel = await OpenAsync(endPoint, ct);
        if (!trust.IsTrusted(channel.PeerPublicKey)) throw new InvalidOperationException("Máy ở địa chỉ này chưa được ghép nối (hoặc khóa đã đổi). Hãy ghép nối lại trước khi gửi.");
        var info = new FileInfo(path);
        string hash;
        await using (var read = info.OpenRead()) hash = Convert.ToHexString(await SHA256.HashDataAsync(read, ct));
        await channel.SendJsonAsync(new { type = "file", name = info.Name, size = info.Length, sha256 = hash }, ct);
        var answer = await channel.ReceiveJsonAsync(ct);
        if (answer.GetProperty("type").GetString() != "accept") throw new InvalidOperationException(answer.TryGetProperty("reason", out var reason) ? reason.GetString() : "Máy nhận từ chối tệp.");
        var meter = new TransferMeter(progress, info.Length);
        await using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, true))
        {
            var buffer = new byte[1 << 20]; long sent = 0; int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0) { await channel.SendAsync(buffer.AsMemory(0, read), ct); sent += read; meter.Report(sent); }
            meter.Report(sent, true);
        }
        if ((await channel.ReceiveJsonAsync(ct)).GetProperty("type").GetString() != "ok") throw new IOException("Máy nhận báo tệp bị hỏng khi truyền. Hãy gửi lại.");
        SecurityLog.Write("lan-sent", $"Gửi tệp LAN {info.Extension} tới {channel.PeerName} ({channel.PeerId})");
    }

    private async Task<LanChannel> OpenAsync(IPEndPoint endPoint, CancellationToken ct)
    {
        var client = new TcpClient();
        try
        {
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connect.CancelAfter(TimeSpan.FromSeconds(8));
            await client.ConnectAsync(endPoint, connect.Token);
            return await LanChannel.ConnectAsync(client, me, initiator: true, ct);
        }
        catch { client.Dispose(); throw; }
    }
}
