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
                      $"nộp {stats.Results,3} · ping TB {stats.AvgPing:0.0} ms (max {stats.MaxPing:0} ms) · lỗi {stats.Errors}");
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
    private readonly Dictionary<int, int> _choices = new();
    private volatile bool _collecting, _released, _submitted;
    private long _pingAt;
    public bool Finished => _released || _submitted && _resultSeen;
    private volatile bool _resultSeen;
    private string Machine => $"LT-{_i:000}";
    private CandidateInfo Info => new() { Sbd = $"LT{_i:000}", FullName = $"Thí sinh thử {_i:000}", BirthDate = "01/09/2009", ClassName = $"12A{1 + _i % 4}" };

    public SimClient(int i, string host, Args o, Stats s) { _i = i; _host = host; _o = o; _s = s; _rnd = new Random(i * 7919); }

    public async Task RunAsync()
    {
        bool first = true;
        while (!_released && !_submitted)
        {
            try
            {
                _c = await Connection.ConnectAsync(_host, Ports.Session, _o.Pin);
                var closed = new TaskCompletionSource();
                _c.Received += OnMessage;
                _c.Closed += _ => closed.TrySetResult();
                _c.Start();
                Interlocked.Increment(ref _s.Connected);
                if (!first) Interlocked.Increment(ref _s.Reconnects);
                first = false;
                _c.Send(Msg.Hello, new { machine = Machine, workgroup = NetUtil.GetWorkgroup(), ip = "loadtest", token = "" });
                _c.Send(Msg.Register, Info);
                foreach (var (k, v) in _choices) _c.Send(Msg.Answer, new { no = k, choice = v });
                await Task.WhenAny(closed.Task, Loop(_c));
                Interlocked.Decrement(ref _s.Connected);
                _c.Close();
            }
            catch { Interlocked.Increment(ref _s.Errors); }
            if (!_released && !_submitted) await Task.Delay(2000 + _rnd.Next(3000));
        }
    }

    private async Task Loop(Connection c)
    {
        var last = DateTime.Now;
        while (!_released && !_submitted)
        {
            await Task.Delay(TimeSpan.FromSeconds(_o.Think * (0.5 + _rnd.NextDouble())));
            _pingAt = Stopwatch.GetTimestamp();
            c.Send(Msg.Ping);
            var ex = _exam;
            if (ex != null && !_collecting)
            {
                var q = ex.Questions[_rnd.Next(ex.Questions.Count)];
                switch (q.Type)
                {
                    case QuestionType.MultipleChoice:
                        var ch = _rnd.Next(Math.Max(1, q.Options.Count));
                        lock (_choices) _choices[q.No] = ch;
                        c.Send(Msg.Answer, new { no = q.No, choice = ch });
                        break;
                    case QuestionType.MultiSelect:
                        c.Send(Msg.AnswerEx, new AnswerEx { No = q.No, Type = q.Type, Choices = Enumerable.Range(0, 4).Where(_ => _rnd.Next(2) == 0).ToList() });
                        break;
                    case QuestionType.TrueFalse:
                        c.Send(Msg.AnswerEx, new AnswerEx { No = q.No, Type = q.Type, Tf = Enumerable.Range(0, 4).ToDictionary(k => k, _ => _rnd.Next(2) == 0) });
                        break;
                    case QuestionType.ShortAnswer:
                        c.Send(Msg.AnswerEx, new AnswerEx { No = q.No, Type = q.Type, Text = _rnd.Next(100).ToString() });
                        break;
                    default:
                        c.Send(Msg.Essay, new { no = q.No, text = $"Bài làm thử của {Machine} lúc {DateTime.Now:HH:mm:ss}. " + new string('x', _rnd.Next(50, 800)) });
                        break;
                }
                Interlocked.Increment(ref _s.Answers);
                // Mất mạng ngẫu nhiên
                double perStep = _o.DropPercent / 100.0 * (_o.Think / 60.0);
                if (_rnd.NextDouble() < perStep) { Interlocked.Increment(ref _s.Drops); c.Close(); return; }
            }
        }
    }

    private void OnMessage(Connection c, JsonObject m)
    {
        switch (m.Str("t"))
        {
            case Msg.Pong:
                _s.Ping(Stopwatch.GetElapsedTime(_pingAt).TotalMilliseconds);
                break;
            case Msg.Registered: Interlocked.Increment(ref _s.Registered); break;
            case Msg.Reject: Console.WriteLine($"{Machine}: bị từ chối · {m.Str("reason")}"); break;
            case Msg.Exam:
                if (_exam == null) Interlocked.Increment(ref _s.GotExam);
                _exam = m.As<ClientExam>("exam");
                break;
            case Msg.Collect:
                _collecting = true;
                c.Send(Msg.Submit, new { auto = true });
                _submitted = true;
                break;
            case Msg.Result: _resultSeen = true; Interlocked.Increment(ref _s.Results); break;
            case Msg.Release: _released = true; c.Close(); break;
        }
    }
}
