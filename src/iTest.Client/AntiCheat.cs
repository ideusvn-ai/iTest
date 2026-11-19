using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace ITest.Client;

/// <summary>
/// Phát hiện dấu hiệu gian lận trên máy thí sinh trong lúc làm bài:
///  • nhiều màn hình, phiên Remote Desktop, máy ảo;
///  • phần mềm điều khiển từ xa (TeamViewer, UltraViewer, AnyDesk, RustDesk…);
///  • trình duyệt, ứng dụng nhắn tin, trợ lý AI đang chạy (bị đóng khi bắt đầu thi và định kỳ);
///  • chặn quay/chụp màn hình cửa sổ bài thi và xoá bộ nhớ tạm (clipboard).
/// Mọi phát hiện được báo về máy chủ để giám thị xử lý — máy không tự huỷ bài.
/// </summary>
public static class AntiCheat
{
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    private const int SM_CMONITORS = 80, SM_REMOTESESSION = 0x1000;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11, WDA_MONITOR = 0x01;

    private static readonly string[] RemoteTools =
    {
        "teamviewer", "teamviewer_service", "tv_w32", "tv_x64", "ultraviewer_desktop", "ultraviewer_service", "ultraviewer",
        "anydesk", "rustdesk", "remoting_host", "tvnserver", "winvnc", "vncserver", "parsec", "screenconnect.clientservice",
        "supremo", "ammyy_admin", "aa_v3", "dwagent", "radmin", "rserver3"
    };

    private static readonly (string proc, string label)[] Forbidden =
    {
        ("chrome", "Google Chrome"), ("msedge", "Microsoft Edge"), ("firefox", "Firefox"), ("opera", "Opera"), ("brave", "Brave"),
        ("browser", "Cốc Cốc"), ("vivaldi", "Vivaldi"), ("iexplore", "Internet Explorer"),
        ("zalo", "Zalo"), ("messenger", "Messenger"), ("telegram", "Telegram"), ("discord", "Discord"), ("skype", "Skype"),
        ("whatsapp", "WhatsApp"), ("viber", "Viber"), ("ms-teams", "Microsoft Teams"), ("teams", "Microsoft Teams"),
        ("chatgpt", "ChatGPT"), ("claude", "Claude"), ("copilot", "Copilot"), ("notepad", "Notepad"), ("winword", "Word"),
        ("calculator", "Máy tính"), ("calculatorapp", "Máy tính"), ("snippingtool", "Snipping Tool"), ("screensketch", "Snip & Sketch"),
        ("obs64", "OBS (quay màn hình)"), ("obs32", "OBS (quay màn hình)"), ("sharex", "ShareX"), ("lightshot", "Lightshot")
    };

    public sealed record Finding(string Kind, string Detail);

    /// <summary>Không cho phần mềm quay/chụp màn hình ghi lại cửa sổ bài thi (Windows 10 2004 trở lên).</summary>
    public static void ProtectWindow(Window w)
    {
        try
        {
            var h = new WindowInteropHelper(w).Handle;
            if (h == IntPtr.Zero) return;
            if (!SetWindowDisplayAffinity(h, WDA_EXCLUDEFROMCAPTURE)) SetWindowDisplayAffinity(h, WDA_MONITOR);
        }
        catch { }
    }

    public static void ClearClipboard()
    {
        try { Clipboard.Clear(); } catch { }
    }

    /// <summary>Kiểm tra môi trường máy; chỉ gọi khi bắt đầu thi và định kỳ (vài giây một lần).</summary>
    public static List<Finding> Scan(bool lenient)
    {
        var list = new List<Finding>();
        try
        {
            int mon = GetSystemMetrics(SM_CMONITORS);
            if (mon > 1) list.Add(new("monitor", $"máy đang nối {mon} màn hình"));
            if (GetSystemMetrics(SM_REMOTESESSION) != 0) list.Add(new("remote", "bài đang được làm qua phiên Remote Desktop"));
        }
        catch { }

        Process[] procs;
        try { procs = Process.GetProcesses(); } catch { return list; }
        int session = Process.GetCurrentProcess().SessionId;
        var seenRemote = new HashSet<string>();
        foreach (var p in procs)
        {
            try
            {
                var name = p.ProcessName.ToLowerInvariant();
                if (RemoteTools.Contains(name) && seenRemote.Add(name))
                {
                    list.Add(new("remoteTool", $"phần mềm điều khiển từ xa đang chạy: {p.ProcessName}"));
                    TryKill(p);
                    continue;
                }
                if (p.SessionId != session) continue;
                foreach (var (proc, label) in Forbidden)
                {
                    if (name != proc) continue;
                    // Đề cho dùng tài liệu: không đóng máy tính, Notepad, Word.
                    if (lenient && label is "Máy tính" or "Notepad" or "Word") break;
                    if (TryKill(p)) list.Add(new("app", $"đã đóng {label}"));
                    break;
                }
            }
            catch { }
            finally { p.Dispose(); }
        }
        return list;
    }

    private static bool TryKill(Process p)
    {
        try { p.Kill(true); return true; } catch { return false; }
    }

    /// <summary>Nhận biết máy ảo qua thông tin BIOS.</summary>
    public static string? VirtualMachine()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            var s = $"{k?.GetValue("SystemManufacturer")} {k?.GetValue("SystemProductName")} {k?.GetValue("BIOSVendor")}".ToLowerInvariant();
            foreach (var tag in new[] { "vmware", "virtualbox", "qemu", "virtual machine", "xen", "parallels", "kvm", "bochs" })
                if (s.Contains(tag)) return s.Trim();
        }
        catch { }
        return null;
    }
}
