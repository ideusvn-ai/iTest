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

/// <summary>Ảnh chụp trạng thái phòng thi, ghi ra đĩa vài giây một lần để khôi phục khi máy chủ gặp sự cố.</summary>
public sealed class RoomState
{
    public string Department { get; set; } = "";
    public string School { get; set; } = "";
    public string Room { get; set; } = "";
    public string Proctor { get; set; } = "";
    public string RoomPin { get; set; } = "";
    public string PackagePath { get; set; } = "";
    /// <summary>Dấu vân tay SHA-256 của bộ đề đã dùng; khi khôi phục phải chọn đúng bộ đề có cùng dấu này.</summary>
    public string ExamHash { get; set; } = "";
    public string BackupDir { get; set; } = "";
    public Phase Phase { get; set; }
    public int SecondsLeft { get; set; }
    public int SubmitWindowMinutes { get; set; }
    public bool ShowScore { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public bool Exported { get; set; }
    public bool Closed { get; set; }
    public DateTime SavedAt { get; set; }
    public List<CandidateState> Candidates { get; set; } = new();

    public string Title => $"{Room} · lưu lúc {SavedAt:HH:mm:ss dd/MM} · {Candidates.Count} thí sinh · " +
                           (Phase == Phase.Running ? "đang thi" : Phase == Phase.Collected ? "đã thu bài, chưa đóng phòng" : "chưa phát đề");
}

public sealed class CandidateState
{
    public AnswerSheet Sheet { get; set; } = new();
    public CandStatus Status { get; set; }
    public int Index { get; set; }
    public int ExtraSeconds { get; set; }
    public string? WarnedAt { get; set; }
    public string? RevokedAt { get; set; }
    public bool MoveApproved { get; set; }
    public int TotalQuestions { get; set; }
    public int CheatCount { get; set; }
    public string Token { get; set; } = "";
    public string Machine { get; set; } = "";
    public string Ip { get; set; } = "";
}

/// <summary>Toàn bộ logic phòng thi: kết nối, phát đề, tính giờ, thu bài, sao lưu.</summary>
public sealed class RoomService : IDisposable
{
    private readonly Dispatcher _ui;
    private TcpListener? _listener;
    private BeaconSender? _beacon;
    /// <summary>Mã phiên quảng bá của phòng đang mở (để nhận ra gói tin của chính mình).</summary>
    public string? ServerId => _beacon?.ServerId;
    /// <summary>Mã phòng thi (4 chữ số) giám thị đọc cho thí sinh nhập; trộn vào khoá phiên để chống máy chủ giả.</summary>
    public string RoomPin { get; private set; } = "";
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
    private bool _showScore;
    /// <summary>Cho thí sinh xem điểm trắc nghiệm sau khi nộp. Đổi lựa chọn thì gửi lại kết quả cho các máy đã nộp.</summary>
    public bool ShowScore
    {
        get => _showScore;
        set
        {
            if (_showScore == value) return;
            _showScore = value;
            foreach (var c in Candidates.Where(c => c.Conn != null && c.Status == CandStatus.Done)) SendResult(c);
        }
    }
    /// <summary>Dấu vân tay của bộ đề đang dùng.</summary>
    public string ExamHash { get; private set; } = "";
    /// <summary>Đang xuất kết quả: không được đóng phòng hay thoát cho tới khi xuất xong.</summary>
    public bool Exporting { get; set; }
    private bool _collectAllPending;
    private readonly OrderedWriter _writer = new();
    private readonly Dictionary<string, DateTime> _rejectLogged = new();
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
        ExamHash = content.Fingerprint;
        PackagePath = path;
        Layouts = Shuffler.Build(Exam);
        _clientExams.Clear();
        if (Phase == Phase.Open) SecondsLeft = Exam.Minutes * 60;
    }

    // Đề theo từng mã chỉ dựng một lần (kèm ảnh/âm thanh) rồi dùng chung cho mọi máy cùng mã.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<ClientExam>> _clientExams = new();
    private ClientExam ClientExamFor(CodeLayout layout)
    {
        var exam = Exam; var media = Media; var dept = Department; var school = School; var hash = ExamHash;
        return _clientExams.GetOrAdd(layout.Code, _ => new Lazy<ClientExam>(() =>
        {
            var ce = Shuffler.ToClient(exam, layout, media, dept, school);
            ce.Fingerprint = hash;
            return ce;
        })).Value;
    }

    // ---------------- Mở / đóng phòng ----------------
    public void Open(string? existingBackupDir = null)
    {
        Phase = Phase.Open;
        SecondsLeft = Exam.Minutes * 60;
        if (RoomPin.Length == 0) RoomPin = Random.Shared.Next(0, 10000).ToString("0000");
        BackupDir = existingBackupDir ?? Path.Combine(BackupRoot, $"{Safe(Room)}_{DateTime.Now:yyyyMMdd_HHmm}");
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
        _lastWall = _wall.Elapsed.TotalSeconds; _carry = 0;
        _tick.Start();
        if (existingBackupDir == null) Log("THÔNG TIN", "", $"Mở {Room} · {Exam.ExamName} – {Exam.Subject} · {Path.GetFileName(PackagePath)} · {Exam.Minutes} phút");
    }

    public void Close()
    {
        SaveState(closed: true, sync: true);
        foreach (var c in Candidates) c.Conn?.Send(Msg.Release);
        Log("THÔNG TIN", "", "Đóng phòng thi · đã mở khoá tất cả máy thí sinh");
        Task.Delay(800).ContinueWith(_ => _ui.Invoke(Dispose));
    }

    /// <summary>Dùng khi thoát ứng dụng: báo các máy thí sinh thoát rồi đóng ngay.</summary>
    public void ReleaseAllAndDispose()
    {
        SaveState(closed: true, sync: true);
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
            // Máy thí sinh gửi nhịp tim 3 giây một lần; kết nối im lặng quá 30 giây (kể cả chưa đăng ký) bị đóng để không chiếm chỗ.
            var conn = new Connection(tcp, isServer: true, RoomPin) { IdleTimeout = TimeSpan.FromSeconds(30) };
            conn.Received += (c, m) => _ui.BeginInvoke(() => OnMessage(c, m));
            conn.Closed += c => _ui.BeginInvoke(() => OnClosed(c));
            _ui.Invoke(() =>
            {
                // Giới hạn số kết nối chưa đăng ký để một máy không thể mở hàng loạt kết nối làm nghẽn máy chủ.
                while (_pending.Count >= MaxPending) { var old = _pending[0]; _pending.RemoveAt(0); old.Close(); }
                _pending.Add(conn);
            });
            conn.Start();
            conn.Send(Msg.Welcome, new { room = Room, proctor = Proctor, exam = $"{Exam.ExamName} – {Exam.Subject}", department = Department, school = School });
        }
    }

    private const int MaxPending = 300;
    private Candidate? ByConn(Connection c) => Candidates.FirstOrDefault(x => x.Conn == c);

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];
    private CodeLayout? LayoutOf(Candidate c) => Layouts.FirstOrDefault(l => l.Code == c.Sheet.Code);

    /// <summary>Ghi nhận kết quả kiểm tra một câu trả lời: hợp lệ thì cập nhật tiến độ, sai định dạng thì ghi nhật ký (một lần mỗi máy).</summary>
    private void Accepted(Candidate cand, bool ok)
    {
        if (ok) { cand.NotifyProgress(); return; }
        if (cand.BadDataLogged) return;
        cand.BadDataLogged = true;
        Log("CẢNH BÁO", cand.Machine, $"gửi câu trả lời không hợp lệ (sai số câu, loại câu hoặc phương án) · đã bỏ qua · {cand.FullName}");
    }

    // ---------------- Thông điệp từ máy thí sinh ----------------
    /// <summary>Mọi tin từ mạng đi qua đây; dữ liệu sai định dạng từ một máy không được phép làm sập máy chủ.</summary>
    private void OnMessage(Connection conn, JsonObject m)
    {
        try { Handle(conn, m); }
        catch (Exception ex)
        {
            ITest.UI.CrashGuard.Write(ex);
            try { conn.Close(); } catch { }
        }
    }

    private void Handle(Connection conn, JsonObject m)
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
                _helloMachine[conn] = Clip(m.Str("machine"), 60);
                _helloToken[conn] = Clip(m.Str("token"), 64);
                break;
            case Msg.Register:
                // Token phiên đi kèm tin đăng ký (ưu tiên) hoặc tin chào.
                var regToken = Clip(m.Str("token"), 64);
                if (regToken.Length > 0) _helloToken[conn] = regToken;
                OnRegister(conn, m.As<CandidateInfo>() ?? new CandidateInfo());
                break;
            case Msg.Answer:
            {
                if (cand == null || cand.Status != CandStatus.Doing) return;
                var lay = LayoutOf(cand);
                if (lay == null) return;
                Accepted(cand, AnswerRules.SetChoice(Exam, lay, cand.Sheet, m.Int("no"), m.Int("choice", -1)));
                return;
            }
            case Msg.AnswerEx:
            {
                if (cand == null || cand.Status != CandStatus.Doing) return;
                var lay = LayoutOf(cand);
                var ax = m.As<AnswerEx>();
                if (lay == null || ax == null) return;
                // Loại câu do máy chủ tự xác định theo đề, không tin trường "type" máy thí sinh gửi.
                var q = AnswerRules.QuestionAt(Exam, lay, ax.No);
                bool ok = q?.Type switch
                {
                    QuestionType.MultiSelect => ax.Type == q.Type && AnswerRules.SetMulti(Exam, lay, cand.Sheet, ax.No, ax.Choices),
                    QuestionType.TrueFalse => ax.Type == q.Type && AnswerRules.SetTf(Exam, lay, cand.Sheet, ax.No, ax.Tf),
                    QuestionType.ShortAnswer => ax.Type == q.Type && AnswerRules.SetShort(Exam, lay, cand.Sheet, ax.No, ax.Text),
                    _ => false
                };
                Accepted(cand, ok);
                return;
            }
            case Msg.Cheat:
                if (cand == null) return;
                cand.CheatCount++;
                cand.RefreshAll();
                Log("GIAN LẬN", cand.Machine, $"{Clip(m.Str("detail"), 300)} · {cand.FullName}");
                break;
            case Msg.Essay:
            {
                if (cand == null || cand.Status != CandStatus.Doing) return;
                var lay = LayoutOf(cand);
                if (lay == null) return;
                Accepted(cand, AnswerRules.SetEssay(Exam, lay, cand.Sheet, m.Int("no"), m.Str("text")));
                return;
            }
            case Msg.Submit:
                OnSubmit(conn, cand, m);
                break;
            case Msg.Focus:
                if (cand != null && cand.IsActive)
                    Log("CẢNH BÁO", cand.Machine, $"{Clip(m.Str("reason"), 200)} · {cand.FullName}");
                break;
        }
        Changed?.Invoke();
    }

    private readonly Dictionary<Connection, string> _helloMachine = new();
    private readonly Dictionary<Connection, string> _helloToken = new();

    private void OnRegister(Connection conn, CandidateInfo info)
    {
        info.Sbd = Clip((info.Sbd ?? "").Trim(), 30);
        info.FullName = Clip((info.FullName ?? "").Trim(), 100);
        info.BirthDate = Clip((info.BirthDate ?? "").Trim(), 20);
        info.ClassName = Clip((info.ClassName ?? "").Trim(), 30);
        if (info.Sbd.Length == 0 || info.FullName.Length == 0)
        {
            conn.Send(Msg.Reject, new { reason = "Thiếu số báo danh hoặc họ tên." });
            return;
        }
        var machine = _helloMachine.TryGetValue(conn, out var mm) && mm.Length > 0 ? Clip(mm, 60) : conn.RemoteAddress;
        var clientToken = _helloToken.TryGetValue(conn, out var tk) ? tk : "";
        var mine = ByConn(conn);   // kết nối này đã đăng ký trước đó (đang sửa thông tin)
        var existing = Candidates.FirstOrDefault(c => string.Equals(c.Sbd, info.Sbd, StringComparison.OrdinalIgnoreCase)) ?? mine;
        if (existing != null && existing.Conn != conn)
        {
            // Chống chiếm phiên: số báo danh đã có bài làm thì chỉ máy giữ đúng token phiên mới được vào lại;
            // máy khác (hoặc máy đã mất token) phải được giám thị duyệt "Chuyển máy". Không dựa vào tên máy hay trạng thái mất kết nối.
            bool started = existing.Status is not (CandStatus.Waiting or CandStatus.Connected);
            bool tokenOk = existing.Token.Length > 0 && clientToken.Length > 0 && existing.Token == clientToken;
            bool approved = existing.MoveApproved || existing.Status == CandStatus.Moving;
            if (!SessionRules.MayAttach(started, tokenOk, approved, existing.Conn != null))
            {
                string reason = !started
                    ? $"Số báo danh {info.Sbd} đang được dùng ở máy {existing.Machine}. Kiểm tra lại số báo danh hoặc báo giám thị."
                    : existing.Status is CandStatus.Done or CandStatus.Revoked
                        ? $"Số báo danh {info.Sbd} đã kết thúc bài thi. Báo giám thị nếu có nhầm lẫn."
                        : $"Số báo danh {info.Sbd} đã có phiên làm bài (máy {existing.Machine}). Nếu bạn đổi máy hoặc máy vừa khởi động lại, hãy báo giám thị bấm “Chuyển máy” rồi đăng nhập lại.";
                conn.Send(Msg.Reject, new { reason });
                var key = info.Sbd + "|" + machine;
                if (!_rejectLogged.TryGetValue(key, out var at) || (DateTime.Now - at).TotalSeconds > 60)
                {
                    _rejectLogged[key] = DateTime.Now;
                    Log("CẢNH BÁO", machine, $"thử vào bằng SBD {info.Sbd} đang thuộc phiên ở máy {existing.Machine} · đã từ chối" +
                        (started && existing.IsActive ? " · nếu đúng là thí sinh đổi máy, bấm “Chuyển máy”" : ""));
                }
                return;
            }
            if (existing.Conn != null)
            {
                var old = existing.Conn;
                existing.Conn = null;
                old.Close();
                if (existing.Status == CandStatus.Doing) existing.Status = CandStatus.Lost;
            }
        }
        // Kết nối này trước đó đăng ký bằng số báo danh khác (sửa thông tin): bỏ dòng cũ còn ở phòng chờ.
        if (mine != null && mine != existing)
        {
            mine.Conn = null;
            if (mine.Status is CandStatus.Waiting or CandStatus.Connected)
            {
                Candidates.Remove(mine);
                for (int i = 0; i < Candidates.Count; i++) Candidates[i].Index = i;
            }
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
                // Chuyển máy xong: cấp token mới, token của máy cũ hết hiệu lực.
                if (wasMoving) cand.Token = SessionRules.NewToken();
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
        if (cand.MoveApproved) { cand.MoveApproved = false; cand.Token = SessionRules.NewToken(); }
        if (cand.Token.Length == 0) cand.Token = SessionRules.NewToken();
        conn.Send(Msg.Registered, new { token = cand.Token });
        if (Phase == Phase.Running && cand.Status == CandStatus.Doing)
        {
            // Máy đang thi kết nối lại: gửi lại đề và bài làm đã lưu.
            SendExam(cand, resume: true);
        }
        else if (Phase == Phase.Running && cand.Status == CandStatus.Waiting)
        {
            // Máy vào phòng sau khi đã phát đề: chờ giám thị phát đề riêng.
            conn.Send(Msg.Notice, new { kind = "all", text = "Bạn vào phòng sau khi đề đã được phát. Vui lòng chờ giám thị phát đề cho máy của bạn." });
            Log("CHỜ PHÁT ĐỀ", cand.Machine, $"vào phòng sau khi đã phát đề · chờ phát đề riêng · {cand.FullName}");
        }
        else if (cand.Status is CandStatus.Done or CandStatus.Revoked)
            SendResult(cand);
        cand.Left = Fmt(LeftOf(cand));
        cand.LastSeen = DateTime.Now;
    }

    /// <summary>
    /// Thí sinh nộp bài. Máy chủ tự kiểm tra quy định thời gian nộp (không tin cờ "auto" của máy thí sinh),
    /// ghi bản bài làm cuối cùng gửi kèm rồi mới chốt bài. Bài đã chốt không bị tin nộp lại ghi đè.
    /// </summary>
    private void OnSubmit(Connection conn, Candidate? cand, JsonObject m)
    {
        if (cand == null) return;
        if (cand.Status is CandStatus.Done or CandStatus.Revoked)
        {
            // Máy thí sinh gửi lại sau khi rớt mạng: bài đã chốt, chỉ xác nhận lại, không nhận thêm dữ liệu.
            SendResult(cand);
            return;
        }
        if (cand.Status != CandStatus.Doing) return;
        bool collecting = cand.CollectDeadline != null;
        int left = LeftOf(cand);
        if (!SessionRules.CanSubmit(left, SubmitWindowMinutes, collecting))
        {
            conn.Send(Msg.SubmitDenied, new
            {
                secondsLeft = left, paused = Paused,
                reason = $"Chưa đến thời gian được nộp bài (chỉ nộp trong {SubmitWindowMinutes} phút cuối). Bạn tiếp tục làm bài."
            });
            Log("CẢNH BÁO", cand.Machine, $"nộp bài khi còn {Fmt(left)} · chưa tới thời gian được nộp · đã từ chối · {cand.FullName}");
            return;
        }
        var layout = LayoutOf(cand);
        if (layout != null)
        {
            int bad = AnswerRules.ApplyFinal(Exam, layout, cand.Sheet, m.As<FinalAnswers>("final"));
            if (bad > 0) Accepted(cand, false);
        }
        FinishCandidate(cand, collecting ? cand.CollectText : m.Bool("auto") ? "tự động nộp bài khi hết giờ" : "đã nộp bài");
    }

    private void OnClosed(Connection conn)
    {
        _pending.Remove(conn);
        _helloMachine.Remove(conn);
        _helloToken.Remove(conn);
        var cand = ByConn(conn);
        if (cand == null) return;
        cand.Conn = null;
        if (cand.Status == CandStatus.Doing && cand.CollectDeadline != null)
        {
            // Đang thu bài mà máy mất kết nối: chốt bài theo bản máy chủ đang giữ.
            FinishCandidate(cand, cand.CollectText + " (máy mất kết nối, dùng bản đã lưu trên máy chủ)");
        }
        else if (cand.Status == CandStatus.Doing)
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

    /// <summary>
    /// Gửi đề (và bài làm đã lưu nếu <paramref name="resume"/>) cho một máy. Việc dựng và mã hoá đề (có thể nặng vì ảnh, âm thanh)
    /// chạy ở luồng nền để bảng điều khiển không bị đứng khi phát đề cho cả phòng; hai tin luôn đi đúng thứ tự Đề → Bài làm.
    /// </summary>
    private void SendExam(Candidate c, bool resume = false)
    {
        var conn = c.Conn;
        var layout = Layouts.FirstOrDefault(l => l.Code == c.Sheet.Code);
        if (conn == null || layout == null) return;
        int left = LeftOf(c), window = SubmitWindowMinutes;
        bool paused = Paused;   // máy nối lại lúc phòng đang tạm dừng phải đứng đồng hồ ngay, không chờ tin giờ kế tiếp
        object? saved = !resume ? null : new
        {
            choices = new Dictionary<int, int>(c.Sheet.Choices),
            essays = new Dictionary<int, string>(c.Sheet.Essays),
            multi = c.Sheet.Multi.ToDictionary(kv => kv.Key, kv => kv.Value.ToList()),
            tf = c.Sheet.Tf.ToDictionary(kv => kv.Key, kv => new Dictionary<int, bool>(kv.Value)),
            shortAns = new Dictionary<int, string>(c.Sheet.Short)
        };
        Task.Run(() =>
        {
            try
            {
                conn.Send(Msg.Exam, new { exam = ClientExamFor(layout), secondsLeft = left, submitWindow = window, paused });
                if (saved != null) conn.Send(Msg.Resume, saved);
            }
            catch (Exception ex) { ITest.UI.CrashGuard.Write(ex); }
        });
    }

    /// <summary>Có máy nào đang chờ phát đề riêng không (vào phòng sau khi đã phát đề).</summary>
    public bool CanHandOut(Candidate c) => Phase == Phase.Running && c.Conn != null && c.Status == CandStatus.Waiting;

    /// <summary>Phát đề riêng cho một máy vào muộn.</summary>
    public void HandOutTo(Candidate c)
    {
        if (!CanHandOut(c)) return;
        AssignCode(c);
        c.Status = CandStatus.Doing;
        SendExam(c, resume: true);
        Log("PHÁT ĐỀ", c.Machine, $"phát đề riêng · mã {c.Sheet.Code} · {c.FullName}");
        SaveState();
        Changed?.Invoke();
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
        SaveState();
        Log("THÔNG TIN", "", $"Đã phát đề cho {Candidates.Count(c => c.Status == CandStatus.Doing)} máy · bắt đầu tính giờ {Exam.Minutes} phút");
        Changed?.Invoke();
    }

    public int LeftOf(Candidate c) => Phase == Phase.Open ? Exam.Minutes * 60 : Math.Max(0, SecondsLeft + c.ExtraSeconds);

    // Đồng hồ phòng thi chạy theo giờ thực của máy (Stopwatch), không theo số lần timer giao diện kêu:
    // khi giao diện bận, timer kêu trễ nhưng giờ thi vẫn đúng.
    private readonly System.Diagnostics.Stopwatch _wall = System.Diagnostics.Stopwatch.StartNew();
    private double _lastWall, _carry;

    private void OnTick()
    {
        double now = _wall.Elapsed.TotalSeconds;
        _carry += now - _lastWall;
        _lastWall = now;
        int steps = (int)_carry;
        _carry -= steps;
        if (steps > 5) steps = 5;   // máy chủ ngủ/treo lâu: không trừ dồn giờ của thí sinh
        for (int i = 0; i < steps; i++) Second();
        if (steps > 0) Changed?.Invoke();
    }

    /// <summary>Công việc của mỗi giây thi.</summary>
    private void Second()
    {
        TickCount++;
        if (Phase == Phase.Running && !Paused)
        {
            // Đồng hồ phòng chạy về 0 thì dừng; từ đó giờ cộng riêng của từng thí sinh mới bị trừ dần
            // (trước đây giờ cộng riêng đứng yên mãi sau khi đồng hồ phòng về 0).
            bool roomRunning = SecondsLeft > 0;
            if (roomRunning) SecondsLeft--;
            foreach (var c in Candidates.ToList())
            {
                if (c.Status is CandStatus.Lost or CandStatus.Moving)
                {
                    if (roomRunning) c.ExtraSeconds++;   // bù đúng phần giờ phòng vừa trôi; phòng đã về 0 thì giờ của máy này đứng yên
                }
                else if (c.Status == CandStatus.Doing && !roomRunning && c.ExtraSeconds > 0 && c.CollectDeadline == null)
                    c.ExtraSeconds--;
                if (c.Status == CandStatus.Doing && c.CollectDeadline == null && LeftOf(c) <= 0)
                    BeginCollect(c, "hết giờ · đã tự động thu bài");
            }
        }
        // Thu bài: chờ bản cuối từ máy thí sinh tối đa 10 giây, quá hạn thì chốt theo bản máy chủ đang giữ.
        foreach (var c in Candidates.Where(c => c.CollectDeadline != null && (c.Conn == null || DateTime.UtcNow >= c.CollectDeadline)).ToList())
            FinishCandidate(c, c.CollectText + " (không nhận được bản cuối, dùng bản đã lưu trên máy chủ)");
        if (Phase == Phase.Running && Candidates.Count > 0 && Candidates.All(c => !c.IsActive))
        {
            if (_collectAllPending) EndExam("Giám thị đã thu bài tất cả thí sinh");
            else if (SecondsLeft <= 0 && !Paused) EndExam("Hết giờ · đã thu bài tất cả thí sinh");
        }
        // Máy không gửi nhịp tim quá 10 giây (mất điện, rút dây mạng…) được coi là mất kết nối.
        foreach (var c in Candidates.Where(c => c.Conn != null && (DateTime.Now - c.LastSeen).TotalSeconds > 10).ToList())
            c.Conn!.Close();
        foreach (var c in Candidates)
        {
            c.Left = Phase == Phase.Collected ? "—" : Fmt(LeftOf(c)) + (c.ExtraSeconds > 0 && Phase == Phase.Running ? "*" : "");
            if (Phase == Phase.Running && c.Status == CandStatus.Doing && c.CollectDeadline == null && TickCount % 5 == 0)
                c.Conn?.Send(Msg.Time, new { secondsLeft = LeftOf(c), paused = Paused });
        }
        // Sao lưu tự động chỉ chạy sau khi đã phát đề (lúc chưa phát đề không có bài làm để lưu).
        if (Phase != Phase.Open)
        {
            if (TickCount % 30 == 0) Backup();
            else if (TickCount % 5 == 0) SaveState();
        }
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

    public bool CanCollectAll => Phase == Phase.Running && !_collectAllPending && SecondsLeft <= SubmitWindowMinutes * 60;
    /// <summary>Thời gian tối đa máy chủ chờ bản bài làm cuối cùng của một máy khi thu bài.</summary>
    private const int CollectWaitSeconds = 10;

    /// <summary>
    /// Thu bài một thí sinh: báo máy thí sinh gửi bản cuối cùng (gồm phần tự luận vừa gõ) rồi chờ tối đa 10 giây.
    /// Máy đang mất kết nối thì chốt ngay theo bản máy chủ đang giữ.
    /// </summary>
    private void BeginCollect(Candidate c, string text)
    {
        if (c.Status is CandStatus.Done or CandStatus.Revoked || c.CollectDeadline != null) return;
        if (c.Conn == null || c.Status != CandStatus.Doing) { FinishCandidate(c, text); return; }
        c.CollectText = text;
        c.CollectDeadline = DateTime.UtcNow.AddSeconds(CollectWaitSeconds);
        c.Conn.Send(Msg.Collect);
        c.NotifyProgress();
    }

    public void CollectAll()
    {
        if (!CanCollectAll) return;
        _collectAllPending = true;
        Log("NỘP BÀI", "", $"Giám thị thu bài tất cả · chờ các máy gửi bản cuối (tối đa {CollectWaitSeconds} giây)");
        foreach (var c in Candidates.Where(c => c.IsActive).ToList()) BeginCollect(c, "đã được thu bài");
        if (Candidates.All(c => !c.IsActive)) EndExam("Giám thị đã thu bài tất cả thí sinh");
        Changed?.Invoke();
    }

    private void EndExam(string text)
    {
        if (Phase == Phase.Collected) return;
        Phase = Phase.Collected;
        _collectAllPending = false;
        EndedAt = DateTime.Now;
        Backup();
        Log("NỘP BÀI", "", text);
        Changed?.Invoke();
    }

    private void FinishCandidate(Candidate c, string text)
    {
        if (c.Status is CandStatus.Done or CandStatus.Revoked) return;
        c.CollectDeadline = null;
        c.Sheet.SubmittedAt = DateTime.Now;
        c.Status = CandStatus.Done;
        Log("NỘP BÀI", c.Machine, $"{text} · {c.FullName} ({c.Answered}/{c.TotalQuestions})");
        // Chỉ xác nhận với máy thí sinh SAU KHI bài đã nằm trên đĩa máy chủ.
        var saved = SaveState();
        saved.ContinueWith(t => _ui.BeginInvoke(() =>
        {
            if (!(t.Status == TaskStatus.RanToCompletion && t.Result) && !_saveFailLogged)
            {
                _saveFailLogged = true;
                Log("CẢNH BÁO", "", "KHÔNG GHI ĐƯỢC BẢN SAO LƯU xuống đĩa (đĩa đầy hoặc bị khoá?). Bài làm vẫn nằm trong bộ nhớ máy chủ — hãy kiểm tra ổ đĩa và xuất kết quả sớm.");
            }
            SendResult(c);
        }));
    }
    private bool _saveFailLogged;

    /// <summary>
    /// Xác nhận với máy thí sinh rằng bài đã được máy chủ lưu. Điểm thật chỉ được gửi khi giám thị cho xem điểm;
    /// nếu không, máy thí sinh không nhận được con số nào (kể cả số câu đúng/sai).
    /// </summary>
    private void SendResult(Candidate c)
    {
        if (c.Conn == null) return;
        var layout = LayoutOf(c);
        if (!ShowScore || c.Sheet.Revoked || c.Status != CandStatus.Done || layout == null)
        {
            c.Conn.Send(Msg.Result, new { showScore = false, revoked = c.Sheet.Revoked });
            return;
        }
        var r = Scoring.GradeMc(Exam, layout, c.Sheet);
        c.Conn.Send(Msg.Result, new { showScore = true, right = r.Right, wrong = r.Wrong, blank = r.Blank, score = r.Score });
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
        c.CollectDeadline = null;
        c.Status = CandStatus.Revoked;
        SendResult(c);   // máy thí sinh khoá bài và chuyển sang màn hình chờ
        SaveState();
        Log("ĐÌNH CHỈ", c.Machine, $"bị thu bài · kết quả thi bị huỷ · {c.FullName}");
    }

    public void ApproveMove(Candidate c)
    {
        if (!c.IsActive) return;
        c.MoveApproved = true;
        c.CollectDeadline = null;
        // Token của máy cũ hết hiệu lực ngay khi duyệt chuyển máy; máy mới sẽ được cấp token mới lúc đăng nhập.
        c.Token = SessionRules.NewToken();
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
        ["KHÔI PHỤC"] = (Color.FromRgb(0xFE, 0xF3, 0xC7), Color.FromRgb(0x92, 0x40, 0x0E)),
        ["GIAN LẬN"] = (Color.FromRgb(0xFE, 0xE2, 0xE2), Color.FromRgb(0x99, 0x1B, 0x1B)),
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

    // Sao lưu ghi ở luồng nền qua OrderedWriter: các bản lưu xếp hàng đúng thứ tự (bản cũ không bao giờ đè bản mới),
    // mỗi bản ghi ra file tạm rồi mới thay file chính (không để lại file ghi dở khi mất điện).
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public void Backup()
    {
        if (BackupDir.Length == 0) return;
        try
        {
            var json = JsonSerializer.Serialize(Candidates.Select(c => c.Sheet).ToList(), Indented);
            _writer.Write(Path.Combine(BackupDir, "bai-lam.json"), json);
        }
        catch { }
        SaveState();
    }

    // ---------------- Khôi phục sau sự cố ----------------
    private const string StateFile = "trang-thai.json";
    public static string BackupRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "iTest-Server", "SaoLuu");

    /// <summary>
    /// Ghi trạng thái phòng thi. Ảnh chụp dữ liệu được dựng ngay trên luồng giao diện; việc ghi đĩa mặc định chạy nền, theo thứ tự.
    /// Đặt <paramref name="sync"/>=true khi cần ghi xong ngay (lúc đóng phòng). Kết quả true khi bản này đã nằm trên đĩa.
    /// </summary>
    public Task<bool> SaveState(bool closed = false, bool sync = false)
    {
        string json; var dir = BackupDir;
        try
        {
            if (BackupDir.Length == 0) return Task.FromResult(true);
            var st = new RoomState
            {
                Department = Department, School = School, Room = Room, Proctor = Proctor, RoomPin = RoomPin, PackagePath = PackagePath, BackupDir = BackupDir,
                ExamHash = ExamHash,
                Phase = Phase, SecondsLeft = SecondsLeft, SubmitWindowMinutes = SubmitWindowMinutes, ShowScore = ShowScore,
                StartedAt = StartedAt, EndedAt = EndedAt, Exported = Exported, Closed = closed, SavedAt = DateTime.Now,
                Candidates = Candidates.Select(c => new CandidateState
                {
                    Sheet = c.Sheet, Status = c.Status, Index = c.Index, ExtraSeconds = c.ExtraSeconds, WarnedAt = c.WarnedAt, RevokedAt = c.RevokedAt,
                    MoveApproved = c.MoveApproved, TotalQuestions = c.TotalQuestions, CheatCount = c.CheatCount, Token = c.Token, Machine = c.Machine, Ip = c.Ip
                }).ToList()
            };
            json = JsonSerializer.Serialize(st, ExamPackage.Json);
        }
        catch { return Task.FromResult(false); }
        var path = Path.Combine(dir, StateFile);
        return sync ? Task.FromResult(_writer.WriteNow(path, json)) : _writer.Write(path, json);
    }

    /// <summary>
    /// Bản dữ liệu riêng để xuất kết quả: sao chép toàn bộ bài làm và trạng thái tại thời điểm gọi (trên luồng giao diện),
    /// nên việc xuất ở luồng nền không đọc dữ liệu phòng thi đang thay đổi.
    /// </summary>
    public ExportData Snapshot() => new()
    {
        Exam = Exam, Layouts = Layouts.ToList(), Department = Department, School = School, Room = Room, Proctor = Proctor,
        PackagePath = PackagePath, BackupDir = BackupDir, StartedAt = StartedAt, EndedAt = EndedAt,
        Candidates = Candidates.Select(c => new ExportCand
        {
            Sheet = JsonSerializer.Deserialize<AnswerSheet>(JsonSerializer.Serialize(c.Sheet, ExamPackage.Json), ExamPackage.Json) ?? new AnswerSheet(),
            Status = c.Status, StatusText = c.StatusText, Discipline = c.Discipline, WarnedAt = c.WarnedAt, RevokedAt = c.RevokedAt, Machine = c.Machine
        }).ToList()
    };

    /// <summary>Tìm phòng thi bị dừng đột ngột (chưa đóng phòng) trong 24 giờ qua, mới nhất trước.</summary>
    public static RoomState? FindUnclosed()
    {
        try
        {
            if (!Directory.Exists(BackupRoot)) return null;
            RoomState? best = null;
            foreach (var dir in Directory.GetDirectories(BackupRoot))
            {
                var f = Path.Combine(dir, StateFile);
                if (!File.Exists(f) || DateTime.Now - File.GetLastWriteTime(f) > TimeSpan.FromHours(24)) continue;
                try
                {
                    var st = JsonSerializer.Deserialize<RoomState>(File.ReadAllText(f), ExamPackage.Json);
                    if (st == null || st.Closed || st.Candidates.Count == 0 && st.Phase == Phase.Open) continue;
                    if (best == null || st.SavedAt > best.SavedAt) best = st;
                }
                catch { }
            }
            return best;
        }
        catch { return null; }
    }

    /// <summary>Bỏ qua phòng thi chưa đóng (đánh dấu đã đóng).</summary>
    public static void Dismiss(RoomState st)
    {
        try
        {
            st.Closed = true;
            SafeFile.WriteAllText(Path.Combine(st.BackupDir, StateFile), JsonSerializer.Serialize(st, ExamPackage.Json));
        }
        catch { }
    }

    /// <summary>
    /// Mở lại phòng thi từ trạng thái đã lưu. Thí sinh đang làm bài chuyển sang "mất kết nối" và máy thí sinh tự kết nối lại;
    /// giờ thi được tạm dừng để giám thị bấm "Tiếp tục" khi các máy đã vào lại đủ.
    /// </summary>
    public void Restore(RoomState st)
    {
        // Bộ đề dùng để khôi phục phải đúng là bộ đề của phòng thi đó: mã đề và số câu của bài làm đã lưu phải khớp.
        var codes = Layouts.Select(l => l.Code).ToHashSet();
        if (st.Candidates.Any(c => c.Sheet.Code.Length > 0 && (!codes.Contains(c.Sheet.Code) || c.TotalQuestions != Exam.Questions.Count)))
            throw new InvalidDataException("Bộ đề vừa chọn không khớp với phòng thi cần khôi phục (khác số câu hoặc số mã đề). Hãy chọn đúng file bộ đề đã dùng.");
        // Cùng số câu, cùng số mã đề nhưng nội dung khác (đề khác môn, bản đề đã sửa…): dấu vân tay SHA-256 sẽ khác.
        if (!string.IsNullOrEmpty(st.ExamHash) && !string.Equals(st.ExamHash, ExamHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Bộ đề vừa chọn có nội dung khác với bộ đề đã dùng trong phòng thi cần khôi phục (dấu vân tay không khớp). Hãy chọn đúng file bộ đề đã dùng.");
        Department = st.Department; School = st.School; Room = st.Room; Proctor = st.Proctor; RoomPin = st.RoomPin;
        SubmitWindowMinutes = st.SubmitWindowMinutes; ShowScore = st.ShowScore;
        _clientExams.Clear();
        foreach (var cs in st.Candidates.OrderBy(c => c.Index))
        {
            // Máy còn ở phòng chờ chưa có bài làm: không cần khôi phục, máy sẽ tự vào lại.
            if (cs.Status is CandStatus.Waiting or CandStatus.Connected) continue;
            var c = new Candidate
            {
                Index = Candidates.Count, ExtraSeconds = cs.ExtraSeconds, WarnedAt = cs.WarnedAt, RevokedAt = cs.RevokedAt,
                MoveApproved = cs.MoveApproved, TotalQuestions = cs.TotalQuestions, CheatCount = cs.CheatCount, Token = cs.Token, Ip = cs.Ip
            };
            c.Sheet.Candidate = cs.Sheet.Candidate; c.Sheet.Code = cs.Sheet.Code;
            c.Sheet.Choices = cs.Sheet.Choices; c.Sheet.Essays = cs.Sheet.Essays; c.Sheet.Multi = cs.Sheet.Multi;
            c.Sheet.Tf = cs.Sheet.Tf; c.Sheet.Short = cs.Sheet.Short; c.Sheet.SubmittedAt = cs.Sheet.SubmittedAt;
            c.Sheet.Revoked = cs.Sheet.Revoked; c.Sheet.Warnings = cs.Sheet.Warnings;
            c.Machine = cs.Machine;
            c.Index = cs.Index;
            c.Status = cs.Status is CandStatus.Doing or CandStatus.Lost ? CandStatus.Lost : cs.Status;
            Candidates.Add(c);
        }
        Open(st.BackupDir);
        Phase = st.Phase;
        SecondsLeft = st.Phase == Phase.Open ? Exam.Minutes * 60 : st.SecondsLeft;
        StartedAt = st.StartedAt; EndedAt = st.EndedAt; Exported = st.Exported;
        if (Phase == Phase.Running) Paused = true;
        Log("KHÔI PHỤC", "", $"Đã khôi phục phòng thi sau sự cố từ bản lưu lúc {st.SavedAt:HH:mm:ss} · {Candidates.Count} thí sinh" +
            (Phase == Phase.Running ? " · giờ thi đang TẠM DỪNG, bấm “Tiếp tục” khi các máy đã kết nối lại" : ""));
        SaveState();
        Changed?.Invoke();
    }

    public static string Fmt(int secs) => $"{secs / 60:00}:{secs % 60:00}";
    private static string Safe(string s) => string.Concat(s.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) || ch == ' ' ? '_' : ch));
}
