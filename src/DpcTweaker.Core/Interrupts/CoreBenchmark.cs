using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using DpcTweaker.Core.Detection;
using DpcTweaker.Core.Infra;
using DpcTweaker.Core.Native;

namespace DpcTweaker.Core.Interrupts;

/// <summary>One physical core's benchmark result (measured on its first thread).</summary>
public sealed class CoreScore
{
    public int Cpu { get; set; }
    /// <summary>Fixed single-thread workload, best of three, in milliseconds (lower = faster core).</summary>
    public double SpeedMs { get; set; }
    /// <summary>How late a 1 ms high-resolution timer wake-up was, in microseconds.</summary>
    public double WakeMedianUs { get; set; }
    public double WakeP90Us { get; set; }
    public double WakeP95Us { get; set; }
    public double WakeP99Us { get; set; }
    public double WakeP999Us { get; set; }
    /// <summary>Share of wake-ups more than 100 µs late, in percent.</summary>
    public double LateOver100Pct { get; set; }
    public double WakeMaxUs { get; set; }
    /// <summary>Load already there at idle.</summary>
    public double InterruptsPerSec { get; set; }
    public double DpcsPerSec { get; set; }
    public double IsrPercent { get; set; }
    public double DpcPercent { get; set; }
    public int SchedulingClass { get; set; }
}

public sealed class BenchResult
{
    public DateTime When { get; set; }
    public int SamplesPerCore { get; set; }
    public int IdleSeconds { get; set; }
    public bool HighResolutionTimer { get; set; }
    public List<CoreScore> Cores { get; set; } = new();

    private const string StateKey = "irq.bench";

    public static BenchResult? Load(Journal j)
    {
        string? s = j.GetState(StateKey);
        if (s is null) return null;
        try { return JsonSerializer.Deserialize<BenchResult>(s); }
        catch { return null; }
    }

    public void Save(Journal j)
    {
        j.SetState(StateKey, JsonSerializer.Serialize(this));
        j.Save();
    }

    public static void Clear(Journal j)
    {
        j.SetState(StateKey, null);
        j.Save();
    }

    public CoreScore? For(int cpu) => Cores.FirstOrDefault(c => c.Cpu == cpu);

    public string Summary => $"measured {When.ToString("g", CultureInfo.CurrentCulture)}, {Cores.Count} cores, {SamplesPerCore} wake-ups each" +
                             (HighResolutionTimer ? "" : " (no high-resolution timer on this Windows: wake numbers are coarse)");
}

/// <summary>
/// The core benchmark (spec step 2): idle load per CPU, a fixed workload per core, then
/// 1 ms high-resolution timer wake-ups per core. Runs on one dedicated thread that is
/// pinned to each core in turn. Read-only: nothing on the PC changes.
/// </summary>
public static class CoreBenchmark
{
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")] private static extern UIntPtr SetThreadAffinityMask(IntPtr thread, UIntPtr mask);
    [DllImport("kernel32.dll")] private static extern bool SetThreadPriority(IntPtr thread, int priority);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period, IntPtr completion, IntPtr arg, bool resume);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint ms);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern bool GetProcessAffinityMask(IntPtr process, out UIntPtr processMask, out UIntPtr systemMask);
    [DllImport("kernel32.dll")] private static extern bool SetProcessAffinityMask(IntPtr process, UIntPtr mask);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessorNumber();

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState { public uint Version; public uint ControlMask; public uint StateMask; }

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PowerThrottlingState info, int size);

    [DllImport("kernel32.dll")]
    private static extern bool SetThreadInformation(IntPtr thread, int infoClass, ref PowerThrottlingState info, int size);

    /// <summary>
    /// Windows 11 throttles timers and clocks of processes it thinks are in the background (window hidden
    /// or occluded). Opt out for the benchmark, like the timer tool does, so every core is measured the same way.
    /// </summary>
    private static void OptOutOfPowerThrottling(IntPtr thread)
    {
        try
        {
            var p = new PowerThrottlingState { Version = 1, ControlMask = 0x1 | 0x4, StateMask = 0 }; // EXECUTION_SPEED | IGNORE_TIMER_RESOLUTION
            SetProcessInformation(GetCurrentProcess(), 4 /* ProcessPowerThrottling */, ref p, Marshal.SizeOf<PowerThrottlingState>());
            var t = new PowerThrottlingState { Version = 1, ControlMask = 0x1, StateMask = 0 };                // THREAD_POWER_THROTTLING_EXECUTION_SPEED
            SetThreadInformation(thread, 3 /* ThreadPowerThrottling */, ref t, Marshal.SizeOf<PowerThrottlingState>());
        }
        catch (Exception ex)
        {
            Log.Warn("Couldn't opt out of power throttling: " + ex.Message);
        }
    }

    private const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x2;
    private const uint TIMER_ALL_ACCESS = 0x1F0003;
    private const int THREAD_PRIORITY_NORMAL = 0;
    private const int THREAD_PRIORITY_TIME_CRITICAL = 15;

    private static ulong _sink;

    /// <summary>The first thread of every physical core in processor group 0.</summary>
    public static List<int> CoresToTest(CpuTopology topo) =>
        topo.Cpus.Where(c => !c.IsSecondThread && c.Index < 64).Select(c => c.Index).OrderBy(c => c).ToList();

    public static TimeSpan Estimate(CpuTopology topo, int samples, int idleSeconds) =>
        TimeSpan.FromSeconds(idleSeconds + CoresToTest(topo).Count * (samples / 1000.0 + 0.4));

    public static BenchResult Run(CpuTopology topo, IReadOnlyList<CpuSetEntry> cpuSets, int samples, int idleSeconds,
        Action<string, double> progress, CancellationToken ct)
    {
        BenchResult? result = null;
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { result = RunOnThisThread(topo, cpuSets, samples, idleSeconds, progress, ct); }
            catch (Exception ex) { error = ex; }
        }, 1 << 20) { IsBackground = true, Name = "core benchmark" };
        t.Start();
        t.Join();
        if (error is OperationCanceledException) throw error;
        if (error is not null) throw new InvalidOperationException("The core benchmark failed: " + error.Message, error);
        return result!;
    }

    private static BenchResult RunOnThisThread(CpuTopology topo, IReadOnlyList<CpuSetEntry> cpuSets, int samples, int idleSeconds,
        Action<string, double> progress, CancellationToken ct)
    {
        var cores = CoresToTest(topo);
        int logical = Math.Min(64, topo.LogicalCount);
        var res = new BenchResult { When = DateTime.Now, SamplesPerCore = samples, IdleSeconds = idleSeconds };
        IntPtr me = GetCurrentThread();
        OptOutOfPowerThrottling(me);

        // Process Lasso (or a "start /affinity") may have limited this app to some CPUs: widen it for the
        // benchmark, and put the original mask back afterwards so the user's rule stays in force.
        UIntPtr? savedAffinity = null;
        if (GetProcessAffinityMask(GetCurrentProcess(), out var procMask, out var sysMask) && procMask != sysMask)
        {
            if (!SetProcessAffinityMask(GetCurrentProcess(), sysMask))
                throw new InvalidOperationException("This app is limited to some CPUs (a Process Lasso rule or similar), so it can't test every core. Exclude StutterGuard.exe from that rule and run the benchmark again.");
            savedAffinity = procMask;
            Log.Info($"Benchmark: widened process affinity from 0x{(ulong)procMask:X} to 0x{(ulong)sysMask:X}");
        }
        double totalSteps = idleSeconds + cores.Count * (samples / 1000.0 + 0.4);
        double done = 0;

        // 1. Load already there, at idle. The benchmark thread sleeps on CPU 0's sibling meanwhile.
        progress($"Measuring the interrupt load already on each CPU ({idleSeconds} s, keep the PC idle)...", 0);
        var a = SysInfo.Counters(logical);
        var sw = Stopwatch.StartNew();
        for (int s = 0; s < idleSeconds; s++)
        {
            ct.ThrowIfCancellationRequested();
            Thread.Sleep(1000);
            done += 1;
            progress($"Measuring the interrupt load already on each CPU ({idleSeconds - s - 1} s left)...", done / totalSteps);
        }
        var b = SysInfo.Counters(logical);
        var idle = CpuRates.Between(a, b, sw.Elapsed.TotalSeconds);

        IntPtr timer = CreateWaitableTimerExW(IntPtr.Zero, null, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
        res.HighResolutionTimer = timer != IntPtr.Zero;
        if (timer == IntPtr.Zero) timer = CreateWaitableTimerExW(IntPtr.Zero, null, 0, TIMER_ALL_ACCESS);
        if (timer == IntPtr.Zero) throw new InvalidOperationException("Couldn't create a waitable timer: " + Marshal.GetLastWin32Error());

        try
        {
            foreach (int cpu in cores)
            {
                ct.ThrowIfCancellationRequested();
                progress($"CPU {cpu}: speed and timer wake-ups...", done / totalSteps);
                if (SetThreadAffinityMask(me, (UIntPtr)(1UL << cpu)) == UIntPtr.Zero)
                    throw new InvalidOperationException($"Couldn't run on CPU {cpu}. If Process Lasso has a rule for StutterGuard.exe, exclude it and run the benchmark again.");
                for (int tries = 0; tries < 50 && GetCurrentProcessorNumber() != cpu; tries++) Thread.Sleep(1); // let the scheduler move us
                if (GetCurrentProcessorNumber() != cpu)
                    throw new InvalidOperationException($"Windows didn't move the benchmark onto CPU {cpu}. Is a tool forcing this app's affinity? Exclude StutterGuard.exe and run again.");

                // 2. Speed: the same fixed workload three times, keep the best.
                double best = double.MaxValue;
                for (int r = 0; r < 3; r++)
                {
                    var w = Stopwatch.StartNew();
                    _sink += Work(12_000_000);
                    best = Math.Min(best, w.Elapsed.TotalMilliseconds);
                }

                // 3. Wake latency: 1 ms timer, how late each wake-up is.
                SetThreadPriority(me, THREAD_PRIORITY_TIME_CRITICAL);
                var late = new double[samples];
                double tickUs = 1_000_000.0 / Stopwatch.Frequency;
                for (int i = -50; i < samples; i++)
                {
                    long due = -10_000; // 1 ms, relative, in 100 ns units
                    long t0 = Stopwatch.GetTimestamp();
                    SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false);
                    WaitForSingleObject(timer, 1000);
                    long t1 = Stopwatch.GetTimestamp();
                    if (i >= 0) late[i] = Math.Max(0, (t1 - t0) * tickUs - 1000.0);
                    if ((i & 1023) == 0) ct.ThrowIfCancellationRequested();
                }
                SetThreadPriority(me, THREAD_PRIORITY_NORMAL);
                Array.Sort(late);

                int coreId = topo.Cpus.First(c => c.Index == cpu).CoreId;
                var coreThreads = topo.Cpus.Where(c => c.CoreId == coreId && c.Index < idle.Length).Select(c => idle[c.Index]).ToList();
                res.Cores.Add(new CoreScore
                {
                    Cpu = cpu,
                    SpeedMs = Math.Round(best, 3),
                    WakeMedianUs = Math.Round(late[late.Length / 2], 1),
                    WakeP90Us = Math.Round(late[(int)((late.Length - 1) * 0.90)], 1),
                    WakeP95Us = Math.Round(late[(int)((late.Length - 1) * 0.95)], 1),
                    WakeP99Us = Math.Round(late[(int)((late.Length - 1) * 0.99)], 1),
                    WakeP999Us = Math.Round(late[(int)((late.Length - 1) * 0.999)], 1),
                    LateOver100Pct = Math.Round(late.Count(x => x > 100) * 100.0 / late.Length, 2),
                    WakeMaxUs = Math.Round(late[^1], 1),
                    InterruptsPerSec = Math.Round(coreThreads.Sum(r => r.InterruptsPerSec), 1),
                    DpcsPerSec = Math.Round(coreThreads.Sum(r => r.DpcsPerSec), 1),
                    IsrPercent = Math.Round(coreThreads.Sum(r => r.IsrPercent), 3),
                    DpcPercent = Math.Round(coreThreads.Sum(r => r.DpcPercent), 3),
                    SchedulingClass = cpuSets.FirstOrDefault(s => s.LogicalIndex == cpu && s.Group == 0)?.SchedulingClass ?? 0,
                });
                done += samples / 1000.0 + 0.4;
            }
        }
        finally
        {
            SetThreadPriority(me, THREAD_PRIORITY_NORMAL);
            CloseHandle(timer);
            if (savedAffinity is { } restore && !SetProcessAffinityMask(GetCurrentProcess(), restore))
                Log.Warn($"Couldn't restore the process affinity mask 0x{(ulong)restore:X} after the benchmark.");
            else if (savedAffinity is { } restored)
                Log.Info($"Benchmark: restored process affinity to 0x{(ulong)restored:X}");
        }
        progress("Done.", 1);
        Log.Info("Core benchmark: " + string.Join("; ", res.Cores.Select(c => $"{c.Cpu}: {c.SpeedMs} ms, wake p99 {c.WakeP99Us} us")));
        return res;
    }

    private static ulong Work(int iterations)
    {
        ulong x = 88172645463325252UL, acc = 0;
        for (int i = 0; i < iterations; i++)
        {
            x ^= x << 13;
            x ^= x >> 7;
            x ^= x << 17;
            acc += x * 0x9E3779B97F4A7C15UL;
        }
        return acc;
    }
}
