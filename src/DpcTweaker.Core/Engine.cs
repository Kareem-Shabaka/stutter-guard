using DpcTweaker.Core.Detection;
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

    public void SetOptions(AppOptions options)
    {
        Options = options;
        Options.Save(Journal);
        Rebuild();
    }

    public void Rebuild()
    {
        var list = new List<ITweak>();
        list.AddRange(PowerModule.Build(Profile, Journal, () => Options));
        list.AddRange(AmdGpuKeys.Build(Profile, Journal));
        list.AddRange(GraphicsModule.Build(Profile, Journal));
        list.AddRange(BootTasksModule.Build(Profile, Journal, () => Options));

        Bench = BenchResult.Load(Journal);
        Plan = AffinityPlanner.Plan(Profile.Cpu, Profile.CpuSets, Bench, Profile.Devices, Options.KeepValidPins);
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
        if (bench is null) BenchResult.Clear(Journal);
        else bench.Save(Journal);
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

    public IReadOnlyList<TweakResult> Apply(IEnumerable<ITweak> tweaks)
    {
        var ctx = NewContext();
        var results = new List<TweakResult>();
        foreach (var t in tweaks)
        {
            try
            {
                t.Apply(ctx);
                Journal.MarkApplied(t.Info.Id);
                _lastErrors.Remove(t.Info.Id);
                results.Add(new TweakResult(t, true, null));
                Log.Info("Applied " + t.Info.Id);
            }
            catch (Exception ex)
            {
                _lastErrors[t.Info.Id] = ex.Message;
                results.Add(new TweakResult(t, false, ex.Message));
                Log.Error($"Apply {t.Info.Id} failed: {ex.Message}");
            }
            finally
            {
                try { Journal.Save(); } catch (Exception ex) { Log.Error("Journal save failed: " + ex.Message); }
            }
        }
        RunFinishers(results.Where(r => r.Ok).Select(r => r.Tweak));
        JournalRestorer.WriteUndoScripts(Journal, _sessionBackup);
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
                try { Journal.Save(); } catch { }
            }
        }
        RunFinishers(results.Where(r => r.Ok).Select(r => r.Tweak));
        JournalRestorer.WriteUndoScripts(Journal, _sessionBackup);
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

    /// <summary>The status a row should show, folding in the last apply/revert error.</summary>
    public TweakState StateOf(ITweak t)
    {
        var s = t.Detect();
        if (LastError(t.Info.Id) is { } err && s.Status is not (TweakStatus.Applied or TweakStatus.PendingReboot))
            return s with { Status = TweakStatus.Failed, Note = err };
        return s;
    }
}
