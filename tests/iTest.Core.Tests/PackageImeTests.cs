using System.Security.Cryptography;

namespace ITest.Core.Tests;

public class PackageTests
{
    private static ExamSet Exam() => new()
    {
        ExamName = "Kiểm tra giữa kỳ", Subject = "Toán", Minutes = 45, Codes = 4,
        Questions =
        {
            new Question { Id = 1, Type = QuestionType.MultipleChoice, Text = "Tính $\\frac{1}{2}$", Options = new() { "1", "2", "3", "4" }, Correct = 2, Points = 5 },
            new Question { Id = 2, Type = QuestionType.TrueFalse, Text = "Đúng sai", Options = new() { "a", "b", "c", "d" }, TfKey = new() { true, false, null, true }, Points = 5, Image = "img1.png" }
        }
    };

    [Fact]
    public void Roundtrip_with_password()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".itz");
        try
        {
            var media = new Dictionary<string, byte[]> { ["img1.png"] = new byte[] { 1, 2, 3, 4, 5 } };
            ExamPackage.Save(path, Exam(), media, "mật-khẩu-123");
            Assert.True(ExamPackage.IsPasswordProtected(path));
            var c = ExamPackage.Load(path, "mật-khẩu-123");
            Assert.Equal("Kiểm tra giữa kỳ", c.Exam.ExamName);
            Assert.Equal(QuestionType.TrueFalse, c.Exam.Questions[1].Type);
            Assert.Equal(new bool?[] { true, false, null, true }, c.Exam.Questions[1].TfKey);
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, c.Media["img1.png"]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Wrong_password_or_tampering_is_rejected()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".itz");
        try
        {
            ExamPackage.Save(path, Exam(), new Dictionary<string, byte[]>(), "abc123");
            Assert.ThrowsAny<CryptographicException>(() => ExamPackage.Load(path, "sai"));
            var bytes = File.ReadAllBytes(path);
            bytes[^5] ^= 0xFF;
            File.WriteAllBytes(path, bytes);
            Assert.ThrowsAny<CryptographicException>(() => ExamPackage.Load(path, "abc123"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void No_password_package_opens_without_password()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".itz");
        try
        {
            ExamPackage.Save(path, Exam(), new Dictionary<string, byte[]>(), null);
            Assert.False(ExamPackage.IsPasswordProtected(path));
            Assert.Equal(2, ExamPackage.Load(path, null).Exam.Questions.Count);
        }
        finally { File.Delete(path); }
    }
}

public class ImeTests
{
    private static string Type(string keys, ImeMode mode)
    {
        var word = "";
        foreach (var k in keys) word = VietIme.ApplyKey(word, k, mode) ?? word + k;
        return word;
    }

    [Theory]
    [InlineData("tieengs", "tiếng")]
    [InlineData("vieetj", "việt")]
    [InlineData("ddi", "đi")]
    [InlineData("toans", "toán")]
    [InlineData("hocj", "học")]
    [InlineData("muwa", "mưa")]
    public void Telex(string keys, string expected) => Assert.Equal(expected, Type(keys, ImeMode.Telex));

    [Theory]
    [InlineData("tie6ng1", "tiếng")]
    [InlineData("d9i", "đi")]
    [InlineData("toan1", "toán")]
    public void Vni(string keys, string expected) => Assert.Equal(expected, Type(keys, ImeMode.Vni));

    [Fact]
    public void Double_tone_key_undoes_tone() => Assert.Equal("toans", Type("toanss", ImeMode.Telex));
}
