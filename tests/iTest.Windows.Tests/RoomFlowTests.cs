using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using System.Windows.Threading;
using ITest.Core;
using ITest.Server;

namespace ITest.Windows.Tests;

/// <summary>
/// Chạy phòng thi thật của iTest-Server (cổng mạng thật, đồng hồ thật) với máy thí sinh giả nói đúng giao thức,
/// để kiểm tra các quy tắc: nộp sớm bị từ chối, chống chiếm phiên bằng token, thu bài chờ bản cuối,
/// xác nhận sau khi đã lưu, bài đã chốt không bị ghi đè, điểm chỉ gửi khi được phép.
/// </summary>
public class RoomFlowTests
{
    private const string FinalText = "FINAL-ESSAY-TEXT-0001";

    private static ExamPackage.Content Package() => new()
    {
        Exam = new ExamSet
        {
            ExamName = "Kiem tra", Subject = "Toan", Minutes = 45, Codes = 1,
            Questions =
            {
                new Question { Id = 1, Type = QuestionType.MultipleChoice, Text = "Cau 1", Options = new() { "a", "b", "c", "d" }, Correct = 0, Points = 5 },
                new Question { Id = 2, Type = QuestionType.MultipleChoice, Text = "Cau 2", Options = new() { "a", "b", "c", "d" }, Correct = 1, Points = 3 },
                new Question { Id = 3, Type = QuestionType.Essay, Text = "Cau 3", Options = new(), Points = 2 }
            }
        },
        Media = new()
    };

    /// <summary>Phòng thi chạy trên một luồng giao diện riêng (RoomService cần Dispatcher của WPF).</summary>
    private sealed class Host : IDisposable
    {
        private readonly Thread _thread;
        private Dispatcher _d = null!;
        public RoomService Room = null!;

        public Host(bool showScore = false)
        {
            using var ready = new ManualResetEventSlim();
            _thread = new Thread(() =>
            {
                _d = Dispatcher.CurrentDispatcher;
                Room = new RoomService(_d);
                ready.Set();
                Dispatcher.Run();
            }) { IsBackground = true };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            ready.Wait();
            Do(() =>
            {
                Room.Room = "Phong thu " + Guid.NewGuid().ToString("N")[..6];
                Room.Proctor = "Giam thi";
                Room.SubmitWindowMinutes = 15;
                Room.LoadPackage(Package(), "de-thu.itz");
                Room.ShowScore = showScore;
                Room.Open();
            });
        }

        public void Do(Action a) => _d.Invoke(a);
        public T Get<T>(Func<T> f) => _d.Invoke(f);

        public async Task Until(Func<bool> condition, string what, int seconds = 20)
        {
            var end = DateTime.UtcNow.AddSeconds(seconds);
            while (!Get(condition))
            {
                if (DateTime.UtcNow > end) throw new TimeoutException("Chờ quá lâu: " + what);
                await Task.Delay(100);
            }
        }

        public void Dispose()
        {
            string dir = "";
            try { dir = Get(() => Room.BackupDir); Do(() => Room.Dispose()); } catch { }
            try { _d.InvokeShutdown(); _thread.Join(5000); } catch { }
            try { if (dir.Length > 0) Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>Máy thí sinh giả: kết nối mã hoá thật, tự gửi nhịp tim như iTest.</summary>
    private sealed class Peer : IDisposable
    {
        private readonly Connection _c;
        private readonly Channel<JsonObject> _inbox = Channel.CreateUnbounded<JsonObject>();
        private readonly Timer _ping;

        private Peer(Connection c)
        {
            _c = c;
            c.Received += (_, m) => _inbox.Writer.TryWrite(m);
            c.Closed += _ => _inbox.Writer.TryComplete();
            c.Start();
            _ping = new Timer(_ => c.Send(Msg.Ping), null, 1000, 2000);
        }

        public static async Task<Peer> Open(string pin)
        {
            var p = new Peer(await Connection.ConnectAsync("127.0.0.1", Ports.Session, pin));
            await p.Wait(Msg.Welcome);
            return p;
        }

        public void Send(string type, object? payload = null) => _c.Send(type, payload);

        public void Register(string sbd, string machine, string token)
        {
            Send(Msg.Hello, new { machine, workgroup = "TEST", ip = "127.0.0.1", token });
            Send(Msg.Register, new { Sbd = sbd, FullName = "Thi sinh " + sbd, BirthDate = "01/01/2009", ClassName = "10A1", token });
        }

        /// <summary>Chờ tin có loại <paramref name="type"/> (bỏ qua các tin khác đến trước).</summary>
        public async Task<JsonObject> Wait(string type, int seconds = 20)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
            try
            {
                while (true)
                {
                    var m = await _inbox.Reader.ReadAsync(cts.Token);
                    if (m.Str("t") == type) return m;
                }
            }
            catch (OperationCanceledException) { throw new TimeoutException($"Không nhận được tin '{type}' sau {seconds} giây."); }
            catch (ChannelClosedException) { throw new InvalidOperationException($"Kết nối đóng trước khi nhận được tin '{type}'."); }
        }

        public void Dispose() { _ping.Dispose(); _c.Close(); }
    }

    /// <summary>Đọc file trạng thái trong lúc máy chủ có thể đang thay bản mới (thử lại vài lần nếu vướng).</summary>
    private static string ReadShared(string path)
    {
        for (int i = 0; ; i++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var sr = new StreamReader(fs);
                return sr.ReadToEnd();
            }
            catch (IOException) when (i < 20) { Thread.Sleep(50); }
            catch (UnauthorizedAccessException) when (i < 20) { Thread.Sleep(50); }
        }
    }

    [Fact]
    public async Task Early_submit_takeover_and_collection_follow_the_rules()
    {
        using var host = new Host();
        string pin = host.Get(() => host.Room.RoomPin);

        var a = await Peer.Open(pin);
        a.Register("001", "PC-A", "");
        string token = (await a.Wait(Msg.Registered)).Str("token");
        Assert.Equal(32, token.Length);

        host.Do(() => host.Room.Start());
        var examMsg = await a.Wait(Msg.Exam);
        Assert.False(examMsg.Bool("paused"));
        var exam = examMsg.As<ClientExam>("exam");
        Assert.NotNull(exam);
        Assert.Equal(3, exam!.Questions.Count);
        Assert.Equal(64, exam.Fingerprint.Length);
        var cand = host.Get(() => host.Room.Candidates.Single());

        // --- Kiểm tra bài làm: câu trả lời sai số câu / loại câu / phương án bị bỏ qua, không làm hỏng bài đã lưu.
        a.Send(Msg.Answer, new { no = 1, choice = 0 });
        a.Send(Msg.Answer, new { no = 1, choice = 9 });
        a.Send(Msg.Answer, new { no = 3, choice = 0 });
        a.Send(Msg.Answer, new { no = 99, choice = 0 });
        a.Send(Msg.AnswerEx, new AnswerEx { No = 1, Type = QuestionType.MultiSelect, Choices = new() { 0, 1 } });
        a.Send(Msg.Essay, new { no = 1, text = "sai loai cau" });
        a.Send(Msg.Essay, new { no = 3, text = "ban nhap" });

        // --- Thời gian nộp: còn 45 phút, quy định chỉ nộp trong 15 phút cuối; cờ auto không được bỏ qua quy định.
        a.Send(Msg.Submit, new { auto = true, final = new FinalAnswers { Essays = new() { [3] = "KHONG-DUOC-NHAN" } } });
        var denied = await a.Wait(Msg.SubmitDenied);
        Assert.True(denied.Int("secondsLeft") > 15 * 60);
        host.Do(() =>
        {
            Assert.Equal(CandStatus.Doing, cand.Status);
            Assert.Equal(0, cand.Sheet.Choices[1]);
            Assert.Single(cand.Sheet.Choices);
            Assert.Empty(cand.Sheet.Multi);
            Assert.Single(cand.Sheet.Essays);
            Assert.Equal("ban nhap", cand.Sheet.Essays[3]);
            Assert.Null(cand.Sheet.SubmittedAt);
        });

        // --- Chống chiếm phiên: máy khác dùng đúng số báo danh nhưng không có token.
        using (var b = await Peer.Open(pin))
        {
            b.Register("001", "PC-B", "");
            Assert.Contains("001", (await b.Wait(Msg.Reject)).Str("reason"));
        }
        a.Dispose();
        await host.Until(() => cand.Status == CandStatus.Lost, "máy A được ghi nhận mất kết nối");
        using (var c = await Peer.Open(pin))
        {
            // Phiên đang "mất kết nối" cũng không cho máy lạ vào thay.
            c.Register("001", "PC-C", "token-doan-bua");
            await c.Wait(Msg.Reject);
        }
        Assert.Equal(CandStatus.Lost, host.Get(() => cand.Status));

        // --- Máy cũ nối lại với đúng token: nhận lại đề và bài làm đã lưu.
        using var a2 = await Peer.Open(pin);
        a2.Register("001", "PC-A", token);
        Assert.Equal(token, (await a2.Wait(Msg.Registered)).Str("token"));
        await a2.Wait(Msg.Exam);
        var resume = await a2.Wait(Msg.Resume);
        Assert.Equal(0, resume.As<Dictionary<int, int>>("choices")![1]);
        Assert.Equal("ban nhap", resume.As<Dictionary<int, string>>("essays")![3]);
        Assert.Equal(CandStatus.Doing, host.Get(() => cand.Status));

        // --- Thu bài: máy chủ chờ bản cuối cùng (có phần tự luận vừa gõ) rồi mới chốt và xác nhận.
        host.Do(() => host.Room.AddMinutesAll(-31));   // còn dưới 15 phút
        Assert.True(host.Get(() => host.Room.CanCollectAll));
        host.Do(() => host.Room.CollectAll());
        await a2.Wait(Msg.Collect);
        Assert.Equal(CandStatus.Doing, host.Get(() => cand.Status));          // chưa chốt: đang chờ bản cuối
        Assert.Equal(Phase.Running, host.Get(() => host.Room.Phase));
        a2.Send(Msg.Submit, new { auto = true, final = new FinalAnswers { Choices = new() { [2] = 1, [7] = 0 }, Essays = new() { [3] = FinalText } } });
        var result = await a2.Wait(Msg.Result);

        // Xác nhận chỉ đến SAU KHI bài đã nằm trên đĩa máy chủ.
        string stateFile = Path.Combine(host.Get(() => host.Room.BackupDir), "trang-thai.json");
        Assert.Contains(FinalText, ReadShared(stateFile));
        // Ẩn điểm: không có con số nào được gửi xuống máy thí sinh.
        Assert.False(result.Bool("showScore"));
        Assert.Null(result["score"]);
        Assert.Null(result["right"]);
        Assert.Null(result["wrong"]);
        host.Do(() =>
        {
            Assert.Equal(CandStatus.Done, cand.Status);
            Assert.Equal(FinalText, cand.Sheet.Essays[3]);
            Assert.Equal(1, cand.Sheet.Choices[2]);
            Assert.Equal(0, cand.Sheet.Choices[1]);                           // câu không có trong bản cuối: giữ bản máy chủ
            Assert.False(cand.Sheet.Choices.ContainsKey(7));
        });
        await host.Until(() => host.Room.Phase == Phase.Collected, "phòng chuyển sang đã thu bài");

        // --- Bài đã chốt: tin nộp lại / câu trả lời gửi lại do mất mạng không được ghi đè.
        a2.Send(Msg.Essay, new { no = 3, text = "GHI-DE" });
        a2.Send(Msg.Answer, new { no = 1, choice = 3 });
        a2.Send(Msg.Submit, new { auto = true, final = new FinalAnswers { Essays = new() { [3] = "GHI-DE" }, Choices = new() { [1] = 3 } } });
        Assert.False((await a2.Wait(Msg.Result)).Bool("showScore"));
        host.Do(() =>
        {
            Assert.Equal(FinalText, cand.Sheet.Essays[3]);
            Assert.Equal(0, cand.Sheet.Choices[1]);
        });
    }

    [Fact]
    public async Task Collection_times_out_without_a_final_copy_and_score_is_sent_only_when_allowed()
    {
        using var host = new Host(showScore: true);
        string pin = host.Get(() => host.Room.RoomPin);
        using var a = await Peer.Open(pin);
        a.Register("002", "PC-D", "");
        await a.Wait(Msg.Registered);
        host.Do(() => host.Room.Start());
        await a.Wait(Msg.Exam);
        a.Send(Msg.Answer, new { no = 1, choice = 0 });   // đúng (5 điểm)
        a.Send(Msg.Answer, new { no = 2, choice = 3 });   // sai
        var cand = host.Get(() => host.Room.Candidates.Single());
        await host.Until(() => cand.Sheet.Choices.Count == 2, "máy chủ nhận hai câu trả lời");

        host.Do(() => host.Room.AddMinutesAll(-31));
        host.Do(() => host.Room.CollectAll());
        await a.Wait(Msg.Collect);
        var started = DateTime.UtcNow;
        // Máy thí sinh không gửi bản cuối (treo): máy chủ chờ tối đa 10 giây rồi chốt theo bản đang giữ.
        var result = await a.Wait(Msg.Result, seconds: 30);
        var waited = (DateTime.UtcNow - started).TotalSeconds;
        Assert.InRange(waited, 7, 20);
        Assert.True(result.Bool("showScore"));
        Assert.Equal(1, result.Int("right"));
        Assert.Equal(1, result.Int("wrong"));
        Assert.Equal(5.0, result.Dbl("score"));
        Assert.Equal(CandStatus.Done, host.Get(() => cand.Status));
        await host.Until(() => host.Room.Phase == Phase.Collected, "phòng chuyển sang đã thu bài");
    }
}
