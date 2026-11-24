using System.IO.Compression;
using ClosedXML.Excel;
using ITest.Core;
using ITest.Server;

namespace ITest.Windows.Tests;

/// <summary>Xuất điểm và gói kết quả của iTest-Server, chạy trên bản dữ liệu riêng (không cần mở phòng thi).</summary>
public class ExportTests
{
    private static ExamSet Exam() => new()
    {
        ExamName = "Kiểm tra", Subject = "Toán", SchoolYear = "2026-2027", Minutes = 45, Codes = 1,
        Questions =
        {
            new Question { Id = 1, Type = QuestionType.MultipleChoice, Text = "Câu một", Options = new() { "a", "b", "c", "d" }, Correct = 0, Points = 5 },
            new Question { Id = 2, Type = QuestionType.MultipleChoice, Text = "Câu hai", Options = new() { "a", "b", "c", "d" }, Correct = 1, Points = 3 },
            new Question { Id = 3, Type = QuestionType.Essay, Text = "Giải phương trình $x^2 = 4$.", Options = new(), Points = 2 }
        }
    };

    /// <param name="right">Số câu trắc nghiệm làm đúng (0–2).</param>
    private static ExportCand Cand(string sbd, string name, string birth, string cls, int right, CandStatus status = CandStatus.Done, bool revoked = false)
    {
        var sheet = new AnswerSheet { Code = "101", Revoked = revoked, Candidate = new CandidateInfo { Sbd = sbd, FullName = name, BirthDate = birth, ClassName = cls } };
        sheet.Choices[1] = right >= 1 ? 0 : 3;
        sheet.Choices[2] = right >= 2 ? 1 : 3;
        sheet.Essays[3] = "x = 2 hoặc x = -2";
        return new ExportCand { Sheet = sheet, Status = status, StatusText = status.ToString(), Machine = "PC-" + sbd };
    }

    private static ExportData Data(string backupDir, params ExportCand[] cands)
    {
        var exam = Exam();
        return new ExportData
        {
            Exam = exam, Layouts = Shuffler.Build(exam), School = "THPT Thử", Room = "Phòng 1", Proctor = "Giám thị", Department = "Sở GD",
            PackagePath = "de.itz", BackupDir = backupDir, StartedAt = DateTime.Now.AddHours(-1), EndedAt = DateTime.Now, Candidates = cands.ToList()
        };
    }

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "itest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Only_submitted_and_valid_papers_get_a_score()
    {
        var rows = SchoolExport.Rows(Data("",
            Cand("001", "Nguyễn Văn An", "01/02/2009", "10A1", right: 2),
            Cand("002", "Trần Thị Bình", "03/04/2009", "10A1", right: 2, status: CandStatus.Lost),      // chưa nộp
            Cand("003", "Lê Văn Cường", "05/06/2009", "10A1", right: 2, status: CandStatus.Revoked, revoked: true),
            Cand("004", "Phạm Thị Dung", "07/08/2009", "10A1", right: 1)), 2);
        Assert.Equal(8.0, rows.Single(r => r.Sbd == "001").Score);
        Assert.Equal(5.0, rows.Single(r => r.Sbd == "004").Score);
        Assert.Null(rows.Single(r => r.Sbd == "002").Score);
        Assert.Contains("Chưa nộp", rows.Single(r => r.Sbd == "002").Note);
        Assert.Null(rows.Single(r => r.Sbd == "003").Score);
        Assert.Contains("huỷ", rows.Single(r => r.Sbd == "003").Note);
    }

    [Fact]
    public void Sheet_names_are_valid_and_never_collide()
    {
        var used = new HashSet<string>();
        Assert.Equal("10A1", SchoolExport.UniqueSheetName("10A1", used));
        Assert.Equal("10A1 (2)", SchoolExport.UniqueSheetName("10A/1", used));       // bỏ ký tự cấm thì trùng tên lớp khác
        Assert.Equal("10a1 (3)", SchoolExport.UniqueSheetName("10a1", used));        // Excel không phân biệt hoa/thường
        Assert.Equal("Lop", SchoolExport.UniqueSheetName("[]:*?", used));
        var long1 = SchoolExport.UniqueSheetName(new string('L', 40) + "1", used);
        var long2 = SchoolExport.UniqueSheetName(new string('L', 40) + "2", used);
        Assert.Equal(31, long1.Length);
        Assert.True(long2.Length <= 31);
        Assert.NotEqual(long1, long2, StringComparer.OrdinalIgnoreCase);

        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "diem.xlsx");
            SchoolExport.WriteByClass(Data("",
                Cand("001", "Nguyễn Văn An", "01/02/2009", "10A1", 2),
                Cand("002", "Trần Thị Bình", "03/04/2009", " 10a1 ", 2),                // cùng lớp, gõ khác kiểu: gộp một sheet
                Cand("003", "Lê Văn Cường", "05/06/2009", "10A/1", 1),                  // lớp khác, tên sheet sau khi bỏ "/" bị trùng
                Cand("004", "Phạm Thị Dung", "07/08/2009", "", 1),
                Cand("005", "Hoàng Văn Em", "09/10/2009", new string('K', 40) + "1", 0),
                Cand("006", "Vũ Thị Giang", "11/12/2009", new string('K', 40) + "2", 0, status: CandStatus.Lost)), path, 2);
            using var wb = new XLWorkbook(path);
            Assert.Equal(5, wb.Worksheets.Count);
            Assert.Equal(5, wb.Worksheets.Select(ws => ws.Name.ToUpperInvariant()).Distinct().Count());
            Assert.Contains(wb.Worksheets, ws => ws.Name == "Chưa rõ lớp");
            // Sheet duy nhất có 2 học sinh là lớp 10A1 (gộp "10A1" và " 10a1 "); lớp "10A/1" nằm ở sheet khác.
            var a1 = wb.Worksheets.Single(ws => ws.LastRowUsed()!.RowNumber() == 3);
            Assert.Equal("001", a1.Cell(2, 2).GetString());
            Assert.Equal("002", a1.Cell(3, 2).GetString());
            Assert.Equal(8.0, a1.Cell(2, 6).GetDouble());
            // Bài chưa nộp: ô điểm để trống, có ghi chú.
            var lost = wb.Worksheets.Single(ws => ws.Cell(2, 2).GetString() == "006");
            Assert.True(lost.Cell(2, 6).Value.IsBlank);
            Assert.Contains("Chưa nộp", lost.Cell(2, 7).GetString());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Template_fill_never_guesses_between_students_with_the_same_name()
    {
        var dir = TempDir();
        try
        {
            var tpl = Path.Combine(dir, "mau.xlsx");
            using (var wb = new XLWorkbook())
            {
                var ws = wb.Worksheets.Add("Lop10A1");
                string[] head = { "STT", "Mã học sinh", "Họ và tên", "Ngày sinh", "Điểm KTGK" };
                for (int i = 0; i < head.Length; i++) ws.Cell(1, i + 1).Value = head[i];
                (string code, string name, string birth)[] rows =
                {
                    ("HS01", "Nguyễn Văn An", "01/02/2009"),     // dòng 2: khớp theo mã + tên
                    ("", "Trần Thị Bình", "03/04/2009"),         // dòng 3, 4: trùng tên, phân biệt bằng ngày sinh
                    ("", "Trần Thị Bình", "05/06/2009"),
                    ("", "Lê Văn Cường", ""),                    // dòng 5, 6: trùng tên, không có ngày sinh → không điền
                    ("", "Lê Văn Cường", ""),
                    ("HS09", "Phạm Thị Dung", "07/08/2009"),     // dòng 7: mã trùng SBD của người khác → không ghép nhầm
                    ("", "Vũ Thị Giang", "11/12/2009")           // dòng 8: có trong phòng nhưng chưa nộp → để trống
                };
                for (int i = 0; i < rows.Length; i++)
                {
                    ws.Cell(i + 2, 1).Value = i + 1;
                    ws.Cell(i + 2, 2).Value = rows[i].code;
                    ws.Cell(i + 2, 3).Value = rows[i].name;
                    ws.Cell(i + 2, 4).Value = rows[i].birth;
                }
                wb.SaveAs(tpl);
            }
            var data = Data("",
                Cand("HS01", "Nguyễn Văn An", "01/02/2009", "10A1", 2),
                Cand("B1", "Trần Thị Bình", "03/04/2009", "10A1", 2),
                Cand("B2", "Trần Thị Bình", "05/06/2009", "10A1", 1),
                Cand("C1", "Lê Văn Cường", "09/09/2009", "10A1", 2),
                Cand("HS09", "Hoàng Văn Em", "09/10/2009", "10A1", 2),
                Cand("G1", "Vũ Thị Giang", "11/12/2009", "10A1", 2, status: CandStatus.Doing));
            var info = SchoolExport.Analyze(tpl);
            Assert.NotNull(info);
            Assert.Equal((3, 4, 2), (info!.NameCol, info.BirthCol, info.CodeCol));
            var outPath = Path.Combine(dir, "ra.xlsx");
            var rep = SchoolExport.Fill(data, tpl, outPath, info, scoreCol: 5, decimals: 2);

            Assert.Equal(3, rep.Matched);
            Assert.Equal(2, rep.Ambiguous.Count);
            Assert.Single(rep.NotFoundInFile);
            Assert.Contains("Hoàng Văn Em", rep.NotFoundInFile[0]);
            Assert.Equal(4, rep.RowsWithoutScore);
            using var res = new XLWorkbook(outPath);
            var sheet = res.Worksheet("Lop10A1");
            Assert.Equal(8.0, sheet.Cell(2, 5).GetDouble());
            Assert.Equal(8.0, sheet.Cell(3, 5).GetDouble());   // Bình sinh 03/04
            Assert.Equal(5.0, sheet.Cell(4, 5).GetDouble());   // Bình sinh 05/06
            for (int r = 5; r <= 8; r++) Assert.True(sheet.Cell(r, 5).Value.IsBlank, $"Dòng {r} phải để trống điểm.");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Result_zip_is_complete_and_replaces_the_old_one_only_when_finished()
    {
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
        var dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "nhat-ky.txt"), "2026-11-24 08:00:00\tTHÔNG TIN\t\tMở phòng\n");
            var zip = Path.Combine(dir, "KetQua.zip");
            File.WriteAllText(zip, "gói cũ");

            // Dữ liệu hỏng (hai câu trùng mã) làm việc xuất thất bại: gói cũ phải còn nguyên, không sót file tạm.
            var broken = Data(dir, Cand("001", "Nguyễn Văn An", "01/02/2009", "10A1", 2));
            broken.Exam.Questions[1].Id = 1;
            Assert.ThrowsAny<Exception>(() => Exporter.ExportZip(broken, zip));
            Assert.Equal("gói cũ", File.ReadAllText(zip));
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));

            var data = Data(dir,
                Cand("002", "Trần Thị Bình", "03/04/2009", "10A1", 1),
                Cand("001", "Nguyễn Văn An", "01/02/2009", "10A1", 2),
                Cand("003", "Lê Văn Cường", "05/06/2009", "10A2", 2, status: CandStatus.Lost));
            Exporter.ExportZip(data, zip);
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
            using var z = ZipFile.OpenRead(zip);
            var names = z.Entries.Select(e => e.FullName).OrderBy(n => n).ToArray();
            Assert.Equal(new[] { "BaiLam.json", "BaiTuLuan_DanhPhach.pdf", "BangDiem_PhanTichDe.xlsx", "BienBanPhongThi.pdf", "NhatKy.txt" }, names);
            Assert.All(z.Entries, e => Assert.True(e.Length > 0, e.FullName + " rỗng"));

            // Bảng điểm: bài chưa nộp không có điểm.
            var xlsx = Path.Combine(dir, "bang.xlsx");
            z.GetEntry("BangDiem_PhanTichDe.xlsx")!.ExtractToFile(xlsx);
            using var wb = new XLWorkbook(xlsx);
            var ws = wb.Worksheet("Bảng điểm");
            Assert.Equal("001", ws.Cell(5, 2).GetString());
            Assert.Equal(8.0, ws.Cell(5, 11).GetDouble());
            Assert.Equal("003", ws.Cell(7, 2).GetString());
            Assert.Equal("Chưa nộp", ws.Cell(7, 11).GetString());
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
