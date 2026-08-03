# CoreShift

**Zero-polling CPU core affinity, process priority and EcoQoS (Efficiency Mode) manager for games and background apps.**

CoreShift pins games to your performance cores, demotes background chatter (Chrome, Discord, …) onto efficiency cores in EcoQoS, and raises/lowers process priority — automatically, on every window-focus change, with **0% CPU while idle** and an **8 MB private-memory** footprint. Ships as a single ~22 MB Native AOT executable.

![icon](src/CoreShift.App/Assets/app-icon-256.png)

## How it works

CoreShift never polls. A single Win32 event hook (`SetWinEventHook` → `EVENT_SYSTEM_FOREGROUND`) wakes the process only when the foreground window actually changes. On each focus change it:

1. resolves the focused window's process (`GetWindowThreadProcessId`),
2. matches it against your target list (`config.json`, next to the exe),
3. applies the configured policy — CPU affinity mask (`SetProcessAffinityMask`), EcoQoS (`SetProcessInformation` → `PROCESS_POWER_THROTTLING_EXECUTION_SPEED`) and priority class (`SetPriorityClass`) — using handles opened with only `PROCESS_SET_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION` (anti-cheat friendly),
4. restores everything it changed when the app loses focus or on exit.

## Features

- **Hybrid CPU aware** — Intel P/E-core masks are derived from `GetLogicalProcessorInformationEx` efficiency classes (P = 1, E = 0); uniform CPUs (AMD) fall back to first-half/second-half.
- **Per-app policy** — All Cores / P-Cores only / E-Cores only, EcoQoS, High priority, "demote when backgrounded".
- **Background demotion** — while a tuned game is focused, configured background apps are pushed to E-cores + EcoQoS (undoes itself when the game loses focus).
- **Silent tray-first UI** — pitch-dark theme (`#0F0F11`), owner-drawn menu, custom-drawn check boxes.
- **Dashboard opens on interactive launch** — launched by Windows startup (auto-start) it stays silent in the tray (`--autostart`).
- **Auto-start toggle** — `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` with `--autostart`.
- **Native AOT** — no JIT, fast startup, one file.
- **File logging** — `Microsoft.Extensions.Logging` abstractions backed by Serilog (`logs\coreshift-YYYYMMDD.log`, 7-day rolling).

## Repository layout

```
CoreShift.slnx                     # XML solution (VS 17.13+ / .NET SDK 9.0.200+)
Directory.Build.props              # shared build settings
publish.ps1                        # one-command Native AOT publish (handles vswhere PATH)
src/
  CoreShift.Core/                  # engine — UI-free, AOT/trim compatible class library
    NativeMethods.cs               # all DllImports, structs, constants
    CoreManager.cs                 # policy engine: affinity/EcoQoS/priority + restore tracking
    WindowHook.cs                  # zero-polling SetWinEventHook wrapper
    AppConfig.cs                   # config model + source-gen JSON persistence
  CoreShift.App/                   # WinForms front-end (namespace CoreShift.App, exe: CoreShift.exe)
    Program.cs                     # entry point, tray context, logging pipeline, self-test
    MainForm.cs                    # dashboard
    DarkRenderer.cs                # dark theme palette + menu renderer
    IconFactory.cs                 # embedded icon loader
    Assets/                        # icon + SVG source (Bootstrap Icons, MIT)
```

## Build & publish

Requirements: Windows 10/11 x64, .NET SDK 9, and Visual Studio with the *Desktop development with C++* workload (needed for the AOT linker).

```powershell
# developer build
dotnet build .\CoreShift.slnx

# native AOT publish → .\publish\CoreShift.exe
.\publish.ps1
# or manually:
dotnet publish .\CoreShift.slnx -c Release -r win-x64 --self-contained true -o .\publish
```

> The publish script prepends `C:\Program Files (x86)\Microsoft Visual Studio\Installer` to `PATH`
> so MSBuild can locate `link.exe` via `vswhere.exe` (required by the ILCompiler on some setups).

Verification:

```powershell
.\publish\CoreShift.exe --selftest   # topology, affinity, EcoQoS, priority, JSON round-trip
.\publish\CoreShift.exe --autostart  # start silently in the tray (used by auto-start)
```

## config.json

Created next to the executable on first save. See [config.example.json](config.example.json).

```json
{
  "Version": 1,
  "Targets": [
    {
      "Name": "game.exe",                // matched case-insensitively, with or without .exe
      "Path": "C:\\Games\\game.exe",     // optional full path (custom adds)
      "Affinity": "PCoresOnly",          // "None" | "AllCores" | "PCoresOnly" | "ECoresOnly"
      "EcoQoS": false,                   // Efficiency Mode (Win10 1809+)
      "HighPriority": true,
      "DemoteInBackground": false        // push to E-cores + EcoQoS while a tuned app is focused
    },
    {
      "Name": "chrome.exe",
      "Affinity": "None",
      "DemoteInBackground": true
    }
  ],
  "BackgroundEcoQoS": true,              // global: demoted apps get EcoQoS
  "BackgroundECores": true               // global: demoted apps pinned to E-cores
}
```

## Known limitations

- Masks are 64-bit — up to 64 logical processors in processor group 0 are covered.
- **Console apps** (cmd, PowerShell windows) belong to `conhost.exe` at the Win32 level, so their windows are not matched as foreground targets. Games and GUI apps are unaffected. Use the dashboard's *Add Custom Exe* if a borderless/console app needs manual tuning.
- EcoQoS requires Windows 10 1809+; hybrid P/E detection requires the efficiency-class data exposed by Windows 11 (falls back to first/second-half split otherwise).
- Process priority is raised to `HIGH_PRIORITY_CLASS` and restored to the previously captured class; if the class cannot be read the restore uses `NORMAL`.

## Credits

- App icon: **Bootstrap Icons** — [`cpu-fill`](https://icons.getbootstrap.com/icons/cpu-fill/), released under the [MIT License](https://github.com/twbs/icons/blob/main/LICENSE).

## License

[MIT](LICENSE) — © bberka
