using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;

namespace CoreShift;

/// <summary>
/// Applies CPU affinity, EcoQoS (Efficiency Mode) and process priority based on the configured
/// target list and the current foreground process. Driven purely by window focus events —
/// never by polling. Every handle is opened with PROCESS_SET_INFORMATION |
/// PROCESS_QUERY_LIMITED_INFORMATION and closed exactly once via SafeProcessHandle.
///
/// Logging is bound through the standard Microsoft.Extensions.Logging abstraction
/// (ILogger&lt;CoreManager&gt;); a NullLogger is used when none is provided.
/// </summary>
public sealed class CoreManager : IDisposable
{
    private static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "coreshift", "csrss", "wininit", "winlogon", "services", "lsass", "smss", "dwm",
        "fontdrvhost", "system", "registry", "memory compression", "secure system", "audiodg",
    };

    private readonly object _gate = new();
    private readonly Dictionary<int, AppliedState> _applied = new(); // pid → what we set
    private readonly ILogger<CoreManager> _logger;
    private AppConfig _config;
    private bool _disposed;

    /// <summary>Tracks what this process modified so it can be restored later.</summary>
    private sealed class AppliedState
    {
        public int Pid;
        public string Name = "";
        public ulong? PriorAffinity;   // captured before first touch (best-effort)
        public uint? PriorPriority;    // captured before first touch (best-effort)
        public bool Affinity;
        public bool Eco;
        public bool HighPri;
    }

    private sealed record Adjustment(bool Restore, ulong? Affinity, bool? Eco, bool? HighPri);

    public CoreManager(AppConfig config, ILogger<CoreManager>? logger = null)
    {
        _config = config;
        _logger = logger ?? NullLogger<CoreManager>.Instance;
    }

    /// <summary>Swap in a new configuration (called from the dashboard on Save).</summary>
    public void SetConfig(AppConfig config)
    {
        lock (_gate) { _config = config; }
        _logger.LogDebug("Configuration updated: {TargetCount} target(s), backgroundEco={BgEco}, backgroundECores={BgECores}",
            config.Targets?.Count ?? 0, config.BackgroundEcoQoS, config.BackgroundECores);
    }

    /// <summary>Recompute policy for the current foreground window (event-driven).</summary>
    public void OnForegroundChanged() => Recompute(forceAll: false);

    /// <summary>Apply the whole config to every running target right now.</summary>
    public void ApplyNow() => Recompute(forceAll: true);

    /// <summary>Restore every process we touched. Called on clean shutdown.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _logger.LogInformation("Shutting down — restoring {Count} modified process(es)", _applied.Count);
            foreach (var kvp in _applied.ToList()) RestorePid(kvp.Key, kvp.Value);
            _applied.Clear();
        }
    }

    public static bool IsProtectedProcess(int pid, string name)
    {
        if (pid <= 4) return true; // Idle & System
        return ProtectedNames.Contains(AppIdentity.NormalizeName(name));
    }

    // ------------------------------------------------------------------ policy recompute

    private void Recompute(bool forceAll)
    {
        Dictionary<int, Adjustment> desired;
        lock (_gate)
        {
            if (_disposed) return;
            desired = ComputeDesired(_config, forceAll);
        }

        _logger.LogDebug("Recompute: {Desired} desired adjustment(s), {Tracked} tracked process(es)",
            desired.Count, _applied.Count);

        // Restore tracked processes that are no longer desired (game unfocused, config removed, …).
        foreach (var kvp in _applied.ToList())
        {
            if (desired.TryGetValue(kvp.Key, out var adj) && !adj.Restore) continue;
            RestorePid(kvp.Key, kvp.Value);
        }

        // Apply the new desired state.
        foreach (var kvp in desired)
        {
            if (kvp.Value.Restore) continue;
            ApplyPid(kvp.Key, kvp.Value);
        }
    }

    private static Dictionary<int, Adjustment> ComputeDesired(AppConfig cfg, bool forceAll)
    {
        var desired = new Dictionary<int, Adjustment>();
        var targets = (cfg.Targets ?? new List<Target>())
            .Where(t => !string.IsNullOrWhiteSpace(t.Name)).ToList();
        if (targets.Count == 0) return desired;

        // Foreground context. "gameActive" means a tuned target owns the foreground, which
        // is the only situation where background demotion is supposed to engage.
        string? fgName = null;
        Target? fgTarget = null;
        bool gameActive = forceAll;

        if (!forceAll)
        {
            fgName = TryGetProcessName(NativeMethods.GetForegroundPid());
            fgTarget = fgName is null ? null : targets.FirstOrDefault(t => t.Matches(fgName));
            gameActive = fgTarget is { HasTuning: true };
        }

        var byName = IndexRunningProcesses();

        foreach (var t in targets)
        {
            var key = AppIdentity.NormalizeName(t.Name);
            if (!byName.TryGetValue(key, out var procs)) continue;

            foreach (var p in procs)
            {
                try
                {
                    if (IsProtectedProcess(p.Id, key)) continue;

                    Adjustment adj;
                    if (forceAll)
                    {
                        adj = t.HasTuning
                            ? TuningAdjustment(t)
                            : t.DemoteInBackground
                                ? DemotionAdjustment(cfg)
                                : new Adjustment(true, null, null, null);
                    }
                    else if (gameActive && ReferenceEquals(fgTarget, t))
                    {
                        adj = TuningAdjustment(t);
                    }
                    else if (gameActive && t.DemoteInBackground)
                    {
                        adj = DemotionAdjustment(cfg);
                    }
                    else
                    {
                        adj = new Adjustment(true, null, null, null);
                    }

                    desired[p.Id] = adj;
                }
                finally
                {
                    p.Dispose();
                }
            }
        }
        return desired;
    }

    private static Dictionary<string, List<Process>> IndexRunningProcesses()
    {
        var byName = new Dictionary<string, List<Process>>(StringComparer.OrdinalIgnoreCase);
        Process[] procs;
        try { procs = Process.GetProcesses(); }
        catch { return byName; }

        foreach (var p in procs)
        {
            string name;
            try { name = p.ProcessName; }
            catch { p.Dispose(); continue; }

            var key = AppIdentity.NormalizeName(name);
            if (!byName.TryGetValue(key, out var list)) byName[key] = list = new List<Process>();
            list.Add(p);
        }
        return byName;
    }

    // ------------------------------------------------------------------ adjustments

    private static Adjustment TuningAdjustment(Target t)
    {
        ulong? aff = t.Affinity switch
        {
            AffinityMode.AllCores => ulong.MaxValue, // clamped to system mask on apply
            AffinityMode.PCoresOnly => CpuTopology.PCoreMask,
            AffinityMode.ECoresOnly => CpuTopology.ECoreMask,
            _ => null,
        };
        return new Adjustment(false, aff, t.EcoQoS, t.HighPriority);
    }

    private static Adjustment DemotionAdjustment(AppConfig cfg)
    {
        // Always set both facets so toggling a policy off actually clears the previous state.
        ulong? aff = cfg.BackgroundECores ? CpuTopology.ECoreMask : ulong.MaxValue;
        return new Adjustment(false, aff, cfg.BackgroundEcoQoS, null);
    }

    // ------------------------------------------------------------------ apply / restore

    private void ApplyPid(int pid, Adjustment adj)
    {
        using var h = OpenLimited(pid);
        if (h.IsInvalid) { _applied.Remove(pid); return; }

        if (!_applied.TryGetValue(pid, out var st))
        {
            st = new AppliedState
            {
                Pid = pid,
                Name = TryGetProcessName((uint)pid) ?? pid.ToString(),
                PriorAffinity = TryGetAffinity(h),
                PriorPriority = TryGetPriority(h),
            };
            _applied[pid] = st;
            _logger.LogDebug("Tuning {Name} (pid {Pid}): prior affinity 0x{PriorAffinity:X}, prior priority class 0x{PriorPriority:X}",
                st.Name, pid, st.PriorAffinity ?? 0, st.PriorPriority ?? 0);
        }

        if (adj.Affinity is ulong aff)
        {
            st.Affinity = SetAffinity(h, aff);
            _logger.LogDebug("  {Name}: affinity 0x{Aff:X} -> {Result}", st.Name, aff, st.Affinity ? "applied" : "failed");
        }

        if (adj.Eco is bool eco)
        {
            st.Eco = SetEco(h, eco);
            _logger.LogDebug("  {Name}: EcoQoS {State} -> {Result}", st.Name, eco ? "on" : "off", st.Eco ? "applied" : "failed");
        }

        if (adj.HighPri is bool high)
        {
            st.HighPri = SetPriority(h, high);
            _logger.LogDebug("  {Name}: priority {State} -> {Result}", st.Name, high ? "HIGH" : "NORMAL", st.HighPri ? "applied" : "failed");
        }
    }

    private void RestorePid(int pid, AppliedState st)
    {
        using var h = OpenLimited(pid);
        if (h.IsInvalid) { _applied.Remove(pid); return; }

        _logger.LogDebug("Restoring {Name} (pid {Pid})", st.Name, pid);

        if (st.Affinity)
        {
            ulong target = st.PriorAffinity ?? SystemMaskOf(h) ?? 0;
            if (target != 0) SetAffinity(h, target);
        }
        if (st.Eco) SetEco(h, false);
        if (st.HighPri)
        {
            uint targetClass = st.PriorPriority ?? NativeMethods.NORMAL_PRIORITY_CLASS;
            try { NativeMethods.SetPriorityClass(h, targetClass); }
            catch { /* best effort */ }
        }

        _applied.Remove(pid);
    }

    private static SafeProcessHandle OpenLimited(int pid) =>
        NativeMethods.OpenProcess(
            NativeMethods.PROCESS_SET_INFORMATION | NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION,
            false,
            (uint)pid);

    private static ulong? TryGetAffinity(SafeProcessHandle h)
    {
        try
        {
            if (NativeMethods.GetProcessAffinityMask(h, out ulong proc, out _)) return proc;
        }
        catch { /* access denied — best effort */ }
        return null;
    }

    private static uint? TryGetPriority(SafeProcessHandle h)
    {
        try { return NativeMethods.GetPriorityClass(h); }
        catch { /* access denied — best effort */ }
        return null;
    }

    private static ulong? SystemMaskOf(SafeProcessHandle h)
    {
        try
        {
            if (NativeMethods.GetProcessAffinityMask(h, out _, out ulong sys)) return sys;
        }
        catch { }
        return null;
    }

    private static bool SetAffinity(SafeProcessHandle h, ulong mask)
    {
        try
        {
            if (!NativeMethods.GetProcessAffinityMask(h, out ulong proc, out ulong sys)) return false;
            ulong target = mask & sys & proc; // never include bits the process cannot use
            if (target == 0) return false;
            return NativeMethods.SetProcessAffinityMask(h, target);
        }
        catch { return false; }
    }

    private static bool SetEco(SafeProcessHandle h, bool enable)
    {
        try
        {
            var state = new NativeMethods.PROCESS_POWER_THROTTLING_STATE
            {
                Version = NativeMethods.PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                ControlMask = NativeMethods.PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                StateMask = enable ? NativeMethods.PROCESS_POWER_THROTTLING_EXECUTION_SPEED : 0,
            };
            return NativeMethods.SetProcessInformation(h, NativeMethods.ProcessPowerThrottling,
                ref state, (uint)Marshal.SizeOf<NativeMethods.PROCESS_POWER_THROTTLING_STATE>());
        }
        catch { return false; }
    }

    private static bool SetPriority(SafeProcessHandle h, bool high)
    {
        try
        {
            // Pinning the priority boost keeps the class from decaying to normal over time.
            NativeMethods.SetProcessPriorityBoost(h, high);
            return NativeMethods.SetPriorityClass(h,
                high ? NativeMethods.HIGH_PRIORITY_CLASS : NativeMethods.NORMAL_PRIORITY_CLASS);
        }
        catch { return false; }
    }

    private static string? TryGetProcessName(uint pid)
    {
        using var h = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h.IsInvalid) return null;
        var sb = new StringBuilder(1024);
        uint len = (uint)sb.Capacity;
        if (!NativeMethods.QueryFullProcessImageName(h, 0, sb, ref len)) return null;
        return Path.GetFileName(sb.ToString());
    }
}

/// <summary>
/// CPU topology, derived once at startup from GetLogicalProcessorInformationEx.
/// Intel hybrid CPUs expose two efficiency classes (P = 1, E = 0). Uniform CPUs
/// (AMD, pre-hybrid Intel) fall back to first-half/second-half logical processors,
/// which maps to CCD0/CCD1 on AMD and is a safe deterministic split everywhere.
/// Note: masks are 64-bit, so group 0 processors up to index 63 are covered.
/// </summary>
public static class CpuTopology
{
    private readonly record struct CoreInfo(int EfficiencyClass, int Group, ulong Mask);

    public static ulong PCoreMask { get; }
    public static ulong ECoreMask { get; }
    public static int LogicalProcessorCount { get; }
    public static string Description { get; }

    static CpuTopology()
    {
        var cores = EnumerateCores();
        var byClass = cores.GroupBy(c => c.EfficiencyClass).OrderBy(g => g.Key).ToList();
        LogicalProcessorCount = cores.Sum(c => BitOperations.PopCount(c.Mask));

        if (byClass.Count > 1)
        {
            // Hybrid: highest efficiency class = performance cores, lowest = efficiency cores.
            PCoreMask = Union(byClass[^1]);
            ECoreMask = Union(byClass[0]);
            Description =
                $"Hybrid CPU — {BitOperations.PopCount(PCoreMask)} P-core / {BitOperations.PopCount(ECoreMask)} E-core logical processors";
        }
        else
        {
            // Uniform topology: P = first half, E = second half of logical processors.
            int half = LogicalProcessorCount / 2;
            ulong p = 0, e = 0;
            for (int i = 0; i < half && i < 64; i++) p |= 1UL << i;
            for (int i = half; i < LogicalProcessorCount && i < 64; i++) e |= 1UL << i;
            if (p == 0 || e == 0) { p = ulong.MaxValue; e = ulong.MaxValue; }
            PCoreMask = p;
            ECoreMask = e;
            Description =
                $"Uniform CPU — {LogicalProcessorCount} logical processors (P = first half, E = second half)";
        }
    }

    private static ulong Union(IEnumerable<CoreInfo> group)
    {
        ulong m = 0;
        foreach (var c in group) m |= c.Mask;
        return m;
    }

    private static unsafe List<CoreInfo> EnumerateCores()
    {
        // Windows 11 24H2+ moved GroupCount from a BYTE@2 to a WORD@22 (see winnt.h
        // PROCESSOR_RELATIONSHIP: BYTE Flags, BYTE EfficiencyClass, BYTE Reserved[20], WORD GroupCount).
        // Parse with the modern layout first, fall back to the legacy one on older systems.
        var list = ParseCores(modernLayout: true);
        if (list.Sum(c => BitOperations.PopCount(c.Mask)) == 0)
            list = ParseCores(modernLayout: false);
        return list;
    }

    private static unsafe List<CoreInfo> ParseCores(bool modernLayout)
    {
        var list = new List<CoreInfo>();

        // First call with a null buffer deliberately fails (ERROR_INSUFFICIENT_BUFFER) —
        // that is how the required buffer size is queried. Only trust the returned size.
        uint size = 0;
        NativeMethods.GetLogicalProcessorInformationEx(
            (int)NativeMethods.LOGICAL_PROCESSOR_RELATIONSHIP.ProcessorCore, null, ref size);
        if (size == 0) return list;

        var buffer = new byte[size];
        fixed (byte* p = buffer)
        {
            var root = (NativeMethods.SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX*)p;
            if (!NativeMethods.GetLogicalProcessorInformationEx(
                    (int)NativeMethods.LOGICAL_PROCESSOR_RELATIONSHIP.ProcessorCore, root, ref size))
                return list;

            // Layout offsets (relative to the entry's union, which starts 8 bytes in):
            //   modern: EfficiencyClass@1, GroupCount(WORD)@22, GroupMask@24
            //   legacy: EfficiencyClass@1, GroupCount(BYTE)@2,  GroupMask@16
            const int effClassOffset = 1;
            int groupCountOffset = modernLayout ? 22 : 2;
            int maskOffset = modernLayout ? 24 : 16;

            int offset = 0;
            while (offset < (int)size)
            {
                var entry = (NativeMethods.SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX*)((byte*)root + offset);
                if (entry->Relationship == (int)NativeMethods.LOGICAL_PROCESSOR_RELATIONSHIP.ProcessorCore)
                {
                    byte* u = (byte*)entry + 8; // union start
                    byte efficiencyClass = u[effClassOffset];
                    int groups = Math.Min(
                        (int)(modernLayout ? (ushort)(u[groupCountOffset] | (u[groupCountOffset + 1] << 8)) : u[groupCountOffset]),
                        NativeMethods.MaxGroupCount);

                    for (int g = 0; g < groups; g++)
                    {
                        byte* ga = u + maskOffset + g * 16;
                        // GROUP_AFFINITY: 8-byte mask + 2-byte group + 6 reserved, read byte-wise.
                        ulong mask = 0;
                        for (int b = 0; b < 8; b++) mask |= (ulong)ga[b] << (8 * b);
                        ushort group = (ushort)(ga[8] | (ga[9] << 8));
                        list.Add(new CoreInfo(efficiencyClass, group, mask));
                    }
                }

                int step = (int)entry->Size;
                if (step <= 0) break; // defensive: never loop forever on a corrupt buffer
                offset += step;
            }
        }
        return list;
    }
}
