using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Serilog;
using ILogger = Microsoft.Extensions.Logging.ILogger; // disambiguate from Serilog.ILogger
using CoreShift;

namespace CoreShift.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] is "--selftest")
        {
            RunSelfTest();
            return;
        }

        // Interactive launch → open the dashboard. When Windows starts the app via the
        // Run key (or the user passes --autostart/--hidden) it stays silent in the tray.
        bool showDashboard = !args.Any(a => a is "--autostart" or "--hidden");

        ApplicationConfiguration.Initialize(); // high-DPI, visual styles, text rendering defaults
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        // ---- logging: Microsoft.Extensions.Logging abstractions + Serilog file sink ----
        var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
        try { Directory.CreateDirectory(logDir); }
        catch { /* read-only location → logging degrades to nothing */ }

        Serilog.Log.Logger = new Serilog.LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                Path.Combine(logDir, "coreshift-.log"),
                rollingInterval: Serilog.RollingInterval.Day,
                retainedFileCountLimit: 7,
                fileSizeLimitBytes: 5_000_000,
                rollOnFileSizeLimit: true)
            .CreateLogger();

        using var loggerFactory = LoggerFactory.Create(builder => builder.AddSerilog(dispose: true));
        var appLogger = loggerFactory.CreateLogger("CoreShift");

        Application.ThreadException += (_, e) =>
        {
            appLogger.LogCritical(e.Exception, "Unhandled UI-thread exception");
            CrashDialog("Unexpected error", e.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            appLogger.LogCritical(e.ExceptionObject as Exception, "Unhandled exception");
            CrashDialog("Fatal error", e.ExceptionObject as Exception);
        };

        try
        {
            using var app = new TrayApplicationContext(loggerFactory, showDashboard);
            Application.Run(app);
        }
        catch (Exception ex)
        {
            appLogger.LogCritical(ex, "Startup failed");
            CrashDialog("CoreShift failed to start", ex);
        }
        finally
        {
            Serilog.Log.CloseAndFlush();
        }
    }

    private static void CrashDialog(string title, Exception? ex)
    {
        try { MessageBox.Show(ex?.ToString() ?? title, title, MessageBoxButtons.OK, MessageBoxIcon.Error); }
        catch { /* no UI available */ }
    }

    /// <summary>
    /// Headless verification: topology detection, affinity apply/restore on our own process,
    /// EcoQoS round-trip and config.json serialization. Exit code 0 = all checks passed.
    /// </summary>
    private static void RunSelfTest()
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* console may not support it */ }
        Console.WriteLine($"CoreShift self-test — PID {Environment.ProcessId}");
        Console.WriteLine($"Topology: {CpuTopology.Description}");
        Console.WriteLine($"  P-Core mask: 0x{CpuTopology.PCoreMask:X16}");
        Console.WriteLine($"  E-Core mask: 0x{CpuTopology.ECoreMask:X16}");

        int failures = 0;

        using (var h = NativeMethods.OpenProcess(
                   NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION | NativeMethods.PROCESS_SET_INFORMATION,
                   false, (uint)Environment.ProcessId))
        {
            if (h.IsInvalid) { Console.WriteLine("FAIL: OpenProcess(self) failed"); failures++; }
            else
            {
                // 1) Affinity: apply P-core mask, verify, restore to previous.
                if (NativeMethods.GetProcessAffinityMask(h, out ulong before, out ulong systemMask))
                {
                    ulong target = CpuTopology.PCoreMask & systemMask;
                    Console.WriteLine($"  System mask: 0x{systemMask:X16}, before: 0x{before:X16}, target: 0x{target:X16}");
                    if (target != 0 && NativeMethods.SetProcessAffinityMask(h, target))
                    {
                        NativeMethods.GetProcessAffinityMask(h, out ulong after, out _);
                        Console.WriteLine($"  Affinity apply: 0x{after:X16} ({(after == target ? "OK" : "MISMATCH")})");
                        if (after != target) failures++;
                        if (!NativeMethods.SetProcessAffinityMask(h, before)) { Console.WriteLine("FAIL: affinity restore"); failures++; }
                    }
                    else { Console.WriteLine("FAIL: SetProcessAffinityMask (0 cores on this CPU?)"); failures++; }
                }
                else { Console.WriteLine("FAIL: GetProcessAffinityMask"); failures++; }

                // 2) EcoQoS: enable then disable.
                var state = new NativeMethods.PROCESS_POWER_THROTTLING_STATE
                {
                    Version = NativeMethods.PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                    ControlMask = NativeMethods.PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                    StateMask = NativeMethods.PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                };
                bool eco = NativeMethods.SetProcessInformation(h, NativeMethods.ProcessPowerThrottling, ref state,
                    (uint)Marshal.SizeOf<NativeMethods.PROCESS_POWER_THROTTLING_STATE>());
                state.StateMask = 0;
                bool ecoOff = NativeMethods.SetProcessInformation(h, NativeMethods.ProcessPowerThrottling, ref state,
                    (uint)Marshal.SizeOf<NativeMethods.PROCESS_POWER_THROTTLING_STATE>());
                Console.WriteLine($"  EcoQoS set/clear: {(eco && ecoOff ? "OK" : "NOT SUPPORTED (older Windows?)")}");
                if (!eco || !ecoOff) failures++;

                // 3) Priority class round-trip.
                uint pc = NativeMethods.GetPriorityClass(h);
                bool pri = NativeMethods.SetPriorityClass(h, NativeMethods.HIGH_PRIORITY_CLASS)
                           && NativeMethods.SetPriorityClass(h, pc);
                Console.WriteLine($"  Priority round-trip: {(pri ? "OK" : "FAIL")}");
                if (!pri) failures++;
            }
        }

        // 4) Config JSON round-trip through the source-gen serializer.
        try
        {
            var cfg = new AppConfig();
            cfg.Targets!.Add(new Target { Name = "game.exe", Affinity = AffinityMode.PCoresOnly, HighPriority = true });
            cfg.BackgroundECores = true;
            var tmp = Path.Combine(Path.GetTempPath(), $"coreshift_selftest_{Guid.NewGuid():N}.json");
            var copy = JsonSerializer.Serialize(cfg, AppConfigJsonContext.Default.AppConfig);
            File.WriteAllText(tmp, copy);
            var back = JsonSerializer.Deserialize(File.ReadAllText(tmp), AppConfigJsonContext.Default.AppConfig);
            File.Delete(tmp);
            bool ok = back is not null && back.Targets is { Count: 1 } && back.Targets[0].Affinity == AffinityMode.PCoresOnly;
            Console.WriteLine($"  Config JSON round-trip: {(ok ? "OK" : "FAIL")}");
            if (!ok) failures++;
        }
        catch (Exception ex) { Console.WriteLine($"FAIL: config round-trip: {ex.Message}"); failures++; }

        Console.WriteLine(failures == 0 ? "SELFTEST PASSED" : $"SELFTEST FAILED ({failures} failure(s))");
        Environment.ExitCode = failures == 0 ? 0 : 1;
    }
}

/// <summary>
/// Application context: owns the tray icon, the zero-polling foreground hook and the
/// dashboard form. The whole app lives here until Exit is chosen from the tray menu.
/// </summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "CoreShift";

    private readonly NotifyIcon _tray;
    private readonly ForegroundWatcher _watcher;
    private readonly AppConfig _config;
    private readonly CoreManager _manager;
    private readonly ILogger _logger;
    private MainForm? _dashboard;
    private bool _exiting;

    public TrayApplicationContext(ILoggerFactory loggerFactory, bool showDashboard)
    {
        _logger = loggerFactory.CreateLogger("CoreShift.Tray");
        _config = ConfigStore.Load();
        _manager = new CoreManager(_config, loggerFactory.CreateLogger<CoreManager>());

        _watcher = new ForegroundWatcher();
        _watcher.ForegroundChanged += OnForeground;
        _watcher.Start(); // sleeps at 0% CPU until a foreground event arrives
        _logger.LogInformation("Foreground hook installed — sleeping at 0% CPU until focus events arrive");

        _tray = new NotifyIcon
        {
            Icon = IconFactory.AppIcon,
            Text = "CoreShift — CPU core & power manager",
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };
        _tray.DoubleClick += (_, _) => ShowDashboard();

        if (showDashboard) ShowDashboard();
    }

    private void OnForeground()
    {
        // The WinEvent callback is delivered on the message-pump thread; apply policy off the
        // UI thread so the pump never stalls.
        ThreadPool.QueueUserWorkItem(_ => _manager.OnForegroundChanged());
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip
        {
            Renderer = new DarkMenuRenderer(),
            ShowImageMargin = true, // keep the image column so checked items render a visible check box
            Font = new Font("Segoe UI", 9.5f),
        };

        var open = new ToolStripMenuItem("Open Dashboard") { ForeColor = Palette.Text };
        open.Click += (_, _) => ShowDashboard();

        var autoStart = new ToolStripMenuItem("Start with Windows") { ForeColor = Palette.Text, Checked = IsAutoStartEnabled() };
        autoStart.Click += (_, _) =>
        {
            bool on = !IsAutoStartEnabled();
            SetAutoStartEnabled(on);
            autoStart.Checked = on;
            _logger.LogInformation("Auto-start {State}", on ? "enabled" : "disabled");
        };

        var exit = new ToolStripMenuItem("Exit") { ForeColor = Palette.Text };
        exit.Click += (_, _) => Exit();

        menu.Items.Add(open);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(autoStart);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exit);
        return menu;
    }

    private void ShowDashboard()
    {
        if (_exiting) return;
        if (_dashboard is { IsDisposed: false })
        {
            if (_dashboard.WindowState == FormWindowState.Minimized)
                _dashboard.WindowState = FormWindowState.Normal;
            _dashboard.Show();
            _dashboard.Activate();
            return;
        }
        _dashboard = new MainForm(_manager, _config, () => _exiting);
        _dashboard.Show();
    }

    private void Exit()
    {
        if (_exiting) return;
        _exiting = true;

        _watcher.Dispose();
        _manager.Dispose(); // restore every process we touched

        if (_dashboard is { IsDisposed: false }) _dashboard.Close();
        _tray.Visible = false;
        _tray.Dispose();
        _logger.LogInformation("Exited");
        ExitThread();
    }

    // ------------------------------------------------------------------ auto-start (registry)

    private static bool IsAutoStartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(RunValueName) is string;
    }

    private static void SetAutoStartEnabled(bool enable)
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
