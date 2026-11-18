using System.Windows;

namespace ITest.Client;

public partial class App : Application
{
    protected override void OnExit(ExitEventArgs e)
    {
        KioskGuard.Leave();
        base.OnExit(e);
    }
}
