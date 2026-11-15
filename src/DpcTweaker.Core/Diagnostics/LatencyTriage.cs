using DpcTweaker.Core.Interrupts;
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
        IReadOnlyList<int>? gameCores = null,
        IReadOnlyList<OverlappingProcess>? overlapping = null,
        int unreadableProcesses = 0,
        IReadOnlyDictionary<int, IReadOnlyList<string>>? devicesByCpu = null,
        int protectedProcesses = 0,
        IReadOnlyList<DpcTweaker.Core.Interrupts.UnreadableProcess>? unreadableNames = null,
        bool elevated = false)
    {
        var findings = new List<TriageFinding>();
        var active = (displays ?? Array.Empty<DisplayInfo>()).Where(d => d.RefreshHz > 0).ToList();

        // ---- 1. the display setup: one cost, one finding ----
        // These used to be two findings - "N different refresh rates" and "M displays attached" - and
        // both fired on a three-screen machine, so one situation read as two problems. They answer the
        // same question: how much compositing does this setup cost per cycle? Several cadences is worth
        // saying; several screens at one cadence is worth saying; both at once is one sentence.
        if (active.Count >= 2)
        {
            var hz = active.Select(d => d.RefreshHz).Distinct().OrderBy(h => h).ToList();
            bool mixed = hz.Count > 1;
            bool many = active.Count >= DisplayCountWarning;

            if (mixed || many)
            {
                string list = string.Join(", ",
                    active.OrderBy(d => d.RefreshHz).Select(d => $"{d.Label} {d.Mode}{(d.Primary ? " (primary)" : "")}"));

                // Severity follows the stronger fact: several cadences is the sharper cost, because the
                // driver is woken on more than one schedule rather than merely more often.
                string title = mixed && many
                    ? $"{active.Count} displays at {hz.Count} different refresh rates ({string.Join(" / ", hz)} Hz)"
                    : mixed
                        ? $"{hz.Count} different refresh rates across {active.Count} displays ({string.Join(" / ", hz)} Hz)"
                        : $"{active.Count} displays attached";

                string why = mixed && many
                    ? "Each screen is composited at its own cadence, and the graphics driver does that work in its " +
                      "interrupt handler for every attached screen - so this setup costs more per cycle than one " +
                      "screen at one rate, in two ways at once."
                    : mixed
                        ? "Each display is composited at its own cadence, so the graphics driver does work in its " +
                          "interrupt handler for every attached screen, not only for the game's. Two cadences mean " +
                          "two schedules to service."
                        : "Every attached display is another surface the compositor presents each cycle, so the cost " +
                          "is paid whether or not anything is open on it.";

                findings.Add(new TriageFinding(
                    mixed ? TriageSeverity.High : TriageSeverity.Medium,
                    title,
                    list + ".",
                    why + " That is a cost of the setup rather than a fault, and nothing this app changes alters it - " +
                    "it is worth knowing when you read the numbers.",
                    false));
            }
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
                                        "Open the Interrupts screen and press Re-apply, then run this check again. If it persists, a device this app does not " +
                    "manage is raising them - the Interrupts screen lists what it does manage, so you can see what is missing.\n\n" +
                    "Why: these cores are reserved for the game, so anything here is competing with it.",
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
                        Suspects(worst.Cpu, devicesByCpu) +
                        "\n\nWhy this matters: sustained load is a different problem from one long DPC - it takes a slice of that " +
                        "core continuously rather than once, so moving the work to another core only moves the loss.",
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
                    "Firmware raises an SMI straight to the processor, outside Windows' interrupt handling, and the " +
                    "operating system is never told when or for how long. No register, driver, affinity mask or power " +
                    "setting can see one, move one or stop one.",
                    "Nothing on this machine can act on it, and this app cannot measure it. That leaves an SMI as the " +
                    "explanation for large DPC times that survive a clean Windows install on the same board - the last " +
                    "candidate rather than the first, and the one worth knowing about only when everything else is clean.",
                false));
        }

        // ---- 6. processes restricted onto the cores reserved for interrupts ----
        // The scheduler never checks whether a core is servicing a device, so ordinary work placed there
        // competes with every interrupt the plan isolated. Measured: 782 us against 8.9 us, same load.
        if (overlapping is { Count: > 0 })
        {
            var names = overlapping
                .GroupBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Count() > 1 ? $"{g.Key} x{g.Count()}" : g.Key)
                .ToList();
            var cpus = overlapping.SelectMany(o => o.OverlapCpus).Distinct().OrderBy(c => c).ToList();
            findings.Add(new TriageFinding(
                TriageSeverity.High,
                $"{overlapping.Count} process(es) restricted onto the interrupt cores ({string.Join(", ", cpus.Select(c => "CPU " + c))})",
                string.Join(", ", names.Take(8)) + (names.Count > 8 ? $", and {names.Count - 8} more" : "") + ".",
                    "These are running with an explicit affinity mask that names a processor this machine uses for " +
                    "devices, so the scheduler is free to run them beside the interrupts. Usually something else set " +
                    "those masks - a process manager keeping a rule, or a launcher applying one at startup - so the " +
                    "program itself is not doing anything wrong.\n\n" +
                    "Why it is worth knowing: the scheduler does not know a core is servicing a device, so anything " +
                    "pinned there competes with every interrupt on it. This app moves devices, not processes, so it " +
                    "cannot change this for you.",
                    false));
        }

        // A count of zero is only meaningful if everything was readable, and a refusal is only worth
        // reporting if the user could do something about it.
        if (unreadableProcesses > 0)
        {
            int explainable = Math.Max(0, unreadableProcesses - protectedProcesses);

    // Name them. A count is something to worry about; a list is something to read and dismiss.
    string names = "";
    if (unreadableNames is { Count: > 0 })
    {
        var shown = unreadableNames.Take(10).Select(u => u.Name).ToList();
        names = unreadableNames.Count > shown.Count
            ? $"\n\nUnread: {string.Join(", ", shown)} and {unreadableNames.Count - shown.Count} more."
            : $"\n\nUnread: {string.Join(", ", shown)}.";
    }
            string title;
            string detail;
            string guidance;

            if (protectedProcesses >= unreadableProcesses)
            {
                // Every one of them is Windows itself. No user-mode tool reads these, elevated or not,
                // so there is no action to offer and saying "run as administrator" would be false.
                title = $"{protectedProcesses} Windows processes could not be read (protected by Windows)";
                detail = "These are System, Idle and the services Windows marks protected. No user-mode tool can read their " +
                         "affinity, and elevation does not change that." + names;
                guidance = "Nothing to do. A program competes with interrupts through an affinity mask, and every process on " +
                           "this PC that could carry one was read.";
            }
            else if (elevated)
            {
                title = $"{unreadableProcesses} process(es) could not be read, {protectedProcesses} of them protected by Windows";
                detail = "Stutter Guard is already running as administrator, so these are refusals elevation cannot fix." + names;
                guidance = "Nothing to do here. This app cannot read those processes, and it cannot say whether any of them " +
                           "competes with an interrupt - the numbers above are the honest answer to that, not this list.";
            }
            else
            {
                title = $"{unreadableProcesses} process(es) could not be inspected for affinity masks";
                detail = $"{explainable} of them are other-account processes, which answer access-denied without elevation; " +
                         $"{protectedProcesses} are Windows-internal and cannot be read by anything." + names;
                guidance = "Run Stutter Guard as administrator and press the check again to include the other-account ones. The pins, " +
                    "the budgets and everything else above do not need elevation.\n\n" +
                    "Why it is counted: a silent zero would look like a clean machine when it is only an unread one.";
            }

            findings.Add(new TriageFinding(TriageSeverity.Info, title, detail, guidance, false));
        }

        return findings;
    }

    /// <summary>
    /// What to do about a core carrying sustained DPC load, named from what this app itself placed there.
    /// <para>
    /// Windows does not report which driver a DPC belongs to without kernel tracing, so this cannot name a driver. It can
    /// name the <b>devices</b> pinned to that core, which is the question the user would otherwise have to answer alone -
    /// and it stays honest rather than guessing when it knows nothing about the core.
    /// </para>
    /// </summary>
    static string Suspects(int cpu, IReadOnlyDictionary<int, IReadOnlyList<string>>? devicesByCpu)
    {
        if (devicesByCpu is null || !devicesByCpu.TryGetValue(cpu, out var names) || names.Count == 0)
            return "This app cannot name the driver behind it: Windows does not report which driver a DPC belongs to " +
                   "without kernel tracing. LatencyMon's Drivers tab does show the name.";

        string list = string.Join(", ", names);
        return names.Count == 1
            ? $"This app has one device pinned to CPU {cpu}: {list}. That is the device whose work this app put here, " +
              "and the first place to look if the load traces back to a device."
            : $"This app has these devices pinned to CPU {cpu}: {list}. Those are the devices whose work this app put " +
              "here, and the first place to look if the load traces back to a device.";
    }

    /// <summary>Worst-first, for display.</summary>
    public static IReadOnlyList<TriageFinding> Ordered(IReadOnlyList<TriageFinding> findings) =>
        findings.OrderByDescending(f => f.Severity).ThenBy(f => f.Title, StringComparer.Ordinal).ToList();
}