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

    private static Dictionary<string, byte[]> Media() => new() { ["img1.png"] = new byte[] { 1, 2, 3, 4, 5 } };

    private static void Throws(Action<ExamSet, Dictionary<string, byte[]>> change, string expected)
    {
        var x = Exam(); var m = Media();
        change(x, m);
        var ex = Assert.Throws<InvalidDataException>(() => ExamPackage.Validate(x, m));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void Validate_accepts_a_good_exam_and_rejects_broken_structure()
    {
        ExamPackage.Validate(Exam(), Media());
        Throws((x, _) => x.Questions.Clear(), "không có câu hỏi");
        Throws((x, _) => x.Questions[1].Id = 1, "mã câu trùng");
        Throws((x, _) => x.Questions[0].Id = 0, "mã câu trùng hoặc không hợp lệ");
        Throws((x, _) => x.Questions[0].Points = double.NaN, "điểm không hợp lệ");
        Throws((x, _) => x.Questions[0].Points = -1, "điểm không hợp lệ");
        Throws((x, _) => x.Questions[0].Correct = 4, "đáp án ngoài phạm vi");
        Throws((x, _) => x.Questions[0].Options = new() { "chỉ một" }, "ít nhất 2 phương án");
        Throws((x, _) => x.Questions[1].TfKey = new() { true }, "không khớp số ý");
        Throws((x, _) => x.Minutes = 0, "thời gian");
        Throws((x, _) => x.Codes = 99, "số mã đề");
        Throws((x, m) => m.Remove("img1.png"), "ảnh không có trong bộ đề");
        Throws((x, m) => m["..\\evil.png"] = new byte[] { 1 }, "tên file");
        Throws((x, m) => m["sub/dir.png"] = new byte[] { 1 }, "tên file");
        Throws((x, m) => m["run.exe"] = new byte[] { 1 }, "tên file");
        Throws((x, m) => m["empty.png"] = Array.Empty<byte>(), "rỗng");
        Throws((x, m) => m["big.mp3"] = new byte[ExamPackage.MaxMediaFileBytes + 1], "lớn hơn");
    }

    [Fact]
    public void Validate_repairs_missing_lists_from_json()
    {
        var x = Exam();
        x.Questions.Add(new Question { Id = 3, Type = QuestionType.Essay, Text = "TL", Points = 0, Options = null!, Corrects = null!, TfKey = null!, Guide = null! });
        ExamPackage.Validate(x, Media());
        Assert.NotNull(x.Questions[2].Options);
        Assert.NotNull(x.Questions[2].Corrects);
    }

    [Fact]
    public void Save_refuses_an_invalid_exam_and_keeps_the_existing_file()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".itz");
        try
        {
            ExamPackage.Save(path, Exam(), Media(), null);
            var before = File.ReadAllBytes(path);
            var bad = Exam(); bad.Questions[1].Id = 1;
            Assert.Throws<InvalidDataException>(() => ExamPackage.Save(path, bad, Media(), null));
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".*.tmp"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Fingerprint_tells_apart_exams_with_the_same_shape()
    {
        var a = Exam(); var b = Exam();
        Assert.Equal(ExamPackage.Fingerprint(a, Media()), ExamPackage.Fingerprint(b, Media()));
        b.CreatedAt = a.CreatedAt.AddDays(3);                         // đóng gói lại cùng nội dung: dấu không đổi
        Assert.Equal(ExamPackage.Fingerprint(a, Media()), ExamPackage.Fingerprint(b, Media()));
        b.Questions[0].Options[3] = "5";                              // cùng số câu, khác một phương án
        Assert.NotEqual(ExamPackage.Fingerprint(a, Media()), ExamPackage.Fingerprint(b, Media()));
        var c = Exam(); c.Questions[0].Correct = 1;                   // khác đáp án
        Assert.NotEqual(ExamPackage.Fingerprint(a, Media()), ExamPackage.Fingerprint(c, Media()));
        var m2 = Media(); m2["img1.png"] = new byte[] { 9, 9, 9 };    // khác ảnh
        Assert.NotEqual(ExamPackage.Fingerprint(a, Media()), ExamPackage.Fingerprint(a, m2));
        Assert.Equal(64, ExamPackage.Fingerprint(a, Media()).Length);
    }

    [Fact]
    public void Loaded_package_carries_the_same_fingerprint()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".itz");
        try
        {
            ExamPackage.Save(path, Exam(), Media(), null);
            Assert.Equal(ExamPackage.Fingerprint(Exam(), Media()), ExamPackage.Load(path, null).Fingerprint);
        }
        finally { File.Delete(path); }
    }

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
            ExamPackage.Save(path, Exam(), Media(), "abc123");
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
            ExamPackage.Save(path, Exam(), Media(), null);
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
