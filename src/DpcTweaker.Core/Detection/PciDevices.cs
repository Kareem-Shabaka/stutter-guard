using System.Globalization;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using DpcTweaker.Core.Infra;
using DpcTweaker.Core.Native;

namespace DpcTweaker.Core.Detection;

/// <summary>What kind of PCI function this is, from its PCI class code (works for any vendor).</summary>
public enum PciKind
{
    Gpu,
    GpuAudio,
    Audio,
    UsbController,
    UsbHostInterface,
    Ethernet,
    Wifi,
    Capture,
    OtherPinnable,
    Storage,
    Never,
}

/// <summary>What hangs off a device (for a USB controller: everything plugged into it).</summary>
public sealed class Attached
{
    public int Mice { get; set; }
    public int Keyboards { get; set; }
    public int GameControllers { get; set; }
    public int AudioDevices { get; set; }
    public int AudioEndpoints { get; set; }
    public int Bluetooth { get; set; }
    public int Network { get; set; }
    public int Storage { get; set; }
    public int Cameras { get; set; }
    public int Monitors { get; set; }
    /// <summary>Physical USB devices (hubs included), not counting their interfaces.</summary>
    public int UsbDevices { get; set; }
    public int Total { get; set; }
    public bool HasDefaultAudio { get; set; }

    /// <summary>Friendly names of the interesting things, for the UI.</summary>
    public List<string> Names { get; set; } = new();

    /// <summary>Anything plugged in at all (a USB controller with only its root hubs has nothing).</summary>
    public bool HasDevices => UsbDevices + Mice + Keyboards + GameControllers + AudioDevices + Bluetooth + Network + Storage + Cameras > 0;

    public string Describe()
    {
        var parts = new List<string>();
        void Add(int n, string one, string many) { if (n > 0) parts.Add(n == 1 ? one : $"{n} {many}"); }
        Add(Mice, "mouse", "mice");
        Add(Keyboards, "keyboard", "keyboards");
        Add(GameControllers, "game controller", "game controllers");
        Add(AudioDevices, "audio device", "audio devices");
        Add(Bluetooth, "Bluetooth", "Bluetooth radios");
        Add(Network, "network adapter", "network adapters");
        Add(Storage, "drive", "drives");
        Add(Cameras, "camera", "cameras");
        Add(Monitors, "monitor", "monitors");
        if (HasDefaultAudio) parts.Add("the default playback device");
        if (parts.Count == 0 && UsbDevices > 0) Add(UsbDevices, "1 other USB device", "other USB devices");
        return parts.Count == 0 ? "nothing attached" : string.Join(", ", parts);
    }
}

/// <summary>One PCI function with everything the interrupt modules need.</summary>
public sealed class PciDevice
{
    public required string InstanceId { get; init; }
    public required string Name { get; init; }
    public byte BaseClass { get; init; }
    public byte SubClass { get; init; }
    public byte ProgIf { get; init; }
    public string? Service { get; init; }
    /// <summary>"{class guid}\0001": the driver key under Control\Class.</summary>
    public string? DriverKey { get; init; }
    public Guid? ClassGuid { get; init; }
    /// <summary>DEVPKEY_PciDevice_InterruptSupport: 1 = line, 2 = MSI, 4 = MSI-X.</summary>
    public uint InterruptSupport { get; init; }
    public uint MessageMaximum { get; init; }
    public bool Started { get; init; }
    public uint Problem { get; init; }
    public uint? Bus { get; init; }
    public uint? Address { get; init; }
    public string Location { get; init; } = "";
    public Attached Attached { get; init; } = new();
    public PciKind Kind { get; set; }

    /// <summary>The CPU mask this device is pinned to now (Affinity Policy DevicePolicy 4), or null.</summary>
    public ulong? ExistingMask { get; set; }

    // Display adapters
    public bool IsIntegratedGpu { get; set; }

    // Network adapters
    public string? NetCfgInstanceId { get; set; }
    public bool? Connected { get; set; }
    public bool RssCapable { get; set; }
    public List<string> RssQueueOptions { get; set; } = new();

    public string VendorId => Match(@"VEN_([0-9A-F]{4})");
    public string DeviceId => Match(@"DEV_([0-9A-F]{4})");
    public string ShortId => $"{VendorId}:{DeviceId}";

    public bool SupportsMsi => (InterruptSupport & 0x6) != 0;
    public bool SupportsMsiX => (InterruptSupport & 0x4) != 0;

    public string EnumKey => @"HKLM\SYSTEM\CurrentControlSet\Enum\" + InstanceId;
    public string InterruptKey => EnumKey + @"\Device Parameters\Interrupt Management";
    public string AffinityKey => InterruptKey + @"\Affinity Policy";
    public string MsiKey => InterruptKey + @"\MessageSignaledInterruptProperties";
    public string? ClassKey => DriverKey is null ? null : @"HKLM\SYSTEM\CurrentControlSet\Control\Class\" + DriverKey;

    public string InterruptSupportText
    {
        get
        {
            var p = new List<string>();
            if ((InterruptSupport & 1) != 0) p.Add("line");
            if ((InterruptSupport & 2) != 0) p.Add("MSI");
            if ((InterruptSupport & 4) != 0) p.Add("MSI-X");
            string s = p.Count == 0 ? "unknown" : string.Join(" / ", p);
            return MessageMaximum > 0 ? $"{s}, up to {MessageMaximum} message(s)" : s;
        }
    }

    private string Match(string pattern)
    {
        var m = Regex.Match(InstanceId, pattern, RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.ToUpperInvariant() : "????";
    }

    public override string ToString() => $"{Name} [{ShortId}] {Kind}";
}

/// <summary>Walks the live device tree once and builds the PCI device list.</summary>
public static class PciScanner
{
    public static readonly Guid MouseClass = new("4d36e96f-e325-11ce-bfc1-08002be10318");
    public static readonly Guid KeyboardClass = new("4d36e96b-e325-11ce-bfc1-08002be10318");
    public static readonly Guid HidClass = new("745a17a0-74d3-11d0-b6fe-00a0c90f57da");
    public static readonly Guid XnaClass = new("d61ca365-5af4-4486-998b-9db4734c6ca3");
    public static readonly Guid MediaClass = new("4d36e96c-e325-11ce-bfc1-08002be10318");
    public static readonly Guid AudioEndpointClass = new("c166523c-fe0c-4a94-a586-f1a80cfbbf3e");
    public static readonly Guid BluetoothClass = new("e0cbf06c-cd8b-4647-bb8a-263b43f0f974");
    public static readonly Guid NetClass = new("4d36e972-e325-11ce-bfc1-08002be10318");
    public static readonly Guid DiskClass = new("4d36e967-e325-11ce-bfc1-08002be10318");
    public static readonly Guid CameraClass = new("ca3e7ab9-b4c3-4ae6-8251-579ef933890f");
    public static readonly Guid ImageClass = new("6bdd1fc6-810f-11d0-bec7-08002be2092f");
    public static readonly Guid MonitorClass = new("4d36e96e-e325-11ce-bfc1-08002be10318");
    public static readonly Guid SensorClass = new("5175d334-c371-4806-b3ba-71fd53c9258d");

    private sealed class Node
    {
        public uint Inst;
        public string Id = "";
        public int Parent = -1;
        public Guid? Class;
        public List<int> Children = new();
    }

    public static List<PciDevice> Scan(IReadOnlyList<GpuInfo> gpus) => Scan(gpus, out _);

    /// <param name="startedPerClass">How many running devices of each setup class the whole tree has (Bluetooth, Sensor, Image...).</param>
    public static List<PciDevice> Scan(IReadOnlyList<GpuInfo> gpus, out Dictionary<Guid, int> startedPerClass)
    {
        startedPerClass = new Dictionary<Guid, int>();
        var nodes = new List<Node>();
        uint? root = CfgMgr.Root();
        if (root is null) return new List<PciDevice>();

        // Iterative depth-first walk of every present device.
        var stack = new Stack<(uint Inst, int Parent)>();
        stack.Push((root.Value, -1));
        while (stack.Count > 0 && nodes.Count < 20000)
        {
            var (inst, parent) = stack.Pop();
            var n = new Node { Inst = inst, Id = CfgMgr.InstanceId(inst) ?? "", Parent = parent, Class = CfgMgr.GuidValue(inst, CfgMgr.ClassGuid) };
            int idx = nodes.Count;
            nodes.Add(n);
            if (parent >= 0) nodes[parent].Children.Add(idx);
            for (uint? c = CfgMgr.FirstChild(inst); c is not null; c = CfgMgr.NextSibling(c.Value))
                stack.Push((c.Value, idx));
        }

        foreach (var n in nodes)
        {
            if (n.Class is not Guid cls) continue;
            if ((CfgMgr.Status(n.Inst).Status & CfgMgr.DN_STARTED) == 0) continue;
            startedPerClass[cls] = startedPerClass.TryGetValue(cls, out int c) ? c + 1 : 1;
        }

        string? defaultEndpoint = Audio.DefaultRenderEndpointNode();
        var netUp = NetworkUp();
        var list = new List<PciDevice>();

        for (int i = 0; i < nodes.Count; i++)
        {
            var n = nodes[i];
            if (!n.Id.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase)) continue;
            uint inst = n.Inst;
            var (status, problem) = CfgMgr.Status(inst);
            string name = CfgMgr.String(inst, CfgMgr.FriendlyName) ?? CfgMgr.String(inst, CfgMgr.DeviceDesc) ?? n.Id;

            var att = new Attached();
            Summarize(nodes, i, att, defaultEndpoint, isRoot: true);

            // The class code is in every PCI device's PnP ids ("PCI\CC_030000"); the PCI class
            // properties are the fallback (on 26H2 they came back empty for every device).
            var cc = ParseClassCode(CfgMgr.StringList(inst, CfgMgr.CompatibleIds).Concat(CfgMgr.StringList(inst, CfgMgr.HardwareIds)));
            byte baseClass = cc?.Base ?? CfgMgr.Byte(inst, CfgMgr.PciBaseClass) ?? 0xFF;
            byte subClass = cc?.Sub ?? CfgMgr.Byte(inst, CfgMgr.PciSubClass) ?? 0xFF;
            byte progIf = cc?.ProgIf ?? CfgMgr.Byte(inst, CfgMgr.PciProgIf) ?? 0;

            var d = new PciDevice
            {
                InstanceId = n.Id,
                Name = CleanName(name),
                BaseClass = baseClass,
                SubClass = subClass,
                ProgIf = progIf,
                Service = CfgMgr.String(inst, CfgMgr.Service),
                DriverKey = CfgMgr.String(inst, CfgMgr.Driver),
                ClassGuid = n.Class,
                InterruptSupport = CfgMgr.UInt32(inst, CfgMgr.PciInterruptSupport) ?? 0,
                MessageMaximum = CfgMgr.UInt32(inst, CfgMgr.PciInterruptMessageMaximum) ?? 0,
                Started = (status & CfgMgr.DN_STARTED) != 0,
                Problem = (status & CfgMgr.DN_HAS_PROBLEM) != 0 ? problem : 0,
                Bus = CfgMgr.UInt32(inst, CfgMgr.BusNumber),
                Address = CfgMgr.UInt32(inst, CfgMgr.Address),
                Location = CfgMgr.String(inst, CfgMgr.LocationInfo) ?? "",
                Attached = att,
            };
            list.Add(d);
        }

        Classify(list, gpus);
        FillNetwork(list, netUp);
        foreach (var d in list)
        {
            if (RegistryOps.ReadDword(d.AffinityKey, "DevicePolicy") != 4) continue;
            var v = RegistryOps.Read(d.AffinityKey, "AssignmentSetOverride");
            if (v.Exists && v.Value is byte[] b && b.Length > 0)
            {
                ulong m = 0;
                for (int i = 0; i < Math.Min(8, b.Length); i++) m |= (ulong)b[i] << (8 * i);
                d.ExistingMask = m;
            }
        }
        return list;
    }

    /// <summary>Counts what hangs below a node, stopping at the next PCI device (a bridge's children are their own PCI devices).</summary>
    private static void Summarize(List<Node> nodes, int index, Attached att, string? defaultEndpoint, bool isRoot)
    {
        var n = nodes[index];
        if (!isRoot)
        {
            if (n.Id.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase)) return;
            att.Total++;
            if (n.Id.StartsWith(@"USB\VID_", StringComparison.OrdinalIgnoreCase) && !n.Id.Contains("&MI_", StringComparison.OrdinalIgnoreCase))
                att.UsbDevices++;
            var c = n.Class;
            string? interesting = null;
            if (c == MouseClass) { att.Mice++; interesting = "mouse"; }
            else if (c == KeyboardClass) { att.Keyboards++; interesting = "keyboard"; }
            else if (c == XnaClass) { att.GameControllers++; interesting = "controller"; }
            else if (c == HidClass && n.Id.Contains("IG_", StringComparison.OrdinalIgnoreCase)) { att.GameControllers++; }
            else if (c == MediaClass) { att.AudioDevices++; interesting = "audio"; }
            else if (c == AudioEndpointClass) att.AudioEndpoints++;
            else if (c == BluetoothClass && n.Id.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase)) { att.Bluetooth++; interesting = "bluetooth"; }
            else if (c == NetClass) { att.Network++; interesting = "network"; }
            else if (c == DiskClass) att.Storage++;
            else if (c == CameraClass || c == ImageClass) att.Cameras++;
            else if (c == MonitorClass) att.Monitors++;

            if (defaultEndpoint is not null && string.Equals(n.Id, defaultEndpoint, StringComparison.OrdinalIgnoreCase))
                att.HasDefaultAudio = true;

            if (interesting is not null && att.Names.Count < 12)
            {
                string? name = CfgMgr.String(n.Inst, CfgMgr.FriendlyName) ?? CfgMgr.String(n.Inst, CfgMgr.DeviceDesc);
                if (!string.IsNullOrWhiteSpace(name) && !att.Names.Contains(name)) att.Names.Add(name);
            }
        }
        foreach (int child in n.Children) Summarize(nodes, child, att, defaultEndpoint, isRoot: false);
    }

    /// <summary>"PCI\CC_0C0330" -> (0x0C, 0x03, 0x30); "PCI\CC_0C03" -> (0x0C, 0x03, 0). The 6-digit form wins.</summary>
    public static (byte Base, byte Sub, byte ProgIf)? ParseClassCode(IEnumerable<string> ids)
    {
        (byte, byte, byte)? four = null;
        foreach (string id in ids)
        {
            var m = Regex.Match(id, @"CC_([0-9A-F]{2})([0-9A-F]{2})([0-9A-F]{2})?(?![0-9A-F])", RegexOptions.IgnoreCase);
            if (!m.Success) continue;
            byte b = Convert.ToByte(m.Groups[1].Value, 16), sc = Convert.ToByte(m.Groups[2].Value, 16);
            if (m.Groups[3].Success) return (b, sc, Convert.ToByte(m.Groups[3].Value, 16));
            four ??= (b, sc, 0);
        }
        return four;
    }

    private static string CleanName(string s) => Regex.Replace(s, @"\s+", " ").Trim();

    /// <summary>PCI class code -> kind. Public for the tests.</summary>
    public static PciKind KindFromClass(byte baseClass, byte subClass, byte progIf) => (baseClass, subClass) switch
    {
        (0x03, _) => PciKind.Gpu,
        (0x04, 0x03) or (0x04, 0x01) => PciKind.Audio,
        (0x04, _) => PciKind.Capture,
        (0x0C, 0x03) when progIf == 0x40 => PciKind.UsbHostInterface,
        (0x0C, 0x03) when progIf == 0xFE => PciKind.Never, // USB device (not a host controller)
        (0x0C, 0x03) => PciKind.UsbController,
        (0x02, 0x80) => PciKind.Wifi,
        (0x02, _) => PciKind.Ethernet,
        (0x0D, _) => PciKind.OtherPinnable,
        (0x01, _) => PciKind.Storage,
        _ => PciKind.Never,
    };

    public static void Classify(List<PciDevice> list, IReadOnlyList<GpuInfo> gpus)
    {
        foreach (var d in list)
        {
            d.Kind = KindFromClass(d.BaseClass, d.SubClass, d.ProgIf);
            if (d.Kind == PciKind.Gpu)
            {
                var g = gpus.FirstOrDefault(x => string.Equals(x.InstanceId, d.InstanceId, StringComparison.OrdinalIgnoreCase));
                d.IsIntegratedGpu = g?.IsIntegrated ?? Detector.LooksIntegrated(VendorOf(d), d.Name);
            }
            if (d.Kind == PciKind.Capture && d.VendorId == "1022" && d.DeviceId == "15E2")
                d.Kind = PciKind.Never; // AMD Audio CoProcessor (ACP DSP on Ryzen laptops): no MSI, not a video capture card
            if (d.Kind == PciKind.Never && d.BaseClass is 0x0C or 0x08 && Regex.IsMatch(d.Name, @"Thunderbolt|USB4", RegexOptions.IgnoreCase))
                d.Kind = PciKind.UsbHostInterface; // TB3 NHIs report class 08/80
        }

        // An audio function on the same bus and device number as a GPU is that GPU's HDMI/DP audio.
        // Same vendor too: on Ryzen 7000/9000 the board's HD Audio controller (1022) shares the iGPU's (1002) slot.
        var gpuSlots = list.Where(d => d.Kind == PciKind.Gpu && d.Bus is not null && d.Address is not null)
            .Select(d => (d.Bus!.Value, d.Address!.Value >> 16, d.VendorId)).ToHashSet();
        foreach (var d in list.Where(d => d.Kind == PciKind.Audio))
        {
            bool sameSlot = d.Bus is not null && d.Address is not null && gpuSlots.Contains((d.Bus.Value, d.Address.Value >> 16, d.VendorId));
            bool gpuVendor = d.VendorId is "1002" or "10DE" && !d.Name.Contains("Family", StringComparison.OrdinalIgnoreCase);
            if (sameSlot || (gpuSlots.Count == 0 && gpuVendor)) d.Kind = PciKind.GpuAudio;
        }
    }

    private static GpuVendor VendorOf(PciDevice d) => d.VendorId switch
    {
        "1002" => GpuVendor.Amd,
        "10DE" => GpuVendor.Nvidia,
        "8086" => GpuVendor.Intel,
        _ => GpuVendor.Other,
    };

    private static Dictionary<string, bool> NetworkUp()
    {
        var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                map[ni.Id] = ni.OperationalStatus == OperationalStatus.Up;
        }
        catch (Exception ex)
        {
            Log.Warn("NetworkInterface list failed: " + ex.Message);
        }
        return map;
    }

    private static void FillNetwork(List<PciDevice> list, Dictionary<string, bool> up)
    {
        foreach (var d in list.Where(d => d.Kind is PciKind.Ethernet or PciKind.Wifi && d.ClassKey is not null))
        {
            string key = d.ClassKey!;
            d.NetCfgInstanceId = RegistryOps.ReadString(key, "NetCfgInstanceId");
            if (d.NetCfgInstanceId is not null && up.TryGetValue(d.NetCfgInstanceId, out bool isUp)) d.Connected = isUp;
            if (RegistryOps.ReadDword(key, "*IfType") == 71) d.Kind = PciKind.Wifi;
            d.RssCapable = RegistryOps.KeyExists(key + @"\Ndi\params\*RSS");
            d.RssQueueOptions = RegistryOps.SubKeyNames(key + @"\Ndi\params\*NumRssQueues").Length > 0
                ? EnumOptions(key + @"\Ndi\params\*NumRssQueues").Select(o => o.Value).ToList()
                : new List<string>();
        }
    }

    /// <summary>Ndi\params\&lt;keyword&gt;\enum: value -> description.</summary>
    public static List<(string Value, string Text)> EnumOptions(string paramKey)
    {
        var result = new List<(string, string)>();
        string enumKey = paramKey + @"\enum";
        var (hive, sub) = RegistryOps.Split(enumKey);
        using (hive)
        {
            using var k = hive.OpenSubKey(sub, writable: false);
            if (k is null) return result;
            foreach (string name in k.GetValueNames())
                result.Add((name, k.GetValue(name)?.ToString() ?? ""));
        }
        return result.OrderBy(o => int.TryParse(o.Item1, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : int.MaxValue).ToList();
    }
}
