using System.Globalization;
using DpcTweaker.Core.Detection;
using DpcTweaker.Core.Infra;
using DpcTweaker.Core.Interrupts;
using DpcTweaker.Core.Model;
using DpcTweaker.Core.Native;
using Microsoft.Win32;

namespace DpcTweaker.Core.Tweaks;

public enum MsiMode
{
    /// <summary>Only line-based devices in classes known to work with MSI.</summary>
    Safe,
    /// <summary>Every device whose hardware advertises MSI (optional, risk screen).</summary>
    All,
}

/// <summary>Pins one device's interrupts to the planned core(s): Affinity Policy, DevicePolicy 4.</summary>
public sealed class AffinityTweak : TweakBase
{
    public const string ModuleName = "Interrupts";
    public const string GroupName = "Interrupt affinity";

    private readonly Assignment _a;

    public AffinityTweak(Assignment a, Journal journal) : base(Describe(a), journal)
    {
        _a = a;
    }

    public Assignment Assignment => _a;
    private string WrittenKey => "irq.written." + _a.Device.InstanceId;

    private static TweakInfo Describe(Assignment a) => new()
    {
        Id = "irq." + a.Device.InstanceId,
        Module = ModuleName,
        Group = GroupName,
        Title = $"{a.Device.Name} → {a.CpuText}" + (a.Shared ? " (shared)" : "") + (a.Kept ? " (kept)" : ""),
        What = $"Sets this device's Affinity Policy (Enum\\{a.Device.InstanceId}\\Device Parameters\\Interrupt Management\\Affinity Policy): DevicePolicy = 4 (specified processors) and AssignmentSetOverride = {a.CpuText}. Windows then sends all of the device's interrupts there, and its DPCs normally follow. Role: {RoleText(a.Role)}. {a.Reason}",
        Why = "Every busy device on its own core, away from the cores games use, was the desktop's biggest single win: 515 µs worst case with nothing pinned, 20.9 µs with every busy device on its own CCD1 core (LatencyMon).",
        Cost = "None while the core is awake. If Windows parks that core, its interrupts wake it up first.",
        CanBreak = "Nothing known. A driver reinstall can wipe the pin (the row then shows Drifted). Some network drivers steer their own interrupts: the check after the restart shows where they really land.",
        Evidence = Evidence.Measured,
        Risk = Risk.Low,
        NeedsReboot = true,
    };

    public static string RoleText(DeviceRole r) => r switch
    {
        DeviceRole.Gpu => "GPU",
        DeviceRole.Input => "input (mouse / keyboard) USB controller",
        DeviceRole.Audio => "active audio path",
        DeviceRole.Network => "network adapter in use",
        DeviceRole.OtherUsb => "other USB controller",
        _ => "low-traffic device",
    };

    public static (uint? Policy, ulong? Mask) ReadLive(PciDevice d) =>
        (RegistryOps.ReadDword(d.AffinityKey, "DevicePolicy"), RegWrite.AsMask(RegistryOps.Read(d.AffinityKey, "AssignmentSetOverride")));

    public static string Show(uint? policy, ulong? mask) => policy switch
    {
        null => "Windows default (no Affinity Policy)",
        0 => "Windows default (DevicePolicy 0)",
        1 => "close to the device (DevicePolicy 1)",
        2 => "all CPUs (DevicePolicy 2)",
        3 => "one CPU chosen by Windows (DevicePolicy 3)",
        4 => (mask is null ? "DevicePolicy 4 without a CPU mask" : RegWrite.FormatMask(mask.Value)) + " (DevicePolicy 4)",
        5 => "spread across all CPUs (DevicePolicy 5)",
        _ => $"DevicePolicy {policy}",
    };

    protected override Probe Read()
    {
        var (policy, mask) = ReadLive(_a.Device);
        bool ok = policy == 4 && mask == _a.Mask;
        string planned = _a.CpuText + " only (DevicePolicy 4)";
        string? note = null;
        if (!ok && Journal.IsApplied(Info.Id) && ulong.TryParse(Journal.GetState(WrittenKey), out ulong written) && written == mask && policy == 4)
            note = "The plan changed since you applied it (a new benchmark or different devices). Apply again to move it.";
        return new Probe(ok, Show(policy, mask), planned, Note: note);
    }

    public override void Apply(TweakContext ctx)
    {
        var d = _a.Device;
        ctx.Backup.ExportKeyOnce(d.InterruptKey);
        RegWrite.Set(ctx, Info.Id, d.AffinityKey, "DevicePolicy", RegistryValueKind.DWord, 4);
        RegWrite.Set(ctx, Info.Id, d.AffinityKey, "AssignmentSetOverride", RegistryValueKind.Binary, RegWrite.MaskBytes(_a.Mask));
        var (policy, mask) = ReadLive(d);
        if (policy != 4 || mask != _a.Mask)
            throw new InvalidOperationException("Wrote the Affinity Policy, but it reads back as " + Show(policy, mask) + ".");
        ctx.Journal.SetState(WrittenKey, _a.Mask.ToString(CultureInfo.InvariantCulture));
    }

    public override void Revert(TweakContext ctx)
    {
        base.Revert(ctx);
        ctx.Journal.SetState(WrittenKey, null);
    }
}

/// <summary>Points the network adapter's receive queues (RSS) at the same physical cores as its interrupts, SMT included.</summary>
public sealed class RssTweak : TweakBase
{
    private readonly Assignment _a;
    private readonly List<(string Name, string Value)> _items;

    public RssTweak(Assignment a, Journal journal) : base(new TweakInfo
    {
        Id = "rss." + (a.Device.NetCfgInstanceId ?? a.Device.InstanceId),
        Module = AffinityTweak.ModuleName,
        Group = AffinityTweak.GroupName,
        Title = $"{a.Device.Name}: receive queues (RSS) on {a.CpuText}",
        What = "Sets the adapter's standard RSS keywords: RSS on, " +
               (a.Cpus.Length > 1 ? $"{a.Cpus.Length} queues on {a.Cpus.Length} cores" : "1 queue on 1 core") +
               $", processors {(a.Rss?.BaseProcNumber ?? a.Cpus[0])}-{(a.Rss?.MaxProcNumber ?? a.Cpus[^1])}" +
               (a.Rss is { ThreadsPerCore: > 1 } rp ? $" ({rp.NumProcessors} logical processors over {a.Cpus.Length} SMT core(s))" : "") +
               ". Set-NetAdapterRss writes the same keywords. The RSS profile is left at the driver's default.",
        Why = "Received packets are processed (DPC) on the RSS cores. Putting those on the same cores as the adapter's interrupts keeps network work off the game's cores. On the desktop: ndis DPCs only on CPUs 20/22, CCD0 untouched. Games use at most 2 queues.",
        Cost = "None for gaming. Very fast multi-stream downloads use fewer cores.",
        CanBreak = "Nothing known. A driver update can reset these keywords.",
        Evidence = Evidence.Measured,
        Risk = Risk.Low,
        NeedsReboot = true,
    }, journal)
    {
        _a = a;
        var d = a.Device;
        // The planner works the processor span out from the topology. *MaxRssProcessors counts
        // logical processors, so on SMT it is cores * threads, and *RssMaxProcNumber is the last LP
        // of the last core. Without that, "base 20, max 22, 2 processors" picks LPs 20 and 21:
        // the two threads of a single core.
        var rss = a.Rss ?? new RssPlan(a.Cpus[0], a.Cpus[^1], a.Cpus.Length, 1);
        _items = new List<(string, string)>();
        if (d.RssCapable) _items.Add(("*RSS", "1"));
        string queues = a.Cpus.Length.ToString(CultureInfo.InvariantCulture);
        if (d.RssQueueOptions.Contains(queues)) _items.Add(("*NumRssQueues", queues));
        _items.Add(("*RssBaseProcNumber", rss.BaseProcNumber.ToString(CultureInfo.InvariantCulture)));
        _items.Add(("*MaxRssProcessors", rss.NumProcessors.ToString(CultureInfo.InvariantCulture)));
        _items.Add(("*RssMaxProcNumber", rss.MaxProcNumber.ToString(CultureInfo.InvariantCulture)));
    }

    protected override Probe Read()
    {
        string? key = _a.Device.ClassKey;
        if (key is null) return new Probe(false, "?", "?", "The adapter's driver key wasn't found.");
        var parts = new List<string>();
        bool ok = true;
        foreach (var (name, value) in _items)
        {
            string? cur = RegWrite.AsText(RegistryOps.Read(key, name));
            parts.Add($"{name.TrimStart('*')} {cur ?? "default"}");
            if (cur != value) ok = false;
        }
        string planned = string.Join(", ", _items.Select(i => $"{i.Name.TrimStart('*')} {i.Value}"));
        return new Probe(ok, string.Join(", ", parts), planned);
    }

    public override void Apply(TweakContext ctx)
    {
        string key = _a.Device.ClassKey ?? throw new InvalidOperationException("The adapter's driver key wasn't found.");
        ctx.Backup.ExportKeyOnce(key);
        foreach (var (name, value) in _items) RegWrite.Set(ctx, Info.Id, key, name, RegistryValueKind.String, value);
    }
}

/// <summary>Switches a line-based device to MSI: MessageSignaledInterruptProperties\MSISupported = 1.</summary>
public sealed class MsiTweak : TweakBase
{
    public const string GroupName = "MSI mode";
    private readonly PciDevice _d;

    public MsiTweak(PciDevice d, Journal journal, bool safeClass, bool storage, bool allMode) : base(new TweakInfo
    {
        Id = "msi." + d.InstanceId,
        Module = AffinityTweak.ModuleName,
        Group = GroupName,
        Title = $"{d.Name}: MSI mode",
        What = $"Sets MSISupported = 1 for this device, so it signals interrupts by a memory write instead of a shared physical IRQ line. The hardware supports: {d.InterruptSupportText}.",
        Why = safeClass
            ? "Nothing shared, cheaper per interrupt, and needed for more than one vector. The desktop's GPU HDMI audio sat on line-based IRQ 25 while everything else used MSI."
            : "Same reason as the safe list, but this class of device isn't known to be fine with it. Optional only.",
        Cost = "None.",
        CanBreak = storage
            ? "This is a storage controller. If its driver misbehaves with MSI and it holds the Windows drive, Windows won't boot (INACCESSIBLE_BOOT_DEVICE). Undo from Safe Mode with undo-all.ps1, or with the restore point."
            : safeClass
                ? "Rarely, a driver advertises MSI but misbehaves: the device stops (Device Manager code 10, no sound or no network). Revert this row and restart."
                : "Some drivers advertise MSI but misbehave with it: the device stops working (code 10). Revert this row and restart; if Windows won't start, use Safe Mode and undo-all.ps1.",
        Evidence = safeClass ? Evidence.Measured : Evidence.Anecdotal,
        Risk = storage ? Risk.High : safeClass ? Risk.Low : Risk.Medium,
        NeedsReboot = true,
        DefaultSelected = (safeClass || allMode) && !storage,
    }, journal)
    {
        _d = d;
    }

    public static bool IsSafeClass(PciDevice d) =>
        d.Kind is PciKind.Gpu or PciKind.GpuAudio or PciKind.Audio or PciKind.UsbController or PciKind.Ethernet or PciKind.Wifi;

    protected override Probe Read()
    {
        if (!_d.SupportsMsi) return new Probe(false, "line-based", "MSI", "The hardware doesn't support MSI.");
        uint? v = RegistryOps.ReadDword(_d.MsiKey, "MSISupported");
        string cur = v == 1 ? "MSI (MSISupported 1)" : v is null ? "line-based (no MSISupported value)" : $"line-based (MSISupported {v})";
        return new Probe(v == 1, cur, "MSI (MSISupported 1)");
    }

    public override TweakState Detect()
    {
        var s = base.Detect();
        // The safety net: after the restart, a device that stopped working is flagged at once.
        if (s.Status is TweakStatus.Applied or TweakStatus.PendingReboot && Journal.IsApplied(Info.Id) && CfgMgr.Locate(_d.InstanceId) is uint dn)
        {
            var (status, problem) = CfgMgr.Status(dn);
            bool restarted = Journal.AppliedAt(Info.Id) is DateTime t && t < BootTime.LastBoot;
            if (restarted && (status & CfgMgr.DN_HAS_PROBLEM) != 0 && problem != 0)
                return s with { Status = TweakStatus.NotEffective, Note = $"The device reports problem code {problem} since the change: its driver doesn't work with MSI. Revert this row and restart." };
            if (restarted && (status & CfgMgr.DN_STARTED) == 0)
                return s with { Status = TweakStatus.NotEffective, Note = "The device isn't running since the change. Revert this row and restart." };
        }
        return s;
    }

    public override void Apply(TweakContext ctx)
    {
        if (!_d.SupportsMsi) throw new InvalidOperationException("The hardware doesn't support MSI.");
        ctx.Backup.ExportKeyOnce(_d.InterruptKey);
        RegWrite.Set(ctx, Info.Id, _d.MsiKey, "MSISupported", RegistryValueKind.DWord, 1);
    }
}

/// <summary>A pin the app wrote earlier for a device that's no longer in the plan. Revert-only.</summary>
public sealed class LeftoverAffinityTweak : TweakBase
{
    private readonly string _instance;

    public LeftoverAffinityTweak(string id, string instance, string name, Journal journal) : base(new TweakInfo
    {
        Id = id,
        Module = AffinityTweak.ModuleName,
        Group = AffinityTweak.GroupName,
        Title = $"{name}: pinned earlier, no longer in the plan",
        What = "The app pinned this device's interrupts earlier, but the device is now disabled, not working, removed, or no longer planned. Revert puts back the Affinity Policy it had before.",
        Why = "Kept so every change the app made can be undone one by one.",
        Cost = "None.",
        Evidence = Evidence.Documented,
        Risk = Risk.Low,
        NeedsReboot = true,
        DefaultSelected = false,
    }, journal)
    {
        _instance = instance;
    }

    private string Key => @"HKLM\SYSTEM\CurrentControlSet\Enum\" + _instance + @"\Device Parameters\Interrupt Management\Affinity Policy";

    protected override Probe Read()
    {
        uint? policy = RegistryOps.ReadDword(Key, "DevicePolicy");
        ulong? mask = RegWrite.AsMask(RegistryOps.Read(Key, "AssignmentSetOverride"));
        return new Probe(true, AffinityTweak.Show(policy, mask), "revert to what it was before the app",
            Note: "Not in the current plan. Tick it and press Revert selected (or use the Revert button) to put back its previous policy.");
    }

    public override void Apply(TweakContext ctx)
    {
        // Nothing to apply: this row only exists so the old pin can be reverted.
    }
}

public static class InterruptsModule
{
    public static IEnumerable<ITweak> Build(SystemProfile profile, Journal journal, AppOptions options, AffinityPlan plan)
    {
        if (plan.Applicable)
        {
            foreach (var a in plan.Assignments)
            {
                yield return new AffinityTweak(a, journal);
                if (a.Role == DeviceRole.Network && a.Device.ClassKey is not null && a.Device.RssCapable)
                    yield return new RssTweak(a, journal);
            }
        }

        // Pins the app wrote earlier for devices that are no longer in the plan (disabled, failed, removed):
        // keep a row so they can still be reverted one by one.
        var planned = plan.Assignments.Select(a => "irq." + a.Device.InstanceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string id in journal.TweakIds().Where(i => i.StartsWith("irq.", StringComparison.OrdinalIgnoreCase) && !planned.Contains(i)))
        {
            string inst = id[4..];
            var dev = profile.Devices.FirstOrDefault(d => string.Equals(d.InstanceId, inst, StringComparison.OrdinalIgnoreCase));
            yield return new LeftoverAffinityTweak(id, inst, dev?.Name ?? inst, journal);
        }

        bool Touched(string id) => journal.IsApplied(id) || journal.OriginalsFor(id).Count > 0;
        // A device the app switched to MSI keeps its row even if it stopped working: that's when the row matters most.
        foreach (var d in profile.Devices.Where(d => d.SupportsMsi && (d.Started && d.Problem == 0 || Touched("msi." + d.InstanceId)))
                     .OrderBy(d => MsiTweak.IsSafeClass(d) ? 0 : 1).ThenBy(d => d.Name))
        {
            bool safe = MsiTweak.IsSafeClass(d);
            bool storage = d.Kind == PciKind.Storage;
            string id = "msi." + d.InstanceId;
            bool alreadyMsi = RegistryOps.ReadDword(d.MsiKey, "MSISupported") == 1;
            bool touched = Touched(id);
            if (alreadyMsi && !touched) continue;            // nothing to do: the driver already uses MSI
            if (!safe && options.Msi != MsiMode.All && !touched) continue;
            yield return new MsiTweak(d, journal, safe, storage, options.Msi == MsiMode.All);
        }
    }
}
