namespace MoveWindowToScreen;

/// <summary>
/// Enumerates and caches information about all connected monitors.
/// </summary>
internal sealed class MonitorList
{
    public record MonitorEntry(IntPtr Handle, NativeMethods.RECT WorkArea, NativeMethods.RECT Bounds, bool IsPrimary, string DisplayName);

    private List<MonitorEntry> _monitors = [];

    public IReadOnlyList<MonitorEntry> Monitors => _monitors;

    public void Refresh()
    {
        var list = new List<MonitorEntry>();
        int index = 0;
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdc, ref NativeMethods.RECT rect, IntPtr data) =>
        {
            var info = new NativeMethods.MONITORINFO { cbSize = 40 };
            NativeMethods.GetMonitorInfo(hMonitor, ref info);
            bool isPrimary = (info.dwFlags & 1) != 0; // MONITORINFOF_PRIMARY
            index++;
            string name = isPrimary ? $"Screen {index} (Primary)" : $"Screen {index}";
            list.Add(new MonitorEntry(hMonitor, info.rcWork, info.rcMonitor, isPrimary, name));
            return true;
        }, IntPtr.Zero);
        _monitors = list;
    }

    public MonitorEntry? GetByIndex(int index)
    {
        if (index >= 0 && index < _monitors.Count)
            return _monitors[index];
        return null;
    }
}
