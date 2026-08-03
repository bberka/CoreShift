using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CoreShift;

/// <summary>
/// Zero-polling foreground watcher.
///
/// Installs a WinEvent hook for EVENT_SYSTEM_FOREGROUND only. The process then blocks inside
/// the WinForms message pump at exactly 0% CPU until a window focus change actually happens —
/// no timers, no Task.Delay, no Process.GetProcesses polling loops.
///
/// The callback is delivered by the OS to the thread that owns the message pump (UI thread).
/// Keep the event handler fast and offload heavier work.
/// </summary>
public sealed class ForegroundWatcher : IDisposable
{
    private readonly NativeMethods.WinEventDelegate _callback; // kept alive for the hook's lifetime
    private IntPtr _hook;

    /// <summary>Raised whenever the foreground window changes. Delivered on the pump thread.</summary>
    public event Action? ForegroundChanged;

    public ForegroundWatcher()
    {
        _callback = OnWinEvent;
    }

    public void Start()
    {
        if (_hook != IntPtr.Zero) return;

        _hook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero,          // hmodWinEventProc: null → callback lives in this process
            _callback,
            0,                    // idProcess: all processes
            0,                    // idThread: all threads
            NativeMethods.WINEVENT_OUTOFCONTEXT);

        if (_hook == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetWinEventHook failed");
    }

    private void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (eventType != NativeMethods.EVENT_SYSTEM_FOREGROUND) return;
        if (idObject != 0) return; // only top-level window events
        ForegroundChanged?.Invoke();
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
