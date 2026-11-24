using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;

namespace ITest.Core;

public static class Ports
{
    public const int Discovery = 47800;   // UDP: máy chủ quảng bá phòng thi
    public const int Session = 47801;     // TCP: phiên làm bài
}

/// <summary>Các loại thông điệp giữa máy thí sinh (C) và máy chủ (S).</summary>
public static class Msg
{
    // C → S
    public const string Hello = "hello";          // machine, workgroup
    public const string Register = "register";    // CandidateInfo
    public const string Answer = "answer";        // no, choice
    public const string Essay = "essay";          // no, text
    public const string Submit = "submit";        // auto, final: FinalAnswers (toàn bộ bài làm cuối cùng)
    public const string Focus = "focus";          // lost: bool, reason
    public const string AnswerEx = "answerEx";    // AnswerEx: câu nhiều đáp án / Đúng-Sai / trả lời ngắn
    public const string Cheat = "cheat";          // kind, detail: dấu hiệu gian lận phát hiện trên máy thí sinh
    public const string Ping = "ping";            // nhịp tim mỗi 3 giây
    public const string Pong = "pong";            // S → C: trả lời nhịp tim
    // S → C
    public const string Welcome = "welcome";      // room, proctor, department, school
    public const string Registered = "registered";
    public const string Reject = "reject";        // reason
    public const string EditRequest = "editRequest"; // reason
    public const string Exam = "exam";            // ClientExam, secondsLeft, submitWindow, paused
    public const string SubmitDenied = "submitDenied"; // secondsLeft, paused, reason: chưa tới thời gian được nộp, làm bài tiếp
    public const string Resume = "resume";        // choices, essays
    public const string Time = "time";            // secondsLeft, paused
    public const string Notice = "notice";        // kind (all|me), text
    public const string Collect = "collect";      // máy chủ thu bài
    public const string Result = "result";        // showScore; chỉ khi showScore mới kèm right, wrong, blank, score. Là lời xác nhận "máy chủ đã lưu bài".
    public const string Release = "release";      // cho phép thoát ứng dụng
}

/// <summary>Câu trả lời cho các loại câu ngoài trắc nghiệm một đáp án.</summary>
public sealed class AnswerEx
{
    public int No { get; set; }
    public QuestionType Type { get; set; }
    public List<int> Choices { get; set; } = new();
    public Dictionary<int, bool> Tf { get; set; } = new();
    public string Text { get; set; } = "";
}

/// <summary>Toàn bộ bài làm cuối cùng, gửi kèm tin nộp bài để máy chủ không bỏ sót chữ thí sinh vừa gõ.</summary>
public sealed class FinalAnswers
{
    public Dictionary<int, int> Choices { get; set; } = new();
    public Dictionary<int, string> Essays { get; set; } = new();
    public Dictionary<int, List<int>> Multi { get; set; } = new();
    public Dictionary<int, Dictionary<int, bool>> Tf { get; set; } = new();
    public Dictionary<int, string> ShortAns { get; set; } = new();
}

/// <summary>Gói tin quảng bá phòng thi qua UDP.</summary>
public sealed class RoomBeacon
{
    public string App { get; set; } = "iTest";
    /// <summary>Mã định danh phiên của máy chủ, dùng để gộp các gói tin trùng.</summary>
    public string ServerId { get; set; } = "";
    public string Room { get; set; } = "";
    public string Exam { get; set; } = "";
    public string Proctor { get; set; } = "";
    public string Workgroup { get; set; } = "";
    public string Host { get; set; } = "";
    public string Address { get; set; } = "";
    public int Port { get; set; } = Ports.Session;
    public bool Open { get; set; }
    public int Candidates { get; set; }
}

/// <summary>
/// Kết nối TCP được mã hoá đầu-cuối:
///  1. Bắt tay: mỗi bên gửi khung rõ "ITS1" + khoá công khai ECDH P-256 tạm thời.
///  2. Khoá phiên AES-256 = SHA-256(bí mật chung ECDH ‖ "iTest-session-v1:" ‖ mã phòng thi).
///  3. Mỗi khung tin: 4 byte độ dài | nonce 12 byte (vai trò + bộ đếm) | thẻ GCM 16 byte | bản mã JSON UTF-8.
/// Người nghe lén trong mạng LAN không đọc được đề thi và bài làm; khung bị sửa sẽ bị loại.
/// </summary>
public sealed class Connection : IDisposable
{
    private static readonly byte[] Magic = "ITS1"u8.ToArray();
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly System.Threading.Channels.Channel<(byte[] plain, TaskCompletionSource done)> _outbox =
        System.Threading.Channels.Channel.CreateUnbounded<(byte[], TaskCompletionSource)>(new() { SingleReader = false });
    private readonly bool _isServer;
    private readonly string _secret;
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private AesGcm? _aes;
    private long _sendCounter;
    private long _recvCounter;
    // Giới hạn hàng đợi gửi: máy bên kia treo (không đọc) thì đóng kết nối thay vì để bộ nhớ phình mãi.
    private const int MaxQueuedMessages = 5000;
    private const long MaxQueuedBytes = 256L * 1024 * 1024;
    private int _queued;
    private long _queuedBytes;
    private long _lastRxTick = Environment.TickCount64;
    private System.Threading.Timer? _idleTimer;
    /// <summary>Thời gian tối đa để ghi xong một khung tin (cộng thêm theo kích thước khung). Quá hạn thì đóng kết nối.</summary>
    public TimeSpan WriteTimeout { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>Không nhận được khung tin nào trong khoảng này thì đóng kết nối (đặt trước khi gọi Start; mặc định: không giới hạn).</summary>
    public TimeSpan IdleTimeout { get; set; } = Timeout.InfiniteTimeSpan;
    public string RemoteAddress { get; }
    public event Action<Connection, JsonObject>? Received;
    public event Action<Connection>? Closed;
    private int _closed;

    /// <param name="secret">Bí mật phòng thi (mã phòng) trộn vào khoá phiên; hai bên phải trùng mới giải mã được cho nhau.</param>
    public Connection(TcpClient client, bool isServer, string secret = "")
    {
        _client = client;
        _client.NoDelay = true;
        _isServer = isServer;
        _secret = secret ?? "";
        _stream = client.GetStream();
        RemoteAddress = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "?";
    }

    public static async Task<Connection> ConnectAsync(string host, int port, string secret = "", CancellationToken ct = default)
    {
        var c = new TcpClient();
        await c.ConnectAsync(host, port, ct).ConfigureAwait(false);
        return new Connection(c, isServer: false, secret);
    }

    public void Start() => _ = Task.Run(RunAsync);

    private async Task RunAsync()
    {
        try
        {
            // Bắt tay phải xong trong 10 giây: kết nối mở rồi im lặng (quét cổng, máy treo) sẽ bị đóng, không chiếm chỗ mãi.
            var hs = HandshakeAsync();
            if (await Task.WhenAny(hs, Task.Delay(10000)).ConfigureAwait(false) != hs)
            {
                _ = hs.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                throw new TimeoutException();
            }
            await hs.ConfigureAwait(false);
            _ready.TrySetResult(true);
            _ = Task.Run(WriteLoop);
            Volatile.Write(ref _lastRxTick, Environment.TickCount64);
            if (IdleTimeout != Timeout.InfiniteTimeSpan && IdleTimeout > TimeSpan.Zero)
            {
                var idleMs = (long)IdleTimeout.TotalMilliseconds;
                _idleTimer = new System.Threading.Timer(_ => { if (Environment.TickCount64 - Volatile.Read(ref _lastRxTick) > idleMs) Close(); }, null, 2000, 2000);
                if (Volatile.Read(ref _closed) == 1) _idleTimer.Dispose();
            }
            var lenBuf = new byte[4];
            while (true)
            {
                await ReadExact(lenBuf, 4).ConfigureAwait(false);
                int len = BitConverter.ToInt32(lenBuf, 0);
                if (len < 28 || len > 64 * 1024 * 1024) throw new InvalidDataException("Khung tin không hợp lệ");
                var buf = new byte[len];
                await ReadExact(buf, len).ConfigureAwait(false);
                var plain = Open(buf);
                Volatile.Write(ref _lastRxTick, Environment.TickCount64);
                if (JsonNode.Parse(plain) is JsonObject node) Received?.Invoke(this, node);
            }
        }
        catch { /* mất kết nối hoặc khung tin bị sửa */ }
        finally
        {
            _ready.TrySetResult(false);
            Close();
        }
    }

    private byte[] Open(byte[] buf)
    {
        // Khung phải đến từ phía bên kia và bộ đếm tăng dần: chặn phát lại hoặc phản xạ khung tin.
        int role = BitConverter.ToInt32(buf, 0);
        long ctr = BitConverter.ToInt64(buf, 4);
        if (role != (_isServer ? ClientRole : ServerRole) || ctr <= _recvCounter) throw new InvalidDataException("Khung tin bị phát lại");
        var plain = new byte[buf.Length - 28];
        _aes!.Decrypt(buf.AsSpan(0, 12), buf.AsSpan(28), buf.AsSpan(12, 16), plain);
        _recvCounter = ctr;
        return plain;
    }

    private const int ServerRole = 0x53525652, ClientRole = 0x434C4E54;

    private async Task HandshakeAsync()
    {
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var pub = ecdh.ExportSubjectPublicKeyInfo();
        var hello = new byte[4 + pub.Length];
        Magic.CopyTo(hello, 0);
        pub.CopyTo(hello, 4);
        await _stream.WriteAsync(BitConverter.GetBytes(hello.Length)).ConfigureAwait(false);
        await _stream.WriteAsync(hello).ConfigureAwait(false);

        var lenBuf = new byte[4];
        await ReadExact(lenBuf, 4).ConfigureAwait(false);
        int len = BitConverter.ToInt32(lenBuf, 0);
        if (len < 8 || len > 4096) throw new InvalidDataException("Bắt tay không hợp lệ");
        var peer = new byte[len];
        await ReadExact(peer, len).ConfigureAwait(false);
        if (!peer.AsSpan(0, 4).SequenceEqual(Magic)) throw new InvalidDataException("Phiên bản giao thức không khớp");

        using var other = ECDiffieHellman.Create();
        other.ImportSubjectPublicKeyInfo(peer.AsSpan(4), out _);
        // Khoá phiên = SHA-256(bí mật ECDH ‖ "iTest-session-v1:" ‖ mã phòng). Mã phòng sai thì hai bên ra khoá khác nhau,
        // không giải mã được cho nhau — chặn máy chủ giả và máy thí sinh không có mã phòng.
        var info = Encoding.UTF8.GetBytes("iTest-session-v1:" + _secret);
        var key = ecdh.DeriveKeyFromHash(other.PublicKey, HashAlgorithmName.SHA256, null, info);
        _aes = new AesGcm(key, 16);
    }

    private async Task ReadExact(byte[] buf, int count)
    {
        int read = 0;
        while (read < count)
        {
            int n = await _stream.ReadAsync(buf.AsMemory(read, count - read)).ConfigureAwait(false);
            if (n == 0) throw new EndOfStreamException();
            read += n;
        }
    }

    /// <summary>
    /// Xếp tin vào hàng đợi gửi. Tin được mã hoá và gửi đúng thứ tự gọi (kể cả các tin gọi trước khi bắt tay xong).
    /// Task hoàn thành khi tin đã được ghi ra mạng, hoặc khi kết nối đóng.
    /// </summary>
    public Task SendAsync(string type, object? payload = null)
    {
        if (Volatile.Read(ref _closed) == 1) return Task.CompletedTask;
        var obj = payload == null ? new JsonObject() : (JsonSerializer.SerializeToNode(payload, ExamPackage.Json) as JsonObject ?? new JsonObject());
        obj["t"] = type;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bytes = Encoding.UTF8.GetBytes(obj.ToJsonString());
        if (Interlocked.Increment(ref _queued) > MaxQueuedMessages || Interlocked.Add(ref _queuedBytes, bytes.Length) > MaxQueuedBytes)
        {
            Close();   // bên kia không nhận kịp quá lâu: coi như mất kết nối, bên gọi sẽ kết nối lại
            return Task.CompletedTask;
        }
        if (!_outbox.Writer.TryWrite((bytes, done))) done.TrySetResult();
        // Kết nối vừa đóng xen giữa: bảo đảm không tin nào bị treo chờ mãi.
        if (Volatile.Read(ref _closed) == 1) { while (_outbox.Reader.TryRead(out var x)) x.done.TrySetResult(); done.TrySetResult(); }
        return done.Task;
    }

    private async Task WriteLoop()
    {
        TaskCompletionSource? current = null;   // tin đang ghi dở: nếu ghi lỗi vẫn phải báo xong để bên gọi không chờ mãi
        try
        {
            await foreach (var (plain, done) in _outbox.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                current = done;
                Interlocked.Decrement(ref _queued);
                Interlocked.Add(ref _queuedBytes, -plain.Length);
                var frame = new byte[4 + 28 + plain.Length];
                BitConverter.GetBytes(28 + plain.Length).CopyTo(frame, 0);
                Seal(frame, plain);
                // Máy bên kia treo hoặc rút dây mạng: việc ghi không được chờ vô hạn (tối thiểu ~50 KB/giây cho khung lớn).
                using var cts = new CancellationTokenSource(WriteTimeout + TimeSpan.FromSeconds(frame.Length / 50_000));
                await _stream.WriteAsync(frame, cts.Token).ConfigureAwait(false);
                if (_outbox.Reader.Count == 0) await _stream.FlushAsync(cts.Token).ConfigureAwait(false);
                done.TrySetResult();
            }
        }
        catch { }
        finally
        {
            Close();
            current?.TrySetResult();
            while (_outbox.Reader.TryRead(out var x)) x.done.TrySetResult();
        }
    }

    /// <summary>nonce = 4 byte vai trò (máy chủ/máy thí sinh) + 8 byte bộ đếm tăng dần → không lặp lại trong một phiên.</summary>
    private void Seal(byte[] frame, byte[] plain)
    {
        var nonce = frame.AsSpan(4, 12);
        BitConverter.GetBytes(_isServer ? ServerRole : ClientRole).CopyTo(nonce);
        BitConverter.GetBytes(++_sendCounter).CopyTo(nonce[4..]);
        _aes!.Encrypt(nonce, plain, frame.AsSpan(32), frame.AsSpan(16, 16));
    }

    public void Send(string type, object? payload = null) => _ = SendAsync(type, payload);

    public void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1) return;
        try { _idleTimer?.Dispose(); } catch { }
        try { _client.Close(); } catch { }
        _outbox.Writer.TryComplete();
        while (_outbox.Reader.TryRead(out var x)) x.done.TrySetResult();
        _ready.TrySetResult(false);
        Closed?.Invoke(this);
    }

    public void Dispose() { Close(); _aes?.Dispose(); }
}

public static class JsonExt
{
    /// <summary>Đọc chuỗi; trả về "" nếu thiếu hoặc sai kiểu (không ném lỗi với dữ liệu lạ từ mạng).</summary>
    public static string Str(this JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s ?? "" : "";
    public static double Dbl(this JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;
    public static int Int(this JsonObject o, string key, int def = 0) => o[key] is JsonValue v && v.TryGetValue<int>(out var i) ? i : def;
    public static bool Bool(this JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    /// <summary>Đọc một đối tượng; trả về mặc định nếu dữ liệu sai định dạng.</summary>
    public static T? As<T>(this JsonObject o, string key)
    {
        try { return o[key] is JsonNode n ? n.Deserialize<T>(ExamPackage.Json) : default; }
        catch (JsonException) { return default; }
    }
    public static T? As<T>(this JsonObject o)
    {
        try { return o.Deserialize<T>(ExamPackage.Json); }
        catch (JsonException) { return default; }
    }
}
