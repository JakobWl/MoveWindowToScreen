using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace MoveWindowToScreen;

/// <summary>
/// Detects when a system menu appears (Shift+right-click on taskbar) and shows
/// a companion "Move to screen" popup adjacent to it.
///
/// Notes:
/// - The popup must NOT steal activation, and while a system menu is tracking it
///   holds mouse capture, so the popup never receives hover/click messages.
///   A low-level mouse hook hit-tests the popup and swallows clicks inside it.
/// </summary>
internal sealed class SystemMenuInjector : IDisposable
{
    private const string MenuClassName = "#32768";

    private readonly NativeMethods.WinEventDelegate _winEventProc;
    private readonly NativeMethods.LowLevelMouseProc _mouseProc;
    private IntPtr _winEventHook;
    private IntPtr _mouseHook;
    private readonly Dispatcher _dispatcher;
    private Window? _companionPopup;
    private bool _isClosingPopup;
    private IntPtr _menuTargetWindow;
    private IntPtr _menuHwnd;
    private long _lastSysMenuPopupAt;
    private long _lastFallbackTriggerAt;

    // Popup hit-test data in physical screen pixels (valid while popup is shown)
    private NativeMethods.RECT _popupRect;
    private readonly List<PopupButton> _popupButtons = [];
    private PopupButton? _hoveredButton;
    private DispatcherTimer? _menuWatchTimer;

    private sealed class PopupButton
    {
        public required Border Element { get; init; }
        public required Action Action { get; init; }
        public required System.Windows.Media.Brush HoverBrush { get; init; }
        public NativeMethods.RECT ScreenRect;
    }

    public SystemMenuInjector()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _winEventProc = OnWinEvent;
        _mouseProc = OnMouseHook;

        _winEventHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_MENUSTART,
            NativeMethods.EVENT_SYSTEM_MENUPOPUPEND,
            IntPtr.Zero, _winEventProc, 0, 0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);

        _mouseHook = NativeMethods.SetMouseHookEx(
            NativeMethods.WH_MOUSE_LL, _mouseProc, IntPtr.Zero, 0);
    }

    private void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        // System menu popup appeared — idObject -4 = OBJID_SYSMENU
        if (eventType == NativeMethods.EVENT_SYSTEM_MENUPOPUPSTART
            && idObject == -4
            && IsPopupMenuWindow(hwnd))
        {
            // Resolve the window that owns this system menu. GetForegroundWindow()
            // is unreliable here: when the menu is opened from the taskbar (e.g.
            // for a background or minimized window) the foreground window is the
            // taskbar or another app, not the menu's target. Match the menu's
            // HMENU against each top-level window's system menu instead.
            _menuTargetWindow = FindMenuTargetWindow(hwnd);
            if (_menuTargetWindow == IntPtr.Zero)
                _menuTargetWindow = NativeMethods.GetForegroundWindow();
            _menuHwnd = hwnd;
            _lastSysMenuPopupAt = Environment.TickCount64;
            _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                ShowCompanionPopup(hwnd);
            });
        }

        // System menu closed — close the companion popup, but only if it was
        // created for a real system menu. The fallback popup (no menu) must not
        // be closed by an unrelated menu ending in another process.
        if (eventType == 0x0005 /* EVENT_SYSTEM_MENUEND */)
        {
            bool hadMenu = _menuHwnd != IntPtr.Zero;
            _menuHwnd = IntPtr.Zero;
            if (hadMenu)
            {
                _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                {
                    CloseCompanionPopup();
                });
            }
        }
    }

    /// <summary>
    /// While a system menu is tracking it holds mouse capture, so the companion
    /// popup receives no input. Hit-test it here: swallow clicks inside the popup
    /// and invoke the matching button, let everything else pass through (so a
    /// click outside still dismisses the menu, which then closes the popup).
    /// </summary>
    private IntPtr OnMouseHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();

            if (msg == NativeMethods.WM_LBUTTONDOWN)
            {
                var d = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);

                // Restore-to-clicked-screen: a left click on a taskbar button
                // restores a minimized window on its ORIGINAL monitor (that's
                // explorer's behavior). If the click happened on a different
                // monitor's taskbar, move the restored window there instead.
                if (_companionPopup == null && IsOverTaskbar(d.pt))
                    WatchForTaskbarRestore(d.pt);
            }

            // Shift+right-click on a taskbar button. On Windows 11 the taskbar
            // forwards this to the target app, which may or may not show its
            // classic system menu. Never swallow: explorer must still activate
            // the target window (which is how we learn the target).
            if (msg == NativeMethods.WM_RBUTTONDOWN
                && (NativeMethods.GetKeyState(NativeMethods.VK_SHIFT) & 0x8000) != 0
                && Environment.TickCount64 - _lastFallbackTriggerAt > 800) // debounce mouse bounce
            {
                var clickData = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                if (IsOverTaskbar(clickData.pt))
                {
                    _lastFallbackTriggerAt = Environment.TickCount64;
                    var clickPt = clickData.pt;
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                    timer.Tick += (_, _) =>
                    {
                        timer.Stop();
                        MaybeShowFallbackPopup(clickPt);
                    };
                    timer.Start();
                }
            }

            if (_companionPopup != null && _popupButtons.Count > 0
                && (msg == NativeMethods.WM_LBUTTONDOWN || msg == NativeMethods.WM_MOUSEMOVE))
            {
                var data = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                var pt = data.pt;
                if (pt.X >= _popupRect.Left && pt.X < _popupRect.Right
                    && pt.Y >= _popupRect.Top && pt.Y < _popupRect.Bottom)
                {
                    if (msg == NativeMethods.WM_MOUSEMOVE)
                    {
                        UpdateHover(pt);
                    }
                    else
                    {
                        HandlePopupClick(pt);
                        // Swallow: the captured menu would otherwise cancel itself
                        // and eat the click before it reaches the popup.
                        return (IntPtr)1;
                    }
                }
                else if (msg == NativeMethods.WM_LBUTTONDOWN)
                {
                    // Click outside the popup dismisses the system menu — but
                    // MENUEND/MENUPOPUPEND don't always fire, so close here too.
                    _dispatcher.BeginInvoke(() => CloseCompanionPopup());
                }
            }
        }
        return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    /// <summary>
    /// Fallback for apps that don't show a system menu on Shift+right-click:
    /// show the companion popup directly above the clicked taskbar button.
    /// </summary>
    private void MaybeShowFallbackPopup(NativeMethods.POINT pt)
    {
        try
        {
            // A real system menu appeared for this click — the normal path
            // already handled it.
            if (Environment.TickCount64 - _lastSysMenuPopupAt < 1000)
                return;

            // Explorer activates the target window on taskbar (shift+)right-click.
            var target = NativeMethods.GetForegroundWindow();
            if (!IsMovableAppWindow(target)) return;

            _menuTargetWindow = target;
            _menuHwnd = IntPtr.Zero;
            ShowCompanionPopupCore(
                new NativeMethods.RECT { Left = pt.X, Top = pt.Y, Right = pt.X, Bottom = pt.Y },
                IntPtr.Zero, anchorAbove: true);
        }
        catch { }
    }

    private static bool IsOverTaskbar(NativeMethods.POINT pt)
    {
        bool over = false;
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            var sb = new char[64];
            int len = NativeMethods.GetClassName(hwnd, sb, sb.Length);
            if (len <= 0) return true;
            var cls = new string(sb, 0, len);
            if (cls != "Shell_TrayWnd" && cls != "Shell_SecondaryTrayWnd") return true;
            if (NativeMethods.GetWindowRect(hwnd, out var r)
                && pt.X >= r.Left && pt.X < r.Right && pt.Y >= r.Top && pt.Y < r.Bottom)
            {
                over = true;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return over;
    }

    /// <summary>
    /// After a left click on a taskbar button, watches for a window transitioning
    /// from minimized to restored and moves it to the monitor that was clicked.
    /// </summary>
    private void WatchForTaskbarRestore(NativeMethods.POINT clickPt)
    {
        // Snapshot currently-minimized windows
        var minimized = new HashSet<IntPtr>();
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (NativeMethods.IsIconic(hwnd)) minimized.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        if (minimized.Count == 0) return;

        int attempts = 0;
        IntPtr candidate = IntPtr.Zero;
        NativeMethods.RECT candidateRect = default;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        timer.Tick += (_, _) =>
        {
            attempts++;
            IntPtr restored = IntPtr.Zero;
            NativeMethods.RECT restoredRect = default;
            foreach (var h in minimized)
            {
                if (!NativeMethods.IsWindow(h) || NativeMethods.IsIconic(h)) continue;
                // During the restore animation the rect morphs from the taskbar
                // position (and starts as the (-32000) placeholder) — wait until
                // it is valid AND stable across two polls before moving.
                NativeMethods.GetWindowRect(h, out var r);
                if (r.Left < -10000) continue;
                restored = h;
                restoredRect = r;
                break;
            }
            if (restored == IntPtr.Zero)
            {
                if (attempts >= 10) timer.Stop(); // ~3s then give up
                return;
            }
            if (restored != candidate
                || restoredRect.Left != candidateRect.Left || restoredRect.Top != candidateRect.Top
                || restoredRect.Right != candidateRect.Right || restoredRect.Bottom != candidateRect.Bottom)
            {
                // Still animating — remember and re-check on the next tick.
                candidate = restored;
                candidateRect = restoredRect;
                if (attempts >= 10) timer.Stop();
                return;
            }
            timer.Stop();
            // A taskbar restore always activates the window — if it isn't the
            // foreground window, something else restored it; don't touch it.
            if (NativeMethods.GetForegroundWindow() != restored)
                return;
            var hwnd = restored;
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var targetMon = NativeMethods.MonitorFromPoint(clickPt, NativeMethods.MONITOR_DEFAULTTONEAREST);
                    WindowMover.MoveWindowToMonitor(hwnd, targetMon);
                }
                catch { }
            });
        };
        timer.Start();
    }

    private static bool IsMovableAppWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindowVisible(hwnd)) return false;
        var sb = new char[64];
        int len = NativeMethods.GetClassName(hwnd, sb, sb.Length);
        if (len <= 0) return false;
        var cls = new string(sb, 0, len);
        return cls is not ("Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Progman" or "WorkerW");
    }

    private void UpdateHover(NativeMethods.POINT pt)
    {
        PopupButton? hit = null;
        foreach (var b in _popupButtons)
        {
            if (pt.X >= b.ScreenRect.Left && pt.X < b.ScreenRect.Right
                && pt.Y >= b.ScreenRect.Top && pt.Y < b.ScreenRect.Bottom)
            {
                hit = b;
                break;
            }
        }
        if (ReferenceEquals(hit, _hoveredButton)) return;

        var prev = _hoveredButton;
        _hoveredButton = hit;
        if (prev != null) prev.Element.Background = Brushes.Transparent;
        if (hit != null) hit.Element.Background = hit.HoverBrush;
    }

    private void HandlePopupClick(NativeMethods.POINT pt)
    {
        PopupButton? hit = null;
        foreach (var b in _popupButtons)
        {
            if (pt.X >= b.ScreenRect.Left && pt.X < b.ScreenRect.Right
                && pt.Y >= b.ScreenRect.Top && pt.Y < b.ScreenRect.Bottom)
            {
                hit = b;
                break;
            }
        }
        if (hit == null) return; // click on popup padding: swallow only

        var menu = _menuHwnd;
        var action = hit.Action;
        CloseCompanionPopup();
        // The click was swallowed, so the system menu never saw it and is still
        // open — dismiss it ourselves.
        if (menu != IntPtr.Zero)
        {
            NativeMethods.PostMessage(menu, NativeMethods.WM_KEYDOWN, (IntPtr)NativeMethods.VK_ESCAPE, IntPtr.Zero);
            NativeMethods.PostMessage(menu, NativeMethods.WM_KEYUP, (IntPtr)NativeMethods.VK_ESCAPE, IntPtr.Zero);
        }
        // The move can block for seconds (restoring a minimized window involves
        // waiting on the target thread) — never run it inside the LL hook
        // callback / UI thread.
        System.Threading.Tasks.Task.Run(() =>
        {
            try { action(); } catch { }
        });
    }

    private void ShowCompanionPopup(IntPtr menuHwnd)
    {
        try
        {
            if (!NativeMethods.GetWindowRect(menuHwnd, out var menuRect))
                return;
            ShowCompanionPopupCore(menuRect, menuHwnd, anchorAbove: false);
        }
        catch { }
    }

    /// <summary>
    /// Shows the companion popup next to an anchor rectangle (physical pixels).
    /// Menu path: anchored to the system menu, placed to its right.
    /// Fallback path (anchorAbove): anchored to the taskbar click point, placed above it.
    /// </summary>
    private void ShowCompanionPopupCore(NativeMethods.RECT anchor, IntPtr menuHwnd, bool anchorAbove)
    {
        CloseCompanionPopup();

        var monitors = MonitorHelper.GetAllMonitors();
        if (monitors.Count < 2) return;

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
            ShowActivated = false,
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

        var buttons = new List<(Border Element, Action Action, System.Windows.Media.Brush HoverBrush)>();

        // "Current screen" button
        buttons.Add(CreateMenuButton(stack, "Current screen", fg, hoverBg, () =>
        {
            NativeMethods.GetCursorPos(out var cursorPt);
            var mon = NativeMethods.MonitorFromPoint(cursorPt, NativeMethods.MONITOR_DEFAULTTONEAREST);
            WindowMover.MoveWindowToMonitor(targetWnd, mon);
        }));

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
            buttons.Add(CreateMenuButton(stack, m.Name, fg, hoverBg, () =>
            {
                WindowMover.MoveWindowToMonitor(targetWnd, monHandle);
            }));
        }

        border.Child = stack;
        popup.Content = border;

        // Position the popup in PHYSICAL pixels. WPF's Window.Left/Top are DIPs
        // whose conversion at placement time uses the system DPI context, not
        // the target monitor's — with mixed-DPI monitors that can land the popup
        // in the dead zone between displays (invisible). So: Show roughly near
        // the anchor, then SetWindowPos with exact physical coordinates.
        var anchorMon = NativeMethods.MonitorFromPoint(
            new NativeMethods.POINT { X = anchor.Left, Y = anchor.Top },
            NativeMethods.MONITOR_DEFAULTTONEAREST);
        uint dpiX = 0, dpiY = 0;
        if (NativeMethods.GetDpiForMonitor(anchorMon, 0, out dpiX, out dpiY) != 0 || dpiX == 0)
            dpiX = 96;
        double scale = dpiX / 96.0;

        // Coarse initial position in DIPs (corrected physically right after Show)
        popup.Left = anchor.Left / scale;
        popup.Top = anchor.Top / scale;

        _companionPopup = popup;
        popup.Show();

        // The popup now sits on the anchor monitor, so its DpiScale is that
        // monitor's — use it to compute the physical size.
        double popupScale = VisualTreeHelper.GetDpi(popup).DpiScaleX;
        int physW = (int)Math.Ceiling(popup.ActualWidth * popupScale);
        int physH = (int)Math.Ceiling(popup.ActualHeight * popupScale);

        int physX, physY;
        if (!anchorAbove)
        {
            // To the right of the system menu
            physX = anchor.Right + 4;
            physY = anchor.Top;
        }
        else
        {
            // Above the clicked taskbar button
            physX = anchor.Left - physW / 2;
            physY = anchor.Top - physH - (int)(12 * scale);
        }

        // Clamp into the anchor monitor's work area (all physical pixels)
        var mi = new NativeMethods.MONITORINFO { cbSize = 40 };
        if (NativeMethods.GetMonitorInfo(anchorMon, ref mi))
        {
            if (!anchorAbove && physX + physW > mi.rcWork.Right)
                physX = anchor.Left - physW - 4; // flip to the menu's left side
            physX = Math.Max(mi.rcWork.Left, Math.Min(physX, mi.rcWork.Right - physW));
            physY = Math.Max(mi.rcWork.Top, Math.Min(physY, mi.rcWork.Bottom - physH));
        }

        var popupHwnd = new System.Windows.Interop.WindowInteropHelper(popup).Handle;
        NativeMethods.SetWindowPos(popupHwnd, IntPtr.Zero, physX, physY, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

        // MENUEND/MENUPOPUPEND don't always fire when a menu is dismissed, so
        // also poll for the menu window disappearing.
        _menuWatchTimer?.Stop();
        if (menuHwnd != IntPtr.Zero)
        {
            _menuWatchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _menuWatchTimer.Tick += (_, _) =>
            {
                if (!NativeMethods.IsWindow(menuHwnd))
                    CloseCompanionPopup();
            };
            _menuWatchTimer.Start();
        }

        // Record the popup and its buttons in physical screen pixels so the
        // low-level mouse hook can hit-test them (the popup gets no input while
        // the system menu holds capture).
        popup.UpdateLayout();
        _popupButtons.Clear();
        foreach (var (element, action, hoverBrush) in buttons)
        {
            var topLeft = element.PointToScreen(new System.Windows.Point(0, 0));
            var bottomRight = element.PointToScreen(new System.Windows.Point(element.ActualWidth, element.ActualHeight));
            _popupButtons.Add(new PopupButton
            {
                Element = element,
                Action = action,
                HoverBrush = hoverBrush,
                ScreenRect = new NativeMethods.RECT
                {
                    Left = (int)topLeft.X,
                    Top = (int)topLeft.Y,
                    Right = (int)bottomRight.X,
                    Bottom = (int)bottomRight.Y
                }
            });
        }
        _popupRect = new NativeMethods.RECT
        {
            Left = physX,
            Top = physY,
            Right = physX + physW,
            Bottom = physY + physH
        };
    }

    private static (Border Element, Action Action, System.Windows.Media.Brush HoverBrush) CreateMenuButton(
        StackPanel parent, string text, System.Windows.Media.Brush fg, System.Windows.Media.Brush hoverBg, Action onClick)
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

        parent.Children.Add(btn);
        return (btn, onClick, hoverBg);
    }

    private void CloseCompanionPopup()
    {
        _popupButtons.Clear();
        _hoveredButton = null;
        _menuWatchTimer?.Stop();

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

    /// <summary>
    /// Finds the window that owns the given system menu by matching the menu's
    /// HMENU against each top-level window's system menu handle. The taskbar
    /// shows the window's real system menu, so the HMENU identifies the target.
    /// If the HMENU is a copy (no match), falls back to the menu's process:
    /// on Windows 11 the taskbar asks the target app itself to show the menu.
    /// </summary>
    private static IntPtr FindMenuTargetWindow(IntPtr menuHwnd)
    {
        var hMenu = NativeMethods.SendMessageTimeoutW(menuHwnd, (uint)NativeMethods.MN_GETHMENU,
            IntPtr.Zero, IntPtr.Zero, NativeMethods.SMTO_ABORTIFHUNG, 100, out _);
        if (hMenu != IntPtr.Zero)
        {
            IntPtr target = IntPtr.Zero;
            NativeMethods.EnumWindows((hwnd, _) =>
            {
                // Pre-filter by WS_SYSMENU: GetSystemMenu creates a system menu
                // copy for windows that don't have one — don't mutate every
                // top-level window on the desktop just for matching.
                int style = NativeMethods.GetWindowLongA(hwnd, NativeMethods.GWL_STYLE);
                if ((style & NativeMethods.WS_SYSMENU) == 0)
                    return true;
                if (NativeMethods.GetSystemMenu(hwnd, false) == hMenu)
                {
                    target = hwnd;
                    return false; // stop enumerating
                }
                return true;
            }, IntPtr.Zero);
            if (target != IntPtr.Zero)
                return target;
        }

        // Fallback: pick a visible top-level window from the menu's process.
        NativeMethods.GetWindowThreadProcessId(menuHwnd, out uint menuPid);
        if (menuPid == 0)
            return IntPtr.Zero;

        var fg = NativeMethods.GetForegroundWindow();
        NativeMethods.GetWindowThreadProcessId(fg, out uint fgPid);
        if (fgPid == menuPid && fg != IntPtr.Zero
            && NativeMethods.GetWindow(fg, NativeMethods.GW_OWNER) == IntPtr.Zero)
            return fg;

        IntPtr candidate = IntPtr.Zero;
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid != menuPid || !NativeMethods.IsWindowVisible(hwnd))
                return true;
            if (NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER) != IntPtr.Zero)
                return true;
            int style = NativeMethods.GetWindowLongA(hwnd, NativeMethods.GWL_STYLE);
            if ((style & NativeMethods.WS_SYSMENU) == 0)
                return true;
            candidate = hwnd;
            return false;
        }, IntPtr.Zero);
        return candidate;
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

        if (_mouseHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
    }
}
