using DpcTweaker.Core.Native;

namespace DpcTweaker.Core.Diagnostics;

/// <summary>How much a finding matters for stutter.</summary>
public enum TriageSeverity { Info, Medium, High }

/// <summary>
/// One thing found on this machine that costs latency, with what to do about it and - just as
/// important - whether the app's own affinity engine is able to help.
/// </summary>
/// <param name="AffinityCanFix">
/// False for costs that live inside a driver's own execution time. Moving an interrupt to another core
/// changes where a DPC runs, never how long it takes, so those have to be fixed by the user.
/// </param>
public sealed record TriageFinding(TriageSeverity Severity, string Title, string Detail, string Guidance, bool AffinityCanFix);

/// <summary>
/// Reads the machine for latency costs that tweaks alone cannot remove, and says so plainly.
/// <para>
/// The point is the honest boundary. Stutter Guard moves interrupts and strips power states. It cannot
/// make a GPU driver's DPC finish sooner, so when the evidence points at a driver doing expensive work
/// per frame - mixed-refresh multi-monitor composition is the usual one - the app should name it and
/// stop implying another tweak will help.
/// </para>
/// </summary>
public static class LatencyTriage
{
    /// <summary>A game core spending this much of its time in DPCs means the pins are not holding.</summary>
    public const double GameCoreDpcWarning = 0.5;

    /// <summary>
    /// Microsoft's budget for one DPC routine. Its driver documentation ("Example 15: Measuring DPC/ISR
    /// Time") says DPCs "should not run longer than 100 microseconds" and ISRs "should not run longer than
    /// 25 microseconds". A duration with no budget attached is not a finding, so every DPC and ISR figure
    /// this class reports is stated against these two numbers.
    /// </summary>
    public const double DpcBudgetUs = 100;

    /// <summary>Microsoft's budget for one ISR. See <see cref="DpcBudgetUs"/>.</summary>
    public const double IsrBudgetUs = 25;
    /// <summary>The worst core sitting above this suggests sustained DPC work, not a single spike.</summary>
    public const double SustainedDpcWarning = 1.5;

    /// <summary>Beyond two displays the compositor has more work per wake-up.</summary>
    public const int DisplayCountWarning = 3;

    /// <summary>
    /// Pure rule engine, so every branch is testable without a display array or a live counter.
    /// </summary>
    public static IReadOnlyList<TriageFinding> Scan(
        IReadOnlyList<DisplayInfo>? displays = null,
        IReadOnlyList<CpuRates>? rates = null,
        IReadOnlyList<int>? gameCores = null)
    {
        var findings = new List<TriageFinding>();
        var active = (displays ?? Array.Empty<DisplayInfo>()).Where(d => d.RefreshHz > 0).ToList();

        // ---- 1. mixed refresh rates: the compositor must service several cadences ----
        if (active.Count >= 2)
        {
            var hz = active.Select(d => d.RefreshHz).Distinct().OrderBy(h => h).ToList();
            if (hz.Count > 1)
            {
                string list = string.Join(", ",
                    active.OrderBy(d => d.RefreshHz).Select(d => $"{d.Device} {d.Mode}{(d.Primary ? " (primary)" : "")}"));
                findings.Add(new TriageFinding(
                    TriageSeverity.High,
                    $"{hz.Count} different refresh rates across {active.Count} displays ({string.Join(" / ", hz)} Hz)",
                    list + ".",
                    "Windows composites every attached display, and the GPU driver services that work in its own DPC. " +
                    "Mixed refresh rates force it to wake for several cadences instead of one, which is a common cause of " +
                    $"large amdkmdag.sys DPC times - well past Microsoft's {DpcBudgetUs:0} us budget for one DPC. " +
                    "Set every display to the same refresh rate, or disconnect the ones you " +
                    "are not using while you play. No interrupt pin or registry value removes this cost.",
                    false));
            }
        }

        // ---- 2. sheer display count ----
        if (active.Count >= DisplayCountWarning)
        {
            findings.Add(new TriageFinding(
                TriageSeverity.Medium,
                $"{active.Count} displays attached",
                $"Composite cost grows with every attached display: {string.Join(", ", active.Select(d => d.Mode))}.",
                "Leave only the gaming display attached while playing. On a machine that is already tuned this is a " +
                "bigger win than any further affinity change.",
                false));
        }

        // ---- 3. DPCs landing on the cores the plan tries to keep clean ----
        if (rates is { Count: > 0 } && gameCores is { Count: > 0 })
        {
            var keep = gameCores.ToHashSet();
            var hot = rates.Where(r => keep.Contains(r.Cpu) && r.DpcPercent >= GameCoreDpcWarning)
                           .OrderByDescending(r => r.DpcPercent).ToList();
            if (hot.Count > 0)
            {
                string cores = string.Join(", ", hot.Select(r => $"CPU {r.Cpu} ({r.DpcPercent:0.00} %)"));
                findings.Add(new TriageFinding(
                    TriageSeverity.High,
                    $"DPC work is landing on game cores: {cores}",
                    "These are the cores the plan reserves for the game, so something is still interrupting them.",
                    "Either the interrupt pins are not applied (the Interrupts screen shows each row's live state), " +
                    "or a device the app does not manage is raising them. Re-apply, then check the Interrupts screen for " +
                    "rows that read 'already like this' but sit on a reserved core.",
                    true));
            }
        }

        // ---- 4. sustained DPC load anywhere ----
        if (rates is { Count: > 0 })
        {
            var worst = rates.OrderByDescending(r => r.DpcPercent).First();
            if (worst.DpcPercent >= SustainedDpcWarning)
            {
                findings.Add(new TriageFinding(
                    TriageSeverity.Medium,
                    $"Sustained DPC load on CPU {worst.Cpu}: {worst.DpcPercent:0.00} % of its time",
                    $"{worst.DpcsPerSec:0} DPCs/s and {worst.InterruptsPerSec:0} interrupts/s measured over the sample. " +
                    $"Microsoft's budget is {DpcBudgetUs:0} us for one DPC routine and {IsrBudgetUs:0} us for one ISR.",
                    "Sustained load is a different problem from one long DPC. Find which driver owns the hardware on " +
                    "that core; if it is the GPU, the display findings above are the cause.",
                    true));
            }
        }

        // ---- 5. the ceiling: the one cause Windows itself cannot move ----
        // Offered only when there is DPC trouble to explain, so a healthy machine is not lectured.
        if (findings.Any(f => f.Title.Contains("DPC", StringComparison.Ordinal)))
        {
            findings.Add(new TriageFinding(
                TriageSeverity.Info,
                "System Management Interrupts are outside Windows' control",
                "Firmware raises an SMI straight to the processor, commonly for 100 us or more at a time. No register, " +
                "driver, affinity mask or power setting can see one, move one or stop one, and Windows is not told it happened.",
                "If large DPC times survive a clean install on the same board, an SMI is the remaining candidate. Firmware " +
                "updates and turning off unused board features (legacy USB, serial ports, unused controllers) are the only " +
                "levers a user has. This app cannot measure SMIs, so it cannot tell you whether yours are the cause - it can " +
                "only name the possibility.",
                false));
        }

        return findings;
    }

    /// <summary>Worst-first, for display.</summary>
    public static IReadOnlyList<TriageFinding> Ordered(IReadOnlyList<TriageFinding> findings) =>
        findings.OrderByDescending(f => f.Severity).ThenBy(f => f.Title, StringComparer.Ordinal).ToList();
}