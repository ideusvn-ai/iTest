using System.IO;
using System.Text.Json;
using ITest.Core;

namespace ITest.Editor;

/// <summary>Bản nháp bộ đề đang soạn: lưu tự động vào %AppData%\iTest-Editor\Drafts\{Id}.</summary>
public sealed class Draft
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public ExamSet Exam { get; set; } = new();
    public bool Locked { get; set; }
    public string Password { get; set; } = "";
    public DateTime SavedAt { get; set; }

    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "iTest-Editor", "Drafts");
    public string Folder => Path.Combine(Root, Id);
    public string MediaFolder => Path.Combine(Folder, "media");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly object _writeLock = new();
    private bool _deleted;

    /// <summary>
    /// Lưu bản nháp. Mặc định dựng JSON ngay (ảnh chụp nội dung) rồi ghi đĩa ở luồng nền để không giật khi gõ;
    /// đặt <paramref name="sync"/>=true khi cần ghi xong ngay (lúc thoát).
    /// </summary>
    public void Save(bool sync = false)
    {
        SavedAt = DateTime.Now;
        // Mật khẩu bản nháp được lưu bằng DPAPI của Windows (chỉ người dùng hiện tại đọc được).
        var copy = new Draft { Id = Id, Exam = Exam, Locked = Locked, SavedAt = SavedAt, Password = Protect(Password) };
        var json = JsonSerializer.Serialize(copy, Json);
        var folder = Folder;
        void Write()
        {
            lock (_writeLock)
            {
                if (_deleted) return;   // bản nháp đã bị xoá trong lúc chờ ghi: không tạo lại thư mục
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "draft.json"), json);
            }
        }
        if (sync) Write(); else Task.Run(() => { try { Write(); } catch { } });
    }

    public static List<Draft> All()
    {
        var list = new List<Draft>();
        if (!Directory.Exists(Root)) return list;
        foreach (var dir in Directory.GetDirectories(Root))
        {
            try
            {
                var f = Path.Combine(dir, "draft.json");
                if (!File.Exists(f)) continue;
                var d = JsonSerializer.Deserialize<Draft>(File.ReadAllText(f));
                if (d == null) continue;
                d.Password = Unprotect(d.Password);
                list.Add(d);
            }
            catch { }
        }
        return list.OrderByDescending(d => d.SavedAt).ToList();
    }

    public void Delete()
    {
        lock (_writeLock)
        {
            _deleted = true;
            try { if (Directory.Exists(Folder)) Directory.Delete(Folder, true); } catch { }
        }
    }

    public Dictionary<string, byte[]> LoadMedia()
    {
        var m = new Dictionary<string, byte[]>();
        if (Directory.Exists(MediaFolder))
            foreach (var f in Directory.GetFiles(MediaFolder)) m[Path.GetFileName(f)] = File.ReadAllBytes(f);
        return m;
    }

    public string AddMedia(string sourcePath)
    {
        Directory.CreateDirectory(MediaFolder);
        var name = $"{Guid.NewGuid():N}{Path.GetExtension(sourcePath).ToLowerInvariant()}";
        File.Copy(sourcePath, Path.Combine(MediaFolder, name));
        return name;
    }

    public string AddMedia(byte[] data, string ext)
    {
        Directory.CreateDirectory(MediaFolder);
        var name = $"{Guid.NewGuid():N}{ext}";
        File.WriteAllBytes(Path.Combine(MediaFolder, name), data);
        return name;
    }

    private static string Protect(string s)
    {
        if (string.IsNullOrEmpty(s) || !OperatingSystem.IsWindows()) return "";
        var b = System.Security.Cryptography.ProtectedData.Protect(System.Text.Encoding.UTF8.GetBytes(s), null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(b);
    }

    private static string Unprotect(string s)
    {
        if (string.IsNullOrEmpty(s) || !OperatingSystem.IsWindows()) return "";
        try
        {
            var b = System.Security.Cryptography.ProtectedData.Unprotect(Convert.FromBase64String(s), null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
            return System.Text.Encoding.UTF8.GetString(b);
        }
        catch { return ""; }
    }
}
