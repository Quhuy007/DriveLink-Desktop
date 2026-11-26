using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
namespace DriveLink.Services;

/// <summary>
/// Mã hóa đầu cuối (tùy chọn theo không gian): tệp được mã hóa trên máy gửi trước khi tải lên và chỉ giải mã trên máy nhận,
/// nên Google và người không có mật khẩu không gian chỉ thấy dữ liệu đã mã hóa.
/// - Khóa không gian = PBKDF2-SHA256(mật khẩu không gian, muối từ mã thư mục không gian, 600.000 vòng).
/// - Mỗi tệp có muối ngẫu nhiên riêng; khóa tệp = HKDF(khóa không gian, muối tệp).
/// - Nội dung chia khối 1 MB, mỗi khối AES-256-GCM với số thứ tự và cờ "khối cuối" trong dữ liệu xác thực:
///   sửa, đảo thứ tự hay cắt bớt khối đều bị phát hiện.
/// Tên tệp và kích thước gần đúng vẫn thấy được trên Drive.
/// </summary>
public static class E2eCrypto
{
    public const string Extension = ".dlenc";
    public const int ChunkSize = 1 << 20;
    public const int Iterations = 600_000;
    private static readonly byte[] Magic = "DLE2E1"u8.ToArray();
    private const byte Version = 1;
    private const int HeaderLength = 6 + 1 + 16 + 4;

    public static byte[] DeriveSpaceKey(string passphrase, string spaceId)
    {
        var salt = SHA256.HashData(Encoding.UTF8.GetBytes("DriveLink.E2E.v1|" + spaceId));
        return Rfc2898DeriveBytes.Pbkdf2(passphrase.Normalize(NormalizationForm.FormC), salt, Iterations, HashAlgorithmName.SHA256, 32);
    }

    /// <summary>Mã kiểm tra ngắn để các thành viên so với nhau xem đã nhập cùng mật khẩu (không lộ khóa).</summary>
    public static string Fingerprint(byte[] spaceKey)
    {
        var hash = HMACSHA256.HashData(spaceKey, "DriveLink fingerprint"u8);
        var hex = Convert.ToHexString(hash[..6]);
        return $"{hex[..4]}-{hex[4..8]}-{hex[8..12]}";
    }

    public static async Task EncryptAsync(Stream input, Stream output, byte[] spaceKey, CancellationToken ct)
    {
        var header = new byte[HeaderLength];
        Magic.CopyTo(header, 0); header[6] = Version;
        RandomNumberGenerator.Fill(header.AsSpan(7, 20)); // 16 byte muối tệp + 4 byte tiền tố nonce.
        await output.WriteAsync(header, ct);
        using var aes = new AesGcm(FileKey(spaceKey, header.AsSpan(7, 16)), 16);
        var plain = new byte[ChunkSize];
        var next = new byte[ChunkSize];
        var filled = await FillAsync(input, plain, ct);
        long index = 0;
        while (true)
        {
            // Đọc trước khối kế tiếp để biết khối hiện tại có phải khối cuối không.
            var nextFilled = filled == ChunkSize ? await FillAsync(input, next, ct) : 0;
            var final = nextFilled == 0;
            var cipher = new byte[filled];
            var tag = new byte[16];
            aes.Encrypt(Nonce(header, index), plain.AsSpan(0, filled), cipher, tag, Aad(header, index, final));
            var record = new byte[5];
            record[0] = final ? (byte)1 : (byte)0;
            BinaryPrimitives.WriteInt32BigEndian(record.AsSpan(1), filled);
            await output.WriteAsync(record, ct);
            await output.WriteAsync(cipher, ct);
            await output.WriteAsync(tag, ct);
            if (final) break;
            (plain, next) = (next, plain); filled = nextFilled; index++;
        }
        CryptographicOperations.ZeroMemory(plain); CryptographicOperations.ZeroMemory(next);
    }

    /// <summary>Giải mã; sai mật khẩu, dữ liệu bị sửa hoặc bị cắt đều báo <see cref="CryptographicException"/>.</summary>
    public static async Task DecryptAsync(Stream input, Stream output, byte[] spaceKey, CancellationToken ct)
    {
        var header = new byte[HeaderLength];
        if (await FillAsync(input, header, ct) != HeaderLength || !header.AsSpan(0, 6).SequenceEqual(Magic) || header[6] != Version)
            throw new CryptographicException("Tệp không phải định dạng mã hóa của DriveLink.");
        using var aes = new AesGcm(FileKey(spaceKey, header.AsSpan(7, 16)), 16);
        var record = new byte[5];
        long index = 0;
        while (true)
        {
            if (await FillAsync(input, record, ct) != 5) throw new CryptographicException("Tệp mã hóa bị cắt mất phần cuối.");
            var final = record[0] switch { 0 => false, 1 => true, _ => throw new CryptographicException("Tệp mã hóa bị hỏng.") };
            var length = BinaryPrimitives.ReadInt32BigEndian(record.AsSpan(1));
            if (length < 0 || length > ChunkSize) throw new CryptographicException("Tệp mã hóa bị hỏng.");
            var cipher = new byte[length]; var tag = new byte[16];
            if (await FillAsync(input, cipher, ct) != length || await FillAsync(input, tag, ct) != 16) throw new CryptographicException("Tệp mã hóa bị cắt mất phần cuối.");
            var plain = new byte[length];
            try { aes.Decrypt(Nonce(header, index), cipher, tag, plain, Aad(header, index, final)); }
            catch (AuthenticationTagMismatchException) { throw new CryptographicException("Sai mật khẩu không gian hoặc tệp đã bị sửa."); }
            await output.WriteAsync(plain, ct);
            CryptographicOperations.ZeroMemory(plain);
            if (final)
            {
                if (await input.ReadAsync(new byte[1], ct) != 0) throw new CryptographicException("Tệp mã hóa có dữ liệu thừa sau khối cuối.");
                return;
            }
            index++;
        }
    }

    public static async Task EncryptFileAsync(string source, string destination, byte[] spaceKey, CancellationToken ct)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, true);
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true);
        await EncryptAsync(input, output, spaceKey, ct);
    }

    /// <summary>Giải mã ra tệp tạm rồi mới thay tệp đích, để lỗi giữa chừng không để lại tệp hỏng.</summary>
    public static async Task DecryptFileAsync(string source, string destination, byte[] spaceKey, CancellationToken ct)
    {
        var temp = destination + ".decrypting";
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, true))
            await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true))
                await DecryptAsync(input, output, spaceKey, ct);
            File.Move(temp, destination, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static byte[] FileKey(byte[] spaceKey, ReadOnlySpan<byte> salt) => HKDF.DeriveKey(HashAlgorithmName.SHA256, spaceKey, 32, salt.ToArray(), "DriveLink E2E file v1"u8.ToArray());
    private static byte[] Nonce(byte[] header, long index)
    {
        var nonce = new byte[12];
        header.AsSpan(23, 4).CopyTo(nonce);
        BinaryPrimitives.WriteInt64BigEndian(nonce.AsSpan(4), index);
        return nonce;
    }
    private static byte[] Aad(byte[] header, long index, bool final)
    {
        var aad = new byte[HeaderLength + 9];
        header.CopyTo(aad, 0);
        BinaryPrimitives.WriteInt64BigEndian(aad.AsSpan(HeaderLength), index);
        aad[^1] = final ? (byte)1 : (byte)0;
        return aad;
    }
    private static async Task<int> FillAsync(Stream input, byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await input.ReadAsync(buffer.AsMemory(total), ct);
            if (read == 0) break;
            total += read;
        }
        return total;
    }
}

/// <summary>Khóa mã hóa đầu cuối của từng không gian trên máy này (mã hóa DPAPI). Mật khẩu không gian không được lưu.</summary>
public sealed class E2eKeys(string folder)
{
    private readonly ProtectedList<Entry> store = new(Path.Combine(folder, "e2e-keys.dat"), "DriveLink.E2E.Keys.v1");
    public sealed class Entry
    {
        public string SpaceId { get; set; } = "";
        public string SpaceName { get; set; } = "";
        public string Key { get; set; } = "";
        public string Fingerprint { get; set; } = "";
        public DateTimeOffset AddedUtc { get; set; }
    }
    public byte[]? KeyFor(string spaceId) => store.Load().FirstOrDefault(e => e.SpaceId == spaceId) is { } e ? Convert.FromBase64String(e.Key) : null;
    public Entry? Find(string spaceId) => store.Load().FirstOrDefault(e => e.SpaceId == spaceId);
    public Entry Set(string spaceId, string spaceName, string passphrase)
    {
        var key = E2eCrypto.DeriveSpaceKey(passphrase, spaceId);
        var entry = new Entry { SpaceId = spaceId, SpaceName = spaceName, Key = Convert.ToBase64String(key), Fingerprint = E2eCrypto.Fingerprint(key), AddedUtc = DateTimeOffset.UtcNow };
        var all = store.Load(); all.RemoveAll(e => e.SpaceId == spaceId); all.Add(entry); store.Save(all);
        CryptographicOperations.ZeroMemory(key);
        return entry;
    }
    public void Remove(string spaceId) { var all = store.Load(); all.RemoveAll(e => e.SpaceId == spaceId); store.Save(all); }
}
