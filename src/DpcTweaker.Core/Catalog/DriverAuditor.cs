using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using DpcTweaker.Core.Detection;
using DpcTweaker.Core.Infra;

namespace DpcTweaker.Core.Catalog;

public enum DriverActionKind
{
    Update,
    Rollback,
    ReconfigureOrRemove,
    None
}

public sealed class DriverAdvisory
{
    public required BadDriverEntry Entry { get; init; }
    public required string DeviceName { get; init; }
    public required string HardwareId { get; init; }
    public required string InstalledVersion { get; init; }
    public required string DriverFile { get; init; }
    public required DriverActionKind Action { get; init; }
    public required string ActionText { get; init; }
    public bool IsBugged { get; init; }
}

public static class DriverAuditor
{
    public static List<DriverAdvisory> Audit(SystemProfile profile)
    {
        var catalog = Catalogs.BadDrivers;
        if (catalog.Count == 0) return new List<DriverAdvisory>();

        var advisories = new List<DriverAdvisory>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Audit GPUs
        foreach (var gpu in profile.Gpus)
        {
            string hwid = gpu.InstanceId ?? "";
            string drvVer = gpu.DriverVersion ?? "";
            string drvFile = gpu.DriverFile is not null
                ? Path.GetFileName(gpu.DriverFile)
                : (gpu.ServiceName is not null ? gpu.ServiceName + ".sys" : "");

            AuditTarget(catalog, gpu.Name, hwid, drvVer, drvFile, gpu.ServiceName, advisories, seenKeys);
        }

        // 2. Audit PCI Devices
        foreach (var dev in profile.Devices)
        {
            string hwid = dev.InstanceId;
            string drvVer = dev.DriverVersion ?? "";
            string drvFile = dev.DriverFileName ?? (dev.Service is not null ? dev.Service + ".sys" : "");

            AuditTarget(catalog, dev.Name, hwid, drvVer, drvFile, dev.Service, advisories, seenKeys);
        }

        return advisories;
    }

    private static void AuditTarget(
        IReadOnlyList<BadDriverEntry> catalog,
        string deviceName,
        string hwid,
        string drvVer,
        string drvFile,
        string? service,
        List<DriverAdvisory> advisories,
        HashSet<string> seenKeys)
    {
        BadDriverEntry? matchedClean = null;

        foreach (var entry in catalog)
        {
            if (!IsHardwareMatch(entry, hwid, drvFile, service))
                continue;

            bool bugged = IsVersionBugged(drvVer, entry);
            if (bugged)
            {
                string dedupKey = $"BUGGED:{entry.DriverFile}:{hwid}:{drvVer}";
                if (seenKeys.Add(dedupKey))
                {
                    var (action, actionText) = DetermineAction(drvVer, entry, true);
                    advisories.Add(new DriverAdvisory
                    {
                        Entry = entry,
                        DeviceName = deviceName,
                        HardwareId = hwid,
                        InstalledVersion = string.IsNullOrWhiteSpace(drvVer) ? "(unknown)" : drvVer,
                        DriverFile = string.IsNullOrWhiteSpace(drvFile) ? entry.DriverFile : drvFile,
                        Action = action,
                        ActionText = actionText,
                        IsBugged = true
                    });
                }
            }
            else if (matchedClean is null)
            {
                matchedClean = entry;
            }
        }

        // If no bugged match occurred, but the device belongs to a catalogued driver family, record clean advisory
        if (matchedClean is not null && !advisories.Any(a => string.Equals(a.HardwareId, hwid, StringComparison.OrdinalIgnoreCase) && a.IsBugged))
        {
            string dedupKey = $"CLEAN:{matchedClean.DriverFile}:{hwid}:{drvVer}";
            if (seenKeys.Add(dedupKey))
            {
                var (action, actionText) = DetermineAction(drvVer, matchedClean, false);
                advisories.Add(new DriverAdvisory
                {
                    Entry = matchedClean,
                    DeviceName = deviceName,
                    HardwareId = hwid,
                    InstalledVersion = string.IsNullOrWhiteSpace(drvVer) ? "(unknown)" : drvVer,
                    DriverFile = string.IsNullOrWhiteSpace(drvFile) ? matchedClean.DriverFile : drvFile,
                    Action = action,
                    ActionText = actionText,
                    IsBugged = false
                });
            }
        }
    }

    public static bool IsHardwareMatch(BadDriverEntry entry, string hwid, string drvFile, string? service)
    {
        bool fileMatch = !string.IsNullOrEmpty(entry.DriverFile) &&
                         (string.Equals(entry.DriverFile, drvFile, StringComparison.OrdinalIgnoreCase) ||
                          (service is not null && string.Equals(entry.DriverFile, service + ".sys", StringComparison.OrdinalIgnoreCase)));

        if (!fileMatch)
        {
            if (string.IsNullOrEmpty(entry.HardwareIdPattern) || entry.HardwareIdPattern == "*")
                return false;
            return MatchesHwidPattern(entry.HardwareIdPattern, hwid);
        }

        if (string.IsNullOrEmpty(entry.HardwareIdPattern) || entry.HardwareIdPattern == "*")
            return true;

        return MatchesHwidPattern(entry.HardwareIdPattern, hwid);
    }

    public static bool MatchesHwidPattern(string pattern, string hwid)
    {
        if (string.IsNullOrWhiteSpace(hwid)) return false;

        string cleanPattern = pattern.Trim();
        if (cleanPattern == "*") return true;

        try
        {
            if (GlobCatalog<object>.ToRegex(cleanPattern).IsMatch(hwid))
                return true;
        }
        catch { }

        string prefix = cleanPattern.TrimEnd('*');
        if (hwid.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return true;

        var mVenDev = Regex.Match(cleanPattern, @"(VEN_[0-9A-F]{4}&DEV_[0-9A-F]{4})", RegexOptions.IgnoreCase);
        if (mVenDev.Success)
        {
            // If pattern specifies both vendor and device, both must match
            return hwid.Contains(mVenDev.Groups[1].Value, StringComparison.OrdinalIgnoreCase);
        }

        var mDev = Regex.Match(cleanPattern, @"DEV_([0-9A-F]{4})", RegexOptions.IgnoreCase);
        if (mDev.Success)
        {
            // If pattern specifies device, it must match
            return hwid.Contains(mDev.Groups[1].Value, StringComparison.OrdinalIgnoreCase);
        }

        var mVen = Regex.Match(cleanPattern, @"VEN_([0-9A-F]{4})", RegexOptions.IgnoreCase);
        if (mVen.Success)
        {
            return hwid.Contains(mVen.Groups[1].Value, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    public static bool IsVersionBugged(string installedVer, BadDriverEntry entry)
    {
        if (string.IsNullOrWhiteSpace(installedVer)) return false;

        string normInstalled = NormalizeVersion(installedVer, entry.DriverFile);

        foreach (var b in entry.BuggedVersions)
        {
            string normBugged = NormalizeVersion(b, entry.DriverFile);
            if (string.Equals(normInstalled, normBugged, StringComparison.OrdinalIgnoreCase))
                return true;

            if (installedVer.EndsWith(b, StringComparison.OrdinalIgnoreCase) ||
                installedVer.StartsWith(b, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public static string NormalizeVersion(string ver, string? driverFile = null)
    {
        if (string.IsNullOrWhiteSpace(ver)) return "";
        string v = ver.Trim();

        // Handle NVIDIA driver: 32.0.15.3118 or 31.0.15.3598 -> 531.18 or 535.98
        bool isNvidia = (driverFile is not null && driverFile.Contains("nv", StringComparison.OrdinalIgnoreCase)) ||
                        v.StartsWith("30.") || v.StartsWith("31.") || v.StartsWith("32.");

        if (isNvidia)
        {
            var parts = v.Split('.');
            if (parts.Length == 4 && parts[2].Length >= 2 && parts[2].StartsWith("1") && parts[3].Length >= 4)
            {
                string major = parts[2][1..] + parts[3][..2];
                string minor = parts[3][2..];
                return $"{major}.{minor}";
            }
        }

        return v;
    }

    public static (DriverActionKind Action, string ActionText) DetermineAction(string installedVer, BadDriverEntry entry, bool isBugged)
    {
        if (!isBugged)
        {
            return (DriverActionKind.None, $"Clean: Installed version {installedVer} is not in the known regression catalog.");
        }

        if (entry.RecommendedVersion.Contains("remove", StringComparison.OrdinalIgnoreCase) ||
            entry.RecommendedVersion.Contains("disable", StringComparison.OrdinalIgnoreCase))
        {
            return (DriverActionKind.ReconfigureOrRemove, $"Remove, disable, or replace with generic driver: {entry.RecommendedVersion}");
        }

        var m = Regex.Match(entry.RecommendedVersion, @"\b\d+(?:\.\d+)+\b");
        if (m.Success && TryCompareVersions(installedVer, m.Value, entry.DriverFile, out int cmp))
        {
            if (cmp < 0)
            {
                return (DriverActionKind.Update, $"Update driver to {entry.RecommendedVersion} (or newer)");
            }
            else if (cmp > 0)
            {
                return (DriverActionKind.Rollback, $"Roll back driver to {entry.RecommendedVersion}");
            }
        }

        if (entry.RecommendedVersion.Contains("generic", StringComparison.OrdinalIgnoreCase) ||
            entry.RecommendedVersion.Contains("Inbox", StringComparison.OrdinalIgnoreCase))
        {
            return (DriverActionKind.Rollback, $"Roll back or switch to generic Microsoft inbox driver ({entry.RecommendedVersion})");
        }

        return (DriverActionKind.Update, $"Update driver to {entry.RecommendedVersion}");
    }

    public static bool TryCompareVersions(string v1, string v2, string? driverFile, out int result)
    {
        result = 0;
        if (string.IsNullOrWhiteSpace(v1) || string.IsNullOrWhiteSpace(v2)) return false;

        v1 = NormalizeVersion(v1, driverFile);
        v2 = NormalizeVersion(v2, driverFile);

        var p1 = Regex.Split(v1, @"[^\d]+").Where(s => !string.IsNullOrEmpty(s)).Select(s => int.TryParse(s, out int n) ? n : -1).ToList();
        var p2 = Regex.Split(v2, @"[^\d]+").Where(s => !string.IsNullOrEmpty(s)).Select(s => int.TryParse(s, out int n) ? n : -1).ToList();

        if (p1.Count == 0 || p2.Count == 0) return false;

        int len = Math.Max(p1.Count, p2.Count);
        for (int i = 0; i < len; i++)
        {
            int num1 = i < p1.Count ? p1[i] : 0;
            int num2 = i < p2.Count ? p2[i] : 0;
            if (num1 != num2)
            {
                result = num1.CompareTo(num2);
                return true;
            }
        }

        result = 0;
        return true;
    }
}
