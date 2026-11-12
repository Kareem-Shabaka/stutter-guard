using DpcTweaker.Core.Detection;
using DpcTweaker.Core.Infra;
using DpcTweaker.Core.Model;
using Microsoft.Win32;

namespace DpcTweaker.Core.Tweaks;

/// <summary>An MPO-disabling registry override found on this machine.</summary>
public sealed record MpoFinding(string KeyPath, string Name, string ValueDisplay, string Source);

/// <summary>
/// Finds and removes Multi-Plane Overlay overrides.
/// <para>
/// Stutter Guard used to offer MPO off as a tweak. It is gone: it helped a few badly broken systems
/// and gave other people flashing black screens, broken HDR and broken FreeSync, so it is not
/// something an app should switch on. What is already set still needs handling. An override left by
/// an older version, by a YouTube guide or by a vendor tool keeps the desktop compositor on the
/// slower path long after the user forgot about it. This finds it and offers to put Windows back.
/// </para>
/// </summary>
public static class MpoOverride
{
    public const string GraphicsDriversKey = @"HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers";

    /// <summary>What Stutter Guard 0.7 set. Still honoured by the compositor.</summary>
    public const string DisableOverlays = "DisableOverlays";

    /// <summary>The older community method: OverlayTestMode = 5. Ignored on 25H2 and later.</summary>
    public const string OverlayTestMode = "OverlayTestMode";

    /// <summary>Every key/value worth checking on this machine.</summary>
    internal static IReadOnlyList<(string KeyPath, string Name, string Source)> ProbesOf(SystemProfile profile)
    {
        var probes = new List<(string, string, string)>
        {
            (GraphicsDriversKey, DisableOverlays, "system-wide overlay switch"),
            (GraphicsDriversKey, OverlayTestMode, "older system-wide overlay switch"),
        };
        // Some guides put OverlayTestMode under the adapter's own class key instead.
        foreach (var gpu in profile.Gpus.Where(g => g.ClassKeyPath.Length > 0))
            probes.Add((gpu.ClassKeyPath, OverlayTestMode, "set on " + gpu.Name));
        return probes;
    }

    /// <summary>The overrides that are actually present. Read-only.</summary>
    public static IReadOnlyList<MpoFinding> Find(SystemProfile profile) =>
        Scan(ProbesOf(profile), RegistryOps.Read);

    /// <summary>
    /// The same scan with the registry read injected, so the rule can be tested against a throwaway
    /// key instead of a real display stack.
    /// </summary>
    internal static IReadOnlyList<MpoFinding> Scan(
        IEnumerable<(string KeyPath, string Name, string Source)> probes,
        Func<string, string, RegValue> read)
    {
        var found = new List<MpoFinding>();
        foreach (var (keyPath, name, source) in probes)
        {
            RegValue v;
            try { v = read(keyPath, name); }
            catch { continue; }   // an unreadable key is not an override
            if (!v.Exists) continue;
            // OverlayTestMode = 0 is what some drivers write when overlays are allowed; it disables nothing.
            if (name == OverlayTestMode && v.Kind == RegistryValueKind.DWord && v.AsDword() == 0) continue;
            found.Add(new MpoFinding(keyPath, name, v.Display, source));
        }
        return found;
    }

    /// <summary>
    /// One remover per finding. Applying these deletes the value inside the ordinary journal
    /// transaction, so the override is recorded first and Revert puts it straight back.
    /// </summary>
    internal static IReadOnlyList<RegistryValueTweak> BuildRemovers(IEnumerable<MpoFinding> findings, Journal journal) =>
        findings.Select(f => new RegistryValueTweak(new TweakInfo
        {
            Id = "graphics.mpo-remove." + Slug(f.KeyPath, f.Name),
            Module = "GPU",
            Group = "Graphics stack (all GPUs)",
            Title = $"Remove the MPO override ({f.Name} in {ShortKey(f.KeyPath)})",
            What = $"Deletes {f.Name} from {f.KeyPath}, which puts Multi-Plane Overlay back on Windows' default.",
            Why = "MPO off is a workaround for a few broken driver and OS combinations, not a tuning step. " +
                  "It makes the desktop compositor do more work per frame and breaks HDR, FreeSync and overlays on plenty of systems.",
            Cost = "None: this removes an override rather than adding one.",
            CanBreak = "Nothing. If the override was genuinely helping, Revert recorded it and puts it back.",
            Evidence = Evidence.Documented,
            Risk = Risk.Low,
            NeedsReboot = true,
        }, journal, f.KeyPath, f.Name, RegistryValueKind.DWord, null)).ToList();

    /// <summary>What the banner says. One line per override, plus the reason it is offered.</summary>
    public static string Describe(IReadOnlyList<MpoFinding> findings)
    {
        if (findings.Count == 0) return "";
        var lines = findings.Select(f => $"{f.Name} = {f.ValueDisplay} ({f.Source})");
        return string.Join("; ", lines) + ".";
    }

    /// <summary>Journal state key: which set of overrides the user chose to keep.</summary>
    public const string DismissedStateKey = "mpo.dismissed";

    /// <summary>Identifies exactly what was found, so a changed override asks again.</summary>
    public static string Signature(IReadOnlyList<MpoFinding> findings) =>
        string.Join(";", findings.Select(f => f.KeyPath + "|" + f.Name + "|" + f.ValueDisplay));

    /// <summary>True when the user already said "keep it" for exactly these values.</summary>
    public static bool IsDismissed(TweakEngine engine, IReadOnlyList<MpoFinding> findings) =>
        findings.Count > 0 && engine.Journal.GetState(DismissedStateKey) == Signature(findings);

    /// <summary>Remembers "keep it" so the card does not nag on every launch.</summary>
    public static void Dismiss(TweakEngine engine, IReadOnlyList<MpoFinding> findings)
    {
        try
        {
            engine.Journal.SetState(DismissedStateKey, Signature(findings));
            engine.Journal.Flush();
        }
        catch (Exception ex)
        {
            // Only a prompt is lost, nothing about the machine.
            Log.Warn("Could not remember the MPO choice: " + ex.Message);
        }
    }

    private static string Slug(string keyPath, string name) =>
        ShortKey(keyPath).Replace('\\', '-').Replace(' ', '-').ToLowerInvariant() + "." + name.ToLowerInvariant();

    private static string ShortKey(string keyPath) =>
        keyPath.StartsWith(GraphicsDriversKey, StringComparison.OrdinalIgnoreCase)
            ? "GraphicsDrivers"
            : keyPath[(keyPath.LastIndexOf('\\') + 1)..];
}