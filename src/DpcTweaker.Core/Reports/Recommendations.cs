using DpcTweaker.Core.Catalog;
using DpcTweaker.Core.Detection;
using DpcTweaker.Core.Interrupts;
using DpcTweaker.Core.Tweaks;

namespace DpcTweaker.Core.Reports;

public sealed record RecLink(string Label, string Target);

/// <summary>One recommendation card. Action "lasso" = the card offers to save starter Lasso rules.</summary>
public sealed record Recommendation(string Id, string Title, string When, IReadOnlyList<string> Lines, IReadOnlyList<RecLink> Links, string? Action = null);

/// <summary>
/// Things the app recommends rather than does itself (spec "Recommendations"), shown when they
/// apply to this PC. Nothing third-party is bundled or downloaded by the app.
/// </summary>
public static class Recommendations
{
    public static List<Recommendation> For(SystemProfile p, AppOptions o, AffinityPlan plan)
    {
        var list = new List<Recommendation>();

        // 1. Live Driver Advisories from Catalog
        var driverAudit = DriverAuditor.Audit(p);
        foreach (var adv in driverAudit.Where(a => a.IsBugged))
        {
            var lines = new List<string>
            {
                $"Installed driver version: {adv.InstalledVersion} (detected on {adv.DeviceName}).",
                $"Action required: {adv.ActionText}.",
                $"Latency impact: typical DPC execution spikes of ~{adv.Entry.TypicalLatencyUs:N0} µs observed in kernel traces.",
                $"Failure mechanism: {adv.Entry.FailureMechanism}",
                $"Observed symptoms: {adv.Entry.Symptom}",
                $"Prescribed fix: {adv.Entry.Fix}"
            };

            var links = new List<RecLink>();
            if (adv.Entry.DeviceClass == "GPU" && adv.HardwareId.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase))
                links.Add(new RecLink("NVIDIA Driver Downloads", "https://www.nvidia.com/Download/index.aspx"));
            else if (adv.Entry.DeviceClass == "GPU" && adv.HardwareId.Contains("VEN_1002", StringComparison.OrdinalIgnoreCase))
                links.Add(new RecLink("AMD Drivers and Support", "https://www.amd.com/en/support"));
            else if (adv.HardwareId.Contains("VEN_8086", StringComparison.OrdinalIgnoreCase))
                links.Add(new RecLink("Intel Download Center", "https://www.intel.com/content/www/us/en/download-center/home.html"));
            else if (adv.HardwareId.Contains("VEN_10EC", StringComparison.OrdinalIgnoreCase))
                links.Add(new RecLink("Realtek Communications", "https://www.realtek.com/"));

            list.Add(new Recommendation(
                "driver-" + adv.DriverFile.Replace(".", "-").ToLowerInvariant() + "-" + Math.Abs(adv.HardwareId.GetHashCode()),
                $"Driver Advisory: {adv.DeviceName} ({adv.DriverFile})",
                $"{adv.Entry.Severity} Latency Hazard (~{adv.Entry.TypicalLatencyUs:N0} µs)",
                lines,
                links));
        }

        list.Add(new Recommendation("winutil", "Prepare: clean up Windows with Chris Titus WinUtil", "Before tuning",
            new[]
            {
                "WinUtil is a free, open-source Windows cleanup tool. It is not part of this app and changes a lot at once, so use its Standard preset only.",
                "Open PowerShell as administrator and run:  irm \"https://christitus.com/win\" | iex",
                "Accept its restore point. In Tweaks, pick Standard. Leave the Updates tab on Default. Skip any Advanced tweak that is not understood.",
                "Then turn startup apps off (Settings > Apps > Startup) and restart, so the core benchmark and LatencyMon measure a quiet PC. Turn back on desired apps afterwards.",
            },
            new[] { new RecLink("WinUtil on GitHub", "https://github.com/ChrisTitusTech/winutil"), new RecLink("Startup apps settings", "ms-settings:startupapps") }));

        var split = LassoRules.SplitFor(p, plan);
        if (p.ParkingDependentCpu && split is not null)
        {
            bool lassoPath = o.PlanTarget == PlanTargetKind.TunedUltimate;
            string games = CpuTopology.FormatCpus(split.GameCpus);
            string other = CpuTopology.FormatCpus(split.OtherCpus);
            var lines = new List<string>
            {
                lassoPath
                    ? "The Tuned Ultimate plan is selected, so Windows no longer decides which cores games run on. Process Lasso does it instead, matching the reference desktop configuration."
                    : "The Balanced path is selected, where Windows parking (and AMD's V-Cache optimizer or Intel Thread Director) picks the cores. Process Lasso is the alternative if pinning games manually; it pairs with the Tuned Ultimate plan.",
                "Turn every automatic feature off: ProBalance, IdleSaver, SmartTrim, Performance Mode, the Bitsum power plan, Gaming Mode. Set Lasso to start the tuned power plan.",
                $"Games → {split.GameLabel} (CPUs {games}). dwm.exe → the same CPUs, so the desktop compositor sits next to the game.",
                $"A separate profile (\"Game Mode\") with a catch-all *.* → CPUs {other} as its last rule. Background apps then stay off the game's cores during gameplay.{split.Note}",
                "Per game: High priority and thread priority boost off. On the reference desktop that removed about 90% of the remaining 1% low dips.",
                "\"Save starter rules\" writes these rules for this CPU as a Process Lasso JSON rules file to the Desktop (matching the reference configuration). Import it into an isolated profile so the default profile stays intact.",
                $"Add every played game before switching to that profile: a game that isn't in the list falls under the catch-all and runs on CPUs {other}{(p.Cpu.IsHybrid ? " (E-cores only, a severe slowdown)" : "")}.",
            };
            list.Add(new Recommendation("lasso", "Process Lasso, set up for core separation", p.Cpu.IsHybrid ? "Intel hybrid CPU" : "Dual-CCD CPU",
                lines, new[] { new RecLink("Process Lasso (Bitsum)", "https://bitsum.com/") }, Action: "lasso"));
        }

        if (p.Gpus.Any(g => g.Vendor == GpuVendor.Amd && !g.IsIntegrated))
        {
            list.Add(new Recommendation("slimmer", "Radeon Software Slimmer", "AMD graphics card, at the end",
                new[]
                {
                    "Trims Radeon Software: optional components, its auto-start entries and its update tasks. Free and open source (GSDragoon), not made or endorsed by AMD.",
                    "Check its GitHub page for the .NET runtime it needs.",
                    "If undervolting or overclocking in Adrenalin, keep the components behind Performance > Tuning, and keep the StartCN task: it loads the tuning profile at logon.",
                    "A driver update reinstalls what was removed, so run it again after each driver update.",
                },
                new[] { new RecLink("Radeon Software Slimmer on GitHub", "https://github.com/GSDragoon/RadeonSoftwareSlimmer") }));
        }

        if (p.Gpus.Any(g => g.Vendor == GpuVendor.Nvidia))
        {
            list.Add(new Recommendation("nvcleanstall", "NVCleanstall", "NVIDIA graphics card, at the end",
                new[]
                {
                    "Installs the NVIDIA driver without the extras: driver only on a desktop (plus the Optimus components on a laptop), telemetry off.",
                    "Free, by TechPowerUp, not made or endorsed by NVIDIA.",
                    "Each driver update through NVIDIA's own installer brings the extras back; install updates through NVCleanstall instead.",
                },
                new[] { new RecLink("NVCleanstall (TechPowerUp)", "https://www.techpowerup.com/nvcleanstall/") }));
        }

        list.Add(new Recommendation("latencymon", "Measure with LatencyMon", "Before and after every change",
            new[]
            {
                "Close HWiNFO, Afterburner/RTSS, RGB apps and Process Explorer first. They poll hardware and add their own spikes.",
                "Run it 10-15 minutes idle, then once during a game. A 2-minute run lies: some spikes only come once a minute or less.",
                "Read three things. Main tab: highest interrupt-to-process latency, the best \"snappiness\" number. Drivers tab: sort by highest execution to find the slowest driver. CPUs tab: lots of DPCs on CPU 0 means interrupts aren't spread out.",
                "To compare two sets of cores (such as the two CCDs), set LatencyMon's own CPU affinity to one, run, then the other.",
                "Known blind spots: it can miss MSI/MSI-X interrupts, so its highest ISR undersells GPUs. It misreads Ryzen clock speeds. Its own driver (rspLLL64.sys) makes the steady DPC stream on CPU 0. It clashes with any other kernel trace.",
                "Windows runs maintenance tasks after about 10 minutes without input. A long hands-off run can catch that burst; the Tasks screen marks which tasks run at idle.",
                "Scale: under about 100 µs interrupt-to-process is beyond what anyone feels in a game. LatencyMon treats DPCs under about 2,000 µs as fine for real-time audio; dropouts usually start around 1,000 µs.",
            },
            new[] { new RecLink("LatencyMon (Resplendence)", "https://www.resplendence.com/latencymon") }));

        list.Add(new Recommendation("bios", "BIOS checklist (read only)", "Always",
            new[]
            {
                "The app never changes BIOS settings. Names differ per board; look for these.",
                "Zero WHEA errors comes first: an unstable overclock or undervolt costs an interrupt and a DPC per corrected error. The Home screen shows the 14-day count.",
                "RAM profile on (EXPO / XMP). DRAM Power Down Enable off.",
                "Integrated graphics off if the graphics card drives every screen.",
                "On a Wi-Fi + Bluetooth card where only Bluetooth is used, the BT Only option if the board has one.",
                "X3D Gaming Mode off: it switches off the CCD the interrupts should live on.",
                "Global C-states on Auto: the power plan caps idle depth, the BIOS doesn't need to.",
                "PCIe link generation on Auto, unless WHEA event 17 names the graphics card's root port.",
            },
            Array.Empty<RecLink>()));

        return list;
    }
}
