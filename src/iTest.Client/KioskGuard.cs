using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ITest.Client;

/// <summary>
/// Chế độ thi: chặn phím Windows, Alt+Tab, Alt+Esc, Ctrl+Esc, Alt+F4 bằng hook bàn phím cấp thấp
/// và tạm tắt Task Manager (chính sách của người dùng hiện tại) trong lúc thi.
/// Ctrl+Alt+Del không thể chặn bằng phần mềm thường; khi cửa sổ mất tiêu điểm, ứng dụng báo về máy chủ.
/// </summary>
public static class KioskGuard
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104;
    private const int VK_TAB = 0x09, VK_ESCAPE = 0x1B, VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_F4 = 0x73, VK_CONTROL = 0x11, VK_D = 0x44;
    private const int LLKHF_ALTDOWN = 0x20;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public int vkCode; public int scanCode; public int flags; public int time; public IntPtr dwExtraInfo; }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private static IntPtr _hook = IntPtr.Zero;
    private static LowLevelKeyboardProc? _proc;
    public static bool Active => _hook != IntPtr.Zero;
    public static event Action<string>? Blocked;

    private const string PolicyKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\System";
    // Dấu "iTest là bên đã tắt Task Manager": chỉ khi có dấu này iTest mới bật lại, để không gỡ nhầm chính sách do
    // quản trị phòng máy đặt sẵn, và để lần chạy sau dọn được nếu lần này bị tắt đột ngột.
    private const string MarkerKey = @"Software\iTest";
    private const string MarkerName = "TaskMgrLockedByITest";

    public static void Enter()
    {
        if (Active) return;
        _proc = HookCallback;
        using var cur = Process.GetCurrentProcess();
        using var mod = cur.MainModule!;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(mod.ModuleName), 0);
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(PolicyKey);
            bool alreadyOff = k?.GetValue("DisableTaskMgr") is int v && v == 1;
            if (k != null && !alreadyOff)
            {
                using var mk = Registry.CurrentUser.CreateSubKey(MarkerKey);
                mk?.SetValue(MarkerName, 1, RegistryValueKind.DWord);
                k.SetValue("DisableTaskMgr", 1, RegistryValueKind.DWord);
            }
        }
        catch { }
    }

    public static void Leave()
    {
        if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
        RestoreTaskManager();
    }

    /// <summary>Gọi khi khởi động: nếu lần chạy trước kết thúc đột ngột lúc đang khoá, bật lại Task Manager.</summary>
    public static void RecoverFromCrash() => RestoreTaskManager();

    private static void RestoreTaskManager()
    {
        try
        {
            using var mk = Registry.CurrentUser.OpenSubKey(MarkerKey, writable: true);
            if (mk?.GetValue(MarkerName) == null) return;   // không phải iTest tắt: không đụng vào
            using var k = Registry.CurrentUser.OpenSubKey(PolicyKey, writable: true);
            k?.DeleteValue("DisableTaskMgr", throwOnMissingValue: false);
            mk.DeleteValue(MarkerName, throwOnMissingValue: false);
        }
        catch { }
    }

    private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
        {
            var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            bool alt = (kb.flags & LLKHF_ALTDOWN) != 0;
            bool ctrl = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
            string? what = null;
            if (kb.vkCode == VK_LWIN || kb.vkCode == VK_RWIN) what = "phím Windows";
            else if (alt && kb.vkCode == VK_TAB) what = "Alt+Tab";
            else if (alt && kb.vkCode == VK_ESCAPE) what = "Alt+Esc";
            else if (ctrl && kb.vkCode == VK_ESCAPE) what = "Ctrl+Esc";
            else if (alt && kb.vkCode == VK_F4) what = "Alt+F4";
            else if (ctrl && alt && kb.vkCode == VK_D) what = "Ctrl+Alt+D";
            if (what != null)
            {
                Blocked?.Invoke(what);
                return (IntPtr)1;
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }
}
