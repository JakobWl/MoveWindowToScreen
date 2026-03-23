using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace MoveWindowToScreen;

/// <summary>
/// Injects a "Move to >" submenu into the system menu of all top-level windows,
/// with one entry per connected monitor. Uses WH_MOUSE_LL to detect clicks on
/// these items across all processes without needing a separate DLL.
/// </summary>
internal sealed partial class SystemMenuInjector : IDisposable
{
    // Command IDs: SC_BASE + monitorIndex (supports up to 16 monitors)
    private const int SC_BASE = 0xD000;
    private const int SC_MAX = 0xD00F;
    private const int SC_MOVE_CURRENT = 0xD0F0; // "Move to current screen"
    private const string SubmenuText = "Move to screen";
    private const string MoveCurrentText = "Move to current screen";
    private const string MenuClassName = "#32768";

    private readonly HashSet<IntPtr> _injectedWindows = [];
    private readonly List<IntPtr> _createdSubmenus = [];
    private readonly DispatcherTimer _scanTimer;
    private readonly HwndSource _messageWindow;
    private readonly uint _shellHookMsg;
    private readonly NativeMethods.LowLevelMouseProc _mouseProc;
    private IntPtr _mouseHook;
    private readonly NativeMethods.WinEventDelegate _winEventProc;
    private IntPtr _winEventHook;
    private readonly Dispatcher _dispatcher;

    public SystemMenuInjector()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _mouseProc = MouseHookCallback;
        _winEventProc = OnMenuPopupStart;

        var parameters = new HwndSourceParameters("MoveWindowToScreen_ShellHook")
        {
            Width = 0, Height = 0, WindowStyle = 0
        };
        _messageWindow = new HwndSource(parameters);
        _messageWindow.AddHook(WndProc);

        _shellHookMsg = NativeMethods.RegisterWindowMessage("SHELLHOOK");
        NativeMethods.RegisterShellHookWindow(_messageWindow.Handle);

        _mouseHook = NativeMethods.SetMouseHookEx(
            NativeMethods.WH_MOUSE_LL, _mouseProc, IntPtr.Zero, 0);

        // Hook menu popup events so we can re-inject when system menus appear
        _winEventHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_MENUPOPUPSTART,
            NativeMethods.EVENT_SYSTEM_MENUPOPUPSTART,
            IntPtr.Zero, _winEventProc, 0, 0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);

        InjectIntoAllWindows();

        _scanTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _scanTimer.Tick += (_, _) => InjectIntoAllWindows();
        _scanTimer.Start();
    }

    private void OnMenuPopupStart(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        // When a system menu popup appears, find the owning window and re-inject if needed
        if (idObject != 0) return; // OBJID_WINDOW

        // Get the foreground window — it's likely the one whose system menu is showing
        var fgWnd = NativeMethods.GetForegroundWindow();
        if (fgWnd == IntPtr.Zero) return;

        var sysMenu = NativeMethods.GetSystemMenu(fgWnd, false);
        if (sysMenu == IntPtr.Zero) return;

        // Force re-injection by removing from tracked set if our items are gone
        if (!HasOurMenuItem(sysMenu))
        {
            _injectedWindows.Remove(fgWnd);
            InjectMenuItem(fgWnd);
        }
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == NativeMethods.WM_LBUTTONUP)
        {
            var hookStruct = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
            TryHandleMenuClick(hookStruct.pt);
        }
        return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private void TryHandleMenuClick(NativeMethods.POINT pt)
    {
        var hwndUnder = NativeMethods.WindowFromPoint(pt);
        if (hwndUnder == IntPtr.Zero || !IsPopupMenuWindow(hwndUnder))
            return;

        var hMenu = NativeMethods.SendMessage(hwndUnder, NativeMethods.MN_GETHMENU, IntPtr.Zero, IntPtr.Zero);
        if (hMenu == IntPtr.Zero)
            return;

        int itemIndex = NativeMethods.MenuItemFromPoint(IntPtr.Zero, hMenu, pt);
        if (itemIndex < 0)
            return;

        uint itemId = NativeMethods.GetMenuItemID(hMenu, itemIndex);

        // "Move to current screen" — capture the monitor under the mouse RIGHT NOW
        if (itemId == SC_MOVE_CURRENT)
        {
            IntPtr targetWindow = FindOwnerWindow(hMenu);
            if (targetWindow == IntPtr.Zero)
                return;

            // The menu is displayed on the "current screen" — use the click point to determine it
            var targetMonitor = NativeMethods.MonitorFromPoint(pt, NativeMethods.MONITOR_DEFAULTTONEAREST);
            var capturedHwnd = targetWindow;
            var capturedMonitor = targetMonitor;

            _dispatcher.BeginInvoke(() =>
            {
                WindowMover.MoveWindowToMonitor(capturedHwnd, capturedMonitor);
            });
            return;
        }

        // Per-monitor submenu items
        if (itemId < SC_BASE || itemId > SC_MAX)
            return;

        int monitorIndex = (int)(itemId - SC_BASE);

        IntPtr ownerWindow = FindOwnerWindow(hMenu);
        if (ownerWindow == IntPtr.Zero)
            return;

        var monitors = MonitorHelper.GetAllMonitors();
        if (monitorIndex >= monitors.Count)
            return;

        var targetMon = monitors[monitorIndex].Handle;
        var capturedWnd = ownerWindow;

        _dispatcher.BeginInvoke(() =>
        {
            WindowMover.MoveWindowToMonitor(capturedWnd, targetMon);
        });
    }

    /// <summary>
    /// Walks up the menu hierarchy to find the owner window.
    /// A submenu's parent is the system menu, which belongs to a tracked window.
    /// </summary>
    private IntPtr FindOwnerWindow(IntPtr hMenu)
    {
        foreach (var hwnd in _injectedWindows)
        {
            var sysMenu = NativeMethods.GetSystemMenu(hwnd, false);
            if (sysMenu == IntPtr.Zero) continue;

            // Direct match (top-level system menu item)
            if (sysMenu == hMenu)
                return hwnd;

            // Check if hMenu is a submenu of this system menu
            int count = NativeMethods.GetMenuItemCount(sysMenu);
            for (int i = 0; i < count; i++)
            {
                var mii = new NativeMethods.MENUITEMINFO
                {
                    cbSize = (uint)Marshal.SizeOf<NativeMethods.MENUITEMINFO>(),
                    fMask = 0x00000004 // MIIM_SUBMENU
                };
                if (NativeMethods.GetMenuItemInfo(sysMenu, (uint)i, true, ref mii))
                {
                    if (mii.hSubMenu != IntPtr.Zero && mii.hSubMenu == hMenu)
                        return hwnd;
                }
            }
        }
        return IntPtr.Zero;
    }

    private static bool IsPopupMenuWindow(IntPtr hwnd)
    {
        var className = new char[16];
        int len = NativeMethods.GetClassName(hwnd, className, className.Length);
        if (len <= 0) return false;
        return new string(className, 0, len) == MenuClassName;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == (int)_shellHookMsg && wParam.ToInt32() == NativeMethods.HSHELL_WINDOWCREATED)
        {
            var newHwnd = lParam;
            if (IsValidTopLevelWindow(newHwnd))
                InjectMenuItem(newHwnd);
        }
        return IntPtr.Zero;
    }

    private void InjectIntoAllWindows()
    {
        _injectedWindows.RemoveWhere(h => !NativeMethods.IsWindowVisible(h));

        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (IsValidTopLevelWindow(hWnd))
                InjectMenuItem(hWnd);
            return true;
        }, IntPtr.Zero);
    }

    private static bool IsValidTopLevelWindow(IntPtr hWnd)
    {
        if (!NativeMethods.IsWindowVisible(hWnd))
            return false;

        int exStyle = NativeMethods.GetWindowLongA(hWnd, NativeMethods.GWL_EXSTYLE);
        if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0)
            return false;

        var owner = NativeMethods.GetWindow(hWnd, NativeMethods.GW_OWNER);
        if (owner != IntPtr.Zero)
            return false;

        return true;
    }

    private void InjectMenuItem(IntPtr hWnd)
    {
        if (_injectedWindows.Contains(hWnd))
            return;

        var sysMenu = NativeMethods.GetSystemMenu(hWnd, false);
        if (sysMenu == IntPtr.Zero)
            return;

        if (HasOurMenuItem(sysMenu))
        {
            _injectedWindows.Add(hWnd);
            return;
        }

        int itemCount = NativeMethods.GetMenuItemCount(sysMenu);
        if (itemCount < 0)
            return;

        // Build a submenu with one entry per monitor
        var monitors = MonitorHelper.GetAllMonitors();
        if (monitors.Count < 2)
        {
            // Only one monitor — no point showing the menu
            _injectedWindows.Add(hWnd);
            return;
        }

        var subMenu = NativeMethods.CreatePopupMenu();
        if (subMenu == IntPtr.Zero)
            return;

        _createdSubmenus.Add(subMenu);

        foreach (var monitor in monitors)
        {
            NativeMethods.AppendMenu(subMenu, NativeMethods.MF_STRING,
                (nuint)(SC_BASE + monitor.Index), monitor.Name);
        }

        // Insert separator + "Move to current screen" + submenu, above Close (last item)
        uint insertPos = (uint)Math.Max(0, itemCount - 1);
        NativeMethods.InsertMenu(sysMenu, insertPos,
            NativeMethods.MF_BYPOSITION | NativeMethods.MF_SEPARATOR, 0, null);
        NativeMethods.InsertMenu(sysMenu, insertPos + 1,
            NativeMethods.MF_BYPOSITION | NativeMethods.MF_STRING,
            (nuint)SC_MOVE_CURRENT, MoveCurrentText);
        NativeMethods.InsertMenu(sysMenu, insertPos + 2,
            NativeMethods.MF_BYPOSITION | NativeMethods.MF_POPUP,
            (nuint)subMenu, SubmenuText);

        _injectedWindows.Add(hWnd);
    }

    private static bool HasOurMenuItem(IntPtr hMenu)
    {
        int count = NativeMethods.GetMenuItemCount(hMenu);
        for (int i = 0; i < count; i++)
        {
            var mii = new NativeMethods.MENUITEMINFO
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.MENUITEMINFO>(),
                fMask = NativeMethods.MIIM_ID
            };
            if (NativeMethods.GetMenuItemInfo(hMenu, (uint)i, true, ref mii))
            {
                if (mii.wID == (uint)SC_MOVE_CURRENT || (mii.wID >= SC_BASE && mii.wID <= SC_MAX))
                    return true;
            }

            // Also check for submenu containing our items
            var sub = new NativeMethods.MENUITEMINFO
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.MENUITEMINFO>(),
                fMask = 0x00000004 // MIIM_SUBMENU
            };
            if (NativeMethods.GetMenuItemInfo(hMenu, (uint)i, true, ref sub) && sub.hSubMenu != IntPtr.Zero)
            {
                int subCount = NativeMethods.GetMenuItemCount(sub.hSubMenu);
                for (int j = 0; j < subCount; j++)
                {
                    var subItem = new NativeMethods.MENUITEMINFO
                    {
                        cbSize = (uint)Marshal.SizeOf<NativeMethods.MENUITEMINFO>(),
                        fMask = NativeMethods.MIIM_ID
                    };
                    if (NativeMethods.GetMenuItemInfo(sub.hSubMenu, (uint)j, true, ref subItem))
                    {
                        if (subItem.wID >= SC_BASE && subItem.wID <= SC_MAX)
                            return true;
                    }
                }
            }
        }
        return false;
    }

    public void Dispose()
    {
        _scanTimer.Stop();

        if (_mouseHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }

        if (_winEventHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_winEventHook);
            _winEventHook = IntPtr.Zero;
        }

        NativeMethods.DeregisterShellHookWindow(_messageWindow.Handle);
        _messageWindow.RemoveHook(WndProc);
        _messageWindow.Dispose();

        // Restore original system menus
        foreach (var hWnd in _injectedWindows)
        {
            try { NativeMethods.GetSystemMenu(hWnd, true); } catch { }
        }
        _injectedWindows.Clear();

        foreach (var hMenu in _createdSubmenus)
        {
            try { NativeMethods.DestroyMenu(hMenu); } catch { }
        }
        _createdSubmenus.Clear();
    }
}
