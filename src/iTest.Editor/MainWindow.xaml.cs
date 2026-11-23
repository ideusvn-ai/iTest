using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    private TextBox? _mathTarget;
    private bool _mathOn;
    private static readonly QuestionType[] ObjectiveTypes = { QuestionType.MultipleChoice, QuestionType.MultiSelect, QuestionType.YesNo, QuestionType.TrueFalse };
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
            var b = new Button { Content = s, MinWidth = 32, MinHeight = 30, Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(0, 0, 4, 4), IsTabStop = false, Focusable = false };
            b.Click += (_, _) => VietInput.Insert(_mathTarget ?? EText, s);
            EMathBar.Children.Add(b);
        }
        foreach (var ty in ObjectiveTypes) EType.Items.Add(new ComboBoxItem { Content = ty.Label(), Tag = ty });
        EText.GotKeyboardFocus += (_, _) => _mathTarget = EText;
        EGuide.GotKeyboardFocus += (_, _) => _mathTarget = EGuide;
        VietInput.Attach(EShortKey);
        PreviewKeyDown += FastEntryKeys;
        LoadIme();
        UpdateFormTotal();
        Closing += (_, _) => { if (_draft != null) { _draft.Save(sync: true); RememberSession(); } };
        // Mở lại phiên trước rồi mới dọn bản nháp rác, để bản nháp đang soạn dở (kể cả còn trống) không bị xoá nhầm.
        RestoreSession();
        RefreshDrafts();
    }

    // ===================== Bộ gõ tiếng Việt =====================
    private static readonly string ImeFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "iTest-Editor", "bo-go.txt");

    private void LoadIme()
    {
        string mode = "telex";
        try { if (File.Exists(ImeFile)) mode = File.ReadAllText(ImeFile).Trim(); } catch { }
        foreach (ComboBoxItem it in ImeBox.Items) if ((string)it.Tag == mode) ImeBox.SelectedItem = it;
        if (ImeBox.SelectedItem == null) ImeBox.SelectedIndex = 0;
    }

    private void Ime_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ImeBox?.SelectedItem is not ComboBoxItem { Tag: string mode }) return;
        VietInput.Enabled = mode != "off";
        VietInput.Mode = mode == "vni" ? ImeMode.Vni : ImeMode.Telex;
        try { Directory.CreateDirectory(Path.GetDirectoryName(ImeFile)!); File.WriteAllText(ImeFile, mode); } catch { }
    }

    // ===================== Trang đầu =====================
    private static double Num(string s) => double.TryParse(s.Trim().Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;
    private static int Int(string s) => int.TryParse(s.Trim(), out var v) ? v : 0;
    private static string F(double v) => Math.Round(v, 2).ToString("0.##", Vi);

    private void Total_Changed(object sender, TextChangedEventArgs e) => UpdateFormTotal();

    private void UpdateFormTotal()
    {
        if (FTotal == null || FNMc == null || FPMc == null || FNEs == null || FPEs == null || FTotalBox == null || FPerMc == null || FPerEs == null) return;
        int nMc = Int(FNMc.Text), nEs = Int(FNEs.Text);
        double tMc = nMc > 0 ? Num(FPMc.Text) : 0, tEs = nEs > 0 ? Num(FPEs.Text) : 0;
        FPerMc.Text = PerHint(nMc, tMc);
        FPerEs.Text = PerHint(nEs, tEs);
        double total = tMc + tEs;
        bool ok = Math.Abs(total - 10) < 1e-6;
        FTotal.Text = $"Tổng điểm bộ đề: {F(tMc)} + {F(tEs)} = {F(total)} điểm" +
                      (ok ? "  ✓" : total < 10 ? $"  · thiếu {F(10 - total)} điểm" : $"  · thừa {F(total - 10)} điểm");
        FTotalBox.Background = new SolidColorBrush(ok ? Color.FromRgb(0xF0, 0xFD, 0xF4) : Color.FromRgb(0xFE, 0xF2, 0xF2));
        FTotal.Foreground = new SolidColorBrush(ok ? Color.FromRgb(0x16, 0x65, 0x34) : Color.FromRgb(0x99, 0x1B, 0x1B));
    }

    private static string PerHint(int n, double total)
    {
        if (n <= 0) return "Không có phần này trong bộ đề.";
        var pts = ExamSet.Split(total, n);
        double min = pts.Min(), max = pts.Max();
        return min == max ? $"Hệ thống tự chia: {F(min)} điểm/câu" : $"Hệ thống tự chia: {F(min)}–{F(max)} điểm/câu";
    }

    private void Pass_Changed(object sender, RoutedEventArgs e)
    {
        if (FPass2Row == null) return;
        FPass2Row.Visibility = FPass.Password.Length > 0 ? Visibility.Visible : Visibility.Hidden;
        FPassMsg.Text = FPass2.Password.Length == 0 ? "Nhập lại mật khẩu để xác nhận" : FPass.Password == FPass2.Password ? "✓ Mật khẩu khớp" : "Mật khẩu xác nhận không khớp";
    }

    /// <summary>Khung đề chưa có nội dung nào (chưa gõ câu hỏi, phương án, chưa gắn ảnh/âm thanh).</summary>
    private static bool IsBlank(Draft d) => d.Exam.Questions.All(q =>
        string.IsNullOrWhiteSpace(q.Text) && q.Image == null && q.Audio == null && string.IsNullOrWhiteSpace(q.Guide) && string.IsNullOrWhiteSpace(q.ShortKey)
        && (q.Type == QuestionType.YesNo || q.Options.All(string.IsNullOrWhiteSpace)));

    private void RefreshDrafts()
    {
        // Dọn bản nháp rác: khung đề trống (chưa gõ gì), không phải bản đang mở.
        foreach (var d in Draft.All())
            if (d.Id != _draft?.Id && IsBlank(d))
                d.Delete();
        var items = Draft.All().Select(d => new DraftItem(d)).ToList();
        DraftList.ItemsSource = items;
        NoDraftHint.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void DeleteDraftItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not DraftItem di) return;
        if (MessageBox.Show(this, $"Xoá bản nháp “{di.Title}”? Không thể hoàn tác.", "Xoá bản nháp", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        bool current = di.Draft.Id == _draft?.Id;
        if (current) { _draft?.Delete(); _draft = null; }
        di.Draft.Delete();
        if (current && _editingMeta) { FillForm(null, null); ShowHome(false); }
        else RefreshDrafts();
    }

    private void DeleteAllDrafts_Click(object sender, RoutedEventArgs e)
    {
        var all = Draft.All();
        if (all.Count == 0) return;
        if (MessageBox.Show(this, $"Xoá tất cả {all.Count} bản nháp chưa đóng gói? Không thể hoàn tác.", "Xoá tất cả bản nháp", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _draft?.Delete();
        foreach (var d in all) d.Delete();
        _draft = null;
        if (_editingMeta) { FillForm(null, null); ShowHome(false); }
        else RefreshDrafts();
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
        int nMc = Int(FNMc.Text), nEs = Int(FNEs.Text);
        double tMc = nMc > 0 ? Num(FPMc.Text) : 0, tEs = nEs > 0 ? Num(FPEs.Text) : 0;
        if (nMc > 0 && tMc <= 0) { FError.Text = "Vui lòng nhập tổng điểm phần trắc nghiệm."; return; }
        if (nEs > 0 && tEs <= 0) { FError.Text = "Vui lòng nhập tổng điểm phần tự luận."; return; }
        if (_editingMeta && _draft != null)
        {
            x.EnsureTotals();
            x.Resize(nMc, tMc, nEs, tEs);
        }
        else
        {
            x.McCount = nMc; x.McTotal = tMc; x.EssayCount = nEs; x.EssayTotal = tEs;
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
        x?.EnsureTotals();
        FNMc.Text = (x?.McCount ?? 36).ToString(); FPMc.Text = F(x?.McTotal ?? 9);
        FNEs.Text = (x?.EssayCount ?? 4).ToString(); FPEs.Text = F(x?.EssayTotal ?? 1);
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
        // Khung đề còn trống thì bỏ luôn; đã có nội dung thì lưu chắc chắn trước khi rời đi.
        if (_draft != null) { if (IsBlank(_draft)) _draft.Delete(); else _draft.Save(sync: true); }
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

    private async void OpenPackage_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Bộ đề iTest (*.itz)|*.itz" };
        if (dlg.ShowDialog(this) != true) return;
        var file = dlg.FileName;
        try
        {
            string? pass = null;
            if (ExamPackage.IsPasswordProtected(file))
            {
                pass = AskPassword();
                if (pass == null) return;
            }
            var c = await Busy.Run(this, "Đang giải mã và đọc bộ đề…", () => ExamPackage.Load(file, pass));
            // Tránh tạo trùng: nếu đã có bản nháp cùng môn + kỳ thi, mở lại bản nháp đó.
            var dup = Draft.All().FirstOrDefault(x =>
                string.Equals(x.Exam.Subject, c.Exam.Subject, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.Exam.ExamName, c.Exam.ExamName, StringComparison.OrdinalIgnoreCase));
            if (dup != null && MessageBox.Show(this,
                    $"Đã có bản nháp “{dup.Exam.Subject} · {dup.Exam.ExamName}”.\n\nMở bản nháp đó để soạn tiếp? (Chọn No để tạo bản nháp mới từ file.)",
                    "Mở bộ đề", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                _draft = dup;
                OpenEditor();
                return;
            }
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
        var w = new Window { Owner = this, Style = (Style)FindResource("DialogWindow"), Title = "Mật khẩu bộ đề", Width = 380, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
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
        UpdateListButtons();
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
            if (two && (last == null || q.Type.IsObjective() != last.Value.IsObjective())) header = q.Type.IsObjective() ? $"PHẦN I · TRẮC NGHIỆM ({x.McQuestions.Count()})" : $"PHẦN II · TỰ LUẬN ({x.EssayQuestions.Count()})";
            last = q.Type;
            _rows.Add(new QuestionRow(q, header));
        }
    }

    private Question? Cur => (QList.SelectedItem as QuestionRow)?.Q;

    private static readonly string[] TypeHints =
    {
        "Thí sinh chọn một phương án đúng.",
        "Thí sinh chọn tất cả phương án đúng; chỉ được điểm khi chọn đúng và đủ.",
        "Một mệnh đề, thí sinh chọn A. Đúng hoặc B. Sai.",
        "Mỗi ý chọn Đúng hoặc Sai. 4 ý: đúng 1 ý được 10%, 2 ý 25%, 3 ý 50%, 4 ý 100% điểm câu."
    };

    /// <summary>Dựng các ô phương án theo loại câu đang soạn.</summary>
    private void BuildOptionEditors(Question q)
    {
        EOptions.Items.Clear();
        _optBoxes.Clear();
        if (!q.Type.HasOptions()) return;
        if (q.Type == QuestionType.YesNo) { BuildYesNo(q); return; }
        while (q.Options.Count < 4) q.Options.Add("");
        if (q.Type == QuestionType.TrueFalse) while (q.TfKey.Count < q.Options.Count) q.TfKey.Add(null);
        EOptTitle.Text = q.Type switch
        {
            QuestionType.MultiSelect => "Phương án · đánh dấu tất cả đáp án đúng",
            QuestionType.TrueFalse => "Các ý · chọn Đúng hoặc Sai cho từng ý",
            _ => "Phương án · chọn đáp án đúng"
        };
        var panel = new StackPanel();
        for (int k = 0; k < q.Options.Count; k++)
        {
            int idx = k;
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            FrameworkElement mark;
            switch (q.Type)
            {
                case QuestionType.MultiSelect:
                    var cb = new CheckBox { Content = ((char)('A' + k)).ToString(), FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Width = 52, IsChecked = q.Corrects.Contains(k), ToolTip = "Là đáp án đúng (Ctrl+Space trong ô phương án)", IsTabStop = false };
                    cb.Checked += (_, _) => { if (_loading || Cur == null) return; if (!Cur.Corrects.Contains(idx)) Cur.Corrects.Add(idx); Cur.Corrects.Sort(); Changed(); };
                    cb.Unchecked += (_, _) => { if (_loading || Cur == null) return; Cur.Corrects.Remove(idx); Changed(); };
                    mark = cb;
                    break;
                case QuestionType.TrueFalse:
                    var tf = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                    var lab = new TextBlock { Text = (char)('a' + k) + ")", FontWeight = FontWeights.Bold, Width = 26, VerticalAlignment = VerticalAlignment.Center };
                    var rt = new RadioButton { GroupName = "tf" + k, Content = "Đúng", IsChecked = q.TfKey[k] == true, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, IsTabStop = false };
                    var rf = new RadioButton { GroupName = "tf" + k, Content = "Sai", IsChecked = q.TfKey[k] == false, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, IsTabStop = false };
                    rt.Checked += (_, _) => { if (_loading || Cur == null) return; SetTf(idx, true); };
                    rf.Checked += (_, _) => { if (_loading || Cur == null) return; SetTf(idx, false); };
                    tf.Children.Add(lab); tf.Children.Add(rt); tf.Children.Add(rf);
                    mark = tf;
                    break;
                default:
                    var rb = new RadioButton { GroupName = "correct", Content = ((char)('A' + k)).ToString(), FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Width = 52, IsChecked = q.Correct == k, ToolTip = "Đặt làm đáp án đúng (Ctrl+Space trong ô phương án)", IsTabStop = false };
                    rb.Checked += (_, _) => { if (_loading || Cur == null) return; Cur.Correct = idx; Changed(); };
                    mark = rb;
                    break;
            }
            var tb = new TextBox { Text = q.Options[k] };
            VietInput.Attach(tb);
            tb.GotKeyboardFocus += (_, _) => _mathTarget = tb;
            tb.TextChanged += (_, _) =>
            {
                if (_loading || Cur == null) return;
                while (Cur.Options.Count <= idx) Cur.Options.Add("");
                Cur.Options[idx] = tb.Text;
                Changed();
            };
            DockPanel.SetDock(mark, Dock.Left);
            row.Children.Add(mark); row.Children.Add(tb);
            panel.Children.Add(row);
            _optBoxes.Add(tb);
        }
        EOptions.Items.Add(panel);
    }

    /// <summary>
    /// Nhập nhanh: Tab / Shift+Tab đi qua các ô của câu đang soạn (nội dung → phương án A–D → đáp án/hướng dẫn → điểm);
    /// Tab ở ô cuối sang câu tiếp theo. Ctrl+Enter sang câu sau. Trong ô phương án: Ctrl+Space đánh dấu đáp án đúng
    /// (câu Đúng/Sai: lần lượt Đúng → Sai → bỏ chọn).
    /// </summary>
    private void FastEntryKeys(object sender, KeyEventArgs e)
    {
        if (EditPanel.Visibility != Visibility.Visible || Cur == null) return;
        var focused = Keyboard.FocusedElement as TextBox;
        var fields = new List<TextBox> { EText };
        fields.AddRange(_optBoxes);
        if (EShortPanel.Visibility == Visibility.Visible) fields.Add(EShortKey);
        if (EEsPanel.Visibility == Visibility.Visible) fields.Add(EGuide);
        fields.Add(EPoints);
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        if (e.Key == Key.Enter && ctrl)
        {
            e.Handled = true;
            GoNextQuestion();
            return;
        }
        if (focused == null || !fields.Contains(focused)) return;
        if (e.Key == Key.Tab && !ctrl)
        {
            e.Handled = true;
            int i = fields.IndexOf(focused);
            bool back = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            if (!back && i == fields.Count - 1) { GoNextQuestion(); return; }
            if (back && i == 0) { FocusField(EPoints); return; }
            FocusField(fields[i + (back ? -1 : 1)]);
            return;
        }
        if (e.Key == Key.Space && ctrl && _optBoxes.Contains(focused))
        {
            e.Handled = true;
            int k = _optBoxes.IndexOf(focused);
            switch (Cur.Type)
            {
                case QuestionType.MultipleChoice: case QuestionType.YesNo: Cur.Correct = k; break;
                case QuestionType.MultiSelect: if (!Cur.Corrects.Remove(k)) Cur.Corrects.Add(k); Cur.Corrects.Sort(); break;
                case QuestionType.TrueFalse:
                    while (Cur.TfKey.Count <= k) Cur.TfKey.Add(null);
                    Cur.TfKey[k] = Cur.TfKey[k] switch { null => true, true => false, _ => null };
                    break;
            }
            Changed();
            LoadCurrent();
            FocusField(_optBoxes[k]);
        }
    }

    private static void FocusField(TextBox tb)
    {
        tb.Focus();
        tb.SelectAll();
        tb.BringIntoView();
    }

    private void GoNextQuestion()
    {
        if (QList.SelectedIndex >= _rows.Count - 1) return;
        QList.SelectedIndex++;
        QList.ScrollIntoView(QList.SelectedItem);
        Dispatcher.BeginInvoke(() => { EText.Focus(); EText.CaretIndex = EText.Text.Length; }, System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>Câu đúng/sai 2 đáp án: chỉ chọn A. Đúng hoặc B. Sai, không cần nhập phương án.</summary>
    private void BuildYesNo(Question q)
    {
        q.Options = QuestionTypes.YesNoOptions.ToList();
        EOptTitle.Text = "Đáp án đúng";
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        for (int k = 0; k < 2; k++)
        {
            int idx = k;
            var rb = new RadioButton
            {
                GroupName = "yesno", Content = $"{(char)('A' + k)}. {q.Options[k]}", FontWeight = FontWeights.SemiBold, FontSize = 15,
                IsChecked = q.Correct == k, Margin = new Thickness(0, 4, 28, 4)
            };
            rb.Checked += (_, _) => { if (_loading || Cur == null) return; Cur.Correct = idx; Changed(); };
            row.Children.Add(rb);
        }
        EOptions.Items.Add(row);
    }

    private void SetTf(int idx, bool v)
    {
        if (Cur == null) return;
        while (Cur.TfKey.Count <= idx) Cur.TfKey.Add(null);
        Cur.TfKey[idx] = v;
        Changed();
    }

    private void EType_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || Cur == null || EType.SelectedItem is not ComboBoxItem { Tag: QuestionType ty } || ty == Cur.Type) return;
        Cur.Type = ty;
        if (ty == QuestionType.TrueFalse && Cur.TfKey.Count == 0) Cur.TfKey = new List<bool?> { null, null, null, null };
        if (ty == QuestionType.YesNo) { Cur.Options = QuestionTypes.YesNoOptions.ToList(); if (Cur.Correct > 1) Cur.Correct = -1; }
        else if (Cur.Options.SequenceEqual(QuestionTypes.YesNoOptions)) { Cur.Options = new List<string> { "", "", "", "" }; Cur.Correct = -1; }
        LoadCurrent();
        Changed();
    }

    private void EShortKey_Changed(object sender, TextChangedEventArgs e) { if (_loading || Cur == null) return; Cur.ShortKey = EShortKey.Text; Changed(); }

    private void Formula_Click(object sender, RoutedEventArgs e)
    {
        var target = _mathTarget ?? EText;
        var dlg = new FormulaDialog { Owner = this };
        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.Latex))
            VietInput.Insert(target, "$" + dlg.Latex.Trim() + "$");
    }

    /// <summary>Bật/tắt công cụ công thức toán. Mặc định tắt; tự bật khi câu đang mở đã có công thức.</summary>
    private void MathToggle_Click(object sender, RoutedEventArgs e) => SetMath(!_mathOn);

    private void SetMath(bool on)
    {
        _mathOn = on;
        EMathToggle.Content = on ? "∑  Ẩn công thức toán học" : "∑  Bật công thức toán học";
        EMathBar.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        UpdatePreview();
    }

    /// <summary>Xem trước câu hỏi khi đã bật công thức và câu có công thức toán.</summary>
    private void UpdatePreview()
    {
        var q = Cur;
        if (q == null) return;
        bool any = _mathOn && (RichMath.HasMath(q.Text) || (q.Type.HasOptions() && q.Options.Any(RichMath.HasMath)));
        EPreviewBox.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        if (!any) { EPreview.Content = null; return; }
        var sp = new StackPanel();
        sp.Children.Add(RichMath.Block(q.Text, 16));
        if (q.Type.HasOptions())
            for (int k = 0; k < q.Options.Count; k++)
            {
                var label = q.Type == QuestionType.TrueFalse ? $"{(char)('a' + k)}) " : $"{(char)('A' + k)}. ";
                var tb = RichMath.Block(null, 15);
                tb.Margin = new Thickness(12, 4, 0, 0);
                tb.Inlines.Add(new System.Windows.Documents.Run(label) { FontWeight = FontWeights.Bold });
                RichMath.AddTo(tb.Inlines, q.Options[k], 15);
                sp.Children.Add(tb);
            }
        EPreview.Content = sp;
    }

    private void QList_SelectionChanged(object sender, SelectionChangedEventArgs e) { LoadCurrent(); UpdateListButtons(); }

    // ===================== Thêm / xoá / đổi thứ tự câu =====================
    private void UpdateListButtons()
    {
        var x = _draft?.Exam; var q = Cur;
        if (x == null) return;
        int i = q == null ? -1 : x.Questions.IndexOf(q);
        QDelBtn.IsEnabled = q != null && x.Questions.Count > 1;
        QUpBtn.IsEnabled = i > 0 && x.Questions[i - 1].Type.IsObjective() == q!.Type.IsObjective();
        QDownBtn.IsEnabled = i >= 0 && i < x.Questions.Count - 1 && x.Questions[i + 1].Type.IsObjective() == q!.Type.IsObjective();
        QAddEssayBtn.Content = x.McQuestions.Any() ? "＋ Thêm câu tự luận" : "＋ Thêm câu trắc nghiệm";
    }

    /// <summary>Sau khi cấu trúc bộ đề đổi: dựng lại danh sách, chọn câu cần tới và lưu.</summary>
    private void AfterStructureChange(Question? select)
    {
        RebuildRows();
        var row = _rows.FirstOrDefault(r => r.Q == select) ?? _rows.FirstOrDefault();
        QList.SelectedItem = row;
        if (row != null) QList.ScrollIntoView(row);
        _packed = false;
        RefreshSide();
        UpdateListButtons();
        SaveText.Text = "Đang lưu bản nháp…";
        _saveTimer.Stop(); _saveTimer.Start();
    }

    private void AddQuestion_Click(object sender, RoutedEventArgs e)
    {
        if (_draft == null) return;
        var q = _draft.Exam.InsertAfter(Cur, objective: Cur?.Type.IsObjective() ?? true);
        AfterStructureChange(q);
        Dispatcher.BeginInvoke(() => EText.Focus(), DispatcherPriority.Input);
    }

    /// <summary>Thêm câu vào phần còn lại (tự luận, hoặc trắc nghiệm nếu bộ đề chưa có câu trắc nghiệm).</summary>
    private void AddEssay_Click(object sender, RoutedEventArgs e)
    {
        if (_draft == null) return;
        var x = _draft.Exam;
        bool objective = !x.McQuestions.Any();
        bool newPart = objective ? !x.McQuestions.Any() : !x.EssayQuestions.Any();
        var q = x.InsertAfter(null, objective);
        AfterStructureChange(q);
        if (newPart)
            MessageBox.Show(this, "Đã thêm phần mới vào bộ đề. Phần này chưa có điểm: bấm “Sửa” ở khung Thông tin bộ đề để nhập tổng điểm cho từng phần (tổng cả đề phải bằng 10).",
                "iTest-Editor", MessageBoxButton.OK, MessageBoxImage.Information);
        Dispatcher.BeginInvoke(() => EText.Focus(), DispatcherPriority.Input);
    }

    private void DeleteQuestion_Click(object sender, RoutedEventArgs e)
    {
        if (_draft == null || Cur == null) return;
        var x = _draft.Exam; var q = Cur;
        if (x.Questions.Count <= 1) return;
        int i = x.Questions.IndexOf(q);
        bool hasContent = !string.IsNullOrWhiteSpace(q.Text) || q.Image != null || q.Audio != null || q.Options.Any(o => !string.IsNullOrWhiteSpace(o) && !QuestionTypes.YesNoOptions.Contains(o));
        if (hasContent && MessageBox.Show(this, $"Xoá câu {i + 1}? Nội dung câu này sẽ mất và điểm của phần được chia đều lại cho các câu còn lại.",
                "Xoá câu hỏi", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (!x.Remove(q)) return;
        AfterStructureChange(x.Questions[Math.Min(i, x.Questions.Count - 1)]);
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => MoveCurrent(-1);
    private void MoveDown_Click(object sender, RoutedEventArgs e) => MoveCurrent(+1);

    private void MoveCurrent(int delta)
    {
        if (_draft == null || Cur == null) return;
        var q = Cur;
        if (_draft.Exam.Move(q, delta)) AfterStructureChange(q);
    }

    private void LoadCurrent()
    {
        var q = Cur;
        if (q == null) return;
        _loading = true;
        int no = _draft!.Exam.Questions.IndexOf(q) + 1;
        EBadgeText.Text = $"Câu {no} · {q.Type.Label()}";
        EBadge.Background = new SolidColorBrush(q.Type.IsObjective() ? Color.FromRgb(0xDB, 0xEA, 0xFE) : Color.FromRgb(0xED, 0xE9, 0xFE));
        ETypeRow.Visibility = q.Type.IsObjective() ? Visibility.Visible : Visibility.Collapsed;
        int ti = Array.IndexOf(ObjectiveTypes, q.Type);
        EType.SelectedIndex = ti;
        ETypeHint.Text = ti >= 0 ? TypeHints[ti] : "";
        EText.Text = q.Text;
        EPoints.Text = F(q.Points);
        EMcPanel.Visibility = q.Type.HasOptions() ? Visibility.Visible : Visibility.Collapsed;
        EShortPanel.Visibility = q.Type == QuestionType.ShortAnswer ? Visibility.Visible : Visibility.Collapsed;
        EEsPanel.Visibility = q.Type == QuestionType.Essay ? Visibility.Visible : Visibility.Collapsed;
        BuildOptionEditors(q);
        EShortKey.Text = q.ShortKey;
        EGuide.Text = q.Guide;
        _mathTarget = EText;
        // Tự bật công thức nếu câu này đã có công thức, để không che mất công cụ đang dùng.
        if (!_mathOn && (RichMath.HasMath(q.Text) || q.Options.Any(RichMath.HasMath) || RichMath.HasMath(q.Guide))) SetMath(true);
        UpdatePreview();
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
        UpdatePreview();
        ScheduleSide();
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
            _draft.Save(sync: true);
            RememberSession();
            if (!_packed && MessageBox.Show(this, "Bộ đề chưa đóng gói. Phiên làm việc đã được lưu và sẽ tự mở lại lần sau.\n\nThoát iTest-Editor?",
                    "Thoát", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        }
        else RememberSession();
        Close();
    }

    private bool _sidePending;
    /// <summary>Gom các lần gõ phím liên tục, chỉ tính lại khung bên phải khi rảnh.</summary>
    private void ScheduleSide()
    {
        if (_sidePending) return;
        _sidePending = true;
        Dispatcher.BeginInvoke(() => { _sidePending = false; RefreshSide(); }, DispatcherPriority.Background);
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
        x.EnsureTotals();
        SumParts.Text = $"Trắc nghiệm: {F(x.McQuestions.Sum(q => q.Points))}/{F(x.McTotal)}đ   ·   Tự luận: {F(x.EssayQuestions.Sum(q => q.Points))}/{F(x.EssayTotal)}đ";
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
            new { K = "Cấu trúc", V = $"{x.McCount} TN ({F(x.McTotal)}đ) + {x.EssayCount} TL ({F(x.EssayTotal)}đ)" },
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
        _draft.Exam.EnsureTotals();
        _draft.Exam.Distribute(QuestionType.MultipleChoice);
        _draft.Exam.Distribute(QuestionType.Essay);
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

    private async void Import(string filter, Func<string, List<ImportedQuestion>> reader)
    {
        if (_draft == null) return;
        var dlg = new OpenFileDialog { Filter = filter };
        if (dlg.ShowDialog(this) != true) return;
        var file = dlg.FileName;
        List<ImportedQuestion> items;
        try { items = await Busy.Run(this, "Đang đọc và phân tích file câu hỏi…", () => reader(file)); }
        catch (Exception ex) { MessageBox.Show(this, "Không đọc được file: " + ex.Message, "Nhập câu hỏi"); return; }

        var x = _draft.Exam;
        int filled = 0, skipped = 0;
        foreach (var it in items)
        {
            var slot = x.Questions.FirstOrDefault(q => q.Type.IsObjective() == it.Type.IsObjective() && string.IsNullOrWhiteSpace(q.Text) && (q.Type == QuestionType.Essay || q.Options.All(string.IsNullOrWhiteSpace)));
            if (slot == null) { skipped++; continue; }
            slot.Type = it.Type;
            slot.Text = it.Text;
            if (it.Type.HasOptions())
            {
                slot.Options = it.Options.Take(4).ToList();
                while (it.Type != QuestionType.YesNo && slot.Options.Count < 4) slot.Options.Add("");
                slot.Correct = it.Correct;
                slot.Corrects = it.Corrects.ToList();
                slot.TfKey = it.TfKey.ToList();
            }
            if (it.Type == QuestionType.ShortAnswer) slot.ShortKey = it.ShortKey;
            if (it.Type == QuestionType.Essay && it.ShortKey.Length > 0) slot.Guide = it.ShortKey;
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

    private async void ExportPdf_Click(object sender, RoutedEventArgs e)
    {
        if (_draft == null) return;
        SaveNow();
        var x = _draft.Exam;
        var opt = new PdfDialog(x) { Owner = this };
        if (opt.ShowDialog() != true) return;
        var name = string.Join("_", $"BoDeThiMau_{x.Subject}".Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Replace(' ', '_');
        var dlg = new SaveFileDialog { Filter = "PDF (*.pdf)|*.pdf", FileName = name + ".pdf" };
        if (dlg.ShowDialog(this) != true) return;
        var path = dlg.FileName; var media = _draft.MediaFolder; var options = opt.Options;
        try
        {
            // Vẽ công thức trên luồng giao diện (bắt buộc), tạo PDF ở luồng nền.
            var prepared = ExamPdf.RenderFormulas(x);
            await Busy.Run(this, "Đang tạo đề PDF…", () => ExamPdf.Export(path, x, media, options, prepared));
            if (MessageBox.Show(this, "Đã xuất đề thi:\n" + path + "\n\nMở file ngay?", "iTest-Editor", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Không xuất được PDF: " + ex.Message, "iTest-Editor", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Template_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Filter = "Excel (*.xlsx)|*.xlsx", FileName = "Mau_NhapCauHoi_iTest.xlsx" };
        if (dlg.ShowDialog(this) != true) return;
        var path = dlg.FileName;
        try { await Busy.Run(this, "Đang tạo file Excel mẫu…", () => Importer.WriteExcelTemplate(path)); MessageBox.Show(this, "Đã tạo file mẫu:\n" + path, "iTest-Editor"); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "iTest-Editor"); }
    }

    private async void Pack_Click(object sender, RoutedEventArgs e)
    {
        if (_draft == null) return;
        var x = _draft.Exam;
        string Ascii(string s) => new string(s.Normalize(System.Text.NormalizationForm.FormD)
            .Where(ch => CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark).ToArray())
            .Replace('đ', 'd').Replace('Đ', 'D').Replace(' ', '_');
        var dlg = new SaveFileDialog { Filter = "Bộ đề iTest (*.itz)|*.itz", FileName = Ascii($"{x.Subject}_{x.ExamName}").ToUpperInvariant() + ".itz" };
        if (dlg.ShowDialog(this) != true) return;
        var path = dlg.FileName; var pass = _draft.Locked ? _draft.Password : null; var draft = _draft;
        try
        {
            x.CreatedAt = DateTime.Now;
            // Chỉ đóng gói ảnh/âm thanh mà câu hỏi còn dùng (file đã gỡ khỏi câu hỏi không được lọt vào bộ đề).
            var used = x.Questions.SelectMany(q => new[] { q.Image, q.Audio }).Where(n => n != null).ToHashSet();
            await Busy.Run(this, "Đang mã hoá và đóng gói bộ đề…", () =>
                ExamPackage.Save(path, x, draft.LoadMedia().Where(kv => used.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value), pass));
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
    public Brush HeaderBrush => Q.Type.IsObjective() ? new SolidColorBrush(Color.FromRgb(0x1E, 0x3A, 0x8A)) : new SolidColorBrush(Color.FromRgb(0x5B, 0x21, 0xB6));
}

public sealed class DraftItem
{
    public Draft Draft { get; }
    public DraftItem(Draft d) => Draft = d;
    public string Title => $"{Draft.Exam.Subject} · {Draft.Exam.ExamName}";
    public string Meta => $"đủ {Draft.Exam.Questions.Count(q => q.Missing().Count == 0)}/{Draft.Exam.Questions.Count} câu · lưu lúc {Draft.SavedAt:HH:mm dd/MM}";
}
