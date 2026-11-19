using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Threading;
using ITest.Core;

namespace ITest.Server;

public enum Phase { Open, Running, Collected }

/// <summary>Toàn bộ logic phòng thi: kết nối, phát đề, tính giờ, thu bài, sao lưu.</summary>
public sealed class RoomService : IDisposable
{
    private readonly Dispatcher _ui;
    private TcpListener? _listener;
    private BeaconSender? _beacon;
    private readonly DispatcherTimer _tick;
    private readonly List<Connection> _pending = new();

    public ObservableCollection<Candidate> Candidates { get; } = new();
    public ObservableCollection<FeedItem> Feed { get; } = new();

    public string Department { get; set; } = "";
    public string School { get; set; } = "";
    public string Room { get; set; } = "";
    public string Proctor { get; set; } = "";
    public string PackagePath { get; set; } = "";
    public ExamSet Exam { get; private set; } = new();
    public Dictionary<string, byte[]> Media { get; private set; } = new();
    public List<CodeLayout> Layouts { get; private set; } = new();

    public Phase Phase { get; private set; } = Phase.Open;
    public bool Paused { get; private set; }
    public int SecondsLeft { get; private set; }
    public int SubmitWindowMinutes { get; set; } = 15;
    public bool ShowScore { get; set; }
    public DateTime? StartedAt { get; private set; }
    public DateTime? EndedAt { get; private set; }
    public bool Exported { get; set; }
    public int TickCount { get; private set; }
    public string BackupDir { get; private set; } = "";
    public List<string> SpareMachines { get; } = new() { "PC-DỰ-PHÒNG-1", "PC-DỰ-PHÒNG-2", "PC-DỰ-PHÒNG-3" };

    public event Action? Changed;

    public RoomService(Dispatcher ui)
    {
        _ui = ui;
        _tick = new DispatcherTimer(DispatcherPriority.Normal, ui) { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += (_, _) => OnTick();
    }

    public void LoadPackage(ExamPackage.Content content, string path)
    {
        Exam = content.Exam;
        Media = content.Media;
        PackagePath = path;
        Layouts = Shuffler.Build(Exam);
        if (Phase == Phase.Open) SecondsLeft = Exam.Minutes * 60;
    }

    // ---------------- Mở / đóng phòng ----------------
    public void Open()
    {
        Phase = Phase.Open;
        SecondsLeft = Exam.Minutes * 60;
        BackupDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "iTest-Server", "SaoLuu",
            $"{Safe(Room)}_{DateTime.Now:yyyyMMdd_HHmm}");
        Directory.CreateDirectory(BackupDir);
        _listener = new TcpListener(IPAddress.Any, Ports.Session);
        _listener.Start();
        _ = AcceptLoop(_listener);
        _beacon = new BeaconSender(() => new RoomBeacon
        {
            Room = Room, Exam = $"{Exam.ExamName} – {Exam.Subject}", Proctor = Proctor,
            Workgroup = NetUtil.GetWorkgroup(), Host = Environment.MachineName, Address = NetUtil.LocalIPv4(),
            Open = Phase != Phase.Collected, Candidates = Candidates.Count
        });
        _beacon.Start();
        _tick.Start();
        Log("THÔNG TIN", "", $"Mở {Room} · {Exam.ExamName} – {Exam.Subject} · {Path.GetFileName(PackagePath)} · {Exam.Minutes} phút");
    }

    public void Close()
    {
        foreach (var c in Candidates) c.Conn?.Send(Msg.Release);
        Log("THÔNG TIN", "", "Đóng phòng thi · đã mở khoá tất cả máy thí sinh");
        Task.Delay(800).ContinueWith(_ => _ui.Invoke(Dispose));
    }

    /// <summary>Dùng khi thoát ứng dụng: báo các máy thí sinh thoát rồi đóng ngay.</summary>
    public void ReleaseAllAndDispose()
    {
        var sends = Candidates.Where(c => c.Conn != null).Select(c => c.Conn!.SendAsync(Msg.Release)).ToArray();
        try { Task.WaitAll(sends, 800); } catch { }
        Dispose();
    }

    public void Dispose()
    {
        _tick.Stop();
        _beacon?.Dispose();
        try { _listener?.Stop(); } catch { }
        foreach (var c in Candidates) c.Conn?.Close();
        foreach (var c in _pending.ToList()) c.Close();
    }

    private async Task AcceptLoop(TcpListener l)
    {
        while (true)
        {
            TcpClient tcp;
            try { tcp = await l.AcceptTcpClientAsync(); } catch { return; }
            var conn = new Connection(tcp);
            conn.Received += (c, m) => _ui.BeginInvoke(() => OnMessage(c, m));
            conn.Closed += c => _ui.BeginInvoke(() => OnClosed(c));
            _ui.Invoke(() => _pending.Add(conn));
            conn.Start();
            conn.Send(Msg.Welcome, new { room = Room, proctor = Proctor, exam = $"{Exam.ExamName} – {Exam.Subject}", department = Department, school = School });
        }
    }

    private Candidate? ByConn(Connection c) => Candidates.FirstOrDefault(x => x.Conn == c);

    // ---------------- Thông điệp từ máy thí sinh ----------------
    private void OnMessage(Connection conn, JsonObject m)
    {
        var cand = ByConn(conn);
        if (cand != null) cand.LastSeen = DateTime.Now;
        switch (m.Str("t"))
        {
            case Msg.Ping:
                conn.Send(Msg.Pong);
                return;
            case Msg.Hello:
                conn.Send(Msg.Welcome, new { room = Room, proctor = Proctor, exam = $"{Exam.ExamName} – {Exam.Subject}" });
                _helloMachine[conn] = m.Str("machine");
                break;
            case Msg.Register:
                OnRegister(conn, m.As<CandidateInfo>() ?? new CandidateInfo());
                break;
            case Msg.Answer:
                if (cand == null || !cand.IsActive) break;
                cand.Sheet.Choices[m.Int("no")] = m.Int("choice");
                cand.NotifyProgress();
                return;
            case Msg.Essay:
                if (cand == null || !cand.IsActive) break;
                cand.Sheet.Essays[m.Int("no")] = m.Str("text");
                cand.NotifyProgress();
                return;
            case Msg.Submit:
                if (cand == null || cand.Status != CandStatus.Doing) break;
                FinishCandidate(cand, m.Bool("auto") ? "tự động nộp bài khi hết giờ" : "đã nộp bài");
                break;
            case Msg.Focus:
                if (cand != null && cand.IsActive)
                    Log("CẢNH BÁO", cand.Machine, $"{m.Str("reason")} · {cand.FullName}");
                break;
        }
        Changed?.Invoke();
    }

    private readonly Dictionary<Connection, string> _helloMachine = new();

    private void OnRegister(Connection conn, CandidateInfo info)
    {
        info.Sbd = info.Sbd.Trim();
        var machine = _helloMachine.TryGetValue(conn, out var mm) ? mm : conn.RemoteAddress;
        var existing = Candidates.FirstOrDefault(c => string.Equals(c.Sbd, info.Sbd, StringComparison.OrdinalIgnoreCase)) ?? ByConn(conn);
        // Cùng một máy kết nối lại (mạng chập chờn, máy chủ chưa kịp phát hiện) thì cho nhận lại phiên cũ.
        if (existing != null && existing.Conn != null && existing.Conn != conn && string.Equals(existing.Machine, machine, StringComparison.OrdinalIgnoreCase))
        {
            var old = existing.Conn;
            existing.Conn = null;
            old.Close();
            if (existing.Status == CandStatus.Doing) existing.Status = CandStatus.Lost;
        }
        if (existing != null && existing.Conn != null && existing.Conn != conn && existing.Status != CandStatus.Lost && !existing.MoveApproved
            && existing.Status != CandStatus.Moving)
        {
            conn.Send(Msg.Reject, new { reason = $"Số báo danh {info.Sbd} đang được dùng ở máy {existing.Machine}. Báo giám thị nếu cần chuyển máy." });
            return;
        }
        _pending.Remove(conn);
        Candidate cand;
        if (existing != null)
        {
            cand = existing;
            var oldMachine = cand.Machine;
            cand.Conn = conn;
            cand.Machine = machine;
            cand.Ip = conn.RemoteAddress;
            cand.Sheet.Candidate = info;
            if (cand.Status is CandStatus.Lost or CandStatus.Moving)
            {
                var wasMoving = cand.Status == CandStatus.Moving || cand.MoveApproved;
                cand.MoveApproved = false;
                cand.Status = CandStatus.Doing;
                Log(wasMoving ? "CHUYỂN MÁY" : "THAM GIA", cand.Machine,
                    wasMoving ? $"{info.FullName} đã đăng nhập máy mới (từ {oldMachine}) · tiếp tục từ bản lưu, còn {Fmt(LeftOf(cand))}"
                              : $"đã kết nối lại · {info.FullName}");
            }
            else if (cand.Status is CandStatus.Waiting or CandStatus.Connected)
            {
                cand.EditRequested = false;
                cand.Status = Phase == Phase.Open ? CandStatus.Waiting : cand.Status;
                Log("THÔNG TIN", cand.Machine, $"cập nhật thông tin thí sinh · {info.FullName}");
            }
        }
        else
        {
            cand = new Candidate { Conn = conn, Index = Candidates.Count, Machine = machine, Ip = conn.RemoteAddress };
            cand.Sheet.Candidate = info;
            cand.Status = CandStatus.Waiting;
            Candidates.Add(cand);
            Log("THAM GIA", cand.Machine, $"vừa tham gia phòng thi · {info.FullName} · SBD {info.Sbd}");
        }
        conn.Send(Msg.Registered);
        if (Phase == Phase.Running && cand.Status is CandStatus.Waiting or CandStatus.Doing)
        {
            if (cand.Status == CandStatus.Waiting) { AssignCode(cand); cand.Status = CandStatus.Doing; }
            SendExam(cand);
            conn.Send(Msg.Resume, new { choices = cand.Sheet.Choices, essays = cand.Sheet.Essays });
        }
        else if (cand.Status == CandStatus.Done)
            SendResult(cand);
        cand.Left = Fmt(LeftOf(cand));
        cand.LastSeen = DateTime.Now;
    }

    private void OnClosed(Connection conn)
    {
        _pending.Remove(conn);
        _helloMachine.Remove(conn);
        var cand = ByConn(conn);
        if (cand == null) return;
        cand.Conn = null;
        if (cand.Status == CandStatus.Doing)
        {
            cand.Status = cand.MoveApproved ? CandStatus.Moving : CandStatus.Lost;
            Log("NGOẠI TUYẾN", cand.Machine, $"vừa ngoại tuyến · máy chủ không tìm thấy máy · tạm dừng giờ của {cand.FullName}");
        }
        else if (cand.Status == CandStatus.Waiting)
            Log("NGOẠI TUYẾN", cand.Machine, $"rời phòng chờ · {cand.FullName}");
        Changed?.Invoke();
    }

    // ---------------- Phát đề, tính giờ ----------------
    private void AssignCode(Candidate c)
    {
        var layout = Layouts[c.Index % Math.Max(1, Layouts.Count)];
        c.Sheet.Code = layout.Code;
        c.TotalQuestions = Exam.Questions.Count;
    }

    private void SendExam(Candidate c)
    {
        var layout = Layouts.First(l => l.Code == c.Sheet.Code);
        var ce = Shuffler.ToClient(Exam, layout, Media, Department, School);
        c.Conn?.Send(Msg.Exam, new { exam = ce, secondsLeft = LeftOf(c), submitWindow = SubmitWindowMinutes });
    }

    public void Start()
    {
        if (Phase != Phase.Open) return;
        Phase = Phase.Running;
        StartedAt = DateTime.Now;
        SecondsLeft = Exam.Minutes * 60;
        foreach (var c in Candidates.Where(c => c.Status == CandStatus.Waiting))
        {
            AssignCode(c);
            c.Status = CandStatus.Doing;
            SendExam(c);
        }
        Log("THÔNG TIN", "", $"Đã phát đề cho {Candidates.Count(c => c.Status == CandStatus.Doing)} máy · bắt đầu tính giờ {Exam.Minutes} phút");
        Changed?.Invoke();
    }

    public int LeftOf(Candidate c) => Phase == Phase.Open ? Exam.Minutes * 60 : Math.Max(0, SecondsLeft + c.ExtraSeconds);

    private void OnTick()
    {
        TickCount++;
        if (Phase == Phase.Running && !Paused)
        {
            if (SecondsLeft > 0) SecondsLeft--;
            foreach (var c in Candidates)
            {
                if (c.Status is CandStatus.Lost or CandStatus.Moving) c.ExtraSeconds++;
                if (c.Status == CandStatus.Doing && LeftOf(c) <= 0)
                {
                    c.Conn?.Send(Msg.Collect);
                    FinishCandidate(c, "hết giờ · đã tự động thu bài");
                }
            }
            if (Candidates.Count > 0 && Candidates.All(c => !c.IsActive) && SecondsLeft == 0) EndExam("Hết giờ · đã thu bài tất cả thí sinh");
        }
        // Máy không gửi nhịp tim quá 10 giây (mất điện, rút dây mạng…) được coi là mất kết nối.
        foreach (var c in Candidates.Where(c => c.Conn != null && (DateTime.Now - c.LastSeen).TotalSeconds > 10).ToList())
            c.Conn!.Close();
        foreach (var c in Candidates)
        {
            c.Left = Phase == Phase.Collected ? "—" : Fmt(LeftOf(c)) + (c.ExtraSeconds > 0 && Phase == Phase.Running ? "*" : "");
            if (Phase == Phase.Running && c.Status == CandStatus.Doing && TickCount % 5 == 0)
                c.Conn?.Send(Msg.Time, new { secondsLeft = LeftOf(c), paused = Paused });
        }
        if (TickCount % 30 == 0) Backup();
        Changed?.Invoke();
    }

    public void TogglePause()
    {
        Paused = !Paused;
        foreach (var c in Candidates.Where(c => c.Status == CandStatus.Doing)) c.Conn?.Send(Msg.Time, new { secondsLeft = LeftOf(c), paused = Paused });
        Log("THÔNG TIN", "", Paused ? "Tạm dừng tính giờ toàn phòng" : "Tiếp tục tính giờ toàn phòng");
    }

    public void AddMinutesAll(int minutes)
    {
        SecondsLeft += minutes * 60;
        foreach (var c in Candidates.Where(c => c.Status == CandStatus.Doing)) c.Conn?.Send(Msg.Time, new { secondsLeft = LeftOf(c), paused = Paused });
        Log("CỘNG GIỜ", "", $"Cộng thêm {minutes} phút cho toàn phòng");
    }

    public bool CanCollectAll => Phase == Phase.Running && SecondsLeft <= SubmitWindowMinutes * 60;

    public void CollectAll()
    {
        if (!CanCollectAll) return;
        foreach (var c in Candidates.Where(c => c.IsActive).ToList())
        {
            c.Conn?.Send(Msg.Collect);
            FinishCandidate(c, "đã được thu bài");
        }
        EndExam("Giám thị đã thu bài tất cả thí sinh");
    }

    private void EndExam(string text)
    {
        if (Phase == Phase.Collected) return;
        Phase = Phase.Collected;
        EndedAt = DateTime.Now;
        Backup();
        Log("NỘP BÀI", "", text);
        Changed?.Invoke();
    }

    private void FinishCandidate(Candidate c, string text)
    {
        if (c.Status is CandStatus.Done or CandStatus.Revoked) return;
        c.Sheet.SubmittedAt = DateTime.Now;
        c.Status = CandStatus.Done;
        Log("NỘP BÀI", c.Machine, $"{text} · {c.FullName} ({c.Answered}/{c.TotalQuestions})");
        SendResult(c);
    }

    private void SendResult(Candidate c)
    {
        var layout = Layouts.FirstOrDefault(l => l.Code == c.Sheet.Code);
        if (layout == null) return;
        var r = Scoring.GradeMc(Exam, layout, c.Sheet);
        c.Conn?.Send(Msg.Result, new { showScore = ShowScore, right = r.Right, wrong = r.Wrong, blank = r.Blank, score = r.Score });
    }

    // ---------------- Xử lý thí sinh ----------------
    public void NoticeAll(string text)
    {
        foreach (var c in Candidates.Where(c => c.Conn != null)) c.Conn!.Send(Msg.Notice, new { kind = "all", text });
        Log("NHẮC NHỞ", "", $"Nhắc nhở chung tới {Candidates.Count(c => c.Conn != null)} máy: “{text}”");
    }

    public void Warn(Candidate c, string text)
    {
        if (c.WarnedAt != null || !c.IsActive) return;
        c.WarnedAt = DateTime.Now.ToString("HH:mm");
        c.Sheet.Warnings++;
        c.Conn?.Send(Msg.Notice, new { kind = "me", text });
        c.RefreshAll();
        Log("NHẮC NHỞ", c.Machine, $"bị nhắc nhở · {c.FullName}: “{text}”");
    }

    public void Revoke(Candidate c)
    {
        if (c.WarnedAt == null || !c.IsActive) return;
        c.RevokedAt = DateTime.Now.ToString("HH:mm");
        c.Sheet.Revoked = true;
        c.Conn?.Send(Msg.Notice, new { kind = "me", text = "Bài thi của bạn đã bị thu do vi phạm quy chế. Kết quả thi bị huỷ." });
        c.Conn?.Send(Msg.Collect);
        c.Status = CandStatus.Revoked;
        Log("ĐÌNH CHỈ", c.Machine, $"bị thu bài · kết quả thi bị huỷ · {c.FullName}");
    }

    public void ApproveMove(Candidate c)
    {
        if (!c.IsActive) return;
        c.MoveApproved = true;
        if (c.Status == CandStatus.Lost) c.Status = CandStatus.Moving;
        else
        {
            // máy cũ vẫn đang kết nối: ngắt để thí sinh đăng nhập máy khác
            c.Conn?.Send(Msg.Release);
            c.Conn?.Close();
            c.Status = CandStatus.Moving;
        }
        Log("CHUYỂN MÁY", c.Machine, $"cho phép {c.FullName} chuyển sang máy khác · giữ bài đến {c.Answered} câu, thời gian còn {Fmt(LeftOf(c))} · thí sinh đăng nhập lại bằng SBD {c.Sbd}");
    }

    public void AddMinutes(Candidate c, int minutes)
    {
        if (!c.IsActive) return;
        c.ExtraSeconds += minutes * 60;
        c.Conn?.Send(Msg.Time, new { secondsLeft = LeftOf(c), paused = Paused });
        Log("CỘNG GIỜ", c.Machine, $"được cộng thêm {minutes} phút riêng · {c.FullName}");
    }

    /// <summary>Ngắt một máy không đang làm bài: máy thí sinh nhận lệnh thoát ứng dụng.</summary>
    public void Disconnect(Candidate c)
    {
        if (c.IsActive || c.Conn == null) return;
        var conn = c.Conn;
        conn.Send(Msg.Release);
        c.Conn = null;
        Task.Delay(500).ContinueWith(_ => conn.Close());
        if (c.Status is CandStatus.Waiting or CandStatus.Connected)
        {
            Candidates.Remove(c);
            for (int i = 0; i < Candidates.Count; i++) Candidates[i].Index = i;
        }
        Log("NGẮT KẾT NỐI", c.Machine, $"giám thị đã ngắt kết nối · {c.FullName} · iTest trên máy đã thoát");
    }

    public void RequestEdit(Candidate c, string reason)
    {
        if (Phase != Phase.Open || c.Conn == null) return;
        c.EditRequested = true;
        c.Conn.Send(Msg.EditRequest, new { reason });
        Log("YÊU CẦU", c.Machine, $"được yêu cầu sửa thông tin · {c.FullName}: {reason}");
    }

    // ---------------- Nhật ký, sao lưu ----------------
    private static readonly Dictionary<string, (Color bg, Color fg)> TagColors = new()
    {
        ["THAM GIA"] = (Color.FromRgb(0xDB, 0xEA, 0xFE), Color.FromRgb(0x1E, 0x3A, 0x8A)),
        ["NGẮT KẾT NỐI"] = (Color.FromRgb(0xF1, 0xF5, 0xF9), Color.FromRgb(0x47, 0x55, 0x69)),
        ["NGOẠI TUYẾN"] = (Color.FromRgb(0xFE, 0xE2, 0xE2), Color.FromRgb(0xB9, 0x1C, 0x1C)),
        ["NỘP BÀI"] = (Color.FromRgb(0xDC, 0xFC, 0xE7), Color.FromRgb(0x16, 0x65, 0x34)),
        ["NHẮC NHỞ"] = (Color.FromRgb(0xFE, 0xF3, 0xC7), Color.FromRgb(0x78, 0x35, 0x0F)),
        ["ĐÌNH CHỈ"] = (Color.FromRgb(0xED, 0xE9, 0xFE), Color.FromRgb(0x5B, 0x21, 0xB6)),
        ["CHUYỂN MÁY"] = (Color.FromRgb(0xCF, 0xFA, 0xFE), Color.FromRgb(0x15, 0x5E, 0x75)),
        ["CỘNG GIỜ"] = (Color.FromRgb(0xE0, 0xF2, 0xFE), Color.FromRgb(0x07, 0x59, 0x85)),
        ["CẢNH BÁO"] = (Color.FromRgb(0xFF, 0xED, 0xD5), Color.FromRgb(0x9A, 0x34, 0x12)),
        ["YÊU CẦU"] = (Color.FromRgb(0xFF, 0xED, 0xD5), Color.FromRgb(0x9A, 0x34, 0x12)),
        ["XUẤT FILE"] = (Color.FromRgb(0xDC, 0xFC, 0xE7), Color.FromRgb(0x16, 0x65, 0x34)),
        ["THÔNG TIN"] = (Color.FromRgb(0xF1, 0xF5, 0xF9), Color.FromRgb(0x33, 0x41, 0x55)),
    };

    public void Log(string tag, string pc, string text)
    {
        var (bg, fg) = TagColors.TryGetValue(tag, out var t) ? t : TagColors["THÔNG TIN"];
        Feed.Insert(0, new FeedItem(DateTime.Now.ToString("HH:mm:ss"), tag, pc, text, new SolidColorBrush(bg), new SolidColorBrush(fg)));
        try
        {
            if (BackupDir.Length > 0)
                File.AppendAllText(Path.Combine(BackupDir, "nhat-ky.txt"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\t{tag}\t{pc}\t{text}{Environment.NewLine}");
        }
        catch { }
        Changed?.Invoke();
    }

    public void Backup()
    {
        try
        {
            if (BackupDir.Length == 0) return;
            var json = JsonSerializer.Serialize(Candidates.Select(c => c.Sheet).ToList(), new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(BackupDir, "bai-lam.json"), json);
        }
        catch { }
    }

    public static string Fmt(int secs) => $"{secs / 60:00}:{secs % 60:00}";
    private static string Safe(string s) => string.Concat(s.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) || ch == ' ' ? '_' : ch));
}
