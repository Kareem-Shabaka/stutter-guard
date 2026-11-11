using DpcTweaker.Core.Infra;
using DpcTweaker.Core.Model;
using Microsoft.Win32;

namespace DpcTweaker.Core.Tweaks;

/// <summary>Journal-first registry writes shared by the multi-value tweaks.</summary>
public static class RegWrite
{
    /// <summary>Records the old value (first time only), then writes the new one (null = remove).</summary>
    public static void Set(TweakContext ctx, string tweakId, string keyPath, string name, RegistryValueKind kind, object? value)
    {
        var cur = RegistryOps.Read(keyPath, name);
        ctx.Journal.RecordOriginal(tweakId, "reg", keyPath + "|" + name, cur.Exists,
            cur.Exists ? cur.Kind.ToString() : null, cur.Exists ? cur.Serialize() : null);
        if (value is null) RegistryOps.DeleteValue(keyPath, name);
        else RegistryOps.Write(keyPath, name, kind, value);
        Log.Info($"Set {keyPath}\\{name} = {(value is null ? "(removed)" : new RegValue(true, kind, value).Display)} (was {cur.Display})");
    }

    /// <summary>The value as text whatever its type (NDIS keywords are REG_SZ, but some tools write DWORDs).</summary>
    public static string? AsText(RegValue v) => !v.Exists ? null : v.Kind switch
    {
        RegistryValueKind.DWord => v.AsDword().ToString(System.Globalization.CultureInfo.InvariantCulture),
        RegistryValueKind.QWord => v.AsQword().ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => v.Value?.ToString(),
    };

    /// <summary>An affinity mask from REG_BINARY (any length up to 8 bytes, little-endian).</summary>
    public static ulong? AsMask(RegValue v)
    {
        if (!v.Exists) return null;
        if (v.Value is byte[] b)
        {
            ulong m = 0;
            for (int i = 0; i < Math.Min(8, b.Length); i++) m |= (ulong)b[i] << (8 * i);
            return m;
        }
        if (v.Kind == RegistryValueKind.QWord) return v.AsQword();
        if (v.Kind == RegistryValueKind.DWord) return v.AsDword();
        return null;
    }

    public static byte[] MaskBytes(ulong mask) => BitConverter.GetBytes(mask); // little-endian on x64

    public static string FormatMask(ulong mask)
    {
        var cpus = Enumerable.Range(0, 64).Where(i => (mask & (1UL << i)) != 0).ToList();
        if (cpus.Count == 0) return "no CPU";
        return (cpus.Count == 1 ? "CPU " : "CPUs ") + string.Join(", ", cpus);
    }
}
