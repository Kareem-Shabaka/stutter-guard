using System;
using System.Runtime.InteropServices;
using System.Collections.Generic;

// Measures scheduler wake latency on one logical processor.
// A high-resolution waitable timer fires every 1 ms; each wake's real interval is compared
// with the nominal 1000 us. The surplus is "lateness" -- the cost of a ready thread having to
// wait for a CPU. This isolates the wake-to-run leg of interrupt-to-process latency.
public class WakeProbe
{
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] static extern UIntPtr SetThreadAffinityMask(IntPtr h, UIntPtr m);
    [DllImport("kernel32.dll")] static extern bool GetProcessAffinityMask(IntPtr p, out UIntPtr proc, out UIntPtr sys);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetProcessAffinityMask(IntPtr p, UIntPtr m);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateWaitableTimerExW(IntPtr a, string n, uint f, uint acc);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetWaitableTimer(IntPtr t, ref long due, int period, IntPtr c, IntPtr a, bool r);
    [DllImport("kernel32.dll", SetLastError = true)] static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint p);
    [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint p);

    const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x2;
    const uint TIMER_ALL_ACCESS = 0x1F0003;
    static long Zero = 0;

    public static string Masks()
    {
        UIntPtr a, b;
        if (!GetProcessAffinityMask(GetCurrentProcess(), out a, out b)) return "query-failed";
        return string.Format("process=0x{0:X16} system=0x{1:X16}", a.ToUInt64(), b.ToUInt64());
    }

    public static ulong Mask()
    {
        UIntPtr a, b;
        GetProcessAffinityMask(GetCurrentProcess(), out a, out b);
        return a.ToUInt64();
    }

    // A process pinned by an external tool cannot place a thread outside its own mask.
    // Try to widen back to the system mask; report honestly if refused.
    public static bool Widen(out string note)
    {
        UIntPtr a, b;
        GetProcessAffinityMask(GetCurrentProcess(), out a, out b);
        ulong proc = a.ToUInt64(), sys = b.ToUInt64();
        if (proc == sys) { note = "already-full"; return true; }
        bool ok = SetProcessAffinityMask(GetCurrentProcess(), (UIntPtr)sys);
        note = ok ? "widened" : ("widen-failed:" + Marshal.GetLastWin32Error());
        return ok;
    }

    public static bool SetMask(ulong m)
    {
        return SetProcessAffinityMask(GetCurrentProcess(), (UIntPtr)m);
    }

    public static double[] Measure(int cpu, int samples, out string note)
    {
        var res = new List<double>(samples);
        note = "";
        if ((Mask() & (1UL << cpu)) == 0) { note = "cpu-not-in-mask"; return res.ToArray(); }

        IntPtr timer = IntPtr.Zero;
        timeBeginPeriod(1);
        try
        {
            UIntPtr prev = SetThreadAffinityMask(GetCurrentThread(), (UIntPtr)(1UL << cpu));
            if (prev == UIntPtr.Zero) { note = "setaffinity-failed:" + Marshal.GetLastWin32Error(); return res.ToArray(); }

            timer = CreateWaitableTimerExW(IntPtr.Zero, null, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
            bool hiRes = timer != IntPtr.Zero;
            if (!hiRes) timer = CreateWaitableTimerExW(IntPtr.Zero, null, 0, TIMER_ALL_ACCESS);
            if (timer == IntPtr.Zero) { note = "timer-failed:" + Marshal.GetLastWin32Error(); return res.ToArray(); }

            long due = -10000L;
            if (!SetWaitableTimer(timer, ref due, 1, IntPtr.Zero, IntPtr.Zero, false))
            { note = "settimer-failed:" + Marshal.GetLastWin32Error(); return res.ToArray(); }

            for (int i = 0; i < 20; i++) WaitForSingleObject(timer, 1000);

            long freq = System.Diagnostics.Stopwatch.Frequency;
            long prevT = System.Diagnostics.Stopwatch.GetTimestamp();
            for (int i = 0; i < samples; i++)
            {
                uint w = WaitForSingleObject(timer, 1000);
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                double us = (now - prevT) * 1000000.0 / freq;
                prevT = now;
                if (w == 0) res.Add(us - 1000.0);
            }
            note = hiRes ? "hires" : "coarse";
        }
        finally
        {
            if (timer != IntPtr.Zero) { SetWaitableTimer(timer, ref Zero, 0, IntPtr.Zero, IntPtr.Zero, false); CloseHandle(timer); }
            timeEndPeriod(1);
        }
        return res.ToArray();
    }
}
