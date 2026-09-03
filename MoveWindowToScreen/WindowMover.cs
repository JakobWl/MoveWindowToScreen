namespace MoveWindowToScreen;

/// <summary>
/// Core logic to move a window between monitors, preserving relative position and maximized state.
/// </summary>
internal static class WindowMover
{
    /// <summary>
    /// Recent (hwnd, tick) pairs of minimized windows this app itself restored
    /// (popup / companion-menu moves). Used to keep those restores from being
    /// mistaken for taskbar-initiated restores by the restore-to-clicked-screen
    /// watcher in SystemMenuInjector. A small list rather than a single slot:
    /// rapid popup moves can restore several windows concurrently, and a
    /// delayed EVENT_SYSTEM_MINIMIZEEND for the first window must not escape
    /// suppression just because a second restore overwrote the slot.
    /// Writers are background threads (MoveWindowToMonitor may run inside
    /// Task.Run), the reader is the UI thread — guarded by a lock so the
    /// (hwnd, tick) pair can never be observed torn.
    /// </summary>
    private const long OwnRestoreSuppressMs = 1000;
    private static readonly List<(IntPtr Hwnd, long Tick)> _ownRestores = [];
    private static readonly object _ownRestoresLock = new();

    /// <summary>Records that this app is about to restore the given minimized window.</summary>
    public static void RecordOwnRestore(IntPtr hwnd)
    {
        lock (_ownRestoresLock)
        {
            long now = Environment.TickCount64;
            _ownRestores.RemoveAll(r => now - r.Tick > OwnRestoreSuppressMs);
            _ownRestores.Add((hwnd, now));
        }
    }

    /// <summary>
    /// True if this app itself restored the given window within the last
    /// second — its EVENT_SYSTEM_MINIMIZEEND must not be re-attributed to a
    /// taskbar interaction. Other windows' restores are unaffected, unlike a
    /// blanket time-based suppression.
    /// </summary>
    public static bool WasRecentlyRestoredByUs(IntPtr hwnd)
    {
        lock (_ownRestoresLock)
        {
            long now = Environment.TickCount64;
            _ownRestores.RemoveAll(r => now - r.Tick > OwnRestoreSuppressMs);
            return _ownRestores.Any(r => r.Hwnd == hwnd);
        }
    }

    /// <summary>
    /// Moves the currently active (foreground) window to the monitor where the mouse cursor is.
    /// </summary>
    public static void MoveActiveWindowToCurrentScreen()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
            return;

        NativeMethods.GetCursorPos(out var cursorPos);
        var targetMonitor = NativeMethods.MonitorFromPoint(cursorPos, NativeMethods.MONITOR_DEFAULTTONEAREST);
        MoveWindowToMonitor(hwnd, targetMonitor);
    }

    /// <summary>
    /// Moves a specific window to the monitor where the mouse cursor currently is.
    /// </summary>
    public static void MoveWindowToMouseScreen(IntPtr hwnd)
    {
        NativeMethods.GetCursorPos(out var cursorPos);
        var targetMonitor = NativeMethods.MonitorFromPoint(cursorPos, NativeMethods.MONITOR_DEFAULTTONEAREST);
        MoveWindowToMonitor(hwnd, targetMonitor);
    }

    /// <summary>
    /// Moves a window to a specific monitor identified by its handle.
    /// </summary>
    public static void MoveWindowToMonitor(IntPtr hwnd, IntPtr targetMonitor)
    {
        if (hwnd == IntPtr.Zero || targetMonitor == IntPtr.Zero)
            return;

        var sourceMonitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        bool isMinimized = NativeMethods.IsIconic(hwnd);

        // If already on the same monitor, restore (if minimized) and bring to front
        if (targetMonitor == sourceMonitor)
        {
            if (isMinimized)
            {
                RecordOwnRestore(hwnd);
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
            }
            NativeMethods.SetForegroundWindow(hwnd);
            return;
        }

        var srcInfo = GetMonitorInfo(sourceMonitor);
        var tgtInfo = GetMonitorInfo(targetMonitor);

        // Minimized window: restore it (this happens on its original monitor),
        // then move it with the normal path. Note: rewriting rcNormalPosition
        // via SetWindowPlacement does NOT work — Windows ignores the new
        // position (verified on Win11), so restore-then-move is required.
        if (isMinimized)
        {
            RecordOwnRestore(hwnd);
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
        }

        bool wasMaximized = NativeMethods.IsZoomed(hwnd);

        // If maximized, restore first so we can get normal bounds
        if (wasMaximized)
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);

        // Use the placement's normal rect: GetWindowRect is unreliable while a
        // restore animation is running (it morphs from the taskbar position and
        // reports the (-32000) placeholder or in-between values), whereas
        // rcNormalPosition always holds the window's real normal bounds.
        var wp = new NativeMethods.WINDOWPLACEMENT
        {
            length = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.WINDOWPLACEMENT>()
        };
        if (!NativeMethods.GetWindowPlacement(hwnd, ref wp))
            return;
        var windowRect = wp.rcNormalPosition;

        var newRect = MapRectToTargetMonitor(windowRect, srcInfo, tgtInfo,
            GetMonitorDpi(sourceMonitor), GetMonitorDpi(targetMonitor));

        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, newRect.Left, newRect.Top, newRect.Width, newRect.Height,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

        // Re-maximize on the target monitor if it was maximized before
        if (wasMaximized)
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOWMAXIMIZED);

        // Bring the moved window to the front
        NativeMethods.SetForegroundWindow(hwnd);
    }

    /// <summary>
    /// Maps a window rect from the source monitor's work area to the target
    /// monitor's work area, preserving relative position and size (e.g. a
    /// window occupying half the screen stays half after the move, both ways).
    ///
    /// Size compensation: Windows automatically scales a window's size by
    /// tgtDpi/srcDpi when it crosses a monitor DPI boundary, on top of
    /// whatever SetWindowPos sets (verified empirically on Win11 with
    /// PerMonitorV1, V2-WPF and Qt apps). We pre-divide by that factor so the
    /// final on-screen size matches the intended proportional size.
    /// </summary>
    private static NativeMethods.RECT MapRectToTargetMonitor(
        NativeMethods.RECT windowRect,
        NativeMethods.MONITORINFO srcInfo,
        NativeMethods.MONITORINFO tgtInfo,
        uint srcDpi,
        uint tgtDpi)
    {
        var srcWork = srcInfo.rcWork;
        var tgtWork = tgtInfo.rcWork;

        // Relative position and size within the source monitor's work area
        double relX = (srcWork.Width > 0) ? (double)(windowRect.Left - srcWork.Left) / srcWork.Width : 0;
        double relY = (srcWork.Height > 0) ? (double)(windowRect.Top - srcWork.Top) / srcWork.Height : 0;
        double relW = (srcWork.Width > 0) ? (double)windowRect.Width / srcWork.Width : 1;
        double relH = (srcWork.Height > 0) ? (double)windowRect.Height / srcWork.Height : 1;

        // Map position proportionally to the target monitor
        int newX = tgtWork.Left + (int)(relX * tgtWork.Width);
        int newY = tgtWork.Top + (int)(relY * tgtWork.Height);

        // Intended physical size on the target monitor, clamped to its work area
        int desiredW = Math.Min((int)(relW * tgtWork.Width), tgtWork.Width);
        int desiredH = Math.Min((int)(relH * tgtWork.Height), tgtWork.Height);

        // Undo the automatic DPI rescale Windows applies on the transition
        double compensation = (srcDpi > 0 && tgtDpi > 0) ? (double)srcDpi / tgtDpi : 1.0;
        int newW = (int)(desiredW * compensation);
        int newH = (int)(desiredH * compensation);

        // Clamp position into the target work area using the final on-screen size
        newX = Math.Max(tgtWork.Left, Math.Min(newX, tgtWork.Left + tgtWork.Width - desiredW));
        newY = Math.Max(tgtWork.Top, Math.Min(newY, tgtWork.Top + tgtWork.Height - desiredH));

        return new NativeMethods.RECT { Left = newX, Top = newY, Right = newX + newW, Bottom = newY + newH };
    }

    private static uint GetMonitorDpi(IntPtr hMonitor)
    {
        return NativeMethods.GetDpiForMonitor(hMonitor, 0, out uint dpiX, out _) == 0 && dpiX > 0
            ? dpiX
            : 96;
    }

    private static NativeMethods.MONITORINFO GetMonitorInfo(IntPtr hMonitor)
    {
        var info = new NativeMethods.MONITORINFO { cbSize = 40 };
        NativeMethods.GetMonitorInfo(hMonitor, ref info);
        return info;
    }
}
