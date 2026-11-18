using System.Windows;
using System.Windows.Input;

namespace MoveWindowToScreen;

internal partial class ShortcutDialog : Window
{
    private readonly Func<Shortcut, string?> _save;
    private Shortcut _candidate;

    public ShortcutDialog(Shortcut current, Func<Shortcut, string?> save)
    {
        InitializeComponent();
        ShortcutInput.Text = current.ToString();
        _save = save;
        Loaded += (_, _) => ShortcutInput.Focus();
    }

    private void ShortcutInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.Tab or Key.Enter or Key.Escape)
            return;

        e.Handled = true;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return;

        var shortcut = new Shortcut(Keyboard.Modifiers, key);
        SaveButton.IsEnabled = shortcut.IsValid;
        if (!shortcut.IsValid)
        {
            Status.Text = "Use Ctrl, Alt, or Shift with a letter, number, or function key other than F12.";
            return;
        }

        _candidate = shortcut;
        ShortcutInput.Text = shortcut.ToString();
        Status.Text = "";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        string? error = _save(_candidate);
        if (error == null)
            DialogResult = true;
        else
            Status.Text = error;
    }
}
