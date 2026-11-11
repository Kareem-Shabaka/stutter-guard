using System.Buffers.Binary;
using System.Numerics;

namespace DpcTweaker.Core.Detection;

public sealed record LogicalCpu(int Index, int CoreId, int EfficiencyClass, int L3Id, bool IsSecondThread);

public sealed record CacheGroup(int Id, long SizeBytes, ulong Mask)
{
    public IEnumerable<int> Cpus()
    {
        for (int i = 0; i < 64; i++) if ((Mask & (1UL << i)) != 0) yield return i;
    }

    public string CpuRange() => CpuTopology.FormatCpus(Cpus());
}

/// <summary>
/// CPU layout from GetLogicalProcessorInformationEx: physical cores and their SMT
/// threads, L3 groups (one per CCD or CCX), and efficiency classes (Intel P/E).
/// </summary>
public sealed class CpuTopology
{
    public required IReadOnlyList<LogicalCpu> Cpus { get; init; }
    public required IReadOnlyList<CacheGroup> L3Groups { get; init; }
    public int CoreCount { get; init; }
    public int GroupCount { get; init; } = 1;

    public int LogicalCount => Cpus.Count;
    public bool SmtEnabled => Cpus.Any(c => c.IsSecondThread);
    public bool IsHybrid => Cpus.Select(c => c.EfficiencyClass).Distinct().Count() > 1;
    public bool IsMultiCcd => L3Groups.Count >= 2;
    public bool MultipleProcessorGroups => GroupCount > 1;

    /// <summary>The L3 group with clearly more cache (an X3D V-Cache CCD), if the groups differ.</summary>
    public CacheGroup? VCacheGroup
    {
        get
        {
            if (IsHybrid || L3Groups.Count < 2) return null;
            var big = L3Groups.MaxBy(g => g.SizeBytes)!;
            var small = L3Groups.MinBy(g => g.SizeBytes)!;
            return big.SizeBytes >= small.SizeBytes * 3 / 2 ? big : null;
        }
    }

    public bool IsDualCcdX3D => VCacheGroup is not null;

    /// <summary>
    /// A single-CCD part whose one L3 is far bigger than a normal CCD's (7800X3D / 9800X3D: 96 MB
    /// where the same die without V-Cache has 32 MB). Nothing to route around, since there is no
    /// second CCD, but the user should still see what they own.
    /// </summary>
    public bool IsSingleCcdVCache =>
        !IsHybrid && L3Groups.Count == 1 && L3Groups[0].SizeBytes >= SingleCcdVCacheBytes;

    /// <summary>96 MB V-Cache vs 32 MB plain CCD: 64 MB separates them with room to spare.</summary>
    private const long SingleCcdVCacheBytes = 64L * 1024 * 1024;

    public string Describe()
    {
        var parts = new List<string>
        {
            $"{CoreCount} cores / {LogicalCount} threads",
            SmtEnabled ? "SMT on" : "SMT off",
        };
        if (L3Groups.Count > 0)
            parts.Add(string.Join(", ", L3Groups.Select(g => $"L3 {g.SizeBytes / (1024 * 1024)} MB on CPUs {g.CpuRange()}")));
        if (IsHybrid)
        {
            foreach (var cls in Cpus.GroupBy(c => c.EfficiencyClass).OrderByDescending(g => g.Key))
                parts.Add($"efficiency class {cls.Key}: CPUs {FormatCpus(cls.Select(c => c.Index))}");
        }
        if (IsDualCcdX3D) parts.Add("V-Cache CCD = CPUs " + VCacheGroup!.CpuRange());
        else if (IsSingleCcdVCache) parts.Add("single CCD with 3D V-Cache");
        if (MultipleProcessorGroups) parts.Add($"{GroupCount} processor groups (read-only in v1)");
        return string.Join("; ", parts);
    }

    public static string FormatCpus(IEnumerable<int> cpus)
    {
        var list = cpus.OrderBy(x => x).ToList();
        if (list.Count == 0) return "-";
        var ranges = new List<string>();
        int start = list[0], prev = list[0];
        foreach (int c in list.Skip(1))
        {
            if (c == prev + 1) { prev = c; continue; }
            ranges.Add(start == prev ? $"{start}" : $"{start}-{prev}");
            start = prev = c;
        }
        ranges.Add(start == prev ? $"{start}" : $"{start}-{prev}");
        return string.Join(",", ranges);
    }

    /// <summary>
    /// Parses a SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX buffer (RelationAll).
    /// Layout: DWORD Relationship, DWORD Size, then the union at offset 8.
    ///   PROCESSOR_RELATIONSHIP: BYTE Flags, BYTE EfficiencyClass, BYTE Reserved[20], WORD GroupCount, GROUP_AFFINITY[] at 24
    ///   CACHE_RELATIONSHIP:     BYTE Level, BYTE Assoc, WORD LineSize, DWORD CacheSize, DWORD Type, BYTE Reserved[18], WORD GroupCount, GROUP_AFFINITY[] at 32
    ///   GROUP_RELATIONSHIP:     WORD MaximumGroupCount, WORD ActiveGroupCount, ...
    ///   GROUP_AFFINITY:         KAFFINITY Mask (8), WORD Group, WORD Reserved[3] = 16 bytes
    /// Only processor group 0 is mapped (v1 handles up to 64 logical CPUs).
    /// </summary>
    public static CpuTopology Parse(ReadOnlySpan<byte> buf)
    {
        var coreMasks = new List<(ulong Mask, int Eff)>();
        var l3 = new List<(ulong Mask, long Size)>();
        int groups = 1;

        int off = 0;
        while (off + 8 <= buf.Length)
        {
            uint rel = BinaryPrimitives.ReadUInt32LittleEndian(buf[off..]);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(buf[(off + 4)..]);
            if (size < 8 || off + size > buf.Length) break;
            var u = buf.Slice(off + 8, (int)size - 8);

            switch (rel)
            {
                case 0 when u.Length >= 24: // RelationProcessorCore
                {
                    byte eff = u[1];
                    ushort gc = BinaryPrimitives.ReadUInt16LittleEndian(u[22..]);
                    if (gc == 0) gc = 1;
                    for (int g = 0; g < gc && 24 + g * 16 + 16 <= u.Length; g++)
                    {
                        var ga = u.Slice(24 + g * 16, 16);
                        ulong mask = BinaryPrimitives.ReadUInt64LittleEndian(ga);
                        ushort group = BinaryPrimitives.ReadUInt16LittleEndian(ga[8..]);
                        if (group == 0 && mask != 0) coreMasks.Add((mask, eff));
                    }
                    break;
                }
                case 2 when u.Length >= 32: // RelationCache
                {
                    byte level = u[0];
                    uint cacheSize = BinaryPrimitives.ReadUInt32LittleEndian(u[4..]);
                    uint type = BinaryPrimitives.ReadUInt32LittleEndian(u[8..]);
                    ushort gc = BinaryPrimitives.ReadUInt16LittleEndian(u[30..]);
                    if (gc == 0) gc = 1;
                    if (level == 3 && (type == 0 /* unified */ || type == 2 /* data */))
                    {
                        for (int g = 0; g < gc && 32 + g * 16 + 16 <= u.Length; g++)
                        {
                            var ga = u.Slice(32 + g * 16, 16);
                            ulong mask = BinaryPrimitives.ReadUInt64LittleEndian(ga);
                            ushort group = BinaryPrimitives.ReadUInt16LittleEndian(ga[8..]);
                            if (group == 0 && mask != 0) l3.Add((mask, cacheSize));
                        }
                    }
                    break;
                }
                case 4 when u.Length >= 4: // RelationGroup
                    groups = Math.Max(1, (int)BinaryPrimitives.ReadUInt16LittleEndian(u[2..]));
                    break;
            }
            off += (int)size;
        }

        var cacheGroups = l3
            .GroupBy(x => x.Mask)
            .Select((g, i) => new CacheGroup(i, g.First().Size, g.Key))
            .OrderBy(g => BitOperations.TrailingZeroCount(g.Mask))
            .Select((g, i) => g with { Id = i })
            .ToList();

        var cpus = new List<LogicalCpu>();
        int coreId = 0;
        foreach (var (mask, eff) in coreMasks.OrderBy(c => BitOperations.TrailingZeroCount(c.Mask)))
        {
            bool first = true;
            for (int i = 0; i < 64; i++)
            {
                if ((mask & (1UL << i)) == 0) continue;
                int l3Id = cacheGroups.FirstOrDefault(g => (g.Mask & (1UL << i)) != 0)?.Id ?? -1;
                cpus.Add(new LogicalCpu(i, coreId, eff, l3Id, IsSecondThread: !first));
                first = false;
            }
            coreId++;
        }

        return new CpuTopology
        {
            Cpus = cpus.OrderBy(c => c.Index).ToList(),
            L3Groups = cacheGroups,
            CoreCount = coreId,
            GroupCount = groups,
        };
    }
}
