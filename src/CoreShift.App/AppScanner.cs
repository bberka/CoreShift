using System.Diagnostics;
using System.Drawing;
using System.Text;
using CoreShift;

namespace CoreShift.App;

/// <summary>One enumerable GUI application.</summary>
public sealed record AppInfo(string Name, string Path, bool Running);

/// <summary>
/// Enumerates running GUI applications (visible top-level windows) with their full paths
/// and native exe icons. Shared by the dashboard and the add-application dialog.
/// </summary>
internal static class AppScanner
{
    public static List<AppInfo> EnumerateGuiApps()
    {
        var result = new List<AppInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int self = Environment.ProcessId;

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd)) return true;
            if (NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER) != IntPtr.Zero) return true; // owned windows skip
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0 || pid == self) return true;

            string? path = null;
            using (var h = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid))
            {
                if (!h.IsInvalid)
                {
                    var sb = new StringBuilder(1024);
                    uint len = (uint)sb.Capacity;
                    if (NativeMethods.QueryFullProcessImageName(h, 0, sb, ref len))
                        path = sb.ToString();
                }
            }
            if (path is null) return true;

            var name = Path.GetFileName(path);
            if (CoreManager.IsProtectedProcess((int)pid, name)) return true;
            if (!seen.Add(AppIdentity.NormalizeName(name))) return true; // one row per app

            result.Add(new AppInfo(name, path, Running: true));
            return true;
        }, IntPtr.Zero);

        return result;
    }

    /// <summary>Native .exe icon, falling back to the generic application icon.</summary>
    public static Icon LoadAppIcon(AppInfo e)
    {
        try
        {
            if (!string.IsNullOrEmpty(e.Path) && File.Exists(e.Path))
            {
                var ico = Icon.ExtractAssociatedIcon(e.Path);
                if (ico is not null) return ico;
            }
        }
        catch { /* fall through to generic icon */ }
        return (Icon)SystemIcons.Application.Clone();
    }

    /// <summary>True if any process with the given name (with or without .exe) is running.</summary>
    public static bool IsProcessRunning(string nameOrPath)
    {
        try { return Process.GetProcessesByName(AppIdentity.NormalizeName(nameOrPath)).Length > 0; }
        catch { return false; }
    }
}
