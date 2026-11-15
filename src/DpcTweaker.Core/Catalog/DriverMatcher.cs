using System.Text.RegularExpressions;

namespace DpcTweaker.Core.Catalog;

/// <summary>
/// How a catalogue pattern is compared with a device.
/// <para>
/// A pattern is an INF model-section identifier ("PCI\VEN_10DE&DEV_1B80*"), never a device instance
/// id. Windows reports hardware ids and compatible ids as two separate lists, and which list a given
/// identifier lands in varies by build: Microsoft documents that "in Windows 10, some IDs that
/// previously appeared in the Hardware IDs list now appear in the list of Compatible IDs". A device
/// is therefore matched against its hardware ids, its compatible ids and its instance id, and
/// nothing else.
/// </para>
/// <para>
/// The comparison is an anchored glob, which is what INF matching is. Microsoft's own rule is that
/// device identification strings "should not be parsed ... only for string comparisons", so there is
/// deliberately no substring or start-with fallback here: those match one vendor's number inside
/// another vendor's board id. A rule for a Realtek audio component (SWC\VEN_10EC&amp;SID_0001) used to
/// fire on a Realtek Ethernet card (PCI\VEN_10EC&amp;DEV_8126) for exactly that reason.
/// </para>
/// </summary>
public static class DriverMatcher
{
    /// <summary>
    /// Separators for a row that covers several ids. Six catalogue rows list two or three ids in one
    /// pattern, and only the first was ever reachable.
    /// </summary>
    private static readonly char[] Separators = { ';', '|' };

    /// <summary>Each id in a pattern that may list several, trimmed, empty entries dropped.</summary>
    public static IEnumerable<string> Forms(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) yield break;
        foreach (string raw in pattern.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            string form = raw.Trim();
            if (form.Length > 0) yield return form;
        }
    }

    /// <summary>True when any id of <paramref name="pattern"/> equals any identifier the device reports.</summary>
    public static bool MatchesAny(
        string? pattern,
        IEnumerable<string>? hardwareIds,
        IEnumerable<string>? compatibleIds,
        string? instanceId)
    {
        foreach (string form in Forms(pattern))
        {
            var rx = GlobCatalog<object>.ToRegex(form);

            if (hardwareIds is not null)
                foreach (string id in hardwareIds)
                    if (!string.IsNullOrEmpty(id) && rx.IsMatch(id)) return true;

            if (compatibleIds is not null)
                foreach (string id in compatibleIds)
                    if (!string.IsNullOrEmpty(id) && rx.IsMatch(id)) return true;

            if (!string.IsNullOrEmpty(instanceId) && rx.IsMatch(instanceId!)) return true;
        }

        return false;
    }
}