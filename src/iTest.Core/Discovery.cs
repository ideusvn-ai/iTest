using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace ITest.Core;

/// <summary>
/// Máy chủ phát gói quảng bá phòng thi mỗi 2 giây, CHỈ qua card mạng LAN chính
/// (bỏ qua card ảo VMware/VirtualBox/Hyper-V…), nên thí sinh chỉ thấy đúng một địa chỉ IP của máy chủ.
/// </summary>
public sealed class BeaconSender : IDisposable
{
    private readonly Func<RoomBeacon> _factory;
    private readonly CancellationTokenSource _cts = new();
    public string ServerId { get; } = Guid.NewGuid().ToString("N");

    public BeaconSender(Func<RoomBeacon> factory) => _factory = factory;

    public void Start() => _ = Task.Run(async () =>
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var nic = NetUtil.Primary();
                var beacon = _factory();
                beacon.ServerId = ServerId;
                beacon.Address = nic.Address.ToString();
                var bytes = JsonSerializer.SerializeToUtf8Bytes(beacon, ExamPackage.Json);
                using var udp = new UdpClient(new IPEndPoint(nic.Address, 0)) { EnableBroadcast = true };
                await udp.SendAsync(bytes, bytes.Length, new IPEndPoint(nic.Broadcast, Ports.Discovery));
                await udp.SendAsync(bytes, bytes.Length, new IPEndPoint(IPAddress.Broadcast, Ports.Discovery));
            }
            catch { }
            try { await Task.Delay(2000, _cts.Token); } catch { }
        }
    });

    public void Dispose() => _cts.Cancel();
}

/// <summary>Máy thí sinh lắng nghe gói quảng bá, chỉ nhận phòng cùng workgroup.</summary>
public sealed class BeaconListener : IDisposable
{
    private UdpClient? _udp;
    private readonly CancellationTokenSource _cts = new();
    public event Action<RoomBeacon>? Found;
    /// <summary>Một máy khác báo: nó vừa mở iTest-Server và thấy phòng thi của máy này (ServerId = phòng được báo).</summary>
    public event Action<RoomBeacon>? DuplicateReported;
    public string Workgroup { get; } = NetUtil.GetWorkgroup();

    public void Start()
    {
        _udp = new UdpClient();
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, Ports.Discovery));
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    var r = await _udp.ReceiveAsync(_cts.Token);
                    var b = JsonSerializer.Deserialize<RoomBeacon>(r.Buffer, ExamPackage.Json);
                    if (b == null) continue;
                    if (b.App == NetUtil.DupApp)
                    {
                        if (string.IsNullOrEmpty(b.Address)) b.Address = r.RemoteEndPoint.Address.ToString();
                        DuplicateReported?.Invoke(b);
                        continue;
                    }
                    if (b.App != "iTest") continue;
                    if (!string.Equals(b.Workgroup, Workgroup, StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.IsNullOrEmpty(b.Address)) b.Address = r.RemoteEndPoint.Address.ToString();
                    if (string.IsNullOrEmpty(b.ServerId)) b.ServerId = b.Address;
                    Found?.Invoke(b);
                }
                catch (OperationCanceledException) { break; }
                catch { }
            }
        });
    }

    public void Dispose() { _cts.Cancel(); _udp?.Dispose(); }
}

public static class NetUtil
{
    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetGetJoinInformation(string? server, out IntPtr name, out int status);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);

    /// <summary>Tên workgroup (hoặc domain) mà máy tính đang tham gia.</summary>
    public static string GetWorkgroup()
    {
        try
        {
            if (OperatingSystem.IsWindows() && NetGetJoinInformation(null, out var ptr, out _) == 0)
            {
                var name = Marshal.PtrToStringUni(ptr) ?? "WORKGROUP";
                NetApiBufferFree(ptr);
                return name;
            }
        }
        catch { }
        return "WORKGROUP";
    }

    public sealed record Nic(IPAddress Address, IPAddress Broadcast, string Name);

    private static readonly string[] VirtualWords = { "virtual", "vmware", "virtualbox", "hyper-v", "vethernet", "loopback", "pseudo", "tap-", "tunnel", "bluetooth", "wsl", "docker", "zerotier", "hamachi", "vpn" };

    /// <summary>Card mạng LAN chính: đang hoạt động, có IPv4, ưu tiên card có gateway và không phải card ảo.</summary>
    public static Nic Primary()
    {
        Nic? best = null; int bestScore = int.MinValue;
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            var props = ni.GetIPProperties();
            foreach (var ua in props.UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null) continue;
                var ipb = ua.Address.GetAddressBytes();
                if (ipb[0] == 169 && ipb[1] == 254) continue; // APIPA
                int score = 0;
                if (props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any))) score += 100;
                var desc = (ni.Description + " " + ni.Name).ToLowerInvariant();
                if (VirtualWords.Any(desc.Contains)) score -= 200;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet) score += 20;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) score += 10;
                if (ipb[0] == 10 || ipb[0] == 192 && ipb[1] == 168 || ipb[0] == 172 && ipb[1] >= 16 && ipb[1] <= 31) score += 5;
                if (score > bestScore)
                {
                    var mask = ua.IPv4Mask.GetAddressBytes();
                    var bc = new byte[4];
                    for (int i = 0; i < 4; i++) bc[i] = (byte)(ipb[i] | ~mask[i]);
                    best = new Nic(ua.Address, new IPAddress(bc), ni.Name);
                    bestScore = score;
                }
            }
        }
        return best ?? new Nic(IPAddress.Loopback, IPAddress.Broadcast, "loopback");
    }

    public static string LocalIPv4() => Primary().Address.ToString();

    public const string DupApp = "iTest-Server-Dup";

    /// <summary>
    /// Báo cho máy chủ đang mở phòng (theo <paramref name="targetServerId"/>) biết máy này vừa mở iTest-Server.
    /// Gửi bằng UDP vì kênh TCP chỉ dành cho máy biết mã phòng thi.
    /// </summary>
    public static void SendDuplicateNotice(string targetAddress, string targetServerId)
    {
        try
        {
            var nic = Primary();
            var note = new RoomBeacon { App = DupApp, ServerId = targetServerId, Host = Environment.MachineName, Address = nic.Address.ToString(), Workgroup = GetWorkgroup() };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(note, ExamPackage.Json);
            using var udp = new UdpClient(new IPEndPoint(nic.Address, 0)) { EnableBroadcast = true };
            if (IPAddress.TryParse(targetAddress, out var ip)) udp.Send(bytes, bytes.Length, new IPEndPoint(ip, Ports.Discovery));
            udp.Send(bytes, bytes.Length, new IPEndPoint(nic.Broadcast, Ports.Discovery));
        }
        catch { }
    }
}
