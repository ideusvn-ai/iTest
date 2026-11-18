using System.Windows;

namespace ITest.Server;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
        base.OnStartup(e);
    }
}
