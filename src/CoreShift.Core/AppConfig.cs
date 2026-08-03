using System.Text.Json;
using System.Text.Json.Serialization;

namespace CoreShift;

/// <summary>How a target's threads are pinned to logical processors.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AffinityMode>))]
public enum AffinityMode
{
    None = 0,      // leave affinity alone
    AllCores,      // full system mask
    PCoresOnly,    // performance cores (Intel P-cores / first half on uniform CPUs)
    ECoresOnly,    // efficiency cores (Intel E-cores / second half on uniform CPUs)
}

/// <summary>One managed application. Name is always matched case-insensitively (with or without .exe).</summary>
public sealed class Target
{
    public string Name { get; set; } = "";      // "game.exe"
    public string? Path { get; set; }           // optional full path (custom adds)
    public AffinityMode Affinity { get; set; } = AffinityMode.None;
    public bool EcoQoS { get; set; }            // Efficiency Mode (EcoQoS)
    public bool HighPriority { get; set; }      // HIGH_PRIORITY_CLASS
    public bool DemoteInBackground { get; set; }// push to E-cores + EcoQoS while a tuned game is focused

    [JsonIgnore]
    public bool HasTuning => Affinity != AffinityMode.None || EcoQoS || HighPriority;

    public bool Matches(string processName) =>
        AppIdentity.NormalizeName(Name) == AppIdentity.NormalizeName(processName);
}

/// <summary>Root config object, persisted next to the executable as config.json.</summary>
public sealed class AppConfig
{
    public int Version { get; set; } = 1;
    public List<Target>? Targets { get; set; } = new();
    public bool BackgroundEcoQoS { get; set; } = true;   // global: demoted apps get EcoQoS
    public bool BackgroundECores { get; set; } = true;   // global: demoted apps pinned to E-cores
}

/// <summary>Normalization used everywhere for process-name matching.</summary>
public static class AppIdentity
{
    public static string NormalizeName(string nameOrPath) =>
        Path.GetFileNameWithoutExtension(nameOrPath).Trim().ToLowerInvariant();
}

/// <summary>
/// Source-generated JSON context — required for Native AOT (no reflection serializer).
/// </summary>
[JsonSerializable(typeof(AppConfig))]
internal partial class AppConfigJsonContext : JsonSerializerContext;

/// <summary>Loads/saves config.json next to the executable (portable).</summary>
public static class ConfigStore
{
    public static string ConfigPath => Path.Combine(AppContext.BaseDirectory, "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var cfg = JsonSerializer.Deserialize(json, AppConfigJsonContext.Default.AppConfig);
                if (cfg is not null)
                {
                    // Defensive normalization: a hand-edited config must never crash the UI
                    // with a null target list or null names.
                    cfg.Targets ??= new List<Target>();
                    foreach (var t in cfg.Targets) t.Name ??= string.Empty;
                    return cfg;
                }
            }
        }
        catch (Exception ex)
        {
            // Corrupt or unreadable config: fall back to defaults instead of crashing.
            System.Diagnostics.Debug.WriteLine($"[CoreShift] config load failed: {ex.Message}");
        }
        return new AppConfig();
    }

    public static void Save(AppConfig config)
    {
        var tmp = ConfigPath + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
        using (var writer = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true }))
        {
            // JsonTypeInfo overload: fully static, Native-AOT safe, keeps pretty-printed output.
            JsonSerializer.Serialize(writer, config, AppConfigJsonContext.Default.AppConfig);
        }
        File.Move(tmp, ConfigPath, overwrite: true); // atomic-ish replace
    }
}
