using System.Diagnostics;
using DpcTweaker.Core.Detection;
using DpcTweaker.Core.Infra;
using DpcTweaker.Core.Native;
using DpcTweaker.Core.Tweaks;

namespace DpcTweaker.Core.Interrupts;

public sealed record CheckLine(string Device, string Cpus, string Role, double IdlePerSec, double LoadPerSec, double DpcDeltaPerSec, string Verdict, bool Ok);

public sealed class CheckResult
{
    public DateTime When { get; init; }
    public List<CheckLine> Devices { get; init; } = new();
    public double ReservedDeltaPerSec { get; init; }
    public double ReservedBusiestDeltaPerSec { get; init; }
    public int ReservedBusiestCpu { get; init; }
    public List<string> Notes { get; init; } = new();
}

/// <summary>
/// Spec "check, keep, re-check": after the restart each busy device sits alone on its core,
/// so the per-CPU interrupt and DPC counters show where each device's interrupts really land.
/// Idle first, then light load: a silent sound loop on the default output, a download,
/// the user moving the mouse, and the app's own animation for the GPU.
/// </summary>
public static class InterruptCheck
{
    public const string DownloadUrl = "https://speed.cloudflare.com/__down?bytes=200000000";

    /// <param name="pendingRestart">True for an assignment applied since the last boot (written, not yet in effect).</param>
    /// <summary>
    /// Every logical processor of the physical cores an assignment owns. An assignment stores one
    /// ("first") thread per core, but the device's DPCs land on any thread of that core, and RSS
    /// spreads over the whole span: summing only the first threads reports "hardly any network
    /// interrupts here" while the queues are in fact running on the siblings.
    /// </summary>
    public static int[] CpusFor(Assignment a, CpuTopology? topo)
    {
        if (topo is null || topo.Cpus.Count == 0) return a.Cpus;
        var coreIds = a.Cpus
            .Select(c => topo.Cpus.FirstOrDefault(x => x.Index == c)?.CoreId)
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .ToHashSet();
        if (coreIds.Count == 0) return a.Cpus;
        return topo.Cpus.Where(c => coreIds.Contains(c.CoreId)).Select(c => c.Index).Distinct().OrderBy(x => x).ToArray();
    }

    public static CheckResult Run(AffinityPlan plan, int logicalCount, bool download, int idleSeconds, int loadSeconds,
        Func<Assignment, bool> pendingRestart, Action<string, double> progress, CancellationToken ct, CpuTopology? topo = null)
    {
        int n = Math.Min(64, logicalCount);
        var notes = new List<string>();
        double total = idleSeconds + loadSeconds;

        progress($"Idle baseline ({idleSeconds} s): hands off the mouse and keyboard...", 0);
        var a = SysInfo.Counters(n);
        var sw = Stopwatch.StartNew();
        for (int s = 0; s < idleSeconds; s++) { ct.ThrowIfCancellationRequested(); Thread.Sleep(1000); progress($"Idle baseline: {idleSeconds - s - 1} s left, hands off...", (s + 1) / total); }
        var b = SysInfo.Counters(n);
        var idle = CpuRates.Between(a, b, sw.Elapsed.TotalSeconds);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task? dl = null;
        long downloaded = 0;
        try
        {
            try { Audio.StartSilence(); } catch (Exception ex) { notes.Add("Couldn't play the silent sound: " + ex.Message); }
            if (download)
            {
                dl = Task.Run(async () =>
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(loadSeconds + 30) };
                    using var resp = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, stop.Token);
                    resp.EnsureSuccessStatusCode();
                    await using var st = await resp.Content.ReadAsStreamAsync(stop.Token);
                    var buf = new byte[1 << 16];
                    int r;
                    while ((r = await st.ReadAsync(buf, stop.Token)) > 0) Interlocked.Add(ref downloaded, r);
                }, stop.Token);
            }

            progress($"Under load ({loadSeconds} s): move your mouse in circles now...", idleSeconds / total);
            var c = SysInfo.Counters(n);
            sw.Restart();
            for (int s = 0; s < loadSeconds; s++) { ct.ThrowIfCancellationRequested(); Thread.Sleep(1000); progress($"Under load: keep moving the mouse, {loadSeconds - s - 1} s left...", (idleSeconds + s + 1) / total); }
            var d = SysInfo.Counters(n);
            var load = CpuRates.Between(c, d, sw.Elapsed.TotalSeconds);

            stop.Cancel();
            try { dl?.Wait(3000); }
            catch (AggregateException ex) when (ex.InnerException is not OperationCanceledException and not TaskCanceledException)
            {
                notes.Add("The test download failed, so the network part shows idle traffic only: " + ex.InnerException?.Message);
            }
            catch (AggregateException) { }
            if (download && downloaded > 0) notes.Add($"Downloaded {downloaded / (1024 * 1024)} MB during the load phase.");

            var lines = new List<CheckLine>();
            foreach (var asg in plan.Assignments)
            {
                // All threads of the assigned cores, not just the first ones: see CpusFor.
                var span = CpusFor(asg, topo);
                double iIdle = span.Where(x => x < idle.Length).Sum(x => idle[x].InterruptsPerSec);
                double iLoad = span.Where(x => x < load.Length).Sum(x => load[x].InterruptsPerSec);
                double dpcDelta = span.Where(x => x < load.Length).Sum(x => load[x].DpcsPerSec - idle[x].DpcsPerSec);
                var (policy, mask) = AffinityTweak.ReadLive(asg.Device);
                bool pinned = policy == 4 && mask == asg.Mask;
                double delta = iLoad - iIdle;
                (string verdict, bool ok) = !pinned
                    ? ("Not pinned yet: apply the plan and restart first.", false)
                    : pendingRestart(asg)
                        ? ("Applied, but Windows hasn't restarted since: restart, then run the check again.", false)
                    : asg.Role switch
                    {
                        DeviceRole.Gpu => delta > 30 || iLoad > 300 ? ("GPU interrupts land here.", true) : ("Little GPU activity seen here. If games stutter, the driver may ignore the pin: compare in LatencyMon.", false),
                        DeviceRole.Input => delta > 100 ? ("Mouse interrupts land here.", true) : ("No mouse activity seen: was the mouse moving during the load phase?", false),
                        DeviceRole.Audio => delta > 10 || iLoad > 100 ? ("Audio interrupts land here.", true) : ("Little audio activity seen here.", false),
                        DeviceRole.Network when download && downloaded > 0 => delta > 200
                            ? ("Network interrupts land here.", true)
                            : ("Hardly any network interrupts here during the download: the driver may steer its own interrupts elsewhere (see the busiest reserved CPU below).", false),
                        _ => (delta > 50 ? "Active during the check." : "Quiet (expected for a low-traffic device).", true),
                    };
                string cpusText = span.Length > asg.Cpus.Length ? asg.CpuText + " (all threads)" : asg.CpuText;
                lines.Add(new CheckLine(asg.Device.Name, cpusText, AffinityTweak.RoleText(asg.Role), Math.Round(iIdle), Math.Round(iLoad), Math.Round(dpcDelta), verdict, ok));
            }

            var reserved = plan.ReservedCpus.Where(x => x < load.Length).ToList();
            double resDelta = reserved.Sum(x => Math.Max(0, load[x].InterruptsPerSec - idle[x].InterruptsPerSec));
            int busiest = reserved.Count == 0 ? -1 : reserved.MaxBy(x => load[x].InterruptsPerSec - idle[x].InterruptsPerSec);
            double busiestDelta = busiest < 0 ? 0 : load[busiest].InterruptsPerSec - idle[busiest].InterruptsPerSec;
            notes.Add("Reserved CPUs always show some interrupts: Windows' own timer and inter-processor interrupts, plus this app's threads. A device landing there would show as hundreds to thousands per second on one CPU.");

            var result = new CheckResult
            {
                When = DateTime.Now,
                Devices = lines,
                ReservedDeltaPerSec = Math.Round(resDelta),
                ReservedBusiestCpu = busiest,
                ReservedBusiestDeltaPerSec = Math.Round(busiestDelta),
                Notes = notes,
            };
            Log.Info("Interrupt check: " + string.Join("; ", lines.Select(l => $"{l.Device} {l.Cpus}: {l.IdlePerSec}->{l.LoadPerSec}/s {(l.Ok ? "ok" : "CHECK")}")));
            return result;
        }
        finally
        {
            stop.Cancel();
            try { Audio.StopSilence(); } catch { }
        }
    }
}
