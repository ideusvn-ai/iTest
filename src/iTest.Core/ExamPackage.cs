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
        private string? _fingerprint;
        /// <summary>Dấu vân tay SHA-256 của nội dung đề và ảnh/âm thanh (tính một lần, xem <see cref="ExamPackage.Fingerprint"/>).</summary>
        public string Fingerprint => _fingerprint ??= ExamPackage.Fingerprint(Exam, Media);
    }

    // ---------------- Giới hạn và kiểm tra cấu trúc ----------------
    public const int MaxQuestions = 500;
    public const int MaxOptions = 10;
    public const int MaxMinutes = 600;
    public const int MaxCodes = 24;
    public const int MaxTextChars = 20_000;
    public const long MaxMediaFileBytes = 25L * 1024 * 1024;
    public const long MaxMediaTotalBytes = 200L * 1024 * 1024;
    public const long MaxPackageBytes = 300L * 1024 * 1024;
    private const int MaxExamJsonBytes = 16 * 1024 * 1024;
    private const int MaxEntries = 2 * MaxQuestions + 8;
    private static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".bmp" };
    private static readonly HashSet<string> AudioExts = new(StringComparer.OrdinalIgnoreCase) { ".mp3", ".wav", ".m4a" };

    /// <summary>Tên file ảnh/âm thanh hợp lệ: chỉ là tên file (không đường dẫn), ký tự an toàn, đúng phần mở rộng.</summary>
    public static bool IsSafeMediaName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 100 || name[0] == '.' || name.Contains("..")) return false;
        foreach (var ch in name)
            if (!(char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_')) return false;
        var ext = Path.GetExtension(name);
        return ImageExts.Contains(ext) || AudioExts.Contains(ext);
    }

    private static InvalidDataException Bad(string what) => new("Bộ đề không hợp lệ: " + what);

    /// <summary>
    /// Kiểm tra bộ đề trước khi dùng (khi mở và khi đóng gói): số câu, mã câu trùng, điểm, phương án, đáp án trong phạm vi,
    /// tên và kích thước file ảnh/âm thanh. Ném <see cref="InvalidDataException"/> kèm lý do nếu không đạt.
    /// Các danh sách bị thiếu (null) trong file được thay bằng danh sách rỗng.
    /// </summary>
    public static void Validate(ExamSet exam, IDictionary<string, byte[]> media)
    {
        if (exam == null) throw Bad("thiếu nội dung đề.");
        exam.Questions ??= new();
        exam.ExamName ??= ""; exam.Subject ??= ""; exam.SchoolYear ??= ""; exam.Author ??= ""; exam.Unit ??= "";
        if (exam.Questions.Count == 0) throw Bad("không có câu hỏi nào.");
        if (exam.Questions.Count > MaxQuestions) throw Bad($"quá {MaxQuestions} câu hỏi.");
        if (exam.Minutes < 1 || exam.Minutes > MaxMinutes) throw Bad($"thời gian làm bài phải từ 1 đến {MaxMinutes} phút.");
        if (exam.Codes < 1 || exam.Codes > MaxCodes) throw Bad($"số mã đề phải từ 1 đến {MaxCodes}.");
        if (exam.ExamName.Length > 300 || exam.Subject.Length > 300) throw Bad("tên kỳ thi hoặc môn thi quá dài.");

        long total = 0;
        foreach (var (name, data) in media)
        {
            if (!IsSafeMediaName(name)) throw Bad($"tên file ảnh/âm thanh không hợp lệ ({name}).");
            if (data == null || data.Length == 0) throw Bad($"file {name} rỗng.");
            if (data.Length > MaxMediaFileBytes) throw Bad($"file {name} lớn hơn {MaxMediaFileBytes / 1024 / 1024} MB.");
            total += data.Length;
        }
        if (total > MaxMediaTotalBytes) throw Bad($"tổng dung lượng ảnh/âm thanh lớn hơn {MaxMediaTotalBytes / 1024 / 1024} MB.");

        var ids = new HashSet<int>();
        double sum = 0;
        for (int i = 0; i < exam.Questions.Count; i++)
        {
            var q = exam.Questions[i] ?? throw Bad($"câu thứ {i + 1} bị trống.");
            string at = $"câu {i + 1}";
            q.Text ??= ""; q.Options ??= new(); q.Corrects ??= new(); q.TfKey ??= new(); q.ShortKey ??= ""; q.Guide ??= "";
            if (!Enum.IsDefined(q.Type)) throw Bad($"{at} có loại câu không được hỗ trợ.");
            if (q.Id <= 0 || !ids.Add(q.Id)) throw Bad($"{at} có mã câu trùng hoặc không hợp lệ ({q.Id}).");
            if (double.IsNaN(q.Points) || double.IsInfinity(q.Points) || q.Points < 0 || q.Points > 100) throw Bad($"{at} có điểm không hợp lệ.");
            sum += q.Points;
            if (q.Text.Length > MaxTextChars || q.Guide.Length > MaxTextChars || q.ShortKey.Length > 2000) throw Bad($"{at} có nội dung quá dài.");
            if (q.Options.Count > MaxOptions) throw Bad($"{at} có quá {MaxOptions} phương án.");
            for (int k = 0; k < q.Options.Count; k++)
            {
                q.Options[k] ??= "";
                if (q.Options[k].Length > MaxTextChars) throw Bad($"{at} có phương án quá dài.");
            }
            if (q.Type.HasOptions() && q.Options.Count < 2) throw Bad($"{at} phải có ít nhất 2 phương án.");
            if (q.Type == QuestionType.YesNo && q.Options.Count != 2) throw Bad($"{at} (đúng/sai) phải có đúng 2 phương án.");
            if (q.Type.IsSingleChoice() && (q.Correct < -1 || q.Correct >= q.Options.Count)) throw Bad($"{at} có đáp án ngoài phạm vi phương án.");
            if (q.Type == QuestionType.MultiSelect && (q.Corrects.Count != q.Corrects.Distinct().Count() || q.Corrects.Any(c => c < 0 || c >= q.Options.Count)))
                throw Bad($"{at} có đáp án ngoài phạm vi phương án.");
            if (q.Type == QuestionType.TrueFalse && q.TfKey.Count != 0 && q.TfKey.Count != q.Options.Count) throw Bad($"{at} có số đáp án Đúng/Sai không khớp số ý.");
            if (q.Image != null && (!ImageExts.Contains(Path.GetExtension(q.Image)) || !media.ContainsKey(q.Image))) throw Bad($"{at} dùng ảnh không có trong bộ đề.");
            if (q.Audio != null && (!AudioExts.Contains(Path.GetExtension(q.Audio)) || !media.ContainsKey(q.Audio))) throw Bad($"{at} dùng âm thanh không có trong bộ đề.");
            if (q.AudioMaxPlays < 1 || q.AudioMaxPlays > 100) q.AudioMaxPlays = 2;
        }
        if (sum > 1000) throw Bad("tổng điểm không hợp lệ.");
        double part(double v) => double.IsNaN(v) || double.IsInfinity(v) || v < 0 || v > 1000 ? throw Bad("tổng điểm từng phần không hợp lệ.") : v;
        part(exam.McTotal); part(exam.EssayTotal);
    }

    /// <summary>
    /// Dấu vân tay của bộ đề: SHA-256 trên nội dung đề (không tính ngày đóng gói) và từng file ảnh/âm thanh.
    /// Hai file .itz cùng số câu nhưng khác nội dung sẽ cho dấu khác nhau — dùng để không khôi phục nhầm bộ đề.
    /// </summary>
    public static string Fingerprint(ExamSet exam, IDictionary<string, byte[]> media)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var copy = JsonSerializer.Deserialize<ExamSet>(JsonSerializer.SerializeToUtf8Bytes(exam, Json), Json) ?? new ExamSet();
        copy.CreatedAt = default;
        sha.AppendData(JsonSerializer.SerializeToUtf8Bytes(copy, Json));
        foreach (var name in media.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            sha.AppendData(Encoding.UTF8.GetBytes("\n" + name + "\n"));
            sha.AppendData(SHA256.HashData(media[name]));
        }
        return Convert.ToHexString(sha.GetHashAndReset());
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
        Validate(exam, media);
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

        // Ghi ra file tạm rồi mới thay file chính: đóng gói dở dang không làm hỏng file bộ đề đang có.
        using var ms2 = new MemoryStream(49 + cipher.Length);
        ms2.Write(Magic);
        ms2.WriteByte((byte)(locked ? 1 : 0));
        ms2.Write(salt);
        ms2.Write(nonce);
        ms2.Write(tag);
        ms2.Write(cipher);
        SafeFile.WriteAllBytes(path, ms2.ToArray());
    }

    /// <summary>Mở bộ đề. Ném <see cref="CryptographicException"/> nếu sai mật khẩu hoặc file bị sửa.</summary>
    public static Content Load(string path, string? password)
    {
        if (new FileInfo(path).Length > MaxPackageBytes) throw Bad($"file lớn hơn {MaxPackageBytes / 1024 / 1024} MB.");
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
        bool hasExam = false;
        long unpacked = 0;
        try
        {
            using var zip = new ZipArchive(new MemoryStream(plain), ZipArchiveMode.Read);
            if (zip.Entries.Count > MaxEntries) throw Bad("quá nhiều file bên trong.");
            foreach (var e in zip.Entries)
            {
                if (e.FullName.EndsWith('/')) continue;   // mục thư mục
                bool isExam = e.FullName == "exam.json";
                bool isMedia = !isExam && e.FullName.StartsWith("media/", StringComparison.Ordinal) && e.FullName.Length == 6 + e.Name.Length;
                if (!isExam && !isMedia) throw Bad($"có file lạ bên trong ({e.FullName}).");
                if (isMedia && (!IsSafeMediaName(e.Name) || content.Media.ContainsKey(e.Name))) throw Bad($"tên file ảnh/âm thanh không hợp lệ ({e.Name}).");
                if (isExam && hasExam) throw Bad("có hai file exam.json.");
                long cap = isExam ? MaxExamJsonBytes : MaxMediaFileBytes;
                var data = ReadCapped(e, cap) ?? throw Bad($"file {e.Name} quá lớn.");
                unpacked += data.Length;
                if (unpacked > MaxMediaTotalBytes + MaxExamJsonBytes) throw Bad("dung lượng sau khi giải nén quá lớn.");
                if (isExam)
                {
                    hasExam = true;
                    content.Exam = JsonSerializer.Deserialize<ExamSet>(data, Json) ?? throw Bad("exam.json trống.");
                }
                else content.Media[e.Name] = data;
            }
        }
        catch (JsonException) { throw Bad("nội dung đề (exam.json) sai định dạng."); }
        catch (InvalidDataException ex) when (!ex.Message.StartsWith("Bộ đề không hợp lệ")) { throw Bad("cấu trúc nén bên trong bị hỏng."); }
        if (!hasExam) throw Bad("thiếu exam.json.");
        Validate(content.Exam, content.Media);
        _ = content.Fingerprint;   // tính sẵn ở luồng nền (mở bộ đề), không để luồng giao diện phải băm ảnh/âm thanh
        return content;
    }

    /// <summary>Đọc một mục trong zip nhưng dừng khi vượt <paramref name="cap"/> byte (chống file nén "bom").</summary>
    private static byte[]? ReadCapped(ZipArchiveEntry e, long cap)
    {
        using var s = e.Open();
        using var buf = new MemoryStream();
        var chunk = new byte[81920];
        int n;
        while ((n = s.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buf.Length + n > cap) return null;
            buf.Write(chunk, 0, n);
        }
        return buf.ToArray();
    }

    private static byte[] DeriveKey(string secret, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(secret), salt, Iterations, HashAlgorithmName.SHA256, 32);
}
