using System.IO;
using System.IO.Compression;
using System.Text.Json;
using ClosedXML.Excel;
using ITest.Core;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace ITest.Server;

/// <summary>Xuất gói kết quả: bảng điểm + phân tích đề (Excel), bài tự luận đánh phách (PDF), biên bản (PDF), bài làm, nhật ký.</summary>
public static class Exporter
{
    public static void ExportZip(RoomService room, string zipPath)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "itest_export_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var cands = room.Candidates.Where(c => c.Sbd.Length > 0).OrderBy(c => c.Sbd).ToList();
            var phach = cands.Select((c, i) => (c, code: $"P{i + 1:000}")).ToList();
            WriteExcel(room, cands, phach, Path.Combine(tmp, "BangDiem_PhanTichDe.xlsx"));
            WriteEssaysPdf(room, phach, Path.Combine(tmp, "BaiTuLuan_DanhPhach.pdf"));
            WriteReportPdf(room, cands, Path.Combine(tmp, "BienBanPhongThi.pdf"));
            File.WriteAllText(Path.Combine(tmp, "BaiLam.json"),
                JsonSerializer.Serialize(cands.Select(c => c.Sheet), new JsonSerializerOptions { WriteIndented = true }));
            var log = Path.Combine(room.BackupDir, "nhat-ky.txt");
            if (File.Exists(log)) File.Copy(log, Path.Combine(tmp, "NhatKy.txt"));
            if (File.Exists(zipPath)) File.Delete(zipPath);
            ZipFile.CreateFromDirectory(tmp, zipPath);
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }
    }

    private static string F(double v) => Math.Round(v, 2).ToString("0.##", System.Globalization.CultureInfo.GetCultureInfo("vi-VN"));

    private static void WriteExcel(RoomService room, List<Candidate> cands, List<(Candidate c, string code)> phach, string path)
    {
        var exam = room.Exam;
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Bảng điểm");
        ws.Cell(1, 1).Value = $"{exam.ExamName} – {exam.Subject} · Năm học {exam.SchoolYear}";
        ws.Cell(2, 1).Value = $"{room.School} · {room.Room} · Giám thị: {room.Proctor}";
        ws.Range(1, 1, 1, 11).Merge().Style.Font.SetBold().Font.SetFontSize(14);
        string[] head = { "STT", "SBD", "Họ và tên", "Ngày sinh", "Lớp", "Máy", "Mã đề", "Đúng", "Sai", "Bỏ trống", "Điểm TN", "Trạng thái", "Ghi chú" };
        for (int i = 0; i < head.Length; i++) ws.Cell(4, i + 1).Value = head[i];
        ws.Range(4, 1, 4, head.Length).Style.Font.SetBold().Fill.SetBackgroundColor(XLColor.FromHtml("#DBEAFE"));
        int r = 5;
        foreach (var c in cands)
        {
            var layout = room.Layouts.FirstOrDefault(l => l.Code == c.Sheet.Code);
            var g = layout != null ? Scoring.GradeMc(exam, layout, c.Sheet) : new Scoring.McResult(0, 0, 0, 0);
            ws.Cell(r, 1).Value = r - 4;
            ws.Cell(r, 2).Value = c.Sbd;
            ws.Cell(r, 3).Value = c.FullName;
            ws.Cell(r, 4).Value = c.BirthDate;
            ws.Cell(r, 5).Value = c.ClassName;
            ws.Cell(r, 6).Value = c.Machine;
            ws.Cell(r, 7).Value = c.Code;
            ws.Cell(r, 8).Value = g.Right;
            ws.Cell(r, 9).Value = g.Wrong;
            ws.Cell(r, 10).Value = g.Blank;
            if (c.Sheet.Revoked) ws.Cell(r, 11).Value = "Huỷ"; else ws.Cell(r, 11).Value = g.Score;
            ws.Cell(r, 12).Value = c.StatusText;
            ws.Cell(r, 13).Value = c.Discipline == "—" ? "" : c.Discipline;
            r++;
        }
        ws.Columns().AdjustToContents();

        var wp = wb.Worksheets.Add("Phách");
        wp.Cell(1, 1).Value = "Số phách"; wp.Cell(1, 2).Value = "SBD"; wp.Cell(1, 3).Value = "Họ và tên";
        wp.Range(1, 1, 1, 3).Style.Font.SetBold();
        int pr = 2;
        foreach (var (c, code) in phach) { wp.Cell(pr, 1).Value = code; wp.Cell(pr, 2).Value = c.Sbd; wp.Cell(pr, 3).Value = c.FullName; pr++; }
        wp.Columns().AdjustToContents();

        var wa = wb.Worksheets.Add("Phân tích đề");
        string[] ah = { "Câu (gốc)", "Nội dung", "Số lượt trả lời", "Tỉ lệ đúng", "Đáp án", "Chọn A", "Chọn B", "Chọn C", "Chọn D", "Phương án nhiễu nổi bật" };
        for (int i = 0; i < ah.Length; i++) wa.Cell(1, i + 1).Value = ah[i];
        wa.Range(1, 1, 1, ah.Length).Style.Font.SetBold().Fill.SetBackgroundColor(XLColor.FromHtml("#DBEAFE"));
        var stats = Scoring.Analyze(exam, room.Layouts, cands.Select(c => c.Sheet)).OrderBy(s => s.CorrectRate).ToList();
        var byId = exam.Questions.ToDictionary(q => q.Id);
        int ar = 2;
        foreach (var s in stats)
        {
            var q = byId[s.QuestionId];
            wa.Cell(ar, 1).Value = s.QuestionId;
            wa.Cell(ar, 2).Value = q.Text.Length > 80 ? q.Text[..80] + "…" : q.Text;
            wa.Cell(ar, 3).Value = s.Answered;
            wa.Cell(ar, 4).Value = Math.Round(s.CorrectRate, 4);
            wa.Cell(ar, 4).Style.NumberFormat.Format = "0%";
            wa.Cell(ar, 5).Value = q.Correct >= 0 ? ((char)('A' + q.Correct)).ToString() : "";
            for (int k = 0; k < Math.Min(4, s.OptionCounts.Length); k++) wa.Cell(ar, 6 + k).Value = s.OptionCounts[k];
            int bestWrong = -1;
            for (int k = 0; k < s.OptionCounts.Length; k++)
                if (k != q.Correct && (bestWrong < 0 || s.OptionCounts[k] > s.OptionCounts[bestWrong])) bestWrong = k;
            if (bestWrong >= 0 && s.Answered > 0 && s.OptionCounts[bestWrong] > 0)
                wa.Cell(ar, 10).Value = $"{(char)('A' + bestWrong)} ({s.OptionCounts[bestWrong] * 100 / s.Answered}%)";
            if (s.CorrectRate < 0.4 && s.Answered > 0) wa.Range(ar, 1, ar, ah.Length).Style.Fill.SetBackgroundColor(XLColor.FromHtml("#FEE2E2"));
            ar++;
        }
        wa.Columns().AdjustToContents();
        wa.Column(2).Width = 60;
        wb.SaveAs(path);
    }

    private static void WriteEssaysPdf(RoomService room, List<(Candidate c, string code)> phach, string path)
    {
        var exam = room.Exam;
        var essays = exam.EssayQuestions.ToList();
        Document.Create(doc =>
        {
            foreach (var (c, code) in phach)
            {
                var layout = room.Layouts.FirstOrDefault(l => l.Code == c.Sheet.Code);
                doc.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(2, Unit.Centimetre);
                    page.DefaultTextStyle(x => x.FontSize(12).FontFamily("Times New Roman"));
                    page.Header().Row(row =>
                    {
                        row.RelativeItem().Text($"{exam.ExamName} – {exam.Subject}").Bold();
                        row.ConstantItem(120).AlignRight().Text($"Số phách: {code}").Bold();
                    });
                    page.Content().PaddingTop(10).Column(col =>
                    {
                        col.Spacing(10);
                        if (essays.Count == 0) col.Item().Text("Bộ đề không có phần tự luận.");
                        int i = 0;
                        foreach (var q in essays)
                        {
                            i++;
                            int no = layout != null ? layout.Order.IndexOf(q.Id) + 1 : i;
                            var ans = c.Sheet.Essays.TryGetValue(no, out var t) ? t : "";
                            col.Item().Text($"Câu {no} ({F(q.Points)} điểm). {q.Text}").Bold();
                            col.Item().Border(0.5f).Padding(8).MinHeight(80).Text(string.IsNullOrWhiteSpace(ans) ? "(bỏ trống)" : ans);
                            col.Item().Text("Điểm: ............   Giám khảo: ......................").FontSize(10);
                        }
                    });
                    page.Footer().AlignCenter().Text(x => { x.Span("Trang "); x.CurrentPageNumber(); x.Span(" / "); x.TotalPages(); });
                });
            }
            if (phach.Count == 0)
                doc.Page(p => { p.Size(PageSizes.A4); p.Margin(2, Unit.Centimetre); p.Content().Text("Không có bài làm."); });
        }).GeneratePdf(path);
    }

    private static void WriteReportPdf(RoomService room, List<Candidate> cands, string path)
    {
        var exam = room.Exam;
        var violations = cands.Where(c => c.WarnedAt != null || c.RevokedAt != null).ToList();
        Document.Create(doc => doc.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(2, Unit.Centimetre);
            page.DefaultTextStyle(x => x.FontSize(12).FontFamily("Times New Roman"));
            page.Content().Column(col =>
            {
                col.Spacing(6);
                col.Item().Row(row =>
                {
                    row.RelativeItem().Column(l =>
                    {
                        l.Item().AlignCenter().Text(room.Department.ToUpperInvariant());
                        l.Item().AlignCenter().Text(room.School.ToUpperInvariant()).Bold();
                    });
                    row.RelativeItem().Column(r =>
                    {
                        r.Item().AlignCenter().Text("CỘNG HOÀ XÃ HỘI CHỦ NGHĨA VIỆT NAM").Bold();
                        r.Item().AlignCenter().Text("Độc lập – Tự do – Hạnh phúc").Bold();
                    });
                });
                col.Item().PaddingTop(16).AlignCenter().Text("BIÊN BẢN PHÒNG THI").FontSize(16).Bold();
                col.Item().AlignCenter().Text($"{exam.ExamName} – Môn {exam.Subject} – Năm học {exam.SchoolYear}");
                col.Item().PaddingTop(10).Text($"Phòng thi: {room.Room}        Giám thị: {room.Proctor}");
                col.Item().Text($"Bộ đề: {Path.GetFileName(room.PackagePath)} · {exam.Minutes} phút · {exam.Questions.Count} câu · {exam.Codes} mã đề");
                col.Item().Text($"Giờ bắt đầu: {room.StartedAt:HH:mm dd/MM/yyyy}        Giờ kết thúc: {room.EndedAt:HH:mm dd/MM/yyyy}");
                int present = cands.Count(c => c.Status != CandStatus.Connected);
                col.Item().Text($"Số thí sinh có mặt: {present}        Số bài đã thu: {cands.Count(c => c.Status == CandStatus.Done)}        Số vi phạm: {violations.Count}");
                col.Item().PaddingTop(10).Text("Danh sách thí sinh vi phạm").Bold();
                col.Item().Table(t =>
                {
                    t.ColumnsDefinition(cd => { cd.ConstantColumn(30); cd.ConstantColumn(70); cd.RelativeColumn(2); cd.ConstantColumn(60); cd.RelativeColumn(2); });
                    t.Header(h =>
                    {
                        foreach (var s in new[] { "STT", "SBD", "Họ và tên", "Máy", "Hình thức xử lý" })
                            h.Cell().Border(0.5f).Background(Colors.Grey.Lighten3).Padding(4).Text(s).Bold();
                    });
                    int i = 1;
                    foreach (var c in violations)
                    {
                        t.Cell().Border(0.5f).Padding(4).Text((i++).ToString());
                        t.Cell().Border(0.5f).Padding(4).Text(c.Sbd);
                        t.Cell().Border(0.5f).Padding(4).Text(c.FullName);
                        t.Cell().Border(0.5f).Padding(4).Text(c.Machine);
                        t.Cell().Border(0.5f).Padding(4).Text(c.RevokedAt != null ? $"Thu bài, huỷ kết quả ({c.RevokedAt})" : $"Nhắc nhở ({c.WarnedAt})");
                    }
                    if (violations.Count == 0) { t.Cell().ColumnSpan(5).Border(0.5f).Padding(4).Text("Không có thí sinh vi phạm."); }
                });
                col.Item().PaddingTop(30).Row(row =>
                {
                    foreach (var who in new[] { "GIÁM THỊ 1", "GIÁM THỊ 2", "TRƯỞNG ĐIỂM THI" })
                        row.RelativeItem().Column(s =>
                        {
                            s.Item().AlignCenter().Text(who).Bold();
                            s.Item().AlignCenter().Text("(Ký, ghi rõ họ tên)").Italic().FontSize(10);
                        });
                });
            });
        })).GeneratePdf(path);
    }
}
