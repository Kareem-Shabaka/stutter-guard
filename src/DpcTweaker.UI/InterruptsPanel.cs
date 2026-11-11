using System.Collections.ObjectModel;
using System.Globalization;
using DpcTweaker.Core;
using DpcTweaker.Core.Detection;
using DpcTweaker.Core.Interrupts;
using DpcTweaker.Core.Tweaks;

namespace DpcTweaker.UI;

/// <summary>
/// The Interrupts screen's top panel: which cores are kept free and why, the plan,
/// the core benchmark, the MSI mode, and the check after the restart.
/// </summary>
public sealed class InterruptOptionsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private CancellationTokenSource? _cts;
    private bool _running;
    private double _progress;
    private string _progressText = "";
    private string _message = "";
    private Choice<MsiMode> _msiMode;
    private Choice<bool> _pinMode;
    private Choice<HybridDevicePolicy> _hybridChoice;
    private bool _downloadDuringCheck = true;

    public InterruptOptionsViewModel(MainViewModel main, TweakEngine engine)
    {
        _main = main;
        var plan = engine.Plan;
        var p = engine.Profile;

        Applicable = plan.Applicable;
        NotApplicableText = plan.NotApplicableReason ?? "";
        ReservedText = plan.Applicable ? $"Kept free of devices: CPUs {CpuTopology.FormatCpus(plan.ReservedCpus)}. {plan.ReservedReason}" : "";
        CandidatesText = plan.Applicable
            ? "Cores that take devices, best first: " + string.Join(", ", plan.Candidates.Select(c => "CPU " + c.Cpu + (p.Cpu.IsHybrid ? (c.PerformanceCore ? " (P)" : " (E)") : ""))) +
              ". Only the first thread of each core takes a device; its SMT sibling shares the same execution units."
            : "";
        RankingText = plan.RankingSource;

        foreach (var a in plan.Assignments)
            PlanLines.Add(new InfoLine($"{a.CpuText}{(a.Shared ? " (shared)" : "")}{(a.Kept ? " (kept)" : "")}", $"{a.Device.Name}: {AffinityTweak.RoleText(a.Role)}. {a.Reason}"));
        foreach (var n in plan.Notes) Notes.Add(n);
        HasNotes = Notes.Count > 0;

        var bench = engine.Bench;
        BenchStatus = bench is null
            ? $"Not run yet. It takes about {CoreBenchmark.Estimate(p.Cpu, Samples, IdleSeconds).TotalMinutes:0} minutes and changes nothing."
            : "Saved: " + bench.Summary + ".";
        if (bench is not null)
            foreach (var c in bench.Cores.OrderBy(c => c.Cpu))
                BenchLines.Add(new InfoLine("CPU " + c.Cpu,
                    string.Format(CultureInfo.CurrentCulture, "wake-up late by {0:0.0} µs median, {1:0.0} µs p95, {2:0} µs max · workload {3:0.0} ms · Windows rank {4} · idle {5:0} interrupts/s{6}",
                        c.WakeMedianUs, c.WakeP95Us > 0 ? c.WakeP95Us : c.WakeP99Us, c.WakeMaxUs, c.SpeedMs, c.SchedulingClass, c.InterruptsPerSec,
                        plan.ReservedCpus.Contains(c.Cpu) ? " · kept free" : c.InterruptsPerSec > 1000 ? " (mostly devices pinned there now; not scored)" : "")));
        HasBench = bench is not null;

        MsiModes = new()
        {
            new(MsiMode.Safe, "Safe (recommended)", "Only line-based devices in classes known to work with MSI: GPUs and their HDMI audio, HD Audio, USB controllers, network adapters."),
            new(MsiMode.All, "MSI on all (optional)", "Every device whose hardware supports MSI. Some drivers advertise MSI but misbehave: the device stops (code 10). On the Windows drive's controller it can stop Windows starting, so storage controllers stay unticked even here."),
        };
        _msiMode = MsiModes.First(m => m.Value == engine.Options.Msi);

        PinModes = new()
        {
            new(true, "Keep pins that follow the rules (recommended)", "A device already pinned to an allowed core (not kept free, a first thread, not shared with a more important device) stays there. Only the rest are placed by the benchmark. Use this if you tuned interrupts by hand or with another tool and it works."),
            new(false, "Re-plan every device from the benchmark", "Ignores today's pins and places every device by the core order above. Benchmark differences between cores are often within the noise, so this can move devices for little gain."),
        };
        _pinMode = PinModes.First(m => m.Value == engine.Options.KeepValidPins);
        UsePinModeCommand = new RelayCommand(UsePinMode);

        // Only meaningful on an Intel hybrid CPU; hidden everywhere else.
        HybridPolicies = new()
        {
            new(HybridDevicePolicy.EcoresForNetAudio, "E-cores for audio and network (recommended)",
                "GPU and mouse controller take P-cores; audio, network and secondary USB take E-cores, so every spare P-core stays free for the game's threads. " +
                "This can override the core benchmark: an E-core is used even when the benchmark measured a P-core as steadier."),
            new(HybridDevicePolicy.PcoresWhenAvailable, "P-cores when there is room",
                $"Keeps CPU 0/1 plus {AffinityPlanner.HybridGamePoolPcores} P-cores free for the game, then puts audio and network on any P-cores still spare " +
                "before spilling onto E-cores. A good choice when your CPU has enough P-cores to cover both the game and its devices."),
            new(HybridDevicePolicy.BenchmarkDecides, "Let the benchmark decide",
                "Stops treating P-cores as special for ranking. Cores are ordered purely by the 2-minute benchmark's wake-up jitter and execution speed, " +
                "E-cores included. Use this if you ran the benchmark and want its numbers to win."),
        };
        _hybridChoice = HybridPolicies.First(m => m.Value == engine.Options.HybridDevices);
        UseHybridPolicyCommand = new RelayCommand(UseHybridPolicy);

        RunBenchmarkCommand = new RelayCommand(() => _ = RunBenchmarkAsync(), () => !Running);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => Running);
        ForgetBenchmarkCommand = new RelayCommand(ForgetBenchmark, () => !Running && HasBench);
        CheckCommand = new RelayCommand(() => _ = RunCheckAsync(), () => !Running && Applicable);
        UseMsiModeCommand = new RelayCommand(UseMsiMode);
        IsHybrid = engine.Profile.Cpu.IsHybrid;
        OpenStartupAppsCommand = new RelayCommand(() => _main.Ui.Open("ms-settings:startupapps"));
        OpenWinUtilCommand = new RelayCommand(() => _main.Ui.Open("https://github.com/ChrisTitusTech/winutil"));

        if (_main.LastCheck is { } last) ShowCheck(last);
    }

    public const int Samples = 5000;
    public const int IdleSeconds = 30;

    public bool Applicable { get; }
    public bool NotApplicable => !Applicable;
    public string NotApplicableText { get; }
    public string ReservedText { get; }
    public string CandidatesText { get; }
    public string RankingText { get; }
    public ObservableCollection<InfoLine> PlanLines { get; } = new();
    public ObservableCollection<string> Notes { get; } = new();
    public bool HasNotes { get; }

    public string BenchStatus { get; }
    public ObservableCollection<InfoLine> BenchLines { get; } = new();
    public bool HasBench { get; }
    public bool NotHasBench => !HasBench;

    public string PrepText =>
        "For a clean measurement: close games, browsers, RGB and monitoring apps (HWiNFO, Afterburner, iCUE), and turn startup apps off first (Settings > Apps > Startup), then restart. " +
        "Chris Titus Tech's WinUtil can clear startup items and bloat in one place; use its Tweaks tab with care, it changes many things at once. Turn your startup apps back on afterwards if you want them.";

    public List<Choice<MsiMode>> MsiModes { get; }
    public List<Choice<bool>> PinModes { get; }
    public List<Choice<HybridDevicePolicy>> HybridPolicies { get; }

    /// <summary>True only on an Intel hybrid CPU: elsewhere the P/E choice means nothing.</summary>
    public bool IsHybrid { get; }

    public Choice<HybridDevicePolicy> HybridChoice
    {
        get => _hybridChoice;
        set => Set(ref _hybridChoice, value);
    }

    public RelayCommand UseHybridPolicyCommand { get; }

    public Choice<bool> PinModeChoice
    {
        get => _pinMode;
        set => Set(ref _pinMode, value);
    }

    public RelayCommand UsePinModeCommand { get; }

    public Choice<MsiMode> MsiModeChoice
    {
        get => _msiMode;
        set => Set(ref _msiMode, value);
    }

    public bool DownloadDuringCheck
    {
        get => _downloadDuringCheck;
        set => Set(ref _downloadDuringCheck, value);
    }

    public string CheckIntro =>
        "After applying the plan and restarting: about 30 seconds. 8 seconds idle (hands off), then 20 seconds of light load: a silent sound on your default output, " +
        "a test download (speed.cloudflare.com, if ticked), the app's animation for the GPU, and you moving the mouse. Each busy device sits alone on its core, so the per-CPU counters show where its interrupts really land.";

    public ObservableCollection<InfoLine> CheckLines { get; } = new();
    public ObservableCollection<string> CheckNotes { get; } = new();

    private bool _hasCheck;
    public bool HasCheck
    {
        get => _hasCheck;
        private set => Set(ref _hasCheck, value);
    }

    public bool Running
    {
        get => _running;
        private set
        {
            if (!Set(ref _running, value)) return;
            RunBenchmarkCommand.Refresh();
            CancelCommand.Refresh();
            ForgetBenchmarkCommand.Refresh();
            CheckCommand.Refresh();
        }
    }

    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public string ProgressText { get => _progressText; private set => Set(ref _progressText, value); }
    public string Message { get => _message; private set => Set(ref _message, value); }

    public RelayCommand RunBenchmarkCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ForgetBenchmarkCommand { get; }
    public RelayCommand CheckCommand { get; }
    public RelayCommand UseMsiModeCommand { get; }
    public RelayCommand OpenStartupAppsCommand { get; }
    public RelayCommand OpenWinUtilCommand { get; }

    /// <summary>Starts a run with a fresh token and disposes whatever the previous run left behind.</summary>
    private CancellationTokenSource BeginRun()
    {
        var previous = _cts;
        _cts = new CancellationTokenSource();
        try { previous?.Dispose(); } catch { }
        return _cts;
    }

    private void Report(string text, double fraction) => _main.Ui.Post(() =>
    {
        ProgressText = text;
        Progress = Math.Clamp(fraction, 0, 1) * 100;
    });

    private async Task RunBenchmarkAsync()
    {
        var engine = _main.Engine;
        if (engine is null) return;
        if (!_main.Ui.Confirm("Core benchmark",
                $"The benchmark takes about {CoreBenchmark.Estimate(engine.Profile.Cpu, Samples, IdleSeconds).TotalMinutes:0} minutes and changes nothing.\n\n" +
                "Close games, browsers and monitoring apps, then leave the PC alone until it finishes (the first 30 seconds measure the idle load).\n\nStart now?"))
            return;

        var cts = BeginRun();
        Running = true;
        _main.SetGlobalBusy(true);
        Message = "";
        try
        {
            var result = await Task.Run(() => CoreBenchmark.Run(engine.Profile.Cpu, engine.Profile.CpuSets, Samples, IdleSeconds, Report, cts.Token));
            engine.SetBench(result);
            _main.ReloadPages();
            _main.Interrupts.Message = "Benchmark saved. The plan and the rows below now use it: check the CPUs, then Apply selected and restart.";
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
            Running = false;
            _main.SetGlobalBusy(false);
        }
    }

    private void ForgetBenchmark()
    {
        var engine = _main.Engine;
        if (engine is null) return;
        if (!_main.Ui.Confirm("Forget the benchmark", "Go back to the estimated core order? The rows below will be rebuilt; nothing on the PC changes.")) return;
        engine.SetBench(null);
        _main.ReloadPages();
    }

    private async Task RunCheckAsync()
    {
        var engine = _main.Engine;
        if (engine is null) return;
        var cts = BeginRun();
        Running = true;
        _main.SetGlobalBusy(true);
        Message = "";
        try
        {
            var r = await Task.Run(() => InterruptCheck.Run(engine.Plan, engine.Profile.Cpu.LogicalCount, DownloadDuringCheck, 8, 20,
                a => engine.Journal.AppliedAt("irq." + a.Device.InstanceId) is DateTime t && t > Core.Infra.BootTime.LastBoot,
                Report, cts.Token, engine.Profile.Cpu));
            _main.LastCheck = r;
            engine.LastCheck = r;
            ShowCheck(r);
            Message = r.Devices.All(d => d.Ok) ? "Check done: every device looks right." : "Check done: see the rows marked ⚠ below.";
        }
        catch (OperationCanceledException)
        {
            Message = "Check cancelled.";
        }
        catch (Exception ex)
        {
            Message = "The check failed: " + ex.Message;
        }
        finally
        {
            Running = false;
            _main.SetGlobalBusy(false);
            ProgressText = "";
            Progress = 0;
        }
    }

    private void ShowCheck(CheckResult r)
    {
        CheckLines.Clear();
        CheckNotes.Clear();
        foreach (var l in r.Devices)
            CheckLines.Add(new InfoLine((l.Ok ? "✓ " : "⚠ ") + l.Cpus,
                $"{l.Device} ({l.Role}): {l.IdlePerSec:0} interrupts/s idle → {l.LoadPerSec:0}/s under load, DPCs {l.DpcDeltaPerSec:+0;-0;0}/s. {l.Verdict}"));
        if (r.ReservedBusiestCpu >= 0)
            CheckNotes.Add($"Kept-free CPUs: +{r.ReservedDeltaPerSec:0} interrupts/s in total under load; the busiest was CPU {r.ReservedBusiestCpu} (+{r.ReservedBusiestDeltaPerSec:0}/s).");
        foreach (var n in r.Notes) CheckNotes.Add(n);
        CheckNotes.Add("Checked " + r.When.ToString("g", CultureInfo.CurrentCulture) + ".");
        HasCheck = true;
    }

    private void UsePinMode()
    {
        var engine = _main.Engine;
        if (engine is null || PinModeChoice.Value == engine.Options.KeepValidPins) return;
        engine.SetOptions(engine.Options.With(o => o.KeepValidPins = PinModeChoice.Value));
        _main.ReloadPages();
    }

    private void UseHybridPolicy()
    {
        var engine = _main.Engine;
        if (engine is null || HybridChoice.Value == engine.Options.HybridDevices) return;
        engine.SetOptions(engine.Options.With(o => o.HybridDevices = HybridChoice.Value));
        _main.ReloadPages();
    }

    private void UseMsiMode()
    {
        var engine = _main.Engine;
        if (engine is null || MsiModeChoice.Value == engine.Options.Msi) return;
        if (MsiModeChoice.Value == MsiMode.All &&
            !_main.Ui.Confirm("MSI on all: read this first",
                "This lists every device whose hardware supports MSI, not just the classes known to work with it.\n\n" +
                "Some drivers advertise MSI but misbehave with it: the device stops working (Device Manager code 10, no sound, no network). " +
                "On the controller that holds the Windows drive it can stop Windows starting (INACCESSIBLE_BOOT_DEVICE), so storage controllers stay unticked.\n\n" +
                "The app flags any device that reports a problem after the restart. If Windows won't start: Safe Mode, then undo-all.ps1 in C:\\ProgramData\\Stutter Guard, or the restore point.\n\nShow all devices?"))
        {
            MsiModeChoice = MsiModes[0];
            return;
        }
        engine.SetOptions(engine.Options.With(o => o.Msi = MsiModeChoice.Value));
        _main.ReloadPages();
    }
}

/// <summary>The Network screen's top panel: the adapters found and what's left alone.</summary>
public sealed class NetworkSummaryViewModel
{
    public NetworkSummaryViewModel(SystemProfile p)
    {
        foreach (var n in p.NetAdapters)
            Lines.Add(new InfoLine(n.IsWifi ? "Wi-Fi" : "Wired",
                $"{n.Name}: {(n.Connected == true ? "connected" : n.Connected == false ? "not connected" : "state unknown")}, {n.Params.Count} driver properties" +
                (n.IsWifi ? ". Left alone in this version: the power plan's wireless setting (Power plan screen) covers its power saving." : ".")));
        if (p.NetAdapters.Count == 0) Lines.Add(new InfoLine("Adapters", "No physical network adapter found."));
        if (p.NetAdapters.Any(n => n.IsWifi) && !p.NetAdapters.Any(n => !n.IsWifi && n.Connected == true))
            Lines.Add(new InfoLine("Tip", "Ethernet beats Wi-Fi for latency. Wi-Fi's own retries and power saving add milliseconds no PC setting can remove."));
    }

    public List<InfoLine> Lines { get; } = new();

    public string LeftAlone =>
        "Left at Windows defaults on purpose: checksum offloads (cheaper per packet; turning them off didn't help on the desktop), Large Send Offload (TCP sends only, games use UDP), " +
        "receive/transmit buffers (raise only if the adapter reports receive discards), Wake-on-LAN, the TCP stack (TCP Optimizer-style changes, BBR2 which broke local connections on 24H2, " +
        "Nagle/TcpAckFrequency which are TCP-only), NetworkThrottlingIndex and SystemResponsiveness (Microsoft clamps values under 10 to 20). RSS is on the Interrupts screen, with the adapter's cores.";
}
