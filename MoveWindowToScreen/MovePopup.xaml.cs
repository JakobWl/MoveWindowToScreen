using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace MoveWindowToScreen;

public partial class MovePopup : Window
{
    private readonly List<Window> _identifyWindows = [];

    public MovePopup()
    {
        InitializeComponent();
        ApplyWindowsTheme();
        Loaded += (_, _) => RefreshContent();
    }

    private void ApplyWindowsTheme()
    {
        bool isDark = true;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int val)
                isDark = val == 0;
        }
        catch { /* default to dark */ }

        if (isDark)
        {
            SetBrush("AccentBrush", "#0078D4");
            SetBrush("SurfaceBrush", "#2C2C2C");
            SetBrush("CardHoverBrush", "#404040");
            SetBrush("TextPrimaryBrush", "#FFFFFF");
            SetBrush("TextSecondaryBrush", "#9E9E9E");
            SetBrush("BorderBrush", "#454545");
            SetBrush("ScreenButtonBrush", "#3A3A3A");
            SetBrush("ScreenButtonHoverBrush", "#4A4A4A");
        }
        else
        {
            SetBrush("AccentBrush", "#0078D4");
            SetBrush("SurfaceBrush", "#FAFAFA");
            SetBrush("CardHoverBrush", "#E8E8E8");
            SetBrush("TextPrimaryBrush", "#1A1A1A");
            SetBrush("TextSecondaryBrush", "#666666");
            SetBrush("BorderBrush", "#D0D0D0");
            SetBrush("ScreenButtonBrush", "#E5E5E5");
            SetBrush("ScreenButtonHoverBrush", "#D0D0D0");
        }
    }

    private void SetBrush(string key, string hex)
    {
        var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
        Resources[key] = new SolidColorBrush(color);
    }

    public void RefreshContent()
    {
        var monitors = MonitorHelper.GetAllMonitors();
        var windows = GetTopLevelWindows();

        // Build window list
        var items = new List<WindowItem>();
        foreach (var (hwnd, title) in windows)
        {
            // Skip our own popup
            var thisHwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd == thisHwnd) continue;

            var currentMonitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
            string screenLabel = "Unknown screen";
            foreach (var m in monitors)
            {
                if (m.Handle == currentMonitor)
                {
                    screenLabel = $"Currently on: {m.Name}";
                    break;
                }
            }

            var screenButtons = new List<ScreenButtonInfo>();
            foreach (var m in monitors)
            {
                screenButtons.Add(new ScreenButtonInfo
                {
                    Label = $"{m.DeviceNumber}",
                    IsEnabled = m.Handle != currentMonitor,
                    MonitorHandle = m.Handle,
                    WindowHandle = hwnd
                });
            }

            items.Add(new WindowItem
            {
                Title = title,
                ScreenLabel = screenLabel,
                WindowHandle = hwnd,
                ScreenButtons = screenButtons,
                Icon = GetWindowIcon(hwnd)
            });
        }

        WindowList.ItemsSource = items;
    }

    private void ScreenButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.Tag is ScreenButtonInfo info)
        {
            // Move off the UI thread: SetWindowPos synchronously dispatches to
            // the target window and can block for seconds if it is busy/hung.
            System.Threading.Tasks.Task.Run(() =>
            {
                try { WindowMover.MoveWindowToMonitor(info.WindowHandle, info.MonitorHandle); }
                catch { }
            }).ContinueWith(_ => Dispatcher.Invoke(RefreshContent),
                System.Threading.Tasks.TaskScheduler.Default);
        }
    }

    private void WindowItem_Click(object sender, RoutedEventArgs e)
    {
        // Clicking the row itself does nothing (use the screen buttons)
    }

    private void MoveToCurrentScreen_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.Tag is IntPtr hwnd)
        {
            // "Current screen" = the monitor this popup is displayed on
            var popupHwnd = new WindowInteropHelper(this).Handle;
            var currentMonitor = NativeMethods.MonitorFromWindow(popupHwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
            System.Threading.Tasks.Task.Run(() =>
            {
                try { WindowMover.MoveWindowToMonitor(hwnd, currentMonitor); }
                catch { }
            }).ContinueWith(_ => Dispatcher.Invoke(RefreshContent),
                System.Threading.Tasks.TaskScheduler.Default);
        }
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        CloseIdentifyOverlays();
        Hide();
    }

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            CloseIdentifyOverlays();
            Hide();
        }
    }

    private void ShowIdentifyOverlays()
    {
        CloseIdentifyOverlays();
        var monitors = MonitorHelper.GetAllMonitors();

        foreach (var mon in monitors)
        {
            var overlay = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent,
                ShowInTaskbar = false,
                Topmost = true,
                ShowActivated = false, // never steal activation from the main popup
                ResizeMode = ResizeMode.NoResize,
                SizeToContent = SizeToContent.Manual,
                Width = 360,
                Height = 240,
            };

            var border = new System.Windows.Controls.Border
            {
                Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(220, 0, 0, 0)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(0),
                Child = new System.Windows.Controls.TextBlock
                {
                    Text = mon.DeviceNumber.ToString(),
                    FontSize = 140,
                    FontWeight = FontWeights.Bold,
                    FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
                    Foreground = System.Windows.Media.Brushes.White,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    VerticalAlignment = System.Windows.VerticalAlignment.Center,
                }
            };

            overlay.Content = border;
            overlay.Show();

            // Position in physical pixels on the overlay's own monitor —
            // WPF DIP conversion at placement time does not necessarily use
            // the target monitor's DPI.
            var ohwnd = new System.Windows.Interop.WindowInteropHelper(overlay).Handle;
            uint odpiX = 0;
            if (NativeMethods.GetDpiForMonitor(mon.Handle, 0, out odpiX, out _) != 0 || odpiX == 0)
                odpiX = 96;
            double oscale = odpiX / 96.0;
            int ow = (int)(overlay.Width * oscale);
            int oh = (int)(overlay.Height * oscale);
            NativeMethods.SetWindowPos(ohwnd, IntPtr.Zero,
                mon.Bounds.Left + (int)(24 * oscale),
                mon.Bounds.Bottom - oh - (int)(24 * oscale),
                0, 0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

            _identifyWindows.Add(overlay);
        }
    }

    private void CloseIdentifyOverlays()
    {
        // Copy + clear first: closing an overlay can deactivate this window,
        // which re-enters this method via Window_Deactivated.
        var list = _identifyWindows.ToList();
        _identifyWindows.Clear();
        foreach (var w in list)
        {
            try { w.Close(); } catch { }
        }
    }

    public void ShowCentered()
    {
        ApplyWindowsTheme();
        RefreshContent();

        // Center on the monitor where the mouse cursor is
        NativeMethods.GetCursorPos(out var pt);
        var hMon = NativeMethods.MonitorFromPoint(pt, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var mi = new NativeMethods.MONITORINFO { cbSize = 40 };
        NativeMethods.GetMonitorInfo(hMon, ref mi);

        // Show the identify overlays BEFORE the main popup: windows shown later
        // get activated, and an overlay stealing activation would trigger this
        // window's Deactivated -> Hide().
        ShowIdentifyOverlays();

        Show();
        UpdateLayout();

        // Position in physical pixels: reading GetDpi before Show would give
        // the system DPI, and WPF's DIP placement does not necessarily use the
        // target monitor's DPI — so compute physical coordinates directly.
        uint dpiX = 0;
        if (NativeMethods.GetDpiForMonitor(hMon, 0, out dpiX, out _) != 0 || dpiX == 0)
            dpiX = 96;
        double scale = dpiX / 96.0;

        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        int physW = (int)(ActualWidth * scale);
        int physH = (int)(ActualHeight * scale);
        int physX = mi.rcWork.Left + (mi.rcWork.Width - physW) / 2;
        int physY = mi.rcWork.Top + (mi.rcWork.Height - physH) / 2;
        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, physX, physY, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

        Activate();
        Focus();
    }

    private static ImageSource? GetWindowIcon(IntPtr hwnd)
    {
        IntPtr hIcon = IntPtr.Zero;

        // Try WM_GETICON (small icon)
        NativeMethods.SendMessageTimeoutW(hwnd, (uint)NativeMethods.WM_GETICON,
            (IntPtr)NativeMethods.ICON_SMALL2, IntPtr.Zero,
            NativeMethods.SMTO_ABORTIFHUNG, 100, out hIcon);

        if (hIcon == IntPtr.Zero)
            NativeMethods.SendMessageTimeoutW(hwnd, (uint)NativeMethods.WM_GETICON,
                (IntPtr)NativeMethods.ICON_SMALL, IntPtr.Zero,
                NativeMethods.SMTO_ABORTIFHUNG, 100, out hIcon);

        if (hIcon == IntPtr.Zero)
            NativeMethods.SendMessageTimeoutW(hwnd, (uint)NativeMethods.WM_GETICON,
                (IntPtr)NativeMethods.ICON_BIG, IntPtr.Zero,
                NativeMethods.SMTO_ABORTIFHUNG, 100, out hIcon);

        // Fallback to class icon
        if (hIcon == IntPtr.Zero)
            hIcon = NativeMethods.GetClassLongPtrW(hwnd, NativeMethods.GCL_HICONSM);
        if (hIcon == IntPtr.Zero)
            hIcon = NativeMethods.GetClassLongPtrW(hwnd, NativeMethods.GCL_HICON);

        if (hIcon == IntPtr.Zero)
            return null;

        try
        {
            return Imaging.CreateBitmapSourceFromHIcon(hIcon,
                Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        }
        catch
        {
            return null;
        }
    }

    private static List<(IntPtr hwnd, string title)> GetTopLevelWindows()
    {
        var result = new List<(IntPtr, string)>();
        var buffer = new char[256];

        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hWnd))
                return true;

            int exStyle = NativeMethods.GetWindowLongA(hWnd, NativeMethods.GWL_EXSTYLE);
            if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0)
                return true;

            var owner = NativeMethods.GetWindow(hWnd, NativeMethods.GW_OWNER);
            if (owner != IntPtr.Zero)
                return true;

            int len = NativeMethods.GetWindowText(hWnd, buffer, buffer.Length);
            if (len <= 0)
                return true;

            // Skip cloaked windows (hidden UWP/suspended apps)
            if (NativeMethods.DwmGetWindowAttribute(hWnd, NativeMethods.DWMWA_CLOAKED,
                    out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                return true;

            // Skip windows with zero or tiny size (tray-only apps)
            NativeMethods.GetWindowRect(hWnd, out var rect);
            if (rect.Width <= 1 || rect.Height <= 1)
                return true;

            result.Add((hWnd, new string(buffer, 0, len)));
            return true;
        }, IntPtr.Zero);

        return result;
    }
}

internal sealed class WindowItem
{
    public string Title { get; init; } = "";
    public string ScreenLabel { get; init; } = "";
    public IntPtr WindowHandle { get; init; }
    public List<ScreenButtonInfo> ScreenButtons { get; init; } = [];
    public ImageSource? Icon { get; init; }
}

internal sealed class ScreenButtonInfo
{
    public string Label { get; init; } = "";
    public bool IsEnabled { get; init; }
    public IntPtr MonitorHandle { get; init; }
    public IntPtr WindowHandle { get; init; }
}
