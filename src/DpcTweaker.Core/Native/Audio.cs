using System.Runtime.InteropServices;

namespace DpcTweaker.Core.Native;

/// <summary>
/// The default playback device, through the Core Audio API (MMDevice). Its endpoint id
/// "{0.0.0.00000000}.{guid}" is also a device node "SWD\MMDEVAPI\{0.0.0.00000000}.{guid}",
/// which sits under the audio device in the device tree, so the tree walk can find
/// which PCI device (GPU HDMI audio, HD Audio controller or a USB controller) carries it.
/// Also plays a silent loop, so the audio path is busy during the interrupt check.
/// </summary>
public static class Audio
{
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, out IntPtr iface);
        [PreserveSig] int OpenPropertyStore(int stgmAccess, out IntPtr properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    /// <summary>The device-node instance id of the default playback endpoint (eRender, eConsole), or null.</summary>
    public static string? DefaultRenderEndpointNode()
    {
        try
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            try
            {
                if (en.GetDefaultAudioEndpoint(0, 0, out var dev) != 0 || dev is null) return null;
                try
                {
                    if (dev.GetId(out string id) != 0 || string.IsNullOrEmpty(id)) return null;
                    return @"SWD\MMDEVAPI\" + id;
                }
                finally
                {
                    Marshal.ReleaseComObject(dev);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(en);
            }
        }
        catch
        {
            return null;
        }
    }

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW")]
    private static extern bool PlaySound(IntPtr sound, IntPtr module, uint flags);

    private const uint SND_ASYNC = 0x0001;
    private const uint SND_NODEFAULT = 0x0002;
    private const uint SND_MEMORY = 0x0004;
    private const uint SND_LOOP = 0x0008;

    private static IntPtr _silence;

    /// <summary>Loops one second of digital silence on the default device (the stream still runs, so the device interrupts).</summary>
    public static void StartSilence()
    {
        StopSilence();
        byte[] wav = SilentWav(48000, 2, 1.0);
        _silence = Marshal.AllocHGlobal(wav.Length);
        Marshal.Copy(wav, 0, _silence, wav.Length);
        PlaySound(_silence, IntPtr.Zero, SND_ASYNC | SND_MEMORY | SND_LOOP | SND_NODEFAULT);
    }

    public static void StopSilence()
    {
        PlaySound(IntPtr.Zero, IntPtr.Zero, 0);
        if (_silence != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_silence);
            _silence = IntPtr.Zero;
        }
    }

    /// <summary>A 16-bit PCM WAV file of zeros.</summary>
    public static byte[] SilentWav(int rate, short channels, double seconds)
    {
        int dataLen = (int)(rate * seconds) * channels * 2;
        var ms = new MemoryStream(44 + dataLen);
        var w = new BinaryWriter(ms);
        w.Write("RIFF"u8.ToArray());
        w.Write(36 + dataLen);
        w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray());
        w.Write(16);
        w.Write((short)1);              // PCM
        w.Write(channels);
        w.Write(rate);
        w.Write(rate * channels * 2);   // bytes per second
        w.Write((short)(channels * 2)); // block align
        w.Write((short)16);             // bits per sample
        w.Write("data"u8.ToArray());
        w.Write(dataLen);
        w.Write(new byte[dataLen]);
        w.Flush();
        return ms.ToArray();
    }
}
