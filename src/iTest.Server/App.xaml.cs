using System.Windows;

namespace ITest.Server;

public partial class App : Application
{
    private static Mutex? _single;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Lỗi bất ngờ không được làm sập máy chủ giữa buổi thi: ghi nhật ký và chạy tiếp.
        ITest.UI.CrashGuard.Install(this, "iTest-Server", notify: true);
        // Mỗi máy chỉ chạy một iTest-Server.
        bool first = true;
        try { _single = new Mutex(true, @"Global\iTest-Server-single", out first); }
        catch { /* không tạo được mutex toàn cục (quyền hạn): bỏ qua kiểm tra */ }
        if (!first)
        {
            MessageBox.Show("iTest-Server đang chạy trên máy này.", "iTest-Server", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        ITest.UI.SmoothScroll.Enable();
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
        base.OnStartup(e);
        MainWindow = new MainWindow();
        MainWindow.Show();
    }
}
