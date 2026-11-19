using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClosedXML.Excel;
using ITest.Core;
using Microsoft.Win32;

namespace ITest.Server;

/// <summary>
/// Xuất điểm để nhập vào phần mềm quản lý trường (SMAS, vnEdu… hoặc phần mềm khác nhập điểm từ Excel):
///  • Cách 1: tạo file Excel điểm theo lớp (STT, Mã HS/SBD, Họ và tên, Ngày sinh, Lớp, Điểm).
///  • Cách 2: điền điểm vào chính file mẫu tải từ phần mềm quản lý (giữ nguyên định dạng), khớp theo mã học sinh
///    hoặc họ tên + ngày sinh, rồi báo những dòng không khớp.
/// Chỉ xuất điểm phần khách quan (máy chấm); điểm tự luận được giáo viên chấm và nhập sau.
/// </summary>
public static class SchoolExport
{
    public sealed record Row(string Sbd, string Name, string Birth, string Class, double? Score, string Note);

    public static List<Row> Rows(RoomService room, int decimals)
    {
        var list = new List<Row>();
        foreach (var c in room.Candidates.Where(c => c.Sbd.Length > 0))
        {
            var layout = room.Layouts.FirstOrDefault(l => l.Code == c.Sheet.Code);
            double? score = layout == null ? null : Math.Round(Scoring.GradeObjective(room.Exam, layout, c.Sheet).Score, decimals, MidpointRounding.AwayFromZero);
            string note = "";
            if (c.Sheet.Revoked) { score = null; note = "Đình chỉ · huỷ kết quả"; }
            else if (c.Status != CandStatus.Done) note = "Chưa nộp bài";
            list.Add(new Row(c.Sbd, c.FullName, c.BirthDate, c.ClassName, score, note));
        }
        return list.OrderBy(r => r.Class).ThenBy(r => LastName(r.Name)).ThenBy(r => r.Name).ToList();
    }

    private static string LastName(string full) => Norm(full.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "");

    /// <summary>Chuẩn hoá tên để so khớp: bỏ dấu, chữ thường, gộp khoảng trắng.</summary>
    public static string Norm(string s)
    {
        var d = (s ?? "").Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var ch in d)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(ch == 'đ' || ch == 'Đ' ? 'd' : char.ToLowerInvariant(ch));
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string NormDate(IXLCell cell)
    {
        try
        {
            if (cell.DataType == XLDataType.DateTime) return cell.GetDateTime().ToString("dd/MM/yyyy");
        }
        catch { }
        return NormDate(cell.GetFormattedString());
    }

    private static string NormDate(string s)
    {
        s = s.Trim();
        string[] fmts = { "d/M/yyyy", "dd/MM/yyyy", "d-M-yyyy", "dd-MM-yyyy", "yyyy-MM-dd", "d.M.yyyy" };
        return DateTime.TryParseExact(s, fmts, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.ToString("dd/MM/yyyy") : s;
    }

    // ---------------- Cách 1: file điểm theo lớp ----------------
    public static void WriteByClass(RoomService room, string path, int decimals)
    {
        var rows = Rows(room, decimals);
        using var wb = new XLWorkbook();
        foreach (var g in rows.GroupBy(r => string.IsNullOrWhiteSpace(r.Class) ? "Chưa rõ lớp" : r.Class))
        {
            var name = new string(g.Key.Where(ch => !"[]*?/\\:".Contains(ch)).ToArray());
            var ws = wb.Worksheets.Add(name.Length > 31 ? name[..31] : name.Length == 0 ? "Lop" : name);
            string[] head = { "STT", "Mã HS / SBD", "Họ và tên", "Ngày sinh", "Lớp", $"Điểm {room.Exam.Subject}", "Ghi chú" };
            for (int i = 0; i < head.Length; i++) ws.Cell(1, i + 1).Value = head[i];
            ws.Range(1, 1, 1, head.Length).Style.Font.SetBold().Fill.SetBackgroundColor(XLColor.FromHtml("#DBEAFE"));
            int r = 2;
            foreach (var x in g)
            {
                ws.Cell(r, 1).Value = r - 1;
                ws.Cell(r, 2).Value = x.Sbd;
                ws.Cell(r, 3).Value = x.Name;
                ws.Cell(r, 4).Value = x.Birth;
                ws.Cell(r, 5).Value = x.Class;
                if (x.Score is double s) ws.Cell(r, 6).Value = s;
                ws.Cell(r, 7).Value = x.Note;
                r++;
            }
            ws.Column(6).Style.NumberFormat.Format = decimals == 1 ? "0.0" : "0.00";
            ws.Columns().AdjustToContents();
        }
        if (wb.Worksheets.Count == 0) wb.Worksheets.Add("Diem");
        wb.SaveAs(path);
    }

    // ---------------- Cách 2: điền vào file mẫu ----------------
    public sealed class TemplateInfo
    {
        public string SheetName { get; init; } = "";
        public int HeaderRow { get; init; }
        public int NameCol { get; init; }
        public int BirthCol { get; init; }
        public int CodeCol { get; init; }
        public List<(int col, string text)> Headers { get; init; } = new();
    }

    /// <summary>Tìm dòng tiêu đề có cột "Họ và tên" trong 30 dòng đầu của từng sheet.</summary>
    public static TemplateInfo? Analyze(string path)
    {
        using var wb = new XLWorkbook(path);
        foreach (var ws in wb.Worksheets)
        {
            var used = ws.RangeUsed();
            if (used == null) continue;
            int lastCol = used.LastColumn().ColumnNumber();
            for (int r = 1; r <= Math.Min(30, used.LastRow().RowNumber()); r++)
            {
                var heads = new List<(int, string)>();
                int name = 0, birth = 0, code = 0;
                for (int c = 1; c <= lastCol; c++)
                {
                    var text = ws.Cell(r, c).GetFormattedString().Trim();
                    if (text.Length == 0) continue;
                    heads.Add((c, text));
                    var n = Norm(text);
                    if (name == 0 && (n.Contains("ho va ten") || n.Contains("ho ten") || n == "ten hoc sinh" || n.Contains("ho, ten"))) name = c;
                    else if (birth == 0 && n.Contains("ngay sinh")) birth = c;
                    else if (code == 0 && (n.Contains("ma hoc sinh") || n.Contains("ma hs") || n == "ma" || n.Contains("so bao danh") || n == "sbd" || n.Contains("ma dinh danh"))) code = c;
                }
                if (name > 0) return new TemplateInfo { SheetName = ws.Name, HeaderRow = r, NameCol = name, BirthCol = birth, CodeCol = code, Headers = heads };
            }
        }
        return null;
    }

    public sealed record FillReport(int Matched, List<string> NotFoundInFile, int RowsWithoutScore);

    public static FillReport Fill(RoomService room, string templatePath, string outPath, TemplateInfo info, int scoreCol, int decimals)
    {
        var rows = Rows(room, decimals);
        var used = new HashSet<Row>();
        int matched = 0, blank = 0;
        using var wb = new XLWorkbook(templatePath);
        var ws = wb.Worksheet(info.SheetName);
        int last = ws.LastRowUsed()?.RowNumber() ?? info.HeaderRow;
        for (int r = info.HeaderRow + 1; r <= last; r++)
        {
            var name = Norm(ws.Cell(r, info.NameCol).GetFormattedString());
            if (name.Length == 0) continue;
            var code = info.CodeCol > 0 ? ws.Cell(r, info.CodeCol).GetFormattedString().Trim() : "";
            var birth = info.BirthCol > 0 ? NormDate(ws.Cell(r, info.BirthCol)) : "";
            Row? hit = null;
            if (code.Length > 0) hit = rows.FirstOrDefault(x => !used.Contains(x) && string.Equals(x.Sbd.Trim(), code, StringComparison.OrdinalIgnoreCase));
            hit ??= rows.FirstOrDefault(x => !used.Contains(x) && Norm(x.Name) == name && (birth.Length == 0 || NormDate(x.Birth) == birth));
            if (hit == null) { blank++; continue; }
            used.Add(hit);
            if (hit.Score is double s) { ws.Cell(r, scoreCol).Value = s; matched++; }
            else blank++;
        }
        wb.SaveAs(outPath);
        var missing = rows.Where(x => !used.Contains(x)).Select(x => $"{x.Sbd} · {x.Name} · {x.Birth}").ToList();
        return new FillReport(matched, missing, blank);
    }

    // ---------------- Hộp thoại ----------------
    public static void Run(Window owner, RoomService room)
    {
        var dlg = new Window
        {
            Owner = owner, Title = "Xuất điểm cho phần mềm quản lý trường", Width = 600, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Style = (Style)owner.FindResource("DialogWindow")
        };
        var muted = (Brush)owner.FindResource("Muted");
        var p = new StackPanel { Margin = new Thickness(24) };
        p.Children.Add(new TextBlock { Text = "Xuất điểm cho phần mềm quản lý trường", FontSize = 20, FontWeight = FontWeights.Bold });
        p.Children.Add(new TextBlock
        {
            Text = "Dùng cho SMAS, vnEdu hoặc phần mềm khác có chức năng nhập điểm từ Excel. Chỉ xuất điểm phần trắc nghiệm do máy chấm"
                 + (room.Exam.EssayQuestions.Any() ? "; điểm tự luận cần giáo viên chấm và cộng sau." : "."),
            TextWrapping = TextWrapping.Wrap, Foreground = muted, Margin = new Thickness(0, 4, 0, 14)
        });
        var byClass = new RadioButton { Content = "Tạo file Excel điểm theo lớp (mỗi lớp một sheet)", IsChecked = true, Margin = new Thickness(0, 4, 0, 4) };
        var tpl = new RadioButton { Content = "Điền điểm vào file mẫu tải từ phần mềm quản lý trường…", Margin = new Thickness(0, 4, 0, 4) };
        p.Children.Add(byClass); p.Children.Add(tpl);
        p.Children.Add(new TextBlock
        {
            Text = "Với file mẫu: máy tìm cột “Họ và tên”, “Ngày sinh”, “Mã học sinh/SBD”, khớp từng học sinh rồi ghi điểm vào cột bạn chọn. Định dạng file mẫu được giữ nguyên để nhập lại vào phần mềm.",
            TextWrapping = TextWrapping.Wrap, Foreground = muted, FontSize = 12.5, Margin = new Thickness(24, 0, 0, 10)
        });
        var round = new ComboBox { Width = 260, HorizontalAlignment = HorizontalAlignment.Left };
        round.Items.Add(new ComboBoxItem { Content = "Làm tròn 1 chữ số thập phân (7,3)", Tag = 1 });
        round.Items.Add(new ComboBoxItem { Content = "Giữ 2 chữ số thập phân (7,25)", Tag = 2, IsSelected = true });
        p.Children.Add(new TextBlock { Text = "Làm tròn điểm", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 4) });
        p.Children.Add(round);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var cancel = new Button { Content = "Huỷ", IsCancel = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
        var ok = new Button { Content = "Tiếp tục…", IsDefault = true, MinWidth = 130, Style = (Style)owner.FindResource("BtnPrimary") };
        ok.Click += (_, _) => dlg.DialogResult = true;
        buttons.Children.Add(cancel); buttons.Children.Add(ok);
        p.Children.Add(buttons);
        dlg.Content = p;
        if (dlg.ShowDialog() != true) return;
        int dec = (round.SelectedItem as ComboBoxItem)?.Tag is int d ? d : 2;
        var safeRoom = string.Concat(room.Room.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_'));

        try
        {
            if (byClass.IsChecked == true)
            {
                var save = new SaveFileDialog { Filter = "Excel (*.xlsx)|*.xlsx", FileName = $"Diem_{room.Exam.Subject}_{safeRoom}.xlsx" };
                if (save.ShowDialog(owner) != true) return;
                WriteByClass(room, save.FileName, dec);
                room.Log("XUẤT FILE", "", $"Đã xuất điểm theo lớp · {System.IO.Path.GetFileName(save.FileName)}");
                MessageBox.Show(owner, "Đã xuất file điểm:\n" + save.FileName, "Xuất điểm", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var open = new OpenFileDialog { Filter = "Excel (*.xlsx)|*.xlsx", Title = "Chọn file mẫu nhập điểm tải từ phần mềm quản lý trường" };
            if (open.ShowDialog(owner) != true) return;
            var info = Analyze(open.FileName);
            if (info == null)
            {
                MessageBox.Show(owner, "Không tìm thấy dòng tiêu đề có cột “Họ và tên” trong file mẫu.", "Xuất điểm", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var col = PickColumn(owner, info, room.Exam.Subject);
            if (col == null) return;
            var outDlg = new SaveFileDialog
            {
                Filter = "Excel (*.xlsx)|*.xlsx",
                FileName = System.IO.Path.GetFileNameWithoutExtension(open.FileName) + "_DaDienDiem.xlsx"
            };
            if (outDlg.ShowDialog(owner) != true) return;
            var rep = Fill(room, open.FileName, outDlg.FileName, info, col.Value, dec);
            room.Log("XUẤT FILE", "", $"Đã điền {rep.Matched} điểm vào file mẫu · {System.IO.Path.GetFileName(outDlg.FileName)}");
            var msg = $"Đã điền điểm cho {rep.Matched} học sinh vào sheet “{info.SheetName}”.\n";
            if (rep.RowsWithoutScore > 0) msg += $"\n{rep.RowsWithoutScore} dòng trong file mẫu không có điểm (học sinh không thi trong phòng này, chưa nộp hoặc bị huỷ kết quả).";
            if (rep.NotFoundInFile.Count > 0)
                msg += $"\n\n{rep.NotFoundInFile.Count} thí sinh không tìm thấy trong file mẫu — kiểm tra lại họ tên, ngày sinh hoặc mã học sinh:\n• "
                       + string.Join("\n• ", rep.NotFoundInFile.Take(15)) + (rep.NotFoundInFile.Count > 15 ? "\n…" : "");
            MessageBox.Show(owner, msg, "Xuất điểm", MessageBoxButton.OK, rep.NotFoundInFile.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, "Không xuất được điểm: " + ex.Message + "\n\nNếu file mẫu đang mở trong Excel, hãy đóng lại rồi thử lại.", "Xuất điểm", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static int? PickColumn(Window owner, TemplateInfo info, string subject)
    {
        var dlg = new Window
        {
            Owner = owner, Title = "Chọn cột ghi điểm", Width = 520, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Style = (Style)owner.FindResource("DialogWindow")
        };
        var p = new StackPanel { Margin = new Thickness(24) };
        p.Children.Add(new TextBlock { Text = "Chọn cột ghi điểm", FontSize = 18, FontWeight = FontWeights.Bold });
        p.Children.Add(new TextBlock
        {
            Text = $"Sheet “{info.SheetName}”, dòng tiêu đề {info.HeaderRow}. Cột họ tên: {Col(info.NameCol)}" +
                   (info.BirthCol > 0 ? $", ngày sinh: {Col(info.BirthCol)}" : "") + (info.CodeCol > 0 ? $", mã học sinh: {Col(info.CodeCol)}" : "") + ".",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 12)
        });
        var box = new ComboBox();
        var sub = Norm(subject);
        foreach (var (c, text) in info.Headers.Where(h => h.col != info.NameCol && h.col != info.BirthCol && h.col != info.CodeCol))
        {
            var item = new ComboBoxItem { Content = $"Cột {Col(c)} · {text}", Tag = c };
            box.Items.Add(item);
            var n = Norm(text);
            if (box.SelectedItem == null && (n.Contains("diem") || n.Contains("ktgk") || n.Contains("ktck") || n.Contains("gk") || n.Contains("ck") || (sub.Length > 0 && n.Contains(sub))))
                box.SelectedItem = item;
        }
        if (box.SelectedItem == null && box.Items.Count > 0) box.SelectedIndex = 0;
        p.Children.Add(box);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var cancel = new Button { Content = "Huỷ", IsCancel = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
        var ok = new Button { Content = "Điền điểm", IsDefault = true, MinWidth = 120, Style = (Style)owner.FindResource("BtnPrimary") };
        ok.Click += (_, _) => dlg.DialogResult = true;
        buttons.Children.Add(cancel); buttons.Children.Add(ok);
        p.Children.Add(buttons);
        dlg.Content = p;
        return dlg.ShowDialog() == true && box.SelectedItem is ComboBoxItem { Tag: int col } ? col : null;
    }

    private static string Col(int c)
    {
        var s = "";
        while (c > 0) { int m = (c - 1) % 26; s = (char)('A' + m) + s; c = (c - 1) / 26; }
        return s;
    }
}
