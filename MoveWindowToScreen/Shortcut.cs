using System.Windows.Input;
using Microsoft.Win32;

namespace MoveWindowToScreen;

internal readonly record struct Shortcut(ModifierKeys Modifiers, Key Key)
{
    private const string SettingsKey = @"Software\MoveWindowToScreen";
    private const string ValueName = "Shortcut";
    public static readonly Shortcut Default = new(ModifierKeys.Control | ModifierKeys.Alt, Key.M);

    public bool IsValid =>
        Modifiers != ModifierKeys.None &&
        (Modifiers & ~(ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift)) == 0 &&
        (Key is >= Key.A and <= Key.Z or >= Key.D0 and <= Key.D9 or >= Key.F1 and <= Key.F11
            or >= Key.F13 and <= Key.F24);

    public static Shortcut Load()
    {
        using var key = Registry.CurrentUser.OpenSubKey(SettingsKey);
        if (key?.GetValue(ValueName) is string saved)
        {
            var parts = saved.Split(',');
            if (parts.Length == 2 && Enum.TryParse<ModifierKeys>(parts[0], out var modifiers) &&
                Enum.TryParse<Key>(parts[1], out var keyCode))
            {
                var shortcut = new Shortcut(modifiers, keyCode);
                if (shortcut.IsValid)
                    return shortcut;
            }
        }
        return Default;
    }

    public void Save()
    {
        using var key = Registry.CurrentUser.CreateSubKey(SettingsKey);
        key.SetValue(ValueName, $"{(int)Modifiers},{Key}");
    }

    public uint NativeModifiers =>
        ((Modifiers & ModifierKeys.Control) != 0 ? NativeMethods.MOD_CONTROL : 0) |
        ((Modifiers & ModifierKeys.Alt) != 0 ? NativeMethods.MOD_ALT : 0) |
        ((Modifiers & ModifierKeys.Shift) != 0 ? NativeMethods.MOD_SHIFT : 0) |
        NativeMethods.MOD_NOREPEAT;

    public override string ToString()
    {
        string mainKey = Key is >= Key.D0 and <= Key.D9 ? ((int)Key - (int)Key.D0).ToString() : Key.ToString();
        return $"{new ModifierKeysConverter().ConvertToInvariantString(Modifiers)}+{mainKey}";
    }
}
