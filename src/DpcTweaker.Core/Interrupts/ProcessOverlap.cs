using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DpcTweaker.Core.Interrupts;

/// <summary>
/// One process whose processor affinity is restricted to a mask that names an interrupt core.
/// </summary>
/// <param name="Mask">The affinity mask Windows reports for the process, restricted (not every LP).</param>
/// <param name="OverlapCpus">The interrupt logical processors that mask includes.</param>
public sealed record OverlappingProcess(int Pid, string Name, ulong Mask, IReadOnlyList<int> OverlapCpus);

/// <summary>
/// Finds processes that are pinned onto the cores the interrupt plan reserves.
/// <para>
/// The scheduler has no interrupt-load heuristic: it does not know a core is servicing a device, so it
/// will happily run ordinary work there. Measured on the author's machine, a ready thread on an
/// interrupt core waited a median of 782 us against 8.9 us on a reserved one - roughly ninety times -
/// so a process restricted to those cores is competing with every interrupt the plan tried to isolate.
/// </para>
/// <para>
/// A <b>full</b> mask is not evidence of anything: it intersects every interrupt processor trivially
/// and restricts the process to nowhere. Only a restricted mask that happens to include an interrupt
/// core is a finding, which is why <see cref="IsRestricted"/> exists rather than an intersection test.
/// </para>
/// <para>
/// Read-only by design. Moving another process's affinity is runtime state that vanishes when the
/// process exits or the machine restarts, so there is nothing here a journal could make reversible;
/// the honest surface is to name the processes and who put them there.
/// </para>
/// </summary>
public static class ProcessOverlap
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessAffinityMask(IntPtr process, out UIntPtr processMask, out UIntPtr systemMask);

    /// <summary>Every logical processor of the group: the mask a process with no restriction reports.</summary>
    public static ulong FullMask(int logicalCount)
    {
        int n = Math.Clamp(logicalCount, 1, 64);
        return n >= 64 ? ulong.MaxValue : (1UL << n) - 1;
    }

    /// <summary>True when the mask names at least one processor but not all of them.</summary>
    public static bool IsRestricted(ulong mask, int logicalCount) =>
        mask != 0 && mask != FullMask(logicalCount);

    /// <summary>
    /// The pure test, so every branch is reachable without a process list.
    /// </summary>
    public static IReadOnlyList<OverlappingProcess> Find(
        IEnumerable<(int Pid, string Name, ulong Mask)> processes,
        IReadOnlyCollection<int> interruptCpus,
        int logicalCount)
    {
        if (interruptCpus is null || interruptCpus.Count == 0) return Array.Empty<OverlappingProcess>();

        var wanted = interruptCpus.Where(c => c >= 0 && c < 64).Distinct().OrderBy(c => c).ToArray();
        if (wanted.Length == 0) return Array.Empty<OverlappingProcess>();

        var found = new List<OverlappingProcess>();
        foreach (var (pid, name, mask) in processes)
        {
            if (!IsRestricted(mask, logicalCount)) continue;

            var hit = wanted.Where(c => (mask & (1UL << c)) != 0).ToArray();
            if (hit.Length == 0) continue;

            found.Add(new OverlappingProcess(pid, string.IsNullOrWhiteSpace(name) ? $"PID {pid}" : name, mask, hit));
        }

        return found.OrderBy(o => o.Pid).ToList();
    }

    /// <summary>
    /// The logical processors the devices' <b>current</b> interrupt pins name, exactly as pinned.
    /// <para>
    /// Not the plan's <c>DeviceCpus</c>: that is where devices would go, and on a tuned machine the two
    /// differ. Using the plan reported 104 overlaps on the author's own machine, whose live pins sit on
    /// the even processors while its Process Lasso catch-all sits on the odd ones.
    /// </para>
    /// <para>
    /// Deliberately <b>not</b> expanded to SMT siblings, although a device's DPCs may run on any thread
    /// of its core. The project measured sibling occupancy and rejected whole-core isolation on the
    /// result (P8: sibling-shared p99 19 us against 465 us isolated), so a process on a sibling is not a
    /// finding. Expanding here reported the same 104 processes a second time for a cost that was
    /// measured not to exist.
    /// </para>
    /// </summary>
    public static IReadOnlyList<int> LiveCpus(IEnumerable<DpcTweaker.Core.Detection.PciDevice> devices)
    {
        var cpus = new HashSet<int>();
        foreach (var d in devices)
        {
            if (d.ExistingMask is not ulong mask || mask == 0) continue;
            for (int c = 0; c < 64; c++)
                if ((mask & (1UL << c)) != 0) cpus.Add(c);
        }
        return cpus.OrderBy(c => c).ToList();
    }
    /// <summary>
    /// Reads the live process list. <paramref name="unreadable"/> counts the processes whose affinity
    /// could not be read - protected and other-user processes answer ACCESS_DENIED without elevation,
    /// and a silent zero would look like a clean machine.
    /// </summary>
    public static IReadOnlyList<OverlappingProcess> Sample(
        IReadOnlyCollection<int> interruptCpus, int logicalCount, out int unreadable)
    {
        unreadable = 0;
        var rows = new List<(int, string, ulong)>();

        // GetProcessAffinityMask answers for one processor group, so masks are only meaningful up to
        // 64 logical processors. Beyond that the count is reported instead of a false negative.
        if (logicalCount > 64)
        {
            unreadable = -1;
            return Array.Empty<OverlappingProcess>();
        }

        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (!GetProcessAffinityMask(p.Handle, out UIntPtr mask, out _))
                {
                    unreadable++;
                    continue;
                }
                rows.Add((p.Id, p.ProcessName, (ulong)mask));
            }
            catch
            {
                unreadable++;
            }
            finally
            {
                p.Dispose();
            }
        }

        return Find(rows, interruptCpus, logicalCount);
    }
}