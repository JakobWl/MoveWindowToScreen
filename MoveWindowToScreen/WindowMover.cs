namespace MoveWindowToScreen;

/// <summary>
/// Core logic to move a window between monitors, preserving relative position and maximized state.
/// </summary>
internal static class WindowMover
{
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

        // Restore minimized windows first
        if (NativeMethods.IsIconic(hwnd))
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);

        var sourceMonitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);

        // If already on the same monitor, just bring to front
        if (targetMonitor == sourceMonitor)
        {
            NativeMethods.SetForegroundWindow(hwnd);
            return;
        }

        var srcInfo = GetMonitorInfo(sourceMonitor);
        var tgtInfo = GetMonitorInfo(targetMonitor);

        bool wasMaximized = NativeMethods.IsZoomed(hwnd);

        // If maximized, restore first so we can get normal bounds
        if (wasMaximized)
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);

        NativeMethods.GetWindowRect(hwnd, out var windowRect);

        // Calculate relative position on source monitor's work area
        var srcWork = srcInfo.rcWork;
        var tgtWork = tgtInfo.rcWork;

        double relX = (srcWork.Width > 0) ? (double)(windowRect.Left - srcWork.Left) / srcWork.Width : 0;
        double relY = (srcWork.Height > 0) ? (double)(windowRect.Top - srcWork.Top) / srcWork.Height : 0;
        double relW = (srcWork.Width > 0) ? (double)windowRect.Width / srcWork.Width : 1;
        double relH = (srcWork.Height > 0) ? (double)windowRect.Height / srcWork.Height : 1;

        // Map to target monitor
        int newX = tgtWork.Left + (int)(relX * tgtWork.Width);
        int newY = tgtWork.Top + (int)(relY * tgtWork.Height);
        int newW = (int)(relW * tgtWork.Width);
        int newH = (int)(relH * tgtWork.Height);

        // Clamp to target work area
        newW = Math.Min(newW, tgtWork.Width);
        newH = Math.Min(newH, tgtWork.Height);
        newX = Math.Max(tgtWork.Left, Math.Min(newX, tgtWork.Left + tgtWork.Width - newW));
        newY = Math.Max(tgtWork.Top, Math.Min(newY, tgtWork.Top + tgtWork.Height - newH));

        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, newX, newY, newW, newH,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

        // Re-maximize on the target monitor if it was maximized before
        if (wasMaximized)
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOWMAXIMIZED);

        // Bring the moved window to the front
        NativeMethods.SetForegroundWindow(hwnd);
    }

    private static NativeMethods.MONITORINFO GetMonitorInfo(IntPtr hMonitor)
    {
        var info = new NativeMethods.MONITORINFO { cbSize = 40 };
        NativeMethods.GetMonitorInfo(hMonitor, ref info);
        return info;
    }
}
