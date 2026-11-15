using System.Collections.ObjectModel;
using System.Globalization;
using DpcTweaker.Core;
using DpcTweaker.Core.Detection;
using DpcTweaker.Core.Diagnostics;
using DpcTweaker.Core.Infra;
using DpcTweaker.Core.Interrupts;
using DpcTweaker.Core.Model;
using DpcTweaker.Core.Reports;
using DpcTweaker.Core.Tweaks;

namespace DpcTweaker.UI;

public abstract class PageViewModel : ObservableObject
{
    private string _message = "";
    private bool _busy;

    protected PageViewModel(string title, string icon)
    {
        Title = title;
        Icon = icon;
    }

    public string Title { get; }

    /// <summary>A Segoe Fluent Icons / MDL2 glyph for the navigation rail.</summary>
    public string Icon { get; }

    public string Message
    {
        get => _message;
        set => Set(ref _message, value);
    }

    public bool Busy
    {
        get => _busy;
        protected set => Set(ref _busy, value);
    }

    public virtual Task RefreshAsync() => Task.CompletedTask;
}

public sealed class InfoLine
{
    public InfoLine(string label, string value)
    {
        Label = label;
        Value = value;
    }

    public string Label { get; }
    public string Value { get; }
}

public sealed class HomePageViewModel : PageViewModel
{
    private readonly MainViewModel _main;
    private CancellationTokenSource? _cts;
    private bool _runningBench;
    private double _benchProgress;
    private string _benchProgressText = "";
    private string _benchStatus = "";
    private bool _hasBench;

    public HomePageViewModel(MainViewModel main) : base("Home", "")
    {
        _main = main;
        SaveReportCommand = new RelayCommand(SaveReport);
        RescanCommand = new RelayCommand(() => _ = _main.InitializeAsync());
        RunBenchmarkCommand = new RelayCommand(() => _ = RunBenchmarkAsync(), () => !RunningBench);
        CancelBenchmarkCommand = new RelayCommand(() => _cts?.Cancel(), () => RunningBench);
        ForgetBenchmarkCommand = new RelayCommand(ForgetBenchmark, () => !RunningBench && HasBench);
        SubmitReportCommand = new RelayCommand(() => _ = SubmitReportAsync());
        SendFeedbackCommand = new RelayCommand(() => _ = SendFeedbackAsync());
        ReportBugCommand = SendFeedbackCommand;
        HealDriftedCommand = new RelayCommand(() => _ = HealDriftedAsync(), () => HasDrift && !Busy);
        OpenSafetyCommand = new RelayCommand(() => _main.SelectedPage = _main.Safety);
        RemoveMpoCommand = new RelayCommand(() => _ = RemoveMpoAsync(), () => HasMpoOverride && !Busy);
        KeepMpoCommand = new RelayCommand(KeepMpo, () => HasMpoOverride && !Busy);
        RunTriageCommand = new RelayCommand(() => _ = RunTriageAsync(), () => !Busy);
    }

    private DriftReport _drift = DriftReport.Clean;
    private string _driftMessage = "";

    /// <summary>Drift report from the last scan (startup, rescan, or after a heal).</summary>
    public DriftReport Drift
    {
        get => _drift;
        private set
        {
            if (!Set(ref _drift, value)) return;
            OnPropertyChanged(nameof(HasDrift));
            OnPropertyChanged(nameof(DriftHeadline));
            OnPropertyChanged(nameof(DriftDetail));
            OnPropertyChanged(nameof(DriftSubsystems));
            HealDriftedCommand.Refresh();
        }
    }

    public bool HasDrift => _drift.HasDrift;
    public string DriftHeadline => _drift.Headline;
    public string DriftDetail => _drift.Detail;

    /// <summary>"Affected: GPU driver keys (2), Interrupts / MSI (4)."</summary>
    public string DriftSubsystems => _drift.HasDrift ? "Affected: " + _drift.SubsystemText + "." : "";
    public ObservableCollection<InfoLine> DriftLines { get; } = new();

    public string DriftMessage { get => _driftMessage; private set => Set(ref _driftMessage, value); }

    public RelayCommand HealDriftedCommand { get; }
    public RelayCommand OpenSafetyCommand { get; }

    /// <summary>Re-scans for drift. Read-only; cheap enough to run on startup and on rescan.</summary>
    public void RefreshDrift()
    {
        var engine = _main.Engine;
        if (engine is null) return;
        var report = engine.ScanDrift();
        DriftLines.Clear();
        foreach (var t in report.Drifted.Take(40))
            DriftLines.Add(new InfoLine(t.Info.Module, t.Info.Title));
        Drift = report;
        _main.OnDriftScanned(report.Count);
    }

    private async Task HealDriftedAsync()
    {
        var engine = _main.Engine;
        if (engine is null || !HasDrift) return;
        var drifted = _drift.Drifted.ToList();
        if (!_main.Ui.Confirm("Re-apply drifted tweaks",
                (DriftHeadline.Length > 0 ? DriftHeadline + "\n\n" : "") + DriftDetail +
                "\n\nSome of these need a restart to take effect. Re-apply now?"))
            return;

        Busy = true;
        DriftMessage = "Re-applying " + drifted.Count + " tweak(s)...";
        try
        {
            var progress = new Progress<string>(s => DriftMessage = s);
            var results = await Task.Run(() => engine.Heal(drifted, progress));
            int ok = results.Count(r => r.Ok);
            var failed = results.Where(r => !r.Ok).ToList();
            DriftMessage = $"Re-applied {ok} of {results.Count}." +
                           (failed.Count > 0 ? " Failed: " + string.Join("; ", failed.Select(f => $"{f.Tweak.Info.Title}: {f.Error}")) : "");
            if (results.Any(r => r.Ok && r.Tweak.Info.NeedsReboot)) _main.ForceRestartNeeded();
            await _main.RefreshAllAsync();
            RefreshDrift();
            RefreshMpo();
        }
        catch (Exception ex)
        {
            DriftMessage = "Re-apply failed: " + ex.Message;
            Log.Error("Home HealDrifted failed: " + ex);
        }
        finally
        {
            Busy = false;
            HealDriftedCommand.Refresh();
        }
    }

    // ---------------------------------------------------------------- latency triage

    /// <summary>Costs found on this machine that the tweaks cannot remove, worst first.</summary>
    public ObservableCollection<TriageFinding> Triage { get; } = new();

    private string _triageMessage = "";

    public string TriageMessage
    {
        get => _triageMessage;
        private set => Set(ref _triageMessage, value);
    }

    public bool HasTriage => Triage.Count > 0;

    public RelayCommand RunTriageCommand { get; }

    /// <summary>
    /// Samples displays and DPC counters for about a second and reports what no tweak can fix.
    /// Deliberately a button, not a startup scan: the sample costs a second and is only useful
    /// when someone is actually chasing a stutter.
    /// </summary>
    private async Task RunTriageAsync()
    {
        var engine = _main.Engine;
        if (engine is null) return;
        Busy = true;
        TriageMessage = "Sampling displays and DPC counters...";
        try
        {
            var found = await Task.Run(() => engine.Triage());
            Triage.Clear();
            foreach (var f in found) Triage.Add(f);
            OnPropertyChanged(nameof(HasTriage));
            TriageMessage = found.Count == 0
                ? "Nothing found that would cost DPC latency on this machine."
                : $"{found.Count} finding(s). The ones marked as needing a manual fix cannot be removed by interrupt pinning.";
        }
        catch (Exception ex)
        {
            TriageMessage = "Triage failed: " + ex.Message;
            Log.Error("Triage failed: " + ex);
        }
        finally
        {
            Busy = false;
            RunTriageCommand.Refresh();
        }
    }

    // ---------------------------------------------------------------- MPO override

    private IReadOnlyList<MpoFinding> _mpo = Array.Empty<MpoFinding>();
    private bool _mpoDismissed;
    private string _mpoMessage = "";

    /// <summary>True when this machine still carries an MPO-disabling override from any source.</summary>
    public bool HasMpoOverride => _mpo.Count > 0 && !_mpoDismissed;

    public string MpoHeadline => _mpo.Count == 1
        ? "MPO overlay is switched off by a registry override"
        : _mpo.Count + " MPO overrides are still on this machine";

    /// <summary>Which values were found and where, so the user can judge before removing them.</summary>
    public string MpoDetail => _mpo.Count == 0 ? "" : MpoOverride.Describe(_mpo);

    public string MpoMessage
    {
        get => _mpoMessage;
        private set => Set(ref _mpoMessage, value);
    }

    public RelayCommand RemoveMpoCommand { get; }
    public RelayCommand KeepMpoCommand { get; }

    /// <summary>Re-checks for MPO overrides. Read-only and cheap: two values per GPU at most.</summary>
    public void RefreshMpo()
    {
        var engine = _main.Engine;
        if (engine is null) return;
        try
        {
            _mpo = engine.MpoOverrides();
            _mpoDismissed = MpoOverride.IsDismissed(engine, _mpo);
        }
        catch (Exception ex)
        {
            Log.Warn("MPO check failed: " + ex.Message);
            _mpo = Array.Empty<MpoFinding>();
        }
        OnPropertyChanged(nameof(HasMpoOverride));
        OnPropertyChanged(nameof(MpoHeadline));
        OnPropertyChanged(nameof(MpoDetail));
        RemoveMpoCommand.Refresh();
        KeepMpoCommand.Refresh();
    }

    /// <summary>"Keep it": leave the registry alone and stop asking about these exact values.</summary>
    private void KeepMpo()
    {
        var engine = _main.Engine;
        if (engine is null) return;
        MpoOverride.Dismiss(engine, _mpo);
        _mpoDismissed = true;
        OnPropertyChanged(nameof(HasMpoOverride));
        RemoveMpoCommand.Refresh();
        KeepMpoCommand.Refresh();
    }

    private async Task RemoveMpoAsync()
    {
        var engine = _main.Engine;
        if (engine is null || !HasMpoOverride) return;

        // Saying yes here is the whole consent step: the values are listed on the card, and the
        // journal records each one before it is deleted, so Revert can put them back.
        if (!_main.Ui.Confirm("Remove the MPO override",
                MpoHeadline + ".\n\nFound: " + MpoDetail + "\n\n" +
                "Removing it puts Multi-Plane Overlay back on Windows default. That is the right call unless this override " +
                "was added to work around a specific problem you still have. Your previous values are recorded first, so " +
                "Revert on the GPU screen puts them straight back.\n\nRemove now?"))
            return;

        Busy = true;
        MpoMessage = "Removing the override...";
        try
        {
            var results = await Task.Run(() => engine.RemoveMpoOverrides());
            int ok = results.Count(r => r.Ok);
            var failed = results.Where(r => !r.Ok).ToList();
            MpoMessage = ok == results.Count
                ? "Removed. Restart (or sign out) for the compositor to pick up the default."
                : $"Removed {ok} of {results.Count}. " +
                  string.Join("; ", failed.Select(f => $"{f.Tweak.Info.Title}: {f.Error}"));
            if (results.Any(r => r.Ok)) _main.ForceRestartNeeded();
            await _main.RefreshAllAsync();
            RefreshMpo();
        }
        catch (Exception ex)
        {
            MpoMessage = "Could not remove it: " + ex.Message;
            Log.Error("MPO removal failed: " + ex);
        }
        finally
        {
            Busy = false;
            RemoveMpoCommand.Refresh();
        }
    }

    public ObservableCollection<InfoLine> Lines { get; } = new();
    public ObservableCollection<string> Warnings { get; } = new();
    public ObservableCollection<InfoLine> Counts { get; } = new();
    public ObservableCollection<InfoLine> BenchLines { get; } = new();
    public bool HasWarnings => Warnings.Count > 0;

    public string BenchStatus
    {
        get => _benchStatus;
        private set => Set(ref _benchStatus, value);
    }

    public bool HasBench
    {
        get => _hasBench;
        private set
        {
            if (Set(ref _hasBench, value)) ForgetBenchmarkCommand.Refresh();
        }
    }

    public bool RunningBench
    {
        get => _runningBench;
        private set
        {
            if (!Set(ref _runningBench, value)) return;
            RunBenchmarkCommand.Refresh();
            CancelBenchmarkCommand.Refresh();
            ForgetBenchmarkCommand.Refresh();
        }
    }

    public double BenchProgress { get => _benchProgress; private set => Set(ref _benchProgress, value); }
    public string BenchProgressText { get => _benchProgressText; private set => Set(ref _benchProgressText, value); }

    public RelayCommand SaveReportCommand { get; }
    public RelayCommand RescanCommand { get; }
    public RelayCommand RunBenchmarkCommand { get; }
    public RelayCommand CancelBenchmarkCommand { get; }
    public RelayCommand ForgetBenchmarkCommand { get; }
    public RelayCommand SubmitReportCommand { get; }
    public RelayCommand SendFeedbackCommand { get; }
    public RelayCommand ReportBugCommand { get; }

    public void Load(SystemProfile p)
    {
        Lines.Clear();
        Lines.Add(new InfoLine("Windows", p.Windows.Display));
        if (!string.IsNullOrWhiteSpace(p.Manufacturer + p.Model)) Lines.Add(new InfoLine("Machine", $"{p.Manufacturer} {p.Model}".Trim()));
        Lines.Add(new InfoLine("Form factor", p.FormFactor.ToString()));
        Lines.Add(new InfoLine("CPU", p.CpuName));
        Lines.Add(new InfoLine("CPU layout", p.CpuKind));
        Lines.Add(new InfoLine("Topology", p.Cpu.Describe()));
        foreach (var g in p.Gpus)
            Lines.Add(new InfoLine("GPU", $"{g.Label}, driver {g.DriverVersion}"));
        Lines.Add(new InfoLine("Security", p.Security.Display));
        if (p.TimerResolution is { } tr) Lines.Add(new InfoLine("Timer now", $"{tr.Current / 10000.0:0.000} ms"));
        Lines.Add(new InfoLine("WHEA errors", p.WheaErrors14Days is null ? "unknown" : $"{p.WheaErrors14Days} in 14 days"));
        Lines.Add(new InfoLine("Process Lasso", p.Software.ProcessLasso ? "installed" : "not found"));

        Warnings.Clear();
        foreach (var w in p.Warnings) Warnings.Add(w);
        OnPropertyChanged(nameof(HasWarnings));
        LoadBench();
        RefreshDrift();
        RefreshMpo();
    }

    public void LoadBench()
    {
        var engine = _main.Engine;
        if (engine is null) return;
        var bench = engine.Bench;
        var plan = engine.Plan;
        BenchLines.Clear();
        if (bench is null)
        {
            BenchStatus = $"Not run yet. Takes about {CoreBenchmark.Estimate(engine.Profile.Cpu, InterruptOptionsViewModel.Samples, InterruptOptionsViewModel.IdleSeconds).TotalMinutes:0} minutes and changes nothing.";
            HasBench = false;
        }
        else
        {
            BenchStatus = "Saved: " + bench.Summary + ".";
            foreach (var c in bench.Cores.OrderBy(c => c.Cpu))
                BenchLines.Add(new InfoLine("CPU " + c.Cpu,
                    string.Format(CultureInfo.CurrentCulture, "wake-up late by {0:0.0} µs median, {1:0.0} µs p95, {2:0} µs max · workload {3:0.0} ms · Windows rank {4} · idle {5:0} interrupts/s{6}",
                        c.WakeMedianUs, c.WakeP95Us > 0 ? c.WakeP95Us : c.WakeP99Us, c.WakeMaxUs, c.SpeedMs, c.SchedulingClass, c.InterruptsPerSec,
                        plan.ReservedCpus.Contains(c.Cpu) ? " · kept free" : c.InterruptsPerSec > 1000 ? " (mostly devices pinned there now; not scored)" : "")));
            HasBench = true;
        }
    }

    private void ReportBench(string text, double fraction) => _main.Ui.Post(() =>
    {
        BenchProgressText = text;
        BenchProgress = Math.Clamp(fraction, 0, 1) * 100;
    });

    private async Task RunBenchmarkAsync()
    {
        var engine = _main.Engine;
        if (engine is null) return;
        if (!_main.Ui.Confirm("Core benchmark",
                $"The benchmark takes about {CoreBenchmark.Estimate(engine.Profile.Cpu, InterruptOptionsViewModel.Samples, InterruptOptionsViewModel.IdleSeconds).TotalMinutes:0} minutes and changes nothing.\n\n" +
                "Close games, browsers and monitoring apps, then leave the PC alone until it finishes (the first 30 seconds measure the idle load).\n\nStart now?"))
            return;

        _cts = new CancellationTokenSource();
        RunningBench = true;
        _main.SetGlobalBusy(true);
        Message = "";
        try
        {
            var result = await Task.Run(() => CoreBenchmark.Run(engine.Profile.Cpu, engine.Profile.CpuSets, InterruptOptionsViewModel.Samples, InterruptOptionsViewModel.IdleSeconds, ReportBench, _cts.Token));
            engine.SetBench(result);
            _main.ReloadPages();
            Message = "Core benchmark saved. The Interrupts plan now uses it; click 'Save scan report' to include the per-core measurements.";
        }
        catch (OperationCanceledException)
        {
            Message = "Benchmark cancelled. Nothing was saved.";
        }
        catch (Exception ex)
        {
            Message = ex.Message;
        }
        finally
        {
            RunningBench = false;
            _main.SetGlobalBusy(false);
            BenchProgressText = "";
            BenchProgress = 0;
        }
    }

    private void ForgetBenchmark()
    {
        var engine = _main.Engine;
        if (engine is null) return;
        if (!_main.Ui.Confirm("Forget the benchmark", "Go back to the estimated core order? Nothing on the PC changes.")) return;
        engine.SetBench(null);
        _main.ReloadPages();
    }

    public void UpdateCounts(IEnumerable<TweakRowViewModel> rows)
    {
        var list = rows.ToList();
        Counts.Clear();
        Counts.Add(new InfoLine("Applied", list.Count(r => r.Status == TweakStatus.Applied).ToString()));
        Counts.Add(new InfoLine("Pending restart", list.Count(r => r.Status == TweakStatus.PendingReboot).ToString()));
        Counts.Add(new InfoLine("Drifted", list.Count(r => r.Status == TweakStatus.Drifted).ToString()));
        Counts.Add(new InfoLine("Not applied", list.Count(r => r.Status == TweakStatus.NotApplied).ToString()));
        Counts.Add(new InfoLine("Failed", list.Count(r => r.Status == TweakStatus.Failed).ToString()));
    }

    private void SaveReport()
    {
        if (_main.Engine is null) return;
        try
        {
            string file = ScanReport.Write(_main.Engine);
            Message = "Saved " + file;
            _main.Ui.Open(file);
        }
        catch (Exception ex)
        {
            Message = "Couldn't save the report: " + ex.Message;
        }
    }

    private async Task SubmitReportAsync()
    {
        var engine = _main.Engine;
        if (engine is null) return;
        // Build the payload first, then show it in full. Whatever the user approves here is the exact
        // object that gets posted, so the preview cannot drift from what is actually sent.
        var payload = TelemetryClient.BuildDataReportPayload(engine);
        if (!_main.Ui.Review("Review the hardware report before sending",
            "This is everything that will be sent to the developer, in full. Nothing is sent until you press Send.",
            TelemetryClient.Preview(payload)))
            return;

        Message = "Submitting hardware report...";
        _main.SetGlobalBusy(true);
        try
        {
            var (ok, msg) = await TelemetryClient.SendDataReportAsync(payload);
            Message = msg;
            if (ok) _main.Ui.Inform("Report Submitted", msg);
            else _main.Ui.Inform("Submission Failed", msg);
        }
        finally
        {
            _main.SetGlobalBusy(false);
        }
    }

    private async Task SendFeedbackAsync()
    {
        var engine = _main.Engine;
        if (engine is null) return;

        string? text = _main.Ui.Prompt(
            "Send Feedback / Report an Issue",
            "Share your feedback, game results, feature ideas, or report a bug. Your hardware scan report is included automatically so the developer can optimize for your rig:\n\n(Optional: you may also include your Discord tag or email if you want a response)",
            "");

        if (string.IsNullOrWhiteSpace(text)) return;

        // Same rule as the hardware report: build once, show it, send exactly what was shown.
        var payload = TelemetryClient.BuildFeedbackPayload(engine, text);
        if (!_main.Ui.Review("Review your feedback before sending",
            "This is everything that will be sent, in full, including your message and any attached files. Nothing is sent until you press Send.",
            TelemetryClient.Preview(payload)))
            return;

        Message = "Sending feedback...";
        _main.SetGlobalBusy(true);
        try
        {
            var (ok, msg) = await TelemetryClient.SendFeedbackAsync(payload);
            Message = msg;
            if (ok) _main.Ui.Inform("Feedback Sent", msg);
            else _main.Ui.Inform("Submission Failed", msg);
        }
        finally
        {
            _main.SetGlobalBusy(false);
        }
    }
}

/// <summary>A module screen: the tweak rows with apply / revert, plus an optional options panel.</summary>
public sealed class ModulePageViewModel : PageViewModel
{
    private readonly MainViewModel _main;
    private readonly string _module;

    /// <summary>
    /// The cost tier used when a row first decides its tick. Held here, not read from Options on every
    /// row, so a rebuild can tell whether the preset changed (see Load).
    /// </summary>
    private TweakPreset _preset = TweakPreset.Safe;

    public ModulePageViewModel(MainViewModel main, string module, string title, string icon, string description,
        TweakPreset preset = TweakPreset.Safe)
        : base(title, icon)
    {
        _main = main;
        _module = module;
        _preset = preset;
        Description = description;
        ApplySelectedCommand = new RelayCommand(() => _ = ApplySelectedAsync(), () => !Busy);
        RevertSelectedCommand = new RelayCommand(() => _ = RevertSelectedAsync(), () => !Busy);
        SelectAllCommand = new RelayCommand(() => { foreach (var r in Rows) if (r.IsApplicable) r.IsSelected = true; });
        SelectNoneCommand = new RelayCommand(() => { foreach (var r in Rows) r.IsSelected = false; });
        RefreshCommand = new RelayCommand(() => _ = RefreshAsync());
        RevertRowCommand = new RelayCommand(p => { if (p is TweakRowViewModel r) _ = RevertAsync(new[] { r }); });
        Presets = Enum.GetValues<TweakPreset>()
            .Select(x => new Choice<TweakPreset>(x, TweakPresetSelector.Label(x), TweakPresetSelector.Description(x)))
            .ToList();
        SetPresetCommand = new RelayCommand(() => ApplyPreset());
    }

    public string Module => _module;
    public string Description { get; }

    /// <summary>Group headers and rows, in order.</summary>
    public ObservableCollection<object> Items { get; } = new();
    public List<TweakRowViewModel> Rows { get; } = new();

    private object? _optionsPanel;

    public object? OptionsPanel
    {
        get => _optionsPanel;
        set
        {
            if (Set(ref _optionsPanel, value)) OnPropertyChanged(nameof(HasOptions));
        }
    }

    public bool HasOptions => _optionsPanel is not null;

    /// <summary>
    /// The cost tier this page ticks by. Changed by the preset picker in the page header;
    /// <see cref="Load"/> re-resolves every tick when it differs from the preset the ticks were made under.
    /// </summary>
    public TweakPreset Preset
    {
        get => _preset;
        set => Set(ref _preset, value);
    }

    /// <summary>The cost tiers this page can tick by.</summary>
    public List<Choice<TweakPreset>> Presets { get; }

    /// <summary>The tier as the picker shows it; null until the first Load.</summary>
    public Choice<TweakPreset>? PresetChoice
    {
        get => _presetChoice;
        set
        {
            if (!Set(ref _presetChoice, value)) return;
            OnPropertyChanged(nameof(PresetDescription));

            // Picking a tier reticks the list immediately, so what a preset covers is visible before it
            // is applied. This is a preview: nothing on the PC changes until "Apply this preset" (which
            // also puts back what the new tier leaves out) or "Apply selected" is pressed.
            if (!_loading && value is not null && Rows.Count > 0)
            {
                foreach (var r in Rows) r.SetPreset(value.Value);
                RecomputeSelectionSummary();
            }
        }
    }

    private Choice<TweakPreset>? _presetChoice;

    /// <summary>True while Load is rebuilding the list, so the picker setter does not retick stale rows.</summary>
    private bool _loading;

    public string PresetDescription => _presetChoice?.Description ?? "";

    /// <summary>
    /// What pressing <c>Apply selected</c> would actually do, computed from the rows as they stand.
    /// A preset changes which rows are ticked, but a tick on a row that is already applied does nothing —
    /// so the counts, not the ticks, are the honest answer to "what will be used".
    /// </summary>
    private string _selectionSummary = "";

    public string SelectionSummary
    {
        get => _selectionSummary;
        private set
        {
            if (Set(ref _selectionSummary, value)) OnPropertyChanged(nameof(HasSelectionSummary));
        }
    }

    public bool HasSelectionSummary => _selectionSummary.Length > 0;

    /// <summary>The first few titles that will be applied, so the picker has a visible consequence.</summary>
    private string _selectionPreview = "";

    public string SelectionPreview
    {
        get => _selectionPreview;
        private set
        {
            if (Set(ref _selectionPreview, value)) OnPropertyChanged(nameof(HasSelectionPreview));
        }
    }

    public bool HasSelectionPreview => _selectionPreview.Length > 0;

    /// <summary>
    /// Recomputes the preview. Status-first, because a tick alone cannot say whether anything will happen:
    /// an already-applied row can be ticked and do nothing, and an unticked row can be unticked for four
    /// different reasons.
    /// </summary>
    public void RecomputeSelectionSummary()
    {
        if (Rows.Count == 0) { SelectionSummary = ""; SelectionPreview = ""; return; }

        var facts = Rows.Select(r => new RowFacts(
            r.Info.Id, r.Title, r.IsSelected, r.IsApplicable, r.Info.DefaultSelected,
            r.Info.CostClass, r.Info.Evidence, r.Status));

        // The tier the picker shows, which is what the list is ticked for. The saved option can lag it
        // until "Apply this preset" commits the choice.
        var shown = _presetChoice?.Value ?? _preset;
        var counts = DpcTweaker.Core.Model.SelectionSummary.Compute(shown, facts);
        SelectionSummary = DpcTweaker.Core.Model.SelectionSummary.Describe(shown, counts);
        SelectionPreview = DpcTweaker.Core.Model.SelectionSummary.Preview(counts);
    }
    public RelayCommand SetPresetCommand { get; }

    /// <summary>
    /// Makes the machine match the chosen preset: applies what the preset includes and is still needed,
    /// and puts back what it leaves out. A preset is therefore a declared desired state, which is what lets
    /// a user go from Everything back to Safe and actually undo the costly tweaks.
    /// <para>
    /// Nothing changes on the PC without an explicit confirmation that states both counts, and every
    /// reverted value comes from the journal, so the whole switch is undoable in the usual way.
    /// </para>
    /// </summary>
    private async void ApplyPreset()
    {
        var engine = _main.Engine;
        if (engine is null || PresetChoice is null) return;
        var chosen = PresetChoice.Value;

        // Record the choice and retick first: that is free, and it shows the new tier immediately.
        bool changed = engine.Options.Preset != chosen;
        if (changed) engine.SetOptions(engine.Options.With(o => o.Preset = chosen));
        _main.ReloadPages(carryTicks: false);

        // The plan is computed across every module page, because a preset spans all of them.
        var modules = _main.Pages.OfType<ModulePageViewModel>().ToList();
        var rows = modules.SelectMany(m => m.Rows).ToList();
        var facts = rows
            .Select(r => new RowFacts(r.Info.Id, r.Title, r.IsSelected, r.IsApplicable,
                r.Info.DefaultSelected, r.Info.CostClass, r.Info.Evidence, r.Status))
            .ToList();
        var plan = PresetTransition.Plan(chosen, facts);

        var byId = rows.ToDictionary(r => r.Info.Id);

        bool HasHistory(string id) =>
            engine.Journal.IsApplied(id) || engine.Journal.OriginalsFor(id).Count > 0;

        // Narrow the plan to what can actually change. A tweak whose value was already like this
        // before the app touched it has nothing recorded to restore, so counting it as a revert
        // would overstate the switch and make the app claim work it cannot do.
        var effective = new TransitionPlan(
            plan.ToApply.Where(byId.ContainsKey).ToList(),
            plan.ToRevert.Where(byId.ContainsKey).Where(HasHistory).ToList());

        // Say so when there is nothing to do. Silently returning after a button press reads as a
        // broken button, and the transition already knows how to explain it.
        if (effective.IsNoOp)
        {
            Message = PresetTransition.Describe(chosen, effective);
            return;
        }

        if (!_main.Ui.Confirm("Use this preset", PresetTransition.Describe(chosen, effective))) return;

        var toApply = effective.ToApply.Select(id => byId[id].Tweak).ToList();
        var toRevert = effective.ToRevert.Select(id => byId[id].Tweak).ToList();

        SetBusy(true);
        try
        {
            // Before ANY change. The reverts below are changes too: asking for a restore point
            // only when there is something to apply left a switch that could revert first and then
            // stop, with no restore point and no way back.
            if (!await _main.EnsureRestorePointAsync()) return;

            // Reverts first, so the applies that follow are not competing with values being undone.
            int putBack = 0, reverted = 0;
            if (toRevert.Count > 0)
            {
                var rv = await Task.Run(() => engine.Revert(toRevert));
                reverted = rv.Count;
                putBack = rv.Count(r => r.Ok);
            }

            if (toApply.Count > 0)
            {
                var ap = await Task.Run(() => engine.Apply(toApply));
                int ok = ap.Count(r => r.Ok);
                Message = $"Applied {ok} of {ap.Count}." +
                          (reverted > 0 ? $" Put back {putBack} of {reverted}." : "");
                if (ap.Any(r => r.Ok && r.Tweak.Info.NeedsReboot)) _main.ForceRestartNeeded();
            }
            else if (reverted > 0)
            {
                Message = $"Put back {putBack} of {reverted}.";
            }
        }
        finally
        {
            SetBusy(false);
        }
        await RefreshAsync();
    }
    public RelayCommand ApplySelectedCommand { get; }
    public RelayCommand RevertSelectedCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand SelectNoneCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand RevertRowCommand { get; }

    /// <summary>
    /// Rebuilds the rows. A tick is carried over from the previous list only while the preset has not
    /// changed: TweakRowViewModel freezes its decision once a tick is carried in, so carrying them
    /// across a preset change would make the picker look like it did nothing. On a change every row
    /// re-decides from the new preset, and rows the preset now skips are explicitly unticked.
    /// </summary>
    /// <param name="carryTicks">
    /// False forces every row to re-decide. Used when the preset changed, because a carried tick
    /// freezes the row's decision and would make the preset picker look inert.
    /// </param>
    public void Load(TweakEngine engine, bool carryTicks = true)
    {
        var incoming = engine.Options.Preset;
        bool presetChanged = incoming != _preset || !carryTicks;
        _loading = true;
        try
        {
            PresetChoice = Presets.FirstOrDefault(x => x.Value == incoming);
            _preset = incoming;
        }
        finally
        {
            _loading = false;
        }

        var previous = Rows.ToDictionary(r => r.Info.Id, r => r.IsSelected);
        Items.Clear();
        Rows.Clear();

        // Safe-recommended first inside every section. Ordering stays within a group so the section
        // headers keep meaning: a GPU's tweaks stay under the GPU, but the free ones lead.
        // LINQ OrderBy is stable, so equal ranks keep the catalog's own order.
        var ordered = engine.InModule(_module)
            .GroupBy(t => t.Info.Group ?? "")
            .SelectMany(g => g.OrderBy(t => TweakPresetSelector.RecommendationRank(t.Info.CostClass, t.Info.Evidence)));

        string? group = null;
        foreach (var t in ordered)
        {
            if (!string.IsNullOrEmpty(t.Info.Group) && t.Info.Group != group)
            {
                group = t.Info.Group;
                Items.Add(new GroupHeaderViewModel(group));
            }
            // On a preset change, carry nothing: a carried tick freezes the row's decision, so the row
            // would keep its old state in both directions (never unticking an excluded row, and never
            // ticking a newly-included one). A preset change means "re-apply the preset", so every row
            // re-decides from scratch on its first status read.
            bool? carried = presetChanged
                ? null
                : (previous.TryGetValue(t.Info.Id, out bool sel) ? sel : null);
            var row = new TweakRowViewModel(t, incoming, carried);
            Rows.Add(row);
            Items.Add(row);
        }
    }

    public override async Task RefreshAsync()
    {
        var engine = _main.Engine;
        if (engine is null) return;
        var rows = Rows.ToList();
        var states = await Task.Run(() => rows.Select(r => (Row: r, State: engine.StateOf(r.Tweak))).ToList());
        _main.Ui.Post(() =>
        {
            foreach (var (row, state) in states) row.Update(state);
            _main.OnRowsUpdated();
        });
    }

    private async Task ApplySelectedAsync()
    {
        var engine = _main.Engine;
        if (engine is null) return;
        // A tick now means "this preset includes it", so it cannot also mean "apply it again": an
        // applied value would be rewritten and re-journalled for no change. Apply works from the tick
        // AND the status, which is the same precedence the summary and the preset switch use.
        var ticked = Rows.Where(r => r.IsSelected && r.IsApplicable).ToList();
        if (ticked.Count == 0) { Message = "Nothing selected."; return; }

        var selected = ticked.Where(r => r.NeedsWork).ToList();
        if (selected.Count == 0)
        {
            Message = $"Nothing to apply: all {ticked.Count} ticked tweak(s) are already on this PC.";
            return;
        }
        if (!await _main.EnsureRestorePointAsync()) return;

        SetBusy(true);
        Message = $"Applying {selected.Count}...";
        try
        {
            var results = await Task.Run(() => engine.Apply(selected.Select(r => r.Tweak)));
            int ok = results.Count(r => r.Ok);
            var failed = results.Where(r => !r.Ok).ToList();
            foreach (var r in selected.Where(r => results.Any(x => x.Ok && ReferenceEquals(x.Tweak, r.Tweak)))) r.MarkDone();
            Message = $"Applied {ok} of {results.Count}." +
                      (failed.Count > 0 ? " Failed: " + string.Join("; ", failed.Select(f => $"{f.Tweak.Info.Title}: {f.Error}")) : "") +
                      (engine.LastFinishErrors.Count > 0 ? " " + string.Join(" ", engine.LastFinishErrors) : "");
            if (engine.LastFinishErrors.Count > 0) _main.ForceRestartNeeded();
        }
        finally
        {
            SetBusy(false);
        }
        await RefreshAsync();
    }

    private Task RevertSelectedAsync() =>
        RevertAsync(Rows.Where(r => r.IsSelected).ToList());

    private async Task RevertAsync(IReadOnlyList<TweakRowViewModel> rows)
    {
        var engine = _main.Engine;
        if (engine is null || rows.Count == 0) return;
        var withHistory = rows.Where(r => engine.Journal.IsApplied(r.Info.Id) || engine.Journal.OriginalsFor(r.Info.Id).Count > 0).ToList();
        if (withHistory.Count == 0)
        {
            Message = "Nothing to revert: the app hasn't changed " + (rows.Count == 1 ? "this one." : "these.") +
                      " If a value was already like this before the app, it stays.";
            return;
        }
        if (!_main.Ui.Confirm("Revert", $"Put back the previous values of {withHistory.Count} tweak(s)?")) return;

        SetBusy(true);
        Message = $"Reverting {withHistory.Count}...";
        try
        {
            var results = await Task.Run(() => engine.Revert(withHistory.Select(r => r.Tweak)));
            int ok = results.Count(r => r.Ok);
            var failed = results.Where(r => !r.Ok).ToList();
            Message = $"Reverted {ok} of {results.Count}." +
                      (failed.Count > 0 ? " Failed: " + string.Join("; ", failed.Select(f => $"{f.Tweak.Info.Title}: {f.Error}")) : "");
            if (results.Any(r => r.Ok && r.Tweak.Info.NeedsReboot)) _main.ForceRestartNeeded();
        }
        finally
        {
            SetBusy(false);
        }
        await RefreshAsync();
    }

    private void SetBusy(bool busy)
    {
        Busy = busy;
        ApplySelectedCommand.Refresh();
        RevertSelectedCommand.Refresh();
        _main.SetGlobalBusy(busy);
    }
}

public sealed class Choice<T>
{
    public Choice(T value, string label, string description = "")
    {
        Value = value;
        Label = label;
        Description = description;
    }

    public T Value { get; }
    public string Label { get; }
    public string Description { get; }
    public override string ToString() => Label;
}

/// <summary>Power screen options: plan path, C-state mode, plugged-in only vs battery too.</summary>
public sealed class PowerOptionsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private Choice<PlanTargetKind> _target;
    private Choice<PowerProfileKind> _profile;
    private Choice<CStateMode> _cstate;
    private bool _battery;
    private string _planDescription = "";

    public PowerOptionsViewModel(MainViewModel main, AppOptions o, SystemProfile p)
    {
        _main = main;
        // Named by what they do to core placement, not by the Windows plan they happen to use: the plan
        // name is an implementation detail, and "Balanced" alone reads like a power setting rather than a
        // decision about which cores a game runs on.
        Targets = new()
        {
            new(PlanTargetKind.TunedUltimate, "I manage core placement myself",
                "Uses the app's own copy of Ultimate Performance, with Windows core parking off so Windows cannot move a game between CCDs. " +
                "Games are held on the game cores by your own rules, so pair this with Process Lasso: without it, background work is free to sit on the interrupt CPUs. " +
                "Costs more idle power and heat than letting Windows decide."),
            new(PlanTargetKind.Balanced, "Let Windows place games",
                "Keeps Windows core parking, which AMD's 3D V-Cache driver and Intel Thread Director use to put a game on the right cores. " +
                "Needs Xbox Game Bar, and Game Bar misses some games. " +
                "Note this works against the app's interrupt plan, which assumes those cores stay free. The CPU is left stock; only device power settings change."),
            new(PlanTargetKind.CurrentActive, "Tune whichever plan is active now",
                "For laptops: tune the plan your vendor tool switches to in performance mode, so you don't end up with two plans fighting each other."),
        };
        Profiles = new()
        {
            new(PowerProfileKind.Full, "Full tuning (CPU + devices)", "CPU idle, EPP, boost and minimum state, plus every device setting."),
            new(PowerProfileKind.DevicesOnly, "Device power settings only", "USB, PCIe, disk and Wi-Fi only; the CPU is left stock."),
        };
        CStates = new()
        {
            new(CStateMode.Light, "Light (C1 only)", "The desktop's setting. About 20 W more at idle."),
            new(CStateMode.Deep, "Deep (Windows stock)", "For before/after comparisons."),
            new(CStateMode.Off, "Off (tests only)", "Cores never rest. Much hotter."),
        };
        _target = Targets.First(t => t.Value == o.PlanTarget);
        _profile = Profiles.First(t => t.Value == o.PowerProfile);
        _cstate = CStates.First(t => t.Value == o.CState);
        _battery = o.ApplyOnBattery;
        IsLaptop = p.IsLaptop;
        ParkingCpu = p.ParkingDependentCpu;
        ApplyOptionsCommand = new RelayCommand(ApplyOptions);
    }

    public List<Choice<PlanTargetKind>> Targets { get; }
    public List<Choice<PowerProfileKind>> Profiles { get; }
    public List<Choice<CStateMode>> CStates { get; }
    public bool IsLaptop { get; }
    public bool ParkingCpu { get; }

    public string ParkingQuestion => ParkingCpu
        ? "Two ways to keep a game on the right cores. This choice decides whether Windows core parking stays on."
        : "";

    public bool ShowParkingQuestion => ParkingCpu;

    public string BatteryWarning => IsLaptop
        ? "On a laptop, applying to battery too means shorter battery life and a hotter laptop on battery."
        : "On a desktop, battery values only matter on a UPS.";

    public Choice<PlanTargetKind> Target { get => _target; set => Set(ref _target, value); }
    public Choice<PowerProfileKind> Profile { get => _profile; set => Set(ref _profile, value); }
    public Choice<CStateMode> CState { get => _cstate; set => Set(ref _cstate, value); }
    public bool ApplyOnBattery { get => _battery; set => Set(ref _battery, value); }

    public string PlanDescription
    {
        get => _planDescription;
        set => Set(ref _planDescription, value);
    }

    public RelayCommand ApplyOptionsCommand { get; }

    private void ApplyOptions()
    {
        var engine = _main.Engine;
        if (engine is null) return;
        engine.SetOptions(engine.Options.With(n =>
        {
            n.PlanTarget = Target.Value;
            n.PowerProfile = Profile.Value;
            n.CState = CState.Value;
            n.ApplyOnBattery = ApplyOnBattery;
        }));
        _main.ReloadPages();
    }
}

public sealed class TimerOptionsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private Choice<uint> _resolution;

    public TimerOptionsViewModel(MainViewModel main, AppOptions o)
    {
        _main = main;
        Resolutions = new()
        {
            new(5000, "0.500 ms", "The desktop's setting."),
            new(5070, "0.507 ms", "PC-Tuning measured tighter Sleep(1) timing at 0.507 ms on most of 30+ systems."),
            new(10000, "1.000 ms", "Gentler: what many apps already request."),
        };
        _resolution = Resolutions.FirstOrDefault(r => r.Value == o.TimerResolution) ?? Resolutions[0];
        ApplyOptionsCommand = new RelayCommand(ApplyOptions);
    }

    public List<Choice<uint>> Resolutions { get; }

    public Choice<uint> Resolution { get => _resolution; set => Set(ref _resolution, value); }

    public RelayCommand ApplyOptionsCommand { get; }

    public string Note =>
        "After changing the resolution, select the timer task and Apply again so the task gets the new value.";

    private void ApplyOptions()
    {
        var engine = _main.Engine;
        if (engine is null) return;
        engine.SetOptions(engine.Options.With(n => n.TimerResolution = Resolution.Value));
        _main.ReloadPages();
    }
}

public sealed class GpuSummaryViewModel
{
    public GpuSummaryViewModel(SystemProfile p)
    {
        foreach (var g in p.Gpus)
        {
            string scan = g.DriverKeyScan is null
                ? (g.Vendor == GpuVendor.Amd ? "driver check: " + (g.DriverScanError ?? "not run") : "")
                : $"driver check: {g.DriverKeyScan.Count(k => k.Value)} of {g.DriverKeyScan.Count} key names found in {Path.GetFileName(g.DriverFile)}";
            Lines.Add(new InfoLine(g.Label, $"{g.Vendor}, driver {g.DriverVersion}. {scan}".Trim()));
        }
        if (p.Gpus.Count == 0) Lines.Add(new InfoLine("GPU", "none found"));
        if (p.Gpus.Any(g => g.Vendor == GpuVendor.Nvidia))
            Lines.Add(new InfoLine("NVIDIA", "The NVIDIA experimental group arrives in a later milestone (M4)."));
    }

    public List<InfoLine> Lines { get; } = new();
}

public sealed class SafetyPageViewModel : PageViewModel
{
    private readonly MainViewModel _main;
    private bool _turnOnProtection = true;
    private string _journalText = "";
    private bool _degraded;
    private string _degradedText = "";
    private string _brokenCopy = "";

    public SafetyPageViewModel(MainViewModel main) : base("Safety", "")
    {
        _main = main;
        RestorePointCommand = new RelayCommand(() => _ = CreateRestorePointAsync(), () => !Busy);
        OpenBackupsCommand = new RelayCommand(() => { Paths.EnsureDataDirs(); _main.Ui.Open(Paths.BackupsDir); });
        OpenDataCommand = new RelayCommand(() => { Paths.EnsureDataDirs(); _main.Ui.Open(Paths.DataDir); });
        RevertAllCommand = new RelayCommand(() => _ = RevertAllAsync(), () => !Busy);
        RecoverDegradedCommand = new RelayCommand(RecoverDegraded, () => Degraded && !Busy);
        HealDriftedCommand = new RelayCommand(() => _ = HealDriftedAsync(), () => !Busy && HasDrift);
    }

    /// <summary>True when journal.json couldn't be read at startup and is under protection.</summary>
    public bool Degraded
    {
        get => _degraded;
        private set { if (Set(ref _degraded, value)) RecoverDegradedCommand.Refresh(); }
    }

    public string DegradedText { get => _degradedText; private set => Set(ref _degradedText, value); }

    /// <summary>The .broken-* copy the app kept instead of overwriting the unreadable journal.</summary>
    public string BrokenCopy { get => _brokenCopy; private set => Set(ref _brokenCopy, value); }

    public RelayCommand RecoverDegradedCommand { get; }

    private int _driftCount;
    private string _driftText = "";

    /// <summary>How many applied tweaks a driver update or Windows Update has undone.</summary>
    public int DriftCount { get => _driftCount; private set { if (Set(ref _driftCount, value)) OnPropertyChanged(nameof(HasDrift)); } }
    public bool HasDrift => _driftCount > 0;
    public string DriftText { get => _driftText; private set => Set(ref _driftText, value); }
    public RelayCommand HealDriftedCommand { get; }

    public bool TurnOnProtection { get => _turnOnProtection; set => Set(ref _turnOnProtection, value); }

    public string JournalText
    {
        get => _journalText;
        private set => Set(ref _journalText, value);
    }

    public string DataFolder => Paths.DataDir;
    public string UndoScript => Paths.UndoAllScript;

    public RelayCommand RestorePointCommand { get; }
    public RelayCommand OpenBackupsCommand { get; }
    public RelayCommand OpenDataCommand { get; }
    public RelayCommand RevertAllCommand { get; }

    public override Task RefreshAsync()
    {
        var e = _main.Engine;
        if (e is null) return Task.CompletedTask;

        var driftedNow = e.DriftedTweaks();
        DriftCount = driftedNow.Count;
        DriftText = driftedNow.Count == 0
            ? ""
            : driftedNow.Count + " tweak(s) the app applied no longer match what it set: " +
              string.Join(", ", driftedNow.Take(4).Select(t => t.Info.Title)) +
              (driftedNow.Count > 4 ? ", and " + (driftedNow.Count - 4) + " more" : "") +
              ". A driver update, Windows Update or another tuning tool usually does this. Re-applying keeps the original " +
              "before-state in the journal, so every revert still works.";
        HealDriftedCommand.Refresh();

        Degraded = e.Journal.IsDegraded;
        if (Degraded)
        {
            BrokenCopy = FindBrokenCopy();
            DegradedText =
                "journal.json couldn't be read when the app started, so it is under protection: it will not be " +
                "overwritten, and undo-all.ps1 is left exactly as it was, because between them they are the only " +
                "record of what this PC had before." +
                (BrokenCopy.Length > 0 ? "\n\nThe unreadable file was kept as:\n" + BrokenCopy : "") +
                "\n\nPut that file back by hand to recover the recorded before-states, or press \"Start a fresh journal\" " +
                "to accept losing them. Nothing is deleted either way: undo-all.ps1 is archived before it is rewritten.";
            JournalText = "Journal locked (degraded). No new tweak can be applied until this is resolved.";
        }
        else
        {
            BrokenCopy = "";
            DegradedText = "";
            JournalText = $"{e.Journal.AppliedCount} tweak(s) applied by the app, {e.Journal.EntryCount} recorded previous value(s)." +
                          (e.SessionBackup is null ? "" : " This session's backups: " + e.SessionBackup.Folder);
        }
        return Task.CompletedTask;
    }

    /// <summary>Re-applies every drifted tweak. The original before-states stay in the journal.</summary>
    private async Task HealDriftedAsync()
    {
        var e = _main.Engine;
        if (e is null) return;
        var drifted = e.DriftedTweaks();
        if (drifted.Count == 0) { DriftCount = 0; return; }
        if (!_main.Ui.Confirm("Re-apply drifted tweaks",
                drifted.Count + " tweak(s) the app applied no longer match what it set. This usually means a driver update, " +
                "Windows Update or another tuning tool put them back.\n\n" +
                "Re-applying keeps the original before-state in the journal, so Revert selected and Revert everything still work.\n\n" +
                "Some of these need a restart to take effect. Continue?"))
            return;

        Busy = true;
        HealDriftedCommand.Refresh();
        Message = "Re-applying " + drifted.Count + " drifted tweak(s)...";
        try
        {
            var progress = new Progress<string>(s => Message = s);
            var results = await Task.Run(() => e.HealDrifted(progress));
            int ok = results.Count(r => r.Ok);
            var failed = results.Where(r => !r.Ok).ToList();
            Message = $"Re-applied {ok} of {results.Count}." +
                      (failed.Count > 0 ? " Failed: " + string.Join("; ", failed.Select(f => $"{f.Tweak.Info.Title}: {f.Error}")) : "");
            if (results.Any(r => r.Ok && r.Tweak.Info.NeedsReboot)) _main.ForceRestartNeeded();
            await _main.RefreshAllAsync();
        }
        catch (Exception ex)
        {
            Message = "Re-apply failed: " + ex.Message;
            Log.Error("HealDrifted failed: " + ex);
        }
        finally
        {
            Busy = false;
            HealDriftedCommand.Refresh();
        }
    }

    private static string FindBrokenCopy()
    {
        try
        {
            var files = Directory.GetFiles(Paths.DataDir, "journal.json*.broken-*");
            if (files.Length == 0) return "";
            return files.OrderByDescending(f => File.GetLastWriteTime(f)).First();
        }
        catch { return ""; }
    }

    /// <summary>
    /// Accepts the lost journal. The current undo-all.ps1 is archived first: after recovery it gets
    /// regenerated from the empty journal, and that script may still be the best copy around.
    /// </summary>
    private void RecoverDegraded()
    {
        var e = _main.Engine;
        if (e is null) return;
        if (!_main.Ui.Confirm("Start a fresh journal",
                "The recorded before-states in journal.json can no longer be read.\n\n" +
                "Starting fresh means this PC's earlier values can't be reverted from the journal any more. " +
                "The unreadable file" + (BrokenCopy.Length > 0 ? " (" + BrokenCopy + ")" : "") +
                " stays on disk, and undo-all.ps1 is archived before it is rewritten.\n\nContinue?"))
            return;

        try
        {
            if (File.Exists(Paths.UndoAllScript))
            {
                string archive = Path.Combine(Paths.DataDir,
                    "undo-all.ps1.before-recovery-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".ps1");
                File.Copy(Paths.UndoAllScript, archive, overwrite: true);
                Message = "Kept the old undo script as " + archive + ".";
            }
        }
        catch (Exception ex) { Log.Warn("Couldn't archive undo-all.ps1: " + ex.Message); }

        e.Journal.RecoverDegraded();
        e.Journal.Flush();
        Degraded = false;
        Message = "Journal recovered. Changes can be applied again.";
        _ = _main.RefreshAllAsync();
    }

    private async Task CreateRestorePointAsync()
    {
        Busy = true;
        RestorePointCommand.Refresh();
        Message = "Creating a restore point (up to a minute)...";
        var point = await Task.Run(() => RestorePoint.Create("Stutter Guard", TurnOnProtection, _main.Engine?.Journal));
        bool ok = point.Ok;
        string msg = point.Message;
        Message = ok
            ? "Restore point created." + (point.TurnedProtectionOn ? " System Protection was off; it was turned on and recorded, so Revert everything can put it back." : "")
            : "Restore point failed: " + msg;
        if (ok) _main.RestorePointDone = true;
        Busy = false;
        RestorePointCommand.Refresh();
    }

    private async Task RevertAllAsync()
    {
        var e = _main.Engine;
        if (e is null) return;
        if (!_main.Ui.Confirm("Revert everything",
                "Put back every value the app has ever changed, as recorded in the journal, and remove its tasks and files?"))
            return;
        Busy = true;
        Message = "Reverting everything...";
        var (restored, errors) = await Task.Run(() => e.RevertAll());
        Message = errors.Count == 0
            ? $"Done: {restored} value(s) restored. Restart Windows to finish."
            : $"{restored} restored, {errors.Count} failed: {string.Join("; ", errors)}. The journal was kept; undo-all.ps1 can retry.";
        if (restored > 0) _main.ForceRestartNeeded();   // nothing restored means nothing to restart for
        Busy = false;
        await _main.RefreshAllAsync();
        await RefreshAsync();
    }
}
