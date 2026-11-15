using System.Text.Json;
using System.Text.Json.Serialization;
using DpcTweaker.Core.Detection;
using DpcTweaker.Core.Interrupts;
using DpcTweaker.Core.Model;
using DpcTweaker.Core.Infra;

namespace DpcTweaker.Core.Tweaks;

public enum PlanTargetKind
{
    /// <summary>The app's own copy of Ultimate Performance (or an existing Ultimate plan).</summary>
    TunedUltimate,
    /// <summary>Windows' Balanced plan: device settings only, so AMD/Intel parking keeps working.</summary>
    Balanced,
    /// <summary>Whatever plan is active (laptops: the plan the vendor tool maps to performance mode).</summary>
    CurrentActive,
}

public enum PowerProfileKind
{
    /// <summary>Every low-latency setting, including the CPU idle and boost settings.</summary>
    Full,
    /// <summary>USB, PCIe, disk and Wi-Fi power saving only; CPU settings stay stock.</summary>
    DevicesOnly,
}

public enum CStateMode
{
    /// <summary>Cores only enter C1 (the desktop's setting).</summary>
    Light,
    /// <summary>Windows stock idle, for before/after comparisons.</summary>
    Deep,
    /// <summary>Idle disabled: tests only, much hotter.</summary>
    Off,
}

public sealed class AppOptions
{
    public PlanTargetKind PlanTarget { get; set; } = PlanTargetKind.TunedUltimate;
    public PowerProfileKind PowerProfile { get; set; } = PowerProfileKind.Full;
    public bool ApplyOnBattery { get; set; }
    public CStateMode CState { get; set; } = CStateMode.Light;

    /// <summary>
    /// Which cost tier of tweaks is pre-selected. Community-only tweaks are never pre-selected below
    /// TweakPreset.Everything; see TweakPresetSelector.
    /// </summary>
    public TweakPreset Preset { get; set; } = TweakPreset.Safe;

    /// <summary>Timer resolution in 100-ns units (5000 = 0.5 ms, 5070 = 0.507 ms).</summary>
    public uint TimerResolution { get; set; } = 5000;

    /// <summary>MSI module: the safe class list, or every device that supports it (optional).</summary>
    public MsiMode Msi { get; set; } = MsiMode.Safe;

    /// <summary>Services and scheduled tasks preset.</summary>
    public DpcTweaker.Core.Catalog.CleanupPreset Cleanup { get; set; } = DpcTweaker.Core.Catalog.CleanupPreset.Safe;

    /// <summary>Interrupts: keep devices on cores they're already pinned to when those follow the rules.</summary>
    public bool KeepValidPins { get; set; } = true;

    /// <summary>
    /// On an Intel hybrid CPU, where the non-game devices go. Defaults to full P-core isolation,
    /// which is the strongest choice for the game but can override a benchmark that measured
    /// P-cores as better. Ignored on non-hybrid CPUs.
    /// </summary>
    public HybridDevicePolicy HybridDevices { get; set; } = HybridDevicePolicy.EcoresForNetAudio;

    public bool UserChosen { get; set; }

    /// <summary>A copy with some fields changed (so a screen that owns one option never drops the others).</summary>
    public AppOptions With(Action<AppOptions> change)
    {
        var copy = JsonSerializer.Deserialize<AppOptions>(JsonSerializer.Serialize(this, Json), Json)!;
        change(copy);
        return copy;
    }

    /// <summary>The preset defaults from the spec's preset matrix.</summary>
    public static AppOptions DefaultsFor(SystemProfile p)
    {
        var o = new AppOptions();
        if (p.IsLaptop) o.Cleanup = DpcTweaker.Core.Catalog.CleanupPreset.Laptop;
        if (p.IsLaptop)
        {
            o.PlanTarget = PlanTargetKind.CurrentActive;
            o.PowerProfile = PowerProfileKind.Full;
        }
        else if (p.ParkingDependentCpu && !p.Software.ProcessLasso)
        {
            o.PlanTarget = PlanTargetKind.Balanced;
            o.PowerProfile = PowerProfileKind.DevicesOnly;
        }
        return o;
    }

    /// <summary>
    /// Enums go in by <b>name</b>. They used to go in as numbers, so reordering any member silently
    /// turned an old file into a different setting - and two of these enums (MsiMode, HybridDevices)
    /// have been edited five and eight times. No stored value has been misread yet, because every
    /// edit so far appended rather than moved; this removes the chance.
    /// </summary>
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        // allowIntegerValues: false is the whole point. JsonStringEnumConverter accepts numbers by
        // default, so a numeric blob deserialized "successfully" through the current reader and the
        // migration below never ran - the old format would have survived forever, silently. Refusing
        // integers forces a v1 file down the legacy path, where it is read correctly and rewritten.
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    /// <summary>How files written before names were used are read: by number, as they were written.</summary>
    private static readonly JsonSerializerOptions JsonLegacy = new() { WriteIndented = false };

    public static AppOptions Load(Journal j, SystemProfile p)
    {
        string? s = j.GetState("options");
        if (s is not null)
        {
            // Current shape first: enums by name.
            try
            {
                var o = JsonSerializer.Deserialize<AppOptions>(s, Json);
                if (o is not null && o.UserChosen) return o;
                if (o is not null) return DefaultsFor(p);
            }
            catch { /* not the current shape; try the one it was written in */ }

            // A file from a build that wrote enums as numbers. Read it the way it was written, then
            // save it back in the current shape so the old format does not survive the next write.
            try
            {
                var legacy = JsonSerializer.Deserialize<AppOptions>(s, JsonLegacy);
                if (legacy is not null)
                {
                    Log.Info("Options migrated from numeric enums to names.");
                    if (legacy.UserChosen)
                    {
                        try { legacy.Save(j); } catch (Exception ex) { Log.Warn("Could not rewrite options: " + ex.Message); }
                        return legacy;
                    }
                    return DefaultsFor(p);
                }
            }
            catch { /* genuinely unreadable: defaults, which is what the old code did too */ }
        }
        return DefaultsFor(p);
    }

    public void Save(Journal j)
    {
        UserChosen = true;
        j.SetState("options", JsonSerializer.Serialize(this, Json));
        j.Save();
    }
}
