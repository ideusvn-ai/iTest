using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace ITest.Core;

/// <summary>Máy chủ phát gói quảng bá phòng thi mỗi 2 giây.</summary>
public sealed class BeaconSender : IDisposable
{
    private readonly UdpClient _udp = new() { EnableBroadcast = true };
    private readonly Func<RoomBeacon> _factory;
    private readonly CancellationTokenSource _cts = new();

    public BeaconSender(Func<RoomBeacon> factory) => _factory = factory;

    public void Start() => _ = Task.Run(async () =>
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(_factory(), ExamPackage.Json);
                await _udp.SendAsync(bytes, bytes.Length, new IPEndPoint(IPAddress.Broadcast, Ports.Discovery));
                foreach (var b in NetUtil.BroadcastAddresses())
                    await _udp.SendAsync(bytes, bytes.Length, new IPEndPoint(b, Ports.Discovery));
            }
            catch { }
            try { await Task.Delay(2000, _cts.Token); } catch { }
        }
    });

    public void Dispose() { _cts.Cancel(); _udp.Dispose(); }
}

/// <summary>Máy thí sinh lắng nghe gói quảng bá, chỉ nhận phòng cùng workgroup.</summary>
public sealed class BeaconListener : IDisposable
{
    private UdpClient? _udp;
    private readonly CancellationTokenSource _cts = new();
    public event Action<RoomBeacon>? Found;
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
                    if (b == null || b.App != "iTest") continue;
                    if (!string.Equals(b.Workgroup, Workgroup, StringComparison.OrdinalIgnoreCase)) continue;
                    b.Address = r.RemoteEndPoint.Address.ToString();
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

    public static string LocalIPv4()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork) return ua.Address.ToString();
        }
        return "127.0.0.1";
    }

    public static IEnumerable<IPAddress> BroadcastAddresses()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null) continue;
                var ip = ua.Address.GetAddressBytes();
                var mask = ua.IPv4Mask.GetAddressBytes();
                var bc = new byte[4];
                for (int i = 0; i < 4; i++) bc[i] = (byte)(ip[i] | ~mask[i]);
                yield return new IPAddress(bc);
            }
        }
    }
}
