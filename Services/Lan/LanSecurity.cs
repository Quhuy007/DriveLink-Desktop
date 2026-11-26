using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace DriveLink.Services.Lan;

/// <summary>Danh tính của máy này trong mạng LAN: khóa ECDSA P-256 riêng (lưu DPAPI) và mã thiết bị = dấu vân tay khóa công khai.</summary>
public sealed class LanIdentity
{
    private readonly ECDsa key;
    public string Id { get; }
    public string Name { get; }
    public byte[] PublicKey { get; }

    private LanIdentity(ECDsa key, string name)
    {
        this.key = key; Name = name;
        PublicKey = key.ExportSubjectPublicKeyInfo();
        Id = DeviceId(PublicKey);
    }

    public static string DeviceId(byte[] publicKey) => Convert.ToHexString(SHA256.HashData(publicKey))[..16].ToLowerInvariant();

    public static LanIdentity Load(string folder, string? name = null)
    {
        var file = Path.Combine(folder, "lan-identity.dat");
        var entropy = "DriveLink.LAN.Identity.v1"u8.ToArray();
        var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        if (File.Exists(file))
        {
            var pkcs8 = ProtectedData.Unprotect(File.ReadAllBytes(file), entropy, DataProtectionScope.CurrentUser);
            try { ecdsa.ImportPkcs8PrivateKey(pkcs8, out _); } finally { CryptographicOperations.ZeroMemory(pkcs8); }
        }
        else
        {
            Directory.CreateDirectory(folder);
            var pkcs8 = ecdsa.ExportPkcs8PrivateKey();
            try { File.WriteAllBytes(file, ProtectedData.Protect(pkcs8, entropy, DataProtectionScope.CurrentUser)); } finally { CryptographicOperations.ZeroMemory(pkcs8); }
        }
        return new LanIdentity(ecdsa, name ?? Environment.MachineName);
    }

    public byte[] Sign(byte[] data) => key.SignData(data, HashAlgorithmName.SHA256);
    public static bool Verify(byte[] publicKey, byte[] data, byte[] signature)
    {
        using var peer = ECDsa.Create();
        peer.ImportSubjectPublicKeyInfo(publicKey, out _);
        return peer.VerifyData(data, signature, HashAlgorithmName.SHA256);
    }
}

/// <summary>Các thiết bị đã ghép nối (khóa công khai đã được người dùng xác nhận bằng mã 6 số). Lưu DPAPI.</summary>
public sealed class LanTrustStore(string folder)
{
    private readonly ProtectedList<TrustedDevice> store = new(Path.Combine(folder, "lan-trusted.dat"), "DriveLink.LAN.Trusted.v1");
    public sealed class TrustedDevice
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string PublicKey { get; set; } = "";
        public DateTimeOffset PairedUtc { get; set; }
    }
    public IReadOnlyList<TrustedDevice> All => store.Load();
    public bool IsTrusted(byte[] publicKey) { var key = Convert.ToBase64String(publicKey); return store.Load().Any(d => d.PublicKey == key); }
    public void Add(string name, byte[] publicKey)
    {
        var all = store.Load(); var id = LanIdentity.DeviceId(publicKey);
        all.RemoveAll(d => d.Id == id);
        all.Add(new TrustedDevice { Id = id, Name = name, PublicKey = Convert.ToBase64String(publicKey), PairedUtc = DateTimeOffset.UtcNow });
        store.Save(all);
    }
    public void Remove(string id) { var all = store.Load(); all.RemoveAll(d => d.Id == id); store.Save(all); }
}

/// <summary>
/// Kênh mã hóa giữa hai máy: mỗi bên gửi khóa công khai cố định + khóa ECDH tạm thời, ký toàn bộ phần bắt tay bằng khóa cố định,
/// rồi trao đổi dữ liệu bằng AES-256-GCM với khóa phiên riêng cho từng chiều và bộ đếm nonce (chống phát lại, đảo thứ tự).
/// Mã ghép nối 6 số được tính từ bản ghi bắt tay: hai bên thấy cùng mã thì không có ai chen giữa.
/// </summary>
public sealed class LanChannel : IAsyncDisposable
{
    private const string Magic = "DLLAN1";
    public const int MaxFrame = (1 << 20) + 64 * 1024;
    /// <summary>
    /// Phiên bản giao thức: 1 = mỗi kết nối một tệp, băm SHA-256 trước khi gửi (DriveLink 1.3.0);
    /// 2 = gửi cả lô qua một kết nối, băm trong lúc gửi và gửi mã băm ở cuối mỗi tệp.
    /// Hai bên báo phiên bản trong lời chào (được ký cùng bản ghi bắt tay) và máy gửi dùng phiên bản của máy nhận nếu thấp hơn.
    /// </summary>
    public const int CurrentVersion = 2;
    private readonly NetworkStream stream;
    private readonly TcpClient client;
    private readonly AesGcm sendKey, receiveKey;
    private long sent, received;
    // Bộ đệm khung dùng lại cho mọi lần gửi/nhận (mỗi chiều chỉ một luồng dùng tại một thời điểm).
    private byte[]? sendFrame, receiveFrame;
    private readonly byte[] lengthBuffer = new byte[4];
    public byte[] PeerPublicKey { get; }
    /// <summary>Phiên bản giao thức của máy kia (1 nếu máy kia không báo — DriveLink 1.3.0).</summary>
    public int PeerVersion { get; }
    public string PeerName { get; }
    public string PeerId => LanIdentity.DeviceId(PeerPublicKey);
    /// <summary>Mã 6 số để hai người dùng so khi ghép nối.</summary>
    public string PairingCode { get; }

    private LanChannel(TcpClient client, NetworkStream stream, byte[] peerKey, string peerName, int peerVersion, byte[] sendKey, byte[] receiveKey, byte[] transcript)
    {
        this.client = client; this.stream = stream; PeerPublicKey = peerKey; PeerName = peerName; PeerVersion = peerVersion;
        this.sendKey = new AesGcm(sendKey, 16); this.receiveKey = new AesGcm(receiveKey, 16);
        var code = BinaryPrimitives.ReadUInt32BigEndian(SHA256.HashData([.. "DLLAN pairing"u8, .. transcript])) % 1_000_000;
        PairingCode = code.ToString("000000");
        CryptographicOperations.ZeroMemory(sendKey); CryptographicOperations.ZeroMemory(receiveKey);
    }

    public static async Task<LanChannel> ConnectAsync(TcpClient client, LanIdentity me, bool initiator, CancellationToken ct, int version = CurrentVersion)
    {
        Tune(client);
        var stream = client.GetStream();
        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var mine = Hello(me, ephemeral.ExportSubjectPublicKeyInfo(), version);
        byte[] theirs;
        if (initiator) { await WriteRaw(stream, mine, ct); theirs = await ReadRaw(stream, ct); }
        else { theirs = await ReadRaw(stream, ct); await WriteRaw(stream, mine, ct); }
        var (peerStatic, peerEphemeral, peerName, peerVersion) = ParseHello(theirs);
        var transcript = SHA256.HashData(initiator ? [.. mine, .. theirs] : [.. theirs, .. mine]);
        // Mỗi bên ký bản ghi bắt tay bằng khóa cố định: chứng minh sở hữu khóa, chống phát lại và chen giữa.
        var signature = me.Sign([.. Encoding.ASCII.GetBytes(Magic), (byte)(initiator ? 1 : 2), .. transcript]);
        byte[] peerSignature;
        if (initiator) { await WriteRaw(stream, signature, ct); peerSignature = await ReadRaw(stream, ct); }
        else { peerSignature = await ReadRaw(stream, ct); await WriteRaw(stream, signature, ct); }
        if (!LanIdentity.Verify(peerStatic, [.. Encoding.ASCII.GetBytes(Magic), (byte)(initiator ? 2 : 1), .. transcript], peerSignature))
            throw new CryptographicException("Thiết bị kia không chứng minh được danh tính.");
        using var peerKey = ECDiffieHellman.Create();
        peerKey.ImportSubjectPublicKeyInfo(peerEphemeral, out _);
        var secret = ephemeral.DeriveRawSecretAgreement(peerKey.PublicKey);
        var forward = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, transcript, "DLLAN1 initiator->responder"u8.ToArray());
        var backward = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, transcript, "DLLAN1 responder->initiator"u8.ToArray());
        CryptographicOperations.ZeroMemory(secret);
        return new LanChannel(client, stream, peerStatic, peerName, peerVersion, initiator ? forward : backward, initiator ? backward : forward, transcript);
    }

    /// <summary>
    /// Gửi ngay các gói nhỏ (lời chào, tiêu đề tệp, xác nhận) thay vì chờ gộp (thuật toán Nagle) — tránh khựng mỗi lượt hỏi-đáp;
    /// bộ đệm socket 1 MB để mạng luôn có sẵn dữ liệu chờ gửi.
    /// </summary>
    private static void Tune(TcpClient client)
    {
        try { client.NoDelay = true; client.SendBufferSize = 1 << 20; client.ReceiveBufferSize = 1 << 20; }
        catch (SocketException) { }
    }

    private static byte[] Hello(LanIdentity me, byte[] ephemeral, int version)
    {
        var name = me.Name.Length > 60 ? me.Name[..60] : me.Name;
        // Phiên bản 1 không có trường "v" — giữ đúng dạng của DriveLink 1.3.0 (dùng khi kiểm thử tương thích).
        return version >= 2
            ? JsonSerializer.SerializeToUtf8Bytes(new { magic = Magic, key = Convert.ToBase64String(me.PublicKey), eph = Convert.ToBase64String(ephemeral), name, v = version })
            : JsonSerializer.SerializeToUtf8Bytes(new { magic = Magic, key = Convert.ToBase64String(me.PublicKey), eph = Convert.ToBase64String(ephemeral), name });
    }
    private static (byte[] Static, byte[] Ephemeral, string Name, int Version) ParseHello(byte[] data)
    {
        using var json = JsonDocument.Parse(data);
        var root = json.RootElement;
        if (root.GetProperty("magic").GetString() != Magic) throw new CryptographicException("Không phải DriveLink.");
        var version = root.TryGetProperty("v", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? Math.Max(1, n) : 1;
        return (Convert.FromBase64String(root.GetProperty("key").GetString()!), Convert.FromBase64String(root.GetProperty("eph").GetString()!), root.GetProperty("name").GetString() ?? "", version);
    }

    public async Task SendAsync(ReadOnlyMemory<byte> plain, CancellationToken ct)
    {
        var length = Seal(plain.Span);
        await stream.WriteAsync(sendFrame.AsMemory(0, length), ct);
    }
    private int Seal(ReadOnlySpan<byte> plain)
    {
        if (plain.Length + 16 > MaxFrame) throw new ArgumentOutOfRangeException(nameof(plain), "Khung dữ liệu quá lớn.");
        var frame = sendFrame ??= new byte[4 + MaxFrame];
        BinaryPrimitives.WriteInt32BigEndian(frame, plain.Length + 16);
        Span<byte> nonce = stackalloc byte[12]; Nonce(sent++, nonce);
        sendKey.Encrypt(nonce, plain, frame.AsSpan(4, plain.Length), frame.AsSpan(4 + plain.Length, 16));
        return 4 + plain.Length + 16;
    }

    /// <summary>Nhận một khung và giải mã thẳng vào <paramref name="destination"/>; trả về số byte dữ liệu.</summary>
    public async ValueTask<int> ReceiveAsync(Memory<byte> destination, CancellationToken ct)
    {
        var size = await ReadFrameAsync(ct);
        if (size - 16 > destination.Length) throw new CryptographicException("Khung dữ liệu lớn hơn dự kiến.");
        return Open(size, destination.Span);
    }
    public async Task<byte[]> ReceiveAsync(CancellationToken ct)
    {
        var size = await ReadFrameAsync(ct);
        var plain = new byte[size - 16];
        Open(size, plain);
        return plain;
    }
    private async ValueTask<int> ReadFrameAsync(CancellationToken ct)
    {
        await stream.ReadExactlyAsync(lengthBuffer, ct);
        var size = BinaryPrimitives.ReadInt32BigEndian(lengthBuffer);
        if (size < 16 || size > MaxFrame) throw new CryptographicException("Khung dữ liệu quá lớn hoặc hỏng.");
        var frame = receiveFrame ??= new byte[MaxFrame];
        await stream.ReadExactlyAsync(frame.AsMemory(0, size), ct);
        return size;
    }
    private int Open(int size, Span<byte> destination)
    {
        var length = size - 16;
        Span<byte> nonce = stackalloc byte[12]; Nonce(received++, nonce);
        try { receiveKey.Decrypt(nonce, receiveFrame.AsSpan(0, length), receiveFrame.AsSpan(length, 16), destination[..length]); }
        catch (AuthenticationTagMismatchException) { throw new CryptographicException("Dữ liệu LAN bị sửa hoặc giả mạo."); }
        return length;
    }
    public Task SendJsonAsync(object message, CancellationToken ct) => SendAsync(JsonSerializer.SerializeToUtf8Bytes(message), ct);
    public async Task<JsonElement> ReceiveJsonAsync(CancellationToken ct) { using var json = JsonDocument.Parse(await ReceiveAsync(ct)); return json.RootElement.Clone(); }

    private static void Nonce(long counter, Span<byte> nonce) { nonce.Clear(); BinaryPrimitives.WriteInt64BigEndian(nonce[4..], counter); }
    private static async Task WriteRaw(NetworkStream stream, byte[] data, CancellationToken ct)
    {
        // Độ dài và nội dung đi chung một lần ghi, không tách thành hai gói nhỏ.
        var packet = new byte[4 + data.Length];
        BinaryPrimitives.WriteInt32BigEndian(packet, data.Length); data.CopyTo(packet, 4);
        await stream.WriteAsync(packet, ct);
    }
    private static async Task<byte[]> ReadRaw(NetworkStream stream, CancellationToken ct)
    {
        var length = new byte[4];
        await stream.ReadExactlyAsync(length, ct);
        var size = BinaryPrimitives.ReadInt32BigEndian(length);
        if (size <= 0 || size > MaxFrame) throw new CryptographicException("Khung dữ liệu quá lớn hoặc hỏng.");
        var data = new byte[size];
        await stream.ReadExactlyAsync(data, ct);
        return data;
    }

    public async ValueTask DisposeAsync()
    {
        sendKey.Dispose(); receiveKey.Dispose();
        await stream.DisposeAsync(); client.Dispose();
    }
}
