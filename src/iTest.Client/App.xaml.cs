using System.Windows;

namespace ITest.Client;

public partial class App : Application
{
    private static System.Threading.Mutex? _single;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Mỗi máy chỉ chạy một phiên iTest: phiên thứ hai sẽ làm yếu chế độ khoá máy của phiên đang thi
        // và hai phiên cùng ghi một file lưu tạm. Kiểm tra trước mọi việc khác (kể cả việc gỡ khoá sau sự cố).
        _single = new System.Threading.Mutex(true, @"Local\iTest-Client-SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            MessageBox.Show("iTest đang chạy trên máy này. Hãy quay lại cửa sổ iTest đang mở.", "iTest", MessageBoxButton.OK, MessageBoxImage.Information);
            Environment.Exit(0);
            return;
        }
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
