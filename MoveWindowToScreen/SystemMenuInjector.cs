using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace MoveWindowToScreen;

/// <summary>
/// Detects when a system menu appears (Shift+right-click on taskbar) and shows
/// a companion "Move to screen" popup adjacent to it.
/// </summary>
internal sealed class SystemMenuInjector : IDisposable
{
    private const string MenuClassName = "#32768";

    private readonly NativeMethods.WinEventDelegate _winEventProc;
    private IntPtr _winEventHook;
    private IntPtr _winEventEndHook;
    private readonly Dispatcher _dispatcher;
    private Window? _companionPopup;
    private bool _isClosingPopup;
    private IntPtr _menuTargetWindow;

    public SystemMenuInjector()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _winEventProc = OnWinEvent;

        _winEventHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_MENUSTART,
            NativeMethods.EVENT_SYSTEM_MENUPOPUPEND,
            IntPtr.Zero, _winEventProc, 0, 0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);
    }

    private void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        // System menu popup appeared — idObject -4 = OBJID_SYSMENU
        if (eventType == NativeMethods.EVENT_SYSTEM_MENUPOPUPSTART
            && idObject == -4
            && IsPopupMenuWindow(hwnd))
        {
            _menuTargetWindow = NativeMethods.GetForegroundWindow();
            _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                ShowCompanionPopup(hwnd);
            });
        }

        // System menu closed — always close companion popup.
        // Button clicks use MouseLeftButtonDown which fires synchronously before this,
        // so the popup will already be closed by the click handler.
        if (eventType == 0x0005 /* EVENT_SYSTEM_MENUEND */)
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                CloseCompanionPopup();
            });
        }
    }

    private void ShowCompanionPopup(IntPtr menuHwnd)
    {
        try
        {
            ShowCompanionPopupCore(menuHwnd);
        }
        catch { }
    }

    private void ShowCompanionPopupCore(IntPtr menuHwnd)
    {
        CloseCompanionPopup();

        var monitors = MonitorHelper.GetAllMonitors();
        if (monitors.Count < 2) return;

        // Get the menu window's position
        if (!NativeMethods.GetWindowRect(menuHwnd, out var menuRect))
            return;

        var targetWnd = _menuTargetWindow;
        if (targetWnd == IntPtr.Zero) return;

        // Detect theme
        bool isDark = IsDarkTheme();

        var popup = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            Topmost = true,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
        };

        var border = new Border
        {
            Background = new SolidColorBrush(isDark ? Color.FromRgb(44, 44, 44) : Color.FromRgb(249, 249, 249)),
            BorderBrush = new SolidColorBrush(isDark ? Color.FromRgb(60, 60, 60) : Color.FromRgb(200, 200, 200)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(4),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 12,
                ShadowDepth = 2,
                Opacity = 0.3,
                Color = Colors.Black
            }
        };

        var stack = new StackPanel();
        var fg = isDark ? Brushes.White : Brushes.Black;
        var hoverBg = new SolidColorBrush(isDark ? Color.FromRgb(60, 60, 60) : Color.FromRgb(230, 230, 230));

        // Header
        var header = new TextBlock
        {
            Text = "Move to screen",
            FontWeight = FontWeights.SemiBold,
            Foreground = fg,
            Margin = new Thickness(8, 4, 8, 2),
            FontSize = 12
        };
        stack.Children.Add(header);

        // "Current screen" button
        AddMenuButton(stack, "Current screen", fg, hoverBg, () =>
        {
            NativeMethods.GetCursorPos(out var cursorPt);
            var mon = NativeMethods.MonitorFromPoint(cursorPt, NativeMethods.MONITOR_DEFAULTTONEAREST);
            WindowMover.MoveWindowToMonitor(targetWnd, mon);
            CloseCompanionPopup();
        });

        // Separator
        stack.Children.Add(new Border
        {
            Height = 1,
            Background = new SolidColorBrush(isDark ? Color.FromRgb(60, 60, 60) : Color.FromRgb(220, 220, 220)),
            Margin = new Thickness(8, 2, 8, 2)
        });

        // Per-monitor buttons
        foreach (var m in monitors)
        {
            var monHandle = m.Handle;
            AddMenuButton(stack, m.Name, fg, hoverBg, () =>
            {
                WindowMover.MoveWindowToMonitor(targetWnd, monHandle);
                CloseCompanionPopup();
            });
        }

        border.Child = stack;
        popup.Content = border;

        // Position to the right of the system menu
        popup.Left = menuRect.Right + 2;
        popup.Top = menuRect.Top;

        popup.Deactivated += (_, _) => _dispatcher.BeginInvoke(() => CloseCompanionPopup());
        _companionPopup = popup;
        popup.Show();
    }

    private static void AddMenuButton(StackPanel parent, string text, System.Windows.Media.Brush fg, System.Windows.Media.Brush hoverBg, Action onClick)
    {
        var btn = new Border
        {
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 5, 16, 5),
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = new TextBlock
            {
                Text = text,
                Foreground = fg,
                FontSize = 12
            }
        };

        btn.MouseEnter += (_, _) => btn.Background = hoverBg;
        btn.MouseLeave += (_, _) => btn.Background = Brushes.Transparent;
        btn.MouseLeftButtonDown += (_, e) => { e.Handled = true; onClick(); };

        parent.Children.Add(btn);
    }

    private void CloseCompanionPopup()
    {
        if (_isClosingPopup) return;
        _isClosingPopup = true;
        try
        {
            var p = _companionPopup;
            _companionPopup = null;
            p?.Close();
        }
        catch { }
        finally
        {
            _isClosingPopup = false;
        }
    }

    private static bool IsPopupMenuWindow(IntPtr hwnd)
    {
        var className = new char[16];
        int len = NativeMethods.GetClassName(hwnd, className, className.Length);
        if (len <= 0) return false;
        return new string(className, 0, len) == MenuClassName;
    }

    private static bool IsDarkTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var val = key?.GetValue("AppsUseLightTheme");
            return val is int i && i == 0;
        }
        catch { return false; }
    }

    public void Dispose()
    {
        CloseCompanionPopup();

        if (_winEventHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_winEventHook);
            _winEventHook = IntPtr.Zero;
        }

        if (_winEventEndHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_winEventEndHook);
            _winEventEndHook = IntPtr.Zero;
        }
    }
}
