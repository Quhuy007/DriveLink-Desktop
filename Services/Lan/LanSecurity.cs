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
    private readonly NetworkStream stream;
    private readonly TcpClient client;
    private readonly AesGcm sendKey, receiveKey;
    private long sent, received;
    public byte[] PeerPublicKey { get; }
    public string PeerName { get; }
    public string PeerId => LanIdentity.DeviceId(PeerPublicKey);
    /// <summary>Mã 6 số để hai người dùng so khi ghép nối.</summary>
    public string PairingCode { get; }

    private LanChannel(TcpClient client, NetworkStream stream, byte[] peerKey, string peerName, byte[] sendKey, byte[] receiveKey, byte[] transcript)
    {
        this.client = client; this.stream = stream; PeerPublicKey = peerKey; PeerName = peerName;
        this.sendKey = new AesGcm(sendKey, 16); this.receiveKey = new AesGcm(receiveKey, 16);
        var code = BinaryPrimitives.ReadUInt32BigEndian(SHA256.HashData([.. "DLLAN pairing"u8, .. transcript])) % 1_000_000;
        PairingCode = code.ToString("000000");
        CryptographicOperations.ZeroMemory(sendKey); CryptographicOperations.ZeroMemory(receiveKey);
    }

    public static async Task<LanChannel> ConnectAsync(TcpClient client, LanIdentity me, bool initiator, CancellationToken ct)
    {
        var stream = client.GetStream();
        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var mine = Hello(me, ephemeral.ExportSubjectPublicKeyInfo());
        byte[] theirs;
        if (initiator) { await WriteRaw(stream, mine, ct); theirs = await ReadRaw(stream, ct); }
        else { theirs = await ReadRaw(stream, ct); await WriteRaw(stream, mine, ct); }
        var (peerStatic, peerEphemeral, peerName) = ParseHello(theirs);
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
        return new LanChannel(client, stream, peerStatic, peerName, initiator ? forward : backward, initiator ? backward : forward, transcript);
    }

    private static byte[] Hello(LanIdentity me, byte[] ephemeral)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new { magic = Magic, key = Convert.ToBase64String(me.PublicKey), eph = Convert.ToBase64String(ephemeral), name = me.Name.Length > 60 ? me.Name[..60] : me.Name });
        return json;
    }
    private static (byte[] Static, byte[] Ephemeral, string Name) ParseHello(byte[] data)
    {
        using var json = JsonDocument.Parse(data);
        var root = json.RootElement;
        if (root.GetProperty("magic").GetString() != Magic) throw new CryptographicException("Không phải DriveLink.");
        return (Convert.FromBase64String(root.GetProperty("key").GetString()!), Convert.FromBase64String(root.GetProperty("eph").GetString()!), root.GetProperty("name").GetString() ?? "");
    }

    public async Task SendAsync(ReadOnlyMemory<byte> plain, CancellationToken ct)
    {
        var frame = new byte[4 + plain.Length + 16];
        BinaryPrimitives.WriteInt32BigEndian(frame, plain.Length + 16);
        sendKey.Encrypt(Nonce(sent++), plain.Span, frame.AsSpan(4, plain.Length), frame.AsSpan(4 + plain.Length, 16));
        await stream.WriteAsync(frame, ct);
    }
    public async Task<byte[]> ReceiveAsync(CancellationToken ct)
    {
        var frame = await ReadRaw(stream, ct);
        if (frame.Length < 16) throw new CryptographicException("Khung dữ liệu không hợp lệ.");
        var plain = new byte[frame.Length - 16];
        try { receiveKey.Decrypt(Nonce(received++), frame.AsSpan(0, plain.Length), frame.AsSpan(plain.Length), plain); }
        catch (AuthenticationTagMismatchException) { throw new CryptographicException("Dữ liệu LAN bị sửa hoặc giả mạo."); }
        return plain;
    }
    public Task SendJsonAsync(object message, CancellationToken ct) => SendAsync(JsonSerializer.SerializeToUtf8Bytes(message), ct);
    public async Task<JsonElement> ReceiveJsonAsync(CancellationToken ct) { using var json = JsonDocument.Parse(await ReceiveAsync(ct)); return json.RootElement.Clone(); }

    private static byte[] Nonce(long counter) { var nonce = new byte[12]; BinaryPrimitives.WriteInt64BigEndian(nonce.AsSpan(4), counter); return nonce; }
    private static async Task WriteRaw(NetworkStream stream, byte[] data, CancellationToken ct)
    {
        var length = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        await stream.WriteAsync(length, ct); await stream.WriteAsync(data, ct);
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
