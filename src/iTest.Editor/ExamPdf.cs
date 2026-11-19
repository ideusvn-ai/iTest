using System.Globalization;
using System.IO;
using ITest.Core;
using ITest.UI;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace ITest.Editor;

public sealed class PdfOptions
{
    public string Department { get; set; } = "";
    public string School { get; set; } = "";
    /// <summary>Danh sách mã đề cần in (rỗng = chỉ đề gốc 101).</summary>
    public List<string> Codes { get; set; } = new();
    public bool AnswerKey { get; set; } = true;
}

/// <summary>Xuất bộ câu hỏi thành đề thi in giấy theo bố cục đề kiểm tra thông dụng, kèm đáp án từng mã đề.</summary>
public static class ExamPdf
{
    private const float Fs = 12.5f;
    private static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");
    private static string F(double v) => Math.Round(v, 2).ToString("0.##", Vi);

    /// <summary>Phải gọi trên luồng giao diện (công thức được vẽ bằng WPF trước khi dựng PDF).</summary>
    public static void Export(string path, ExamSet exam, string mediaFolder, PdfOptions o)
    {
        var layouts = Shuffler.Build(exam);
        var chosen = o.Codes.Count == 0 ? layouts.Take(1).ToList() : layouts.Where(l => o.Codes.Contains(l.Code)).ToList();
        if (chosen.Count == 0) chosen = layouts.Take(1).ToList();

        // Vẽ trước mọi công thức thành ảnh.
        var formulas = new Dictionary<string, (byte[] png, double w, double h)?>();
        void Collect(string? s)
        {
            if (string.IsNullOrEmpty(s)) return;
            foreach (var seg in RichMath.Split(s))
                if (seg.IsMath && !formulas.ContainsKey(seg.Text)) formulas[seg.Text] = RichMath.RenderPng(seg.Text, Fs);
        }
        foreach (var q in exam.Questions) { Collect(q.Text); q.Options.ForEach(Collect); Collect(q.Guide); }

        var byId = exam.Questions.ToDictionary(q => q.Id);
        var doc = Document.Create(container =>
        {
            foreach (var layout in chosen)
                container.Page(page => PaperPage(page, exam, layout, byId, mediaFolder, formulas, o));
            if (o.AnswerKey)
                container.Page(page => KeyPage(page, exam, chosen, byId, formulas, o));
        });
        doc.GeneratePdf(path);
    }

    private static void Setup(PageDescriptor page)
    {
        page.Size(PageSizes.A4);
        page.MarginVertical(1.4f, Unit.Centimetre);
        page.MarginLeft(2f, Unit.Centimetre);
        page.MarginRight(1.5f, Unit.Centimetre);
        page.DefaultTextStyle(x => x.FontSize(Fs).FontFamily("Times New Roman").LineHeight(1.25f));
    }

    private static void Rich(TextDescriptor t, string? text, Dictionary<string, (byte[] png, double w, double h)?> formulas)
    {
        if (string.IsNullOrEmpty(text)) return;
        foreach (var seg in RichMath.Split(text))
        {
            if (!seg.IsMath) { t.Span(seg.Text); continue; }
            if (formulas.TryGetValue(seg.Text, out var f) && f is { } img)
            {
                // Ảnh công thức cao hơn dòng chữ thì hạ xuống để tâm công thức nằm giữa dòng.
                float shift = (float)Math.Max(0, (img.h - Fs * 0.75) / 2);
                t.Element().TranslateY(shift).Width((float)img.w).Height((float)img.h).Image(img.png);
            }
            else t.Span(seg.Text).Italic();
        }
    }

    private static int PlainLength(string s) => RichMath.Split(s).Sum(x => x.IsMath ? x.Text.Length / 2 + 2 : x.Text.Length);

    private static void PaperPage(PageDescriptor page, ExamSet exam, CodeLayout layout, Dictionary<int, Question> byId, string mediaFolder,
                                  Dictionary<string, (byte[] png, double w, double h)?> formulas, PdfOptions o)
    {
        Setup(page);
        var sec = "ma" + layout.Code;
        page.Footer().AlignCenter().Text(t =>
        {
            t.DefaultTextStyle(x => x.FontSize(10).Italic());
            t.Span("Trang ");
            t.PageNumberWithinSection(sec);
            t.Span("/");
            t.TotalPagesWithinSection(sec);
            t.Span($" – Mã đề {layout.Code}");
        });

        page.Content().Section(sec).Column(col =>
        {
            col.Spacing(4);
            // ---- Tiêu đề ----
            col.Item().Row(r =>
            {
                r.RelativeItem(4).Column(c =>
                {
                    if (!string.IsNullOrWhiteSpace(o.Department)) c.Item().AlignCenter().Text(o.Department.ToUpper(Vi));
                    c.Item().AlignCenter().Text(string.IsNullOrWhiteSpace(o.School) ? exam.Unit.ToUpper(Vi) : o.School.ToUpper(Vi)).Bold();
                    c.Item().AlignCenter().Width(90).PaddingTop(2).LineHorizontal(0.8f);
                    c.Item().PaddingTop(4).AlignCenter().Text(t =>
                    {
                        t.DefaultTextStyle(x => x.Italic().FontSize(11));
                        t.Span("(Đề có ");
                        t.TotalPagesWithinSection(sec);
                        t.Span(" trang)");
                    });
                });
                r.RelativeItem(6).Column(c =>
                {
                    c.Item().AlignCenter().Text((string.IsNullOrWhiteSpace(exam.ExamName) ? "ĐỀ KIỂM TRA" : exam.ExamName).ToUpper(Vi)).Bold();
                    if (!string.IsNullOrWhiteSpace(exam.SchoolYear)) c.Item().AlignCenter().Text("NĂM HỌC " + exam.SchoolYear).Bold();
                    c.Item().AlignCenter().Text("Môn: " + exam.Subject).Bold();
                    c.Item().AlignCenter().Text($"Thời gian làm bài: {exam.Minutes} phút, không kể thời gian phát đề").Italic().FontSize(11.5f);
                });
            });
            col.Item().PaddingTop(10).Row(r =>
            {
                r.RelativeItem().AlignBottom().Text("Họ, tên thí sinh: ……………………………………………   Số báo danh: ………………");
                r.ConstantItem(92).Border(1).PaddingVertical(4).AlignCenter().Text("Mã đề " + layout.Code).Bold();
            });

            // ---- Nội dung ----
            var qs = layout.Order.Select(id => byId[id]).ToList();
            var obj = qs.Where(q => q.Type.IsObjective()).ToList();
            var es = qs.Where(q => !q.Type.IsObjective()).ToList();
            int no = 1;
            bool two = obj.Count > 0 && es.Count > 0;
            if (obj.Count > 0)
            {
                col.Item().PaddingTop(10).Text(t =>
                {
                    t.Span(two ? "PHẦN I. TRẮC NGHIỆM " : "TRẮC NGHIỆM ").Bold();
                    t.Span($"({F(obj.Sum(q => q.Points))} điểm)").Bold();
                });
                if (obj.Any(q => q.Type == QuestionType.TrueFalse))
                    col.Item().Text("Với câu Đúng/Sai: trong mỗi ý a), b), c), d), thí sinh chọn Đúng hoặc Sai.").Italic().FontSize(11.5f);
                foreach (var q in obj) Item(col, q, no++, layout, mediaFolder, formulas, false);
            }
            if (es.Count > 0)
            {
                col.Item().PaddingTop(10).Text(t =>
                {
                    t.Span(two ? "PHẦN II. TỰ LUẬN " : "TỰ LUẬN ").Bold();
                    t.Span($"({F(es.Sum(q => q.Points))} điểm)").Bold();
                });
                foreach (var q in es) Item(col, q, no++, layout, mediaFolder, formulas, true);
            }

            col.Item().PaddingTop(16).AlignCenter().Text("------------- HẾT -------------").Bold();
            col.Item().PaddingTop(4).Text(t =>
            {
                t.DefaultTextStyle(x => x.Italic().FontSize(11));
                if (!exam.AllowDocuments) t.Line("- Thí sinh không được sử dụng tài liệu;");
                t.Span("- Cán bộ coi thi không giải thích gì thêm.");
            });
        });
    }

    private static void Item(ColumnDescriptor col, Question q, int no, CodeLayout layout, string mediaFolder,
                             Dictionary<string, (byte[] png, double w, double h)?> formulas, bool essay)
    {
        col.Item().PaddingTop(6).Text(t =>
        {
            t.Justify();
            t.Span(essay ? $"Câu {no} ({F(q.Points)} điểm). " : $"Câu {no}. ").Bold();
            Rich(t, q.Text, formulas);
            if (q.Type == QuestionType.MultiSelect) t.Span("  (Chọn tất cả phương án đúng)").Italic().FontSize(11);
        });
        if (q.Image != null)
        {
            var p = Path.Combine(mediaFolder, q.Image);
            if (File.Exists(p)) col.Item().PaddingVertical(4).AlignCenter().MaxHeight(190).MaxWidth(360).Image(p).FitArea();
        }
        if (q.Type.HasOptions())
        {
            var perm = layout.PermOf(q);
            var opts = perm.Select(i => q.Options[i]).ToList();
            bool tf = q.Type == QuestionType.TrueFalse;
            string Label(int k) => tf ? $"{(char)('a' + k)}) " : $"{(char)('A' + k)}. ";
            int longest = opts.Count == 0 ? 0 : opts.Max(PlainLength);
            int perRow = tf ? 1 : longest <= 20 ? 4 : longest <= 42 ? 2 : 1;
            for (int start = 0; start < opts.Count; start += perRow)
            {
                int s0 = start;
                col.Item().PaddingLeft(14).Row(r =>
                {
                    for (int k = s0; k < Math.Min(opts.Count, s0 + perRow); k++)
                    {
                        int kk = k;
                        r.RelativeItem().Text(t =>
                        {
                            t.Span(Label(kk)).Bold();
                            Rich(t, opts[kk], formulas);
                        });
                    }
                    for (int pad = opts.Count - s0; pad < perRow; pad++) r.RelativeItem();
                });
            }
        }
        else if (q.Type == QuestionType.ShortAnswer)
        {
            col.Item().PaddingLeft(14).Text("Trả lời: ……………………………………");
        }
    }

    private static void KeyPage(PageDescriptor page, ExamSet exam, List<CodeLayout> layouts, Dictionary<int, Question> byId,
                                Dictionary<string, (byte[] png, double w, double h)?> formulas, PdfOptions o)
    {
        Setup(page);
        page.Footer().AlignCenter().Text(t => { t.DefaultTextStyle(x => x.FontSize(10).Italic()); t.Span("Đáp án – Trang "); t.PageNumberWithinSection("key"); });
        page.Content().Section("key").Column(col =>
        {
            col.Spacing(4);
            col.Item().AlignCenter().Text("ĐÁP ÁN VÀ HƯỚNG DẪN CHẤM").Bold().FontSize(15);
            col.Item().AlignCenter().Text($"{exam.ExamName} · Môn {exam.Subject}" + (string.IsNullOrWhiteSpace(exam.SchoolYear) ? "" : $" · Năm học {exam.SchoolYear}")).Italic();
            col.Item().PaddingTop(2).AlignCenter().Text("(Tài liệu dành cho giáo viên – không phát cho thí sinh)").Italic().FontSize(11);

            foreach (var layout in layouts)
            {
                var qs = layout.Order.Select(id => byId[id]).ToList();
                col.Item().PaddingTop(14).Text("MÃ ĐỀ " + layout.Code).Bold().FontSize(13.5f);
                var obj = qs.Select((q, i) => (q, no: i + 1)).Where(x => x.q.Type.IsObjective()).ToList();
                if (obj.Count > 0)
                {
                    col.Item().Table(tb =>
                    {
                        tb.ColumnsDefinition(c => { c.ConstantColumn(44); c.ConstantColumn(118); c.RelativeColumn(); c.ConstantColumn(54); });
                        static IContainer Cell(IContainer c) => c.Border(0.6f).BorderColor(Colors.Grey.Medium).PaddingVertical(3).PaddingHorizontal(5);
                        tb.Header(h =>
                        {
                            foreach (var s in new[] { "Câu", "Loại", "Đáp án", "Điểm" })
                                h.Cell().Element(Cell).Background(Colors.Grey.Lighten3).AlignCenter().Text(s).Bold().FontSize(11);
                        });
                        foreach (var (q, no) in obj)
                        {
                            tb.Cell().Element(Cell).AlignCenter().Text(no.ToString()).FontSize(11);
                            tb.Cell().Element(Cell).Text(q.Type.Label()).FontSize(10.5f);
                            tb.Cell().Element(Cell).Text(t => { t.DefaultTextStyle(x => x.FontSize(11).Bold()); Rich(t, KeyText(q, layout), formulas); });
                            tb.Cell().Element(Cell).AlignCenter().Text(F(q.Points)).FontSize(11);
                        }
                    });
                }
                foreach (var (q, i) in qs.Select((q, i) => (q, i)).Where(x => !x.q.Type.IsObjective()))
                {
                    col.Item().PaddingTop(6).Text(t =>
                    {
                        t.Span($"Câu {i + 1} ({F(q.Points)} điểm). ").Bold();
                        if (string.IsNullOrWhiteSpace(q.Guide)) t.Span("(chưa có hướng dẫn chấm)").Italic();
                        else Rich(t, q.Guide, formulas);
                    });
                }
            }
            if (exam.Questions.Any(q => q.Type == QuestionType.TrueFalse))
                col.Item().PaddingTop(12).Text("Câu Đúng/Sai (4 ý): đúng 1 ý được 10%, 2 ý 25%, 3 ý 50%, cả 4 ý 100% số điểm của câu.").Italic().FontSize(11);
        });
    }

    /// <summary>Đáp án của câu theo thứ tự phương án đã trộn của mã đề.</summary>
    public static string KeyText(Question q, CodeLayout layout)
    {
        var perm = layout.PermOf(q);
        switch (q.Type)
        {
            case QuestionType.MultipleChoice:
                int k = Array.IndexOf(perm, q.Correct);
                return k < 0 ? "?" : ((char)('A' + k)).ToString();
            case QuestionType.MultiSelect:
                var ks = Enumerable.Range(0, perm.Length).Where(i => q.Corrects.Contains(perm[i])).Select(i => ((char)('A' + i)).ToString());
                return string.Join(", ", ks);
            case QuestionType.TrueFalse:
                return string.Join("   ", Enumerable.Range(0, perm.Length).Select(i =>
                    $"{(char)('a' + i)}) " + (perm[i] < q.TfKey.Count ? q.TfKey[perm[i]] switch { true => "Đ", false => "S", _ => "?" } : "?")));
            case QuestionType.ShortAnswer:
                return q.ShortKey.Replace(";", "  hoặc ");
            default:
                return "";
        }
    }
}
