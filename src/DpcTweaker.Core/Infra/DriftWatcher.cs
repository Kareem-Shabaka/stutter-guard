using System.Text;
using System.Text.Json;
using DpcTweaker.Core.Detection;
using DpcTweaker.Core.Model;

namespace DpcTweaker.Core.Infra;

/// <summary>Why applied tweaks no longer match what the app set.</summary>
public enum DriftCause
{
    /// <summary>Nothing is off.</summary>
    None,
    /// <summary>The driver set changed: the strongest evidence available for "a driver update did this".</summary>
    DriverUpdate,
    /// <summary>Tweaks are off, but no driver version changed: a Windows update, a tuning tool, or the user.</summary>
    ExternalChange,
}

/// <summary>The outcome of one drift scan, ready for the Home banner.</summary>
/// <summary>One subsystem and how many of its tweaks drifted.</summary>
public sealed record DriftGroup(string Module, int Count)
{
    /// <summary>The name a user recognises, rather than the internal module string.</summary>
    public string Label => Module switch
    {
        "GPU" => "GPU driver keys",
        "Interrupts" => "Interrupts / MSI",
        "Network" => "Network adapter",
        "Power" => "Power plan",
        "Boot tasks" => "Boot tasks / timer",
        "Services" => "Services",
        "Tasks" => "Scheduled tasks",
        _ => Module,
    };
}

public sealed record DriftReport(
    IReadOnlyList<ITweak> Drifted,
    DriftCause Cause,
    IReadOnlyList<string> ChangedDevices,
    IReadOnlyList<DriftGroup> Subsystems,
    string Headline,
    string Detail)
{
    public static readonly DriftReport Clean = new(
        Array.Empty<ITweak>(), DriftCause.None, Array.Empty<string>(), Array.Empty<DriftGroup>(), "", "");

    public int Count => Drifted.Count;
    public bool HasDrift => Drifted.Count > 0;
    public bool IsDriverUpdate => Cause == DriftCause.DriverUpdate;

    /// <summary>"GPU driver keys (2), Interrupts / MSI (4), Power plan (1)" — biggest first.</summary>
    public string SubsystemText => string.Join(", ", Subsystems.Select(g => $"{g.Label} ({g.Count})"));

    /// <summary>Groups the drifted tweaks by module, most affected first.</summary>
    public static IReadOnlyList<DriftGroup> Group(IEnumerable<ITweak> drifted) =>
        drifted.GroupBy(t => t.Info.Module)
               .Select(g => new DriftGroup(g.Key, g.Count()))
               .OrderByDescending(g => g.Count)
               .ThenBy(g => g.Module, StringComparer.OrdinalIgnoreCase)
               .ToList();
}

/// <summary>
/// Compares the live system against what the app recorded.
/// <para>
/// Drift itself is already answered per tweak by <see cref="TweakBase.Detect"/>: a tweak marked
/// applied whose live value no longer matches is Drifted. That alone cannot say <em>why</em>.
/// So the watcher also keeps a fingerprint of every device's driver identity (version + date) as it
/// was when the tweaks were last written, and compares that on startup. A changed fingerprint plus
/// drift is a driver update; drift without one is something else. Saying "driver update detected"
/// without that evidence would be a guess, and this app doesn't guess.
/// </para>
/// </summary>
public static class DriftWatcher
{
    /// <summary>Journal state key holding the last captured fingerprint.</summary>
    public const string StateKey = "drift.fingerprint";

    /// <summary>
    /// Device instance id -> "driverVersion|driverDate" for every device that has a driver key.
    /// Those are exactly the devices whose Driver-registry keys a reinstall rewrites.
    /// </summary>
    public static string Capture(SystemProfile profile) =>
        Compose(profile.Devices
            .Where(d => d.ClassKey is not null)
            .Select(d => (d.InstanceId, d.DriverVersion, d.DriverDate)));

    /// <summary>Builds the fingerprint string from instance id / version / date triples.</summary>
    internal static string Compose(IEnumerable<(string InstanceId, string? Version, string? Date)> devices)
    {
        var sb = new StringBuilder();
        foreach (var d in devices.OrderBy(x => x.InstanceId, StringComparer.OrdinalIgnoreCase))
        {
            string version = d.Version ?? "";
            string date = d.Date ?? "";
            if (version.Length == 0 && date.Length == 0) continue;
            if (sb.Length > 0) sb.Append(';');
            sb.Append(d.InstanceId).Append('=').Append(version).Append('|').Append(date);
        }
        return sb.ToString();
    }

    /// <summary>Records the driver set the tweaks were just written against.</summary>
    public static void Remember(TweakEngine engine)
    {
        try
        {
            engine.Journal.SetState(StateKey, Capture(engine.Profile));
            engine.Journal.Save();
        }
        catch (Exception ex)
        {
            // A missing fingerprint only costs attribution, never correctness.
            Log.Warn("Couldn't record the driver fingerprint: " + ex.Message);
        }
    }

    /// <summary>Devices whose driver version or date differs from the last capture.</summary>
    public static IReadOnlyList<string> ChangedDevices(string? before, string? after, SystemProfile profile)
    {
        var old = Parse(before);
        var now = Parse(after);
        var names = new List<string>();
        foreach (var kv in now)
        {
            if (old.TryGetValue(kv.Key, out string? was))
            {
                if (was != kv.Value) names.Add(Label(profile, kv.Key));
            }
            else
            {
                names.Add(Label(profile, kv.Key) + " (new)");
            }
        }
        foreach (var kv in old)
            if (!now.ContainsKey(kv.Key)) names.Add(Label(profile, kv.Key) + " (removed)");
        return names;
    }

    private static string Label(SystemProfile profile, string instanceId)
    {
        var dev = profile.Devices.FirstOrDefault(d => string.Equals(d.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase));
        if (dev is not null) return dev.Name;
        var gpu = profile.Gpus.FirstOrDefault(g => string.Equals(g.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase));
        return gpu?.Name ?? instanceId;
    }

    private static Dictionary<string, string> Parse(string? fingerprint)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(fingerprint)) return map;
        foreach (string part in fingerprint.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.LastIndexOf('=');
            if (eq <= 0) continue;
            map[part[..eq]] = part[(eq + 1)..];
        }
        return map;
    }

    /// <summary>Scans for drift and attributes it. Read-only: it changes nothing.</summary>
    public static DriftReport Scan(TweakEngine engine)
    {
        IReadOnlyList<ITweak> drifted;
        try
        {
            drifted = engine.DriftedTweaks();
        }
        catch (Exception ex)
        {
            Log.Warn("Drift scan failed: " + ex.Message);
            return DriftReport.Clean;
        }

        return Attribute(drifted, engine.Journal.GetState(StateKey), Capture(engine.Profile), engine.Profile);
    }

    /// <summary>
    /// Turns a list of drifted tweaks plus the two fingerprints into a report. Pure, so the
    /// attribution rule can be tested without a live machine.
    /// </summary>
    internal static DriftReport Attribute(IReadOnlyList<ITweak> drifted, string? before, string? after, SystemProfile profile)
    {
        if (drifted.Count == 0) return DriftReport.Clean;

        IReadOnlyList<string> changed = before is null
            ? Array.Empty<string>()
            : ChangedDevices(before, after, profile);
        bool driverChanged = changed.Count > 0;

        // Nothing recorded yet (a journal from a build before this feature): drift is real, the
        // cause is unknown, so do not dress it up as a driver update.
        var cause = driverChanged ? DriftCause.DriverUpdate : DriftCause.ExternalChange;

        var groups = DriftReport.Group(drifted);
        string what = drifted.Count == 1 ? "1 applied tweak was" : $"{drifted.Count} applied tweaks were";
        string headline = driverChanged
            ? $"Driver Update Detected — {what} reset to stock"
            : $"Windows Update or Another Tool — {what} reset to stock";

        string who = changed.Count switch
        {
            0 => "",
            1 => changed[0],
            2 => changed[0] + " and " + changed[1],
            _ => changed[0] + ", " + changed[1] + $" and {changed.Count - 2} more",
        };

        string detail = driverChanged
            ? $"The driver for {who} changed since these tweaks were applied" +
              (before is null ? " (no earlier fingerprint was recorded, so this is inferred)." :
               ". A driver install or update usually wipes the device's Interrupt Management and Class keys, which puts interrupts back on CPU 0.") +
              " Re-applying keeps the original before-state in the journal, so every revert still works."
            : "These were applied and no driver version changed, so a Windows update, another tuning tool or a manual edit put them back. " +
              "Re-applying keeps the original before-state in the journal, so every revert still works.";

        detail = "Affected: " + string.Join(", ", groups.Select(g => $"{g.Label} ({g.Count})")) + ".\n\n" + detail;

        Log.Info($"Drift: {headline} ({cause})" +
                 (changed.Count > 0 ? " — driver changed: " + string.Join(", ", changed) : "") +
                 " — " + string.Join(", ", groups.Select(g => $"{g.Module}={g.Count}")));
        return new DriftReport(drifted, cause, changed, groups, headline, detail);
    }
}