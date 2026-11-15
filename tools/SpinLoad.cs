using System;
using System.Collections.Generic;
using System.Threading;
using System.Runtime.InteropServices;

// Controlled background load, pinned to an explicit set of logical processors.
// Simulates "N ordinary processes confined to this mask" so the two candidate affinity
// masks can be compared without depending on whatever the desktop happens to be doing.
public class SpinLoad
{
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll", SetLastError = true)] static extern UIntPtr SetThreadAffinityMask(IntPtr h, UIntPtr m);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] static extern bool GetProcessAffinityMask(IntPtr p, out UIntPtr proc, out UIntPtr sys);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetProcessAffinityMask(IntPtr p, UIntPtr m);

    static volatile bool _stop;
    static readonly List<Thread> _threads = new List<Thread>();
    static long _sink;

    public static void Widen()
    {
        UIntPtr a, b;
        GetProcessAffinityMask(GetCurrentProcess(), out a, out b);
        if (a.ToUInt64() != b.ToUInt64()) SetProcessAffinityMask(GetCurrentProcess(), (UIntPtr)b.ToUInt64());
    }

    public static int Start(ulong mask, int threadsPerCpu)
    {
        Stop();
        _stop = false;
        Widen();
        for (int cpu = 0; cpu < 64; cpu++)
        {
            if ((mask & (1UL << cpu)) == 0) continue;
            for (int t = 0; t < threadsPerCpu; t++)
            {
                int target = cpu;
                var th = new Thread(() => Body(target)) { IsBackground = true, Priority = ThreadPriority.Normal };
                th.Start();
                _threads.Add(th);
            }
        }
        return _threads.Count;
    }

    static void Body(int cpu)
    {
        SetThreadAffinityMask(GetCurrentThread(), (UIntPtr)(1UL << cpu));
        var buf = new long[64];
        long i = 0;
        while (!_stop)
        {
            // Mix of ALU and memory traffic: closer to a real background process than a bare spin.
            for (int k = 0; k < 64; k++) buf[k] += (i + k) ^ (i >> 3);
            _sink += buf[(int)(i & 63)];
            i++;
            if ((i & 0x3FFF) == 0) Thread.Yield();
        }
    }

    public static void Stop()
    {
        _stop = true;
        foreach (var t in _threads) { try { t.Join(2000); } catch { } }
        _threads.Clear();
    }

    public static int ThreadCount() { return _threads.Count; }
    public static long Sink() { return _sink; }
}
