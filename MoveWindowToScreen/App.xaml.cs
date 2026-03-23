using System.Windows;

namespace MoveWindowToScreen;

public partial class App : System.Windows.Application
{
    private TrayIconManager? _trayIconManager;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _trayIconManager = new TrayIconManager(this);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIconManager?.Dispose();
        base.OnExit(e);
    }
}
