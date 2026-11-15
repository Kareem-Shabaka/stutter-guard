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
        if (Plan.Applicable && Profile.ParkingDependentCpu && Options.PlanTarget == PlanTargetKind.Balanced)
            Plan.Notes.Add("With Windows parking (Balanced), the device cores can sleep deeply while a game runs on the other cores, so each interrupt pays a wake-up first. The Tuned Ultimate plan (light C-states) avoids that.");
        list.AddRange(InterruptsModule.Build(Profile, Journal, Options, Plan));
        list.AddRange(NetworkModule.Build(Profile, Journal));
        list.AddRange(ServicesModule.Build(Profile, Journal, Options));
        list.AddRange(TasksModule.Build(Profile, Journal, Options));
        Tweaks = list;
        CatalogChanged?.Invoke();
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

        var gameCores = Plan.Applicable ? Plan.ReservedCpus : new List<int>();

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