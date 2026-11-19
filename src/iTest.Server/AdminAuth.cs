using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace ITest.Server;

/// <summary>
/// Mật khẩu quản trị iTest-Server. Chỉ lưu giá trị băm PBKDF2-SHA256 (200 000 vòng, muối ngẫu nhiên) trong
/// %ProgramData%\iTest-Server\admin.json — dùng chung cho mọi tài khoản Windows trên máy chủ.
/// </summary>
public static class AdminAuth
{
    private sealed record Store(string Salt, string Hash, int Iterations, DateTime ChangedAt);

    public static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "iTest-Server", "admin.json");
    public static bool IsSet => File.Exists(FilePath);
    public const int MinLength = 6;

    public static void Set(string password)
    {
        if (password.Length < MinLength) throw new ArgumentException($"Mật khẩu cần ít nhất {MinLength} ký tự.");
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 200_000, HashAlgorithmName.SHA256, 32);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(new Store(Convert.ToBase64String(salt), Convert.ToBase64String(hash), 200_000, DateTime.Now)));
    }

    public static bool Verify(string password)
    {
        try
        {
            var s = JsonSerializer.Deserialize<Store>(File.ReadAllText(FilePath));
            if (s == null) return false;
            var hash = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(s.Salt), s.Iterations, HashAlgorithmName.SHA256, 32);
            return CryptographicOperations.FixedTimeEquals(hash, Convert.FromBase64String(s.Hash));
        }
        catch { return false; }
    }
}
