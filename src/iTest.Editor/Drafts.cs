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

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        SavedAt = DateTime.Now;
        // Mật khẩu bản nháp được lưu bằng DPAPI của Windows (chỉ người dùng hiện tại đọc được).
        var copy = new Draft { Id = Id, Exam = Exam, Locked = Locked, SavedAt = SavedAt, Password = Protect(Password) };
        File.WriteAllText(Path.Combine(Folder, "draft.json"), JsonSerializer.Serialize(copy, Json));
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

    public void Delete() { try { Directory.Delete(Folder, true); } catch { } }

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
