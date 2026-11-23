using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using ITest.Core;
using ITest.UI;
using Microsoft.Win32;

namespace ITest.Server;

public partial class MainWindow : Window
{
    private RoomService? _room;
    private string? _pkgPath;
    private ExamPackage.Content? _pkg;
    private ICollectionView? _view;
    private readonly string _settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "iTest-Server", "settings.json");

    private static readonly (string key, string label)[] Filters =
    {
        ("all", "Tất cả"), ("Waiting", "Chờ phát đề"), ("Doing", "Đang làm bài"), ("Done", "Đã nộp bài"),
        ("Lost", "Mất kết nối"), ("Moving", "Đang chuyển máy"), ("Revoked", "Đình chỉ")
    };

    public MainWindow()
    {
        InitializeComponent();
        HostLine.Text = $"Máy {Environment.MachineName} · {NetUtil.LocalIPv4()} · Workgroup {NetUtil.GetWorkgroup()}";
        LoadSettings();
        foreach (var f in Filters) FilterBox.Items.Add(new ComboBoxItem { Content = f.label, Tag = f.key });
        FilterBox.SelectedIndex = 0;
        Closing += OnClosing;
        SetupPanel.Visibility = Visibility.Collapsed;
        StartPeerWatch();
    }

    // ================== Chỉ một máy chủ trong mạng LAN ==================
    private BeaconListener? _peers;
    private RoomBeacon? _peer;
    private DateTime _peerSeen;
    private readonly HashSet<string> _peerReported = new();
    private readonly HashSet<string> _peerNotified = new();
    private readonly HashSet<string> _ownIds = new();

    /// <summary>Lắng nghe gói quảng bá của iTest-Server khác cùng workgroup suốt thời gian chạy.</summary>
    private async void StartPeerWatch()
    {
        try
        {
            _peers = new BeaconListener();
            _peers.Found += b => Dispatcher.BeginInvoke(() => OnPeer(b));
            _peers.DuplicateReported += b => Dispatcher.BeginInvoke(() => OnDuplicateReported(b));
            _peers.Start();
        }
        catch { /* không nghe được UDP: bỏ qua kiểm tra */ }
        await Task.Delay(3500);   // máy chủ đang mở phòng quảng bá mỗi 2 giây
        ScanPanel.Visibility = Visibility.Collapsed;
        if (!PeerActive()) { Anim.Show(SetupPanel); CheckRecovery(); }
    }

    private bool PeerActive() => _peer != null && DateTime.Now - _peerSeen < TimeSpan.FromSeconds(8);

    private void OnPeer(RoomBeacon b)
    {
        if (_room?.ServerId is { } id) _ownIds.Add(id);
        if (_ownIds.Contains(b.ServerId)) return;   // gói của chính máy này (kể cả phòng vừa đóng)
        _peer = b; _peerSeen = DateTime.Now;
        if (_room != null)
        {
            // Đang mở phòng mà có máy chủ khác cũng mở phòng (hai máy mở cùng lúc)
            if (_peerReported.Add(b.ServerId))
                _room.Log("CẢNH BÁO", b.Host, $"Máy {b.Host} ({b.Address}) cũng đang mở phòng thi “{b.Room}” trong cùng mạng — thí sinh có thể vào nhầm phòng");
            return;
        }
        if (_ignorePeer) return;   // giám thị đã chọn "Vẫn mở phòng thi"
        Block(b);
    }

    /// <summary>Máy khác báo: nó vừa mở iTest-Server trong khi phòng thi của máy này đang mở.</summary>
    private void OnDuplicateReported(RoomBeacon b)
    {
        if (_room == null || b.ServerId != _room.ServerId) return;
        if (!_dupLogged.Add(b.Host + "|" + b.Address)) return;
        _room.Log("CẢNH BÁO", b.Host, $"Máy {b.Host} ({b.Address}) vừa mở iTest-Server trong cùng mạng khi phòng thi này đang mở");
    }
    private readonly HashSet<string> _dupLogged = new();

    /// <summary>Chặn máy này và báo cho máy chủ đang mở phòng biết.</summary>
    private void Block(RoomBeacon b)
    {
        BlockText.Text = $"Máy {b.Host} ({b.Address}) đang mở phòng thi “{b.Room}” · {b.Exam}" +
                         (string.IsNullOrWhiteSpace(b.Proctor) ? "" : $" · giám thị {b.Proctor}") + $" · {b.Candidates} thí sinh.";
        BlockNote.Text = "Nên dùng đúng một máy chủ cho mỗi phòng. Nếu máy kia đã đóng, bấm “Kiểm tra lại”. "
                       + "Nếu máy này mới là máy chủ đúng, bấm “Vẫn mở phòng thi” — thí sinh vẫn chỉ vào đúng máy nhờ mã phòng bạn đọc.";
        SetupPanel.Visibility = Visibility.Collapsed;
        ScanPanel.Visibility = Visibility.Collapsed;
        if (BlockPanel.Visibility != Visibility.Visible) Anim.Show(BlockPanel);
        if (_peerNotified.Add(b.ServerId))
        {
            var addr = b.Address; var id = b.ServerId;
            Task.Run(() => NetUtil.SendDuplicateNotice(addr, id));
        }
    }

    private bool _ignorePeer;

    /// <summary>Giám thị chủ động mở phòng trên máy này dù phát hiện máy chủ khác (máy này mới là máy chủ đúng).</summary>
    private void ForceOpen_Click(object sender, RoutedEventArgs e)
    {
        _ignorePeer = true;
        BlockPanel.Visibility = Visibility.Collapsed;
        ScanPanel.Visibility = Visibility.Collapsed;
        Anim.Show(SetupPanel);
        CheckRecovery();
    }

    private async void BlockRetry_Click(object sender, RoutedEventArgs e)
    {
        BlockRetry.IsEnabled = false;
        BlockRetry.Content = "Đang kiểm tra…";
        _peer = null;
        await Task.Delay(4500);
        BlockRetry.IsEnabled = true;
        BlockRetry.Content = "Kiểm tra lại";
        if (PeerActive()) return;
        BlockPanel.Visibility = Visibility.Collapsed;
        Anim.Show(SetupPanel);
        CheckRecovery();
    }

    // ================== Khôi phục sau sự cố ==================
    private RoomState? _recover;

    private void CheckRecovery()
    {
        _recover = RoomService.FindUnclosed();
        if (_recover == null) { RecoverBox.Visibility = Visibility.Collapsed; return; }
        RecoverText.Text = $"{_recover.Title}.\nBộ đề: {Path.GetFileName(_recover.PackagePath)}. Khôi phục để các máy thí sinh tự kết nối lại và làm tiếp từ bài đã lưu.";
        Anim.Show(RecoverBox);
    }

    private void DismissRecover_Click(object sender, RoutedEventArgs e)
    {
        if (_recover == null) return;
        if (MessageBox.Show(this, "Bỏ qua phòng thi này? Bản sao lưu bài làm vẫn được giữ trong thư mục:\n" + _recover.BackupDir,
                "Khôi phục", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        RoomService.Dismiss(_recover);
        _recover = null;
        RecoverBox.Visibility = Visibility.Collapsed;
    }

    private async void Recover_Click(object sender, RoutedEventArgs e)
    {
        if (_recover == null) return;
        if (!_ignorePeer && PeerActive()) { Block(_peer!); return; }
        var st = _recover;
        if (!File.Exists(st.PackagePath))
        {
            MessageBox.Show(this, "Không tìm thấy file bộ đề:\n" + st.PackagePath + "\n\nHãy chọn lại đúng file bộ đề đã dùng.", "Khôi phục", MessageBoxButton.OK, MessageBoxImage.Warning);
            var dlg = new OpenFileDialog { Filter = "Bộ đề iTest (*.itz)|*.itz" };
            if (dlg.ShowDialog(this) != true) return;
            st.PackagePath = dlg.FileName;
        }
        ExamPackage.Content pkg;
        try
        {
            string? pw = null;
            if (ExamPackage.IsPasswordProtected(st.PackagePath))
            {
                pw = Prompt.Show(this, "Khôi phục phòng thi", "Nhập mật khẩu bộ đề " + Path.GetFileName(st.PackagePath) + ":", "", password: true);
                if (pw == null) return;
            }
            var path = st.PackagePath;
            pkg = await Busy.Run(this, "Đang mở lại bộ đề để khôi phục…", () => ExamPackage.Load(path, pw));
        }
        catch (CryptographicException) { MessageBox.Show(this, "Sai mật khẩu bộ đề.", "Khôi phục", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        catch (Exception ex) { MessageBox.Show(this, "Không mở được bộ đề: " + ex.Message, "Khôi phục", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

        _room = new RoomService(Dispatcher);
        _room.LoadPackage(pkg, st.PackagePath);
        try { _room.Restore(st); }
        catch (InvalidDataException ex) { MessageBox.Show(this, ex.Message, "Khôi phục", MessageBoxButton.OK, MessageBoxImage.Warning); _room.Dispose(); _room = null; return; }
        catch (Exception ex) { MessageBox.Show(this, "Không mở được cổng mạng (có thể iTest-Server khác đang chạy): " + ex.Message, "Khôi phục"); _room.Dispose(); _room = null; return; }
        _pkg = pkg; _pkgPath = st.PackagePath;
        _recover = null;
        RecoverBox.Visibility = Visibility.Collapsed;
        foreach (ComboBoxItem it in WindowBox.Items) if (it.Tag?.ToString() == _room.SubmitWindowMinutes.ToString()) WindowBox.SelectedItem = it;
        ShowScoreBox.IsChecked = _room.ShowScore;
        EnterRoom();
    }

    // ================== Thiết lập ==================
    private void LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var s = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_settingsPath)) ?? new();
                DeptBox.Text = s.GetValueOrDefault("dept", "");
                SchoolBox.Text = s.GetValueOrDefault("school", "");
                RoomBox.Text = s.GetValueOrDefault("room", "");
                ProctorBox.Text = s.GetValueOrDefault("proctor", "");
            }
        }
        catch { }
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(new Dictionary<string, string>
            { ["dept"] = DeptBox.Text, ["school"] = SchoolBox.Text, ["room"] = RoomBox.Text, ["proctor"] = ProctorBox.Text }));
        }
        catch { }
    }

    private void Pick_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Bộ đề iTest (*.itz)|*.itz", Title = "Chọn bộ đề" };
        if (dlg.ShowDialog(this) != true) return;
        SelectPackage(dlg.FileName);
    }

    private void SelectPackage(string path)
    {
        SetupError.Text = "";
        _pkg = null; _pkgPath = path;
        try
        {
            bool locked = ExamPackage.IsPasswordProtected(path);
            FileName.Text = Path.GetFileName(path);
            FileLock.Text = locked ? "Bộ đề có mật khẩu · nhập mật khẩu để mở" : "Bộ đề không đặt mật khẩu";
            PassRow.Visibility = locked ? Visibility.Visible : Visibility.Collapsed;
            PkgFileInfo.ItemsSource = null;
            Anim.Show(FileBox);
            PickBtn.Content = "⬆  Đã chọn · bấm để chọn bộ đề khác";
            if (!locked) _ = Unlock(null);
        }
        catch (Exception ex) { SetupError.Text = ex.Message; }
    }

    private void Unlock_Click(object sender, RoutedEventArgs e) => _ = Unlock(PassBox.Password);

    private bool _unlocking;
    /// <summary>Giải mã và đọc bộ đề ở luồng nền (tránh treo giao diện), có thanh tiến trình.</summary>
    private async Task Unlock(string? password)
    {
        if (_pkgPath == null || _unlocking) return;
        _unlocking = true;
        var path = _pkgPath;
        SetupError.Text = "";
        PkgFileInfo.ItemsSource = null;
        PkgLoadingText.Text = "Đang giải mã và đọc bộ đề…";
        PkgLoading.Visibility = Visibility.Visible;
        try
        {
            var pkg = await Task.Run(() => ExamPackage.Load(path, password));
            if (_pkgPath != path) return;   // người dùng đã đổi file khác trong lúc chờ
            _pkg = pkg;
            PkgFileInfo.ItemsSource = InfoRows(pkg.Exam);
            FileLock.Text = "✓ Đã mở khoá · chữ ký hợp lệ";
            PassRow.Visibility = Visibility.Collapsed;
            SetupError.Text = "";
        }
        catch (CryptographicException) { SetupError.Text = "Sai mật khẩu hoặc file bộ đề đã bị chỉnh sửa."; }
        catch (Exception ex) { SetupError.Text = "Không mở được bộ đề: " + ex.Message; }
        finally { PkgLoading.Visibility = Visibility.Collapsed; _unlocking = false; }
    }

    private static List<object> InfoRows(ExamSet x) => new()
    {
        new { K = "Kỳ thi", V = x.ExamName }, new { K = "Năm học", V = x.SchoolYear }, new { K = "Môn thi", V = x.Subject },
        new { K = "Thời gian", V = $"{x.Minutes} phút" }, new { K = "Số câu", V = $"{x.Questions.Count} câu ({x.McQuestions.Count()} TN, {x.EssayQuestions.Count()} TL)" },
        new { K = "Tổng điểm", V = $"{x.TotalPoints:0.##} điểm" }, new { K = "Số mã đề", V = x.Codes.ToString() },
        new { K = "Tài liệu", V = x.AllowDocuments ? "Được sử dụng" : "Không được sử dụng" },
        new { K = "Người soạn", V = string.IsNullOrEmpty(x.Unit) ? x.Author : $"{x.Author} · {x.Unit}" }
    };

    private void OpenRoom_Click(object sender, RoutedEventArgs e)
    {
        var miss = new List<string>();
        if (string.IsNullOrWhiteSpace(DeptBox.Text)) miss.Add("Sở GD&ĐT");
        if (string.IsNullOrWhiteSpace(SchoolBox.Text)) miss.Add("trường");
        if (string.IsNullOrWhiteSpace(RoomBox.Text)) miss.Add("phòng thi");
        if (string.IsNullOrWhiteSpace(ProctorBox.Text)) miss.Add("giám thị");
        if (_pkgPath == null) miss.Add("bộ đề");
        if (miss.Count > 0) { SetupError.Text = "Vui lòng nhập/chọn: " + string.Join(", ", miss) + "."; return; }
        if (_pkg == null) { SetupError.Text = "Bộ đề có mật khẩu. Vui lòng nhập mật khẩu và bấm “Mở khoá”."; return; }
        SaveSettings();
        _room = new RoomService(Dispatcher)
        {
            Department = DeptBox.Text.Trim(), School = SchoolBox.Text.Trim(), Room = RoomBox.Text.Trim(), Proctor = ProctorBox.Text.Trim()
        };
        _room.LoadPackage(_pkg, _pkgPath!);
        _room.SubmitWindowMinutes = SelectedWindow();
        _room.ShowScore = ShowScoreBox.IsChecked == true;
        if (!_ignorePeer && PeerActive()) { _room.Dispose(); _room = null; Block(_peer!); return; }
        try { _room.Open(); }
        catch (Exception ex) { SetupError.Text = "Không mở được cổng mạng (có thể đang có iTest-Server khác chạy): " + ex.Message; _room.Dispose(); _room = null; return; }
        EnterRoom();
    }

    private void EnterRoom()
    {
        if (_room == null) return;
        _room.Changed += ScheduleRefresh;
        _view = CollectionViewSource.GetDefaultView(_room.Candidates);
        _view.Filter = FilterCandidate;
        Table.ItemsSource = _view;
        Tiles.ItemsSource = _view;
        FeedList.ItemsSource = _room.Feed;
        ShowPackage();
        SetupPanel.Visibility = Visibility.Collapsed;
        Anim.Show(MainPanel);
        Refresh();
    }

    private void ShowPackage()
    {
        if (_room == null) return;
        PkgName.Text = Path.GetFileName(_room.PackagePath);
        PkgAuthor.Text = "Người soạn: " + _room.Exam.Author + (string.IsNullOrEmpty(_room.Exam.Unit) ? "" : " · " + _room.Exam.Unit);
        PkgInfo.ItemsSource = InfoRows(_room.Exam).Take(8).ToList();
        ExamTitle.Text = $"{_room.Exam.ExamName} – {_room.Exam.Subject}";
        RoomLine.Text = $"{_room.School} · {_room.Room} · Giám thị: {_room.Proctor}";
        PinText.Text = _room.RoomPin;
    }

    // ================== Làm mới giao diện ==================
    // Mọi thay đổi được gộp lại và vẽ lại tối đa một lần mỗi khung hình (ưu tiên thấp) để tránh giật khi nhiều máy cùng gửi dữ liệu.
    private bool _refreshPending;
    private string _discSig = "", _countSig = "";

    private void ScheduleRefresh()
    {
        if (_refreshPending) return;
        _refreshPending = true;
        Dispatcher.BeginInvoke(() => { _refreshPending = false; Refresh(); }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void Refresh()
    {
        if (_room == null) return;
        var r = _room;
        int secs = r.Phase == Phase.Open ? r.Exam.Minutes * 60 : r.SecondsLeft;
        TimerText.Text = RoomService.Fmt(secs);
        TimerSub.Text = $"còn lại / {r.Exam.Minutes} phút";
        PhaseText.Text = r.Phase switch
        {
            Phase.Open => "ĐÃ MỞ PHÒNG · CHỜ PHÁT ĐỀ",
            Phase.Running => r.Paused ? "TẠM DỪNG" : "ĐANG THI",
            _ => r.Exported ? "ĐÃ XUẤT KẾT QUẢ · CÓ THỂ ĐÓNG PHÒNG" : "ĐÃ THU BÀI · CHỜ XUẤT KẾT QUẢ"
        };
        StartBtn.Visibility = r.Phase == Phase.Open ? Visibility.Visible : Visibility.Collapsed;
        PauseBtn.Visibility = AddAllBtn.Visibility = NoticeAllBtn.Visibility = r.Phase == Phase.Running ? Visibility.Visible : Visibility.Collapsed;
        PauseBtn.Content = r.Paused ? "Tiếp tục" : "Tạm dừng";
        CollectBtn.IsEnabled = r.CanCollectAll;
        int wait = r.SecondsLeft - r.SubmitWindowMinutes * 60;
        CollectBtn.Content = r.Phase == Phase.Collected ? "✓ Đã thu bài" : r.Phase == Phase.Running && wait > 0 ? $"Thu bài · mở sau {RoomService.Fmt(wait)}" : "Thu bài tất cả";
        bool canClose = r.Phase == Phase.Open || r.Exported;
        CloseBtn.IsEnabled = canClose;
        CloseBtn.ToolTip = r.Phase == Phase.Open ? "Đóng phòng khi chưa phát đề; các máy thí sinh sẽ tự thoát"
            : r.Exported ? "Đóng phòng và mở khoá các máy thí sinh" : "Chỉ đóng được sau khi đã xuất kết quả";
        SwapBtn.Visibility = r.Phase == Phase.Open ? Visibility.Visible : Visibility.Collapsed;
        WindowBox.IsEnabled = r.Phase == Phase.Open;

        int total = r.Candidates.Count;
        ListTitle.Text = $"Danh sách thí sinh · {total} máy";
        FeedTitle.Text = $"Hoạt động trực tiếp · {r.Feed.Count} sự kiện";
        var disc = r.Candidates.Where(c => c.WarnedAt != null || c.RevokedAt != null).ToList();
        var sig = string.Join("|", disc.Select(c => c.Sbd + c.Discipline));
        if (sig != _discSig) { _discSig = sig; DiscList.ItemsSource = disc; }
        DiscTitle.Text = $"ĐÃ NHẮC NHỞ / THU BÀI · {disc.Count}";
        EmptyHint.Visibility = total == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateFilterCounts();
        UpdateSelection();

        BackupLine.Text = r.Phase == Phase.Collected ? "Đã sao lưu bài làm"
            : r.Phase == Phase.Open ? "Sao lưu tự động sẽ bắt đầu sau khi phát đề"
            : $"Sao lưu tự động sau {30 - r.TickCount % 30} giây · {r.BackupDir}";
        BackupBar.Value = r.Phase == Phase.Running ? r.TickCount % 30 : 0;
        ExportBtn.IsEnabled = r.Phase == Phase.Collected;
        SchoolExportBtn.IsEnabled = r.Phase == Phase.Collected;
        ExportBtn.Content = r.Exported ? "✓ Đã xuất · Xuất lại" : "⬇  Xuất kết quả";
        ExportHint.Text = r.Phase == Phase.Collected
            ? "Gói .zip gồm: bảng điểm và phân tích đề (Excel), bài tự luận đánh phách (PDF), biên bản phòng thi có chỗ ký (PDF), toàn bộ bài làm và nhật ký."
            : "Có thể xuất sau khi đã thu bài tất cả thí sinh.";
        if (r.Phase == Phase.Collected && Preview.Visibility != Visibility.Visible) { BuildPreview(); Anim.Show(Preview); }
    }

    private void BuildPreview()
    {
        if (_room == null) return;
        var r = _room;
        var cands = r.Candidates.ToList();
        ReportText.Text = $"Có mặt: {cands.Count(c => c.Status != CandStatus.Connected)} · Đã thu: {cands.Count(c => c.Status == CandStatus.Done)} · Vi phạm: {cands.Count(c => c.WarnedAt != null || c.RevokedAt != null)}\n" +
                          $"Bắt đầu: {r.StartedAt:HH:mm} · Kết thúc: {r.EndedAt:HH:mm} · Ô ký: Giám thị 1, Giám thị 2, Trưởng điểm thi";
        var stats = Scoring.Analyze(r.Exam, r.Layouts, cands.Select(c => c.Sheet)).Where(s => s.Answered > 0).OrderBy(s => s.CorrectRate).Take(4).ToList();
        AnalysisText.Text = stats.Count == 0 ? "Chưa có dữ liệu." : string.Join("\n", stats.Select(s =>
        {
            var q = r.Exam.Questions.First(x => x.Id == s.QuestionId);
            int wrong = Enumerable.Range(0, s.OptionCounts.Length).Where(k => k != q.Correct).OrderByDescending(k => s.OptionCounts[k]).FirstOrDefault();
            return $"Câu {s.QuestionId}: đúng {s.CorrectRate:P0} · nhiễu {(char)('A' + wrong)} ({s.OptionCounts[wrong]} lượt)";
        }));
    }

    private void UpdateFilterCounts()
    {
        if (_room == null) return;
        var sig = string.Join(",", _room.Candidates.Select(c => (int)c.Status));
        if (sig == _countSig) return;
        _countSig = sig;
        foreach (ComboBoxItem it in FilterBox.Items)
        {
            var key = (string)it.Tag;
            var label = Filters.First(f => f.key == key).label;
            int n = key == "all" ? _room.Candidates.Count : _room.Candidates.Count(c => c.Status.ToString() == key);
            it.Content = $"{label} ({n})";
        }
        // Chỉ lọc lại danh sách khi đang bật bộ lọc trạng thái (trạng thái vừa thay đổi)
        if ((FilterBox.SelectedItem as ComboBoxItem)?.Tag as string != "all") _view?.Refresh();
    }

    private bool FilterCandidate(object o)
    {
        if (o is not Candidate c) return false;
        var key = (FilterBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";
        if (key != "all" && c.Status.ToString() != key) return false;
        var q = SearchBox.Text.Trim();
        if (q.Length > 0 && !($"{c.FullName} {c.Sbd} {c.Machine}".Contains(q, StringComparison.CurrentCultureIgnoreCase))) return false;
        return true;
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e) => _view?.Refresh();
    private void Search_Changed(object sender, TextChangedEventArgs e) => _view?.Refresh();

    private void View_Changed(object sender, RoutedEventArgs e)
    {
        if (Table == null || Tiles == null) return;
        bool grid = ViewGrid.IsChecked == true;
        Table.Visibility = grid ? Visibility.Collapsed : Visibility.Visible;
        Tiles.Visibility = grid ? Visibility.Visible : Visibility.Collapsed;
    }

    // ================== Chọn thí sinh ==================
    private Candidate? _sel;
    private void Table_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (Table.SelectedItem is Candidate c) { _sel = c; UpdateSelection(); } }
    private void Tiles_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (Tiles.SelectedItem is Candidate c) { _sel = c; UpdateSelection(); } }
    private void Disc_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (DiscList.SelectedItem is Candidate c) { _sel = c; UpdateSelection(); } }

    private void UpdateSelection()
    {
        var r = _room; var c = _sel;
        bool running = r?.Phase == Phase.Running;
        SelLine.Text = c == null ? "Chọn một thí sinh trong danh sách." : $"{c.Machine} · {c.FullName} · SBD {c.Sbd} · {c.StatusText} · còn {c.Left}";
        WarnBtn.IsEnabled = c != null && running && c.WarnedAt == null && c.IsActive;
        RevokeBtn.IsEnabled = c != null && running && c.WarnedAt != null && c.RevokedAt == null && c.IsActive;
        MoveBtn.IsEnabled = c != null && running && (c.Status is CandStatus.Doing or CandStatus.Lost);
        ExtraBtn.IsEnabled = c != null && running && c.IsActive;
        EditReqBtn.IsEnabled = c != null && r?.Phase == Phase.Open && c.Conn != null && !c.EditRequested;
        EditReqBtn.Content = c != null && c.EditRequested ? "✓ Đã gửi yêu cầu sửa thông tin" : "Yêu cầu sửa thông tin";
        WarnBtn.Content = c?.WarnedAt != null ? $"Đã nhắc ({c.WarnedAt})" : "Nhắc nhở";
        KickBtn.IsEnabled = c != null && c.Conn != null && !c.IsActive;
        HandOutBtn.Visibility = c != null && r?.CanHandOut(c) == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void HandOut_Click(object sender, RoutedEventArgs e)
    {
        if (_room == null || _sel == null || !_room.CanHandOut(_sel)) return;
        _room.HandOutTo(_sel);
        UpdateSelection();
    }

    // ================== Nút điều khiển ==================
    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_room == null) return;
        if (_room.Candidates.Count(c => c.Status == CandStatus.Waiting) == 0 &&
            MessageBox.Show(this, "Chưa có thí sinh nào trong phòng chờ. Vẫn phát đề?", "Phát đề", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        _room.Start();
    }

    private void Pause_Click(object sender, RoutedEventArgs e) => _room?.TogglePause();
    private void AddAll_Click(object sender, RoutedEventArgs e) => _room?.AddMinutesAll(5);

    private void NoticeAll_Click(object sender, RoutedEventArgs e)
    {
        if (_room == null) return;
        var text = Prompt.Show(this, "Nhắc nhở chung", "Nội dung gửi tới tất cả máy thí sinh:", $"Còn {Math.Max(1, _room.SecondsLeft / 60)} phút, các em kiểm tra lại bài làm.");
        if (!string.IsNullOrWhiteSpace(text)) _room.NoticeAll(text);
    }

    private void Collect_Click(object sender, RoutedEventArgs e)
    {
        if (_room == null) return;
        if (MessageBox.Show(this, "Thu bài tất cả thí sinh? Thí sinh sẽ không thể sửa bài nữa.", "Thu bài", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            _room.CollectAll();
    }

    private void Warn_Click(object sender, RoutedEventArgs e)
    {
        if (_room == null || _sel == null) return;
        var text = Prompt.Show(this, "Nhắc nhở riêng", $"Nội dung nhắc {_sel.FullName} ({_sel.Machine}):", "Giữ trật tự, không trao đổi bài. Đây là lần nhắc nhở duy nhất.");
        if (!string.IsNullOrWhiteSpace(text)) _room.Warn(_sel, text);
    }

    private void Revoke_Click(object sender, RoutedEventArgs e)
    {
        if (_room == null || _sel == null) return;
        if (MessageBox.Show(this, $"Thu bài và huỷ kết quả của {_sel.FullName} (SBD {_sel.Sbd})?", "Thu bài · huỷ kết quả", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            _room.Revoke(_sel);
    }

    private void Move_Click(object sender, RoutedEventArgs e)
    {
        if (_room == null || _sel == null) return;
        if (MessageBox.Show(this, $"Cho phép {_sel.FullName} chuyển sang máy khác?\n\nThí sinh mở iTest trên máy dự phòng, chọn phòng và nhập đúng SBD {_sel.Sbd}. Bài làm và thời gian còn lại được giữ nguyên.",
                "Chuyển máy", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            _room.ApproveMove(_sel);
    }

    private void Kick_Click(object sender, RoutedEventArgs e)
    {
        if (_room == null || _sel == null) return;
        if (_sel.IsActive) { MessageBox.Show(this, "Thí sinh đang làm bài: chỉ có thể chuyển máy, không ngắt kết nối.", "Ngắt kết nối"); return; }
        if (MessageBox.Show(this, $"Ngắt kết nối {_sel.Machine} ({_sel.FullName})? iTest trên máy đó sẽ tự thoát.", "Ngắt kết nối", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _room.Disconnect(_sel);
        _sel = null;
        Refresh();
    }

    private void Quit_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Thoát iTest-Server?", "Thoát", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) Close();
    }

    private void Extra_Click(object sender, RoutedEventArgs e) { if (_room != null && _sel != null) _room.AddMinutes(_sel, 5); }

    private void EditReq_Click(object sender, RoutedEventArgs e)
    {
        if (_room == null || _sel == null) return;
        var reason = Prompt.Show(this, "Yêu cầu sửa thông tin", $"Lý do gửi cho {_sel.FullName}:", "Thông tin không khớp với danh sách phòng thi, vui lòng kiểm tra lại.");
        if (!string.IsNullOrWhiteSpace(reason)) _room.RequestEdit(_sel, reason);
    }

    private async void Swap_Click(object sender, RoutedEventArgs e)
    {
        if (_room == null || _room.Phase != Phase.Open) return;
        var dlg = new OpenFileDialog { Filter = "Bộ đề iTest (*.itz)|*.itz", Title = "Đổi bộ đề" };
        if (dlg.ShowDialog(this) != true) return;
        var file = dlg.FileName;
        try
        {
            string? pass = null;
            if (ExamPackage.IsPasswordProtected(file))
            {
                pass = Prompt.Show(this, "Mật khẩu bộ đề", $"Nhập mật khẩu của {Path.GetFileName(file)}:", "", password: true);
                if (pass == null) return;
            }
            var pkg = await Busy.Run(this, "Đang giải mã và đọc bộ đề…", () => ExamPackage.Load(file, pass));
            var old = Path.GetFileName(_room.PackagePath);
            _room.LoadPackage(pkg, file);
            _room.Log("THÔNG TIN", "", $"Đổi bộ đề: {old} → {Path.GetFileName(file)} · {pkg.Exam.Minutes} phút · {pkg.Exam.Questions.Count} câu · {pkg.Exam.Codes} mã đề");
            ShowPackage();
            Refresh();
        }
        catch (CryptographicException) { MessageBox.Show(this, "Sai mật khẩu hoặc file bộ đề đã bị chỉnh sửa.", "Đổi bộ đề"); }
        catch (Exception ex) { MessageBox.Show(this, "Không mở được bộ đề: " + ex.Message, "Đổi bộ đề"); }
    }

    private int SelectedWindow() => WindowBox.SelectedItem is ComboBoxItem it && int.TryParse(it.Tag as string, out var m) ? m : 15;
    private void Window_Changed(object sender, SelectionChangedEventArgs e) { if (_room != null) { _room.SubmitWindowMinutes = SelectedWindow(); Refresh(); } }
    private void ShowScore_Changed(object sender, RoutedEventArgs e) { if (_room != null) _room.ShowScore = ShowScoreBox.IsChecked == true; }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_room == null || _room.Phase != Phase.Collected) return;
        var dlg = new SaveFileDialog
        {
            Filter = "Gói kết quả (*.zip)|*.zip",
            FileName = $"KetQua_{_room.Room.Replace(' ', '_')}_{DateTime.Now:yyyyMMdd_HHmm}.zip",
            Title = "Xuất kết quả"
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            ExportBtn.IsEnabled = false;
            ExportBtn.Content = "Đang đóng gói kết quả…";
            var room = _room;
            var path = dlg.FileName;
            Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
            await Task.Run(() => Exporter.ExportZip(room, path));
            _room.Exported = true;
            _room.Log("XUẤT FILE", "", $"Đã xuất {Path.GetFileName(dlg.FileName)} · bảng điểm, bài tự luận, biên bản, phân tích đề");
            MessageBox.Show(this, "Đã xuất kết quả:\n" + dlg.FileName, "Xuất kết quả", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { MessageBox.Show(this, "Lỗi khi xuất kết quả: " + ex.Message, "Xuất kết quả", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { Mouse.OverrideCursor = null; Refresh(); }
    }

    private void SchoolExport_Click(object sender, RoutedEventArgs e)
    {
        if (_room == null || _room.Phase != Phase.Collected) return;
        SchoolExport.Run(this, _room);
    }

    private void CloseRoom_Click(object sender, RoutedEventArgs e)
    {
        if (_room == null || !(_room.Phase == Phase.Open || _room.Exported)) return;
        var msg = _room.Phase == Phase.Open
            ? $"Đóng phòng khi chưa phát đề?\n\n{_room.Candidates.Count} máy thí sinh đang trong phòng chờ sẽ tự thoát iTest."
            : "Đóng phòng thi và mở khoá tất cả máy thí sinh?";
        if (MessageBox.Show(this, msg, "Đóng phòng", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _room.Changed -= ScheduleRefresh;
        _room.Close();
        _room = null; _sel = null; _pkg = null; _pkgPath = null;
        FileBox.Visibility = Visibility.Collapsed;
        PickBtn.Content = "⬆  Chọn file bộ đề (.itz) đóng gói từ iTest-Editor";
        Preview.Visibility = Visibility.Collapsed;
        MainPanel.Visibility = Visibility.Collapsed;
        Anim.Show(SetupPanel);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_room == null) return;
        if (_room.Phase == Phase.Running)
        {
            MessageBox.Show(this, "Phòng thi đang diễn ra. Hãy thu bài và xuất kết quả trước khi thoát.", "iTest-Server");
            e.Cancel = true; return;
        }
        if (_room.Phase == Phase.Collected && !_room.Exported &&
            MessageBox.Show(this, "Chưa xuất kết quả. Bài làm đã được sao lưu tại:\n" + _room.BackupDir + "\n\nVẫn thoát?", "iTest-Server", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
        { e.Cancel = true; return; }
        _room.Backup();
        _room.ReleaseAllAndDispose();
    }
}

/// <summary>Hộp thoại nhập một dòng văn bản.</summary>
public static class Prompt
{
    public static string? Show(Window owner, string title, string label, string value, bool password = false)
    {
        var w = new Window
        {
            Owner = owner, Title = title, Width = 480, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize, Style = (Style)owner.FindResource("DialogWindow")
        };
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        var tb = new TextBox { Text = value, AcceptsReturn = false, TextWrapping = TextWrapping.Wrap };
        var pb = new PasswordBox();
        if (password) panel.Children.Add(pb); else { panel.Children.Add(tb); VietInput.Attach(tb); }
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var ok = new Button { Content = "Gửi", Style = (Style)owner.FindResource("BtnPrimary"), IsDefault = true, MinWidth = 90 };
        var cancel = new Button { Content = "Huỷ", IsCancel = true, MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
        string? result = null;
        ok.Click += (_, _) => { result = password ? pb.Password : tb.Text; w.DialogResult = true; };
        row.Children.Add(ok); row.Children.Add(cancel);
        panel.Children.Add(row);
        w.Content = panel;
        w.Loaded += (_, _) => { if (password) pb.Focus(); else { tb.Focus(); tb.SelectAll(); } };
        return w.ShowDialog() == true ? result : null;
    }
}
