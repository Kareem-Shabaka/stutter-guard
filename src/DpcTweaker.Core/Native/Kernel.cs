using System.Runtime.InteropServices;

namespace DpcTweaker.Core.Native;

public static class Kernel
{
    public const ushort RelationProcessorCore = 0;
    public const ushort RelationCache = 2;
    public const ushort RelationGroup = 4;
    public const ushort RelationAll = 0xFFFF;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformationEx(int relationshipType, IntPtr buffer, ref uint returnedLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryTimerResolution(out uint minimum, out uint maximum, out uint current);

    /// <summary>The raw SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX buffer for RelationAll.</summary>
    public static byte[] GetProcessorInformation()
    {
        uint len = 0;
        GetLogicalProcessorInformationEx(RelationAll, IntPtr.Zero, ref len);
        if (len == 0) throw new InvalidOperationException("GetLogicalProcessorInformationEx returned no size.");
        IntPtr buf = Marshal.AllocHGlobal((int)len);
        try
        {
            if (!GetLogicalProcessorInformationEx(RelationAll, buf, ref len))
                throw new InvalidOperationException("GetLogicalProcessorInformationEx failed: " + Marshal.GetLastWin32Error());
            var bytes = new byte[len];
            Marshal.Copy(buf, bytes, 0, (int)len);
            return bytes;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    /// <summary>True when Windows reports a system battery (BatteryFlag 128 = no battery, 255 = unknown).</summary>
    public static bool? HasBattery()
    {
        if (!GetSystemPowerStatus(out var s)) return null;
        if (s.BatteryFlag == 255) return null;
        return (s.BatteryFlag & 128) == 0;
    }

    public static bool? OnAcPower()
    {
        if (!GetSystemPowerStatus(out var s)) return null;
        return s.ACLineStatus switch { 0 => false, 1 => true, _ => null };
    }

    /// <summary>Timer resolution in 100-ns units: (coarsest, finest, current).</summary>
    public static (uint Min, uint Max, uint Current)? TimerResolution()
    {
        try
        {
            return NtQueryTimerResolution(out uint mn, out uint mx, out uint cur) == 0 ? (mn, mx, cur) : null;
        }
        catch
        {
            return null;
        }
    }
}
