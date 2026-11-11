using System.Collections.ObjectModel;
using System.Globalization;
using DpcTweaker.Core;
using DpcTweaker.Core.Detection;
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
        if (!_main.Ui.Confirm("Submit Anonymous Hardware Report",
            "Send an anonymous copy of your hardware scan report (CPU layout, GPU drivers, NIC properties, uncatalogued services) directly to the developer to help optimize Stutter Guard?\n\nNo personal files, usernames, passwords, or IP addresses are collected."))
            return;

        Message = "Submitting hardware report...";
        _main.SetGlobalBusy(true);
        try
        {
            var (ok, msg) = await TelemetryClient.SubmitDataReportAsync(engine);
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

        Message = "Sending feedback to developer...";
        _main.SetGlobalBusy(true);
        try
        {
            var (ok, msg) = await TelemetryClient.SubmitFeedbackAsync(engine, text);
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

    public ModulePageViewModel(MainViewModel main, string module, string title, string icon, string description)
        : base(title, icon)
    {
        _main = main;
        _module = module;
        Description = description;
        ApplySelectedCommand = new RelayCommand(() => _ = ApplySelectedAsync(), () => !Busy);
        RevertSelectedCommand = new RelayCommand(() => _ = RevertSelectedAsync(), () => !Busy);
        SelectAllCommand = new RelayCommand(() => { foreach (var r in Rows) if (r.IsApplicable) r.IsSelected = true; });
        SelectNoneCommand = new RelayCommand(() => { foreach (var r in Rows) r.IsSelected = false; });
        RefreshCommand = new RelayCommand(() => _ = RefreshAsync());
        RevertRowCommand = new RelayCommand(p => { if (p is TweakRowViewModel r) _ = RevertAsync(new[] { r }); });
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

    public RelayCommand ApplySelectedCommand { get; }
    public RelayCommand RevertSelectedCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand SelectNoneCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand RevertRowCommand { get; }

    public void Load(TweakEngine engine)
    {
        var previous = Rows.ToDictionary(r => r.Info.Id, r => r.IsSelected);
        Items.Clear();
        Rows.Clear();
        string? group = null;
        foreach (var t in engine.InModule(_module))
        {
            if (!string.IsNullOrEmpty(t.Info.Group) && t.Info.Group != group)
            {
                group = t.Info.Group;
                Items.Add(new GroupHeaderViewModel(group));
            }
            var row = new TweakRowViewModel(t, previous.TryGetValue(t.Info.Id, out bool sel) ? sel : null);
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
        var selected = Rows.Where(r => r.IsSelected && r.IsApplicable).ToList();
        if (selected.Count == 0) { Message = "Nothing selected."; return; }
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
            if (results.Any(r => r.Ok && r.Tweak.Info.NeedsReboot) || engine.LastFinishErrors.Count > 0) _main.RestartNeeded = true;
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
            if (results.Any(r => r.Ok && r.Tweak.Info.NeedsReboot)) _main.RestartNeeded = true;
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
        Targets = new()
        {
            new(PlanTargetKind.TunedUltimate, "Tuned Ultimate Performance", "The app's own copy of Ultimate Performance (or your existing Ultimate plan) with the full low-latency tuning. Pick this if you manage cores yourself with Process Lasso."),
            new(PlanTargetKind.Balanced, "Balanced, device settings only", "Keeps Windows' parking, which AMD's dual-CCD driver and Intel hybrid scheduling rely on. Only USB, PCIe, disk and Wi-Fi power saving change."),
            new(PlanTargetKind.CurrentActive, "The plan that's active now", "For laptops: tune the plan your vendor tool switches to in performance mode."),
        };
        Profiles = new()
        {
            new(PowerProfileKind.Full, "Full low-latency tuning", "CPU idle, EPP, boost, minimum state, plus every device setting."),
            new(PowerProfileKind.DevicesOnly, "Device settings only", "USB, PCIe, disk and Wi-Fi only; CPU settings stay stock."),
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
        ? "Your CPU's game placement depends on Windows parking (AMD dual-CCD or Intel hybrid). Do you want Windows and the AMD/Intel drivers to decide which cores games run on (Balanced, device settings only), or manage it yourself with Process Lasso (Tuned Ultimate)?"
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

    public SafetyPageViewModel(MainViewModel main) : base("Safety", "")
    {
        _main = main;
        RestorePointCommand = new RelayCommand(() => _ = CreateRestorePointAsync(), () => !Busy);
        OpenBackupsCommand = new RelayCommand(() => { Paths.EnsureDataDirs(); _main.Ui.Open(Paths.BackupsDir); });
        OpenDataCommand = new RelayCommand(() => { Paths.EnsureDataDirs(); _main.Ui.Open(Paths.DataDir); });
        RevertAllCommand = new RelayCommand(() => _ = RevertAllAsync(), () => !Busy);
    }

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
        if (e is not null)
            JournalText = $"{e.Journal.AppliedCount} tweak(s) applied by the app, {e.Journal.EntryCount} recorded previous value(s)." +
                          (e.SessionBackup is null ? "" : " This session's backups: " + e.SessionBackup.Folder);
        return Task.CompletedTask;
    }

    private async Task CreateRestorePointAsync()
    {
        Busy = true;
        RestorePointCommand.Refresh();
        Message = "Creating a restore point (up to a minute)...";
        var (ok, msg) = await Task.Run(() => RestorePoint.Create("Stutter Guard", TurnOnProtection));
        Message = ok ? "Restore point created." : "Restore point failed: " + msg;
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
        _main.RestartNeeded = true;
        Busy = false;
        await _main.RefreshAllAsync();
        await RefreshAsync();
    }
}
