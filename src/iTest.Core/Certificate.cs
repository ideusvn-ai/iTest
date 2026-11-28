using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ITest.Core;

/// <summary>
/// Chứng chỉ sử dụng iTest cấp cho một trường: tên Sở, tên trường, người đăng ký và hạn dùng, kèm chữ ký số của nơi cấp.
/// iTest-Server và iTest-Editor lấy tên Sở và tên trường từ đây (không còn ô nhập tay).
/// </summary>
public sealed class Certificate
{
    public string Id { get; init; } = "";
    public string Department { get; init; } = "";
    public string School { get; init; } = "";
    public string Contact { get; init; } = "";
    public string Email { get; init; } = "";
    public string Phone { get; init; } = "";
    public DateTime Issued { get; init; }
    /// <summary>Ngày cuối cùng còn hiệu lực; null = không có hạn.</summary>
    public DateTime? Expires { get; init; }
    /// <summary>Nguyên văn nội dung file chứng chỉ (để lưu lại và gửi cho máy thí sinh kiểm tra).</summary>
    public string Raw { get; init; } = "";

    public bool Expired => Expires is DateTime e && DateTime.Today > e.Date;
    public string ExpiryText => Expires is DateTime e ? $"đến hết {e:dd/MM/yyyy}" : "không thời hạn";
    public string Summary => $"{School} · {Department} · số {Id} · {ExpiryText}";
}

/// <summary>
/// Đọc, kiểm tra và lưu chứng chỉ. File chứng chỉ (.itcert) là JSON gồm "data" (nội dung, base64) và "sig"
/// (chữ ký RSA-SHA256 trên đúng các byte của nội dung). Ứng dụng chỉ chứa khoá CÔNG KHAI để kiểm tra;
/// khoá bí mật dùng để ký nằm ở máy chủ trang web, không có trong mã nguồn hay bộ cài.
/// </summary>
public static class CertificateStore
{
    public const string Extension = ".itcert";
    public const string Format = "iTest-Certificate-1";
    public const string FileFilter = "Chứng chỉ iTest (*.itcert)|*.itcert|Tất cả|*.*";

    private const string EmbeddedPublicKey = @"-----BEGIN PUBLIC KEY-----
MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEAsX3MJz5TRI6nErThB+7j
18w4h9BXWoz0/p/xEqD4e4HIGJZtUxi1OxlaUinLr6XNyWwfBUOFk+1MHluvjdxP
iHigWLbl0mnjkMC74LVWXM7pBNP0u1tyKtMA5pMwG+PjJnkC2OdSFayYrrlDC+Nm
PBr9LbwDomDwq0des1P+cFGos6ohQDkzqxz2LQ5tsA0FD1K8F4YTgpmjGn4luq3b
qF0xQVu8UAQuNouX2WUMkknMmAu+Tx6b+tG8wuYF9TgQoLChUzlGwGkMjPDdS4BM
TjZ4d6myA5nOexcAkjKDhiHhhxd+hRKqYZ0qwWiPq/le8IxASXsq4Fax2WiiKU+l
6l/JpncbJN0X6OGUkFzHeT93ItYNv+J0sIlDxlaUmEdF1MK0qSnddMcVQVPzJzCv
1vkVShNf+DnMBA/xn3FBC45TpN9WhaI63X3WdxjmXnKImsAN9aTWisABwTaNCs6R
QWaNpmkLuAxrkbvM+z3cVVCy3ryzEhxaRMNvpik/dJnRAgMBAAE=
-----END PUBLIC KEY-----";

    private static string _publicKeyPem = EmbeddedPublicKey;
    /// <summary>Khoá công khai dùng để kiểm tra chữ ký. Chỉ bộ kiểm thử mới được đổi (thuộc tính nội bộ).</summary>
    internal static string PublicKeyPem { get => _publicKeyPem; set => _publicKeyPem = value; }
    internal static void ResetPublicKey() => _publicKeyPem = EmbeddedPublicKey;

    private static InvalidDataException Bad(string why) => new("Chứng chỉ không hợp lệ: " + why);

    /// <summary>Đọc và kiểm tra chữ ký. Ném <see cref="InvalidDataException"/> nếu file hỏng, bị sửa hoặc không do nơi cấp ký. KHÔNG kiểm tra hạn dùng (xem <see cref="Certificate.Expired"/>).</summary>
    public static Certificate Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 20_000) throw Bad("file trống hoặc không đúng định dạng.");
        byte[] data, sig;
        try
        {
            if (JsonNode.Parse(text.Trim().TrimStart('\uFEFF')) is not JsonObject o) throw Bad("không đúng định dạng.");
            if (o.Str("format") != Format) throw Bad("không phải chứng chỉ iTest hoặc khác phiên bản.");
            data = Convert.FromBase64String(o.Str("data"));
            sig = Convert.FromBase64String(o.Str("sig"));
        }
        catch (InvalidDataException) { throw; }
        catch { throw Bad("không đúng định dạng."); }

        bool ok;
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(PublicKeyPem);
            ok = rsa.VerifyData(data, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch { ok = false; }
        if (!ok) throw Bad("chữ ký không đúng (file đã bị sửa hoặc không do iTest cấp).");

        try
        {
            if (JsonNode.Parse(Encoding.UTF8.GetString(data)) is not JsonObject p) throw Bad("nội dung trống.");
            var cert = new Certificate
            {
                Id = p.Str("id"), Department = p.Str("department").Trim(), School = p.Str("school").Trim(),
                Contact = p.Str("contact"), Email = p.Str("email"), Phone = p.Str("phone"),
                Issued = Date(p.Str("issued")) ?? default,
                Expires = Date(p.Str("expires")),
                Raw = text.Trim()
            };
            if (cert.School.Length == 0 || cert.Department.Length == 0) throw Bad("thiếu tên trường hoặc tên Sở.");
            return cert;
        }
        catch (InvalidDataException) { throw; }
        catch { throw Bad("nội dung không đọc được."); }
    }

    private static DateTime? Date(string s) =>
        DateTime.TryParseExact(s, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) ? d : null;

    public static Certificate Load(string path)
    {
        if (new FileInfo(path).Length > 20_000) throw Bad("file quá lớn.");
        return Parse(File.ReadAllText(path));
    }

    /// <summary>Đọc, kiểm tra chữ ký và kiểm tra hạn dùng: chứng chỉ dùng được ngay hôm nay.</summary>
    public static Certificate LoadUsable(string path) => Usable(Load(path));

    public static Certificate Usable(Certificate c) =>
        c.Expired ? throw new InvalidDataException($"Chứng chỉ của {c.School} đã hết hạn từ {c.Expires:dd/MM/yyyy}. Đăng nhập trang web iTest để lấy lại chứng chỉ mới.") : c;

    /// <summary>Tạo nội dung file chứng chỉ, ký bằng khoá bí mật. Dùng cho công cụ cấp chứng chỉ và bộ kiểm thử (trang web ký bằng PHP theo đúng định dạng này).</summary>
    public static string Issue(RSA privateKey, string id, string department, string school, string contact, string email, string phone, DateTime issued, DateTime? expires)
    {
        var payload = new JsonObject
        {
            ["id"] = id, ["department"] = department, ["school"] = school, ["contact"] = contact, ["email"] = email, ["phone"] = phone,
            ["issued"] = issued.ToString("yyyy-MM-dd"), ["expires"] = expires?.ToString("yyyy-MM-dd") ?? ""
        };
        var data = Encoding.UTF8.GetBytes(payload.ToJsonString());
        var sig = privateKey.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return new JsonObject { ["format"] = Format, ["data"] = Convert.ToBase64String(data), ["sig"] = Convert.ToBase64String(sig) }
            .ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Kiểm tra một file chứng chỉ cho bộ cài đặt (tham số --check-cert): 0 = dùng được, 2 = không hợp lệ, 3 = đã hết hạn.</summary>
    public static int Check(string path)
    {
        try { return Load(path).Expired ? 3 : 0; }
        catch { return 2; }
    }

    // ---------------- Chứng chỉ đã cài trên máy ----------------
    private const string FileName = "chung-chi.itcert";
    /// <summary>Nơi bộ cài đặt đặt chứng chỉ (dùng chung cho mọi tài khoản Windows trên máy).</summary>
    public static string MachinePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "iTest", FileName);
    /// <summary>Nơi ứng dụng lưu chứng chỉ khi người dùng chọn lại trong ứng dụng (không cần quyền quản trị).</summary>
    public static string UserPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "iTest", FileName);

    /// <summary>
    /// Chứng chỉ đang cài trên máy và còn dùng được; null nếu chưa có hoặc không hợp lệ (lý do ở <paramref name="problem"/>).
    /// Ưu tiên bản người dùng chọn trong ứng dụng, sau đó tới bản do bộ cài đặt đặt.
    /// </summary>
    public static Certificate? Installed(out string problem)
    {
        problem = "Máy này chưa có chứng chỉ sử dụng.";
        foreach (var path in new[] { UserPath, MachinePath })
        {
            try
            {
                if (!File.Exists(path)) continue;
                return LoadUsable(path);
            }
            catch (Exception ex) { problem = ex is InvalidDataException ? ex.Message : "Không đọc được file chứng chỉ đã cài."; }
        }
        return null;
    }

    /// <summary>Kiểm tra file chứng chỉ người dùng chọn rồi lưu làm chứng chỉ của máy (trong hồ sơ người dùng). Ném lỗi kèm lý do nếu không dùng được.</summary>
    public static Certificate Install(string sourcePath)
    {
        var cert = LoadUsable(sourcePath);
        SafeFile.WriteAllText(UserPath, cert.Raw);
        return cert;
    }
}
