using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using ITest.Core;
using W = DocumentFormat.OpenXml.Wordprocessing;
using A = DocumentFormat.OpenXml.Drawing;

namespace ITest.Editor;

/// <summary>Câu hỏi đọc được từ file Word/Excel.</summary>
public sealed class ImportedQuestion
{
    public QuestionType Type { get; set; }
    public string Text { get; set; } = "";
    public List<string> Options { get; } = new();
    public int Correct { get; set; } = -1;
    public List<int> Corrects { get; } = new();
    public List<bool?> TfKey { get; } = new();
    public string ShortKey { get; set; } = "";
    public double? Points { get; set; }
    public byte[]? Image { get; set; }
    public string ImageExt { get; set; } = ".png";
}

/// <summary>
/// Nhập thông minh:
///  • Word: dòng bắt đầu "Câu n." / "Câu n:" là câu hỏi; "A." "B." "C." "D." là phương án (có thể nhiều phương án trên một dòng);
///    đáp án đúng được gạch chân, tô đỏ hoặc có dấu * phía trước; dòng có "TỰ LUẬN" chuyển sang phần tự luận; ảnh trong đoạn được giữ lại.
///  • Excel: hàng 1 là tiêu đề; các cột: Loại | Nội dung | A | B | C | D | Đáp án | Điểm.
///    Loại: TN (1 đáp án) · NDA (nhiều đáp án, Đáp án "A,C") · DS (Đúng/Sai, cột A–D là các ý, Đáp án "Đ,S,Đ,S")
///          · TLN (trả lời ngắn, Đáp án là giá trị đúng) · TL (tự luận, Đáp án là hướng dẫn chấm).
/// </summary>
public static class Importer
{
    private static readonly Regex QStart = new(@"^\s*Câu\s*(\d+)\s*[\.:\)]?\s*(.*)$", RegexOptions.IgnoreCase);
    private static readonly Regex OptSplit = new(@"(?<![A-Za-zÀ-ỹ])\*?([A-D])\s*[\.\)]\s*", RegexOptions.Compiled);

    public static List<ImportedQuestion> FromWord(string path)
    {
        var list = new List<ImportedQuestion>();
        using var doc = WordprocessingDocument.Open(path, false);
        var main = doc.MainDocumentPart;
        var body = main?.Document?.Body;
        if (main == null || body == null) return list;
        bool essay = false;
        ImportedQuestion? cur = null;

        foreach (var p in body.Descendants<W.Paragraph>())
        {
            // Ghép văn bản, đánh dấu ký tự nào được gạch chân / tô đỏ
            var sb = new StringBuilder();
            var marked = new List<bool>();
            foreach (var run in p.Descendants<W.Run>())
            {
                var rp = run.RunProperties;
                bool underlined = rp?.Underline != null && (rp.Underline.Val == null || !string.Equals(rp.Underline.Val.InnerText, "none", StringComparison.OrdinalIgnoreCase));
                var col = rp?.Color?.Val?.Value ?? "";
                bool red = col.Equals("FF0000", StringComparison.OrdinalIgnoreCase) || col.Equals("C00000", StringComparison.OrdinalIgnoreCase) || col.Equals("EE0000", StringComparison.OrdinalIgnoreCase);
                bool m = underlined || red;
                foreach (var t in run.Elements<W.Text>()) { sb.Append(t.Text); for (int i = 0; i < t.Text.Length; i++) marked.Add(m); }
                foreach (var _ in run.Elements<W.TabChar>()) { sb.Append('\t'); marked.Add(false); }
            }
            var text = sb.ToString();
            var blip = p.Descendants<A.Blip>().FirstOrDefault();
            if (blip?.Embed?.Value is string rid && cur != null && main.GetPartById(rid) is ImagePart ip)
            {
                using var s = ip.GetStream();
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                cur.Image = ms.ToArray();
                cur.ImageExt = ip.ContentType.Contains("jpeg") ? ".jpg" : ".png";
            }
            if (string.IsNullOrWhiteSpace(text)) continue;
            var upper = text.Trim().ToUpperInvariant();
            if (upper.Contains("TỰ LUẬN") && !QStart.IsMatch(text)) { essay = true; continue; }
            if (upper.Contains("TRẮC NGHIỆM") && !QStart.IsMatch(text)) { essay = false; continue; }

            var qm = QStart.Match(text);
            if (qm.Success)
            {
                cur = new ImportedQuestion { Type = essay ? QuestionType.Essay : QuestionType.MultipleChoice };
                list.Add(cur);
                var rest = qm.Groups[2].Value;
                var pts = Regex.Match(rest, @"^\(\s*([\d,\.]+)\s*điểm\s*\)\s*", RegexOptions.IgnoreCase);
                if (pts.Success)
                {
                    if (double.TryParse(pts.Groups[1].Value.Replace(',', '.'), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var pv)) cur.Points = pv;
                    rest = rest[pts.Length..];
                }
                int offset = qm.Groups[2].Index + (pts.Success ? pts.Length : 0);
                ParseOptionsInto(cur, rest, marked, offset, appendStemIfNoOption: true);
                continue;
            }
            if (cur == null) continue;
            if (cur.Type == QuestionType.MultipleChoice && OptSplit.IsMatch(text))
                ParseOptionsInto(cur, text, marked, 0, appendStemIfNoOption: false);
            else if (cur.Options.Count == 0)
                cur.Text = (cur.Text + "\n" + text.Trim()).Trim();
        }
        return list;
    }

    private static void ParseOptionsInto(ImportedQuestion q, string text, List<bool> marked, int offset, bool appendStemIfNoOption)
    {
        var ms = OptSplit.Matches(text).Cast<Match>().Where(m => m.Index == 0 || char.IsWhiteSpace(text[m.Index - 1])).ToList();
        if (ms.Count == 0 || q.Type == QuestionType.Essay)
        {
            if (appendStemIfNoOption || q.Options.Count == 0) q.Text = (q.Text + " " + text.Trim()).Trim();
            return;
        }
        var stem = text[..ms[0].Index].Trim();
        if (stem.Length > 0) q.Text = (q.Text + " " + stem).Trim();
        for (int i = 0; i < ms.Count; i++)
        {
            int start = ms[i].Index + ms[i].Length;
            int end = i + 1 < ms.Count ? ms[i + 1].Index : text.Length;
            var opt = text[start..end].Trim().TrimEnd(';').Trim();
            bool star = ms[i].Value.TrimStart().StartsWith('*');
            bool under = false;
            for (int k = start; k < end && offset + k < marked.Count; k++) if (marked[offset + k] && !char.IsWhiteSpace(text[k])) { under = true; break; }
            if (ms[i].Index + offset < marked.Count && marked[ms[i].Index + offset]) under = true;
            if (star || under) q.Correct = q.Options.Count;
            q.Options.Add(opt);
        }
    }

    public static List<ImportedQuestion> FromExcel(string path)
    {
        var list = new List<ImportedQuestion>();
        using var wb = new XLWorkbook(path);
        var ws = wb.Worksheets.First();
        var used = ws.RangeUsed();
        if (used == null) return list;
        foreach (var row in used.RowsUsed().Skip(1))
        {
            string C(int i) => row.Cell(i).GetFormattedString().Trim();
            var type = C(1).ToUpperInvariant().Replace(" ", "");
            var q = new ImportedQuestion { Type = ParseType(type), Text = C(2) };
            if (q.Text.Length == 0) continue;
            var ans = C(7);
            switch (q.Type)
            {
                case QuestionType.MultipleChoice:
                    for (int i = 3; i <= 6; i++) q.Options.Add(C(i));
                    var a1 = ans.ToUpperInvariant();
                    if (a1.Length == 1 && a1[0] >= 'A' && a1[0] <= 'D') q.Correct = a1[0] - 'A';
                    break;
                case QuestionType.MultiSelect:
                    for (int i = 3; i <= 6; i++) q.Options.Add(C(i));
                    foreach (var ch in ans.ToUpperInvariant()) if (ch >= 'A' && ch <= 'D' && !q.Corrects.Contains(ch - 'A')) q.Corrects.Add(ch - 'A');
                    break;
                case QuestionType.TrueFalse:
                    for (int i = 3; i <= 6; i++) q.Options.Add(C(i));
                    var parts = ans.Split(new[] { ',', ';', ' ', '-' }, StringSplitOptions.RemoveEmptyEntries);
                    for (int i = 0; i < 4; i++)
                    {
                        var s = i < parts.Length ? parts[i].Trim().ToUpperInvariant() : "";
                        q.TfKey.Add(s is "Đ" or "D" or "ĐÚNG" or "DUNG" or "T" or "TRUE" or "1" ? true : s is "S" or "SAI" or "F" or "FALSE" or "0" ? false : null);
                    }
                    break;
                case QuestionType.ShortAnswer:
                    q.ShortKey = ans;
                    break;
                case QuestionType.Essay:
                    q.ShortKey = ans; // dùng làm hướng dẫn chấm
                    break;
            }
            if (double.TryParse(C(8).Replace(',', '.'), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var pv)) q.Points = pv;
            list.Add(q);
        }
        return list;
    }

    private static QuestionType ParseType(string t) => t switch
    {
        "NDA" or "NHIEU" or "NHIỀU" or "NHIEUDAPAN" or "NHIỀUĐÁPÁN" => QuestionType.MultiSelect,
        "DS" or "ĐS" or "DUNGSAI" or "ĐÚNGSAI" or "Đ/S" or "D/S" => QuestionType.TrueFalse,
        "TLN" or "TRALOINGAN" or "TRẢLỜINGẮN" => QuestionType.ShortAnswer,
        _ when t.StartsWith("TL") || t.Contains("TỰ") => QuestionType.Essay,
        _ => QuestionType.MultipleChoice
    };

    /// <summary>Tạo file Excel mẫu để giáo viên điền câu hỏi.</summary>
    public static void WriteExcelTemplate(string path)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Câu hỏi");
        string[] h = { "Loại (TN/NDA/DS/TL)", "Nội dung", "A", "B", "C", "D", "Đáp án", "Điểm" };
        for (int i = 0; i < h.Length; i++) ws.Cell(1, i + 1).Value = h[i];
        ws.Range(1, 1, 1, h.Length).Style.Font.SetBold().Fill.SetBackgroundColor(XLColor.FromHtml("#CCFBF1"));
        ws.Cell(2, 1).Value = "TN"; ws.Cell(2, 2).Value = "Tập xác định của hàm số y = √(x − 2) là";
        ws.Cell(2, 3).Value = "[2; +∞)"; ws.Cell(2, 4).Value = "(2; +∞)"; ws.Cell(2, 5).Value = "ℝ"; ws.Cell(2, 6).Value = "(−∞; 2]"; ws.Cell(2, 7).Value = "A"; ws.Cell(2, 8).Value = 0.25;
        ws.Cell(3, 1).Value = "NDA"; ws.Cell(3, 2).Value = "Những số nào sau đây là số nguyên tố?";
        ws.Cell(3, 3).Value = "2"; ws.Cell(3, 4).Value = "9"; ws.Cell(3, 5).Value = "11"; ws.Cell(3, 6).Value = "15"; ws.Cell(3, 7).Value = "A,C"; ws.Cell(3, 8).Value = 0.5;
        ws.Cell(4, 1).Value = "DS"; ws.Cell(4, 2).Value = "Cho hàm số $y = x^2 - 4x + 3$. Xét tính đúng sai của các mệnh đề:";
        ws.Cell(4, 3).Value = "Đồ thị cắt trục tung tại điểm (0; 3)"; ws.Cell(4, 4).Value = "Hàm số đồng biến trên ℝ";
        ws.Cell(4, 5).Value = "Đỉnh parabol là I(2; −1)"; ws.Cell(4, 6).Value = "Phương trình y = 0 vô nghiệm"; ws.Cell(4, 7).Value = "Đ,S,Đ,S"; ws.Cell(4, 8).Value = 1;
        // (Loại TLN — trả lời ngắn — vẫn được nhận khi nhập, nhưng không còn trong mẫu.)
        ws.Cell(5, 1).Value = "TL"; ws.Cell(5, 2).Value = "Giải phương trình $x^2 - 5x + 6 = 0$."; ws.Cell(5, 7).Value = "Δ = 1; x = 2 hoặc x = 3"; ws.Cell(5, 8).Value = 1;
        ws.Columns().AdjustToContents();
        ws.Column(2).Width = 60;
        wb.SaveAs(path);
    }
}
