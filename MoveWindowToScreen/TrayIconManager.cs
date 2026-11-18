using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using MessageBox = System.Windows.MessageBox;
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
/// Left-click or the configured shortcut shows the popup.
/// Right-click opens settings and commands.
/// </summary>
internal sealed class TrayIconManager : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly SystemMenuInjector _menuInjector;
    private readonly Application _app;
    private MovePopup? _popup;

    private const int FirstHotkeyId = 9002;
    private const int SecondHotkeyId = 9003;
    private HwndSource? _hotkeyWindow;
    private int _registeredHotkeyId;
    private Shortcut _shortcut = Shortcut.Default;
    private bool _choosingShortcut;

    public TrayIconManager(Application app)
    {
        _app = app;

        _notifyIcon = new NotifyIcon
        {
            Text = "Move Window To Screen",
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

        var parameters = new HwndSourceParameters("MoveWindowToScreen_Hotkey")
        {
            Width = 0, Height = 0, WindowStyle = 0
        };
        _hotkeyWindow = new HwndSource(parameters);
        _hotkeyWindow.AddHook(HotkeyWndProc);
        _shortcut = Shortcut.Load();
        string? error = RegisterShortcut(_shortcut, save: false);
        if (error != null)
        {
            _app.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (MessageBox.Show(
                    $"{error} Do you want to set a different shortcut? You can also open the app from the tray.",
                    "Keyboard shortcut unavailable", MessageBoxButton.YesNo, MessageBoxImage.Warning) ==
                    MessageBoxResult.Yes)
                    ShowShortcutDialog();
            }));
        }
    }

    private string? RegisterShortcut(Shortcut shortcut, bool save)
    {
        if (_registeredHotkeyId != 0 && _shortcut == shortcut)
            return null;

        var hwnd = _hotkeyWindow!.Handle;
        int nextId = _registeredHotkeyId == FirstHotkeyId ? SecondHotkeyId : FirstHotkeyId;
        uint key = (uint)System.Windows.Input.KeyInterop.VirtualKeyFromKey(shortcut.Key);
        if (!NativeMethods.RegisterHotKey(hwnd, nextId, shortcut.NativeModifiers, key))
        {
            int error = Marshal.GetLastPInvokeError();
            return error == 1409
                ? $"{shortcut} is already in use."
                : $"Windows could not register {shortcut} (error {error}).";
        }

        if (save)
        {
            try
            {
                shortcut.Save();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                NativeMethods.UnregisterHotKey(hwnd, nextId);
                return "The shortcut could not be saved. Your previous shortcut is still selected.";
            }
        }

        if (_registeredHotkeyId != 0)
            NativeMethods.UnregisterHotKey(hwnd, _registeredHotkeyId);

        _registeredHotkeyId = nextId;
        _shortcut = shortcut;
        _notifyIcon.Text = $"Move Window To Screen ({shortcut})";
        return null;
    }

    private void ShowShortcutDialog()
    {
        _choosingShortcut = true;
        try
        {
            new ShortcutDialog(_shortcut, shortcut => RegisterShortcut(shortcut, save: true)).ShowDialog();
        }
        finally
        {
            _choosingShortcut = false;
        }
    }

    private IntPtr HotkeyWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_HOTKEY = 0x0312;
        if (msg == WM_HOTKEY && _registeredHotkeyId != 0 && wParam.ToInt32() == _registeredHotkeyId)
        {
            if (!_choosingShortcut)
                ShowPopup();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private async void ShowTrayContextMenu()
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

        var shortcutItem = new MenuItem { Header = "Change keyboard shortcut…" };
        shortcutItem.Click += (_, _) => ShowShortcutDialog();

        var startupItem = new MenuItem();
        try
        {
            bool startupEnabled = await SignInStartup.IsEnabledAsync();
            startupItem.Header = startupEnabled ? "Start at sign-in (on)" : "Start at sign-in (off)";
            startupItem.Click += async (_, _) =>
            {
                try
                {
                    await SignInStartup.SetEnabledAsync(!startupEnabled);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, "Could not change startup setting",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            };
        }
        catch (Exception ex)
        {
            startupItem.Header = "Start at sign-in (unavailable)";
            startupItem.Click += (_, _) => MessageBox.Show(ex.Message, "Startup setting unavailable",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        ctxMenu.Items.Add(showItem);
        ctxMenu.Items.Add(shortcutItem);
        ctxMenu.Items.Add(startupItem);
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
        var resource = Application.GetResourceStream(new Uri("Assets/app.ico", UriKind.Relative));
        if (resource == null)
            return SystemIcons.Application;

        using var stream = resource.Stream;
        using var icon = new Icon(stream);
        return (Icon)icon.Clone();
    }

    public void Dispose()
    {
        if (_registeredHotkeyId != 0 && _hotkeyWindow != null)
            NativeMethods.UnregisterHotKey(_hotkeyWindow.Handle, _registeredHotkeyId);
        _hotkeyWindow?.Dispose();
        _popup?.Close();
        _menuInjector.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
