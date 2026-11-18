using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

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
    public const string Ping = "ping";
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

/// <summary>Gói tin quảng bá phòng thi qua UDP.</summary>
public sealed class RoomBeacon
{
    public string App { get; set; } = "iTest";
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

/// <summary>Kết nối TCP với khung tin: 4 byte độ dài (little-endian) + JSON UTF-8.</summary>
public sealed class Connection : IDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    public string RemoteAddress { get; }
    public event Action<Connection, JsonObject>? Received;
    public event Action<Connection>? Closed;
    private int _closed;

    public Connection(TcpClient client)
    {
        _client = client;
        _client.NoDelay = true;
        _stream = client.GetStream();
        RemoteAddress = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "?";
    }

    public static async Task<Connection> ConnectAsync(string host, int port, CancellationToken ct = default)
    {
        var c = new TcpClient();
        await c.ConnectAsync(host, port, ct);
        return new Connection(c);
    }

    public void Start() => _ = Task.Run(ReadLoop);

    private async Task ReadLoop()
    {
        var lenBuf = new byte[4];
        try
        {
            while (true)
            {
                await ReadExact(lenBuf, 4);
                int len = BitConverter.ToInt32(lenBuf, 0);
                if (len <= 0 || len > 64 * 1024 * 1024) throw new InvalidDataException("Khung tin không hợp lệ");
                var buf = new byte[len];
                await ReadExact(buf, len);
                var node = JsonNode.Parse(buf) as JsonObject;
                if (node != null) Received?.Invoke(this, node);
            }
        }
        catch { /* mất kết nối */ }
        finally { Close(); }
    }

    private async Task ReadExact(byte[] buf, int count)
    {
        int read = 0;
        while (read < count)
        {
            int n = await _stream.ReadAsync(buf.AsMemory(read, count - read));
            if (n == 0) throw new EndOfStreamException();
            read += n;
        }
    }

    public async Task SendAsync(string type, object? payload = null)
    {
        var obj = payload == null ? new JsonObject() : (JsonSerializer.SerializeToNode(payload, ExamPackage.Json) as JsonObject ?? new JsonObject());
        obj["t"] = type;
        var bytes = Encoding.UTF8.GetBytes(obj.ToJsonString());
        await _sendLock.WaitAsync();
        try
        {
            await _stream.WriteAsync(BitConverter.GetBytes(bytes.Length));
            await _stream.WriteAsync(bytes);
            await _stream.FlushAsync();
        }
        catch { Close(); }
        finally { _sendLock.Release(); }
    }

    public void Send(string type, object? payload = null) => _ = SendAsync(type, payload);

    public void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1) return;
        try { _client.Close(); } catch { }
        Closed?.Invoke(this);
    }

    public void Dispose() => Close();
}

public static class JsonExt
{
    public static string Str(this JsonObject o, string key) => o[key]?.GetValue<string>() ?? "";
    public static int Int(this JsonObject o, string key, int def = 0) => o[key] is JsonValue v && v.TryGetValue<int>(out var i) ? i : def;
    public static bool Bool(this JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    public static T? As<T>(this JsonObject o, string key) => o[key] is JsonNode n ? n.Deserialize<T>(ExamPackage.Json) : default;
    public static T? As<T>(this JsonObject o) => o.Deserialize<T>(ExamPackage.Json);
}
