using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ITest.Core;

/// <summary>
/// Định dạng file bộ đề .itz:
///   "ITZ1" | flags(1 byte: bit0 = có mật khẩu) | salt(16) | nonce(12) | tag(16) | ciphertext
/// Bản rõ là một file zip gồm exam.json và thư mục media/.
/// Luôn được mã hoá AES-256-GCM; khi không đặt mật khẩu thì dùng khoá mặc định
/// (chỉ chống đọc trộm thông thường). Thẻ GCM đảm bảo file không bị sửa.
/// </summary>
public static class ExamPackage
{
    private static readonly byte[] Magic = "ITZ1"u8.ToArray();
    private const string DefaultSecret = "iTest-default-key-v1";
    private const int Iterations = 200_000;

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };

    public sealed class Content
    {
        public ExamSet Exam { get; set; } = new();
        public Dictionary<string, byte[]> Media { get; set; } = new();
    }

    public static bool IsPasswordProtected(string path)
    {
        using var fs = File.OpenRead(path);
        var head = new byte[5];
        if (fs.Read(head, 0, 5) != 5 || !head.AsSpan(0, 4).SequenceEqual(Magic))
            throw new InvalidDataException("File không phải bộ đề iTest (.itz).");
        return (head[4] & 1) == 1;
    }

    public static void Save(string path, ExamSet exam, IDictionary<string, byte[]> media, string? password)
    {
        byte[] zipBytes;
        using (var ms = new MemoryStream())
        {
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry = zip.CreateEntry("exam.json", CompressionLevel.Optimal);
                using (var s = entry.Open())
                    JsonSerializer.Serialize(s, exam, Json);
                foreach (var (name, data) in media)
                {
                    var e = zip.CreateEntry("media/" + name, CompressionLevel.Optimal);
                    using var s = e.Open();
                    s.Write(data, 0, data.Length);
                }
            }
            zipBytes = ms.ToArray();
        }

        bool locked = !string.IsNullOrEmpty(password);
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var key = DeriveKey(locked ? password! : DefaultSecret, salt);
        var cipher = new byte[zipBytes.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, 16))
            aes.Encrypt(nonce, zipBytes, cipher, tag);

        using var fs = File.Create(path);
        fs.Write(Magic);
        fs.WriteByte((byte)(locked ? 1 : 0));
        fs.Write(salt);
        fs.Write(nonce);
        fs.Write(tag);
        fs.Write(cipher);
    }

    /// <summary>Mở bộ đề. Ném <see cref="CryptographicException"/> nếu sai mật khẩu hoặc file bị sửa.</summary>
    public static Content Load(string path, string? password)
    {
        var all = File.ReadAllBytes(path);
        if (all.Length < 49 || !all.AsSpan(0, 4).SequenceEqual(Magic))
            throw new InvalidDataException("File không phải bộ đề iTest (.itz).");
        bool locked = (all[4] & 1) == 1;
        var salt = all.AsSpan(5, 16).ToArray();
        var nonce = all.AsSpan(21, 12).ToArray();
        var tag = all.AsSpan(33, 16).ToArray();
        var cipher = all.AsSpan(49).ToArray();
        var key = DeriveKey(locked ? (password ?? "") : DefaultSecret, salt);
        var plain = new byte[cipher.Length];
        using (var aes = new AesGcm(key, 16))
            aes.Decrypt(nonce, cipher, tag, plain);

        var content = new Content();
        using var zip = new ZipArchive(new MemoryStream(plain), ZipArchiveMode.Read);
        foreach (var e in zip.Entries)
        {
            using var s = e.Open();
            using var buf = new MemoryStream();
            s.CopyTo(buf);
            if (e.FullName == "exam.json")
                content.Exam = JsonSerializer.Deserialize<ExamSet>(buf.ToArray(), Json) ?? new ExamSet();
            else if (e.FullName.StartsWith("media/", StringComparison.Ordinal) && e.Name.Length > 0)
                content.Media[e.Name] = buf.ToArray();
        }
        return content;
    }

    private static byte[] DeriveKey(string secret, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(secret), salt, Iterations, HashAlgorithmName.SHA256, 32);
}
