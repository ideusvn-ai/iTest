using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using ITest.Core;
using ITest.Editor;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace ITest.Windows.Tests;

/// <summary>Nhập câu hỏi từ file Word thật (tạo bằng Open XML SDK) qua đúng mã của iTest-Editor.</summary>
public class ImportWordTests
{
    // PNG 1×1 hợp lệ.
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    private static W.Run Text(string text, bool underline = false)
    {
        var run = new W.Run();
        if (underline) run.Append(new W.RunProperties(new W.Underline { Val = W.UnderlineValues.Single }));
        run.Append(new W.Text(text) { Space = SpaceProcessingModeValues.Preserve });
        return run;
    }

    private static W.Run Picture(string relationshipId) => new(new W.Drawing(
        new DW.Inline(
            new DW.Extent { Cx = 990000L, Cy = 792000L },
            new DW.DocProperties { Id = 1U, Name = "Hinh 1" },
            new A.Graphic(
                new A.GraphicData(
                    new PIC.Picture(
                        new PIC.NonVisualPictureProperties(
                            new PIC.NonVisualDrawingProperties { Id = 0U, Name = "hinh.png" },
                            new PIC.NonVisualPictureDrawingProperties()),
                        new PIC.BlipFill(new A.Blip { Embed = relationshipId }, new A.Stretch(new A.FillRectangle())),
                        new PIC.ShapeProperties()))
                { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" }))));

    /// <summary>Tạo file .docx; <paramref name="build"/> nhận hàm thêm đoạn và mã liên kết của ảnh đã nhúng.</summary>
    private static string Docx(Action<Action<OpenXmlElement[]>, string> build)
    {
        var path = Path.Combine(Path.GetTempPath(), "itest_" + Guid.NewGuid().ToString("N") + ".docx");
        using (var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            var body = new W.Body();
            main.Document = new W.Document(body);
            var image = main.AddImagePart(ImagePartType.Png);
            using (var ms = new MemoryStream(Png)) image.FeedData(ms);
            build(children => body.Append(new W.Paragraph(children)), main.GetIdOfPart(image));
            main.Document.Save();
        }
        return path;
    }

    private static List<ImportedQuestion> Import(Action<Action<OpenXmlElement[]>, string> build)
    {
        var path = Docx(build);
        try { return Importer.FromWord(path); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Picture_in_the_question_paragraph_belongs_to_that_question()
    {
        var qs = Import((p, img) =>
        {
            p(new OpenXmlElement[] { Text("Câu 1. Thủ đô của Việt Nam là") });
            p(new OpenXmlElement[] { Text("A. Huế   "), Text("B. Hà Nội", underline: true), Text("   C. Đà Nẵng   D. Cần Thơ") });
            // Ảnh nằm ngay trong đoạn mở đầu Câu 2: phải thuộc Câu 2 (trước đây bị gán sang Câu 1).
            p(new OpenXmlElement[] { Text("Câu 2. Quan sát hình và cho biết đây là hình gì?"), Picture(img) });
            p(new OpenXmlElement[] { Text("A. Hình vuông") });
            p(new OpenXmlElement[] { Text("*B. Hình tròn") });
        });
        Assert.Equal(2, qs.Count);
        Assert.Null(qs[0].Image);
        Assert.Equal(new[] { "Huế", "Hà Nội", "Đà Nẵng", "Cần Thơ" }, qs[0].Options);
        Assert.Equal(1, qs[0].Correct);
        Assert.NotNull(qs[1].Image);
        Assert.Equal(Png, qs[1].Image);
        Assert.Equal(".png", qs[1].ImageExt);
        Assert.Equal("Quan sát hình và cho biết đây là hình gì?", qs[1].Text);
        // Câu chỉ có hai phương án giữ nguyên hai phương án.
        Assert.Equal(new[] { "Hình vuông", "Hình tròn" }, qs[1].Options);
        Assert.Equal(1, qs[1].Correct);
        Assert.Equal(QuestionType.MultipleChoice, qs[1].Type);
    }

    [Fact]
    public void Picture_in_a_following_paragraph_belongs_to_the_current_question()
    {
        var qs = Import((p, img) =>
        {
            p(new OpenXmlElement[] { Text("Câu 1. Cho hình vẽ sau.") });
            p(new OpenXmlElement[] { Picture(img) });
            p(new OpenXmlElement[] { Text("A. Một   B. Hai   *C. Ba   D. Bốn") });
            p(new OpenXmlElement[] { Text("Câu 2. Câu không có hình.") });
            p(new OpenXmlElement[] { Text("A. Có   *B. Không") });
        });
        Assert.Equal(2, qs.Count);
        Assert.NotNull(qs[0].Image);
        Assert.Null(qs[1].Image);
        Assert.Equal(2, qs[0].Correct);
    }

    [Fact]
    public void Wrapped_options_and_points_note_are_read_correctly()
    {
        var qs = Import((p, _) =>
        {
            // Dấu chấm sau phần ghi điểm không được lọt vào nội dung câu.
            p(new OpenXmlElement[] { Text("Câu 1 (0,5 điểm). Phát biểu nào sau đây đúng?") });
            p(new OpenXmlElement[] { Text("A. Phương án thứ nhất rất dài") });
            p(new OpenXmlElement[] { Text("nên phải viết tiếp ở dòng dưới.") });           // đoạn viết tiếp của phương án A
            p(new OpenXmlElement[] { Text("*B. Phương án hai") });
            // Các phương án cách nhau bằng ngắt dòng mềm (Shift+Enter) trong cùng một đoạn.
            p(new OpenXmlElement[] { Text("C. Phương án ba"), new W.Run(new W.Break()), Text("D. Phương án bốn") });
            p(new OpenXmlElement[] { Text("--- HẾT ---") });
            p(new OpenXmlElement[] { Text("PHẦN II. TỰ LUẬN") });
            p(new OpenXmlElement[] { Text("Câu 2: (2 điểm) Trình bày lời giải.") });
            p(new OpenXmlElement[] { Text("Gợi ý: vẽ hình trước.") });
        });
        Assert.Equal(2, qs.Count);
        Assert.Equal("Phát biểu nào sau đây đúng?", qs[0].Text);
        Assert.Equal(0.5, qs[0].Points);
        Assert.Equal(4, qs[0].Options.Count);
        Assert.Equal("Phương án thứ nhất rất dài\nnên phải viết tiếp ở dòng dưới.", qs[0].Options[0]);
        Assert.Equal("Phương án hai", qs[0].Options[1]);
        Assert.Equal("Phương án ba", qs[0].Options[2]);
        Assert.Equal("Phương án bốn", qs[0].Options[3]);                             // không dính "--- HẾT ---"
        Assert.Equal(1, qs[0].Correct);
        Assert.Equal(QuestionType.Essay, qs[1].Type);
        Assert.Equal(2.0, qs[1].Points);
        Assert.Equal("Trình bày lời giải.\nGợi ý: vẽ hình trước.", qs[1].Text);
    }

    [Fact]
    public void Question_types_are_recognised()
    {
        var qs = Import((p, _) =>
        {
            p(new OpenXmlElement[] { Text("Câu 1. Chọn các số nguyên tố.") });
            p(new OpenXmlElement[] { Text("*A. 2   B. 9   *C. 11   D. 15") });
            p(new OpenXmlElement[] { Text("Câu 2. Số 0 là số tự nhiên.") });
            p(new OpenXmlElement[] { Text("*A. Đúng   B. Sai") });
            p(new OpenXmlElement[] { Text("Câu 3. Xét tính đúng sai của các mệnh đề sau.") });
            p(new OpenXmlElement[] { Text("*a) 2 là số chẵn") });
            p(new OpenXmlElement[] { Text("b) 3 là số chẵn") });
            p(new OpenXmlElement[] { Text("*c) 5 là số lẻ") });
        });
        Assert.Equal(3, qs.Count);
        Assert.Equal(QuestionType.MultiSelect, qs[0].Type);
        Assert.Equal(new[] { 0, 2 }, qs[0].Corrects);
        Assert.Equal(QuestionType.YesNo, qs[1].Type);
        Assert.Equal(0, qs[1].Correct);
        Assert.Equal(QuestionType.TrueFalse, qs[2].Type);
        Assert.Equal(3, qs[2].Options.Count);                                         // 3 ý giữ nguyên 3 ý
        Assert.Equal(new bool?[] { true, false, true }, qs[2].TfKey);
    }
}
