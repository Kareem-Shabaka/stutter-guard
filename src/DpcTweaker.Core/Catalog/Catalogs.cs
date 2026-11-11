using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DpcTweaker.Core.Catalog;

/// <summary>The service / scheduled-task presets (spec "Presets").</summary>
public enum CleanupPreset
{
    /// <summary>Puts back Microsoft's default start types (services only).</summary>
    WindowsDefault,
    /// <summary>Gaming desktops: telemetry off, polling helpers to Manual, hardware services only when the hardware is absent.</summary>
    Safe,
    /// <summary>For people who measure: Safe plus DPS, PcaSvc, TrkWks, WerSvc, Windows Search and more.</summary>
    Aggressive,
    /// <summary>Telemetry and updaters only; keeps sensors, Wi-Fi, Bluetooth, tablet input and location.</summary>
    Laptop,
    /// <summary>Safe, but Hyper-V, VMware, printing and network discovery stay as they are.</summary>
    Workstation,
    /// <summary>The author's desktop (SHABAKA-B4), as a reference.</summary>
    Author,
}

public static class Presets
{
    public static string Label(CleanupPreset p) => p switch
    {
        CleanupPreset.WindowsDefault => "Windows default",
        CleanupPreset.Safe => "Gaming, safe",
        CleanupPreset.Aggressive => "Gaming, aggressive",
        CleanupPreset.Laptop => "Laptop",
        CleanupPreset.Workstation => "Workstation / VM host",
        _ => "Author's desktop (reference)",
    };

    public static string Description(CleanupPreset p) => p switch
    {
        CleanupPreset.WindowsDefault => "Puts back Microsoft's default start type for every service the catalog knows. Use it to undo changes made by other tools too.",
        CleanupPreset.Safe => "Telemetry off, background updaters and helpers to Manual, and Wi-Fi / Bluetooth / printer / scanner / sensor services only when this PC doesn't have that hardware. Nothing a normal gamer would notice.",
        CleanupPreset.Aggressive => "Safe, plus DPS (measured: it caused an all-core ~490 µs event every 16 minutes on the author's desktop), compatibility assistant, link tracking, error reporting, Windows Search and vendor app services. You lose troubleshooters, usage history and fast file search.",
        CleanupPreset.Laptop => "Telemetry and updaters only. Keeps sensors, Wi-Fi, Bluetooth, tablet input, location and OEM battery/fan tools.",
        CleanupPreset.Workstation => "Like Safe, but Hyper-V, VMware, printing, network discovery and enterprise sync stay as they are.",
        _ => "Exactly what the author changed on SHABAKA-B4 (Oct 15 2026). A reference, not a recommendation: it assumes Process Lasso pins games, so the AMD V-Cache optimizer is off.",
    };
}

public sealed class BadDriverEntry
{
    [JsonPropertyName("deviceClass")] public string DeviceClass { get; set; } = "";
    [JsonPropertyName("deviceName")] public string DeviceName { get; set; } = "";
    [JsonPropertyName("hardwareIdPattern")] public string HardwareIdPattern { get; set; } = "";
    [JsonPropertyName("driverFile")] public string DriverFile { get; set; } = "";
    [JsonPropertyName("buggedVersions")] public List<string> BuggedVersions { get; set; } = new();
    [JsonPropertyName("recommendedVersion")] public string RecommendedVersion { get; set; } = "";
    [JsonPropertyName("typicalLatencyUs")] public int TypicalLatencyUs { get; set; }
    [JsonPropertyName("failureMechanism")] public string FailureMechanism { get; set; } = "";
    [JsonPropertyName("symptom")] public string Symptom { get; set; } = "";
    [JsonPropertyName("fix")] public string Fix { get; set; } = "";
    [JsonPropertyName("severity")] public string Severity { get; set; } = "High";
}

public sealed class ServiceEntry
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("display")] public string Display { get; set; } = "";
    [JsonPropertyName("what")] public string What { get; set; } = "";
    [JsonPropertyName("ifOff")] public string IfOff { get; set; } = "";
    [JsonPropertyName("group")] public string Group { get; set; } = "keep";
    [JsonPropertyName("lockedReason")] public string? LockedReason { get; set; }
    [JsonPropertyName("default")] public string? Default { get; set; }
    [JsonPropertyName("safe")] public string? Safe { get; set; }
    [JsonPropertyName("aggressive")] public string? Aggressive { get; set; }
    [JsonPropertyName("laptop")] public string? Laptop { get; set; }
    [JsonPropertyName("workstation")] public string? Workstation { get; set; }
    [JsonPropertyName("author")] public string? Author { get; set; }
    [JsonPropertyName("onlyIfAbsent")] public string? OnlyIfAbsent { get; set; }
    [JsonPropertyName("ms")] public string? Ms { get; set; }
    [JsonPropertyName("evidence")] public string? Evidence { get; set; }

    public bool IsLocked => Group is "locked" or "games";

    public string? Target(CleanupPreset p) => p switch
    {
        CleanupPreset.WindowsDefault => Default,
        CleanupPreset.Safe => Safe,
        CleanupPreset.Aggressive => Aggressive,
        CleanupPreset.Laptop => Laptop,
        CleanupPreset.Workstation => Workstation,
        _ => Author,
    };
}

public sealed class TaskEntry
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("what")] public string What { get; set; } = "";
    [JsonPropertyName("ifOff")] public string IfOff { get; set; } = "";
    [JsonPropertyName("group")] public string Group { get; set; } = "keep";
    [JsonPropertyName("lockedReason")] public string? LockedReason { get; set; }
    [JsonPropertyName("safe")] public string? Safe { get; set; }
    [JsonPropertyName("aggressive")] public string? Aggressive { get; set; }
    [JsonPropertyName("laptop")] public string? Laptop { get; set; }
    [JsonPropertyName("workstation")] public string? Workstation { get; set; }
    [JsonPropertyName("author")] public string? Author { get; set; }
    [JsonPropertyName("idle")] public bool Idle { get; set; }
    [JsonPropertyName("comesBack")] public string? ComesBack { get; set; }
    [JsonPropertyName("evidence")] public string? Evidence { get; set; }

    public bool IsLocked => Group == "locked";

    /// <summary>True when this preset disables the task. Windows default never disables anything.</summary>
    public bool Disables(CleanupPreset p) => (p switch
    {
        CleanupPreset.Safe => Safe,
        CleanupPreset.Aggressive => Aggressive,
        CleanupPreset.Laptop => Laptop,
        CleanupPreset.Workstation => Workstation,
        CleanupPreset.Author => Author,
        _ => null,
    }) == "Disable";
}

/// <summary>
/// A name/path catalog with glob matching: * matches anything (backslashes too), case-insensitive.
/// An exact entry beats a glob; a longer glob beats a shorter one.
/// </summary>
public sealed class GlobCatalog<T>
{
    private readonly Dictionary<string, T> _exact = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Pattern, Regex Rx, T Entry)> _globs = new();

    public GlobCatalog(IEnumerable<T> entries, Func<T, string> key)
    {
        foreach (var e in entries)
        {
            string k = key(e);
            if (k.Contains('*') || k.Contains('?'))
                _globs.Add((k, ToRegex(k), e));
            else
                _exact.TryAdd(k, e);
        }
        _globs.Sort((a, b) => b.Pattern.Length.CompareTo(a.Pattern.Length));
        All = entries.ToList();
    }

    public IReadOnlyList<T> All { get; }

    public static Regex ToRegex(string glob) =>
        new("^" + Regex.Escape(glob).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public T? Find(string key)
    {
        if (_exact.TryGetValue(key, out var e)) return e;
        foreach (var g in _globs)
            if (g.Rx.IsMatch(key)) return g.Entry;
        return default;
    }
}

public static class Catalogs
{
    private sealed class ServiceFile { [JsonPropertyName("services")] public List<ServiceEntry> Services { get; set; } = new(); }
    private sealed class TaskFile { [JsonPropertyName("tasks")] public List<TaskEntry> Tasks { get; set; } = new(); }

    private static readonly Lazy<GlobCatalog<ServiceEntry>> _services = new(() =>
        LoadServices(ReadResource("services.json")));

    private static readonly Lazy<GlobCatalog<TaskEntry>> _tasks = new(() =>
        LoadTasks(ReadResource("tasks.json")));

    private static readonly Lazy<List<BadDriverEntry>> _badDrivers = new(() =>
        LoadBadDrivers(ReadResource("bad_drivers.json")));

    public static GlobCatalog<ServiceEntry> Services => _services.Value;
    public static GlobCatalog<TaskEntry> Tasks => _tasks.Value;
    public static IReadOnlyList<BadDriverEntry> BadDrivers => _badDrivers.Value;

    public static List<BadDriverEntry> LoadBadDrivers(string json) =>
        JsonSerializer.Deserialize<List<BadDriverEntry>>(json) ?? new();

    public static GlobCatalog<ServiceEntry> LoadServices(string json) =>
        new(JsonSerializer.Deserialize<ServiceFile>(json)?.Services ?? new(), e => e.Name);

    public static GlobCatalog<TaskEntry> LoadTasks(string json) =>
        new(JsonSerializer.Deserialize<TaskFile>(json)?.Tasks ?? new(), e => e.Path);

    private static string ReadResource(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
                      ?? throw new InvalidOperationException(name + " isn't embedded in this build.");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    /// <summary>The template name of a per-user service instance (CDPUserSvc_957c1 -> CDPUserSvc).</summary>
    public static string TemplateName(string serviceName)
    {
        var m = Regex.Match(serviceName, @"^(.+)_[0-9a-f]{4,8}$", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : serviceName;
    }
}
