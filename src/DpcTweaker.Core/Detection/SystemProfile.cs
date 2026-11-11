using DpcTweaker.Core.Native;
using DpcTweaker.Core.Tweaks;

namespace DpcTweaker.Core.Detection;

public enum FormFactor { Desktop, Laptop }

public sealed record WindowsInfo(string ProductName, string DisplayVersion, int Build, int Ubr, string Edition)
{
    public bool IsWindows11 => Build >= 22000;

    /// <summary>GlobalTimerResolutionRequests exists on Windows 11 and Server 2022 (build 20348) and later.</summary>
    public bool SupportsGlobalTimer => Build >= 20348;

    public string Display => $"{(IsWindows11 ? ProductName.Replace("Windows 10", "Windows 11") : ProductName)} {DisplayVersion} (build {Build}.{Ubr})";
}

public enum GpuVendor { Amd, Nvidia, Intel, Other }

public sealed class GpuInfo
{
    public required string Name { get; init; }
    public required GpuVendor Vendor { get; init; }
    public required string ClassKeyPath { get; init; }
    public required string ClassSubkey { get; init; }
    public string? InstanceId { get; init; }
    public string? DriverVersion { get; init; }
    public string? DriverDate { get; init; }
    public string? ServiceName { get; init; }
    public string? DriverFile { get; init; }
    public long? MemoryBytes { get; init; }
    public bool IsIntegrated { get; init; }

    /// <summary>Key name -> does the installed driver file contain it (null = couldn't scan).</summary>
    public Dictionary<string, bool>? DriverKeyScan { get; set; }
    public string? DriverScanError { get; set; }

    public string Label => IsIntegrated ? Name + " (integrated)" : Name;
}

public sealed record SecurityInfo(int? VbsStatus, bool HvciRunning, bool? HypervisorPresent)
{
    public string Display => VbsStatus switch
    {
        null => "unknown",
        0 => "VBS off",
        1 => "VBS enabled but not running",
        2 => HvciRunning ? "VBS running, Memory Integrity on" : "VBS running",
        _ => "VBS status " + VbsStatus,
    } + (HypervisorPresent == true ? "; a hypervisor is running" : "");
}

public sealed class SoftwareInfo
{
    public bool ProcessLasso { get; init; }
    public bool AmdVCacheService { get; init; }
    public bool XboxGameBar { get; init; }
    public List<string> OtherTimerTools { get; init; } = new();
}

public sealed class SystemProfile
{
    public required WindowsInfo Windows { get; init; }
    public required string CpuName { get; init; }
    public required string CpuVendor { get; init; }
    public required CpuTopology Cpu { get; init; }
    public required FormFactor FormFactor { get; init; }
    public required string FormFactorEvidence { get; init; }
    public required IReadOnlyList<GpuInfo> Gpus { get; init; }
    public required SecurityInfo Security { get; init; }
    public (uint Min, uint Max, uint Current)? TimerResolution { get; init; }
    public int? WheaErrors14Days { get; init; }
    public required SoftwareInfo Software { get; init; }
    public string Manufacturer { get; init; } = "";
    public string Model { get; init; } = "";
    public List<string> Warnings { get; init; } = new();

    /// <summary>Every present PCI function, with what hangs off it (USB devices, audio endpoints, monitors).</summary>
    public IReadOnlyList<PciDevice> Devices { get; init; } = Array.Empty<PciDevice>();

    /// <summary>CPU sets: Windows' preferred-core ranking (SchedulingClass) per logical CPU.</summary>
    public IReadOnlyList<CpuSetEntry> CpuSets { get; init; } = Array.Empty<CpuSetEntry>();

    /// <summary>Every installed service (Win32_Service + the registry start values).</summary>
    public IReadOnlyList<ServiceInfo> Services { get; init; } = Array.Empty<ServiceInfo>();

    /// <summary>Every scheduled task, read as administrator.</summary>
    public IReadOnlyList<TaskDetails> Tasks { get; init; } = Array.Empty<TaskDetails>();

    /// <summary>Hardware the "only when absent" service rules check.</summary>
    public HardwareFacts Hardware { get; init; } = new();

    /// <summary>Physical network adapters (PCI and USB) with the properties their drivers offer.</summary>
    public IReadOnlyList<NicInfo> NetAdapters { get; init; } = Array.Empty<NicInfo>();

    public bool IsLaptop => FormFactor == FormFactor.Laptop;

    /// <summary>CPUs where Windows' own parking decides game placement (AMD dual-CCD, Intel hybrid).</summary>
    public bool ParkingDependentCpu => Cpu.IsMultiCcd && CpuVendor.Contains("AMD", StringComparison.OrdinalIgnoreCase) || Cpu.IsHybrid;

    public string CpuKind
    {
        get
        {
            if (Cpu.IsHybrid) return "Intel hybrid (P-cores and E-cores)";
            if (Cpu.IsDualCcdX3D) return "Dual-CCD X3D (V-Cache on one CCD)";
            // 7800X3D / 9800X3D: one CCD, but its L3 is the stacked one. The name says it even if
            // the cache layout is reported oddly by a given firmware.
            if (Cpu.IsSingleCcdVCache || CpuName.Contains("X3D", StringComparison.OrdinalIgnoreCase))
                return "Single CCD with 3D V-Cache";
            if (Cpu.IsMultiCcd) return $"{Cpu.L3Groups.Count} CCDs / CCXs";
            return "Single CCD / single L3";
        }
    }
}
