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
    public const string Submit = "submit";
    public const string Focus = "focus";          // lost: bool, reason
    public const string AnswerEx = "answerEx";    // AnswerEx: câu nhiều đáp án / Đúng-Sai / trả lời ngắn
    public const string Cheat = "cheat";
    public const string DupServer = "dupServer";  // machine, ip: một máy khác vừa mở iTest-Server trong cùng mạng          // kind, detail: dấu hiệu gian lận phát hiện trên máy thí sinh
    public const string Ping = "ping";            // nhịp tim mỗi 3 giây
    public const string Pong = "pong";            // S → C: trả lời nhịp tim
    // S → C
    public const string Welcome = "welcome";      // room, proctor, department, school
    public const string Registered = "registered";
    public const string Reject = "reject";        // reason
    public const string EditRequest = "editRequest"; // reason
    public const string Exam = "exam";            // ClientExam, secondsLeft, submitWindow
    public const string Resume = "resume";        // choices, essays
    public const string Time = "time";            // secondsLeft, paused
    public const string Notice = "notice";        // kind (all|me), text
    public const string Collect = "collect";      // máy chủ thu bài
    public const string Result = "result";        // right, wrong, blank, score, showScore
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
///  2. Khoá phiên AES-256 = SHA-256(bí mật chung ECDH ‖ "iTest-session-v1").
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
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private AesGcm? _aes;
    private long _sendCounter;
    private long _recvCounter;
    public string RemoteAddress { get; }
    public event Action<Connection, JsonObject>? Received;
    public event Action<Connection>? Closed;
    private int _closed;

    public Connection(TcpClient client, bool isServer)
    {
        _client = client;
        _client.NoDelay = true;
        _isServer = isServer;
        _stream = client.GetStream();
        RemoteAddress = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "?";
    }

    public static async Task<Connection> ConnectAsync(string host, int port, CancellationToken ct = default)
    {
        var c = new TcpClient();
        await c.ConnectAsync(host, port, ct).ConfigureAwait(false);
        return new Connection(c, isServer: false);
    }

    public void Start() => _ = Task.Run(RunAsync);

    private async Task RunAsync()
    {
        try
        {
            await HandshakeAsync().ConfigureAwait(false);
            _ready.TrySetResult(true);
            _ = Task.Run(WriteLoop);
            var lenBuf = new byte[4];
            while (true)
            {
                await ReadExact(lenBuf, 4).ConfigureAwait(false);
                int len = BitConverter.ToInt32(lenBuf, 0);
                if (len < 28 || len > 64 * 1024 * 1024) throw new InvalidDataException("Khung tin không hợp lệ");
                var buf = new byte[len];
                await ReadExact(buf, len).ConfigureAwait(false);
                if (JsonNode.Parse(Open(buf)) is JsonObject node) Received?.Invoke(this, node);
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
        var key = ecdh.DeriveKeyFromHash(other.PublicKey, HashAlgorithmName.SHA256, null, "iTest-session-v1"u8.ToArray());
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
        if (!_outbox.Writer.TryWrite((Encoding.UTF8.GetBytes(obj.ToJsonString()), done))) done.TrySetResult();
        return done.Task;
    }

    private async Task WriteLoop()
    {
        try
        {
            await foreach (var (plain, done) in _outbox.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                var frame = new byte[4 + 28 + plain.Length];
                BitConverter.GetBytes(28 + plain.Length).CopyTo(frame, 0);
                Seal(frame, plain);
                await _stream.WriteAsync(frame).ConfigureAwait(false);
                if (_outbox.Reader.Count == 0) await _stream.FlushAsync().ConfigureAwait(false);
                done.TrySetResult();
            }
        }
        catch { Close(); }
        finally
        {
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
    public static string Str(this JsonObject o, string key) => o[key]?.GetValue<string>() ?? "";
    public static int Int(this JsonObject o, string key, int def = 0) => o[key] is JsonValue v && v.TryGetValue<int>(out var i) ? i : def;
    public static bool Bool(this JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    public static T? As<T>(this JsonObject o, string key) => o[key] is JsonNode n ? n.Deserialize<T>(ExamPackage.Json) : default;
    public static T? As<T>(this JsonObject o) => o.Deserialize<T>(ExamPackage.Json);
}
