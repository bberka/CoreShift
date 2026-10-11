using Microsoft.Win32;

namespace CoreShift.App;

/// <summary>
/// Windows auto-start via the HKCU Run key. The value launches CoreShift with --autostart
/// so it starts silently in the tray (the dashboard only opens on interactive launches).
/// </summary>
internal static class AutoStart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "CoreShift";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(RunValueName) is string;
    }

    public static void SetEnabled(bool enable)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (key is null) return;
        if (enable)
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path)) key.SetValue(RunValueName, $"\"{path}\" --autostart");
        }
        else
        {
            key.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
    }
}
