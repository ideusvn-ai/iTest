using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace ITest.Core.Tests;

public class ConnectionTests
{
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
    public async Task Encrypted_roundtrip_both_directions()
    {
        var (server, client, l) = await Pair();
        try
        {
            var gotOnServer = new TaskCompletionSource<JsonObject>();
            var gotOnClient = new TaskCompletionSource<JsonObject>();
            server.Received += (_, m) => gotOnServer.TrySetResult(m);
            client.Received += (_, m) => gotOnClient.TrySetResult(m);
            server.Start(); client.Start();

            await client.SendAsync(Msg.Answer, new { no = 7, choice = 2 });
            await server.SendAsync(Msg.Notice, new { kind = "all", text = "Còn 15 phút – giữ trật tự!" });

            var a = await gotOnServer.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var b = await gotOnClient.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(Msg.Answer, a.Str("t"));
            Assert.Equal(7, a.Int("no"));
            Assert.Equal(2, a.Int("choice"));
            Assert.Equal("Còn 15 phút – giữ trật tự!", b.Str("text"));
        }
        finally { server.Dispose(); client.Dispose(); l.Stop(); }
    }

    [Fact]
    public async Task Many_messages_arrive_in_order()
    {
        var (server, client, l) = await Pair();
        try
        {
            var got = new List<int>();
            var done = new TaskCompletionSource();
            server.Received += (_, m) => { lock (got) { got.Add(m.Int("no")); if (got.Count == 500) done.TrySetResult(); } };
            server.Start(); client.Start();
            for (int i = 1; i <= 500; i++) client.Send(Msg.Answer, new { no = i, choice = i % 4 });
            await done.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(Enumerable.Range(1, 500), got);
        }
        finally { server.Dispose(); client.Dispose(); l.Stop(); }
    }

    [Fact]
    public async Task Large_payload_is_delivered()
    {
        var (server, client, l) = await Pair();
        try
        {
            var got = new TaskCompletionSource<JsonObject>();
            client.Received += (_, m) => got.TrySetResult(m);
            server.Start(); client.Start();
            var blob = Convert.ToBase64String(Enumerable.Range(0, 3_000_000).Select(i => (byte)i).ToArray());
            await server.SendAsync(Msg.Exam, new { image = blob });
            var m = await got.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(blob, m.Str("image"));
        }
        finally { server.Dispose(); client.Dispose(); l.Stop(); }
    }

    [Fact]
    public async Task Matching_room_pin_communicates()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        var accept = l.AcceptTcpClientAsync();
        var client = await Connection.ConnectAsync("127.0.0.1", port, "1234");
        var server = new Connection(await accept, isServer: true, "1234");
        try
        {
            var got = new TaskCompletionSource<JsonObject>();
            server.Received += (_, m) => got.TrySetResult(m);
            server.Start(); client.Start();
            await client.SendAsync(Msg.Hello, new { machine = "PC1" });
            var m = await got.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("PC1", m.Str("machine"));
        }
        finally { server.Dispose(); client.Dispose(); l.Stop(); }
    }

    [Fact]
    public async Task Wrong_room_pin_cannot_communicate()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        var accept = l.AcceptTcpClientAsync();
        var client = await Connection.ConnectAsync("127.0.0.1", port, "1234");
        var server = new Connection(await accept, isServer: true, "9999");
        try
        {
            var got = new TaskCompletionSource<JsonObject>();
            server.Received += (_, m) => got.TrySetResult(m);
            server.Start(); client.Start();
            await client.SendAsync(Msg.Hello, new { machine = "PC1" });
            // Mã phòng khác nhau → khoá phiên khác nhau → máy chủ không giải mã được, không nhận được tin.
            await Task.WhenAny(got.Task, Task.Delay(3000));
            Assert.False(got.Task.IsCompleted);
        }
        finally { server.Dispose(); client.Dispose(); l.Stop(); }
    }

    [Fact]
    public async Task Plain_text_client_is_dropped()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        var accept = l.AcceptTcpClientAsync();
        using var raw = new TcpClient();
        await raw.ConnectAsync(IPAddress.Loopback, port);
        var server = new Connection(await accept, isServer: true);
        var closed = new TaskCompletionSource();
        var received = false;
        server.Received += (_, _) => received = true;
        server.Closed += _ => closed.TrySetResult();
        server.Start();
        var json = System.Text.Encoding.UTF8.GetBytes("{\"t\":\"hello\"}");
        var s = raw.GetStream();
        await s.WriteAsync(BitConverter.GetBytes(json.Length));
        await s.WriteAsync(json);
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(received);
        l.Stop();
    }
}
