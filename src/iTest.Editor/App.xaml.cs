using System.Windows;

namespace ITest.Editor;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Bộ cài đặt gọi "--check-cert <file>" để kiểm tra chứng chỉ người dùng chọn: mã thoát 0 = dùng được, 2 = không hợp lệ, 3 = hết hạn.
        if (e.Args.Length >= 2 && e.Args[0] == "--check-cert") { Environment.Exit(ITest.Core.CertificateStore.Check(e.Args[1])); return; }
        // Lỗi bất ngờ không làm mất bộ đề đang soạn: ghi nhật ký, báo một lần và chạy tiếp.
        ITest.UI.CrashGuard.Install(this, "iTest-Editor", notify: true);
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
        ITest.UI.SmoothScroll.Enable();
        base.OnStartup(e);
    }
}
