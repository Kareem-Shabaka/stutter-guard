using DpcTweaker.Core.Detection;
using DpcTweaker.Core.Diagnostics;
using DpcTweaker.Core.Native;
using DpcTweaker.Core.Infra;
using DpcTweaker.Core.Interrupts;
using DpcTweaker.Core.Model;
using DpcTweaker.Core.Tweaks;

namespace DpcTweaker.Core;

public sealed record TweakResult(ITweak Tweak, bool Ok, string? Error);

/// <summary>Holds the profile, journal, options and catalog; applies and reverts tweaks.</summary>
public sealed class TweakEngine
{
    private readonly Dictionary<string, string> _lastErrors = new();
    private BackupSet? _sessionBackup;

    public TweakEngine(SystemProfile profile, Journal journal)
    {
        Profile = profile;
        Journal = journal;

        // Keep undo-all.ps1 in lockstep with journal.json: it is rewritten every time the
        // journal is flushed, which now happens before each change rather than after it.
        Journal.SidecarWriter = j => JournalRestorer.WriteUndoScripts(j, _sessionBackup);

        RepairOrphanedEntries();

        Options = AppOptions.Load(journal, profile);
        Rebuild();
    }

    public SystemProfile Profile { get; }
    public Journal Journal { get; }
    public AppOptions Options { get; private set; }
    public IReadOnlyList<ITweak> Tweaks { get; private set; } = Array.Empty<ITweak>();
    public BackupSet? SessionBackup => _sessionBackup;

    /// <summary>The saved core benchmark, if one was run.</summary>
    public BenchResult? Bench { get; private set; }

    /// <summary>Where each device's interrupts go (from the topology, the benchmark and the device scan).</summary>
    public AffinityPlan Plan { get; private set; } = new();

    /// <summary>The last interrupt check this session (for the scan report).</summary>
    public CheckResult? LastCheck { get; set; }

    /// <summary>Follow-up steps (adapter restarts) that failed in the last apply or revert.</summary>
    public List<string> LastFinishErrors { get; } = new();

    public event Action? CatalogChanged;

    /// <summary>
    /// Moves journal entries that no tweak owns any more onto the tweak that should own them.
    /// Without this they can never be reverted one by one and they keep showing up in undo-all.ps1.
    /// </summary>
    private void RepairOrphanedEntries()
    {
        int moved = Journal.RebindTweak("power.plan", PowerModule.PlanActiveId);
        if (moved > 0)
        {
            // Flush, not Save: the repaired journal and undo-all.ps1 must not drift apart.
            Journal.Flush();
            Log.Info($"Journal: moved {moved} orphaned \"power.plan\" entry/entries to {PowerModule.PlanActiveId}.");
        }
    }

    public void SetOptions(AppOptions options)
    {
        Options = options;
        try { Options.Save(Journal); }
        catch (Exception ex) { Log.Error("Options not saved: " + ex.Message); }
        Rebuild();
    }

    public void Rebuild()
    {
        var list = new List<ITweak>();
        list.AddRange(PowerModule.Build(Profile, Journal, () => Options));
        list.AddRange(AmdGpuKeys.Build(Profile, Journal));
        // No graphics-stack tweak here any more: MPO off is a diagnostic workaround, not a tune.
        // MpoOverride offers to undo any override that is already on the machine.
        list.AddRange(BootTasksModule.Build(Profile, Journal, () => Options));

        Bench = BenchResult.Load(Journal);
        Plan = AffinityPlanner.Plan(Profile.Cpu, Profile.CpuSets, Bench, Profile.Devices, Options.KeepValidPins, Options.HybridDevices);
        // Placement needs cores that will not park under the device. Two paths cannot promise that, so
        // they do not build pins at all rather than pinning and warning:
        //   Balanced                          - Windows keeps core placement, including parking.
        //   parking still active on a CPU     - CurrentActive + DevicesOnly leaves CPMINCORES stock, and
        //   that needs parking held off         on a dual-CCD or hybrid CPU the pinned core can sleep.
        // Exception by design: a path where nothing depends on parking (single-CCD AMD, classic Intel,
        // no P/E split) applies normally, because there is no cross-domain placement to protect.
        if (Plan.Applicable)
        {
            bool balanced = Options.PlanTarget == PlanTargetKind.Balanced;
            bool parkingActive = Profile.ParkingDependentCpu && !PowerSettings.ParkingHeldOff(Options);
            if (balanced)
                Plan.Suppress("The Balanced path leaves core placement to Windows, which can park the cores " +
                              "this app would pin devices to. Device interrupts are not placed on this path: choose " +
                              "the TunedUltimate plan to place them, which keeps every core unparked.");
            else if (parkingActive)
                Plan.Suppress("Windows core parking is still active on this path, so a core a device is pinned to " +
                              "could park and every interrupt would pay a wake-up first. Device interrupts are not " +
                              "placed here: use the TunedUltimate plan (cores unparked), or the Balanced path and let " +
                              "Windows place them.");
        }
        list.AddRange(InterruptsModule.Build(Profile, Journal, Options, Plan));
        list.AddRange(NetworkModule.Build(Profile, Journal));
        list.AddRange(ServicesModule.Build(Profile, Journal, Options));
        list.AddRange(TasksModule.Build(Profile, Journal, Options));
        // After every module, because one of the notes exists only when some module left a revert-only
        // row, and those come from Power, Interrupts, Services and Tasks alike.
        AddPlanNotes(list);
        Tweaks = list;
        CatalogChanged?.Invoke();
    }

    /// <summary>
    /// The three notes the Interrupts screen shows under the plan. They are added after the modules are
    /// built because two of them describe state the current path does not act on - a leftover row, or a
    /// plan Windows is free to park - and that is exactly the state the user has to be told about.
    /// </summary>
    private void AddPlanNotes(List<ITweak> built)
    {
        // M1: the Balanced path (or a suppressed CurrentActive) builds no pins, but earlier runs may
        // have left device pins or CPU power settings on this machine. Those values are live, so the
        // tab has to say so where the user will look rather than only in the rows below.
        if (!Plan.Applicable && built.Any(t => t is LeftoverTweak or LeftoverAffinityTweak))
            Plan.Notes.Add("Settings from an earlier configuration (device pins or CPU power settings) are " +
                           "still active on this PC. Use the leftover rows below or on the Power plan tab to revert them.");

        // A laptop on the CurrentActive path tunes whatever plan the vendor tool switched to. If that
        // plan still allows parking, a pinned core can sleep under the interrupt, which is worse than
        // not pinning. Read live, because the values change when the vendor tool changes mode.
        if (Options.PlanTarget == PlanTargetKind.CurrentActive && Profile.ParkingDependentCpu && Plan.Applicable &&
            PowerApi.GetActive() is { } active)
        {
            uint? min = PowerApi.ReadAc(active, PowerSettings.Proc, PowerSettings.CoreParkingAc);
            uint? min1 = Profile.Cpu.Cpus.Any(c => c.EfficiencyClass > 0)
                ? PowerApi.ReadAc(active, PowerSettings.Proc, PowerSettings.CoreParkingAcClass1)
                : null;
            if (ParkingWarning(Options.PlanTarget, Profile.ParkingDependentCpu, Plan.Applicable,
                    Profile.IsLaptop, Options.ApplyOnBattery, min, min1) is { } parking)
                Plan.Notes.Add(parking);
        }

        // P-E: on a dual-CCD X3D part, AMD's service can move a game onto the V-Cache CCD and park or
        // demote the other one - which is where this app just pinned the devices. The two mechanisms
        // are not enemies, but the user has to know which one owns CCD placement on the path chosen.
        if (Profile.Cpu.IsDualCcdX3D && Options.PlanTarget != PlanTargetKind.Balanced && Profile.Software.AmdVCacheService)
            Plan.Notes.Add("AMD's 3D V-Cache Performance Optimizer service (amd3dvcacheSvc) is installed. " +
                           "When Xbox Game Bar detects a game, that driver tries to park/demote the non-V-Cache CCD - " +
                           "which is the CCD holding your pinned device interrupts. On the TunedUltimate path, " +
                           "Process Lasso (or CPU Sets) owns CCD placement.");
    }

    /// <summary>
    /// The note for a path whose plan still allows core parking, or null when nothing is wrong. Kept as
    /// a pure function of the reads so each branch is testable without touching a live plan.
    /// </summary>
    /// <param name="minCores">CPMINCORES on the active plan; null when it could not be read.</param>
    /// <param name="minCoresClass1">CPMINCORES1, only asked for on a CPU that reports a class 1.</param>
    public static string? ParkingWarning(PlanTargetKind target, bool parkingDependent, bool applicable, bool laptop,
        bool applyOnBattery, uint? minCores, uint? minCoresClass1)
    {
        if (target != PlanTargetKind.CurrentActive || !parkingDependent || !applicable) return null;
        bool parked = minCores is < 100;
        bool parkedClass1 = minCoresClass1 is < 100;
        if (!parked && !parkedClass1) return null;
        string text = "The active power plan currently allows core parking (minimum unparked cores < 100%). " +
                      "Apply the Power plan tab so pinned interrupt cores cannot sleep under load.";
        if (laptop && !applyOnBattery)
            text += " On battery the plan switches to its DC values, where parking stays active unless " +
                    "\"Apply on battery too\" is enabled.";
        return text;
    }

    public IEnumerable<ITweak> InModule(string module) => Tweaks.Where(t => t.Info.Module == module);

    public string? LastError(string tweakId) => _lastErrors.TryGetValue(tweakId, out var e) ? e : null;

    private TweakContext NewContext() => new(Journal, () => _sessionBackup ??= BackupSet.Create());

    /// <summary>Saves a new benchmark and rebuilds the plan.</summary>
    public void SetBench(BenchResult? bench)
    {
        try
        {
            if (bench is null) BenchResult.Clear(Journal);
            else bench.Save(Journal);
        }
        catch (Exception ex) { Log.Error("Benchmark not saved: " + ex.Message); }
        Rebuild();
    }

    /// <summary>One follow-up per key (an adapter restart after several of its properties changed).</summary>
    private void RunFinishers(IEnumerable<ITweak> done)
    {
        LastFinishErrors.Clear();
        foreach (var f in done.OfType<IBatchFinisher>().GroupBy(f => f.FinishKey).Select(g => g.First()))
        {
            try { f.Finish(); }
            catch (Exception ex)
            {
                LastFinishErrors.Add(ex.Message);
                Log.Warn($"Finish {f.FinishKey} failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Write-ahead apply: each tweak runs inside a journal transaction, so its before-states are
    /// on disk (and in undo-all.ps1) before it changes anything. If it throws, the transaction is
    /// rolled back and the tweak is not marked applied, leaving no phantom records behind.
    /// </summary>
    public IReadOnlyList<TweakResult> Apply(IEnumerable<ITweak> tweaks)
    {
        var ctx = NewContext();
        var results = new List<TweakResult>();
        foreach (var t in tweaks)
        {
            var txn = Journal.BeginTransaction(t.Info.Id);
            try
            {
                try
                {
                    // Each RecordOriginal writes journal.json before it touches the system; a failed
                    // WAL write throws here, so the tweak aborts without changing anything.
                    t.Apply(ctx);
                    Journal.MarkApplied(t.Info.Id);
                    _lastErrors.Remove(t.Info.Id);
                    results.Add(new TweakResult(t, true, null));
                    Log.Info("Applied " + t.Info.Id);
                }
                catch (Exception ex)
                {
                    bool rolledBack = Journal.RollbackTransaction(txn);
                    _lastErrors[t.Info.Id] = ex.Message;
                    results.Add(new TweakResult(t, false, ex.Message));
                    Log.Error($"Apply {t.Info.Id} failed: {ex.Message}" + (rolledBack ? " (journal entries rolled back)" : ""));
                }
            }
            finally
            {
                // Close the transaction first, then flush once with the sidecar: journal.json and
                // undo-all.ps1 are rewritten per tweak instead of per recorded entry.
                Journal.CommitTransaction(txn);
                Journal.Flush();
            }
        }
        RunFinishers(results.Where(r => r.Ok).Select(r => r.Tweak));
        if (results.Any(r => r.Ok)) DriftWatcher.Remember(this);
        return results;
    }

    public IReadOnlyList<TweakResult> Revert(IEnumerable<ITweak> tweaks)
    {
        var ctx = NewContext();
        var results = new List<TweakResult>();
        foreach (var t in tweaks)
        {
            try
            {
                t.Revert(ctx);
                Journal.ClearTweak(t.Info.Id);
                _lastErrors.Remove(t.Info.Id);
                results.Add(new TweakResult(t, true, null));
            }
            catch (Exception ex)
            {
                _lastErrors[t.Info.Id] = ex.Message;
                results.Add(new TweakResult(t, false, ex.Message));
                Log.Error($"Revert {t.Info.Id} failed: {ex.Message}");
            }
            finally
            {
                Journal.Flush(includeSidecar: false);
            }
        }
        RunFinishers(results.Where(r => r.Ok).Select(r => r.Tweak));
        Journal.Flush();   // one undo-all.ps1 rewrite for the whole revert batch
        return results;
    }

    /// <summary>Back to the state before the app was first used, from the oldest journal entries.</summary>
    public (int Restored, List<string> Errors) RevertAll()
    {
        // Stop the timer tool first so its file can be deleted.
        foreach (var p in System.Diagnostics.Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Paths.TimerExeName)))
        {
            try { p.Kill(); p.WaitForExit(3000); } catch { }
        }
        var r = JournalRestorer.RestoreAll(Journal);
        _lastErrors.Clear();
        DevicePower.Invalidate();
        JournalRestorer.WriteUndoScripts(Journal, _sessionBackup);
        return r;
    }

    // ---------------------------------------------------------------- drift detection & healing

    /// <summary>
    /// Tweaks the app applied whose live state no longer matches. Every tweak answers this through
    /// TweakBase.Detect: a mismatch plus an "applied" journal mark is exactly Drifted. That covers
    /// AMD GPU keys, MSI mode, interrupt affinity, the NIC power/RSS keywords and the global timer
    /// switch alike, because each of them reads its own state back from the system.
    /// </summary>
    public static IReadOnlyList<ITweak> FindDrifted(IEnumerable<ITweak> tweaks)
    {
        var list = new List<ITweak>();
        foreach (var t in tweaks)
        {
            try
            {
                if (t.Detect().Status == TweakStatus.Drifted) list.Add(t);
            }
            catch (Exception ex)
            {
                Log.Warn($"Couldn't read {t.Info.Id} while checking for drift: {ex.Message}");
            }
        }
        return list;
    }

    /// <summary>How many applied tweaks have been undone behind the app's back (driver update, Windows Update).</summary>
    public int DriftedCount => FindDrifted(Tweaks).Count;

    public IReadOnlyList<ITweak> DriftedTweaks() => FindDrifted(Tweaks);

    /// <summary>
    /// Re-applies the given tweaks inside journal transactions. A second apply does not duplicate
    /// the recorded before-state: RecordOriginal keeps only the first entry per (tweak, kind,
    /// target), so the baseline from the original run survives and revert still works.
    /// </summary>
    public IReadOnlyList<TweakResult> Heal(IEnumerable<ITweak> tweaks, IProgress<string>? progress = null)
    {
        var ctx = NewContext();
        var results = new List<TweakResult>();
        foreach (var t in tweaks)
        {
            progress?.Report("Re-applying " + t.Info.Title + "...");
            var txn = Journal.BeginTransaction(t.Info.Id);
            try
            {
                try
                {
                    t.Apply(ctx);
                    Journal.MarkApplied(t.Info.Id);
                    _lastErrors.Remove(t.Info.Id);
                    results.Add(new TweakResult(t, true, null));
                    Log.Info("Re-applied " + t.Info.Id);
                }
                catch (Exception ex)
                {
                    Journal.RollbackTransaction(txn);
                    _lastErrors[t.Info.Id] = ex.Message;
                    results.Add(new TweakResult(t, false, ex.Message));
                    Log.Error($"Re-apply {t.Info.Id} failed: {ex.Message}");
                }
            }
            finally
            {
                Journal.CommitTransaction(txn);
                Journal.Flush();
            }
        }
        // The same follow-up an Apply runs. A healed NIC property is written to the registry and only
        // Finish() restarts the adapter - which is why NicPropertyTweak reports NeedsReboot = false.
        // Healing without it left the registry changed and the running driver unaware of it.
        RunFinishers(results.Where(r => r.Ok).Select(r => r.Tweak));
        progress?.Report(results.All(r => r.Ok)
            ? $"{results.Count} drifted tweak(s) put back."
            : $"{results.Count(r => r.Ok)} of {results.Count} drifted tweak(s) put back.");
        if (results.Any(r => r.Ok)) DriftWatcher.Remember(this);
        return results;
    }

    /// <summary>Re-applies everything that has drifted since it was applied.</summary>
    public IReadOnlyList<TweakResult> HealDrifted(IProgress<string>? progress = null) =>
        Heal(DriftedTweaks(), progress);

    /// <summary>
    /// Drift plus its likely cause, for the Home banner. Compares the live system with the journal
    /// and with the driver fingerprint recorded at the last apply.
    /// </summary>
    public DriftReport ScanDrift() => DriftWatcher.Scan(this);

    /// <summary>
    /// MPO-disabling overrides present on this machine, from any source. Read-only: the app no longer
    /// sets MPO off itself, it only offers to undo an override that is already there.
    /// </summary>
    public IReadOnlyList<MpoFinding> MpoOverrides() => MpoOverride.Find(Profile);

    /// <summary>
    /// Latency costs on this machine that no tweak can remove, with the display and DPC evidence
    /// behind each one. Read-only; samples DPC counters for about a second.
    /// </summary>
    public IReadOnlyList<TriageFinding> Triage(TimeSpan? sample = null)
    {
        var window = sample ?? TimeSpan.FromSeconds(1);
        var displays = Displays.Active();

        IReadOnlyList<CpuRates>? rates = null;
        try
        {
            int n = Math.Max(1, Profile.Cpu.LogicalCount);
            var a = SysInfo.Counters(n);
            Thread.Sleep((int)window.TotalMilliseconds);
            var b = SysInfo.Counters(n);
            rates = CpuRates.Between(a, b, window.TotalSeconds);
        }
        catch (Exception ex)
        {
            // The display findings are still worth showing without the counters.
            Log.Warn("DPC sample failed: " + ex.Message);
        }

        // ReservedCpus is filled in whether or not this path places interrupts. On the Balanced path
        // Applicable is false because no pins are built, but the cores games use are still known - and
        // "DPC work is landing on game cores" is exactly the check that path needs.
        var gameCores = Plan.ReservedCpus;

        // Processes pinned onto the cores the plan reserves: the scheduler has no way to know a core is
        // servicing a device, so anything restricted there competes with every interrupt. Read-only and
        // unelevated; the unreadable count is reported rather than folded into a clean zero.
        IReadOnlyList<OverlappingProcess> overlap = Array.Empty<OverlappingProcess>();
        int unreadable = 0, protectedCount = 0;
        IReadOnlyList<UnreadableProcess> unreadableNames = Array.Empty<UnreadableProcess>();
        bool elevated = false;
        try
        {
            // Live pins, not the plan's intent: the plan is where devices would go, and the question is
            // where interrupts land now. On a machine with nothing pinned there is no interrupt core to
            // defend, so the check is skipped rather than answered from a plan that is not in effect.
            var interruptCpus = ProcessOverlap.LiveCpus(Profile.Devices).ToList();
            if (interruptCpus.Count > 0)
            {
                overlap = ProcessOverlap.Sample(interruptCpus, Profile.Cpu.LogicalCount, out unreadable, out protectedCount, out unreadableNames);
                elevated = ProcessOverlap.IsElevated();
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Process overlap check failed: " + ex.Message);
        }

        // Which devices this app has on which core. Windows will not say which driver a DPC belongs to, but
        // naming the devices pinned to the hot core turns "go and find out" into something the user can act on.
        var devicesByCpu = new Dictionary<int, List<string>>();
        void Note(int cpu, string name)
        {
            if (!devicesByCpu.TryGetValue(cpu, out var list)) devicesByCpu[cpu] = list = new List<string>();
            if (!list.Contains(name)) list.Add(name);
        }

        // Live pins first: they describe what is actually on the core now.
        foreach (var d in Profile.Devices)
        {
            if (d.ExistingMask is not ulong mask || mask == 0) continue;
            for (int c = 0; c < 64; c++)
                if ((mask & (1UL << c)) != 0) Note(c, d.Name);
        }

        // Nothing pinned yet (a fresh machine runs this scan before applying anything): fall back to what
        // the plan would place, so the advice is still specific rather than blank.
        if (devicesByCpu.Count == 0 && Plan.Applicable)
            foreach (var a in Plan.Assignments)
                foreach (int c in a.MaskCpus) Note(c, a.Device.Name);

        var byCpu = devicesByCpu.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value);
        return LatencyTriage.Ordered(LatencyTriage.Scan(displays, rates, gameCores, overlap, unreadable,
            byCpu, protectedCount, unreadableNames, elevated));
    }

    /// <summary>
    /// Deletes whatever MPO overrides were found, inside ordinary journal transactions so the
    /// previous values are recorded first and Revert puts them back.
    /// </summary>
    public IReadOnlyList<TweakResult> RemoveMpoOverrides(IProgress<string>? progress = null) =>
        Heal(MpoOverride.BuildRemovers(MpoOverrides(), Journal), progress);

    /// <summary>The status a row should show, folding in the last apply/revert error.</summary>
    public TweakState StateOf(ITweak t)
    {
        var s = t.Detect();
        if (LastError(t.Info.Id) is { } err && s.Status is not (TweakStatus.Applied or TweakStatus.PendingReboot))
            return s with { Status = TweakStatus.Failed, Note = err };
        return s;
    }
}