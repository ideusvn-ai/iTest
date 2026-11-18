using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ITest.Core;
using ITest.UI;
using Microsoft.Win32;

namespace ITest.Editor;

public partial class MainWindow : Window
{
    private Draft? _draft;
    private bool _editingMeta;
    private bool _loading;
    private bool _packed;
    private static readonly string SessionFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "iTest-Editor", "session.txt");
    private readonly ObservableCollection<QuestionRow> _rows = new();
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(800) };
    private readonly List<TextBox> _optBoxes = new();
    private readonly List<RadioButton> _optRadios = new();
    private static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");
    private static readonly string[] MathSymbols = { "√", "²", "³", "≤", "≥", "≠", "±", "×", "÷", "π", "Δ", "∞", "°", "∈", "∉", "⊂", "∩", "∪", "∅", "ℝ", "⇒", "⇔", "α", "β" };

    public MainWindow()
    {
        InitializeComponent();
        QList.ItemsSource = _rows;
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveNow(); };
        foreach (var tb in new[] { FExam, FSubject, FAuthor, FUnit, EText, EGuide }) VietInput.Attach(tb);
        foreach (var s in MathSymbols)
        {
            var b = new Button { Content = s, MinWidth = 32, MinHeight = 30, Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(0, 0, 4, 4) };
            b.Click += (_, _) => VietInput.Insert(EText, s);
            EMathBar.Children.Add(b);
        }
        BuildOptionEditors();
        RefreshDrafts();
        UpdateFormTotal();
        Closing += (_, _) => { if (_draft != null) { SaveNow(); RememberSession(); } };
        RestoreSession();
    }

    // ===================== Trang đầu =====================
    private static double Num(string s) => double.TryParse(s.Trim().Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;
    private static int Int(string s) => int.TryParse(s.Trim(), out var v) ? v : 0;
    private static string F(double v) => Math.Round(v, 2).ToString("0.##", Vi);

    private void Total_Changed(object sender, TextChangedEventArgs e) => UpdateFormTotal();

    private void UpdateFormTotal()
    {
        if (FTotal == null || FNMc == null || FPMc == null || FNEs == null || FPEs == null || FTotalBox == null) return;
        double total = Int(FNMc.Text) * Num(FPMc.Text) + Int(FNEs.Text) * Num(FPEs.Text);
        bool ok = Math.Abs(total - 10) < 1e-6;
        FTotal.Text = $"{Int(FNMc.Text)} × {FPMc.Text} + {Int(FNEs.Text)} × {FPEs.Text} = {F(total)} điểm" +
                      (ok ? "  ✓" : total < 10 ? $"  · thiếu {F(10 - total)} điểm" : $"  · thừa {F(total - 10)} điểm");
        FTotalBox.Background = new SolidColorBrush(ok ? Color.FromRgb(0xF0, 0xFD, 0xF4) : Color.FromRgb(0xFE, 0xF2, 0xF2));
        FTotal.Foreground = new SolidColorBrush(ok ? Color.FromRgb(0x16, 0x65, 0x34) : Color.FromRgb(0x99, 0x1B, 0x1B));
    }

    private void Pass_Changed(object sender, RoutedEventArgs e)
    {
        if (FPass2Row == null) return;
        FPass2Row.Visibility = FPass.Password.Length > 0 ? Visibility.Visible : Visibility.Hidden;
        FPassMsg.Text = FPass2.Password.Length == 0 ? "Nhập lại mật khẩu để xác nhận" : FPass.Password == FPass2.Password ? "✓ Mật khẩu khớp" : "Mật khẩu xác nhận không khớp";
    }

    private void RefreshDrafts()
    {
        DraftList.ItemsSource = Draft.All().Select(d => new DraftItem(d)).ToList();
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        var miss = new List<string>();
        if (string.IsNullOrWhiteSpace(FExam.Text)) miss.Add("kỳ thi");
        if (string.IsNullOrWhiteSpace(FSubject.Text)) miss.Add("môn thi");
        if (string.IsNullOrWhiteSpace(FAuthor.Text)) miss.Add("người soạn đề");
        if (string.IsNullOrWhiteSpace(FYear.Text)) miss.Add("năm học");
        if (Int(FMinutes.Text) <= 0) miss.Add("thời gian");
        if (Int(FCodes.Text) <= 0) miss.Add("số mã đề");
        if (Int(FNMc.Text) + Int(FNEs.Text) <= 0) miss.Add("số câu");
        if (miss.Count > 0) { FError.Text = "Vui lòng nhập: " + string.Join(", ", miss) + "."; return; }
        if (FPass.Password.Length > 0 && FPass.Password != FPass2.Password) { FError.Text = "Mật khẩu xác nhận không khớp."; return; }
        FError.Text = "";

        var d = _editingMeta && _draft != null ? _draft : new Draft();
        var x = d.Exam;
        x.ExamName = FExam.Text.Trim(); x.Subject = FSubject.Text.Trim(); x.Author = FAuthor.Text.Trim(); x.Unit = FUnit.Text.Trim();
        x.SchoolYear = FYear.Text.Trim(); x.AllowDocuments = FDocs.SelectedIndex == 1; x.Minutes = Int(FMinutes.Text); x.Codes = Int(FCodes.Text);
        if (_editingMeta && _draft != null)
            x.Resize(Int(FNMc.Text), Num(FPMc.Text), Int(FNEs.Text), Num(FPEs.Text));
        else
        {
            x.McCount = Int(FNMc.Text); x.McPoints = Num(FPMc.Text); x.EssayCount = Int(FNEs.Text); x.EssayPoints = Num(FPEs.Text);
            x.BuildSkeleton();
        }
        d.Locked = FPass.Password.Length > 0;
        d.Password = FPass.Password;
        _draft = d;
        _editingMeta = false;
        SaveNow();
        OpenEditor();
    }

    private void CancelMeta_Click(object sender, RoutedEventArgs e) { _editingMeta = false; OpenEditor(); }

    private void FillForm(ExamSet? x, Draft? d)
    {
        FExam.Text = x?.ExamName ?? ""; FSubject.Text = x?.Subject ?? ""; FAuthor.Text = x?.Author ?? FAuthor.Text; FUnit.Text = x?.Unit ?? FUnit.Text;
        FYear.Text = x?.SchoolYear ?? "2026 – 2027"; FDocs.SelectedIndex = x?.AllowDocuments == true ? 1 : 0;
        FMinutes.Text = (x?.Minutes ?? 45).ToString(); FCodes.Text = (x?.Codes ?? 4).ToString();
        FNMc.Text = (x?.McCount ?? 36).ToString(); FPMc.Text = F(x?.McPoints ?? 0.25);
        FNEs.Text = (x?.EssayCount ?? 4).ToString(); FPEs.Text = F(x?.EssayPoints ?? 0.25);
        FPass.Password = d?.Password ?? ""; FPass2.Password = d?.Password ?? "";
        Pass_Changed(this, new RoutedEventArgs());
        UpdateFormTotal();
    }

    private void ShowHome(bool editMeta)
    {
        _editingMeta = editMeta;
        FormTitle.Text = editMeta ? "Sửa thông tin bộ đề" : "Tạo bộ đề mới";
        FormSub.Text = editMeta ? "Các câu hỏi đã soạn được giữ nguyên." : "Nhập thông tin bộ đề trước, phần mềm sẽ tạo sẵn khung câu hỏi và chia điểm cho từng câu.";
        FCreate.Content = editMeta ? "Lưu thông tin bộ đề" : "Tạo bộ đề & bắt đầu soạn câu hỏi";
        FCancel.Visibility = editMeta ? Visibility.Visible : Visibility.Collapsed;
        FMetaNote.Visibility = editMeta ? Visibility.Visible : Visibility.Collapsed;
        FError.Text = "";
        EditPanel.Visibility = Visibility.Collapsed;
        EditButtons.Visibility = Visibility.Collapsed;
        TitleText.Text = "iTest-Editor";
        SaveText.Text = "Soạn bộ đề chuẩn cho iTest-Server";
        RefreshDrafts();
        Anim.Show(HomePanel);
    }

    private void NewSet_Click(object sender, RoutedEventArgs e)
    {
        if (_draft != null) SaveNow();
        var author = _draft?.Exam.Author; var unit = _draft?.Exam.Unit;
        _draft = null;
        FillForm(null, null);
        FAuthor.Text = author ?? ""; FUnit.Text = unit ?? "";
        ShowHome(false);
    }

    private void EditMeta_Click(object sender, RoutedEventArgs e)
    {
        if (_draft == null) return;
        SaveNow();
        FillForm(_draft.Exam, _draft);
        ShowHome(true);
    }

    private void OpenDraft_Click(object sender, RoutedEventArgs e) => OpenSelectedDraft();
    private void DraftList_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => OpenSelectedDraft();

    private void OpenSelectedDraft()
    {
        if (DraftList.SelectedItem is not DraftItem di) return;
        _draft = di.Draft;
        OpenEditor();
    }

    private void DeleteDraft_Click(object sender, RoutedEventArgs e)
    {
        if (DraftList.SelectedItem is not DraftItem di) return;
        if (MessageBox.Show(this, $"Xoá bản nháp “{di.Title}”? Không thể hoàn tác.", "Xoá bản nháp", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        di.Draft.Delete();
        RefreshDrafts();
    }

    private void OpenPackage_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Bộ đề iTest (*.itz)|*.itz" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            string? pass = null;
            if (ExamPackage.IsPasswordProtected(dlg.FileName))
            {
                pass = AskPassword();
                if (pass == null) return;
            }
            var c = ExamPackage.Load(dlg.FileName, pass);
            var d = new Draft { Exam = c.Exam, Locked = pass != null, Password = pass ?? "" };
            Directory.CreateDirectory(d.MediaFolder);
            foreach (var (name, data) in c.Media) File.WriteAllBytes(Path.Combine(d.MediaFolder, name), data);
            _draft = d;
            SaveNow();
            OpenEditor();
        }
        catch (CryptographicException) { MessageBox.Show(this, "Sai mật khẩu hoặc file đã bị chỉnh sửa.", "Mở bộ đề"); }
        catch (Exception ex) { MessageBox.Show(this, "Không mở được bộ đề: " + ex.Message, "Mở bộ đề"); }
    }

    private string? AskPassword()
    {
        var w = new Window { Owner = this, Title = "Mật khẩu bộ đề", Width = 380, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
        var p = new StackPanel { Margin = new Thickness(18) };
        var pb = new PasswordBox();
        var ok = new Button { Content = "Mở", IsDefault = true, Margin = new Thickness(0, 12, 0, 0), Style = (Style)FindResource("BtnTeal") };
        string? res = null;
        ok.Click += (_, _) => { res = pb.Password; w.DialogResult = true; };
        p.Children.Add(new TextBlock { Text = "Nhập mật khẩu bộ đề:" }); p.Children.Add(pb); p.Children.Add(ok);
        w.Content = p;
        w.Loaded += (_, _) => pb.Focus();
        return w.ShowDialog() == true ? res : null;
    }

    // ===================== Soạn câu hỏi =====================
    private void OpenEditor()
    {
        if (_draft == null) return;
        HomePanel.Visibility = Visibility.Collapsed;
        EditButtons.Visibility = Visibility.Visible;
        Anim.Show(EditPanel);
        RebuildRows();
        QList.SelectedIndex = _rows.Count > 0 ? 0 : -1;
        RefreshSide();
    }

    private void RebuildRows()
    {
        _rows.Clear();
        if (_draft == null) return;
        var x = _draft.Exam;
        bool two = x.McQuestions.Any() && x.EssayQuestions.Any();
        QuestionType? last = null;
        foreach (var q in x.Questions)
        {
            string? header = null;
            if (two && q.Type != last) header = q.Type == QuestionType.MultipleChoice ? $"PHẦN I · TRẮC NGHIỆM ({x.McQuestions.Count()})" : $"PHẦN II · TỰ LUẬN ({x.EssayQuestions.Count()})";
            last = q.Type;
            _rows.Add(new QuestionRow(q, header));
        }
    }

    private Question? Cur => (QList.SelectedItem as QuestionRow)?.Q;

    private void BuildOptionEditors()
    {
        var panel = new StackPanel();
        for (int k = 0; k < 4; k++)
        {
            int idx = k;
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var rb = new RadioButton { GroupName = "correct", Content = ((char)('A' + k)).ToString(), FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Width = 48, ToolTip = "Đặt làm đáp án đúng" };
            rb.Checked += (_, _) => { if (_loading || Cur == null) return; Cur.Correct = idx; Changed(); };
            var tb = new TextBox();
            VietInput.Attach(tb);
            tb.TextChanged += (_, _) =>
            {
                if (_loading || Cur == null) return;
                while (Cur.Options.Count < 4) Cur.Options.Add("");
                Cur.Options[idx] = tb.Text;
                Changed();
            };
            DockPanel.SetDock(rb, Dock.Left);
            row.Children.Add(rb); row.Children.Add(tb);
            panel.Children.Add(row);
            _optRadios.Add(rb); _optBoxes.Add(tb);
        }
        EOptions.Items.Add(panel);
    }

    private void QList_SelectionChanged(object sender, SelectionChangedEventArgs e) => LoadCurrent();

    private void LoadCurrent()
    {
        var q = Cur;
        if (q == null) return;
        _loading = true;
        int no = _draft!.Exam.Questions.IndexOf(q) + 1;
        EBadgeText.Text = $"Câu {no} · {(q.Type == QuestionType.MultipleChoice ? "Trắc nghiệm" : "Tự luận")}";
        EBadge.Background = new SolidColorBrush(q.Type == QuestionType.MultipleChoice ? Color.FromRgb(0xDB, 0xEA, 0xFE) : Color.FromRgb(0xED, 0xE9, 0xFE));
        EText.Text = q.Text;
        EPoints.Text = F(q.Points);
        EMcPanel.Visibility = q.Type == QuestionType.MultipleChoice ? Visibility.Visible : Visibility.Collapsed;
        EEsPanel.Visibility = q.Type == QuestionType.Essay ? Visibility.Visible : Visibility.Collapsed;
        while (q.Type == QuestionType.MultipleChoice && q.Options.Count < 4) q.Options.Add("");
        for (int k = 0; k < 4; k++)
        {
            _optBoxes[k].Text = q.Options.Count > k ? q.Options[k] : "";
            _optRadios[k].IsChecked = q.Correct == k;
        }
        EGuide.Text = q.Guide;
        ShowMedia(q);
        _loading = false;
        UpdateMissing();
        Anim.FadeIn(EditorBody, 0.2);
    }

    private void ShowMedia(Question q)
    {
        var parts = new List<string>();
        EImage.Source = null;
        if (q.Image != null)
        {
            parts.Add("Ảnh: " + q.Image);
            var p = Path.Combine(_draft!.MediaFolder, q.Image);
            if (File.Exists(p))
            {
                var bmp = new BitmapImage();
                bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.UriSource = new Uri(p); bmp.EndInit();
                EImage.Source = bmp;
            }
        }
        if (q.Audio != null) parts.Add($"Âm thanh: {q.Audio} · nghe tối đa {q.AudioMaxPlays} lần");
        EMediaText.Text = parts.Count == 0 ? "Chưa có ảnh hoặc âm thanh." : string.Join("   ·   ", parts);
        RemoveMediaBtn.IsEnabled = parts.Count > 0;
    }

    private void UpdateMissing()
    {
        var q = Cur;
        if (q == null) return;
        var m = q.Missing();
        EMissing.Text = m.Count == 0 ? "✓ Đủ thông tin" : "Còn thiếu: " + string.Join(", ", m);
        EMissing.Foreground = new SolidColorBrush(m.Count == 0 ? Color.FromRgb(0x16, 0x65, 0x34) : Color.FromRgb(0x9A, 0x34, 0x12));
    }

    private void EText_Changed(object sender, TextChangedEventArgs e) { if (_loading || Cur == null) return; Cur.Text = EText.Text; Changed(); }
    private void EGuide_Changed(object sender, TextChangedEventArgs e) { if (_loading || Cur == null) return; Cur.Guide = EGuide.Text; Changed(); }
    private void EPoints_Changed(object sender, TextChangedEventArgs e) { if (_loading || Cur == null) return; Cur.Points = Num(EPoints.Text); Changed(); }

    private void Changed()
    {
        _packed = false;
        (QList.SelectedItem as QuestionRow)?.Refresh();
        UpdateMissing();
        RefreshSide();
        SaveText.Text = "Đang lưu bản nháp…";
        _saveTimer.Stop(); _saveTimer.Start();
    }

    // ===================== Phiên làm việc =====================
    private void RememberSession()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SessionFile)!);
            if (_draft != null && !_packed) File.WriteAllText(SessionFile, _draft.Id);
            else if (File.Exists(SessionFile)) File.Delete(SessionFile);
        }
        catch { }
    }

    private void RestoreSession()
    {
        try
        {
            if (!File.Exists(SessionFile)) return;
            var id = File.ReadAllText(SessionFile).Trim();
            var d = Draft.All().FirstOrDefault(x => x.Id == id);
            if (d == null) return;
            _draft = d;
            OpenEditor();
            SaveText.Text = $"Đã mở lại phiên làm việc trước · bản nháp lưu lúc {d.SavedAt:HH:mm dd/MM}";
        }
        catch { }
    }

    private void Quit_Click(object sender, RoutedEventArgs e)
    {
        if (_draft != null)
        {
            SaveNow();
            RememberSession();
            if (!_packed && MessageBox.Show(this, "Bộ đề chưa đóng gói. Phiên làm việc đã được lưu và sẽ tự mở lại lần sau.\n\nThoát iTest-Editor?",
                    "Thoát", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        }
        else RememberSession();
        Close();
    }

    private void SaveNow()
    {
        if (_draft == null) return;
        try
        {
            _draft.Save();
            SaveText.Text = $"Bản nháp đã tự lưu lúc {_draft.SavedAt:HH:mm:ss} · chưa đóng gói";
        }
        catch (Exception ex) { SaveText.Text = "Không lưu được bản nháp: " + ex.Message; }
    }

    private void RefreshSide()
    {
        if (_draft == null) return;
        var x = _draft.Exam;
        TitleText.Text = $"iTest-Editor · {x.Subject} – {x.ExamName}";
        double total = x.TotalPoints;
        bool ok10 = Math.Abs(total - 10) < 1e-6;
        int done = x.Questions.Count(q => q.Missing().Count == 0);
        TotalText.Text = F(total);
        TotalText.Foreground = new SolidColorBrush(ok10 ? Color.FromRgb(0x16, 0x65, 0x34) : Color.FromRgb(0xB9, 0x1C, 0x1C));
        TotalBar.Value = Math.Min(10, total);
        TotalBar.Foreground = new SolidColorBrush(ok10 ? Color.FromRgb(0x16, 0xA3, 0x4A) : total > 10 ? Color.FromRgb(0xDC, 0x26, 0x26) : Color.FromRgb(0xF5, 0x9E, 0x0B));
        TotalMsg.Text = ok10 ? "Tổng điểm hợp lệ." : total < 10 ? $"Chưa đủ 10 điểm · còn thiếu {F(10 - total)} điểm." : $"Vượt quá 10 điểm · thừa {F(total - 10)} điểm.";
        TotalMsgBox.Background = new SolidColorBrush(ok10 ? Color.FromRgb(0xF0, 0xFD, 0xF4) : Color.FromRgb(0xFE, 0xF2, 0xF2));
        SumParts.Text = $"Trắc nghiệm: {F(x.McQuestions.Sum(q => q.Points))}đ   ·   Tự luận: {F(x.EssayQuestions.Sum(q => q.Points))}đ";
        DoneCount.Text = $"đủ {done}/{x.Questions.Count}";
        DoneBar.Value = x.Questions.Count == 0 ? 0 : (double)done / x.Questions.Count;
        Check1.Text = (done == x.Questions.Count ? "✓ " : "! ") + $"Đủ thông tin {done}/{x.Questions.Count} câu";
        Check2.Text = (ok10 ? "✓ " : "! ") + $"Tổng điểm bằng 10 (hiện tại {F(total)})";
        Check3.Text = "✓ " + (_draft.Locked ? "Có mật khẩu, đã xác nhận" : "Không đặt mật khẩu");
        Check1.Foreground = done == x.Questions.Count ? Brushes.DarkGreen : Brushes.DarkOrange;
        Check2.Foreground = ok10 ? Brushes.DarkGreen : Brushes.DarkOrange;
        Check3.Foreground = Brushes.DarkGreen;
        PackBtn.IsEnabled = done == x.Questions.Count && ok10 && x.Questions.Count > 0;
        PackBtn.ToolTip = PackBtn.IsEnabled ? "Đóng gói thành file .itz cho iTest-Server" : "Cần đủ thông tin tất cả câu hỏi và tổng điểm bằng 10";
        MetaRows.ItemsSource = new List<object>
        {
            new { K = "Người soạn", V = x.Author }, new { K = "Đơn vị", V = x.Unit }, new { K = "Kỳ thi", V = x.ExamName },
            new { K = "Môn thi", V = x.Subject }, new { K = "Năm học", V = x.SchoolYear }, new { K = "Thời gian", V = $"{x.Minutes} phút" },
            new { K = "Số mã đề", V = x.Codes.ToString() }, new { K = "Tài liệu", V = x.AllowDocuments ? "Được sử dụng" : "Không được sử dụng" },
            new { K = "Mật khẩu", V = _draft.Locked ? "Có (đã xác nhận)" : "Không" }
        };
    }

    private void Prev_Click(object sender, RoutedEventArgs e) { if (QList.SelectedIndex > 0) QList.SelectedIndex--; QList.ScrollIntoView(QList.SelectedItem); }
    private void Next_Click(object sender, RoutedEventArgs e) { if (QList.SelectedIndex < _rows.Count - 1) QList.SelectedIndex++; QList.ScrollIntoView(QList.SelectedItem); }

    private void NextMissing_Click(object sender, RoutedEventArgs e)
    {
        int n = _rows.Count;
        for (int k = 1; k <= n; k++)
        {
            int j = (QList.SelectedIndex + k + n) % n;
            if (_rows[j].Q.Missing().Count > 0) { QList.SelectedIndex = j; QList.ScrollIntoView(_rows[j]); return; }
        }
        MessageBox.Show(this, "Tất cả câu hỏi đã đủ thông tin.", "iTest-Editor");
    }

    private void ResetPoints_Click(object sender, RoutedEventArgs e)
    {
        if (_draft == null) return;
        foreach (var q in _draft.Exam.Questions) q.Points = q.Type == QuestionType.MultipleChoice ? _draft.Exam.McPoints : _draft.Exam.EssayPoints;
        foreach (var r in _rows) r.Refresh();
        LoadCurrent();
        Changed();
    }

    private void AddImage_Click(object sender, RoutedEventArgs e)
    {
        if (_draft == null || Cur == null) return;
        var dlg = new OpenFileDialog { Filter = "Ảnh (*.png;*.jpg;*.jpeg;*.gif;*.bmp)|*.png;*.jpg;*.jpeg;*.gif;*.bmp" };
        if (dlg.ShowDialog(this) != true) return;
        Cur.Image = _draft.AddMedia(dlg.FileName);
        ShowMedia(Cur); Changed();
    }

    private void AddAudio_Click(object sender, RoutedEventArgs e)
    {
        if (_draft == null || Cur == null) return;
        var dlg = new OpenFileDialog { Filter = "Âm thanh (*.mp3;*.wav;*.m4a)|*.mp3;*.wav;*.m4a" };
        if (dlg.ShowDialog(this) != true) return;
        Cur.Audio = _draft.AddMedia(dlg.FileName);
        ShowMedia(Cur); Changed();
    }

    private void RemoveMedia_Click(object sender, RoutedEventArgs e)
    {
        if (Cur == null) return;
        Cur.Image = null; Cur.Audio = null;
        ShowMedia(Cur); Changed();
    }

    // ===================== Nhập & đóng gói =====================
    private void ImportWord_Click(object sender, RoutedEventArgs e) => Import("Word (*.docx)|*.docx", Importer.FromWord);
    private void ImportExcel_Click(object sender, RoutedEventArgs e) => Import("Excel (*.xlsx)|*.xlsx", Importer.FromExcel);

    private void Import(string filter, Func<string, List<ImportedQuestion>> reader)
    {
        if (_draft == null) return;
        var dlg = new OpenFileDialog { Filter = filter };
        if (dlg.ShowDialog(this) != true) return;
        List<ImportedQuestion> items;
        try { items = reader(dlg.FileName); }
        catch (Exception ex) { MessageBox.Show(this, "Không đọc được file: " + ex.Message, "Nhập câu hỏi"); return; }

        var x = _draft.Exam;
        int filled = 0, skipped = 0;
        foreach (var it in items)
        {
            var slot = x.Questions.FirstOrDefault(q => q.Type == it.Type && string.IsNullOrWhiteSpace(q.Text) && (q.Type == QuestionType.Essay || q.Options.All(string.IsNullOrWhiteSpace)));
            if (slot == null) { skipped++; continue; }
            slot.Text = it.Text;
            if (it.Type == QuestionType.MultipleChoice)
            {
                slot.Options = it.Options.Take(4).ToList();
                while (slot.Options.Count < 4) slot.Options.Add("");
                slot.Correct = it.Correct;
            }
            if (it.Points is double p && p > 0) slot.Points = p;
            if (it.Image != null) slot.Image = _draft.AddMedia(it.Image, it.ImageExt);
            filled++;
        }
        var bad = x.Questions.Select((q, i) => (q, i)).Where(t => t.q.Missing().Count > 0 && !string.IsNullOrWhiteSpace(t.q.Text)).Select(t => (t.i + 1).ToString()).ToList();
        ImportNote.Text = $"Đã nhập {filled} câu từ {Path.GetFileName(dlg.FileName)}." +
                          (skipped > 0 ? $" Bỏ qua {skipped} câu vì không còn chỗ trống phù hợp (tăng số câu trong Thông tin bộ đề)." : "") +
                          (bad.Count > 0 ? $" Cần kiểm tra câu {string.Join(", ", bad)} (thiếu đáp án hoặc phương án)." : "");
        Anim.Show(ImportNoteBox);
        foreach (var r in _rows) r.Refresh();
        LoadCurrent();
        Changed();
    }

    private void Template_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Filter = "Excel (*.xlsx)|*.xlsx", FileName = "Mau_NhapCauHoi_iTest.xlsx" };
        if (dlg.ShowDialog(this) != true) return;
        try { Importer.WriteExcelTemplate(dlg.FileName); MessageBox.Show(this, "Đã tạo file mẫu:\n" + dlg.FileName, "iTest-Editor"); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "iTest-Editor"); }
    }

    private void Pack_Click(object sender, RoutedEventArgs e)
    {
        if (_draft == null) return;
        var x = _draft.Exam;
        string Ascii(string s) => new string(s.Normalize(System.Text.NormalizationForm.FormD)
            .Where(ch => CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark).ToArray())
            .Replace('đ', 'd').Replace('Đ', 'D').Replace(' ', '_');
        var dlg = new SaveFileDialog { Filter = "Bộ đề iTest (*.itz)|*.itz", FileName = Ascii($"{x.Subject}_{x.ExamName}").ToUpperInvariant() + ".itz" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            x.CreatedAt = DateTime.Now;
            ExamPackage.Save(dlg.FileName, x, _draft.LoadMedia(), _draft.Locked ? _draft.Password : null);
            _packed = true;
            RememberSession();
            MessageBox.Show(this, $"Đã đóng gói bộ đề:\n{dlg.FileName}\n\n{(_draft.Locked ? "Hãy gửi mật khẩu cho giám thị qua kênh riêng." : "Bộ đề không đặt mật khẩu.")}",
                "Đóng gói bộ câu hỏi", MessageBoxButton.OK, MessageBoxImage.Information);
            if (MessageBox.Show(this, "Xoá bản nháp này khỏi danh sách bản nháp?", "iTest-Editor", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            {
                _draft.Delete();
                _draft = null;
                FillForm(null, null);
                FAuthor.Text = x.Author; FUnit.Text = x.Unit;
                ShowHome(false);
            }
        }
        catch (Exception ex) { MessageBox.Show(this, "Lỗi khi đóng gói: " + ex.Message, "iTest-Editor", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
}

public sealed class QuestionRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public Question Q { get; }
    public string? Header { get; }
    public QuestionRow(Question q, string? header) { Q = q; Header = header; }
    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    public int No => Q.Id;
    public string Preview => string.IsNullOrWhiteSpace(Q.Text) ? "Chưa có nội dung" : Q.Text.Replace('\n', ' ');
    public string PointsText => Math.Round(Q.Points, 2).ToString("0.##", CultureInfo.GetCultureInfo("vi-VN")) + "đ";
    public Brush DotBrush => Q.Missing().Count == 0 ? Brushes.ForestGreen : Brushes.Orange;
    public Visibility HeaderVisibility => Header == null ? Visibility.Collapsed : Visibility.Visible;
    public Brush HeaderBrush => Q.Type == QuestionType.MultipleChoice ? new SolidColorBrush(Color.FromRgb(0x1E, 0x3A, 0x8A)) : new SolidColorBrush(Color.FromRgb(0x5B, 0x21, 0xB6));
}

public sealed class DraftItem
{
    public Draft Draft { get; }
    public DraftItem(Draft d) => Draft = d;
    public string Title => $"{Draft.Exam.Subject} · {Draft.Exam.ExamName}";
    public string Meta => $"đủ {Draft.Exam.Questions.Count(q => q.Missing().Count == 0)}/{Draft.Exam.Questions.Count} câu · lưu lúc {Draft.SavedAt:HH:mm dd/MM}";
}
