using System.Windows;

namespace ITest.Client;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Lần chạy trước bị tắt đột ngột (mất điện, treo máy) lúc đang khoá máy: mở lại Task Manager ngay khi khởi động.
        KioskGuard.RecoverFromCrash();
        // Lỗi bất ngờ khi đang thi: ghi nhật ký và chạy tiếp, không đóng sập bài làm của thí sinh.
        // Nếu không cứu được thì ít nhất phải gỡ khoá máy trước khi thoát.
        ITest.UI.CrashGuard.Install(this, "iTest", notify: false, onFatal: KioskGuard.Leave);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => KioskGuard.Leave();
        ITest.UI.SmoothScroll.Enable();
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        KioskGuard.Leave();
        base.OnExit(e);
    }
}
