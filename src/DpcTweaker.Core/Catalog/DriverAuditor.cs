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
            string drvVer = gpu.DriverVersion ?? "";
            string drvFile = gpu.DriverFile is not null
                ? Path.GetFileName(gpu.DriverFile)
                : (gpu.ServiceName is not null ? gpu.ServiceName + ".sys" : "");

            AuditTarget(catalog, gpu.Name, new PnpNode { InstanceId = gpu.InstanceId ?? "" },
                drvVer, drvFile, gpu.ServiceName, advisories, seenKeys);
        }

        // 2. Audit PCI Devices
        foreach (var dev in profile.Devices)
        {
            string drvVer = dev.DriverVersion ?? "";
            string drvFile = dev.DriverFileName ?? (dev.Service is not null ? dev.Service + ".sys" : "");

            AuditTarget(catalog, dev.Name, new PnpNode { InstanceId = dev.InstanceId },
                drvVer, drvFile, dev.Service, advisories, seenKeys);
        }

        // 3. Audit every node, whichever bus enumerated it. Until the scan supplied these, a rule for a
        //    USB audio interface, an audio processing component or a vendor root driver had nothing to
        //    match: 22 of the 77 rules could not fire on any machine, and one of them targets a node
        //    that is present on the author's own board.
        foreach (var node in profile.PnpNodes)
        {
            // Reading a driver version costs a registry hit, so ask the cheap question first.
            if (!catalog.Any(e => IsHardwareMatch(e, node))) continue;

            string drvVer = node.DriverVersion ?? "";
            string drvFile = node.DriverFileName ?? (node.Service is not null ? node.Service + ".sys" : "");

            AuditTarget(catalog, node.Name, node, drvVer, drvFile, node.Service, advisories, seenKeys);
        }

        return advisories;
    }

    private static void AuditTarget(
        IReadOnlyList<BadDriverEntry> catalog,
        string deviceName,
        PnpNode node,
        string drvVer,
        string drvFile,
        string? service,
        List<DriverAdvisory> advisories,
        HashSet<string> seenKeys)
    {
        string hwid = node.InstanceId;
        // Several catalog entries can describe the same driver (a Pascal entry and a broader Pascal
        // one, say). Taking the first match made the reported advisory depend on file order and let
        // a Critical entry be shadowed by a High one, so the most severe match wins instead.
        BadDriverEntry? buggedMatch = null;
        bool bugged = false;
        BadDriverEntry? matchedClean = null;

        foreach (var entry in catalog)
        {
            if (!IsHardwareMatch(entry, node))
                continue;

            if (IsVersionBugged(drvVer, entry))
            {
                bugged = true;
                if (buggedMatch is null || SeverityRank(entry.Severity) > SeverityRank(buggedMatch.Severity))
                    buggedMatch = entry;
            }
            else if (matchedClean is null)
            {
                matchedClean = entry;
            }
        }

        if (bugged && buggedMatch is not null)
        {
            string dedupKey = $"BUGGED:{buggedMatch.DriverFile}:{hwid}:{drvVer}";
            if (seenKeys.Add(dedupKey))
            {
                var (action, actionText) = DetermineAction(drvVer, buggedMatch, true);
                advisories.Add(new DriverAdvisory
                {
                    Entry = buggedMatch,
                    DeviceName = deviceName,
                    HardwareId = hwid,
                    InstalledVersion = string.IsNullOrWhiteSpace(drvVer) ? "(unknown)" : drvVer,
                    DriverFile = string.IsNullOrWhiteSpace(drvFile) ? buggedMatch.DriverFile : drvFile,
                    Action = action,
                    ActionText = actionText,
                    IsBugged = true
                });
            }
        }

        // If no bugged match occurred, but the device belongs to a catalogued driver family, record clean advisory
        if (!bugged && matchedClean is not null && !advisories.Any(a => string.Equals(a.HardwareId, hwid, StringComparison.OrdinalIgnoreCase) && a.IsBugged))
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

    /// <summary>
    /// Matches a rule against a device node: its hardware ids, its compatible ids and its instance id.
    /// A node with no usable pattern in the rule falls back to the rule's driver file name.
    /// </summary>
    public static bool IsHardwareMatch(BadDriverEntry entry, PnpNode node) =>
        HasPattern(entry)
            ? DriverMatcher.MatchesAny(entry.HardwareIdPattern, node.HardwareIds, node.CompatibleIds, node.InstanceId)
            : FileMatches(entry, node.DriverFileName, node.Service);

    /// <summary>
    /// The same test for a caller that holds only an instance id (a GpuInfo or a PciDevice). The
    /// instance id is the last resort rather than the first: Windows never compares it when it picks
    /// a driver, so a rule that matches only here is a rule that should be rewritten as an id.
    /// </summary>
    public static bool IsHardwareMatch(BadDriverEntry entry, string hwid, string drvFile, string? service) =>
        HasPattern(entry)
            ? (DriverMatcher.MatchesAny(entry.HardwareIdPattern, null, null, hwid) ||
               DriverMatcher.MatchesAny(entry.HardwareIdPattern, new[] { hwid }, null, null))
            : FileMatches(entry, drvFile, service);

    private static bool HasPattern(BadDriverEntry entry) =>
        !string.IsNullOrWhiteSpace(entry.HardwareIdPattern) && entry.HardwareIdPattern.Trim() != "*";

    private static bool FileMatches(BadDriverEntry entry, string? drvFile, string? service) =>
        !string.IsNullOrEmpty(entry.DriverFile) &&
        (string.Equals(entry.DriverFile, drvFile, StringComparison.OrdinalIgnoreCase) ||
         (service is not null && string.Equals(entry.DriverFile, service + ".sys", StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// True when the rule's pattern equals one identifier, as an INF glob. Deliberately a comparison
    /// and not a search: the start-with and contains fallbacks this replaces matched a Realtek audio
    /// rule (SWC\VEN_10EC&amp;SID_0001) against a Realtek Ethernet card (PCI\VEN_10EC&amp;DEV_8126),
    /// because the card's identifier happens to contain "10EC".
    /// </summary>
    public static bool MatchesHwidPattern(string pattern, string hwid) =>
        !string.IsNullOrWhiteSpace(hwid) && DriverMatcher.MatchesAny(pattern, new[] { hwid }, null, null);

    internal static int SeverityRank(string? severity) => severity switch
    {
        "Critical" => 3,
        "High" => 2,
        "Medium" => 1,
        _ => 0,
    };

    /// <summary>
    /// True when <paramref name="wanted"/> matches the tail (or the head) of
    /// <paramref name="installed"/> <em>as whole version components</em>. A trailing vendor suffix
    /// ("23.11.1 (WHQL)") is ignored, but "3118" no longer counts as ending in "18".
    /// </summary>
    internal static bool MatchesVersionComponents(string installed, string wanted)
    {
        static List<int> Parts(string s) =>
            Regex.Split(s, @"[^\d]+").Where(x => x.Length > 0)
                 .Select(x => int.TryParse(x, out int n) ? n : -1).ToList();

        var a = Parts(installed ?? "");
        var b = Parts(wanted ?? "");
        if (b.Count == 0 || a.Count == 0 || b.Count > a.Count) return false;

        bool suffix = true;
        for (int i = 0; i < b.Count; i++)
            if (a[a.Count - b.Count + i] != b[i]) { suffix = false; break; }
        if (suffix) return true;

        for (int i = 0; i < b.Count; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    /// <summary>
    /// The version numbers one string holds, most-specific first.
    /// <para>
    /// AMD's catalogue rows store "23.9.1 (31.0.21029.1006)": the Adrenalin release name and, in
    /// brackets, the WDDM version Windows actually reports. A user's machine only ever yields the
    /// bracketed quad, so comparing the two strings whole could never match - 33 listed AMD variants
    /// were unreachable, and an affected user was told their bugged driver was clean.
    /// </para>
    /// </summary>
    public static IEnumerable<string> VersionForms(string? ver)
    {
        if (string.IsNullOrWhiteSpace(ver)) yield break;
        string s = ver.Trim();

        int open = s.IndexOf('(');
        int close = s.IndexOf(')');
        if (open > 0 && close > open)
        {
            string inner = s[(open + 1)..close].Trim();
            if (inner.Length > 0) yield return inner;      // the WDDM build Windows reports
            string head = s[..open].Trim();
            if (head.Length > 0) yield return head;        // the marketing name
        }

        yield return s;
    }

    /// <summary>How many dotted numeric components a version string has.</summary>
    public static int ComponentCount(string? ver) =>
        Regex.Split(ver ?? "", @"[^\d]+").Count(x => x.Length > 0);

    public static bool IsVersionBugged(string installedVer, BadDriverEntry entry)
    {
        if (string.IsNullOrWhiteSpace(installedVer)) return false;

        var installedForms = VersionForms(installedVer).ToList();
        if (installedForms.Count == 0) return false;

        foreach (var b in entry.BuggedVersions)
        {
            foreach (var buggedForm in VersionForms(b))
            {
                string normBugged = NormalizeVersion(buggedForm, entry.DriverFile);

                foreach (var installedForm in installedForms)
                {
                    if (string.Equals(NormalizeVersion(installedForm, entry.DriverFile), normBugged,
                                      StringComparison.OrdinalIgnoreCase))
                        return true;

                    // Compare whole version components, not raw substrings: "31.0.15.3118".EndsWith("1.18")
                    // looked like a match even though 3118 is not 18. A vendor string with a suffix such as
                    // "23.11.1 (WHQL)" still matches, because the components line up.
                    if (MatchesVersionComponents(installedForm, buggedForm))
                        return true;
                }
            }
        }

        return false;
    }

    public static string NormalizeVersion(string ver, string? driverFile = null)
    {
        if (string.IsNullOrWhiteSpace(ver)) return "";
        string v = ver.Trim();

        // Handle NVIDIA driver: 32.0.15.3118 or 31.0.15.3598 -> 531.18 or 535.98.
        // The third component is always "14" or "15" for NVIDIA. A bare "30./31./32." prefix is
        // NOT enough: Intel Arc drivers are 32.0.101.x and AMD uses 32.0.21025.x, so the old test
        // mangled both vendors' versions into nonsense ("32.0.101.5186" became "151.86").
        // Every NVIDIA display driver encodes its marketing version in the third and fourth
        // components, and the third is always 14 or 15. Nothing else may be translated: a file-name
        // test is not enough, because nvraid.sys and nvstor.sys also start with "nv" and their
        // 10.0.19041.1234 storage versions would be mangled into a fabricated 904112.34.
        var parts = v.Split('.');
        bool nvidiaShape = parts.Length == 4 && (parts[2] == "14" || parts[2] == "15") && parts[3].Length >= 4;

        if (nvidiaShape && parts[3].Length >= 4)
        {
            string major = parts[2][1..] + parts[3][..2];
            string minor = parts[3][2..];
            return $"{major}.{minor}";
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

        // Prefer a like-for-like comparison, then fall back to the first number in the text.
        if (TryCompareLikeForLike(installedVer, entry, out int cmp))
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

    /// <summary>
    /// Compares the installed version with the entry's recommended one using only forms that carry the
    /// same number of components.
    /// <para>
    /// Without this, "23.12.1 (31.0.23013.1029)" sent the first number in the text (23.12.1) to be
    /// compared against the WDDM quad the machine reports (31.0.23013.1006). 31 is greater than 23, so
    /// an out-of-date AMD driver was reported as needing a rollback - the one instruction that would
    /// make it worse. Counting components separates the two forms without a vendor-specific rule.
    /// </para>
    /// </summary>
    public static bool TryCompareLikeForLike(string installedVer, BadDriverEntry entry, out int result)
    {
        result = 0;
        if (string.IsNullOrWhiteSpace(installedVer) || string.IsNullOrWhiteSpace(entry.RecommendedVersion))
            return false;

        var targets = VersionForms(entry.RecommendedVersion).ToList();
        var first = Regex.Match(entry.RecommendedVersion, @"\b\d+(?:\.\d+)+\b");
        if (first.Success) targets.Add(first.Value);

        foreach (var installed in VersionForms(installedVer))
        {
            int components = ComponentCount(installed);
            if (components == 0) continue;

            foreach (var target in targets)
            {
                if (ComponentCount(target) != components) continue;
                if (TryCompareVersions(installed, target, entry.DriverFile, out result)) return true;
            }
        }

        return false;
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
