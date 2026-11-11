using System.Globalization;
using System.Text;
using DpcTweaker.Core.Catalog;
using DpcTweaker.Core.Detection;
using DpcTweaker.Core.Infra;
using DpcTweaker.Core.Interrupts;
using DpcTweaker.Core.Model;
using DpcTweaker.Core.Tweaks;

namespace DpcTweaker.Core.Reports;

/// <summary>A plain-text report of the profile and every tweak's live state. Nothing personal in it.</summary>
public static class ScanReport
{
    public static string Build(TweakEngine engine)
    {
        var p = engine.Profile;
        var sb = new StringBuilder();
        sb.AppendLine("Stutter Guard - scan report");
        sb.AppendLine($"Generated {DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}, app {BuildInfo.Version}, built {BuiltAt()}");
        sb.AppendLine(new string('=', 72));
        sb.AppendLine();

        sb.AppendLine("SYSTEM");
        sb.AppendLine($"  Windows        {p.Windows.Display}, {p.Windows.Edition}");
        sb.AppendLine($"  Machine        {p.Manufacturer} {p.Model}".TrimEnd());
        sb.AppendLine($"  Form factor    {p.FormFactor} ({p.FormFactorEvidence})");
        sb.AppendLine($"  CPU            {p.CpuName} ({p.CpuVendor})");
        sb.AppendLine($"  CPU layout     {p.CpuKind}: {p.Cpu.Describe()}");
        if (p.Cpu.IsDualCcdX3D)
            sb.AppendLine($"  V-Cache CCD    CPUs {p.Cpu.VCacheGroup!.CpuRange()} (games go here; interrupts go elsewhere)");
        sb.AppendLine($"  Security       {p.Security.Display}");
        if (p.TimerResolution is { } tr)
            sb.AppendLine($"  Timer          now {tr.Current / 10000.0:0.000} ms (range {tr.Max / 10000.0:0.000}-{tr.Min / 10000.0:0.000} ms)");
        sb.AppendLine($"  WHEA errors    {(p.WheaErrors14Days is null ? "unknown" : p.WheaErrors14Days + " in the last 14 days")}");
        sb.AppendLine($"  Software       Process Lasso {(p.Software.ProcessLasso ? "yes" : "no")}, AMD 3D V-Cache optimizer {(p.Software.AmdVCacheService ? "yes" : "no")}, Xbox Game Bar {(p.Software.XboxGameBar ? "yes" : "no")}");
        if (p.Software.OtherTimerTools.Count > 0)
            sb.AppendLine($"  Timer tools    already running: {string.Join(", ", p.Software.OtherTimerTools)}");
        sb.AppendLine();

        sb.AppendLine("GPUS");
        if (p.Gpus.Count == 0) sb.AppendLine("  none found");
        foreach (var g in p.Gpus)
        {
            sb.AppendLine($"  {g.Label}");
            sb.AppendLine($"    vendor {g.Vendor}, driver {g.DriverVersion} ({g.DriverDate}), key ...\\{g.ClassSubkey}");
            sb.AppendLine($"    device {g.InstanceId}");
            sb.AppendLine($"    driver file {g.DriverFile ?? "(not found)"}");
            if (g.MemoryBytes is long mem) sb.AppendLine($"    memory {mem / (1024 * 1024)} MB");
            if (g.DriverKeyScan is { } scan)
            {
                sb.AppendLine("    keys the driver reads:");
                foreach (var kv in scan.OrderBy(k => k.Key))
                    sb.AppendLine($"      {(kv.Value ? "yes" : "NO ")}  {kv.Key}");
            }
            if (g.DriverScanError is not null) sb.AppendLine("    driver scan failed: " + g.DriverScanError);
        }
        sb.AppendLine();

        sb.AppendLine("PCI DEVICES");
        foreach (var d in p.Devices.OrderBy(d => d.Kind).ThenBy(d => d.Name))
        {
            var (policy, mask) = AffinityTweak.ReadLive(d);
            uint? msi = RegistryOps.ReadDword(d.MsiKey, "MSISupported");
            sb.AppendLine($"  [{d.Kind}] {d.Name} ({d.ShortId}, class {d.BaseClass:X2}/{d.SubClass:X2}/{d.ProgIf:X2}){(d.Started ? "" : " NOT RUNNING")}{(d.Problem != 0 ? " problem " + d.Problem : "")}");
            sb.AppendLine($"      {d.InstanceId}");
            sb.AppendLine($"      interrupts: {d.InterruptSupportText}; MSISupported {(msi is null ? "not set" : msi.ToString())}; affinity {AffinityTweak.Show(policy, mask)}");
            if (d.Kind is not (PciKind.Never or PciKind.Storage) || d.Attached.HasDevices || d.Attached.Monitors + d.Attached.AudioEndpoints > 0)
                sb.AppendLine($"      attached: {d.Attached.Describe()}, {d.Attached.AudioEndpoints} audio endpoint(s), {d.Attached.Total} child node(s)" +
                              $"{(d.Attached.Names.Count > 0 ? " [" + string.Join(", ", d.Attached.Names) + "]" : "")}");
            if (d.Bus is not null)
                sb.AppendLine(d.Address is uint addr
                    ? $"      location: bus {d.Bus}, device {addr >> 16}, function {addr & 0xFFFF}"
                    : $"      location: bus {d.Bus}, device/function unknown");
            if (d.Kind is PciKind.Ethernet or PciKind.Wifi)
                sb.AppendLine($"      network: {(d.Connected == true ? "connected" : d.Connected == false ? "not connected" : "state unknown")}, RSS {(d.RssCapable ? "yes, queues " + string.Join("/", d.RssQueueOptions) : "no")}");
        }
        if (p.Devices.Count == 0) sb.AppendLine("  (none read)");
        sb.AppendLine();

        var driverAudit = DriverAuditor.Audit(p);
        sb.AppendLine("DRIVER ADVISORIES");
        var buggedDrivers = driverAudit.Where(a => a.IsBugged).ToList();
        if (buggedDrivers.Count == 0)
        {
            sb.AppendLine("  All detected catalogued devices are running verified stable/clean driver versions (no known DPC regressions).");
        }
        else
        {
            foreach (var adv in buggedDrivers)
            {
                sb.AppendLine($"  [HAZARD - {adv.Entry.Severity.ToUpperInvariant()}] {adv.DeviceName} ({adv.DriverFile})");
                sb.AppendLine($"      Hardware ID:         {adv.HardwareId}");
                sb.AppendLine($"      Installed Version:   {adv.InstalledVersion}");
                sb.AppendLine($"      Action:              {adv.ActionText}");
                sb.AppendLine($"      Typical Latency:     ~{adv.Entry.TypicalLatencyUs:N0} us DPC/ISR execution spike");
                sb.AppendLine($"      Failure Mechanism:   {adv.Entry.FailureMechanism}");
                sb.AppendLine($"      Prescribed Fix:      {adv.Entry.Fix}");
                sb.AppendLine();
            }
        }
        sb.AppendLine();

        var plan = engine.Plan;
        sb.AppendLine("INTERRUPT PLAN");
        if (!plan.Applicable) sb.AppendLine("  " + plan.NotApplicableReason);
        else
        {
            sb.AppendLine($"  Kept free     CPUs {CpuTopology.FormatCpus(plan.ReservedCpus)}: {plan.ReservedReason}");
            sb.AppendLine($"  Core order    {string.Join(", ", plan.Candidates.Select(c => c.Cpu))} ({plan.RankingSource})");
            foreach (var a in plan.Assignments)
                sb.AppendLine($"  {a.CpuText,-14}{a.Device.Name} [{a.Role}]{(a.Shared ? " shared" : "")}{(a.Kept ? " kept (already pinned here)" : "")}");
            foreach (var n in plan.Notes) sb.AppendLine("  note: " + n);
            if (engine.Bench is { } b)
            {
                sb.AppendLine("  Benchmark     " + b.Summary);
                foreach (var c in b.Cores)
                    sb.AppendLine($"    CPU {c.Cpu,2}: wake median {c.WakeMedianUs,6:0.0} us, p90 {c.WakeP90Us,6:0.0}, p95 {c.WakeP95Us,6:0.0}, p99 {c.WakeP99Us,6:0.0}, p99.9 {c.WakeP999Us,6:0}, max {c.WakeMaxUs,6:0}, >100us {c.LateOver100Pct,5:0.00}%, workload {c.SpeedMs,6:0.0} ms, idle {c.InterruptsPerSec,6:0} int/s {c.DpcsPerSec,6:0} DPC/s, rank {c.SchedulingClass}");
            }
        }
        sb.AppendLine();

        if (engine.LastCheck is { } chk)
        {
            sb.AppendLine("INTERRUPT CHECK (" + chk.When.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + ", interrupts/s idle -> under load, DPC change)");
            foreach (var l in chk.Devices)
                sb.AppendLine($"  {(l.Ok ? "ok   " : "CHECK")} {l.Cpus,-12} {l.Device} ({l.Role}): {l.IdlePerSec:0} -> {l.LoadPerSec:0}, DPC {l.DpcDeltaPerSec:+0;-0;0}. {l.Verdict}");
            if (chk.ReservedBusiestCpu >= 0)
                sb.AppendLine($"  kept-free CPUs: +{chk.ReservedDeltaPerSec:0}/s in total, busiest CPU {chk.ReservedBusiestCpu} (+{chk.ReservedBusiestDeltaPerSec:0}/s)");
            foreach (var n in chk.Notes) sb.AppendLine("  note: " + n);
            sb.AppendLine();
        }

        sb.AppendLine("NETWORK ADAPTERS");
        foreach (var n in p.NetAdapters)
        {
            sb.AppendLine($"  {(n.IsWifi ? "Wi-Fi" : "Wired")}  {n.Name} ({n.ClassKey}): {(n.Connected == true ? "connected" : "not connected")}, {n.Params.Count} properties, hidden values: {string.Join(", ", n.HiddenValues)}");
            foreach (var prm in n.Params.OrderBy(x => x.Keyword, StringComparer.OrdinalIgnoreCase))
            {
                string? cur = RegWrite.AsText(RegistryOps.Read(n.ClassKey, prm.Keyword));
                string opts = prm.Options.Count > 0 ? $" [{string.Join(", ", prm.Options.Select(o => $"{o.Value}={o.Text}"))}]" : "";
                sb.AppendLine($"      {prm.Keyword,-32} = {(cur ?? "(unset)"),-8} (default {prm.Default ?? "-"}) {prm.Description}{opts}");
            }
        }
        sb.AppendLine();

        sb.AppendLine("CPU TOPOLOGY & SETS");
        foreach (var g in p.Cpu.L3Groups)
            sb.AppendLine($"  L3 group {g.Id}: {g.SizeBytes / (1024 * 1024)} MB, mask 0x{g.Mask:X}, CPUs {g.CpuRange()}");
        foreach (var c in p.Cpu.Cpus)
        {
            var cs = p.CpuSets.FirstOrDefault(s => s.LogicalIndex == c.Index);
            sb.AppendLine($"  CPU {c.Index,2}: core {c.CoreId,2}, {(c.IsSecondThread ? "2nd" : "1st")} thread, L3 {c.L3Id}, eff {c.EfficiencyClass}, rank {(cs?.SchedulingClass.ToString() ?? "-")}{(cs?.Parked == true ? " (parked)" : "")}");
        }
        sb.AppendLine();

        if (p.Warnings.Count > 0)
        {
            sb.AppendLine("WARNINGS");
            foreach (var w in p.Warnings) sb.AppendLine("  - " + w);
            sb.AppendLine();
        }

        var o = engine.Options;
        sb.AppendLine("OPTIONS");
        sb.AppendLine($"  Power plan     {o.PlanTarget}, {o.PowerProfile}, C-states {o.CState}, {(o.ApplyOnBattery ? "plugged in + battery" : "plugged in only")}");
        sb.AppendLine($"  Timer          {o.TimerResolution / 10000.0:0.000} ms");
        sb.AppendLine($"  Interrupts     {(o.KeepValidPins ? "keep pins that follow the rules" : "re-plan every device")}, MSI mode {o.Msi}");
        sb.AppendLine($"  Services/tasks {DpcTweaker.Core.Catalog.Presets.Label(o.Cleanup)}");
        sb.AppendLine($"  Hardware       {p.Hardware.Describe()}");
        sb.AppendLine($"  Inventory      {p.Services.Count} services, {p.Tasks.Count} scheduled tasks; catalog knows {p.Services.Count(s => DpcTweaker.Core.Catalog.Catalogs.Services.Find(s.Name) is not null || DpcTweaker.Core.Catalog.Catalogs.Services.Find(DpcTweaker.Core.Catalog.Catalogs.TemplateName(s.Name)) is not null)} services and {p.Tasks.Count(t => DpcTweaker.Core.Catalog.Catalogs.Tasks.Find(t.Path) is not null)} tasks");
        sb.AppendLine();

        var uncatSvcs = p.Services
            .Where(s => !s.IsPerUserInstance &&
                        DpcTweaker.Core.Catalog.Catalogs.Services.Find(s.Name) is null &&
                        DpcTweaker.Core.Catalog.Catalogs.Services.Find(DpcTweaker.Core.Catalog.Catalogs.TemplateName(s.Name)) is null)
            .OrderBy(s => s.IsWindowsBinary).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (uncatSvcs.Count > 0)
        {
            sb.AppendLine($"UNCATALOGUED SERVICES ({uncatSvcs.Count})");
            foreach (var s in uncatSvcs)
                sb.AppendLine($"  {s.Name,-36} [{StartTypes.Label(s.Start),-25}] {(s.IsWindowsBinary ? "Windows" : s.Company ?? "3rd-party"),-22} {s.DisplayName} ({s.Path})");
            sb.AppendLine();
        }

        var uncatTasks = p.Tasks
            .Where(t => DpcTweaker.Core.Catalog.Catalogs.Tasks.Find(t.Path) is null)
            .OrderBy(t => t.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (uncatTasks.Count > 0)
        {
            sb.AppendLine($"UNCATALOGUED SCHEDULED TASKS ({uncatTasks.Count})");
            foreach (var t in uncatTasks)
                sb.AppendLine($"  {(t.Enabled ? "enabled " : "disabled")} {t.Path,-56} [{t.Author ?? "-"}] triggers: {(string.IsNullOrEmpty(t.TriggersText) ? "-" : t.TriggersText)} -> {t.ActionsText}");
            sb.AppendLine();
        }

        sb.AppendLine("TWEAKS (live state)");
        foreach (var module in engine.Tweaks.GroupBy(t => t.Info.Module))
        {
            sb.AppendLine("  " + module.Key.ToUpperInvariant());
            foreach (var t in module)
            {
                var s = engine.StateOf(t);
                string group = string.IsNullOrEmpty(t.Info.Group) ? "" : $"[{t.Info.Group}] ";
                sb.AppendLine($"    {Label(s.Status),-15} {group}{t.Info.Title}");
                sb.AppendLine($"                    now: {s.Current}");
                sb.AppendLine($"                    planned: {s.Planned}");
                if (!string.IsNullOrEmpty(s.Note)) sb.AppendLine($"                    note: {s.Note}");
            }
        }
        sb.AppendLine();
        sb.AppendLine($"Journal: {engine.Journal.EntryCount} recorded values, {engine.Journal.AppliedCount} tweaks applied by the app.");
        return sb.ToString();
    }

    private static string BuiltAt()
    {
        try { return Environment.ProcessPath is { } p ? File.GetLastWriteTime(p).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "?"; }
        catch { return "?"; }
    }

    public static string Label(TweakStatus s) => s switch
    {
        TweakStatus.Applied => "APPLIED",
        TweakStatus.NotApplied => "not applied",
        TweakStatus.PendingReboot => "PENDING REBOOT",
        TweakStatus.Drifted => "DRIFTED",
        TweakStatus.NotEffective => "NOT EFFECTIVE",
        TweakStatus.NotApplicable => "n/a",
        TweakStatus.Failed => "FAILED",
        _ => "unknown",
    };

    public static string Write(TweakEngine engine, string? folder = null)
    {
        folder ??= Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, $"dpc-tweaker-scan-{DateTime.Now:yyyyMMdd-HHmm}.txt");
        File.WriteAllText(file, Build(engine), new UTF8Encoding(true));
        Log.Info("Scan report written: " + file);
        return file;
    }
}
