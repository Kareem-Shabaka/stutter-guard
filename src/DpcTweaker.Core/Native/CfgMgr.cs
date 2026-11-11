using System.Runtime.InteropServices;
using System.Text;

namespace DpcTweaker.Core.Native;

/// <summary>A device property key (DEVPROPKEY): format GUID + property id.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct DevPropKey
{
    public Guid FmtId;
    public uint Pid;

    public DevPropKey(string guid, uint pid)
    {
        FmtId = new Guid(guid);
        Pid = pid;
    }
}

/// <summary>
/// The Configuration Manager API (cfgmgr32.dll): walks the live device tree and reads
/// device properties. Read-only. Works for every device Windows has, any make or model.
/// </summary>
public static class CfgMgr
{
    public const int CR_SUCCESS = 0;
    public const int CR_BUFFER_SMALL = 0x1A;
    public const uint DN_STARTED = 0x00000008;
    public const uint DN_HAS_PROBLEM = 0x00000400;

    private const uint DEVPROP_TYPE_BYTE = 0x03;
    private const uint DEVPROP_TYPE_UINT32 = 0x07;
    private const uint DEVPROP_TYPE_GUID = 0x0D;
    private const uint DEVPROP_TYPE_STRING = 0x12;
    private const uint DEVPROP_TYPE_STRING_LIST = 0x2012;

    // DEVPKEY_Device_* {a45c254e-df1c-4efd-8020-67d146a850e0}
    private const string DeviceFmt = "a45c254e-df1c-4efd-8020-67d146a850e0";
    public static readonly DevPropKey DeviceDesc = new(DeviceFmt, 2);
    public static readonly DevPropKey HardwareIds = new(DeviceFmt, 3);
    public static readonly DevPropKey CompatibleIds = new(DeviceFmt, 4);
    public static readonly DevPropKey Service = new(DeviceFmt, 6);
    public static readonly DevPropKey ClassGuid = new(DeviceFmt, 10);
    public static readonly DevPropKey Driver = new(DeviceFmt, 11);
    public static readonly DevPropKey FriendlyName = new(DeviceFmt, 14);
    public static readonly DevPropKey LocationInfo = new(DeviceFmt, 15);
    public static readonly DevPropKey BusNumber = new(DeviceFmt, 23);
    public static readonly DevPropKey Address = new(DeviceFmt, 30);

    // DEVPKEY_PciDevice_* {3ab22e31-8264-4b4e-9af5-a8d2d8e33e62}
    private const string PciFmt = "3ab22e31-8264-4b4e-9af5-a8d2d8e33e62";
    public static readonly DevPropKey PciBaseClass = new(PciFmt, 3);
    public static readonly DevPropKey PciSubClass = new(PciFmt, 4);
    public static readonly DevPropKey PciProgIf = new(PciFmt, 5);
    public static readonly DevPropKey PciInterruptSupport = new(PciFmt, 14);
    public static readonly DevPropKey PciInterruptMessageMaximum = new(PciFmt, 15);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint devInst, string? deviceId, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_Child(out uint child, uint devInst, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_Sibling(out uint sibling, uint devInst, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_IDW(uint devInst, StringBuilder buffer, int bufferLen, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_DevNode_PropertyW(uint devInst, ref DevPropKey key, out uint type,
        byte[]? buffer, ref uint size, uint flags);

    public static uint? Root()
    {
        return CM_Locate_DevNodeW(out uint root, null, 0) == CR_SUCCESS ? root : null;
    }

    /// <summary>The present device with this instance id, or null.</summary>
    public static uint? Locate(string instanceId)
    {
        return CM_Locate_DevNodeW(out uint dn, instanceId, 0) == CR_SUCCESS ? dn : null;
    }

    public static uint? FirstChild(uint devInst) => CM_Get_Child(out uint c, devInst, 0) == CR_SUCCESS ? c : null;

    public static uint? NextSibling(uint devInst) => CM_Get_Sibling(out uint s, devInst, 0) == CR_SUCCESS ? s : null;

    public static string? InstanceId(uint devInst)
    {
        var sb = new StringBuilder(512);
        return CM_Get_Device_IDW(devInst, sb, sb.Capacity, 0) == CR_SUCCESS ? sb.ToString() : null;
    }

    public static (uint Status, uint Problem) Status(uint devInst)
    {
        return CM_Get_DevNode_Status(out uint st, out uint pr, devInst, 0) == CR_SUCCESS ? (st, pr) : (0u, 0u);
    }

    private static (uint Type, byte[]? Data) Raw(uint devInst, DevPropKey key)
    {
        uint size = 0;
        int cr = CM_Get_DevNode_PropertyW(devInst, ref key, out uint type, null, ref size, 0);
        if (cr != CR_BUFFER_SMALL || size == 0) return (0, null);
        var buf = new byte[size];
        cr = CM_Get_DevNode_PropertyW(devInst, ref key, out type, buf, ref size, 0);
        return cr == CR_SUCCESS ? (type, buf) : (0, null);
    }

    public static string? String(uint devInst, DevPropKey key)
    {
        var (type, data) = Raw(devInst, key);
        if (data is null || type != DEVPROP_TYPE_STRING) return null;
        return Encoding.Unicode.GetString(data).TrimEnd('\0');
    }

    public static string[] StringList(uint devInst, DevPropKey key)
    {
        var (type, data) = Raw(devInst, key);
        if (data is null || type != DEVPROP_TYPE_STRING_LIST) return Array.Empty<string>();
        return Encoding.Unicode.GetString(data).Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    public static uint? UInt32(uint devInst, DevPropKey key)
    {
        var (type, data) = Raw(devInst, key);
        if (data is null || type != DEVPROP_TYPE_UINT32 || data.Length < 4) return null;
        return BitConverter.ToUInt32(data, 0);
    }

    public static byte? Byte(uint devInst, DevPropKey key)
    {
        var (type, data) = Raw(devInst, key);
        if (data is null || data.Length < 1) return null;
        // Accept any small integer type: the documented type isn't what every Windows build returns.
        return type switch
        {
            DEVPROP_TYPE_BYTE or 0x02 => data[0],
            0x04 or 0x05 when data.Length >= 2 => (byte)BitConverter.ToUInt16(data, 0),
            0x06 or DEVPROP_TYPE_UINT32 when data.Length >= 4 => (byte)BitConverter.ToUInt32(data, 0),
            _ => null,
        };
    }

    public static Guid? GuidValue(uint devInst, DevPropKey key)
    {
        var (type, data) = Raw(devInst, key);
        if (data is null || type != DEVPROP_TYPE_GUID || data.Length < 16) return null;
        return new Guid(data.AsSpan(0, 16));
    }
}
