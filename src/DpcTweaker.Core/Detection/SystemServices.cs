using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using DpcTweaker.Core.Infra;

namespace DpcTweaker.Core.Detection;

/// <summary>A service's start type as the registry stores it (Start + DelayedAutostart).</summary>
public enum StartType { Boot, System, Auto, AutoDelayed, Manual, Disabled, Unknown }

public static class StartTypes
{
    public static StartType Parse(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "auto" or "automatic" => StartType.Auto,
        "autodelayed" or "delayed-auto" or "automatic (delayed)" => StartType.AutoDelayed,
        "manual" or "demand" => StartType.Manual,
        "disabled" => StartType.Disabled,
        _ => StartType.Unknown,
    };

    public static StartType FromRegistry(uint? start, uint? delayed) => start switch
    {
        0 => StartType.Boot,
        1 => StartType.System,
        2 => delayed == 1 ? StartType.AutoDelayed : StartType.Auto,
        3 => StartType.Manual,
        4 => StartType.Disabled,
        _ => StartType.Unknown,
    };

    /// <summary>How "active" a start type is: Disabled 0 < Manual 1 < Auto (Delayed) 2 < Auto 3.</summary>
    public static int Rank(StartType t) => t switch
    {
        StartType.Disabled => 0,
        StartType.Manual => 1,
        StartType.AutoDelayed => 2,
        StartType.Auto or StartType.Boot or StartType.System => 3,
        _ => 99,
    };

    public static string Label(StartType t) => t switch
    {
        StartType.Auto => "Automatic",
        StartType.AutoDelayed => "Automatic (delayed start)",
        StartType.Manual => "Manual",
        StartType.Disabled => "Disabled",
        StartType.Boot => "Boot",
        StartType.System => "System",
        _ => "unknown",
    };

    /// <summary>The sc.exe "start=" word.</summary>
    public static string ScWord(StartType t) => t switch
    {
        StartType.Auto => "auto",
        StartType.AutoDelayed => "delayed-auto",
        StartType.Manual => "demand",
        StartType.Disabled => "disabled",
        _ => throw new ArgumentException("No sc.exe start type for " + t),
    };
}

public sealed class ServiceInfo
{
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public string State { get; init; } = "";
    public StartType Start { get; init; } = StartType.Unknown;
    public bool TriggerStart { get; init; }
    public string Account { get; init; } = "";
    public string Path { get; init; } = "";
    public string Description { get; init; } = "";
    /// <summary>The binary's CompanyName (only read for non-Windows binaries).</summary>
    public string? Company { get; init; }
    /// <summary>A per-user instance (CDPUserSvc_957c1): listed under its template, never changed directly.</summary>
    public bool IsPerUserInstance { get; init; }
    public bool IsWindowsBinary { get; init; }

    public string RegistryKey => @"HKLM\SYSTEM\CurrentControlSet\Services\" + Name;
}

/// <summary>Hardware the "only when absent" service rules look at. Null = couldn't tell, treated as present.</summary>
public sealed class HardwareFacts
{
    public bool? Wifi { get; init; }
    public bool? Bluetooth { get; init; }
    public bool? Printer { get; init; }
    public bool? ScannerOrCamera { get; init; }
    public bool? Sensors { get; init; }
    /// <summary>A spinning disk, or a disk whose type Windows doesn't report (USB disks, RAID volumes).</summary>
    public bool? Hdd { get; init; }
    public List<string> RealPrinters { get; init; } = new();

    /// <summary>
    /// True when the hardware a service exists for is (or may be) present, so an "only if absent" rule
    /// must not apply. Anything the scan couldn't read counts as present.
    /// </summary>
    public bool Has(string? what) => what switch
    {
        "wifi" => Wifi ?? true,
        "bluetooth" => Bluetooth ?? true,
        "printer" => Printer ?? true,
        "scanner" => ScannerOrCamera ?? true,
        "sensors" => Sensors ?? true,
        "hdd" => Hdd ?? true,
        _ => true,
    };

    private static string YesNo(bool? b) => b switch { true => "yes", false => "no", _ => "unknown (treated as yes)" };

    public string Describe() =>
        $"Wi-Fi {YesNo(Wifi)}, Bluetooth {YesNo(Bluetooth)}, printer {(Printer == true ? string.Join(", ", RealPrinters) : YesNo(Printer))}, " +
        $"scanner/camera {YesNo(ScannerOrCamera)}, sensors {YesNo(Sensors)}, spinning or unidentified disk {YesNo(Hdd)}";

    public static readonly Regex VirtualPrinter = new(@"PDF|XPS|OneNote|Fax|Snagit|Send To|Microsoft Print", RegexOptions.IgnoreCase);
    private static readonly HashSet<string> VirtualPorts = new(StringComparer.OrdinalIgnoreCase) { "PORTPROMPT:", "nul:", "SHRFAX:", "XPSPort:" };

    /// <summary>
    /// Printers that look real: local ones not on a PDF/XPS/OneNote/fax driver or print-to-file port,
    /// plus network printer connections (per user, HKCU\Printers\Connections).
    /// </summary>
    public static List<string> FindRealPrinters()
    {
        const string key = @"HKLM\SYSTEM\CurrentControlSet\Control\Print\Printers";
        var list = new List<string>();
        foreach (string p in RegistryOps.SubKeyNames(key))
        {
            string port = RegistryOps.ReadString(key + "\\" + p, "Port") ?? "";
            if (VirtualPrinter.IsMatch(p) || VirtualPorts.Contains(port) || port.StartsWith("OneNote", StringComparison.OrdinalIgnoreCase)) continue;
            list.Add(p);
        }
        foreach (string c in RegistryOps.SubKeyNames(@"HKCU\Printers\Connections"))
            list.Add(c.Replace(',', '\\'));
        return list;
    }
}

public static class ServiceScanner
{
    private const string Script = """
        $list = foreach ($s in @(Get-CimInstance Win32_Service -ErrorAction SilentlyContinue)) {
            [ordered]@{ n = [string]$s.Name; d = [string]$s.DisplayName; st = [string]$s.State; a = [string]$s.StartName; p = [string]$s.PathName; ds = [string]$s.Description }
        }
        @{ s = @($list) } | ConvertTo-Json -Compress -Depth 4
        """;

    public static List<ServiceInfo> Scan()
    {
        var result = new List<ServiceInfo>();
        JsonElement root;
        try { root = PowerShellBridge.RunJson(Script, 120_000); }
        catch (Exception ex)
        {
            Log.Warn("Service scan failed: " + ex.Message);
            return result;
        }
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (var e in root.Arr("s"))
        {
            string name = e.Str("n") ?? "";
            if (name.Length == 0) continue;
            string key = @"HKLM\SYSTEM\CurrentControlSet\Services\" + name;
            uint type = RegistryOps.ReadDword(key, "Type") ?? 0;
            string path = e.Str("p") ?? "";
            string? exe = ExePath(path);
            bool windows = exe is null || exe.StartsWith(win, StringComparison.OrdinalIgnoreCase) && !exe.Contains(@"\DriverStore\", StringComparison.OrdinalIgnoreCase);
            string? company = null;
            if (!windows && exe is not null)
            {
                try { if (File.Exists(exe)) company = FileVersionInfo.GetVersionInfo(exe).CompanyName?.Trim(); }
                catch { }
            }
            result.Add(new ServiceInfo
            {
                Name = name,
                DisplayName = e.Str("d") ?? name,
                State = e.Str("st") ?? "",
                Start = StartTypes.FromRegistry(RegistryOps.ReadDword(key, "Start"), RegistryOps.ReadDword(key, "DelayedAutostart")),
                TriggerStart = RegistryOps.KeyExists(key + @"\TriggerInfo"),
                Account = e.Str("a") ?? "",
                Path = path,
                Description = e.Str("ds") ?? "",
                Company = company,
                IsPerUserInstance = (type & 0x80) != 0,
                IsWindowsBinary = windows,
            });
        }
        return result.OrderBy(s => s.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>The .exe in a service ImagePath: quoted, or up to the first ".exe".</summary>
    public static string? ExePath(string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath)) return null;
        string s = Environment.ExpandEnvironmentVariables(imagePath.Trim());
        if (s.StartsWith('"'))
        {
            int end = s.IndexOf('"', 1);
            return end > 1 ? s[1..end] : null;
        }
        int i = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return i > 0 ? s[..(i + 4)] : s.Split(' ')[0];
    }
}
