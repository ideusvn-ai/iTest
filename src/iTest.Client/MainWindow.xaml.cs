using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ITest.Core;
using ITest.UI;

namespace ITest.Client;

public partial class MainWindow : Window
{
    // ---- trạng thái ----
    private readonly BeaconListener _listener = new();
    private readonly ObservableCollection<RoomItem> _rooms = new();
    private Connection? _conn;
    private RoomBeacon? _room;
    private CandidateInfo _info = new();
    private ClientExam? _exam;
    private int _secondsLeft;
    private int _submitWindow = 15;
    private bool _paused;
    private bool _examRunning;
    private bool _submitted;
    private bool _released;
    private bool _registered;
    private string _sessionToken = "";
    private bool _awaitingWelcome;     // đã kết nối, đang chờ máy chủ xác nhận (đúng mã phòng mới giải mã được)
    private bool _offline;             // đã đăng ký nhưng đang mất kết nối với máy chủ
    private DateTime _offlineSince;
    private bool _reconnecting;
    private int _zeroHold;             // số giây đã giữ ở 00:00 khi mất kết nối (chờ máy chủ bù giờ)
    private readonly string _mediaDir = Path.Combine(Path.GetTempPath(), "iTest_" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<int, string> _audioFiles = new();
    private readonly Dictionary<int, int> _audioPlays = new();
    private readonly MediaPlayer _player = new();
    private readonly Dictionary<int, int> _choices = new();
    private readonly Dictionary<int, string> _essays = new();
    private readonly Dictionary<int, HashSet<int>> _multi = new();
    private readonly Dictionary<int, Dictionary<int, bool>> _tf = new();
    private readonly Dictionary<int, string> _short = new();
    private readonly HashSet<int> _dirtyShort = new();
    private readonly Dictionary<int, QuestionType> _types = new();
    private readonly Dictionary<int, List<(Border yes, Border no)>> _tfMarks = new();
    private readonly Dictionary<int, TextBlock> _summaries = new();
    private readonly Dictionary<int, FrameworkElement> _cards = new();
    private readonly Dictionary<int, List<Border>> _optionCircles = new();
    private readonly Dictionary<int, List<Border>> _bubbles = new();
    private readonly List<WrapPanel> _mathBars = new();
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _essayFlush = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _heartbeat = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _cheatTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    private readonly HashSet<string> _reported = new();
    private readonly DispatcherTimer _saveDebounce = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime _lastRx = DateTime.Now;
    private static readonly Brush TimerNormal = Frozen(0x0F, 0x17, 0x2A), TimerLow = Frozen(0xB9, 0x1C, 0x1C), Ink = Frozen(0x1E, 0x3A, 0x8A);
    private static Brush Frozen(byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromRgb(r, g, b)); x.Freeze(); return x; }
    private readonly HashSet<int> _dirtyEssays = new();
    private DateTime _submittedAt;
    private readonly string _localSave = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "iTest", "autosave.json");

    private static readonly string[] Rules =
    {
        "Có mặt đúng giờ, ngồi đúng máy theo sơ đồ của giám thị.",
        "Không mang tài liệu, điện thoại, thiết bị điện tử vào phòng (trừ khi đề cho phép).",
        "Không trao đổi, nhìn bài hay giúp đỡ thí sinh khác.",
        "Khi làm bài, máy ở chế độ khoá: không chuyển cửa sổ, không thoát ứng dụng.",
        "Mọi lần thoát ứng dụng hoặc mất kết nối đều được ghi nhận và báo giám thị.",
        "Vi phạm lần đầu bị nhắc nhở; tái phạm sẽ bị thu bài và huỷ kết quả.",
        "Chỉ được nộp bài trong khoảng thời gian giám thị quy định; hết giờ bài tự nộp."
    };

    private static readonly string[] MathSymbols = { "√", "²", "³", "≤", "≥", "≠", "±", "×", "÷", "π", "Δ", "∞", "°", "∈", "∉", "⊂", "∩", "∪", "∅", "ℝ", "⇒", "⇔", "α", "β", "½" };

    public MainWindow()
    {
        InitializeComponent();
        MachineInfo.Text = $"Workgroup: {_listener.Workgroup}   ·   Máy: {Environment.MachineName} · {NetUtil.LocalIPv4()}";
        RoomList.ItemsSource = _rooms;
        RulesList.ItemsSource = Rules.Select((t, i) => new { N = i + 1, Text = t }).ToList();
        ScanText.Text = $"Đang dò máy chủ iTest-Server cùng workgroup {_listener.Workgroup}…";
        _listener.Found += b => Dispatcher.Invoke(() => OnBeacon(b));
        try { _listener.Start(); } catch (Exception ex) { FindError.Text = "Không mở được cổng dò phòng: " + ex.Message; }
        StartWave();

        _clock.Tick += (_, _) => OnClock();
        _cheatTimer.Tick += (_, _) => CheatScan();
        _essayFlush.Tick += (_, _) => FlushEssays();
        _saveDebounce.Tick += (_, _) => { _saveDebounce.Stop(); SaveLocalNow(); };
        _heartbeat.Tick += (_, _) => OnHeartbeat();
        _heartbeat.Start();
        KioskGuard.Blocked += what => Dispatcher.BeginInvoke(() => _conn?.Send(Msg.Focus, new { lost = true, reason = "Bấm " + what }));
        Deactivated += OnDeactivated;
        Closing += OnClosing;
        ImeHint.Text = ImeHintFor(ImeMode.Telex);
    }

    // =================== 1. Tìm phòng ===================
    private void StartWave()
    {
        var st = (ScaleTransform)Wave1.RenderTransform;
        var a = new DoubleAnimation(0.6, 1.6, TimeSpan.FromSeconds(2)) { RepeatBehavior = RepeatBehavior.Forever };
        st.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        Wave1.BeginAnimation(OpacityProperty, new DoubleAnimation(0.8, 0, TimeSpan.FromSeconds(2)) { RepeatBehavior = RepeatBehavior.Forever });
    }

    private void OnBeacon(RoomBeacon b)
    {
        var existing = _rooms.FirstOrDefault(r => r.Beacon.ServerId == b.ServerId);
        if (existing != null) { existing.Update(b); }
        else _rooms.Add(new RoomItem(b));
        ScanText.Text = $"Tìm thấy {_rooms.Count} phòng thi trong mạng LAN";
        RoomList.Items.Refresh();
        JoinBtn.IsEnabled = RoomList.SelectedItem is RoomItem ri && ri.Beacon.Open;
    }

    private void RoomList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        JoinBtn.IsEnabled = RoomList.SelectedItem is RoomItem ri && ri.Beacon.Open;

    private async void Join_Click(object sender, RoutedEventArgs e)
    {
        if (RoomList.SelectedItem is not RoomItem ri) return;
        await ConnectTo(ri.Beacon);
    }

    private async void ManualConnect_Click(object sender, RoutedEventArgs e)
    {
        var host = ManualHost.Text.Trim();
        if (host.Length == 0) return;
        await ConnectTo(new RoomBeacon { Address = host, Port = Ports.Session, Room = host, Open = true });
    }

    private string _pin = "";
    private bool _connecting;
    private async Task ConnectTo(RoomBeacon b)
    {
        if (_connecting || _awaitingWelcome) return;
        FindError.Text = "";
        var pin = PinBox.Text.Trim();
        if (pin.Length == 0) { FindError.Text = "Nhập mã phòng thi do giám thị đọc."; return; }
        _pin = pin;
        _connecting = true;
        ScanText.Text = "Đang kết nối và kiểm tra mã phòng thi…";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            var c = await Connection.ConnectAsync(b.Address, b.Port, _pin, cts.Token);
            _room = b;
            _registered = false;
            _awaitingWelcome = true;
            AttachConnection(c);
            SendHello(c);
            RoomLine.Text = $"{b.Room} · {b.Exam} · Giám thị: {b.Proctor}";
            // Chỉ khi nhận được lời chào (giải mã được = đúng mã phòng) mới sang bước nhập thông tin.
            _ = Task.Delay(6000).ContinueWith(_ => Dispatcher.BeginInvoke(() => { if (_awaitingWelcome && _conn == c) c.Close(); }));
        }
        catch (OperationCanceledException) { FindError.Text = "Không kết nối được máy chủ (hết thời gian chờ). Kiểm tra địa chỉ và mạng LAN."; }
        catch (Exception ex) { FindError.Text = "Không kết nối được máy chủ: " + ex.Message; }
        finally { _connecting = false; }
    }

    private void SendHello(Connection c) =>
        c.Send(Msg.Hello, new { machine = Environment.MachineName, workgroup = _listener.Workgroup, ip = NetUtil.LocalIPv4(), token = _sessionToken });

    private object RegisterPayload() => new { _info.Sbd, _info.FullName, _info.BirthDate, _info.ClassName, token = _sessionToken };

    private void AttachConnection(Connection c)
    {
        _conn = c;
        _lastRx = DateTime.Now;
        c.Received += (_, m) => { _lastRx = DateTime.Now; if (m.Str("t") != Msg.Pong) Dispatcher.BeginInvoke(() => { if (c == _conn) OnMessage(m); }); };
        c.Closed += _ => Dispatcher.BeginInvoke(() => OnDisconnected(c));
        c.Start();
    }

    private void OnHeartbeat()
    {
        // Phòng thi không còn quảng bá quá 10 giây thì bỏ khỏi danh sách tìm phòng.
        if (FindPanel.Visibility == Visibility.Visible)
        {
            var stale = _rooms.Where(r => (DateTime.Now - r.LastSeen).TotalSeconds > 10).ToList();
            foreach (var r in stale) _rooms.Remove(r);
            if (stale.Count > 0 && !_connecting && !_awaitingWelcome)
                ScanText.Text = _rooms.Count == 0 ? $"Đang dò máy chủ iTest-Server cùng workgroup {_listener.Workgroup}…" : $"Tìm thấy {_rooms.Count} phòng thi trong mạng LAN";
        }
        if (_conn != null)
        {
            _conn.Send(Msg.Ping);
            // Đã vào phòng mà không nhận được gì từ máy chủ quá 12 giây: chủ động ngắt để kết nối lại.
            if (_registered && !_released && (DateTime.Now - _lastRx).TotalSeconds > 12) _conn.Close();
        }
        // Đã nộp bài nhưng mất liên lạc với máy chủ hơn 1 phút: không giữ khoá máy vô thời hạn.
        if (_submitted && _offline && !_released && (DateTime.Now - _offlineSince).TotalSeconds > 60) OfferExitOffline();
    }

    /// <summary>Đã nộp bài, máy chủ không còn liên lạc được: mở khoá để thí sinh thoát sau khi báo giám thị.</summary>
    private void OfferExitOffline()
    {
        _released = true;
        KioskGuard.Leave();
        Topmost = false;
        WaitTitle.Text = "Mất kết nối với máy chủ";
        WaitSub.Text = "Bài làm của bạn đã được nộp và lưu lại. Hãy báo giám thị, sau đó có thể thoát ứng dụng.";
        ExitBtn.Visibility = Visibility.Visible;
    }

    // =================== 2. Thông tin thí sinh ===================
    private bool _dobBusy;
    private void DobBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_dobBusy) return;
        var digits = new string(DobBox.Text.Where(char.IsDigit).Take(8).ToArray());
        string v = digits.Length > 4 ? $"{digits[..2]}/{digits[2..4]}/{digits[4..]}" : digits.Length > 2 ? $"{digits[..2]}/{digits[2..]}" : digits;
        if (v == DobBox.Text) return;
        _dobBusy = true;
        DobBox.Text = v;
        DobBox.CaretIndex = v.Length;
        _dobBusy = false;
    }

    private void Register_Click(object sender, RoutedEventArgs e)
    {
        var miss = new List<string>();
        if (string.IsNullOrWhiteSpace(SbdBox.Text)) miss.Add("số báo danh");
        if (string.IsNullOrWhiteSpace(NameBox.Text)) miss.Add("họ và tên");
        if (DobBox.Text.Length != 10) miss.Add("ngày sinh (dd/mm/yyyy)");
        if (string.IsNullOrWhiteSpace(ClassBox.Text)) miss.Add("lớp");
        if (miss.Count > 0) { InfoError.Text = "Vui lòng nhập " + string.Join(", ", miss) + "."; return; }
        if (AgreeBox.IsChecked != true) { InfoError.Text = "Bạn cần xác nhận cam kết tuân thủ nội quy."; return; }
        InfoError.Text = "";
        _info = new CandidateInfo { Sbd = SbdBox.Text.Trim(), FullName = NameBox.Text.Trim(), BirthDate = DobBox.Text, ClassName = ClassBox.Text.Trim() };
        if (_conn == null) { InfoError.Text = "Chưa kết nối được máy chủ. Vui lòng chờ kết nối lại."; return; }
        _conn.Send(Msg.Register, RegisterPayload());
    }

    private void SetInfoLocked(bool locked)
    {
        foreach (var tb in new[] { SbdBox, NameBox, DobBox, ClassBox }) tb.IsEnabled = !locked;
        AgreeBox.IsEnabled = !locked;
        RegisterBtn.Visibility = locked ? Visibility.Collapsed : Visibility.Visible;
        QuitInfoBtn.Visibility = (locked || _registered) && !_offline ? Visibility.Collapsed : Visibility.Visible;
        WaitBox.Visibility = locked ? Visibility.Visible : Visibility.Collapsed;
        if (locked)
        {
            var rt = (RotateTransform)Spinner.RenderTransform;
            rt.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever });
        }
    }

    private void EditInfo_Click(object sender, RoutedEventArgs e)
    {
        EditReqBox.Visibility = Visibility.Collapsed;
        SetInfoLocked(false);
        RegisterBtn.Content = "Cập nhật & quay lại phòng chờ";
    }

    // =================== Nhận thông điệp ===================
    private void OnMessage(JsonObject m)
    {
        switch (m.Str("t"))
        {
            case Msg.Welcome:
                RoomLine.Text = $"{m.Str("room")} · {m.Str("exam")} · Giám thị: {m.Str("proctor")}";
                if (_awaitingWelcome)
                {
                    _awaitingWelcome = false;
                    FindPanel.Visibility = Visibility.Collapsed;
                    Anim.Show(InfoPanel);
                }
                break;
            case Msg.Registered:
                _registered = true;
                _sessionToken = m.Str("token");
                if (!_examRunning) SetInfoLocked(true);
                if (_offline) SetOffline(false);   // máy chủ đã nhận lại phiên: hết trạng thái mất kết nối
                break;
            case Msg.Reject:
                if (_examRunning)
                {
                    // Máy chủ chưa nhận ra phiên cũ đã mất: ngắt và thử lại sau vài giây (vòng kết nối lại sẽ lo).
                    _conn?.Close();
                    break;
                }
                _registered = false;
                InfoError.Text = m.Str("reason");
                SetInfoLocked(false);
                break;
            case Msg.EditRequest:
                EditReqText.Text = "Giám thị yêu cầu sửa thông tin: " + m.Str("reason");
                Anim.Show(EditReqBox);
                break;
            case Msg.Exam:
                if (_submitted)
                {
                    // Đã nộp bài nhưng máy chủ chưa ghi nhận (rớt mạng đúng lúc nộp): nộp lại, không mở lại đề.
                    _conn?.Send(Msg.Submit, new { auto = true });
                    break;
                }
                var exam = m.As<ClientExam>("exam");
                if (exam == null || exam.Questions.Count == 0) break;
                _exam = exam;
                _secondsLeft = m.Int("secondsLeft");
                _submitWindow = m.Int("submitWindow", 15);
                _zeroHold = 0;
                StartExam();
                break;
            case Msg.Resume:
                if (_submitted) break;
                // Bài làm trên máy này là bản mới nhất (có thể vừa làm lúc mất mạng): chỉ lấy từ máy chủ những câu máy này chưa có.
                bool textAdded = false;
                var ch = m.As<Dictionary<int, int>>("choices");
                var es = m.As<Dictionary<int, string>>("essays");
                var mu = m.As<Dictionary<int, List<int>>>("multi");
                var tfr = m.As<Dictionary<int, Dictionary<int, bool>>>("tf");
                var sh = m.As<Dictionary<int, string>>("shortAns");
                if (ch != null) foreach (var (k, v) in ch) _choices.TryAdd(k, v);
                if (es != null) foreach (var (k, v) in es) if (!_essays.ContainsKey(k) && !string.IsNullOrEmpty(v)) { _essays[k] = v; textAdded = true; }
                if (mu != null) foreach (var (k, v) in mu) if (!_multi.ContainsKey(k) && v != null) _multi[k] = v.ToHashSet();
                if (tfr != null) foreach (var (k, v) in tfr) if (!_tf.ContainsKey(k) && v != null) _tf[k] = v;
                if (sh != null) foreach (var (k, v) in sh) if (!_short.ContainsKey(k) && !string.IsNullOrEmpty(v)) { _short[k] = v; textAdded = true; }
                if (_exam != null && textAdded) BuildPaper(); else RefreshAnswers();
                SaveLocal();
                break;
            case Msg.Time:
                _secondsLeft = m.Int("secondsLeft");
                _paused = m.Bool("paused");
                _zeroHold = 0;
                UpdateClock();
                break;
            case Msg.Notice:
                ShowNotice(m.Str("text"), m.Str("kind") == "me");
                break;
            case Msg.Collect:
                if (_examRunning && !_submitted) DoSubmit(auto: true);
                break;
            case Msg.Result:
                // Máy chủ đã ghi nhận bài nộp: bản lưu tạm trên máy không còn cần nữa.
                try { File.Delete(_localSave); } catch { }
                ShowResult(m.Bool("showScore"), m.Int("right"), m.Int("wrong"), m.Int("blank"), m.Dbl("score"));
                break;
            case Msg.Release:
                _released = true;
                KioskGuard.Leave();
                Topmost = false;
                WaitTitle.Text = "Giám thị đã kết thúc phiên thi";
                WaitSub.Text = "Bạn có thể thoát ứng dụng.";
                ExitBtn.Visibility = Visibility.Visible;
                if (!_examRunning || _submitted) { Close(); }
                else if (_examRunning && !_submitted)
                {
                    // Giám thị cho chuyển máy: phiên trên máy này kết thúc, bài làm đã ở máy chủ
                    _submitted = true;
                    ExamPanel.Visibility = Visibility.Collapsed;
                    DoneExam.Text = $"{_exam?.ExamName} – {_exam?.Subject} · Mã đề {_exam?.Code}";
                    DoneLine.Text = $"{_info.FullName} · SBD {_info.Sbd}";
                    DoneHeading.Text = "Đã lưu bài làm trên máy chủ";
                    WaitTitle.Text = "Phiên làm bài trên máy này đã kết thúc";
                    WaitSub.Text = "Giám thị đã cho phép chuyển máy. Hãy sang máy dự phòng, mở iTest và đăng nhập lại bằng đúng số báo danh để làm tiếp.";
                    Anim.Show(DonePanel);
                }
                break;
        }
    }

    private async void OnDisconnected(Connection closed)
    {
        if (closed != _conn) return;   // kết nối cũ đã được thay bằng kết nối mới
        _conn = null;
        if (_released) return;
        // Chưa đăng ký xong: quay lại màn hình tìm phòng và báo rõ lý do.
        if (!_registered && !_examRunning)
        {
            bool wrongPin = _awaitingWelcome;
            _awaitingWelcome = false;
            InfoPanel.Visibility = Visibility.Collapsed;
            if (FindPanel.Visibility != Visibility.Visible) Anim.Show(FindPanel);
            ScanText.Text = $"Tìm thấy {_rooms.Count} phòng thi trong mạng LAN";
            FindError.Text = wrongPin
                ? "Không vào được phòng. Kiểm tra lại mã phòng thi (do giám thị đọc) rồi thử lại."
                : "Mất kết nối với máy chủ. Hãy chọn phòng và vào lại.";
            return;
        }
        // Đã vào phòng (đang chờ, đang làm bài hoặc đã nộp): tự kết nối lại cho tới khi giám thị kết thúc phiên.
        if (_reconnecting) return;
        _reconnecting = true;
        SetOffline(true);
        try
        {
            while (_room != null && !_released)
            {
                await Task.Delay(3000);
                if (_released) break;
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                    var c = await Connection.ConnectAsync(_room.Address, _room.Port, _pin, cts.Token);
                    if (_released) { c.Close(); break; }
                    AttachConnection(c);
                    SendHello(c);
                    c.Send(Msg.Register, RegisterPayload());
                    if (_examRunning)
                    {
                        foreach (var (k, v) in _choices) c.Send(Msg.Answer, new { no = k, choice = v });
                        foreach (var (k, v) in _essays) c.Send(Msg.Essay, new { no = k, text = v });
                        ResendExtra(c);
                        if (_submitted) c.Send(Msg.Submit, new { auto = true });
                    }
                    return;   // trạng thái "mất kết nối" chỉ tắt khi máy chủ xác nhận (tin Registered)
                }
                catch { }
            }
        }
        finally { _reconnecting = false; }
    }

    /// <summary>Hiện/ẩn trạng thái mất kết nối phù hợp với màn hình đang mở.</summary>
    private void SetOffline(bool offline)
    {
        if (offline && !_offline) _offlineSince = DateTime.Now;
        _offline = offline;
        if (!offline) _zeroHold = 0;
        OfflineBar.Visibility = offline && _examRunning && !_submitted ? Visibility.Visible : Visibility.Collapsed;
        if (!_examRunning)
        {
            // Phòng chờ: báo ngay trên khung chờ và cho phép thoát nếu máy chủ không trở lại.
            WaitText.Text = offline ? "Mất kết nối với máy chủ · đang kết nối lại…" : "Đang chờ giám thị phát đề…";
            QuitInfoBtn.Visibility = offline ? Visibility.Visible : Visibility.Collapsed;
        }
        else if (_submitted && !_released)
        {
            WaitSub.Text = offline
                ? "Mất kết nối với máy chủ · đang kết nối lại… Bài làm của bạn đã được lưu."
                : "Không tắt máy, không rời khỏi chỗ ngồi. Máy sẽ tự mở khoá khi giám thị đóng phiên.";
        }
    }

    // =================== 3. Làm bài ===================
    private void StartExam()
    {
        if (_exam == null) return;
        bool firstTime = !_examRunning;
        _examRunning = true;
        LoadLocalSave();
        InfoPanel.Visibility = Visibility.Collapsed;
        FindPanel.Visibility = Visibility.Collapsed;
        TopBar.Visibility = Visibility.Collapsed;
        // Kết nối lại giữa giờ: giữ nguyên trang đề đang làm (không dựng lại để khỏi mất con trỏ đang gõ).
        if (firstTime || _builtCode != _exam.Code || PaperHost.Children.Count == 0) BuildPaper(); else RefreshAnswers();
        if (ExamPanel.Visibility != Visibility.Visible) Anim.Show(ExamPanel);
        ExamTitle.Text = $"{_exam.ExamName} – {_exam.Subject} · Mã đề {_exam.Code}";
        CandidateLine.Text = $"{_info.FullName} · SBD {_info.Sbd} · Lớp {_info.ClassName}";
        SubmitRule.Text = $"Quy định phòng thi: chỉ được nộp bài trong {_submitWindow} phút cuối. Hết giờ bài được nộp tự động.";
        if (firstTime)
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;
            Topmost = true;
            KioskGuard.Enter();
            _clock.Start();
            AntiCheat.ProtectWindow(this);
            AntiCheat.ClearClipboard();
            var vm = AntiCheat.VirtualMachine();
            if (vm != null) ReportCheat("vm", "máy thí sinh là máy ảo (" + vm + ")");
            CheatScan();
            _cheatTimer.Start();
            _essayFlush.Start();
        }
        UpdateClock();
    }

    private string _builtCode = "";
    private void BuildPaper()
    {
        if (_exam == null) return;
        _builtCode = _exam.Code;
        PaperHost.Children.Clear(); SheetHost.Children.Clear();
        _cards.Clear(); _optionCircles.Clear(); _bubbles.Clear(); _mathBars.Clear();

        // Đầu đề
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition());
        var left = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        left.Children.Add(Centered(_exam.Department.ToUpperInvariant(), FontWeights.Normal));
        left.Children.Add(Centered(_exam.School.ToUpperInvariant(), FontWeights.Bold));
        left.Children.Add(new Border { Width = 120, Height = 1, Background = Brushes.Black, Margin = new Thickness(0, 4, 0, 6) });
        left.Children.Add(Centered($"(Đề thi có {_exam.Questions.Count} câu)", FontWeights.Normal, italic: true));
        var right = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        right.Children.Add(Centered(_exam.ExamName.ToUpperInvariant(), FontWeights.Bold));
        right.Children.Add(Centered("NĂM HỌC " + _exam.SchoolYear, FontWeights.Bold));
        right.Children.Add(Centered("Môn: " + _exam.Subject.ToUpperInvariant(), FontWeights.Bold));
        right.Children.Add(Centered($"Thời gian làm bài: {_exam.Minutes} phút, không kể thời gian phát đề", FontWeights.Normal, italic: true));
        Grid.SetColumn(right, 1);
        head.Children.Add(left); head.Children.Add(right);
        PaperHost.Children.Add(head);

        var who = new DockPanel { Margin = new Thickness(0, 22, 0, 8) };
        var code = new Border { BorderBrush = Brushes.Black, BorderThickness = new Thickness(1.5), Padding = new Thickness(14, 4, 14, 4) };
        code.Child = new TextBlock { Text = "Mã đề\n" + _exam.Code, FontWeight = FontWeights.Bold, TextAlignment = TextAlignment.Center };
        DockPanel.SetDock(code, Dock.Right);
        who.Children.Add(code);
        var whoText = new StackPanel();
        whoText.Children.Add(new TextBlock { Text = "Họ và tên thí sinh: " + _info.FullName });
        whoText.Children.Add(new TextBlock { Text = "Số báo danh: " + _info.Sbd, Margin = new Thickness(0, 4, 0, 0) });
        who.Children.Add(whoText);
        PaperHost.Children.Add(who);
        PaperHost.Children.Add(new Border { Height = 1, Background = Brushes.Black, Margin = new Thickness(0, 10, 0, 18) });

        bool twoParts = _exam.McCount > 0 && _exam.EssayCount > 0;
        var mcs = _exam.Questions.Where(q => q.Type.IsObjective()).ToList();
        var ess = _exam.Questions.Where(q => q.Type == QuestionType.Essay).ToList();
        _types.Clear(); _tfMarks.Clear(); _summaries.Clear();
        foreach (var q in _exam.Questions) _types[q.No] = q.Type;
        if (mcs.Count > 0)
        {
            if (twoParts)
            {
                PaperHost.Children.Add(new TextBlock { Text = $"PHẦN I. TRẮC NGHIỆM ({Fmt(_exam.McTotal)} điểm)", FontWeight = FontWeights.Bold });
                PaperHost.Children.Add(new TextBlock
                {
                    Text = mcs.All(q => q.Type.IsSingleChoice()) ? "Mỗi câu hỏi chỉ chọn một phương án."
                         : "Đọc kỹ yêu cầu từng câu: chọn một phương án, chọn nhiều phương án, chọn Đúng/Sai từng ý hoặc điền câu trả lời ngắn.",
                    FontStyle = FontStyles.Italic, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 12)
                });
            }
            foreach (var q in mcs) PaperHost.Children.Add(BuildObjectiveCard(q));
        }
        if (ess.Count > 0)
        {
            if (twoParts)
            {
                PaperHost.Children.Add(new TextBlock { Text = $"PHẦN II. TỰ LUẬN ({Fmt(_exam.EssayTotal)} điểm)", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 20, 0, 0) });
                PaperHost.Children.Add(new TextBlock { Text = "Thí sinh trình bày bài làm vào khung dưới mỗi câu.", FontStyle = FontStyles.Italic, Margin = new Thickness(0, 2, 0, 12) });
            }
            foreach (var q in ess) PaperHost.Children.Add(BuildEssayCard(q));
        }
        PaperHost.Children.Add(new TextBlock { Text = "———— HẾT ————", FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 24, 0, 6) });
        PaperHost.Children.Add(new TextBlock
        {
            Text = (_exam.AllowDocuments ? "Thí sinh được sử dụng tài liệu." : "Thí sinh không được sử dụng tài liệu.") + " Giám thị không giải thích gì thêm.",
            FontStyle = FontStyles.Italic, HorizontalAlignment = HorizontalAlignment.Center
        });

        BuildSheet(mcs, ess);
        RefreshAnswers();
        ApplyMathVisibility();
    }

    private static TextBlock Centered(string text, FontWeight w, bool italic = false) =>
        new() { Text = text, FontWeight = w, FontStyle = italic ? FontStyles.Italic : FontStyles.Normal, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };

    private static string Fmt(double v) => Math.Round(v, 2).ToString("0.##", System.Globalization.CultureInfo.GetCultureInfo("vi-VN"));

    private static readonly Brush TfOn = Frozen(0x1E, 0x3A, 0x8A), TfOff = Frozen(0xCB, 0xD5, 0xE1);

    private FrameworkElement BuildObjectiveCard(ClientQuestion q)
    {
        var card = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        var tb = new TextBlock { TextWrapping = TextWrapping.Wrap };
        tb.Inlines.Add(new System.Windows.Documents.Run($"Câu {q.No}. ") { FontWeight = FontWeights.Bold });
        RichMath.AddTo(tb.Inlines, q.Text, 17);
        if (q.Type == QuestionType.MultiSelect)
            tb.Inlines.Add(new System.Windows.Documents.Run("  (Chọn tất cả phương án đúng)") { FontStyle = FontStyles.Italic, Foreground = Brushes.DimGray });
        card.Children.Add(tb);
        AddMedia(card, q);
        int no = q.No;

        if (q.Type == QuestionType.TrueFalse)
        {
            var marks = new List<(Border, Border)>();
            var grid = new Grid { Margin = new Thickness(30, 8, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            for (int k = 0; k < q.Options.Count; k++)
            {
                int idx = k;
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var st = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 12, 4) };
                st.Inlines.Add(new System.Windows.Documents.Run($"{(char)('a' + k)}) ") { FontWeight = FontWeights.Bold });
                RichMath.AddTo(st.Inlines, q.Options[k], 17);
                Grid.SetRow(st, k);
                grid.Children.Add(st);
                var pair = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                Border Mark(string text, bool val)
                {
                    var b = new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1.5), BorderBrush = TfOff, Padding = new Thickness(12, 3, 12, 3), Background = Brushes.White };
                    b.Child = new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, FontFamily = new FontFamily("Segoe UI"), FontSize = 14 };
                    var btn = new Button { Content = b, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(2), MinHeight = 32 };
                    btn.Click += (_, _) => ChooseTf(no, idx, val);
                    pair.Children.Add(btn);
                    return b;
                }
                var yes = Mark("Đúng", true); var noB = Mark("Sai", false);
                marks.Add((yes, noB));
                Grid.SetRow(pair, k); Grid.SetColumn(pair, 1);
                grid.Children.Add(pair);
            }
            _tfMarks[no] = marks;
            card.Children.Add(grid);
        }
        else if (q.Type == QuestionType.ShortAnswer)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(30, 8, 0, 0) };
            row.Children.Add(new TextBlock { Text = "Trả lời: ", FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center });
            var box = new TextBox
            {
                Width = 260, FontFamily = new FontFamily("Times New Roman"), FontSize = 17, Foreground = Ink,
                Text = _short.TryGetValue(no, out var sv) ? sv : ""
            };
            VietInput.Attach(box);
            box.TextChanged += (_, _) => { if (_submitted) return; _short[no] = box.Text; _dirtyShort.Add(no); RefreshAnswers(); SaveLocal(); };
            box.LostKeyboardFocus += (_, _) => FlushEssays();
            row.Children.Add(box);
            card.Children.Add(row);
        }
        else
        {
            bool multi = q.Type == QuestionType.MultiSelect;
            bool shortOpts = q.Options.All(o => o.Length <= 16 && !RichMath.HasMath(o));
            var grid = new UniformGrid { Columns = shortOpts ? 4 : 2, Margin = new Thickness(30, 6, 0, 0) };
            var circles = new List<Border>();
            for (int k = 0; k < q.Options.Count; k++)
            {
                int choice = k;
                var circle = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(multi ? 5 : 15), BorderThickness = new Thickness(2), BorderBrush = multi ? TfOff : Brushes.Transparent };
                circle.Child = new TextBlock { Text = ((char)('A' + k)).ToString(), FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(circle);
                var ot = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
                RichMath.AddTo(ot.Inlines, q.Options[k], 17);
                row.Children.Add(ot);
                var btn = new Button { Content = row, Background = Brushes.Transparent, BorderThickness = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(4), MinHeight = 36, HorizontalContentAlignment = HorizontalAlignment.Left };
                btn.Click += (_, _) => { if (multi) ToggleMulti(no, choice); else Choose(no, choice); };
                circles.Add(circle);
                grid.Children.Add(btn);
            }
            _optionCircles[no] = circles;
            card.Children.Add(grid);
        }
        _cards[no] = card;
        return card;
    }

    private void ToggleMulti(int no, int choice)
    {
        if (_submitted) return;
        if (!_multi.TryGetValue(no, out var set)) _multi[no] = set = new HashSet<int>();
        if (!set.Remove(choice)) set.Add(choice);
        SendExtra(_conn, no);
        RefreshAnswers();
        SaveLocal();
    }

    private void ChooseTf(int no, int idx, bool val)
    {
        if (_submitted) return;
        if (!_tf.TryGetValue(no, out var d)) _tf[no] = d = new Dictionary<int, bool>();
        if (d.TryGetValue(idx, out var cur) && cur == val) d.Remove(idx); else d[idx] = val;
        SendExtra(_conn, no);
        RefreshAnswers();
        SaveLocal();
    }

    /// <summary>Gửi câu trả lời của câu nhiều đáp án / Đúng-Sai / trả lời ngắn lên máy chủ.</summary>
    private void SendExtra(Connection? c, int no)
    {
        if (c == null || !_types.TryGetValue(no, out var ty)) return;
        var ax = new AnswerEx { No = no, Type = ty };
        switch (ty)
        {
            case QuestionType.MultiSelect: ax.Choices = _multi.TryGetValue(no, out var s) ? s.OrderBy(x => x).ToList() : new(); break;
            case QuestionType.TrueFalse: ax.Tf = _tf.TryGetValue(no, out var d) ? new Dictionary<int, bool>(d) : new(); break;
            case QuestionType.ShortAnswer: ax.Text = _short.TryGetValue(no, out var v) ? v : ""; break;
            default: return;
        }
        c.Send(Msg.AnswerEx, ax);
    }

    private void ResendExtra(Connection c)
    {
        foreach (var no in _multi.Keys.Concat(_tf.Keys).Concat(_short.Keys).Distinct().ToList()) SendExtra(c, no);
    }

    private int AnsweredCount() =>
        _choices.Count + _multi.Count(kv => kv.Value.Count > 0) + _tf.Count(kv => kv.Value.Count > 0)
        + _short.Count(kv => !string.IsNullOrWhiteSpace(kv.Value)) + _essays.Count(kv => !string.IsNullOrWhiteSpace(kv.Value));

    private FrameworkElement BuildEssayCard(ClientQuestion q)
    {
        var card = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
        var tb = new TextBlock { TextWrapping = TextWrapping.Wrap };
        tb.Inlines.Add(new System.Windows.Documents.Run($"Câu {q.No} ") { FontWeight = FontWeights.Bold });
        tb.Inlines.Add(new System.Windows.Documents.Run($"({Fmt(q.Points)} điểm). ") { FontStyle = FontStyles.Italic });
        RichMath.AddTo(tb.Inlines, q.Text, 17);
        card.Children.Add(tb);
        AddMedia(card, q);
        var box = new TextBox
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 140, VerticalContentAlignment = VerticalAlignment.Top,
            FontFamily = new FontFamily("Times New Roman"), FontSize = 17, Foreground = new SolidColorBrush(Color.FromRgb(0x1E, 0x3A, 0x8A)),
            Text = _essays.TryGetValue(q.No, out var t) ? t : "", Margin = new Thickness(0, 6, 0, 0)
        };
        VietInput.Attach(box);
        box.TextChanged += (_, _) => { _essays[q.No] = box.Text; _dirtyEssays.Add(q.No); UpdateProgress(); SaveLocal(); };
        var bar = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var s in MathSymbols)
        {
            var b = new Button { Content = s, MinWidth = 34, MinHeight = 32, Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(0, 0, 4, 4), FontSize = 15, ToolTip = "Chèn " + s };
            b.Click += (_, _) => VietInput.Insert(box, s);
            bar.Children.Add(b);
        }
        _mathBars.Add(bar);
        card.Children.Add(bar);
        card.Children.Add(box);
        _cards[q.No] = card;
        return card;
    }

    private void AddMedia(Panel card, ClientQuestion q)
    {
        if (q.ImageBase64 != null)
        {
            try
            {
                var img = LoadImage(q.ImageBase64);
                var image = new Image { Source = img, MaxHeight = 220, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(30, 8, 0, 0), Cursor = Cursors.Hand, ToolTip = "Bấm để phóng to" };
                image.MouseLeftButtonUp += (_, _) => ShowZoom(img);
                card.Children.Add(image);
            }
            catch { }
        }
        if (q.AudioBase64 != null)
        {
            try
            {
                // Mỗi file âm thanh chỉ ghi ra đĩa một lần (thư mục tạm riêng của phiên, xoá khi thoát);
                // số lượt đã nghe được giữ lại khi đề được dựng lại sau lúc rớt mạng.
                int no = q.No;
                if (!_audioFiles.TryGetValue(no, out var path))
                {
                    Directory.CreateDirectory(_mediaDir);
                    path = Path.Combine(_mediaDir, $"cau{no}{q.AudioExt}");
                    File.WriteAllBytes(path, Convert.FromBase64String(q.AudioBase64));
                    _audioFiles[no] = path;
                }
                int max = Math.Max(1, q.AudioMaxPlays);
                int Left() => max - _audioPlays.GetValueOrDefault(no);
                var btn = new Button { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(30, 8, 0, 0) };
                void Paint()
                {
                    btn.Content = Left() <= 0 ? "Đã hết lượt nghe" : _audioPlays.ContainsKey(no) ? $"▶ Nghe lại (còn {Left()} lần)" : $"▶ Nghe âm thanh (còn {Left()} lần)";
                    btn.IsEnabled = Left() > 0;
                }
                btn.Click += (_, _) =>
                {
                    if (Left() <= 0) return;
                    _audioPlays[no] = _audioPlays.GetValueOrDefault(no) + 1;
                    try { _player.Open(new Uri(path)); _player.Play(); } catch { }
                    Paint();
                };
                Paint();
                card.Children.Add(btn);
            }
            catch (Exception ex) { CrashGuard.Write(ex); }
        }
    }

    private static BitmapImage LoadImage(string b64)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = new MemoryStream(Convert.FromBase64String(b64));
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    private void ShowZoom(ImageSource src)
    {
        var scale = new ScaleTransform(1, 1);
        var img = new Image { Source = src, LayoutTransform = scale, Stretch = Stretch.None };
        var w = new Window
        {
            Owner = this, WindowStyle = WindowStyle.ToolWindow, Title = "Phóng to hình", Width = 900, Height = 700, Topmost = true,
            Content = new DockPanel()
        };
        var dock = (DockPanel)w.Content;
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(8) };
        var minus = new Button { Content = "−", Width = 44 }; var plus = new Button { Content = "+", Width = 44, Margin = new Thickness(8, 0, 8, 0) };
        var close = new Button { Content = "Đóng", Style = (Style)FindResource("BtnPrimary") };
        minus.Click += (_, _) => { scale.ScaleX = scale.ScaleY = Math.Max(0.5, scale.ScaleX - 0.5); };
        plus.Click += (_, _) => { scale.ScaleX = scale.ScaleY = Math.Min(4, scale.ScaleX + 0.5); };
        close.Click += (_, _) => w.Close();
        bar.Children.Add(minus); bar.Children.Add(plus); bar.Children.Add(close);
        DockPanel.SetDock(bar, Dock.Bottom);
        dock.Children.Add(bar);
        dock.Children.Add(new ScrollViewer { Content = img, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        w.ShowDialog();
    }

    private void BuildSheet(List<ClientQuestion> mcs, List<ClientQuestion> ess)
    {
        foreach (var q in mcs)
        {
            if (!q.Type.IsSingleChoice() && q.Type != QuestionType.MultiSelect)
            {
                // Đúng/Sai và trả lời ngắn: hiện tóm tắt câu trả lời trên phiếu
                var r2 = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
                int n2 = q.No;
                var nb = new Button { Content = q.No.ToString(), Width = 40, MinHeight = 28, Padding = new Thickness(0), FontWeight = FontWeights.Bold, Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
                nb.Click += (_, _) => GoTo(n2);
                DockPanel.SetDock(nb, Dock.Left);
                r2.Children.Add(nb);
                var sum = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Foreground = Brushes.Gray, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(6, 0, 0, 0) };
                r2.Children.Add(sum);
                _summaries[q.No] = sum;
                SheetHost.Children.Add(r2);
                continue;
            }
            var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            for (int i = 0; i < 4; i++) row.ColumnDefinitions.Add(new ColumnDefinition());
            var num = new Button { Content = q.No.ToString(), MinHeight = 28, Padding = new Thickness(0), FontWeight = FontWeights.Bold, Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
            int no = q.No;
            num.Click += (_, _) => GoTo(no);
            row.Children.Add(num);
            var list = new List<Border>();
            for (int k = 0; k < Math.Min(4, q.Options.Count); k++)
            {
                int choice = k;
                var bub = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(q.Type == QuestionType.MultiSelect ? 4 : 11), BorderThickness = new Thickness(1.5), BorderBrush = Brushes.Gray, Background = Brushes.White };
                bub.Child = new TextBlock { Text = ((char)('A' + k)).ToString(), FontSize = 10, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.Gray };
                var b = new Button { Content = bub, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), MinHeight = 28 };
                bool isMulti = q.Type == QuestionType.MultiSelect;
                b.Click += (_, _) => { if (isMulti) ToggleMulti(no, choice); else Choose(no, choice); };
                Grid.SetColumn(b, k + 1);
                row.Children.Add(b);
                list.Add(bub);
            }
            _bubbles[q.No] = list;
            SheetHost.Children.Add(row);
        }
        if (ess.Count > 0)
        {
            SheetHost.Children.Add(new TextBlock { Text = "TỰ LUẬN", FontWeight = FontWeights.Bold, FontSize = 11, Margin = new Thickness(0, 12, 0, 6) });
            var wrap = new WrapPanel();
            foreach (var q in ess)
            {
                int no = q.No;
                var b = new Button { Content = q.No.ToString(), Width = 56, Margin = new Thickness(0, 0, 6, 6) };
                b.Click += (_, _) => GoTo(no);
                wrap.Children.Add(b);
            }
            SheetHost.Children.Add(wrap);
        }
    }

    private void GoTo(int no)
    {
        if (!_cards.TryGetValue(no, out var card)) return;
        card.BringIntoView(new Rect(0, 0, card.ActualWidth, Math.Min(card.ActualHeight, 400)));
        var flash = new SolidColorBrush(Color.FromArgb(0, 253, 230, 138));
        if (card is Panel p)
        {
            p.Background = flash;
            flash.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(Color.FromArgb(200, 253, 230, 138), Color.FromArgb(0, 253, 230, 138), TimeSpan.FromSeconds(1.4)));
        }
    }

    private void Choose(int no, int choice)
    {
        if (_submitted) return;
        _choices[no] = choice;
        _conn?.Send(Msg.Answer, new { no, choice });
        RefreshAnswers();
        SaveLocal();
    }

    private void RefreshAnswers()
    {
        var ink = Ink;
        bool On(int no, int k) => _types.TryGetValue(no, out var ty) && ty == QuestionType.MultiSelect
            ? _multi.TryGetValue(no, out var s) && s.Contains(k)
            : _choices.TryGetValue(no, out var c) && c == k;
        foreach (var (no, circles) in _optionCircles)
        {
            bool multi = _types.TryGetValue(no, out var ty) && ty == QuestionType.MultiSelect;
            for (int k = 0; k < circles.Count; k++)
            {
                bool on = On(no, k);
                circles[k].BorderBrush = on ? ink : multi ? TfOff : Brushes.Transparent;
                circles[k].Background = on && multi ? ink : Brushes.Transparent;
                ((TextBlock)circles[k].Child).Foreground = on ? (multi ? Brushes.White : ink) : Brushes.Black;
            }
        }
        foreach (var (no, marks) in _tfMarks)
            for (int k = 0; k < marks.Count; k++)
            {
                bool? v = _tf.TryGetValue(no, out var d) && d.TryGetValue(k, out var b) ? b : null;
                Paint(marks[k].yes, v == true);
                Paint(marks[k].no, v == false);
            }
        foreach (var (no, sum) in _summaries)
        {
            if (_types[no] == QuestionType.TrueFalse)
            {
                var d = _tf.TryGetValue(no, out var dd) ? dd : new Dictionary<int, bool>();
                int n = _exam?.Questions.FirstOrDefault(x => x.No == no)?.Options.Count ?? 4;
                sum.Text = string.Join("  ", Enumerable.Range(0, n).Select(i => $"{(char)('a' + i)}:" + (d.TryGetValue(i, out var b) ? (b ? "Đ" : "S") : "–")));
                sum.Foreground = d.Count > 0 ? ink : Brushes.Gray;
            }
            else
            {
                var v = _short.TryGetValue(no, out var sv) ? sv : "";
                sum.Text = string.IsNullOrWhiteSpace(v) ? "(chưa trả lời)" : v;
                sum.Foreground = string.IsNullOrWhiteSpace(v) ? Brushes.Gray : ink;
            }
        }
        foreach (var (no, bubs) in _bubbles)
            for (int k = 0; k < bubs.Count; k++)
            {
                bool on = On(no, k);
                bubs[k].Background = on ? ink : Brushes.White;
                bubs[k].BorderBrush = on ? ink : Brushes.Gray;
                ((TextBlock)bubs[k].Child).Foreground = on ? Brushes.White : Brushes.Gray;
            }
        UpdateProgress();
    }

    private static void Paint(Border b, bool on)
    {
        b.Background = on ? TfOn : Brushes.White;
        b.BorderBrush = on ? TfOn : TfOff;
        ((TextBlock)b.Child).Foreground = on ? Brushes.White : Brushes.Black;
    }

    private void UpdateProgress()
    {
        if (_exam == null) return;
        int done = AnsweredCount();
        int total = _exam.Questions.Count;
        ProgressText.Text = $"Đã làm {done}/{total}";
        ProgressBar.Value = total == 0 ? 0 : (double)done / total;
    }

    private void FlushEssays()
    {
        if (_conn == null) return;
        foreach (var no in _dirtyShort.ToList()) SendExtra(_conn, no);
        _dirtyShort.Clear();
        if (_dirtyEssays.Count == 0) return;
        foreach (var no in _dirtyEssays.ToList())
            _conn.Send(Msg.Essay, new { no, text = _essays.TryGetValue(no, out var t) ? t : "" });
        _dirtyEssays.Clear();
    }

    private void OnClock()
    {
        if (!_examRunning || _submitted) return;
        if (!_paused && _secondsLeft > 0) _secondsLeft--;
        UpdateClock();
        if (_secondsLeft > 0) return;
        // Hết giờ lúc đang mất kết nối: máy chủ có bù giờ cho thời gian rớt mạng, nên chờ kết nối lại tối đa 2 phút rồi mới tự nộp.
        if (_offline && ++_zeroHold < 120) return;
        DoSubmit(auto: true);
    }

    private void UpdateClock()
    {
        TimerText.Text = $"{_secondsLeft / 60:00}:{_secondsLeft % 60:00}";
        var tb = _secondsLeft < 300 ? TimerLow : TimerNormal;
        if (TimerBox.Background != tb) TimerBox.Background = tb;
        int wait = _secondsLeft - _submitWindow * 60;
        SubmitBtn.IsEnabled = wait <= 0 && !_submitted;
        SubmitBtn.Content = wait <= 0 ? "Nộp bài" : $"Nộp bài · mở sau {wait / 60:00}:{wait % 60:00}";
    }

    private void Submit_Click(object sender, RoutedEventArgs e)
    {
        int done = AnsweredCount();
        var r = MessageBox.Show(this, $"Bạn đã làm {done}/{_exam?.Questions.Count} câu. Sau khi nộp sẽ không sửa được bài.\n\nNộp bài ngay?", "Nộp bài", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r == MessageBoxResult.Yes) DoSubmit(auto: false);
    }

    private void DoSubmit(bool auto)
    {
        if (_submitted) return;
        // Ghi bản lưu tạm lần cuối trước khi khoá bài; chỉ xoá khi máy chủ xác nhận đã nhận bài (tin Result).
        _saveDebounce.Stop();
        SaveLocalNow();
        _submitted = true;
        FlushEssays();
        _conn?.Send(Msg.Submit, new { auto });
        OfflineBar.Visibility = Visibility.Collapsed;
        _submittedAt = DateTime.Now;
        SettingsPopup.IsOpen = false;
        ExamPanel.Visibility = Visibility.Collapsed;
        DoneExam.Text = $"{_exam?.ExamName} – {_exam?.Subject} · Mã đề {_exam?.Code}";
        DoneLine.Text = $"{_info.FullName} · SBD {_info.Sbd} · nộp lúc {_submittedAt:HH:mm}" + (auto ? " (tự động)" : "");
        ReceiptText.Text = "Mã xác nhận nộp bài: " + $"{_info.Sbd}-{_exam?.Code}-{Math.Abs((_info.Sbd + _submittedAt.Ticks).GetHashCode()) % 10000:0000}";
        Anim.Show(DonePanel);
        var st = (ScaleTransform)DoneCheck.RenderTransform;
        var pop = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.5)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 } };
        st.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        if (_offline) SetOffline(true);
    }

    private void ShowResult(bool showScore, int right, int wrong, int blank, double score)
    {
        if (showScore && _exam != null)
        {
            ScoreBox.Visibility = Visibility.Visible;
            NoScoreText.Visibility = Visibility.Collapsed;
            ScoreText.Text = Fmt(score);
            ScoreMax.Text = "/ " + Fmt(_exam.McTotal) + " điểm";
            RightText.Text = right.ToString(); WrongText.Text = wrong.ToString(); BlankText.Text = blank.ToString();
            EssayPending.Text = _exam.EssayCount > 0 ? $"Phần tự luận ({Fmt(_exam.EssayTotal)} điểm) đang chờ giáo viên chấm." : "";
            Anim.FadeIn(ScoreBox, 0.6);
        }
        else
        {
            ScoreBox.Visibility = Visibility.Collapsed;
            NoScoreText.Visibility = Visibility.Visible;
        }
    }

    private void ShowNotice(string text, bool personal)
    {
        NoticeText.Text = (personal ? "Giám thị nhắc riêng bạn: " : "Giám thị nhắc chung: ") + text;
        NoticeBar.Background = personal ? (Brush)FindResource("BadSoft") : (Brush)FindResource("WarnSoft");
        Anim.Show(NoticeBar);
        if (_submitted || !_examRunning) MessageBox.Show(this, NoticeText.Text, "iTest");
    }

    private void CloseNotice_Click(object sender, RoutedEventArgs e) => NoticeBar.Visibility = Visibility.Collapsed;

    // ---- Cài đặt ----
    private void Settings_Click(object sender, RoutedEventArgs e) => SettingsPopup.IsOpen = !SettingsPopup.IsOpen;

    private void Ime_Changed(object sender, RoutedEventArgs e)
    {
        VietInput.Enabled = ImeOff?.IsChecked != true;
        VietInput.Mode = ImeVni?.IsChecked == true ? ImeMode.Vni : ImeMode.Telex;
        if (ImeHint != null) ImeHint.Text = VietInput.Enabled ? ImeHintFor(VietInput.Mode) : "Bộ gõ của iTest đang tắt (dùng khi làm bài tiếng Anh hoặc máy đã bật Unikey).";
    }

    private static string ImeHintFor(ImeMode m) => m == ImeMode.Telex
        ? "aa→â, aw→ă, ow→ơ, uw→ư, dd→đ · dấu: s f r x j, z để bỏ dấu"
        : "6→â ê ô, 7→ơ ư, 8→ă, 9→đ · dấu: 1 2 3 4 5, 0 để bỏ dấu";

    private void Fs_Changed(object sender, RoutedEventArgs e)
    {
        if (PaperScale == null || FsSmall == null || FsLarge == null) return;
        double s = FsSmall.IsChecked == true ? 0.9 : FsLarge.IsChecked == true ? 1.2 : 1.0;
        PaperScale.ScaleX = PaperScale.ScaleY = s;
    }

    private void Math_Changed(object sender, RoutedEventArgs e) => ApplyMathVisibility();

    private void ApplyMathVisibility()
    {
        var v = MathToggle?.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        foreach (var b in _mathBars) b.Visibility = v;
    }

    // ---- Chống thoát ----
    private bool _scanning;
    /// <summary>Quét gian lận chạy ở luồng nền (liệt kê tiến trình có thể chậm), không làm giật màn hình thi.</summary>
    private async void CheatScan()
    {
        if (!_examRunning || _submitted) { _cheatTimer.Stop(); return; }
        if (_scanning) return;
        _scanning = true;
        bool lenient = _exam?.AllowDocuments == true;
        try
        {
            var findings = await Task.Run(() => AntiCheat.Scan(lenient));
            if (_examRunning && !_submitted)
                foreach (var f in findings) ReportCheat(f.Kind, f.Detail);
        }
        catch { }
        finally { _scanning = false; }
    }

    /// <summary>Báo dấu hiệu gian lận lên máy chủ (mỗi nội dung chỉ báo một lần).</summary>
    private void ReportCheat(string kind, string detail)
    {
        if (!_reported.Add(detail)) return;
        _conn?.Send(Msg.Cheat, new { kind, detail });
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (!_examRunning || _released) return;
        {
            _conn?.Send(Msg.Focus, new { lost = true, reason = "Cửa sổ iTest mất tiêu điểm" });
            Dispatcher.BeginInvoke(() => { Activate(); Topmost = true; }, DispatcherPriority.ApplicationIdle);
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_examRunning && !_released)
        {
            e.Cancel = true;
            _conn?.Send(Msg.Focus, new { lost = true, reason = "Cố đóng ứng dụng" });
            return;
        }
        KioskGuard.Leave();
        _released = true;
        _listener.Dispose();
        _conn?.Close();
        try { _player.Close(); } catch { }
        try { if (Directory.Exists(_mediaDir)) Directory.Delete(_mediaDir, true); } catch { }
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Thoát khi chưa vào phòng chờ (sau khi đăng ký chỉ máy chủ mới cho thoát).</summary>
    private void Quit_Click(object sender, RoutedEventArgs e)
    {
        if (_examRunning) return;
        if (_registered && !_released && !_offline) return;   // đang ở phòng chờ và còn kết nối: chỉ giám thị cho thoát
        _released = true;
        _conn?.Close();
        Close();
    }

    // ---- Lưu cục bộ (phòng mất kết nối / mất điện) ----
    /// <summary>Gom các lần lưu (gõ phím liên tục) lại, ghi đĩa tối đa mỗi giây một lần.</summary>
    private void SaveLocal() { _saveDebounce.Stop(); _saveDebounce.Start(); }

    private readonly object _localSaveLock = new();
    private void SaveLocalNow()
    {
        if (_submitted) return;
        // Dựng ảnh chụp trên luồng giao diện rồi ghi đĩa ở luồng nền để không giật khi đang làm bài.
        string json;
        try
        {
            json = JsonSerializer.Serialize(new { sbd = _info.Sbd, code = _exam?.Code, exam = ExamKey(),
                choices = new Dictionary<int, int>(_choices), essays = new Dictionary<int, string>(_essays),
                multi = _multi.ToDictionary(kv => kv.Key, kv => kv.Value.ToList()),
                tf = _tf.ToDictionary(kv => kv.Key, kv => new Dictionary<int, bool>(kv.Value)),
                shortAns = new Dictionary<int, string>(_short) });
        }
        catch { return; }
        var path = _localSave;
        Task.Run(() =>
        {
            try { lock (_localSaveLock) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, json); } }
            catch { }
        });
    }

    /// <summary>Dấu nhận diện bài thi, để bản lưu tạm của bài thi khác (cùng SBD, cùng mã đề) không bị nạp nhầm.</summary>
    private string ExamKey() => _exam == null ? "" : $"{_exam.ExamName}|{_exam.Subject}|{_exam.SchoolYear}|{_exam.Questions.Count}|{DateTime.Today:yyyyMMdd}";

    private void LoadLocalSave()
    {
        try
        {
            if (!File.Exists(_localSave)) return;
            var node = JsonNode.Parse(File.ReadAllText(_localSave)) as JsonObject;
            // Chỉ dùng lại bản lưu tạm của đúng thí sinh, đúng mã đề và đúng bài thi này (cùng ngày).
            if (node == null || node.Str("sbd") != _info.Sbd || node.Str("code") != _exam?.Code || node.Str("exam") != ExamKey()) return;
            var ch = node.As<Dictionary<int, int>>("choices");
            var es = node.As<Dictionary<int, string>>("essays");
            if (ch != null) foreach (var (k, v) in ch) if (!_choices.ContainsKey(k)) { _choices[k] = v; _conn?.Send(Msg.Answer, new { no = k, choice = v }); }
            if (es != null) foreach (var (k, v) in es) if (!_essays.ContainsKey(k)) { _essays[k] = v; _dirtyEssays.Add(k); }
            var mu = node.As<Dictionary<int, List<int>>>("multi");
            var tf = node.As<Dictionary<int, Dictionary<int, bool>>>("tf");
            var sh = node.As<Dictionary<int, string>>("shortAns");
            if (mu != null) foreach (var (k, v) in mu) if (!_multi.ContainsKey(k)) { _multi[k] = v.ToHashSet(); }
            if (tf != null) foreach (var (k, v) in tf) if (!_tf.ContainsKey(k)) { _tf[k] = v; }
            if (sh != null) foreach (var (k, v) in sh) if (!_short.ContainsKey(k)) { _short[k] = v; _dirtyShort.Add(k); }
            foreach (var k in _multi.Keys.Concat(_tf.Keys)) _dirtyShort.Add(k);
        }
        catch { }
    }
}

/// <summary>Dòng phòng thi trong danh sách tìm thấy.</summary>
public sealed class RoomItem
{
    public RoomBeacon Beacon { get; private set; }
    public DateTime LastSeen { get; private set; } = DateTime.Now;
    public RoomItem(RoomBeacon b) => Beacon = b;
    public void Update(RoomBeacon b) { Beacon = b; LastSeen = DateTime.Now; }
    public string Title => $"{Beacon.Room} · {Beacon.Exam}";
    public string Meta => $"{Beacon.Host} · {Beacon.Address} · Giám thị: {Beacon.Proctor} · {Beacon.Candidates} máy";
    public string BadgeText => Beacon.Open ? "Đang mở" : "Chưa mở";
    public Brush BadgeBg => Beacon.Open ? new SolidColorBrush(Color.FromRgb(0xDC, 0xFC, 0xE7)) : new SolidColorBrush(Color.FromRgb(0xF1, 0xF5, 0xF9));
    public Brush BadgeFg => Beacon.Open ? new SolidColorBrush(Color.FromRgb(0x16, 0x65, 0x34)) : new SolidColorBrush(Color.FromRgb(0x47, 0x55, 0x69));
}
