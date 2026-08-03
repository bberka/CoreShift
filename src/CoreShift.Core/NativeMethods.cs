using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CoreShift;

/// <summary>
/// Central Win32 interop surface. Signatures, constants and structs only — no business logic.
/// Every process handle is opened with the minimal privilege set required
/// (PROCESS_SET_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION) to avoid access violations
/// and anti-cheat conflicts. Handles are returned as <see cref="SafeProcessHandle"/> so they are
/// always closed exactly once (CloseHandle) even on exceptions.
/// </summary>
internal static class NativeMethods
{
    // ------------------------------------------------------------------ user32: window events (zero-polling hook)
    public delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    // ------------------------------------------------------------------ user32: window enumeration (dashboard)
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    public const uint GW_OWNER = 4;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    // ------------------------------------------------------------------ kernel32: process handles
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint PROCESS_SET_INFORMATION = 0x0200;
    public const uint PROCESS_QUERY_INFORMATION = 0x0400;
    public const uint PROCESS_SET_LIMITED_INFORMATION = 0x2000;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern SafeProcessHandle OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool QueryFullProcessImageName(SafeProcessHandle hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);

    // ------------------------------------------------------------------ kernel32: CPU affinity
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetProcessAffinityMask(SafeProcessHandle hProcess, out ulong lpProcessAffinityMask, out ulong lpSystemAffinityMask);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetProcessAffinityMask(SafeProcessHandle hProcess, ulong dwProcessAffinityMask);

    // ------------------------------------------------------------------ kernel32: process priority
    public const uint HIGH_PRIORITY_CLASS = 0x00000080;
    public const uint ABOVE_NORMAL_PRIORITY_CLASS = 0x00008000;
    public const uint NORMAL_PRIORITY_CLASS = 0x00000020;
    public const uint BELOW_NORMAL_PRIORITY_CLASS = 0x00004000;
    public const uint IDLE_PRIORITY_CLASS = 0x00000040;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetPriorityClass(SafeProcessHandle hProcess, uint dwPriorityClass);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint GetPriorityClass(SafeProcessHandle hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetProcessPriorityBoost(SafeProcessHandle hProcess, [MarshalAs(UnmanagedType.Bool)] bool bDisablePriorityBoost);

    // ------------------------------------------------------------------ kernel32: EcoQoS / power throttling
    public const int ProcessPowerThrottling = 4; // PROCESS_INFORMATION_CLASS::ProcessPowerThrottling
    public const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
    public const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetProcessInformation(SafeProcessHandle hProcess, int ProcessInformationClass, ref PROCESS_POWER_THROTTLING_STATE ProcessInformation, uint ProcessInformationSize);

    // ------------------------------------------------------------------ kernel32: CPU topology (hybrid P/E detection)
    public enum LOGICAL_PROCESSOR_RELATIONSHIP : int
    {
        ProcessorCore = 0,
        NumaNode = 1,
        Cache = 2,
        ProcessorPackage = 3,
        Group = 4,
        ProcessorDie = 5,
        NumaNodeEx = 6,
        ProcessorModule = 7,
    }

    public const int MaxGroupCount = 8;

    /// <summary>Union head only; GroupMask[] is read as raw bytes (layouts differ across Windows versions).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct PROCESSOR_RELATIONSHIP
    {
        public byte Flags;
        public byte EfficiencyClass;
        public byte GroupCount;
        public fixed byte Reserved[20];
        public fixed byte GroupMask[MaxGroupCount * 16]; // GROUP_AFFINITY is 16 bytes each
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX
    {
        public int Relationship;
        public uint Size;
        public PROCESSOR_RELATIONSHIP Processor; // union start — only ProcessorCore entries are interpreted
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern unsafe bool GetLogicalProcessorInformationEx(int RelationshipType, SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX* Buffer, ref uint ReturnedLength);

    // ------------------------------------------------------------------ helpers
    public static uint GetForegroundPid()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
        return pid;
    }
}
