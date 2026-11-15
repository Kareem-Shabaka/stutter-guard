using System.Text.RegularExpressions;
using System.Runtime.InteropServices;

namespace DpcTweaker.Core.Native;

/// <summary>One display as Windows currently has it configured.</summary>
public sealed record DisplayInfo(string Device, string Adapter, int Width, int Height, int RefreshHz, bool Primary)
{
    public string Mode => $"{Width}x{Height} @ {RefreshHz} Hz";

    /// <summary>
    /// "Display 2" - Windows enumerates these as \\.\DISPLAY2 and Settings calls them Display 2, so the
    /// number is the user's own name for the screen. Printing the raw device path put "\\.\DISPLAY2"
    /// in front of them, which is a Win32 detail they never chose and cannot act on.
    /// </summary>
    public string Label
    {
        get
        {
            var m = Regex.Match(Device ?? "", @"DISPLAY(\d+)$", RegexOptions.IgnoreCase);
            return m.Success ? "Display " + m.Groups[1].Value : "A display";
        }
    }
}

/// <summary>
/// Active displays and their current mode. Windows composites every attached display, so their
/// refresh rates matter to the GPU driver's DPC cost, not just to the one you are looking at.
/// </summary>
public static class Displays
{
    private const int EnumCurrentSettings = -1;
    private const int AttachedToDesktop = 0x1;
    private const int PrimaryDevice = 0x4;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public short SpecVersion, DriverVersion, Size, DriverExtra;
        public int Fields;
        public int PositionX, PositionY, DisplayOrientation, DisplayFixedOutput;
        public short Color, Duplex, YResolution, TTOption, Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public short LogPixels;
        public int BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency;
        public int IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct DisplayDevice
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Ansi)]
    private static extern bool EnumDisplayDevices(string? device, uint devNum, ref DisplayDevice dd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Ansi)]
    private static extern bool EnumDisplaySettings(string device, int modeNum, ref DevMode dm);

    /// <summary>Every display currently attached to the desktop. Read-only, needs no elevation.</summary>
    public static IReadOnlyList<DisplayInfo> Active()
    {
        var list = new List<DisplayInfo>();
        try
        {
            var dd = new DisplayDevice();
            dd.cb = Marshal.SizeOf(dd);
            for (uint i = 0; EnumDisplayDevices(null, i, ref dd, 0); i++)
            {
                if ((dd.StateFlags & AttachedToDesktop) == 0) continue;
                var name = dd.DeviceName;
                var adapter = dd.DeviceString ?? "";
                bool primary = (dd.StateFlags & PrimaryDevice) != 0;

                var dm = new DevMode();
                dm.Size = (short)Marshal.SizeOf(dm);
                if (!EnumDisplaySettings(name, EnumCurrentSettings, ref dm)) continue;
                list.Add(new DisplayInfo(name, adapter, dm.PelsWidth, dm.PelsHeight, dm.DisplayFrequency, primary));

                dd = new DisplayDevice();
                dd.cb = Marshal.SizeOf(dd);
            }
        }
        catch (Exception ex)
        {
            Infra.Log.Warn("Display enumeration failed: " + ex.Message);
        }
        return list;
    }
}