using System.Diagnostics;
using System.Threading;
using System.Windows;

namespace MoveWindowToScreen;

public partial class App : System.Windows.Application
{
    private TrayIconManager? _trayIconManager;
    private Mutex? _instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _instance = new Mutex(initiallyOwned: false, @"Local\MoveWindowToScreen.1", out bool firstInstance);
        if (!firstInstance)
        {
            Shutdown();
            return;
        }

        try
        {
            SignInStartup.Initialize();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Could not initialize sign-in startup: {ex}");
        }

        _trayIconManager = new TrayIconManager(this);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIconManager?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
