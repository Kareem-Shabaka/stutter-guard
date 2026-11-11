using System.Globalization;
using DpcTweaker.Core.Detection;
using DpcTweaker.Core.Native;

namespace DpcTweaker.Core.Interrupts;

/// <summary>Why a device gets a core, in priority order (spec step 4).</summary>
public enum DeviceRole { Gpu, Input, Audio, Network, OtherUsb, Rest }

/// <summary>A physical core that may take devices, best first.</summary>
public sealed record RankedCore(int Cpu, int CoreId, int Rank, bool PerformanceCore, string Detail);

/// <summary>
/// The RSS keywords that make NDIS spread receive queues over exactly the cores an adapter is
/// pinned to. *MaxRssProcessors counts <em>logical</em> processors and *RssMaxProcNumber is an
/// inclusive logical-processor number, so on an SMT CPU both must be scaled by the core's thread
/// count: "2 processors starting at 20" covers LPs 20 and 21, which are the two threads of ONE
/// core, not two cores.
/// </summary>
public sealed record RssPlan(int BaseProcNumber, int MaxProcNumber, int NumProcessors, int ThreadsPerCore)
{
    public string Describe() =>
        $"{NumProcessors} logical processor(s), LPs {BaseProcNumber}-{MaxProcNumber} ({ThreadsPerCore} thread(s) per core)";
}

public sealed class Assignment
{
    public required PciDevice Device { get; init; }
    public required DeviceRole Role { get; init; }
    public required int[] Cpus { get; init; }
    public bool Shared { get; init; }
    /// <summary>Already pinned here before, in line with the rules, so the plan keeps it.</summary>
    public bool Kept { get; init; }
    /// <summary>For a network adapter: the RSS keywords that cover these cores exactly.</summary>
    public RssPlan? Rss { get; init; }
    public required string Reason { get; init; }

    public ulong Mask => Cpus.Aggregate(0UL, (m, c) => m | (1UL << c));
    public string CpuText => (Cpus.Length == 1 ? "CPU " : "CPUs ") + string.Join(", ", Cpus);
}

public sealed class AffinityPlan
{
    public bool Applicable { get; init; } = true;
    public string? NotApplicableReason { get; init; }
    public List<int> ReservedCpus { get; init; } = new();
    public string ReservedReason { get; init; } = "";
    public List<RankedCore> Candidates { get; init; } = new();
    public List<Assignment> Assignments { get; init; } = new();
    public List<string> Notes { get; init; } = new();
    public string RankingSource { get; init; } = "";

    public Assignment? For(PciDevice d) =>
        Assignments.FirstOrDefault(a => string.Equals(a.Device.InstanceId, d.InstanceId, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Game-core-aware interrupt placement. Pure logic on detected data, so it is tested
/// without hardware: reserve the cores games need, rank the rest (benchmark when there
/// is one, Windows' preferred-core ranking otherwise), then hand out cores best-first in
/// order of how latency-critical each device is.
/// </summary>
public static class AffinityPlanner
{
    private sealed class PhysCore
    {
        public int Id;
        public int First;
        public List<int> Threads = new();
        public int Eff;
        public int L3;
        public int Sched;
    }

    /// <param name="keepValidPins">Keep a device on the core it's already pinned to when that core is allowed
    /// (not kept free, a first thread, not taken by a more important device).</param>
    public static AffinityPlan Plan(CpuTopology topo, IReadOnlyList<CpuSetEntry> sets, BenchResult? bench,
        IReadOnlyList<PciDevice> devices, bool keepValidPins = false)
    {
        if (topo.MultipleProcessorGroups || topo.LogicalCount > 64)
            return new AffinityPlan { Applicable = false, NotApplicableReason = "More than 64 logical CPUs (several processor groups): interrupt placement stays read-only in this version." };

        // Physical cores, group 0.
        var cores = topo.Cpus.GroupBy(c => c.CoreId).Select(g => new PhysCore
        {
            Id = g.Key,
            First = g.Min(c => c.Index),
            Threads = g.Select(c => c.Index).OrderBy(x => x).ToList(),
            Eff = g.First().EfficiencyClass,
            L3 = g.First().L3Id,
            Sched = g.Max(c => sets.FirstOrDefault(s => s.Group == 0 && s.LogicalIndex == c.Index)?.SchedulingClass ?? 0),
        }).OrderBy(c => c.First).ToList();

        if (cores.Count < 2)
            return new AffinityPlan { Applicable = false, NotApplicableReason = "Single-core CPU: there is no other core to move interrupts to." };

        bool schedVaries = cores.Select(c => c.Sched).Distinct().Count() > 1;
        int maxEff = cores.Max(c => c.Eff);
        bool hybrid = topo.IsHybrid;

        var core0 = cores.First(c => c.Threads.Contains(0));

        // "Top" for reservations: Windows' preferred-core ranking, then the benchmark's speed (only when
        // speed differences exceed the 5% measurement noise floor across non-0 cores), then the lowest CPU number.
        double SpeedOf(PhysCore c)
        {
            if (bench?.For(c.First) is not { } b || b.SpeedMs <= 0) return double.MaxValue;
            return b.SpeedMs * (1 - Math.Clamp((b.IsrPercent + b.DpcPercent) / 100.0, 0, 0.5));
        }
        bool speedVaries = false;
        if (bench is not null)
        {
            var speeds = cores.Where(c => c.Id != core0.Id).Select(SpeedOf).Where(s => s < double.MaxValue).ToList();
            if (speeds.Count >= 2) speedVaries = speeds.Max() - speeds.Min() > speeds.Average() * 0.05;
        }
        var byTop = cores.OrderByDescending(c => c.Sched)
            .ThenBy(c => speedVaries ? SpeedOf(c) : 0)
            .ThenBy(c => c.First).ToList();
        bool haveTopInfo = schedVaries || speedVaries;

        var reserved = new HashSet<int> { core0.Id };
        string reason;

        if (topo.IsMultiCcd && !hybrid)
        {
            int gameL3;
            if (topo.IsDualCcdX3D)
            {
                gameL3 = topo.VCacheGroup!.Id;
                reason = $"CPU 0/1 and the V-Cache CCD (CPUs {topo.VCacheGroup.CpuRange()}): that's where games run on an X3D.";
            }
            else
            {
                gameL3 = haveTopInfo ? byTop[0].L3 : core0.L3;
                var g = topo.L3Groups.FirstOrDefault(x => x.Id == gameL3);
                reason = haveTopInfo
                    ? $"CPU 0/1 and the CCD with the top-ranked cores (CPUs {g?.CpuRange()}): games run there."
                    : $"CPU 0/1 and the first CCD (CPUs {g?.CpuRange()}): no core ranking from Windows, so the CCD games usually land on.";
            }
            foreach (var c in cores.Where(c => c.L3 == gameL3)) reserved.Add(c.Id);
        }
        else
        {
            var pool = cores.Where(c => c.Id != core0.Id && (!hybrid || c.Eff == maxEff)).ToList();
            var top = (haveTopInfo
                    ? pool.OrderByDescending(c => c.Sched).ThenBy(c => speedVaries ? SpeedOf(c) : 0).ThenBy(c => c.First)
                    : pool.OrderBy(c => c.First))
                .Take(2).ToList();
            foreach (var c in top) reserved.Add(c.Id);
            string which = hybrid ? "P-cores" : "cores";
            string list = string.Join(" and ", top.Select(c => "CPU " + c.First));
            reason = haveTopInfo
                ? $"CPU 0/1 and the 2 top-ranked {which} ({list}), for the game's main and render threads."
                : $"CPU 0/1 and the next 2 {which} ({list}): Windows gives no core ranking here, so the ones games usually take first.";
        }

        var notes = new List<string>();
        var free = cores.Where(c => !reserved.Contains(c.Id)).ToList();
        if (free.Count < 2 && reserved.Count > 1)
        {
            reserved = new HashSet<int> { core0.Id };
            free = cores.Where(c => c.Id != core0.Id).ToList();
            reason = "Only CPU 0/1: this CPU has too few cores to keep game cores free as well.";
            notes.Add("Few cores: only CPU 0 and its sibling are kept free of devices.");
        }

        // Rank the free cores, best first.
        string rankingSource;
        List<PhysCore> ranked;
        if (bench is not null && free.All(c => bench.For(c.First) is not null))
        {
            ranked = RankByBenchmark(free.Select(c => (c.First, c.Sched)).ToList(), bench, schedVaries)
                .Select(cpu => free.First(c => c.First == cpu)).ToList();
            rankingSource = "Core benchmark, " + bench.When.ToString("g", CultureInfo.CurrentCulture) +
                            ": wake-up median 30%, workload speed 30% (minus time stolen by interrupts already there)" +
                            (schedVaries ? ", Windows' preferred-core rank 40%" : "") +
                            ". Differences smaller than the measurement noise count as ties. The wake-up tail is shown but not scored: it moved from core to core between runs.";
        }
        else
        {
            ranked = (schedVaries ? free.OrderByDescending(c => c.Sched).ThenBy(c => c.First) : free.OrderBy(c => c.First)).ToList();
            rankingSource = schedVaries
                ? "Estimated from Windows' preferred-core ranking. Run the core benchmark for a measured order."
                : "Estimated (lowest CPU number first). Run the core benchmark for a measured order.";
        }

        var candidates = ranked.Select((c, i) => new RankedCore(c.First, c.Id, i + 1, !hybrid || c.Eff == maxEff,
            Detail(c, bench, hybrid, maxEff))).ToList();

        var order = Order(devices, notes, candidates.Count);
        var assignments = Assign(order, candidates, cores, hybrid, keepValidPins);
        if (assignments.Any(a => a.Kept))
            notes.Add($"{assignments.Count(a => a.Kept)} device(s) were already pinned to an allowed core and stay there (\"Keep pins that follow the rules\").");

        if (assignments.Any(a => a.Shared))
            notes.Add("More devices than free cores: the lowest-traffic devices share a core.");

        return new AffinityPlan
        {
            ReservedCpus = cores.Where(c => reserved.Contains(c.Id)).SelectMany(c => c.Threads).OrderBy(x => x).ToList(),
            ReservedReason = reason,
            Candidates = candidates,
            Assignments = assignments,
            Notes = notes,
            RankingSource = rankingSource,
        };
    }

    /// <summary>
    /// Benchmark order, best first. The idle interrupt load is NOT scored: on a PC that already has
    /// pins it mostly measures the devices that are about to move. Each metric is scaled by at least
    /// its noise floor, so tiny differences can't decide the order on their own; ties fall back to
    /// Windows' preferred-core rank, then the lowest number.
    /// </summary>
    public static List<int> RankByBenchmark(List<(int Cpu, int Sched)> cores, BenchResult bench, bool useSched)
    {
        var rows = cores.Select(c => (c.Cpu, c.Sched, B: bench.For(c.Cpu)!)).ToList();
        double Norm(double v, IEnumerable<double> all, double floor)
        {
            var list = all.ToList();
            double lo = list.Min(), hi = list.Max();
            return (v - lo) / Math.Max(hi - lo, floor);
        }
        // The workload runs slower on a core that's busy with interrupts and DPCs right now (on a pinned PC:
        // the devices about to move). Take that stolen time back out, so the score is about the core itself.
        double Speed(CoreScore b) => b.SpeedMs * (1 - Math.Clamp((b.IsrPercent + b.DpcPercent) / 100.0, 0, 0.5));
        double meanSpeed = rows.Average(r => Speed(r.B));
        int maxSched = rows.Max(r => r.Sched);
        double Score((int Cpu, int Sched, CoreScore B) r)
        {
            double s = 0.30 * Norm(r.B.WakeMedianUs, rows.Select(x => x.B.WakeMedianUs), 2.0)
                     + 0.30 * Norm(Speed(r.B), rows.Select(x => Speed(x.B)), meanSpeed * 0.02);
            if (useSched) s += 0.40 * Norm(maxSched - r.Sched, rows.Select(x => (double)(maxSched - x.Sched)), 2.0);
            return s;
        }
        return rows.OrderBy(r => Math.Round(Score(r), 3)).ThenByDescending(r => r.Sched).ThenBy(r => r.Cpu).Select(r => r.Cpu).ToList();
    }

    private static string Detail(PhysCore c, BenchResult? bench, bool hybrid, int maxEff)
    {
        var parts = new List<string>();
        if (hybrid) parts.Add(c.Eff == maxEff ? "P-core" : "E-core");
        if (bench?.For(c.First) is { } b)
            parts.Add($"wake median {b.WakeMedianUs:0.0} µs, p95 {b.WakeP95Us:0.0} µs, speed {b.SpeedMs:0.0} ms, Windows rank {c.Sched}");
        else if (c.Sched > 0)
            parts.Add($"Windows rank {c.Sched}");
        return string.Join(", ", parts);
    }

    private sealed record Want(PciDevice Device, DeviceRole Role, int Cores, string Reason);

    /// <summary>Spec step 4: the order devices get cores in.</summary>
    private static List<Want> Order(IReadOnlyList<PciDevice> devices, List<string> notes, int coreBudget)
    {
        var eligible = devices.Where(d => d.Kind is not (PciKind.Never or PciKind.Storage)).ToList();
        foreach (var d in eligible.Where(d => !d.Started || d.Problem != 0))
            notes.Add($"{d.Name}: {(d.Problem != 0 ? "has problem code " + d.Problem : "not running (disabled or no driver)")}, left alone.");
        eligible = eligible.Where(d => d.Started && d.Problem == 0).ToList();

        var want = new List<Want>();
        var taken = new HashSet<PciDevice>();
        string? escalatedHub = null;   // input controller that asked for a second core
        void Add(PciDevice d, DeviceRole r, string why, int n = 1)
        {
            if (!taken.Add(d)) return;
            want.Add(new Want(d, r, n, why));
        }

        // 1. Primary GPU(s): discrete GPUs first (or the integrated GPU when there is no discrete GPU).
        var gpus = eligible.Where(d => d.Kind == PciKind.Gpu).ToList();
        var discrete = gpus.Where(g => !g.IsIntegratedGpu).OrderByDescending(g => g.Attached.Monitors).ToList();
        foreach (var g in discrete)
            Add(g, DeviceRole.Gpu, "The graphics card: its interrupts arrive every frame and are the most latency-critical on the PC.");
        if (discrete.Count == 0)
            foreach (var g in gpus.Where(g => g.IsIntegratedGpu))
                Add(g, DeviceRole.Gpu, "The GPU: its interrupts arrive every frame and are the most latency-critical on the PC.");

        // Where the default playback device hangs is decided before the input controller's cores are
        // handed out: a USB DAC can sit on the same root hub as the mouse.
        var defaultAudio = eligible.FirstOrDefault(d => d.Attached.HasDefaultAudio);

        // 2. The USB controller with the mouse (then keyboard / game controller).
        // Placed before any secondary screen-driving iGPU so on 4-core Optimus laptops the mouse never shares with audio/network.
        var usb = eligible.Where(d => d.Kind == PciKind.UsbController).ToList();
        var input = usb.Where(u => u.Attached.Mice > 0).OrderByDescending(u => u.Attached.Mice).ThenByDescending(u => u.Attached.Keyboards).FirstOrDefault()
                    ?? usb.Where(u => u.Attached.Keyboards > 0 || u.Attached.GameControllers > 0)
                          .OrderByDescending(u => u.Attached.Keyboards + u.Attached.GameControllers).FirstOrDefault();
        if (input is not null)
        {
            bool carriesAudio = defaultAudio is not null &&
                string.Equals(defaultAudio.InstanceId, input.InstanceId, StringComparison.OrdinalIgnoreCase);
            if (carriesAudio)
            {
                // One root hub raises one interrupt for all of its ports, so the mouse and the DAC
                // already share an ISR. Per-port affinity doesn't exist, so the controller gets a
                // second core and the plan says plainly that only moving the DAC separates them.
                Add(input, DeviceRole.Input,
                    $"Carries your input devices ({input.Attached.Describe()}) and your default playback device on the same root hub. " +
                    "A root hub raises one interrupt for every port on it, so both streams arrive through the same ISR.", 2);
                escalatedHub = input.InstanceId;
                notes.Add($"{input.Name} carries both your mouse and your default playback device, so their interrupts share one ISR and no affinity setting can separate them. " +
                          "A second core is worth having here (it gives the scheduler room and costs no bandwidth), but only from spare cores. " +
                          "For a real split, move the USB DAC to a port on a different xHCI controller; the plan lists the other controllers below.");
            }
            else
            {
                Add(input, DeviceRole.Input, $"Has your input devices ({input.Attached.Describe()}). A 1-8 kHz mouse is a steady interrupt stream of its own.");
            }
        }

        // 2b. Secondary integrated GPU that drives the laptop panel alongside a discrete GPU.
        if (discrete.Count > 0)
            foreach (var g in gpus.Where(g => g.IsIntegratedGpu && g.Attached.Monitors > 0))
                Add(g, DeviceRole.Gpu, "The integrated GPU drives a screen, so it's on the frame path too.");

        // 3. The active audio path.
        if (defaultAudio is not null)
        {
            if (taken.Contains(defaultAudio))
                notes.Add($"Your default playback device is on {defaultAudio.Name}, which is already placed above.");
            else
                Add(defaultAudio, DeviceRole.Audio, "Carries your default playback device. Audio crackles when its DPCs have to wait.");
        }
        else
        {
            var audio = eligible.FirstOrDefault(d => d.Kind == PciKind.Audio && d.Attached.AudioEndpoints + d.Attached.AudioDevices > 0)
                        ?? eligible.FirstOrDefault(d => d.Kind == PciKind.GpuAudio && d.Attached.AudioEndpoints > 0);
            if (audio is not null)
                Add(audio, DeviceRole.Audio, "An audio controller with active outputs (the default playback device couldn't be read).");
        }

        // 4. The network adapter in use: RSS queues + MSI-X on the same 2 cores.
        var nics = eligible.Where(d => d.Kind is PciKind.Ethernet or PciKind.Wifi).ToList();
        var inUse = nics.FirstOrDefault(n => n.Kind == PciKind.Ethernet && n.Connected == true)
                    ?? nics.FirstOrDefault(n => n.Kind == PciKind.Wifi && n.Connected == true)
                    ?? nics.FirstOrDefault(n => n.Kind == PciKind.Ethernet && n.Connected is null);
        if (inUse is not null)
            Add(inUse, DeviceRole.Network,
                inUse.RssCapable
                    ? "The network adapter in use. Its receive queues (RSS) and its interrupts go to the same 2 cores, so packets are handled where they arrive."
                    : "The network adapter in use.",
                inUse.RssCapable ? 2 : 1);

        // 5. Every other USB controller, busiest-looking first, empty ones too.
        static int Weight(PciDevice u) => 4 * u.Attached.Mice + 3 * (u.Attached.Keyboards + u.Attached.GameControllers) +
                                          2 * Math.Sign(u.Attached.AudioDevices) + 2 * Math.Sign(u.Attached.Bluetooth) +
                                          Math.Sign(u.Attached.Network + u.Attached.Storage + u.Attached.Cameras + u.Attached.UsbDevices);
        foreach (var u in usb.OrderByDescending(Weight).ThenBy(u => u.InstanceId, StringComparer.OrdinalIgnoreCase))
            Add(u, DeviceRole.OtherUsb,
                !u.Attached.HasDevices
                    ? "A USB controller with nothing plugged in. Pinned anyway: on the author's desktop that still lowered the worst case."
                    : $"Another USB controller ({u.Attached.Describe()}).");

        // 6. Everything else that can take a pin.
        static int RestOrder(PciDevice d) => d.Kind switch
        {
            PciKind.Audio => 0,
            PciKind.GpuAudio => 1,
            PciKind.Ethernet => 2,
            PciKind.Wifi => 3,
            PciKind.UsbHostInterface => 4,
            PciKind.Capture => 5,
            _ => 6,
        };
        foreach (var d in eligible.Where(d => !taken.Contains(d)).OrderBy(RestOrder).ThenBy(d => d.InstanceId, StringComparer.OrdinalIgnoreCase))
        {
            string why = d.Kind switch
            {
                PciKind.GpuAudio => "The graphics card's HDMI/DP audio. Low traffic unless your speakers are on the monitor.",
                PciKind.Audio => "An audio controller that isn't your default output. Low traffic.",
                PciKind.Ethernet or PciKind.Wifi => "A network adapter that isn't in use. Low traffic.",
                PciKind.UsbHostInterface => "Thunderbolt / USB4 host interface. Low traffic.",
                PciKind.Capture => "A capture or video device.",
                PciKind.Gpu => "A GPU that doesn't drive a screen.",
                _ => "Low traffic.",
            };
            Add(d, DeviceRole.Rest, why);
        }
        // A second core for the shared root hub is a want, not a right: if spending it would push
        // another device onto a shared core, the controller keeps one and the note says why. The
        // warning above still stands, and moving the DAC is the fix that costs nothing.
        int demand = want.Sum(w => w.Cores);
        if (escalatedHub is not null && demand > coreBudget)
        {
            int i = want.FindIndex(w => string.Equals(w.Device.InstanceId, escalatedHub, StringComparison.OrdinalIgnoreCase));
            if (i >= 0 && want[i].Cores > 1)
            {
                want[i] = want[i] with { Cores = 1 };
                notes.Add($"{want[i].Device.Name} keeps one core: {coreBudget} device core(s) are free and this plan needs {demand}. " +
                          "Move the USB DAC to another xHCI controller to separate the two interrupt streams instead, which costs nothing.");
            }
        }
        return want;
    }

    /// <summary>
    /// The RSS keywords for a network assignment: every logical processor of the target physical
    /// cores and nothing else. BaseProcNumber is the first LP, RssMaxProcNumber the last LP of the
    /// last core (inclusive), MaxRssProcessors the number of LPs in that span.
    /// </summary>
    private static RssPlan? RssFor(Want w, int[] cpus, List<PhysCore> cores)
    {
        if (w.Role != DeviceRole.Network || cpus.Length == 0) return null;
        int threads = cores.FirstOrDefault(c => c.Threads.Contains(cpus[0]))?.Threads.Count ?? 1;
        if (threads < 1) threads = 1;
        int last = cpus[^1];
        return new RssPlan(cpus[0], last + threads - 1, cpus.Length * threads, threads);
    }

    private static List<Assignment> Assign(List<Want> order, List<RankedCore> candidates, List<PhysCore> cores, bool hybrid, bool keepValidPins)
    {
        var free = candidates.ToList(); // best first
        var used = new Dictionary<int, List<DeviceRole>>();
        var usedDevs = new Dictionary<int, List<PciDevice>>();
        var result = new List<Assignment>();
        // Physical order of every core (reserved ones too): RSS uses BaseProcNumber..MaxProcNumber
        // across logical processors, so a 2-core RSS pair must be two consecutive physical cores
        // with no other core's logical threads sitting numerically between them.
        var physOrder = cores.OrderBy(c => c.First).Select(c => c.First).ToList();
        bool ConsecutiveRssPair(int cpuA, int cpuB)
        {
            if (cpuB <= cpuA) return false;
            if (physOrder.IndexOf(cpuB) != physOrder.IndexOf(cpuA) + 1) return false;
            int coreA = cores.First(c => c.First == cpuA).Id;
            return !cores.Any(c => c.Id != coreA && c.Threads.Any(t => t > cpuA && t < cpuB));
        }

        // Pins that already follow the rules stay where they are (most important device first wins a core).
        var kept = new Dictionary<Want, int[]>();
        if (keepValidPins)
        {
            var allowedFirst = candidates.Select(c => c.Cpu).ToHashSet();
            int? AllowedCoreFirst(int cpu)
            {
                if (allowedFirst.Contains(cpu)) return cpu;
                var pc = cores.FirstOrDefault(c => c.Threads.Contains(cpu));
                return pc is not null && allowedFirst.Contains(pc.First) ? pc.First : null;
            }
            var taken = new HashSet<int>();
            foreach (var w in order)
            {
                if (w.Device.ExistingMask is not ulong m || m == 0) continue;
                var cpus = Enumerable.Range(0, 64).Where(i => (m & (1UL << i)) != 0).ToArray();
                var physCpus = cpus.Select(AllowedCoreFirst).ToArray();
                bool ok = cpus.Length <= w.Cores && physCpus.All(p => p is not null) &&
                          physCpus.Select(p => p!.Value).Distinct().Count() == cpus.Length &&
                          !physCpus.Any(p => taken.Contains(p!.Value));
                if (ok && cpus.Length == 2) ok = ConsecutiveRssPair(cpus[0], cpus[1]);
                if (hybrid && ok && w.Role is DeviceRole.Gpu or DeviceRole.Input)
                    ok = physCpus.All(p => candidates.First(x => x.Cpu == p!.Value).PerformanceCore);
                if (!ok) continue;
                kept[w] = cpus;
                foreach (int? p in physCpus)
                {
                    taken.Add(p!.Value);
                    free.RemoveAll(x => x.Cpu == p.Value);
                }
            }
        }

        IEnumerable<RankedCore> Preferred(DeviceRole r)
        {
            if (!hybrid) return free;
            bool wantP = r is DeviceRole.Gpu or DeviceRole.Input;
            return free.Where(c => c.PerformanceCore == wantP).Concat(free.Where(c => c.PerformanceCore != wantP));
        }

        foreach (var w in order)
        {
            if (kept.TryGetValue(w, out var keptCpus))
            {
                foreach (int c in keptCpus)
                {
                    int coreFirst = cores.FirstOrDefault(pc => pc.Threads.Contains(c))?.First ?? c;
                    if (!used.TryGetValue(coreFirst, out var kl)) used[coreFirst] = kl = new List<DeviceRole>();
                    kl.Add(w.Role);
                    if (!usedDevs.TryGetValue(coreFirst, out var kd)) usedDevs[coreFirst] = kd = new List<PciDevice>();
                    kd.Add(w.Device);
                }
                result.Add(new Assignment { Device = w.Device, Role = w.Role, Cpus = keptCpus, Kept = true, Rss = RssFor(w, keptCpus, cores), Reason = w.Reason + " Already pinned here, kept." });
                continue;
            }

            int[]? cpus = null;
            if (w.Cores == 2)
            {
                // The best pair of free, physically adjacent cores (by the worse rank of the two).
                var pref = Preferred(w.Role).ToList();
                var pair = pref
                    .SelectMany(a => pref.Where(b => ConsecutiveRssPair(a.Cpu, b.Cpu))
                        .Select(b => (A: a, B: b)))
                    .Where(p => !hybrid || p.A.PerformanceCore == p.B.PerformanceCore)
                    .OrderBy(p => pref.IndexOf(p.A) + pref.IndexOf(p.B))
                    .ThenBy(p => p.A.Cpu)
                    .FirstOrDefault();
                if (pair.A is not null) cpus = new[] { pair.A.Cpu, pair.B.Cpu };
            }
            if (cpus is null && Preferred(w.Role).FirstOrDefault() is { } one) cpus = new[] { one.Cpu };

            bool shared = false;
            if (cpus is null)
            {
                // Out of free cores: share the core whose most important device matters least
                // (so never the primary GPU's or the mouse's core while another exists), then the emptiest, then the worst-ranked.
                // A secondary integrated GPU (when a discrete GPU is also present) is treated as more shareable than Input (score 2).
                bool hasDiscreteGpu = order.Any(o => o.Role == DeviceRole.Gpu && !o.Device.IsIntegratedGpu);
                int SharePriority(int cpu) => used[cpu].Select((r, idx) =>
                    r == DeviceRole.Gpu && hasDiscreteGpu && usedDevs[cpu][idx].IsIntegratedGpu ? 2 : (int)r).Min();
                shared = true;
                int pick = candidates
                    .Where(c => used.ContainsKey(c.Cpu))
                    .OrderByDescending(c => SharePriority(c.Cpu))
                    .ThenBy(c => used[c.Cpu].Count)
                    .ThenByDescending(c => c.Rank)
                    .Select(c => c.Cpu)
                    .DefaultIfEmpty(candidates[^1].Cpu)
                    .First();
                cpus = new[] { pick };
            }

            foreach (int c in cpus)
            {
                free.RemoveAll(x => x.Cpu == c);
                if (!used.TryGetValue(c, out var l)) used[c] = l = new List<DeviceRole>();
                l.Add(w.Role);
                if (!usedDevs.TryGetValue(c, out var dl)) usedDevs[c] = dl = new List<PciDevice>();
                dl.Add(w.Device);
            }
            result.Add(new Assignment { Device = w.Device, Role = w.Role, Cpus = cpus, Shared = shared, Rss = RssFor(w, cpus, cores), Reason = w.Reason });
        }
        return result;
    }
}
