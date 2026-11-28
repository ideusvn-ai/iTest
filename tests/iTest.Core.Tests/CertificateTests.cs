using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace ITest.Core.Tests;

/// <summary>Chứng chỉ sử dụng: chỉ file do đúng khoá bí mật ký, chưa bị sửa và còn hạn mới dùng được.</summary>
public class CertificateTests
{
    private static string Issue(RSA key, DateTime? expires, string school = "Trường THPT Thử") =>
        CertificateStore.Issue(key, "IT-00001-01", "Sở Giáo dục và Đào tạo Thử", school, "Nguyễn Văn An", "an@truong.edu.vn", "0900000000", DateTime.Today, expires);

    /// <summary>Chạy với khoá công khai thử, xong trả lại khoá thật.</summary>
    private static void WithKey(RSA key, Action body)
    {
        CertificateStore.PublicKeyPem = key.ExportSubjectPublicKeyInfoPem();
        try { body(); }
        finally { CertificateStore.ResetPublicKey(); }
    }

    [Fact]
    public void Valid_certificate_gives_school_and_department()
    {
        using var key = RSA.Create(2048);
        WithKey(key, () =>
        {
            var c = CertificateStore.Parse(Issue(key, DateTime.Today.AddDays(30)));
            Assert.Equal("Trường THPT Thử", c.School);
            Assert.Equal("Sở Giáo dục và Đào tạo Thử", c.Department);
            Assert.Equal("IT-00001-01", c.Id);
            Assert.Equal(DateTime.Today.AddDays(30), c.Expires);
            Assert.False(c.Expired);
            Assert.Same(c, CertificateStore.Usable(c));
            Assert.Null(CertificateStore.Parse(Issue(key, null)).Expires);          // không thời hạn
            Assert.False(CertificateStore.Parse(Issue(key, DateTime.Today)).Expired); // ngày cuối vẫn dùng được
        });
    }

    [Fact]
    public void Edited_or_foreign_certificates_are_rejected()
    {
        using var key = RSA.Create(2048);
        using var other = RSA.Create(2048);
        WithKey(key, () =>
        {
            var text = Issue(key, null);
            // Sửa tên trường trong nội dung nhưng giữ chữ ký cũ.
            var o = JsonNode.Parse(text)!.AsObject();
            var data = Encoding.UTF8.GetString(Convert.FromBase64String(o.Str("data"))).Replace("Trường THPT Thử", "Trường THPT Khác");
            o["data"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(data));
            Assert.Contains("chữ ký", Assert.Throws<InvalidDataException>(() => CertificateStore.Parse(o.ToJsonString())).Message);
            // Chứng chỉ do khoá khác ký.
            Assert.Throws<InvalidDataException>(() => CertificateStore.Parse(Issue(other, null)));
            // File rác, file trống, sai định dạng.
            Assert.Throws<InvalidDataException>(() => CertificateStore.Parse(""));
            Assert.Throws<InvalidDataException>(() => CertificateStore.Parse("không phải chứng chỉ"));
            Assert.Throws<InvalidDataException>(() => CertificateStore.Parse("{\"format\":\"khac\",\"data\":\"\",\"sig\":\"\"}"));
            Assert.Throws<InvalidDataException>(() => CertificateStore.Parse(new string('x', 30_000)));
            // Thiếu tên trường.
            Assert.Throws<InvalidDataException>(() => CertificateStore.Parse(Issue(key, null, school: " ")));
        });
    }

    [Fact]
    public void Expired_certificate_is_readable_but_not_usable()
    {
        using var key = RSA.Create(2048);
        WithKey(key, () =>
        {
            var c = CertificateStore.Parse(Issue(key, DateTime.Today.AddDays(-1)));
            Assert.True(c.Expired);
            Assert.Contains("hết hạn", Assert.Throws<InvalidDataException>(() => CertificateStore.Usable(c)).Message);

            var dir = Path.Combine(Path.GetTempPath(), "itest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string good = Path.Combine(dir, "a.itcert"), old = Path.Combine(dir, "b.itcert"), junk = Path.Combine(dir, "c.itcert");
                File.WriteAllText(good, Issue(key, DateTime.Today.AddDays(5)));
                File.WriteAllText(old, Issue(key, DateTime.Today.AddDays(-5)));
                File.WriteAllText(junk, "{}");
                // Mã thoát mà bộ cài đặt dùng (tham số --check-cert).
                Assert.Equal(0, CertificateStore.Check(good));
                Assert.Equal(3, CertificateStore.Check(old));
                Assert.Equal(2, CertificateStore.Check(junk));
                Assert.Equal(2, CertificateStore.Check(Path.Combine(dir, "khong-co.itcert")));
            }
            finally { Directory.Delete(dir, true); }
        });
    }

    [Fact]
    public void Certificate_signed_by_the_website_verifies_with_the_embedded_key()
    {
        // File này do mã PHP của trang web ký bằng khoá bí mật thật (và cố ý đã hết hạn):
        // kiểm tra định dạng và chữ ký của PHP khớp với khoá công khai nhúng trong phần mềm.
        CertificateStore.ResetPublicKey();
        var c = CertificateStore.Load(Path.Combine(AppContext.BaseDirectory, "het-han.itcert"));
        Assert.Equal("Trường THPT Kiểm Thử", c.School);
        Assert.Equal("Sở Giáo dục và Đào tạo Thử Nghiệm", c.Department);
        Assert.True(c.Expired);
        Assert.Throws<InvalidDataException>(() => CertificateStore.Usable(c));
    }
}
