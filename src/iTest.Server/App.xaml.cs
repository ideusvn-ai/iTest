using System.Windows;

namespace ITest.Server;

public partial class App : Application
{
    private static Mutex? _single;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Mỗi máy chỉ chạy một iTest-Server.
        _single = new Mutex(true, @"Global\iTest-Server-single", out bool first);
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
