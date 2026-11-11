using System.Text.Json;
using System.Text.RegularExpressions;
using DpcTweaker.Core.Infra;
using DpcTweaker.Core.Native;
using DpcTweaker.Core.Tweaks;

namespace DpcTweaker.Core.Detection;

/// <summary>Builds the hardware profile. Read-only, about 5-10 seconds.</summary>
public static class Detector
{
    public const string DisplayClassKey = @"HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
    private const string DisplayClassGuid = "{4d36e968-e325-11ce-bfc1-08002be10318}";

    private static readonly HashSet<int> LaptopChassis = new() { 8, 9, 10, 11, 12, 14, 18, 21, 30, 31, 32 };
    private static readonly HashSet<int> DesktopChassis = new() { 3, 4, 5, 6, 7, 13, 15, 16, 17, 23, 24, 34, 35, 36 };

    public static SystemProfile Detect(Action<string>? progress = null)
    {
        progress?.Invoke("Reading Windows version...");
        var windows = ReadWindows();

        progress?.Invoke("Reading the CPU layout...");
        var topo = CpuTopology.Parse(Kernel.GetProcessorInformation());
        const string cpuKey = @"HKLM\HARDWARE\DESCRIPTION\System\CentralProcessor\0";
        string cpuName = (RegistryOps.ReadString(cpuKey, "ProcessorNameString") ?? "Unknown CPU").Trim();
        string cpuVendor = RegistryOps.ReadString(cpuKey, "VendorIdentifier") ?? "";
        cpuVendor = cpuVendor.Contains("AMD", StringComparison.OrdinalIgnoreCase) ? "AMD"
                  : cpuVendor.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? "Intel"
                  : cpuVendor;

        progress?.Invoke("Asking Windows about the chassis, security and devices (WMI)...");
        JsonElement wmi = default;
        string? wmiError = null;
        try { wmi = PowerShellBridge.RunJson(WmiScript, 90_000); }
        catch (Exception ex) { wmiError = ex.Message; Log.Warn("WMI bundle failed: " + ex.Message); }
        bool haveWmi = wmi.ValueKind == JsonValueKind.Object;

        var chassis = haveWmi ? wmi.Arr("chassis").Select(x => x.TryGetInt32(out int v) ? v : 0).Where(v => v > 0).ToList() : new List<int>();
        int pcSystemType = haveWmi ? (int)(wmi.Num("pcSystemType") ?? 0) : 0;
        bool? battery = Kernel.HasBattery();
        int role = PowerApi.PlatformRole();
        var (form, evidence) = DecideFormFactor(chassis, pcSystemType, battery, role);

        progress?.Invoke("Finding GPUs and their drivers...");
        var presentDisplays = haveWmi
            ? wmi.Arr("display").Select(x => x.GetString() ?? "").Where(s => s.Length > 0).Select(s => s.ToUpperInvariant()).ToHashSet()
            : null;
        var gpus = FindGpus(presentDisplays);

        foreach (var g in gpus.Where(g => g.Vendor == GpuVendor.Amd && g.DriverFile is not null))
        {
            progress?.Invoke($"Checking which keys {Path.GetFileName(g.DriverFile)} reads...");
            try { g.DriverKeyScan = DriverStringScanner.Scan(g.DriverFile!, AmdGpuKeys.AllNames()); }
            catch (Exception ex) { g.DriverScanError = ex.Message; }
        }

        progress?.Invoke("Mapping PCI devices and what's plugged into them...");
        var warningsEarly = new List<string>();
        List<PciDevice> devices;
        Dictionary<Guid, int> classCounts = new();
        bool treeOk = true;
        try { devices = PciScanner.Scan(gpus, out classCounts); }
        catch (Exception ex) { treeOk = false; devices = new List<PciDevice>(); warningsEarly.Add("The device tree couldn't be read, so the Interrupts screen is empty: " + ex.Message); Log.Warn("PCI scan failed: " + ex); }

        progress?.Invoke("Reading network adapters and their driver properties...");
        List<NicInfo> nics;
        try { nics = NicScanner.Scan(); }
        catch (Exception ex) { nics = new List<NicInfo>(); warningsEarly.Add("Network adapters couldn't be read: " + ex.Message); Log.Warn("NIC scan failed: " + ex); }

        var cpuSets = SysInfo.CpuSets();

        progress?.Invoke("Listing services and scheduled tasks...");
        List<ServiceInfo> services;
        try { services = ServiceScanner.Scan(); }
        catch (Exception ex) { services = new(); warningsEarly.Add("Services couldn't be listed: " + ex.Message); }
        List<TaskDetails> tasks;
        try { tasks = TaskScanner.Scan(); }
        catch (Exception ex) { tasks = new(); warningsEarly.Add("Scheduled tasks couldn't be listed: " + ex.Message); }

        bool? Present(params Guid[] classes) => treeOk ? classes.Sum(g => classCounts.TryGetValue(g, out int c) ? c : 0) > 0 : null;
        List<string>? printers = null;
        try { printers = HardwareFacts.FindRealPrinters(); } catch (Exception ex) { Log.Warn("Printer list failed: " + ex.Message); }

        var security = new SecurityInfo(
            haveWmi ? (int?)wmi.Num("vbs") : null,
            haveWmi && wmi.Arr("secRunning").Any(x => x.TryGetInt32(out int v) && v == 2),
            haveWmi ? wmi.Bool("hypervisor") : null);

        var software = new SoftwareInfo
        {
            ProcessLasso = haveWmi && (wmi.Bool("lasso") ?? false),
            AmdVCacheService = haveWmi && (wmi.Bool("vcache") ?? false),
            XboxGameBar = haveWmi && (wmi.Bool("gamebar") ?? false),
            OtherTimerTools = haveWmi ? wmi.Arr("timerTools").Select(x => x.GetString() ?? "").Where(s => s.Length > 0).ToList() : new(),
        };

        var warnings = new List<string>(warningsEarly);
        if (wmiError is not null) warnings.Add("WMI queries failed, so form factor, VBS and WHEA are guesses: " + wmiError);
        if (topo.MultipleProcessorGroups) warnings.Add("More than 64 logical CPUs (several processor groups): interrupt changes stay read-only in v1.");
        int? whea = haveWmi ? (int?)wmi.Num("whea") : null;
        if (whea > 0) warnings.Add($"{whea} WHEA hardware error(s) in the last 14 days. Each corrected error costs an interrupt and a DPC: fix stability (overclock, undervolt, PCIe riser) before tuning.");
        if (security.VbsStatus == 2) warnings.Add("Virtualization-based security is running. It adds overhead to every interrupt; the app reports it but never turns it off.");

        return new SystemProfile
        {
            Windows = windows,
            CpuName = cpuName,
            CpuVendor = cpuVendor,
            Cpu = topo,
            FormFactor = form,
            FormFactorEvidence = evidence,
            Gpus = gpus,
            Security = security,
            TimerResolution = Kernel.TimerResolution(),
            WheaErrors14Days = whea,
            Software = software,
            Manufacturer = haveWmi ? wmi.Str("manufacturer") ?? "" : "",
            Model = haveWmi ? wmi.Str("model") ?? "" : "",
            Warnings = warnings,
            Devices = devices,
            CpuSets = cpuSets,
            NetAdapters = nics,
            Services = services,
            Tasks = tasks,
            Hardware = new HardwareFacts
            {
                Wifi = treeOk ? devices.Any(d => d.Kind == PciKind.Wifi && d.Started) || nics.Any(n => n.IsWifi && n.Started) : null,
                Bluetooth = Present(PciScanner.BluetoothClass),
                Printer = printers is null ? null : printers.Count > 0,
                RealPrinters = printers ?? new(),
                ScannerOrCamera = Present(PciScanner.ImageClass, PciScanner.CameraClass),
                Sensors = Present(PciScanner.SensorClass),
                Hdd = haveWmi && wmi.Num("hdd") is long hdd ? hdd > 0 : null,
            },
        };
    }

    private const string WmiScript = """
        $o = [ordered]@{}
        $enc = Get-CimInstance Win32_SystemEnclosure -ErrorAction SilentlyContinue | Select-Object -First 1
        $o.chassis = @($enc.ChassisTypes | ForEach-Object { [int]$_ })
        $cs = Get-CimInstance Win32_ComputerSystem -ErrorAction SilentlyContinue
        $o.pcSystemType = [int]$cs.PCSystemType
        $o.manufacturer = [string]$cs.Manufacturer
        $o.model = [string]$cs.Model
        $o.hypervisor = [bool]$cs.HypervisorPresent
        $dg = Get-CimInstance -Namespace root\Microsoft\Windows\DeviceGuard -ClassName Win32_DeviceGuard -ErrorAction SilentlyContinue
        if ($dg) {
            $o.vbs = [int]$dg.VirtualizationBasedSecurityStatus
            $o.secRunning = @($dg.SecurityServicesRunning | ForEach-Object { [int]$_ })
        }
        $o.display = @(Get-PnpDevice -PresentOnly -Class Display -ErrorAction SilentlyContinue | ForEach-Object { [string]$_.InstanceId })
        try {
            $o.whea = @(Get-WinEvent -FilterHashtable @{ LogName = 'System'; ProviderName = 'Microsoft-Windows-WHEA-Logger'; StartTime = (Get-Date).AddDays(-14) } -ErrorAction Stop).Count
        } catch { $o.whea = 0 }
        $o.lasso = (Test-Path "$env:ProgramFiles\Process Lasso") -or [bool](Get-Process -Name processgovernor -ErrorAction SilentlyContinue)
        $o.vcache = [bool](Get-Service -Name amd3dvcacheSvc -ErrorAction SilentlyContinue)
        $o.gamebar = [bool](Get-AppxPackage -Name Microsoft.XboxGamingOverlay -ErrorAction SilentlyContinue)
        try { $o.hdd = @(Get-CimInstance -Namespace root\Microsoft\Windows\Storage -ClassName MSFT_PhysicalDisk -ErrorAction Stop | Where-Object { $_.MediaType -eq 3 -or $_.MediaType -eq 0 }).Count } catch { }
        $o.timerTools = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -match 'SetTimerResolution|TimerResolution|ISLC|TimerTool' } | ForEach-Object { [string]$_.ProcessName } | Select-Object -Unique)
        $o | ConvertTo-Json -Compress -Depth 4
        """;

    internal static (FormFactor, string) DecideFormFactor(List<int> chassis, int pcSystemType, bool? battery, int role)
    {
        string ev = $"chassis types [{string.Join(",", chassis)}], PCSystemType {pcSystemType}, battery {(battery is null ? "unknown" : battery.Value ? "present" : "none")}, platform role {role}";
        if (chassis.Any(LaptopChassis.Contains)) return (FormFactor.Laptop, "Laptop: " + ev);
        if (chassis.Any(DesktopChassis.Contains)) return (FormFactor.Desktop, "Desktop: " + ev);
        if (pcSystemType == 2 || role is 2 or 8 || battery == true) return (FormFactor.Laptop, "Laptop (no clear chassis type): " + ev);
        return (FormFactor.Desktop, "Desktop (no clear chassis type): " + ev);
    }

    private static WindowsInfo ReadWindows()
    {
        const string k = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        int.TryParse(RegistryOps.ReadString(k, "CurrentBuildNumber"), out int build);
        int ubr = (int)(RegistryOps.ReadDword(k, "UBR") ?? 0);
        return new WindowsInfo(
            RegistryOps.ReadString(k, "ProductName") ?? "Windows",
            RegistryOps.ReadString(k, "DisplayVersion") ?? RegistryOps.ReadString(k, "ReleaseId") ?? "",
            build,
            ubr,
            RegistryOps.ReadString(k, "EditionID") ?? "");
    }

    /// <summary>Display-class driver keys, matched to their PCI device instance, service and driver file.</summary>
    public static List<GpuInfo> FindGpus(HashSet<string>? presentInstanceIds)
    {
        // Driver reference "{class}\0000" -> (instance id, service)
        var byDriverRef = new Dictionary<string, (string Instance, string? Service)>(StringComparer.OrdinalIgnoreCase);
        const string enumPci = @"HKLM\SYSTEM\CurrentControlSet\Enum\PCI";
        foreach (string hwid in RegistryOps.SubKeyNames(enumPci))
        {
            foreach (string inst in RegistryOps.SubKeyNames(enumPci + "\\" + hwid))
            {
                string key = enumPci + "\\" + hwid + "\\" + inst;
                string? drv = RegistryOps.ReadString(key, "Driver");
                if (drv is null || !drv.StartsWith(DisplayClassGuid, StringComparison.OrdinalIgnoreCase)) continue;
                byDriverRef[drv] = ("PCI\\" + hwid + "\\" + inst, RegistryOps.ReadString(key, "Service"));
            }
        }

        var list = new List<GpuInfo>();
        foreach (string sub in RegistryOps.SubKeyNames(DisplayClassKey).Where(s => Regex.IsMatch(s, @"^\d{4}$")))
        {
            string key = DisplayClassKey + "\\" + sub;
            string? desc = RegistryOps.ReadString(key, "DriverDesc");
            if (desc is null) continue;

            byDriverRef.TryGetValue(DisplayClassGuid + "\\" + sub, out var dev);
            if (dev.Instance is null) continue; // no PCI device (virtual/basic display or a leftover key)
            if (presentInstanceIds is not null && !presentInstanceIds.Contains(dev.Instance.ToUpperInvariant())) continue;

            string id = (RegistryOps.ReadString(key, "MatchingDeviceId") ?? dev.Instance).ToUpperInvariant();
            GpuVendor vendor = id.Contains("VEN_1002") ? GpuVendor.Amd
                             : id.Contains("VEN_10DE") ? GpuVendor.Nvidia
                             : id.Contains("VEN_8086") ? GpuVendor.Intel
                             : GpuVendor.Other;

            long? mem = null;
            var qw = RegistryOps.Read(key, "HardwareInformation.qwMemorySize");
            if (qw.Exists && qw.Kind == Microsoft.Win32.RegistryValueKind.QWord) mem = (long)qw.AsQword();
            else if (qw.Exists && qw.Value is byte[] qb && qb.Length >= 8) mem = BitConverter.ToInt64(qb, 0);

            list.Add(new GpuInfo
            {
                Name = desc,
                Vendor = vendor,
                ClassKeyPath = key,
                ClassSubkey = sub,
                InstanceId = dev.Instance,
                DriverVersion = RegistryOps.ReadString(key, "DriverVersion"),
                DriverDate = RegistryOps.ReadString(key, "DriverDate"),
                ServiceName = dev.Service,
                DriverFile = dev.Service is null ? null : ResolveDriverFile(dev.Service),
                MemoryBytes = mem,
                IsIntegrated = LooksIntegrated(vendor, desc),
            });
        }
        return list;
    }

    internal static bool LooksIntegrated(GpuVendor vendor, string name)
    {
        switch (vendor)
        {
            case GpuVendor.Nvidia:
                return false;
            case GpuVendor.Amd:
                if (Regex.IsMatch(name, @"\bRX\b|Radeon Pro|Radeon PRO|Vega (56|64)|Radeon VII|FirePro|Instinct", RegexOptions.IgnoreCase)) return false;
                return Regex.IsMatch(name, @"Graphics|\d{3}M\b", RegexOptions.IgnoreCase);
            case GpuVendor.Intel:
                return !Regex.IsMatch(name, @"Arc(\(TM\))?\s+[AB]\d{3}", RegexOptions.IgnoreCase);
            default:
                return false;
        }
    }

    private static string? ResolveDriverFile(string service)
    {
        string? image = RegistryOps.ReadString(@"HKLM\SYSTEM\CurrentControlSet\Services\" + service, "ImagePath");
        if (string.IsNullOrWhiteSpace(image)) return null;
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        image = image.Trim().Trim('"');
        if (image.StartsWith(@"\??\")) image = image[4..];
        if (image.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase)) image = Path.Combine(win, image[12..]);
        else if (image.StartsWith(@"System32\", StringComparison.OrdinalIgnoreCase)) image = Path.Combine(win, image);
        image = Environment.ExpandEnvironmentVariables(image);
        return File.Exists(image) ? image : null;
    }
}
