using System.Globalization;
using DpcTweaker.Core.Detection;
using DpcTweaker.Core.Infra;
using DpcTweaker.Core.Model;
using DpcTweaker.Core.Native;

namespace DpcTweaker.Core.Tweaks;

public sealed record PowerSettingDef(
    string Key,
    string Title,
    Guid Sub,
    Guid Setting,
    Func<AppOptions, uint> Target,
    Func<uint, string> Format,
    string What,
    string Why,
    string Cost,
    Evidence Evidence,
    bool InFull,
    bool InDevicesOnly,
    bool DesktopOnly = false);

/// <summary>The power settings of the desktop's tuned Ultimate plan (all verified by tweak-audit.ps1 on SHABAKA-B4).</summary>
public static class PowerSettings
{
    public static readonly Guid Proc = new("54533251-82be-4824-96c1-47b60b740d00");
    public static readonly Guid Usb = new("2a737441-1930-4402-8d77-b2bebba308a3");
    public static readonly Guid Disk = new("0012ee47-9041-4b5d-9b77-535fba8b1442");
    public static readonly Guid Pcie = new("501a4d13-42af-4429-9fd1-a8218c268e20");
    public static readonly Guid Display = new("7516b95f-f776-4464-8c53-06167f40cc99");
    public static readonly Guid Wireless = new("19cbb8fa-5279-450e-9fac-8a3d5fedd0c1");
    public static readonly Guid IoCoalescing = new("2e601130-5351-4d9d-8e04-252966bad054");
    public static readonly Guid Steering = new("48672f38-7a9a-4bb2-8bf8-3d85be19de4e");

    private static string Num(uint v) => v.ToString(CultureInfo.InvariantCulture);
    private static string Pct(uint v) => v + "%";
    private static string Ms(uint v) => v + " ms";
    private static string Never(uint v) => v == 0 ? "Never" : TimeSpan.FromSeconds(v).ToString() ;
    private static Func<uint, string> Names(params string[] names) => v => v < names.Length ? names[v] : Num(v);

    public static readonly PowerSettingDef[] All =
    {
        new("usb.selective-suspend", "USB selective suspend", Usb, new("48e6b7a6-50f5-4782-a5d4-53bb8f07e226"),
            _ => 0, Names("Disabled", "Enabled"),
            "Stops Windows suspending idle USB ports.",
            "No wake-up hitch on mouse, keyboard, audio or Bluetooth.", "A little idle power.", Evidence.Documented, true, true),
        new("usb.link-power", "USB 3 link power management", Usb, new("d4e98f31-5ffe-4ce1-be31-1b38b384c009"),
            _ => 0, Names("Off", "Minimum power savings", "Moderate power savings", "Maximum power savings"),
            "Keeps USB 3 links out of their U1/U2 sleep states.",
            "No link wake delay on USB 3 devices.", "A little idle power.", Evidence.Documented, true, true),
        new("cpu.epp", "Energy performance preference (EPP)", Proc, new("36687f9e-e3a5-4dbf-b1dc-15eb381c6863"),
            _ => 0, Pct,
            "Tells the CPU's own boost logic to favour performance over saving power.",
            "Fastest clock response to bursts of work.", "More power at light load.", Evidence.Documented, true, false),
        new("cpu.epp-latency", "Latency-sensitivity hint EPP", Proc, new("4b70f900-cdd9-4e66-aa26-ae8417f98173"),
            _ => 0, Pct,
            "The EPP Windows uses while it thinks something latency-sensitive is running.",
            "Insurance: moot with EPP at 0.", "None beyond EPP 0.", Evidence.Documented, true, false),
        new("cpu.min-state", "Minimum processor state", Proc, new("893dee8e-2bef-41e0-89c6-b55d0929964c"),
            _ => 100, Pct,
            "Sets a clock floor.",
            "No ramp-up delay between bursts.", "More idle power and heat.", Evidence.Documented, true, false),
        new("cpu.max-state", "Maximum processor state", Proc, new("bc5038f7-23e0-4960-96da-33abaf5935ec"),
            _ => 100, Pct,
            "Lets the CPU use its full speed.",
            "Already 100% on stock plans; checked.", "None.", Evidence.Documented, true, false),
        new("cpu.throttle", "Allow throttle states", Proc, new("3b04d4fd-1cc7-4f23-ab1c-d1337819c4bb"),
            _ => 0, Names("Off", "On", "Automatic"),
            "Turns off clock throttling states.",
            "Probably a no-op on Ryzen; zero risk.", "None.", Evidence.Documented, true, false),
        new("cpu.idle-disable", "Idle disable", Proc, new("5d76a2ca-e8c0-402f-a133-2158492d58ad"),
            o => o.CState == CStateMode.Off ? 1u : 0u, Names("Idle allowed", "Idle disabled"),
            "Whether cores may idle at all. Only the Off C-state mode disables idle.",
            "Light mode keeps idle on and caps it at C1 instead.", "Off mode: cores never rest, much hotter.", Evidence.Measured, true, false),
        new("cpu.idle-max", "Processor idle state maximum", Proc, new("9943e905-9a30-4ec1-9b99-44dd3b76f7a2"),
            o => o.CState == CStateMode.Deep ? 0u : 1u, v => v == 0 ? "0 (no cap)" : "C" + v,
            "Light C-states: cores may only enter C1, never deep sleep (CC6).",
            "Waking a core from deep sleep costs time on every interrupt. On the desktop: Core C6 0%, C1 about 94%.",
            "About 20 W more at idle on the desktop (50 W vs 30 W package).", Evidence.Measured, true, false),
        new("cpu.idle-promote", "Idle promote threshold", Proc, new("7b224883-b3cc-4d79-819f-8374152cbe7c"),
            o => o.CState == CStateMode.Deep ? 60u : 100u, Pct,
            "How busy a core must be before Windows lets it go to a deeper idle state.",
            "100 = never promotes past the shallowest state.", "Part of the C1 cap.", Evidence.Measured, true, false),
        new("cpu.idle-demote", "Idle demote threshold", Proc, new("4b92d758-5a24-4851-a470-815d78aee119"),
            o => o.CState == CStateMode.Deep ? 40u : 100u, Pct,
            "When Windows moves a core back to a shallower idle state.",
            "100 = always demotes back to the shallowest.", "Part of the C1 cap.", Evidence.Measured, true, false),
        new("cpu.core-parking", "Core parking: minimum unparked cores", Proc, new("0cc5b647-c1df-4637-891a-dec35c318583"),
            _ => 100, Pct,
            "Keeps every core unparked.",
            "Already 100% on stock Ultimate; checked. (The Balanced path leaves parking alone on purpose.)", "More idle power.", Evidence.Documented, true, false),
        new("cpu.boost", "Processor performance boost mode", Proc, new("be337238-0d82-4146-a960-4f3749d470c7"),
            _ => 2, Names("Disabled", "Enabled", "Aggressive", "Efficient enabled", "Efficient aggressive", "Aggressive at guaranteed", "Efficient aggressive at guaranteed"),
            "How eagerly the CPU boosts.",
            "Already Aggressive on stock Ultimate; checked.", "None.", Evidence.Documented, true, false),
        new("cpu.check-interval", "Processor performance time check interval", Proc, new("4d2b0152-7d5c-498b-88e2-34345392a2c5"),
            _ => 5000, Ms,
            "How often Windows' power manager re-evaluates CPU performance (stock 15 ms).",
            "Fewer power-manager DPCs (PpmCheck, 37-90 µs each in the desktop's traces). With minimum state 100% and EPP 0 the checks do little.",
            "None measured.", Evidence.Measured, true, false),
        new("pcie.aspm", "PCI Express link state power management", Pcie, new("ee12f906-d277-404b-b6da-e5fa1a576df5"),
            _ => 0, Names("Off", "Moderate power savings", "Maximum power savings"),
            "Keeps PCIe links out of their sleep states (OS-controlled ASPM).",
            "No link wake delay for the GPU, NVMe and network card.", "A few watts at idle.", Evidence.Documented, true, true),
        new("disk.ahci-link", "AHCI link power management (HIPM/DIPM)", Disk, new("0b2d69d7-a2a1-449c-9680-f91c70521c60"),
            _ => 0, Names("Active", "HIPM", "HIPM+DIPM", "DIPM", "Lowest"),
            "Keeps SATA links active.",
            "No link wake delay on SATA drives.", "Tiny.", Evidence.Documented, true, true),
        new("disk.ahci-adaptive", "AHCI link power management (adaptive)", Disk, new("dab60367-53fe-4fbc-825e-521d069d2456"),
            _ => 0, Ms,
            "How long a SATA link idles before it may sleep.",
            "0 = never.", "Tiny.", Evidence.Documented, false, true),
        new("disk.turn-off", "Turn off hard disk after", Disk, new("6738e2c4-e8a5-4a42-b16a-e040e769756e"),
            _ => 0, Never,
            "Never spins down or powers off disks.",
            "No spin-up stalls.", "HDDs keep spinning.", Evidence.Documented, true, true),
        new("disk.nvme-primary", "NVMe primary idle timeout / latency tolerance", Disk, new("fc95af4d-40e7-4b6d-835a-56d131dbc80e"),
            _ => 0, Ms,
            "Stops NVMe drives entering their deeper power states.",
            "No wake latency on the boot and game drives.", "A little idle power.", Evidence.Documented, true, true),
        new("disk.nvme-secondary", "NVMe secondary idle timeout / latency tolerance", Disk, new("dbc9e238-6de9-49e3-92cd-8c2b4946b472"),
            _ => 0, Ms,
            "Same, for the secondary NVMe power-state tolerance.",
            "No wake latency on NVMe drives.", "A little idle power.", Evidence.Documented, true, true),
        new("wifi.power", "Wireless adapter power saving", Wireless, new("12bbebe6-58d6-4636-95bb-3217ef867c1a"),
            _ => 0, Names("Maximum performance", "Low power saving", "Medium power saving", "Maximum power saving"),
            "Keeps the Wi-Fi radio at full performance.",
            "No power-save latency on Wi-Fi (Ethernet is still better).", "Wi-Fi uses more power.", Evidence.Documented, true, true),
        new("io.coalescing", "IO coalescing", IoCoalescing, new("c36f0eb4-2988-4a70-8eee-0884fc2c2433"),
            _ => 0, Num,
            "Stops Windows batching IO completions to save power.",
            "Already 0 on stock Ultimate; checked.", "None.", Evidence.Documented, true, false),
        new("cpu.interrupt-steering", "Interrupt steering mode", Steering, new("2bfc24f9-5ea2-4801-8213-3dbae01aa39d"),
            _ => 0, v => v == 0 ? "Default" : "Mode " + v,
            "Puts interrupt steering back to Default.",
            "The Legion's vendor plans had it on \"Processor 1\": every steerable interrupt landed on CPU 1 (about 4,240 DPCs/s there).",
            "None.", Evidence.Measured, true, true),
        new("display.off", "Turn off display after", Display, new("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e"),
            _ => 0, Never,
            "Never turns the screen off on this plan.",
            "A screen waking from sleep is a heavy GPU driver event; matches the desktop.", "Screen stays on.", Evidence.Documented, true, false, DesktopOnly: true),
        new("display.dim", "Dim display after", Display, new("17aaa29b-8b43-4b94-aafe-35f64daaf1ee"),
            _ => 0, Never,
            "Never dims the screen on this plan.",
            "Matches the desktop.", "Screen stays bright.", Evidence.Documented, true, false, DesktopOnly: true),
    };
}

/// <summary>Works out which plan the power tweaks write to, and creates the tuned plan on first apply.</summary>
public sealed class PowerPlanTarget
{
    public const string TunedPlanName = "Stutter Guard - Ultimate";
    private const string StateKey = "plan.tuned";

    private readonly Journal _journal;
    private readonly Func<AppOptions> _options;

    public PowerPlanTarget(Journal journal, Func<AppOptions> options)
    {
        _journal = journal;
        _options = options;
    }

    public PlanTargetKind Kind => _options().PlanTarget;

    public Guid? Resolve()
    {
        switch (Kind)
        {
            case PlanTargetKind.Balanced:
                return PowerApi.Exists(PowerApi.Balanced) ? PowerApi.Balanced : null;
            case PlanTargetKind.CurrentActive:
                return PowerApi.GetActive();
            default:
                if (Guid.TryParse(_journal.GetState(StateKey), out var known) && PowerApi.Exists(known)) return known;
                foreach (var g in PowerApi.EnumerateSchemes())
                {
                    string name = PowerApi.GetName(g);
                    if (name.Contains("Ultimate", StringComparison.OrdinalIgnoreCase) || name == TunedPlanName || name == "DPC Latency Tweaker - Ultimate") return g;
                }
                return null;
        }
    }

    public string Describe()
    {
        var g = Resolve();
        if (g is null)
            return Kind == PlanTargetKind.TunedUltimate ? TunedPlanName + " (created on first apply)" : "(not found)";
        return PowerApi.GetName(g.Value) + " {" + g + "}";
    }

    public Guid Ensure(TweakContext ctx)
    {
        var existing = Resolve();
        if (existing is not null) return existing.Value;
        if (Kind != PlanTargetKind.TunedUltimate) throw new InvalidOperationException("The target power plan doesn't exist.");

        var g = PowerApi.Duplicate(PowerApi.UltimateTemplate);
        try { PowerApi.SetName(g, TunedPlanName); } catch (Exception ex) { Log.Warn("Rename failed: " + ex.Message); }
        ctx.Journal.RecordOriginal("power.plan", "plan-created", g.ToString(), false, null, null);
        ctx.Journal.MarkApplied("power.plan");
        _journal.SetState(StateKey, g.ToString());
        _journal.Save();
        Log.Info("Created power plan " + g);
        return g;
    }
}

public sealed class PowerSettingTweak : TweakBase
{
    private readonly PowerSettingDef _def;
    private readonly PowerPlanTarget _target;
    private readonly Func<AppOptions> _options;

    public PowerSettingTweak(TweakInfo info, Journal journal, PowerSettingDef def, PowerPlanTarget target, Func<AppOptions> options)
        : base(info, journal)
    {
        _def = def;
        _target = target;
        _options = options;
    }

    protected override Probe Read()
    {
        var o = _options();
        uint want = _def.Target(o);
        string planned = _def.Format(want) + (o.ApplyOnBattery ? " (plugged in and battery)" : " (plugged in)");
        var plan = _target.Resolve();
        if (plan is null) return new Probe(false, "(plan not created yet)", planned);

        uint? ac = PowerApi.ReadAc(plan.Value, _def.Sub, _def.Setting);
        if (ac is null) return new Probe(false, "(not on this PC)", planned, "This power setting doesn't exist on this PC.");
        if (!o.ApplyOnBattery) return new Probe(ac == want, _def.Format(ac.Value), planned);

        uint? dc = PowerApi.ReadDc(plan.Value, _def.Sub, _def.Setting);
        return new Probe(ac == want && dc == want,
            $"{_def.Format(ac.Value)} plugged in, {(dc is null ? "?" : _def.Format(dc.Value))} on battery", planned);
    }

    public override void Apply(TweakContext ctx)
    {
        var o = _options();
        uint want = _def.Target(o);
        Guid plan = _target.Ensure(ctx);
        ctx.Backup.ExportPlanOnce(plan);

        uint? ac = PowerApi.ReadAc(plan, _def.Sub, _def.Setting);
        if (ac is not null)
            ctx.Journal.RecordOriginal(Info.Id, "power", JournalRestorer.PowerTarget(plan, _def.Sub, _def.Setting, false), true, null, ac.Value.ToString(CultureInfo.InvariantCulture));
        PowerApi.WriteAc(plan, _def.Sub, _def.Setting, want);

        if (o.ApplyOnBattery)
        {
            uint? dc = PowerApi.ReadDc(plan, _def.Sub, _def.Setting);
            if (dc is not null)
                ctx.Journal.RecordOriginal(Info.Id, "power", JournalRestorer.PowerTarget(plan, _def.Sub, _def.Setting, true), true, null, dc.Value.ToString(CultureInfo.InvariantCulture));
            PowerApi.WriteDc(plan, _def.Sub, _def.Setting, want);
        }

        if (PowerApi.GetActive() == plan) PowerApi.SetActive(plan);
        Log.Info($"Power {_def.Key} = {want} on {plan} (was {ac})");
    }
}

/// <summary>Makes the target plan the active one.</summary>
public sealed class PlanActiveTweak : TweakBase
{
    private readonly PowerPlanTarget _target;

    public PlanActiveTweak(TweakInfo info, Journal journal, PowerPlanTarget target) : base(info, journal)
    {
        _target = target;
    }

    protected override Probe Read()
    {
        var active = PowerApi.GetActive();
        string current = active is null ? "?" : PowerApi.GetName(active.Value);
        var plan = _target.Resolve();
        if (plan is null) return new Probe(false, current, _target.Describe());
        return new Probe(active == plan, current, PowerApi.GetName(plan.Value));
    }

    public override void Apply(TweakContext ctx)
    {
        Guid plan = _target.Ensure(ctx);
        var active = PowerApi.GetActive();
        if (active is not null)
            ctx.Journal.RecordOriginal(Info.Id, "power-active", "active", true, null, active.Value.ToString());
        PowerApi.SetActive(plan);
        Log.Info("Active power plan -> " + plan);
    }
}

public static class PowerModule
{
    public static IEnumerable<ITweak> Build(SystemProfile profile, Journal journal, Func<AppOptions> options)
    {
        var target = new PowerPlanTarget(journal, options);

        yield return new PlanActiveTweak(new TweakInfo
        {
            Id = "power.active",
            Module = "Power",
            Group = "Plan",
            Title = "Use the tuned plan",
            What = "Makes the chosen plan the active one. On the Ultimate path the app makes its own copy of Ultimate Performance (or tunes an existing Ultimate plan).",
            Why = "Every setting below lives in that plan.",
            Cost = "See the settings.",
            Evidence = Evidence.Measured,
            Risk = Risk.Low,
        }, journal, target);

        var o = options();
        foreach (var d in PowerSettings.All)
        {
            bool inProfile = o.PowerProfile == PowerProfileKind.Full ? d.InFull : d.InDevicesOnly;
            if (!inProfile) continue;
            if (d.DesktopOnly && profile.IsLaptop) continue;

            yield return new PowerSettingTweak(new TweakInfo
            {
                Id = "power." + d.Key,
                Module = "Power",
                Group = d.Sub == PowerSettings.Proc ? "CPU" : "Devices",
                Title = d.Title,
                What = d.What,
                Why = d.Why,
                Cost = d.Cost,
                CanBreak = "Nothing; it's a power-plan value. Revert puts the old value back.",
                Evidence = d.Evidence,
                Risk = Risk.Low,
            }, journal, d, target, options);
        }

        yield return new RegistryValueTweak(new TweakInfo
        {
            Id = "power.fast-startup",
            Module = "Power",
            Group = "Windows",
            Title = "Fast Startup off",
            What = "HiberbootEnabled = 0: shutting down really shuts down instead of hibernating the kernel.",
            Why = "With Fast Startup, drivers resume in their old state, and interrupt, MSI and GPU-key changes only take effect after a real restart.",
            Cost = "Cold boots take a few seconds longer.",
            CanBreak = "Nothing known.",
            Evidence = Evidence.Documented,
            Risk = Risk.Low,
            NeedsReboot = true,
        }, journal, @"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled",
            Microsoft.Win32.RegistryValueKind.DWord, 0);
    }
}
