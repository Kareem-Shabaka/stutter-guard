using System.Text.RegularExpressions;
using DpcTweaker.Core.Detection;
using DpcTweaker.Core.Infra;
using DpcTweaker.Core.Model;
using DpcTweaker.Core.Native;
using Microsoft.Win32;

namespace DpcTweaker.Core.Tweaks;

/// <summary>One advanced property the driver offers (Ndi\params\&lt;keyword&gt;).</summary>
public sealed record NicParam(string Keyword, string Description, string? Default, List<(string Value, string Text)> Options);

/// <summary>A physical network adapter (PCI or USB), read from its driver key.</summary>
public sealed class NicInfo
{
    public required string Name { get; init; }
    public required string ClassKey { get; init; }
    public required string InstanceId { get; init; }
    public string? NetCfgInstanceId { get; init; }
    public bool IsWifi { get; init; }
    public bool? Connected { get; set; }
    /// <summary>The device is running (not disabled in Device Manager, driver loaded).</summary>
    public bool Started { get; init; }
    public List<NicParam> Params { get; init; } = new();
    /// <summary>Values in the driver key with no Ndi\params entry (hidden keywords such as Realtek's ASPM).</summary>
    public HashSet<string> HiddenValues { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public NicParam? Param(string keyword) => Params.FirstOrDefault(p => string.Equals(p.Keyword, keyword, StringComparison.OrdinalIgnoreCase));
}

public static class NicScanner
{
    public const string NetClassKey = @"HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";

    public static List<NicInfo> Scan()
    {
        var up = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                up[ni.Id] = ni.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up;
        }
        catch { }

        var list = new List<NicInfo>();
        foreach (string sub in RegistryOps.SubKeyNames(NetClassKey).Where(s => Regex.IsMatch(s, @"^\d{4}$")))
        {
            string key = NetClassKey + "\\" + sub;
            uint ifType = RegistryOps.ReadDword(key, "*IfType") ?? 0;
            uint chars = RegistryOps.ReadDword(key, "Characteristics") ?? 0;
            string? inst = RegistryOps.ReadString(key, "DeviceInstanceID");
            if ((chars & 0x4) == 0 || inst is null) continue;                 // NCF_PHYSICAL only
            if (ifType is not (6 or 71)) continue;                            // Ethernet or Wi-Fi
            if (!inst.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase) && !inst.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase)) continue;
            if (CfgMgr.Locate(inst) is not uint dn) continue;                 // not present

            string? guid = RegistryOps.ReadString(key, "NetCfgInstanceId");
            var nic = new NicInfo
            {
                Name = RegistryOps.ReadString(key, "DriverDesc") ?? inst,
                ClassKey = key,
                InstanceId = inst,
                NetCfgInstanceId = guid,
                IsWifi = ifType == 71,
                Started = (CfgMgr.Status(dn).Status & CfgMgr.DN_STARTED) != 0,
                Connected = guid is not null && up.TryGetValue(guid, out bool u) ? u : null,
            };

            string ndi = key + @"\Ndi\params";
            foreach (string kw in RegistryOps.SubKeyNames(ndi))
            {
                string pk = ndi + "\\" + kw;
                nic.Params.Add(new NicParam(kw, RegistryOps.ReadString(pk, "ParamDesc") ?? kw,
                    RegistryOps.ReadString(pk, "default"), PciScanner.EnumOptions(pk)));
            }

            var (hive, path) = RegistryOps.Split(key);
            using (hive)
            {
                using var k = hive.OpenSubKey(path);
                if (k is not null)
                    foreach (string v in k.GetValueNames())
                        if (nic.Param(v) is null) nic.HiddenValues.Add(v);
            }
            list.Add(nic);
        }
        return list;
    }
}

/// <summary>A known power-saving keyword and what "off" is.</summary>
public sealed record NicRule(
    string Id, string[] Keywords, string Title, string What, string Why, Evidence Evidence,
    Risk Risk = Risk.Low, bool OnForDesktop = true, bool OnForLaptop = true, bool HiddenOnly = false, string Cost = "A little power at idle.",
    string CanBreak = "Nothing known.");

/// <summary>One advanced property of one adapter, set to its "off" value.</summary>
public sealed class NicPropertyTweak : TweakBase, IBatchFinisher
{
    private readonly NicInfo _nic;
    private readonly string _keyword;
    private readonly string _target;
    private readonly NicParam? _param;

    public NicPropertyTweak(TweakInfo info, Journal journal, NicInfo nic, string keyword, string target) : base(info, journal)
    {
        _nic = nic;
        _keyword = keyword;
        _target = target;
        _param = nic.Param(keyword);
    }

    public string FinishKey => "nic-restart:" + (_nic.NetCfgInstanceId ?? _nic.InstanceId);

    private string Text(string? value)
    {
        if (value is null) return "not set";
        var o = _param?.Options.FirstOrDefault(x => x.Value == value);
        return o?.Text is { Length: > 0 } t ? $"{t} ({value})" : value;
    }

    protected override Probe Read()
    {
        string? cur = RegWrite.AsText(RegistryOps.Read(_nic.ClassKey, _keyword));
        string current = cur is null ? $"driver default: {Text(_param?.Default)}" : Text(cur);
        bool ok = (cur ?? _param?.Default) == _target;
        return new Probe(ok, current, Text(_target));
    }

    public override void Apply(TweakContext ctx)
    {
        ctx.Backup.ExportKeyOnce(_nic.ClassKey);
        RegWrite.Set(ctx, Info.Id, _nic.ClassKey, _keyword, RegistryValueKind.String, _target);
    }

    /// <summary>The driver reads its properties when it starts: restart the adapter once (2-5 s offline).</summary>
    public void Finish()
    {
        if (_nic.NetCfgInstanceId is null) throw new InvalidOperationException("Restart the adapter or Windows to use the new values.");
        string script = $$"""
            $a = Get-NetAdapter -IncludeHidden -ErrorAction SilentlyContinue | Where-Object { $_.InterfaceGuid -eq {{PowerShellBridge.Quote(_nic.NetCfgInstanceId)}} } | Select-Object -First 1
            if ($a) { Restart-NetAdapter -InputObject $a -Confirm:$false -ErrorAction Stop; '{"ok":true}' } else { '{"ok":false}' }
            """;
        var r = PowerShellBridge.RunJson(script, 60_000);
        if (r.Bool("ok") != true) throw new InvalidOperationException("Couldn't restart " + _nic.Name + ": restart Windows to use the new values.");
        Log.Info("Restarted adapter " + _nic.Name);
    }
}

public static class NetworkModule
{
    public const string ModuleName = "Network";

    private const string ProbeCost = "A little power at idle.";

    public static readonly NicRule[] Rules =
    {
        new("eee", new[] { "*EEE" }, "Energy-Efficient Ethernet off",
            "Energy-Efficient Ethernet (802.3az, Microsoft's standard *EEE keyword) lets the link sleep between packets.",
            "Waking the link costs latency on the next packet. Off on the desktop's RTL8126.", Evidence.Measured),
        new("advanced-eee", new[] { "AdvancedEEE" }, "Advanced EEE off (Realtek)",
            "Realtek's extra link power saving on top of EEE.", "Same as EEE: link wake-ups add latency. Off on the desktop.", Evidence.Measured),
        new("green", new[] { "EnableGreenEthernet", "GreenEthernet" }, "Green Ethernet off",
            "Lowers PHY power on short cables and idle links.", "Vendor PHY power saving. Off on the desktop.", Evidence.Measured),
        new("gigalite", new[] { "GigaLite", "EnableGigaLite" }, "Gigabit Lite off (Realtek)",
            "Lets the card drop to a lower-power gigabit mode.", "Vendor link power saving. Off on the desktop.", Evidence.Measured),
        new("auto-disable-gigabit", new[] { "AutoDisableGigabit" }, "Auto Disable Gigabit off (Realtek)",
            "Realtek's link speed power-down when on battery or re-linking.", "Vendor link power saving.", Evidence.Documented),
        new("power-saving-mode", new[] { "PowerSavingMode" }, "Power Saving Mode off",
            "The card's own power saving while the link is idle.", "Vendor power saving. Off on the desktop.", Evidence.Measured),
        new("aspm", new[] { "ASPM" }, "Card-side PCIe link power saving off (ASPM = 0, hidden keyword)",
            "A hidden Realtek keyword: PCIe link power states on the card itself.", "Link wake-ups add latency. Read 0 on the desktop's RTL8126.",
            Evidence.Measured, HiddenOnly: true),
        new("clkreq", new[] { "CLKREQ" }, "Card-side PCIe clock request off (CLKREQ = 0, hidden keyword)",
            "A hidden Realtek keyword: lets the card stop its PCIe reference clock.", "Clock restarts add latency. Read 0 on the desktop's RTL8126.",
            Evidence.Measured, HiddenOnly: true),
        new("selective-suspend", new[] { "*SelectiveSuspend" }, "Selective Suspend off",
            "Microsoft's standard keyword: the card powers down when idle.", "Waking the card costs latency.", Evidence.Documented),
        new("sleep-on-disconnect", new[] { "*DeviceSleepOnDisconnect" }, "Device Sleep on Disconnect off",
            "Microsoft's standard keyword: the card powers down while the cable is unplugged.", "Only matters while unplugged; harmless to turn off.", Evidence.Documented),
        new("flow-control", new[] { "*FlowControl" }, "Flow Control off",
            "Pause frames let the switch or router stall the card's receive.", "Pause frames can hold packets back. Common practice, low risk.",
            Evidence.Anecdotal, Cost: "None.", CanBreak: "On a congested network a few more packets may be dropped instead of paused."),
        new("interrupt-moderation", new[] { "*InterruptModeration" }, "Interrupt Moderation off",
            "The card raises an interrupt per packet batch at once instead of waiting to coalesce several.",
            "Lower latency per packet; Microsoft's low-latency NIC guidance. The extra interrupts land on the adapter's own cores (Interrupts screen). Off on the desktop.",
            Evidence.Measured, OnForLaptop: false, Cost: "More interrupts under heavy download (thousands per second), so more CPU time on the adapter's cores.",
            CanBreak: "Nothing breaks. On a laptop it costs battery, so it's unticked there."),
        new("ulp", new[] { "ULPMode" }, "Ultra Low Power mode off (Intel)",
            "Intel's deepest idle state for the PHY.", "Vendor power saving. Not yet verified against current Intel driver INFs.", Evidence.Anecdotal,
            OnForDesktop: false, OnForLaptop: false),
        new("sips", new[] { "SipsEnabled" }, "System Idle Power Saver off (Intel)",
            "Intel's idle power saving.", "Vendor power saving. Not yet verified against current Intel driver INFs.", Evidence.Anecdotal,
            OnForDesktop: false, OnForLaptop: false),
    };

    private static readonly Regex LooksLikePower = new(@"energy|green|power\s*sav|idle\s*power|ultra\s*low\s*power|\bEEE\b|sleep", RegexOptions.IgnoreCase);
    private static readonly Regex OffText = new(@"^\s*(disabled?|off)\s*$", RegexOptions.IgnoreCase);

    /// <summary>The value meaning "off" for a parameter: the option named Disabled/Off, else "0" if offered.</summary>
    public static string? OffValue(NicParam p)
    {
        var named = p.Options.FirstOrDefault(o => OffText.IsMatch(o.Text));
        if (named.Value is not null) return named.Value;
        return p.Options.Any(o => o.Value == "0") ? "0" : null;
    }

    public static IEnumerable<ITweak> Build(SystemProfile profile, Journal journal)
    {
        foreach (var nic in profile.NetAdapters.Where(n => !n.IsWifi))
        {
            string group = nic.Name + (nic.Connected == true ? " (connected)" : nic.Connected == false ? " (not connected)" : "");
            var handled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rule in Rules)
            {
                foreach (string kw in rule.Keywords)
                {
                    string? target;
                    if (rule.HiddenOnly)
                        target = nic.HiddenValues.Contains(kw) ? "0" : null;
                    else
                        target = nic.Param(kw) is { } p ? OffValue(p) : null;
                    if (target is null) continue;
                    handled.Add(kw);
                    yield return new NicPropertyTweak(new TweakInfo
                    {
                        Id = $"nic.{nic.NetCfgInstanceId ?? nic.InstanceId}.{kw}",
                        Module = ModuleName,
                        Group = group,
                        Title = rule.Title,
                        What = rule.What + $" Registry keyword {kw}.",
                        Why = rule.Why,
                        Cost = rule.Cost,
                        CanBreak = rule.CanBreak + " Applying restarts the adapter once (2-5 s offline).",
                        Evidence = rule.Evidence,
                        Risk = rule.Risk,
                        DefaultSelected = profile.IsLaptop ? rule.OnForLaptop : rule.OnForDesktop,
                    }, journal, nic, kw, target);
                    break;
                }
            }

            // Anything else that looks like power saving: shown, never ticked.
            foreach (var p in nic.Params.Where(p => !handled.Contains(p.Keyword) && LooksLikePower.IsMatch(p.Description + " " + p.Keyword)))
            {
                if (Regex.IsMatch(p.Keyword, @"Wake|WoL|PME|\*PM|Shutdown|S5", RegexOptions.IgnoreCase)) continue; // wake features stay as they are
                string? off = OffValue(p);
                if (off is null) continue;
                yield return new NicPropertyTweak(new TweakInfo
                {
                    Id = $"nic.{nic.NetCfgInstanceId ?? nic.InstanceId}.{p.Keyword}",
                    Module = ModuleName,
                    Group = group,
                    Title = $"{p.Description} off (looks like power saving)",
                    What = $"The driver's \"{p.Description}\" property (keyword {p.Keyword}). It isn't in the app's verified list.",
                    Why = "Its name suggests runtime power saving. Not measured: try it and compare in LatencyMon.",
                    Cost = ProbeCost,
                    CanBreak = "Unknown for this driver. Applying restarts the adapter once (2-5 s offline).",
                    Evidence = Evidence.Anecdotal,
                    Risk = Risk.Medium,
                    DefaultSelected = false,
                }, journal, nic, p.Keyword, off);
            }
        }
    }
}
