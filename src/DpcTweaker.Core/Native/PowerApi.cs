using System.Runtime.InteropServices;
using System.Text;

namespace DpcTweaker.Core.Native;

/// <summary>powrprof.dll: the same API powercfg.exe uses, without its localized text output.</summary>
public static class PowerApi
{
    public static readonly Guid UltimateTemplate = new("e9a42b02-d5df-448d-aa00-03f14749eb61");
    public static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    public static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    public static readonly Guid PowerSaver = new("a1841308-3541-4fab-bc81-f71556f20b4a");

    private const uint ERROR_SUCCESS = 0;
    private const uint ERROR_MORE_DATA = 234;
    private const uint ERROR_NO_MORE_ITEMS = 259;
    private const uint ACCESS_SCHEME = 16;

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadACValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroup, ref Guid setting, out uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadDCValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroup, ref Guid setting, out uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteACValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroup, ref Guid setting, uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteDCValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroup, ref Guid setting, uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerDuplicateScheme(IntPtr rootPowerKey, ref Guid sourceSchemeGuid, out IntPtr destinationSchemeGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerDeleteScheme(IntPtr rootPowerKey, ref Guid schemeGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerEnumerate(IntPtr rootPowerKey, IntPtr schemeGuid, IntPtr subGroupGuid,
        uint accessFlags, uint index, ref Guid buffer, ref uint bufferSize);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadFriendlyName(IntPtr rootPowerKey, ref Guid schemeGuid, IntPtr subGroupGuid,
        IntPtr settingGuid, IntPtr buffer, ref uint bufferSize);

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteFriendlyName(IntPtr rootPowerKey, ref Guid schemeGuid, IntPtr subGroupGuid,
        IntPtr settingGuid, byte[] buffer, uint bufferSize);

    [DllImport("powrprof.dll")]
    private static extern uint PowerDeterminePlatformRoleEx(uint version);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private static Guid TakeGuid(IntPtr p)
    {
        try { return Marshal.PtrToStructure<Guid>(p); }
        finally { LocalFree(p); }
    }

    public static Guid? GetActive() =>
        PowerGetActiveScheme(IntPtr.Zero, out var p) == ERROR_SUCCESS ? TakeGuid(p) : null;

    public static void SetActive(Guid scheme)
    {
        uint rc = PowerSetActiveScheme(IntPtr.Zero, ref scheme);
        if (rc != ERROR_SUCCESS) throw new InvalidOperationException($"Couldn't activate power plan {scheme} (error {rc}).");
    }

    public static uint? ReadAc(Guid scheme, Guid sub, Guid setting) =>
        PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out uint v) == ERROR_SUCCESS ? v : null;

    public static uint? ReadDc(Guid scheme, Guid sub, Guid setting) =>
        PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out uint v) == ERROR_SUCCESS ? v : null;

    public static void WriteAc(Guid scheme, Guid sub, Guid setting, uint value)
    {
        uint rc = PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, value);
        if (rc != ERROR_SUCCESS) throw new InvalidOperationException($"Couldn't write setting {setting} (error {rc}).");
    }

    public static void WriteDc(Guid scheme, Guid sub, Guid setting, uint value)
    {
        uint rc = PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, value);
        if (rc != ERROR_SUCCESS) throw new InvalidOperationException($"Couldn't write setting {setting} on battery (error {rc}).");
    }

    public static List<Guid> EnumerateSchemes()
    {
        var list = new List<Guid>();
        for (uint i = 0; ; i++)
        {
            var g = Guid.Empty;
            uint size = 16;
            uint rc = PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ACCESS_SCHEME, i, ref g, ref size);
            if (rc == ERROR_NO_MORE_ITEMS) break;
            if (rc != ERROR_SUCCESS) break;
            list.Add(g);
        }
        return list;
    }

    public static bool Exists(Guid scheme) => EnumerateSchemes().Contains(scheme);

    public static string GetName(Guid scheme)
    {
        uint size = 0;
        uint rc = PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref size);
        if ((rc != ERROR_SUCCESS && rc != ERROR_MORE_DATA) || size == 0) return scheme.ToString();
        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            rc = PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, buf, ref size);
            if (rc != ERROR_SUCCESS) return scheme.ToString();
            return Marshal.PtrToStringUni(buf) ?? scheme.ToString();
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    public static void SetName(Guid scheme, string name)
    {
        byte[] bytes = Encoding.Unicode.GetBytes(name + "\0");
        uint rc = PowerWriteFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, bytes, (uint)bytes.Length);
        if (rc != ERROR_SUCCESS) throw new InvalidOperationException($"Couldn't rename power plan (error {rc}).");
    }

    public static Guid Duplicate(Guid source)
    {
        uint rc = PowerDuplicateScheme(IntPtr.Zero, ref source, out IntPtr p);
        if (rc != ERROR_SUCCESS)
            throw new InvalidOperationException($"Windows refused to create the plan (error {rc}). Modern Standby laptops block extra plans.");
        return TakeGuid(p);
    }

    public static void Delete(Guid scheme)
    {
        uint rc = PowerDeleteScheme(IntPtr.Zero, ref scheme);
        if (rc != ERROR_SUCCESS) throw new InvalidOperationException($"Couldn't delete power plan (error {rc}).");
    }

    /// <summary>POWER_PLATFORM_ROLE: 1 Desktop, 2 Mobile, 3 Workstation, 8 Slate, ...</summary>
    public static int PlatformRole()
    {
        try { return (int)PowerDeterminePlatformRoleEx(2); }
        catch { return 0; }
    }
}
