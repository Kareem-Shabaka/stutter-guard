using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace DpcTweaker.Core.Native;

/// <summary>
/// One CPU set from GetSystemCpuSetInformation. SchedulingClass is Windows' preferred-core
/// ranking (higher = the scheduler prefers it; AMD CPPC / Intel Turbo Boost Max ranks end up here).
/// </summary>
public sealed record CpuSetEntry(int LogicalIndex, int Group, int CoreIndex, int LastLevelCacheIndex,
    int EfficiencyClass, int SchedulingClass, bool Parked);

/// <summary>Per-CPU counters since boot (NtQuerySystemInformation).</summary>
public sealed record CpuCounters(long IdleTime, long KernelTime, long UserTime, long DpcTime, long InterruptTime,
    uint InterruptCount, uint DpcCount);

public static class SysInfo
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemCpuSetInformation(IntPtr information, uint bufferLength, out uint returnedLength,
        IntPtr process, uint flags);

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returnLength);

    private const int SystemProcessorPerformanceInformation = 8;
    private const int SystemInterruptInformation = 23;

    public static IReadOnlyList<CpuSetEntry> CpuSets()
    {
        try
        {
            GetSystemCpuSetInformation(IntPtr.Zero, 0, out uint len, IntPtr.Zero, 0);
            if (len == 0) return Array.Empty<CpuSetEntry>();
            IntPtr buf = Marshal.AllocHGlobal((int)len);
            try
            {
                if (!GetSystemCpuSetInformation(buf, len, out len, IntPtr.Zero, 0)) return Array.Empty<CpuSetEntry>();
                var bytes = new byte[len];
                Marshal.Copy(buf, bytes, 0, (int)len);
                return ParseCpuSets(bytes);
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
        catch
        {
            return Array.Empty<CpuSetEntry>();
        }
    }

    /// <summary>
    /// SYSTEM_CPU_SET_INFORMATION: DWORD Size, DWORD Type, then CpuSet:
    ///   DWORD Id @8, WORD Group @12, BYTE LogicalProcessorIndex @14, BYTE CoreIndex @15,
    ///   BYTE LastLevelCacheIndex @16, BYTE NumaNodeIndex @17, BYTE EfficiencyClass @18,
    ///   BYTE AllFlags @19 (bit 0 = Parked), BYTE SchedulingClass @20 (union with Reserved), DWORD64 AllocationTag @24.
    /// </summary>
    public static IReadOnlyList<CpuSetEntry> ParseCpuSets(ReadOnlySpan<byte> buf)
    {
        var list = new List<CpuSetEntry>();
        int off = 0;
        while (off + 8 <= buf.Length)
        {
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(buf[off..]);
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(buf[(off + 4)..]);
            if (size < 24 || off + size > buf.Length) break;
            if (type == 0) // CpuSetInformation
            {
                var e = buf.Slice(off, (int)size);
                list.Add(new CpuSetEntry(
                    LogicalIndex: e[14],
                    Group: BinaryPrimitives.ReadUInt16LittleEndian(e[12..]),
                    CoreIndex: e[15],
                    LastLevelCacheIndex: e[16],
                    EfficiencyClass: e[18],
                    SchedulingClass: e[20],
                    Parked: (e[19] & 1) != 0));
            }
            off += (int)size;
        }
        return list;
    }

    /// <summary>Counters for every logical CPU in processor group 0, indexed by CPU number.</summary>
    public static CpuCounters[] Counters(int cpuCount)
    {
        const int perfSize = 48; // 5 x LARGE_INTEGER + ULONG InterruptCount + padding
        const int intSize = 24;  // 6 x ULONG
        var result = new CpuCounters[cpuCount];
        IntPtr perf = Marshal.AllocHGlobal(perfSize * cpuCount);
        IntPtr ints = Marshal.AllocHGlobal(intSize * cpuCount);
        try
        {
            int st1 = NtQuerySystemInformation(SystemProcessorPerformanceInformation, perf, perfSize * cpuCount, out int len1);
            int st2 = NtQuerySystemInformation(SystemInterruptInformation, ints, intSize * cpuCount, out int len2);
            if (st1 != 0) throw new InvalidOperationException($"NtQuerySystemInformation(8) failed: 0x{st1:X8}");
            int n1 = Math.Min(cpuCount, len1 / perfSize);
            int n2 = st2 == 0 ? Math.Min(cpuCount, len2 / intSize) : 0;
            for (int i = 0; i < cpuCount; i++)
            {
                if (i >= n1) { result[i] = new CpuCounters(0, 0, 0, 0, 0, 0, 0); continue; }
                IntPtr p = perf + i * perfSize;
                uint dpcCount = i < n2 ? (uint)Marshal.ReadInt32(ints + i * intSize + 4) : 0;
                result[i] = new CpuCounters(
                    Marshal.ReadInt64(p, 0), Marshal.ReadInt64(p, 8), Marshal.ReadInt64(p, 16),
                    Marshal.ReadInt64(p, 24), Marshal.ReadInt64(p, 32),
                    (uint)Marshal.ReadInt32(p, 40), dpcCount);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(perf);
            Marshal.FreeHGlobal(ints);
        }
        return result;
    }
}

/// <summary>Rates between two counter snapshots.</summary>
public sealed record CpuRates(int Cpu, double InterruptsPerSec, double DpcsPerSec, double IsrPercent, double DpcPercent)
{
    public static CpuRates[] Between(CpuCounters[] a, CpuCounters[] b, double seconds)
    {
        var r = new CpuRates[Math.Min(a.Length, b.Length)];
        for (int i = 0; i < r.Length; i++)
        {
            // KernelTime includes idle time, so kernel + user = all time on that CPU.
            double total = (b[i].KernelTime - a[i].KernelTime) + (b[i].UserTime - a[i].UserTime);
            double isr = b[i].InterruptTime - a[i].InterruptTime;
            double dpc = b[i].DpcTime - a[i].DpcTime;
            r[i] = new CpuRates(i,
                unchecked(b[i].InterruptCount - a[i].InterruptCount) / seconds,
                unchecked(b[i].DpcCount - a[i].DpcCount) / seconds,
                total > 0 ? isr * 100 / total : 0,
                total > 0 ? dpc * 100 / total : 0);
        }
        return r;
    }
}
