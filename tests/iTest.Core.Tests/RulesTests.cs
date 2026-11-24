using System.Net;
using System.Net.Sockets;

namespace ITest.Core.Tests;

/// <summary>Các quy tắc máy chủ tự kiểm tra: câu trả lời hợp lệ, thời gian nộp, chống chiếm phiên, ghi file an toàn.</summary>
public class RulesTests
{
    // Câu 1: một đáp án (4 phương án) · 2: nhiều đáp án (3 phương án) · 3: Đúng/Sai 4 ý · 4: trả lời ngắn · 5: đúng/sai 2 phương án · 6: tự luận
    private static (ExamSet x, CodeLayout l) Exam()
    {
        var x = new ExamSet { Codes = 1, Minutes = 45 };
        x.Questions.Add(new Question { Id = 1, Type = QuestionType.MultipleChoice, Options = new() { "a", "b", "c", "d" }, Correct = 1, Points = 1 });
        x.Questions.Add(new Question { Id = 2, Type = QuestionType.MultiSelect, Options = new() { "a", "b", "c" }, Corrects = new() { 0, 2 }, Points = 1 });
        x.Questions.Add(new Question { Id = 3, Type = QuestionType.TrueFalse, Options = new() { "a", "b", "c", "d" }, TfKey = new() { true, false, true, false }, Points = 1 });
        x.Questions.Add(new Question { Id = 4, Type = QuestionType.ShortAnswer, ShortKey = "12", Points = 1 });
        x.Questions.Add(new Question { Id = 5, Type = QuestionType.YesNo, Options = new() { "Đúng", "Sai" }, Correct = 0, Points = 1 });
        x.Questions.Add(new Question { Id = 6, Type = QuestionType.Essay, Options = new(), Points = 5 });
        return (x, Shuffler.Build(x)[0]);
    }

    [Fact]
    public void Answers_must_match_question_number_type_and_option_range()
    {
        var (x, l) = Exam();
        var s = new AnswerSheet { Code = l.Code };
        Assert.True(AnswerRules.SetChoice(x, l, s, 1, 3));
        Assert.False(AnswerRules.SetChoice(x, l, s, 1, 4));      // phương án ngoài phạm vi
        Assert.False(AnswerRules.SetChoice(x, l, s, 1, -1));
        Assert.False(AnswerRules.SetChoice(x, l, s, 0, 0));      // số câu không có trong đề
        Assert.False(AnswerRules.SetChoice(x, l, s, 7, 0));
        Assert.False(AnswerRules.SetChoice(x, l, s, 2, 0));      // sai loại câu
        Assert.False(AnswerRules.SetChoice(x, l, s, 6, 0));
        Assert.True(AnswerRules.SetChoice(x, l, s, 5, 1));       // đúng/sai 2 phương án
        Assert.False(AnswerRules.SetChoice(x, l, s, 5, 2));
        Assert.Equal(3, s.Choices[1]);                            // lần gửi sai không làm hỏng câu trả lời đã lưu

        Assert.True(AnswerRules.SetMulti(x, l, s, 2, new[] { 2, 0, 2 }));
        Assert.Equal(new[] { 0, 2 }, s.Multi[2]);
        Assert.False(AnswerRules.SetMulti(x, l, s, 2, new[] { 0, 3 }));   // câu chỉ có 3 phương án
        Assert.False(AnswerRules.SetMulti(x, l, s, 1, new[] { 0 }));
        Assert.Equal(new[] { 0, 2 }, s.Multi[2]);

        Assert.True(AnswerRules.SetTf(x, l, s, 3, new Dictionary<int, bool> { [0] = true, [3] = false }));
        Assert.False(AnswerRules.SetTf(x, l, s, 3, new Dictionary<int, bool> { [4] = true }));
        Assert.False(AnswerRules.SetTf(x, l, s, 2, new Dictionary<int, bool> { [0] = true }));

        Assert.True(AnswerRules.SetShort(x, l, s, 4, new string('9', 5000)));
        Assert.Equal(AnswerRules.MaxShortChars, s.Short[4].Length);
        Assert.False(AnswerRules.SetShort(x, l, s, 6, "x"));
        Assert.True(AnswerRules.SetEssay(x, l, s, 6, new string('a', AnswerRules.MaxEssayChars + 10)));
        Assert.Equal(AnswerRules.MaxEssayChars, s.Essays[6].Length);
        Assert.False(AnswerRules.SetEssay(x, l, s, 1, "x"));
        Assert.False(s.Essays.ContainsKey(1));
    }

    [Fact]
    public void Final_answers_replace_valid_items_and_skip_bad_ones()
    {
        var (x, l) = Exam();
        var s = new AnswerSheet { Code = l.Code };
        s.Choices[1] = 0; s.Choices[5] = 1; s.Essays[6] = "bản cũ";
        var final = new FinalAnswers
        {
            Choices = new() { [1] = 1, [99] = 0 },                // câu 99 không có: bỏ qua
            Essays = new() { [6] = "bản cuối cùng, có thêm đoạn vừa gõ" },
            Multi = new() { [2] = new() { 0, 2 }, [1] = new() { 0 } },   // câu 1 không phải câu nhiều đáp án
            Tf = new() { [3] = new() { [0] = true, [1] = false, [2] = true, [3] = false } },
            ShortAns = new() { [4] = "12" }
        };
        Assert.Equal(2, AnswerRules.ApplyFinal(x, l, s, final));
        Assert.Equal(1, s.Choices[1]);
        Assert.Equal(1, s.Choices[5]);                            // không có trong bản cuối: giữ bản máy chủ đang có
        Assert.Equal("bản cuối cùng, có thêm đoạn vừa gõ", s.Essays[6]);
        Assert.False(s.Choices.ContainsKey(99));
        Assert.False(s.Multi.ContainsKey(1));
        var r = Scoring.GradeObjective(x, l, s);
        Assert.Equal(4, r.Right);                                 // câu 1–4 đúng, câu 5 chọn Sai (đáp án Đúng)
        Assert.Equal(1, r.Wrong);
        Assert.Equal(0, AnswerRules.ApplyFinal(x, l, s, null));
    }

    [Fact]
    public void Early_submit_is_refused_unless_the_server_is_collecting()
    {
        Assert.False(SessionRules.CanSubmit(30 * 60, 15, collecting: false));
        Assert.True(SessionRules.CanSubmit(15 * 60, 15, collecting: false));
        Assert.True(SessionRules.CanSubmit(15 * 60 + SessionRules.ClockSlackSeconds, 15, collecting: false));
        Assert.False(SessionRules.CanSubmit(15 * 60 + SessionRules.ClockSlackSeconds + 1, 15, collecting: false));
        Assert.True(SessionRules.CanSubmit(30 * 60, 15, collecting: true));   // máy chủ thu bài: luôn nhận
        Assert.True(SessionRules.CanSubmit(0, 0, collecting: false));
        Assert.False(SessionRules.CanSubmit(60, 0, collecting: false));
    }

    [Fact]
    public void A_started_session_needs_its_token_or_an_approved_move()
    {
        // Đã có bài làm: mất kết nối cũng KHÔNG cho máy khác vào bằng số báo danh đó.
        Assert.False(SessionRules.MayAttach(started: true, tokenOk: false, moveApproved: false, oldConnectionAlive: false));
        Assert.False(SessionRules.MayAttach(started: true, tokenOk: false, moveApproved: false, oldConnectionAlive: true));
        Assert.True(SessionRules.MayAttach(started: true, tokenOk: true, moveApproved: false, oldConnectionAlive: true));
        Assert.True(SessionRules.MayAttach(started: true, tokenOk: false, moveApproved: true, oldConnectionAlive: false));
        // Còn ở phòng chờ: được vào lại nếu phiên cũ đã rời phòng.
        Assert.True(SessionRules.MayAttach(started: false, tokenOk: false, moveApproved: false, oldConnectionAlive: false));
        Assert.False(SessionRules.MayAttach(started: false, tokenOk: false, moveApproved: false, oldConnectionAlive: true));
        Assert.NotEqual(SessionRules.NewToken(), SessionRules.NewToken());
        Assert.Equal(32, SessionRules.NewToken().Length);
    }

    [Fact]
    public void Grading_ignores_out_of_range_picks_and_broken_permutations()
    {
        var (x, l) = Exam();
        var s = new AnswerSheet { Code = l.Code };
        s.Multi[2] = new() { 0, 2, 7 };                           // đúng 2 phương án + 1 phương án không tồn tại: không được điểm
        s.Choices[1] = 9;
        s.Tf[3] = new() { [0] = true, [9] = true };
        var r = Scoring.GradeObjective(x, l, s);
        Assert.Equal(0.1, r.Score, 3);                            // chỉ ý a của câu Đúng/Sai đúng (1/4 ý → 10%)
        Assert.Equal(0, r.Right);

        // Hoán vị hỏng (lặp phần tử / ngoài phạm vi) → dùng thứ tự gốc, không ném lỗi.
        l.Perm[1] = new[] { 0, 0, 5, 1 };
        Assert.Equal(new[] { 0, 1, 2, 3 }, l.PermOf(x.Questions[0]));
        l.Perm[1] = new[] { 3, 2, 1, 0 };
        Assert.Equal(new[] { 3, 2, 1, 0 }, l.PermOf(x.Questions[0]));
        // Bố cục chứa mã câu không có trong đề: bỏ qua câu đó.
        l.Order.Add(999);
        Assert.Equal(0.1, Scoring.GradeObjective(x, l, s).Score, 3);
    }

    [Fact]
    public async Task Ordered_writer_never_lets_an_older_copy_win()
    {
        var dir = Path.Combine(Path.GetTempPath(), "itest_" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "sub", "state.json");
        try
        {
            var w = new OrderedWriter();
            var tasks = Enumerable.Range(1, 60).Select(i => w.Write(path, "bản " + i)).ToArray();
            Assert.All(await Task.WhenAll(tasks), ok => Assert.True(ok));
            Assert.Equal("bản 60", File.ReadAllText(path));
            Assert.True(w.WriteNow(path, "bản cuối"));
            Assert.Equal("bản cuối", File.ReadAllText(path));
            Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));   // không để lại file tạm
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task Ordered_writer_reports_failure_instead_of_throwing()
    {
        var w = new OrderedWriter();
        var bad = Path.Combine(Path.GetTempPath(), "itest_" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(bad, "đây là file");
        try
        {
            // Đường dẫn đi "xuyên qua" một file: không thể tạo thư mục, việc ghi phải báo false.
            Assert.False(await w.Write(Path.Combine(bad, "x", "state.json"), "{}"));
        }
        finally { File.Delete(bad); }
    }

    private static async Task<(Connection server, Connection client, TcpListener l)> Pair()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        var acceptTask = l.AcceptTcpClientAsync();
        var client = await Connection.ConnectAsync("127.0.0.1", port);
        var server = new Connection(await acceptTask, isServer: true);
        return (server, client, l);
    }

    [Fact]
    public async Task Send_never_hangs_after_the_peer_is_gone()
    {
        var (server, client, l) = await Pair();
        try
        {
            server.Start(); client.Start();
            await client.SendAsync(Msg.Ping).WaitAsync(TimeSpan.FromSeconds(10));
            var closed = new TaskCompletionSource();
            server.Closed += _ => closed.TrySetResult();
            client.Close();
            // Gửi liên tục trong lúc kết nối đang đóng: mọi Task phải kết thúc, không cái nào treo.
            var sends = Enumerable.Range(0, 200).Select(i => server.SendAsync(Msg.Notice, new { kind = "all", text = new string('x', 2000) + i })).ToArray();
            await Task.WhenAll(sends).WaitAsync(TimeSpan.FromSeconds(15));
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await server.SendAsync(Msg.Ping).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { server.Close(); client.Close(); l.Stop(); }
    }

    [Fact]
    public async Task Silent_connection_is_closed_after_the_idle_timeout()
    {
        var (server, client, l) = await Pair();
        try
        {
            var closed = new TaskCompletionSource();
            server.Closed += _ => closed.TrySetResult();
            server.IdleTimeout = TimeSpan.FromSeconds(1);
            server.Start(); client.Start();
            await client.SendAsync(Msg.Ping).WaitAsync(TimeSpan.FromSeconds(10));   // bắt tay xong, rồi im lặng
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally { server.Close(); client.Close(); l.Stop(); }
    }

    [Fact]
    public async Task Sending_to_a_peer_that_stops_reading_is_bounded()
    {
        // Bên kia không bao giờ đọc: hàng đợi gửi đầy thì kết nối tự đóng, bộ nhớ không phình vô hạn và không Task nào treo.
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        var acceptTask = l.AcceptTcpClientAsync();
        var client = await Connection.ConnectAsync("127.0.0.1", port);
        using var raw = await acceptTask;   // phía máy chủ giả: không bắt tay, không đọc
        try
        {
            var closed = new TaskCompletionSource();
            client.Closed += _ => closed.TrySetResult();
            client.Start();
            var sends = new List<Task>();
            for (int i = 0; i < 6000 && !closed.Task.IsCompleted; i++) sends.Add(client.SendAsync(Msg.Answer, new { no = 1, choice = i % 4 }));
            Assert.True(sends.Count <= 5200, "Hàng đợi gửi phải có giới hạn.");
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await Task.WhenAll(sends).WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { client.Close(); l.Stop(); }
    }
}
