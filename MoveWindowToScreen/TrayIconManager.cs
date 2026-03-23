using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Interop;
using System.Windows.Media;
using Application = System.Windows.Application;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;

namespace MoveWindowToScreen;

/// <summary>
/// Manages the system tray icon and the modern popup UI.
/// Left-click or hotkey (Ctrl+Alt+M) shows the popup.
/// Right-click shows a simple context menu with Exit.
/// </summary>
internal sealed class TrayIconManager : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly SystemMenuInjector _menuInjector;
    private readonly Application _app;
    private MovePopup? _popup;

    // Hotkey
    private const int HOTKEY_ID = 9002;
    private const uint VK_M = 0x4D;
    private HwndSource? _hotkeyWindow;
    private bool _hotkeyRegistered;

    public TrayIconManager(Application app)
    {
        _app = app;

        _notifyIcon = new NotifyIcon
        {
            Text = "Move Window To Screen (Ctrl+Alt+M)",
            Visible = true
        };

        _notifyIcon.Icon = LoadAppIcon();
        _menuInjector = new SystemMenuInjector();

        // Left click → show popup
        _notifyIcon.Click += (_, e) =>
        {
            if (e is MouseEventArgs me && me.Button == MouseButtons.Left)
                ShowPopup();
        };

        // Right click → WPF context menu
        _notifyIcon.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Right)
                ShowTrayContextMenu();
        };

        // Register Ctrl+Alt+M hotkey
        RegisterHotkey();
    }

    private void RegisterHotkey()
    {
        var parameters = new HwndSourceParameters("MoveWindowToScreen_Hotkey")
        {
            Width = 0, Height = 0, WindowStyle = 0
        };
        _hotkeyWindow = new HwndSource(parameters);
        _hotkeyWindow.AddHook(HotkeyWndProc);

        _hotkeyRegistered = NativeMethods.RegisterHotKey(
            _hotkeyWindow.Handle, HOTKEY_ID,
            NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT,
            VK_M);

        if (!_hotkeyRegistered)
        {
            _notifyIcon.ShowBalloonTip(3000, "Move Window To Screen",
                "Could not register Ctrl+Alt+M hotkey. Click the tray icon instead.",
                ToolTipIcon.Warning);
        }
    }

    private IntPtr HotkeyWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_HOTKEY = 0x0312;
        if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
        {
            ShowPopup();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void ShowTrayContextMenu()
    {
        bool isDark = true;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int val)
                isDark = val == 0;
        }
        catch { }

        var bgColor = isDark
            ? System.Windows.Media.Color.FromRgb(0x2C, 0x2C, 0x2C)
            : System.Windows.Media.Color.FromRgb(0xF9, 0xF9, 0xF9);
        var fgBrush = isDark
            ? System.Windows.Media.Brushes.White
            : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1A, 0x1A));
        var hoverBrush = new SolidColorBrush(isDark
            ? System.Windows.Media.Color.FromRgb(0x40, 0x40, 0x40)
            : System.Windows.Media.Color.FromRgb(0xE8, 0xE8, 0xE8));
        var borderColor = isDark
            ? System.Windows.Media.Color.FromRgb(0x45, 0x45, 0x45)
            : System.Windows.Media.Color.FromRgb(0xD0, 0xD0, 0xD0);
        var sepBrush = new SolidColorBrush(borderColor);

        var menuItemStyle = new Style(typeof(MenuItem));
        menuItemStyle.Setters.Add(new Setter(MenuItem.ForegroundProperty, fgBrush));
        menuItemStyle.Setters.Add(new Setter(MenuItem.FontFamilyProperty, new System.Windows.Media.FontFamily("Segoe UI")));
        menuItemStyle.Setters.Add(new Setter(MenuItem.FontSizeProperty, 13.0));
        menuItemStyle.Setters.Add(new Setter(MenuItem.PaddingProperty, new Thickness(12, 8, 24, 8)));
        menuItemStyle.Setters.Add(new Setter(MenuItem.TemplateProperty, CreateMenuItemTemplate(fgBrush, hoverBrush)));

        var separatorStyle = new Style(typeof(Separator));
        separatorStyle.Setters.Add(new Setter(Separator.MarginProperty, new Thickness(8, 4, 8, 4)));
        separatorStyle.Setters.Add(new Setter(Separator.BackgroundProperty, sepBrush));
        separatorStyle.Setters.Add(new Setter(Separator.HeightProperty, 1.0));

        var ctxMenu = new ContextMenu
        {
            Background = new SolidColorBrush(bgColor),
            BorderBrush = new SolidColorBrush(borderColor),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(0, 4, 0, 4),
            HasDropShadow = true,
        };
        ctxMenu.Resources[typeof(MenuItem)] = menuItemStyle;
        ctxMenu.Resources[typeof(Separator)] = separatorStyle;

        // Override the ContextMenu template for rounded corners
        ctxMenu.Template = CreateContextMenuTemplate(bgColor, borderColor);

        var showItem = new MenuItem { Header = "Show Window Mover", FontWeight = FontWeights.SemiBold };
        showItem.Click += (_, _) => ShowPopup();

        var exitItem = new MenuItem { Header = "Exit" };
        exitItem.Click += (_, _) =>
        {
            _notifyIcon.Visible = false;
            _app.Shutdown();
        };

        ctxMenu.Items.Add(showItem);
        ctxMenu.Items.Add(new Separator());
        ctxMenu.Items.Add(exitItem);

        ctxMenu.IsOpen = true;
    }

    private static ControlTemplate CreateContextMenuTemplate(
        System.Windows.Media.Color bgColor, System.Windows.Media.Color borderColor)
    {
        var template = new ControlTemplate(typeof(ContextMenu));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new SolidColorBrush(bgColor));
        border.SetValue(Border.BorderBrushProperty, new SolidColorBrush(borderColor));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        border.SetValue(Border.PaddingProperty, new Thickness(0, 6, 0, 6));
        border.SetValue(UIElement.ClipToBoundsProperty, true);

        var presenter = new FrameworkElementFactory(typeof(StackPanel));
        presenter.SetValue(StackPanel.IsItemsHostProperty, true);
        presenter.SetValue(System.Windows.Input.KeyboardNavigation.DirectionalNavigationProperty,
            System.Windows.Input.KeyboardNavigationMode.Cycle);
        border.AppendChild(presenter);

        template.VisualTree = border;
        return template;
    }

    private static ControlTemplate CreateMenuItemTemplate(
        System.Windows.Media.Brush fgBrush, System.Windows.Media.Brush hoverBrush)
    {
        var template = new ControlTemplate(typeof(MenuItem));
        var border = new FrameworkElementFactory(typeof(Border), "Bd");
        border.SetValue(Border.BackgroundProperty, System.Windows.Media.Brushes.Transparent);
        border.SetValue(Border.PaddingProperty, new Thickness(12, 8, 24, 8));
        border.SetValue(Border.MarginProperty, new Thickness(4, 1, 4, 1));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));

        var text = new FrameworkElementFactory(typeof(ContentPresenter));
        text.SetValue(ContentPresenter.ContentSourceProperty, "Header");
        text.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(text);

        template.VisualTree = border;

        var hoverTrigger = new Trigger
        {
            Property = MenuItem.IsHighlightedProperty,
            Value = true
        };
        hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, hoverBrush, "Bd"));
        template.Triggers.Add(hoverTrigger);

        return template;
    }

    private void ShowPopup()
    {
        if (_popup == null || !_popup.IsLoaded)
        {
            _popup = new MovePopup();
            _popup.Closed += (_, _) => _popup = null;
        }

        if (_popup.IsVisible)
        {
            _popup.Hide();
        }
        else
        {
            _popup.ShowCentered();
        }
    }

    private static Icon LoadAppIcon()
    {
        var exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";
        var iconPath = Path.Combine(exeDir, "Assets", "app.ico");
        if (File.Exists(iconPath))
            return new Icon(iconPath);

        return SystemIcons.Application;
    }

    public void Dispose()
    {
        if (_hotkeyRegistered && _hotkeyWindow != null)
        {
            NativeMethods.UnregisterHotKey(_hotkeyWindow.Handle, HOTKEY_ID);
            _hotkeyRegistered = false;
        }
        _hotkeyWindow?.Dispose();
        _popup?.Close();
        _menuInjector.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
