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
    private readonly Dictionary<int, int> _choices = new();
    private readonly Dictionary<int, string> _essays = new();
    private readonly Dictionary<int, FrameworkElement> _cards = new();
    private readonly Dictionary<int, List<Border>> _optionCircles = new();
    private readonly Dictionary<int, List<Border>> _bubbles = new();
    private readonly List<WrapPanel> _mathBars = new();
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _essayFlush = new() { Interval = TimeSpan.FromSeconds(2) };
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
        _essayFlush.Tick += (_, _) => FlushEssays();
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

    private async Task ConnectTo(RoomBeacon b)
    {
        FindError.Text = "";
        try
        {
            var c = await Connection.ConnectAsync(b.Address, b.Port);
            AttachConnection(c);
            _room = b;
            c.Send(Msg.Hello, new { machine = Environment.MachineName, workgroup = _listener.Workgroup, ip = NetUtil.LocalIPv4() });
            RoomLine.Text = $"{b.Room} · {b.Exam} · Giám thị: {b.Proctor}";
            FindPanel.Visibility = Visibility.Collapsed;
            Anim.Show(InfoPanel);
        }
        catch (Exception ex) { FindError.Text = "Không kết nối được máy chủ: " + ex.Message; }
    }

    private void AttachConnection(Connection c)
    {
        _conn = c;
        c.Received += (_, m) => Dispatcher.BeginInvoke(() => OnMessage(m));
        c.Closed += _ => Dispatcher.BeginInvoke(OnDisconnected);
        c.Start();
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
        _conn?.Send(Msg.Register, _info);
    }

    private void SetInfoLocked(bool locked)
    {
        foreach (var tb in new[] { SbdBox, NameBox, DobBox, ClassBox }) tb.IsEnabled = !locked;
        AgreeBox.IsEnabled = !locked;
        RegisterBtn.Visibility = locked ? Visibility.Collapsed : Visibility.Visible;
        QuitInfoBtn.Visibility = locked || _registered ? Visibility.Collapsed : Visibility.Visible;
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
                break;
            case Msg.Registered:
                _registered = true;
                SetInfoLocked(true);
                break;
            case Msg.Reject:
                InfoError.Text = m.Str("reason");
                SetInfoLocked(false);
                break;
            case Msg.EditRequest:
                EditReqText.Text = "Giám thị yêu cầu sửa thông tin: " + m.Str("reason");
                Anim.Show(EditReqBox);
                break;
            case Msg.Exam:
                _exam = m.As<ClientExam>("exam");
                _secondsLeft = m.Int("secondsLeft");
                _submitWindow = m.Int("submitWindow", 15);
                if (_exam != null) StartExam();
                break;
            case Msg.Resume:
                var ch = m.As<Dictionary<int, int>>("choices");
                var es = m.As<Dictionary<int, string>>("essays");
                if (ch != null) foreach (var (k, v) in ch) _choices[k] = v;
                if (es != null) foreach (var (k, v) in es) _essays[k] = v;
                RefreshAnswers();
                break;
            case Msg.Time:
                _secondsLeft = m.Int("secondsLeft");
                _paused = m.Bool("paused");
                UpdateClock();
                break;
            case Msg.Notice:
                ShowNotice(m.Str("text"), m.Str("kind") == "me");
                break;
            case Msg.Collect:
                if (_examRunning && !_submitted) DoSubmit(auto: true);
                break;
            case Msg.Result:
                ShowResult(m.Bool("showScore"), m.Int("right"), m.Int("wrong"), m.Int("blank"), m["score"]?.GetValue<double>() ?? 0);
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

    private async void OnDisconnected()
    {
        if (_released) return;
        if (!_examRunning || _submitted)
        {
            if (!_submitted) { FindError.Text = "Mất kết nối với máy chủ."; }
            return;
        }
        OfflineBar.Visibility = Visibility.Visible;
        while (_room != null && !_released)
        {
            await Task.Delay(3000);
            try
            {
                var c = await Connection.ConnectAsync(_room.Address, _room.Port);
                AttachConnection(c);
                c.Send(Msg.Hello, new { machine = Environment.MachineName, workgroup = _listener.Workgroup, ip = NetUtil.LocalIPv4() });
                c.Send(Msg.Register, _info);
                foreach (var (k, v) in _choices) c.Send(Msg.Answer, new { no = k, choice = v });
                foreach (var (k, v) in _essays) c.Send(Msg.Essay, new { no = k, text = v });
                OfflineBar.Visibility = Visibility.Collapsed;
                return;
            }
            catch { }
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
        BuildPaper();
        Anim.Show(ExamPanel);
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
            _essayFlush.Start();
        }
        UpdateClock();
    }

    private void BuildPaper()
    {
        if (_exam == null) return;
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
        var mcs = _exam.Questions.Where(q => q.Type == QuestionType.MultipleChoice).ToList();
        var ess = _exam.Questions.Where(q => q.Type == QuestionType.Essay).ToList();
        if (mcs.Count > 0)
        {
            if (twoParts)
            {
                PaperHost.Children.Add(new TextBlock { Text = $"PHẦN I. TRẮC NGHIỆM ({Fmt(_exam.McTotal)} điểm)", FontWeight = FontWeights.Bold });
                PaperHost.Children.Add(new TextBlock { Text = "Mỗi câu hỏi chỉ chọn một phương án.", FontStyle = FontStyles.Italic, Margin = new Thickness(0, 2, 0, 12) });
            }
            foreach (var q in mcs) PaperHost.Children.Add(BuildMcCard(q));
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

    private FrameworkElement BuildMcCard(ClientQuestion q)
    {
        var card = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        var tb = new TextBlock { TextWrapping = TextWrapping.Wrap };
        tb.Inlines.Add(new System.Windows.Documents.Run($"Câu {q.No}. ") { FontWeight = FontWeights.Bold });
        tb.Inlines.Add(new System.Windows.Documents.Run(q.Text));
        card.Children.Add(tb);
        AddMedia(card, q);
        bool shortOpts = q.Options.All(o => o.Length <= 16);
        var grid = new UniformGrid { Columns = shortOpts ? 4 : 2, Margin = new Thickness(30, 6, 0, 0) };
        var circles = new List<Border>();
        for (int k = 0; k < q.Options.Count; k++)
        {
            int choice = k;
            var circle = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(15), BorderThickness = new Thickness(2), BorderBrush = Brushes.Transparent };
            circle.Child = new TextBlock { Text = ((char)('A' + k)).ToString(), FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(circle);
            row.Children.Add(new TextBlock { Text = q.Options[k], TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) });
            var btn = new Button { Content = row, Background = Brushes.Transparent, BorderThickness = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(4), MinHeight = 36, HorizontalContentAlignment = HorizontalAlignment.Left };
            btn.Click += (_, _) => Choose(q.No, choice);
            circles.Add(circle);
            grid.Children.Add(btn);
        }
        _optionCircles[q.No] = circles;
        card.Children.Add(grid);
        _cards[q.No] = card;
        return card;
    }

    private FrameworkElement BuildEssayCard(ClientQuestion q)
    {
        var card = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
        var tb = new TextBlock { TextWrapping = TextWrapping.Wrap };
        tb.Inlines.Add(new System.Windows.Documents.Run($"Câu {q.No} ") { FontWeight = FontWeights.Bold });
        tb.Inlines.Add(new System.Windows.Documents.Run($"({Fmt(q.Points)} điểm). ") { FontStyle = FontStyles.Italic });
        tb.Inlines.Add(new System.Windows.Documents.Run(q.Text));
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
            int plays = 0;
            var path = Path.Combine(Path.GetTempPath(), $"itest_audio_{q.No}.mp3");
            File.WriteAllBytes(path, Convert.FromBase64String(q.AudioBase64));
            var player = new MediaPlayer();
            var btn = new Button { Content = $"▶ Nghe âm thanh (còn {q.AudioMaxPlays} lần)", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(30, 8, 0, 0) };
            btn.Click += (_, _) =>
            {
                if (plays >= q.AudioMaxPlays) return;
                plays++;
                player.Open(new Uri(path));
                player.Play();
                btn.Content = plays >= q.AudioMaxPlays ? "Đã hết lượt nghe" : $"▶ Nghe lại (còn {q.AudioMaxPlays - plays} lần)";
                btn.IsEnabled = plays < q.AudioMaxPlays;
            };
            card.Children.Add(btn);
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
                var bub = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), BorderThickness = new Thickness(1.5), BorderBrush = Brushes.Gray, Background = Brushes.White };
                bub.Child = new TextBlock { Text = ((char)('A' + k)).ToString(), FontSize = 10, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.Gray };
                var b = new Button { Content = bub, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), MinHeight = 28 };
                b.Click += (_, _) => Choose(no, choice);
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
        var ink = new SolidColorBrush(Color.FromRgb(0x1E, 0x3A, 0x8A));
        foreach (var (no, circles) in _optionCircles)
            for (int k = 0; k < circles.Count; k++)
            {
                bool on = _choices.TryGetValue(no, out var c) && c == k;
                circles[k].BorderBrush = on ? ink : Brushes.Transparent;
                ((TextBlock)circles[k].Child).Foreground = on ? ink : Brushes.Black;
            }
        foreach (var (no, bubs) in _bubbles)
            for (int k = 0; k < bubs.Count; k++)
            {
                bool on = _choices.TryGetValue(no, out var c) && c == k;
                bubs[k].Background = on ? ink : Brushes.White;
                bubs[k].BorderBrush = on ? ink : Brushes.Gray;
                ((TextBlock)bubs[k].Child).Foreground = on ? Brushes.White : Brushes.Gray;
            }
        UpdateProgress();
    }

    private void UpdateProgress()
    {
        if (_exam == null) return;
        int done = _choices.Count + _essays.Count(kv => !string.IsNullOrWhiteSpace(kv.Value));
        int total = _exam.Questions.Count;
        ProgressText.Text = $"Đã làm {done}/{total}";
        ProgressBar.Value = total == 0 ? 0 : (double)done / total;
    }

    private void FlushEssays()
    {
        if (_conn == null || _dirtyEssays.Count == 0) return;
        foreach (var no in _dirtyEssays.ToList())
            _conn.Send(Msg.Essay, new { no, text = _essays.TryGetValue(no, out var t) ? t : "" });
        _dirtyEssays.Clear();
    }

    private void OnClock()
    {
        if (!_examRunning || _submitted) return;
        if (!_paused && _secondsLeft > 0) _secondsLeft--;
        UpdateClock();
        if (_secondsLeft == 0) DoSubmit(auto: true);
    }

    private void UpdateClock()
    {
        TimerText.Text = $"{_secondsLeft / 60:00}:{_secondsLeft % 60:00}";
        TimerBox.Background = _secondsLeft < 300 ? new SolidColorBrush(Color.FromRgb(0xB9, 0x1C, 0x1C)) : new SolidColorBrush(Color.FromRgb(0x0F, 0x17, 0x2A));
        int wait = _secondsLeft - _submitWindow * 60;
        SubmitBtn.IsEnabled = wait <= 0 && !_submitted;
        SubmitBtn.Content = wait <= 0 ? "Nộp bài" : $"Nộp bài · mở sau {wait / 60:00}:{wait % 60:00}";
    }

    private void Submit_Click(object sender, RoutedEventArgs e)
    {
        int done = _choices.Count + _essays.Count(kv => !string.IsNullOrWhiteSpace(kv.Value));
        var r = MessageBox.Show(this, $"Bạn đã làm {done}/{_exam?.Questions.Count} câu. Sau khi nộp sẽ không sửa được bài.\n\nNộp bài ngay?", "Nộp bài", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r == MessageBoxResult.Yes) DoSubmit(auto: false);
    }

    private void DoSubmit(bool auto)
    {
        if (_submitted) return;
        _submitted = true;
        FlushEssays();
        _conn?.Send(Msg.Submit, new { auto });
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
        try { File.Delete(_localSave); } catch { }
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
        VietInput.Mode = ImeVni?.IsChecked == true ? ImeMode.Vni : ImeMode.Telex;
        if (ImeHint != null) ImeHint.Text = ImeHintFor(VietInput.Mode);
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
        _listener.Dispose();
        _conn?.Close();
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Thoát khi chưa vào phòng chờ (sau khi đăng ký chỉ máy chủ mới cho thoát).</summary>
    private void Quit_Click(object sender, RoutedEventArgs e)
    {
        if (_examRunning || _registered && !_released) return;
        _conn?.Close();
        Close();
    }

    // ---- Lưu cục bộ (phòng mất kết nối / mất điện) ----
    private void SaveLocal()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_localSave)!);
            File.WriteAllText(_localSave, JsonSerializer.Serialize(new { sbd = _info.Sbd, code = _exam?.Code, choices = _choices, essays = _essays }));
        }
        catch { }
    }

    private void LoadLocalSave()
    {
        try
        {
            if (!File.Exists(_localSave)) return;
            var node = JsonNode.Parse(File.ReadAllText(_localSave)) as JsonObject;
            if (node == null || node.Str("sbd") != _info.Sbd || node.Str("code") != _exam?.Code) return;
            var ch = node.As<Dictionary<int, int>>("choices");
            var es = node.As<Dictionary<int, string>>("essays");
            if (ch != null) foreach (var (k, v) in ch) if (!_choices.ContainsKey(k)) { _choices[k] = v; _conn?.Send(Msg.Answer, new { no = k, choice = v }); }
            if (es != null) foreach (var (k, v) in es) if (!_essays.ContainsKey(k)) { _essays[k] = v; _dirtyEssays.Add(k); }
        }
        catch { }
    }
}

/// <summary>Dòng phòng thi trong danh sách tìm thấy.</summary>
public sealed class RoomItem
{
    public RoomBeacon Beacon { get; private set; }
    public RoomItem(RoomBeacon b) => Beacon = b;
    public void Update(RoomBeacon b) => Beacon = b;
    public string Title => $"{Beacon.Room} · {Beacon.Exam}";
    public string Meta => $"{Beacon.Host} · {Beacon.Address} · Giám thị: {Beacon.Proctor} · {Beacon.Candidates} máy";
    public string BadgeText => Beacon.Open ? "Đang mở" : "Chưa mở";
    public Brush BadgeBg => Beacon.Open ? new SolidColorBrush(Color.FromRgb(0xDC, 0xFC, 0xE7)) : new SolidColorBrush(Color.FromRgb(0xF1, 0xF5, 0xF9));
    public Brush BadgeFg => Beacon.Open ? new SolidColorBrush(Color.FromRgb(0x16, 0x65, 0x34)) : new SolidColorBrush(Color.FromRgb(0x47, 0x55, 0x69));
}
