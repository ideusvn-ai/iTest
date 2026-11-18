using System.Windows;

namespace ITest.Editor;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        ITest.UI.SmoothScroll.Enable();
        base.OnStartup(e);
    }
}
