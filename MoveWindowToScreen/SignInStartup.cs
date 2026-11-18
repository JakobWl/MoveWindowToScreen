using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Win32;
using Windows.ApplicationModel;

namespace MoveWindowToScreen;

internal static class SignInStartup
{
    private const string TaskId = "MoveWindowToScreenStartup";
    private const string SettingsKey = @"Software\MoveWindowToScreen";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "MoveWindowToScreen";
    private const string InitializedValue = "SignInStartupInitialized";
    private const int ErrorInsufficientBuffer = 122;

    private static readonly bool IsPackaged = HasPackageIdentity();

    private static bool HasPackageIdentity()
    {
        uint length = 0;
        return GetCurrentPackageFullName(ref length, IntPtr.Zero) == ErrorInsufficientBuffer;
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern int GetCurrentPackageFullName(ref uint length, IntPtr packageFullName);

    public static void Initialize()
    {
        if (IsPackaged)
            return;

        using var settings = Registry.CurrentUser.CreateSubKey(SettingsKey);
        if (settings.GetValue(InitializedValue) is null)
        {
            SetPortableStartup(true);
            settings.SetValue(InitializedValue, 1, RegistryValueKind.DWord);
        }
    }

    public static async Task<bool> IsEnabledAsync()
    {
        if (IsPackaged)
        {
            var task = await StartupTask.GetAsync(TaskId);
            return task.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
        }

        using var run = Registry.CurrentUser.OpenSubKey(RunKey);
        return run?.GetValue(RunValue) is string;
    }

    public static async Task SetEnabledAsync(bool enabled)
    {
        if (!IsPackaged)
        {
            using var settings = Registry.CurrentUser.CreateSubKey(SettingsKey);
            SetPortableStartup(enabled);
            settings.SetValue(InitializedValue, 1, RegistryValueKind.DWord);
            return;
        }

        var task = await StartupTask.GetAsync(TaskId);
        if (enabled)
            await task.RequestEnableAsync();
        else
            task.Disable();

        if (await IsEnabledAsync() != enabled)
            throw new InvalidOperationException("Change this setting in Windows Settings → Apps → Startup.");
    }

    private static void SetPortableStartup(bool enabled)
    {
        using var run = Registry.CurrentUser.CreateSubKey(RunKey);
        if (!enabled)
        {
            run.DeleteValue(RunValue, throwOnMissingValue: false);
            return;
        }

        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("The application path is unavailable.");
        string command = $"\"{executable}\"";
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            command += $" \"{Path.Combine(AppContext.BaseDirectory, "MoveWindowToScreen.dll")}\"";
        run.SetValue(RunValue, command);
    }
}
