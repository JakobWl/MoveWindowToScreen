using System.Runtime.InteropServices;

namespace MoveWindowToScreen;

/// <summary>
/// Provides information about connected monitors.
/// </summary>
internal sealed class MonitorInfo
{
    public IntPtr Handle { get; init; }
    public int Index { get; init; }
    /// <summary>The Windows display device number (e.g. 1 for \\.\DISPLAY1).</summary>
    public int DeviceNumber { get; init; }
    public string Name { get; init; } = "";
    public bool IsPrimary { get; init; }
    public NativeMethods.RECT WorkArea { get; init; }
    public NativeMethods.RECT Bounds { get; init; }

    public override string ToString() => Name;
}

internal static class MonitorHelper
{
    /// <summary>
    /// Returns a list of all active monitors with Windows display device numbers.
    /// </summary>
    public static List<MonitorInfo> GetAllMonitors()
    {
        var monitors = new List<MonitorInfo>();
        int index = 0;

        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr _, ref NativeMethods.RECT _, IntPtr _) =>
        {
            var miEx = new NativeMethods.MONITORINFOEXW();
            miEx.cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEXW>();
            miEx.szDevice = new string('\0', 32);
            NativeMethods.GetMonitorInfoEx(hMonitor, ref miEx);

            bool isPrimary = (miEx.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0;
            int displayNumber = index + 1; // 1-based, matches Windows Display Settings order
            int width = miEx.rcMonitor.Width;
            int height = miEx.rcMonitor.Height;
            string name = $"{displayNumber} ({width}x{height})";

            monitors.Add(new MonitorInfo
            {
                Handle = hMonitor,
                Index = index,
                DeviceNumber = displayNumber,
                Name = name,
                IsPrimary = isPrimary,
                WorkArea = miEx.rcWork,
                Bounds = miEx.rcMonitor
            });
            index++;
            return true;
        }, IntPtr.Zero);

        return monitors;
    }

    /// <summary>
    /// Extracts the display number from a device name like "\\.\DISPLAY1".
    /// </summary>
    private static int ExtractDeviceNumber(string? deviceName)
    {
        if (string.IsNullOrEmpty(deviceName))
            return 0;

        // Find trailing digits
        int i = deviceName.Length - 1;
        while (i >= 0 && char.IsDigit(deviceName[i]))
            i--;

        if (i < deviceName.Length - 1 && int.TryParse(deviceName.AsSpan(i + 1), out int num))
            return num;

        return 0;
    }
}
