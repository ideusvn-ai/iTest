using System.Diagnostics;
using System.Text.Json.Nodes;
using ITest.Core;

// iTest-LoadTest: giả lập nhiều máy thí sinh kết nối tới iTest-Server để thử tải trước kỳ thi.
//   iTest-LoadTest [--host 192.168.1.10] [--pin 1234] [--clients 60] [--drop 5] [--think 3]
//   --host     IP máy chủ (bỏ trống: tự tìm phòng thi trong mạng LAN qua UDP)
//   --pin      Mã phòng thi 4 số hiển thị trên iTest-Server (bắt buộc để vào phòng)
//   --clients  số máy giả lập (mặc định 40)
//   --drop     % khả năng mỗi máy mất mạng trong mỗi phút làm bài (mặc định 5)
//   --think    số giây trung bình giữa hai lần trả lời (mặc định 4)
// Trên iTest-Server: mở phòng như bình thường, đợi các máy LT001… vào phòng rồi bấm "Phát đề".
Console.OutputEncoding = System.Text.Encoding.UTF8;
var opt = Args.Parse(args);
Console.WriteLine("iTest-LoadTest · giả lập máy thí sinh");
string host = opt.Host ?? "";
if (host.Length == 0)
{
    Console.Write("Đang tìm phòng thi trong mạng LAN… ");
    var found = new TaskCompletionSource<RoomBeacon>();
    using var listener = new BeaconListener();
    listener.Found += b => found.TrySetResult(b);
    listener.Start();
    var b = await found.Task.WaitAsync(TimeSpan.FromSeconds(15)).ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : null);
    if (b == null) { Console.WriteLine("không thấy. Dùng --host <IP máy chủ>."); return 1; }
    host = b.Address;
    Console.WriteLine($"{b.Room} · {b.Exam} · {host}");
}

var stats = new Stats();
var sw = Stopwatch.StartNew();
var sims = Enumerable.Range(1, opt.Clients).Select(i => new SimClient(i, host, opt, stats)).ToList();
foreach (var s in sims) { _ = s.RunAsync(); await Task.Delay(40); }   // vào phòng lần lượt như phòng máy thật

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
Console.WriteLine("Ctrl+C để dừng.\n");
while (!cts.IsCancellationRequested && !sims.All(s => s.Finished))
{
    try { await Task.Delay(5000, cts.Token); } catch { break; }
    Console.WriteLine($"[{sw.Elapsed:mm\\:ss}] kết nối {stats.Connected,3}/{opt.Clients} · đăng ký {stats.Registered,3} · nhận đề {stats.GotExam,3} · " +
                      $"đã gửi {stats.Answers,6} câu trả lời · mất mạng {stats.Drops,3} · nối lại {stats.Reconnects,3} · " +
                      $"máy chủ xác nhận nộp {stats.Results,3} · ping TB {stats.AvgPing:0.0} ms (max {stats.MaxPing:0} ms) · lỗi {stats.Errors}");
}
Console.WriteLine("\nKết thúc. Kiểm tra trên iTest-Server: tất cả máy LT… phải ở trạng thái đúng, bài làm khớp, không mất câu trả lời.");
return 0;

sealed record Args(string? Host, int Clients, double DropPercent, double Think, string Pin)
{
    public static Args Parse(string[] a)
    {
        string? Get(string k) { int i = Array.IndexOf(a, k); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }
        return new Args(Get("--host"), int.TryParse(Get("--clients"), out var n) ? n : 40,
            double.TryParse(Get("--drop"), out var d) ? d : 5, double.TryParse(Get("--think"), out var t) ? Math.Max(0.2, t) : 4,
            Get("--pin") ?? "");
    }
}

sealed class Stats
{
    public int Connected, Registered, GotExam, Answers, Drops, Reconnects, Results, Errors;
    private double _pingSum; private int _pingN; public double MaxPing;
    public double AvgPing { get { lock (this) return _pingN == 0 ? 0 : _pingSum / _pingN; } }
    public void Ping(double ms) { lock (this) { _pingSum += ms; _pingN++; if (ms > MaxPing) MaxPing = ms; } }
}

sealed class SimClient
{
    private readonly int _i; private readonly string _host; private readonly Args _o; private readonly Stats _s;
    private readonly Random _rnd;
    private Connection? _c;
    private ClientExam? _exam;
    // Bài làm của máy giả lập: giữ đủ mọi loại câu để gửi lại khi nối lại và gửi kèm lúc nộp (như máy thí sinh thật).
    private readonly object _sheet = new();
    private readonly Dictionary<int, int> _choices = new();
    private readonly Dictionary<int, List<int>> _multi = new();
    private readonly Dictionary<int, Dictionary<int, bool>> _tf = new();
    private readonly Dictionary<int, string> _short = new();
    private readonly Dictionary<int, string> _essays = new();
    private volatile string _token = "";
    private volatile bool _released, _submitted, _resultSeen;
    private long _pingAt;
    /// <summary>Xong khi máy chủ cho thoát, hoặc đã nộp VÀ máy chủ đã xác nhận lưu bài.</summary>
    public bool Finished => _released || _resultSeen;
    private string Machine => $"LT-{_i:000}";
    private CandidateInfo Info => new() { Sbd = $"LT{_i:000}", FullName = $"Thí sinh thử {_i:000}", BirthDate = "01/09/2009", ClassName = $"12A{1 + _i % 4}" };

    public SimClient(int i, string host, Args o, Stats s) { _i = i; _host = host; _o = o; _s = s; _rnd = new Random(i * 7919); }

    private FinalAnswers Final()
    {
        lock (_sheet)
            return new FinalAnswers
            {
                Choices = new(_choices), Essays = new(_essays), ShortAns = new(_short),
                Multi = _multi.ToDictionary(kv => kv.Key, kv => kv.Value.ToList()),
                Tf = _tf.ToDictionary(kv => kv.Key, kv => new Dictionary<int, bool>(kv.Value))
            };
    }

    private void SendSubmit(Connection c) => c.Send(Msg.Submit, new { auto = true, final = Final() });

    /// <summary>Gửi lại toàn bộ bài làm đang giữ (sau khi nối lại).</summary>
    private void ResendAll(Connection c)
    {
        var f = Final();
        foreach (var (k, v) in f.Choices) c.Send(Msg.Answer, new { no = k, choice = v });
        foreach (var (k, v) in f.Multi) c.Send(Msg.AnswerEx, new AnswerEx { No = k, Type = QuestionType.MultiSelect, Choices = v });
        foreach (var (k, v) in f.Tf) c.Send(Msg.AnswerEx, new AnswerEx { No = k, Type = QuestionType.TrueFalse, Tf = v });
        foreach (var (k, v) in f.ShortAns) c.Send(Msg.AnswerEx, new AnswerEx { No = k, Type = QuestionType.ShortAnswer, Text = v });
        foreach (var (k, v) in f.Essays) c.Send(Msg.Essay, new { no = k, text = v });
    }

    public async Task RunAsync()
    {
        bool first = true;
        // Chạy tới khi máy chủ xác nhận đã lưu bài (không dừng ngay lúc vừa gửi tin nộp).
        while (!Finished)
        {
            try
            {
                var c = _c = await Connection.ConnectAsync(_host, Ports.Session, _o.Pin);
                var closed = new TaskCompletionSource();
                c.Received += OnMessage;
                c.Closed += _ => closed.TrySetResult();
                c.Start();
                Interlocked.Increment(ref _s.Connected);
                if (!first) Interlocked.Increment(ref _s.Reconnects);
                first = false;
                // Giữ token phiên do máy chủ cấp: nối lại phải trình đúng token mới được nhận lại phiên.
                var info = Info;
                c.Send(Msg.Hello, new { machine = Machine, workgroup = NetUtil.GetWorkgroup(), ip = "loadtest", token = _token });
                c.Send(Msg.Register, new { info.Sbd, info.FullName, info.BirthDate, info.ClassName, token = _token });
                if (_submitted) SendSubmit(c); else ResendAll(c);
                await Task.WhenAny(closed.Task, Loop(c));
                Interlocked.Decrement(ref _s.Connected);
                c.Close();
            }
            catch { Interlocked.Increment(ref _s.Errors); }
            if (!Finished) await Task.Delay(2000 + _rnd.Next(3000));
        }
    }

    private async Task Loop(Connection c)
    {
        while (!Finished)
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(6, _o.Think * (0.5 + _rnd.NextDouble()))));
            _pingAt = Stopwatch.GetTimestamp();
            c.Send(Msg.Ping);
            var ex = _exam;
            if (ex == null || _submitted || ex.Questions.Count == 0) continue;
            var q = ex.Questions[_rnd.Next(ex.Questions.Count)];
            int n = Math.Max(1, q.Options.Count);
            switch (q.Type)
            {
                case QuestionType.MultipleChoice:
                case QuestionType.YesNo:
                    var ch = _rnd.Next(n);
                    lock (_sheet) _choices[q.No] = ch;
                    c.Send(Msg.Answer, new { no = q.No, choice = ch });
                    break;
                case QuestionType.MultiSelect:
                    var picks = Enumerable.Range(0, n).Where(_ => _rnd.Next(2) == 0).ToList();
                    lock (_sheet) _multi[q.No] = picks;
                    c.Send(Msg.AnswerEx, new AnswerEx { No = q.No, Type = q.Type, Choices = picks });
                    break;
                case QuestionType.TrueFalse:
                    var tf = Enumerable.Range(0, n).ToDictionary(k => k, _ => _rnd.Next(2) == 0);
                    lock (_sheet) _tf[q.No] = tf;
                    c.Send(Msg.AnswerEx, new AnswerEx { No = q.No, Type = q.Type, Tf = tf });
                    break;
                case QuestionType.ShortAnswer:
                    var sa = _rnd.Next(100).ToString();
                    lock (_sheet) _short[q.No] = sa;
                    c.Send(Msg.AnswerEx, new AnswerEx { No = q.No, Type = q.Type, Text = sa });
                    break;
                default:
                    var essay = $"Bài làm thử của {Machine} lúc {DateTime.Now:HH:mm:ss}. " + new string('x', _rnd.Next(50, 800));
                    lock (_sheet) _essays[q.No] = essay;
                    c.Send(Msg.Essay, new { no = q.No, text = essay });
                    break;
            }
            Interlocked.Increment(ref _s.Answers);
            // Mất mạng ngẫu nhiên
            double perStep = _o.DropPercent / 100.0 * (_o.Think / 60.0);
            if (_rnd.NextDouble() < perStep) { Interlocked.Increment(ref _s.Drops); c.Close(); return; }
        }
    }

    private void OnMessage(Connection c, JsonObject m)
    {
        switch (m.Str("t"))
        {
            case Msg.Pong:
                _s.Ping(Stopwatch.GetElapsedTime(_pingAt).TotalMilliseconds);
                break;
            case Msg.Registered:
                _token = m.Str("token");
                Interlocked.Increment(ref _s.Registered);
                break;
            case Msg.Reject: Console.WriteLine($"{Machine}: bị từ chối · {m.Str("reason")}"); break;
            case Msg.Exam:
                if (_exam == null) Interlocked.Increment(ref _s.GotExam);
                _exam = m.As<ClientExam>("exam");
                if (_submitted) SendSubmit(c);
                break;
            case Msg.Collect:
                // Máy chủ thu bài: gửi bản cuối cùng rồi chờ máy chủ xác nhận (tin Result).
                _submitted = true;
                SendSubmit(c);
                break;
            case Msg.SubmitDenied:
                _submitted = false;
                break;
            case Msg.Result:
                if (!_resultSeen) Interlocked.Increment(ref _s.Results);
                _submitted = true;
                _resultSeen = true;
                break;
            case Msg.Release: _released = true; c.Close(); break;
        }
    }
}
