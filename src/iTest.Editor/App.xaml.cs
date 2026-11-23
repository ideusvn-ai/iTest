using System.Windows;

namespace ITest.Editor;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Lỗi bất ngờ không làm mất bộ đề đang soạn: ghi nhật ký, báo một lần và chạy tiếp.
        ITest.UI.CrashGuard.Install(this, "iTest-Editor", notify: true);
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
        ITest.UI.SmoothScroll.Enable();
        base.OnStartup(e);
    }
}
