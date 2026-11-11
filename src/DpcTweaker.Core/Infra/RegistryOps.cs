using System.Globalization;
using Microsoft.Win32;

namespace DpcTweaker.Core.Infra;

/// <summary>A registry value as read: exists or not, its type, its data.</summary>
public readonly record struct RegValue(bool Exists, RegistryValueKind Kind, object? Value)
{
    public static readonly RegValue Missing = new(false, RegistryValueKind.Unknown, null);

    public string Display
    {
        get
        {
            if (!Exists) return "(not set)";
            return Kind switch
            {
                RegistryValueKind.DWord => FormatDword(AsDword()),
                RegistryValueKind.QWord => AsQword().ToString(CultureInfo.InvariantCulture),
                RegistryValueKind.Binary => BitConverter.ToString((byte[])Value!).Replace('-', ' '),
                RegistryValueKind.MultiString => string.Join(" | ", (string[])Value!),
                _ => "\"" + (Value?.ToString() ?? "") + "\"",
            };
        }
    }

    public uint AsDword() => Value switch
    {
        int i => unchecked((uint)i),
        uint u => u,
        long l => unchecked((uint)l),
        _ => 0,
    };

    public ulong AsQword() => Value switch
    {
        long l => unchecked((ulong)l),
        ulong u => u,
        int i => unchecked((ulong)i),
        _ => 0,
    };

    /// <summary>Text form for the journal (reversible with <see cref="Deserialize"/>).</summary>
    public string Serialize() => Kind switch
    {
        RegistryValueKind.DWord => AsDword().ToString(CultureInfo.InvariantCulture),
        RegistryValueKind.QWord => AsQword().ToString(CultureInfo.InvariantCulture),
        RegistryValueKind.Binary => Convert.ToHexString((byte[])Value!),
        RegistryValueKind.MultiString => string.Join("\n", (string[])Value!),
        _ => Value?.ToString() ?? "",
    };

    public static object Deserialize(RegistryValueKind kind, string data) => kind switch
    {
        RegistryValueKind.DWord => unchecked((int)uint.Parse(data, CultureInfo.InvariantCulture)),
        RegistryValueKind.QWord => unchecked((long)ulong.Parse(data, CultureInfo.InvariantCulture)),
        RegistryValueKind.Binary => Convert.FromHexString(data),
        RegistryValueKind.MultiString => data.Split('\n'),
        _ => data,
    };

    public static string FormatDword(uint v) => v > 9 ? $"{v} (0x{v:X})" : v.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Registry access with full paths like HKLM\SYSTEM\..., always the 64-bit view.</summary>
public static class RegistryOps
{
    public static (RegistryKey Hive, string SubKey) Split(string path)
    {
        int i = path.IndexOf('\\');
        string hive = i < 0 ? path : path[..i];
        string sub = i < 0 ? "" : path[(i + 1)..];
        RegistryHive h = hive.ToUpperInvariant() switch
        {
            "HKLM" or "HKEY_LOCAL_MACHINE" => RegistryHive.LocalMachine,
            "HKCU" or "HKEY_CURRENT_USER" => RegistryHive.CurrentUser,
            "HKCR" or "HKEY_CLASSES_ROOT" => RegistryHive.ClassesRoot,
            "HKU" or "HKEY_USERS" => RegistryHive.Users,
            _ => throw new ArgumentException("Unknown registry hive: " + hive),
        };
        return (RegistryKey.OpenBaseKey(h, RegistryView.Registry64), sub);
    }

    /// <summary>HKLM\X -> HKEY_LOCAL_MACHINE\X (the form reg.exe and PowerShell's Registry:: provider want).</summary>
    public static string LongForm(string path)
    {
        int i = path.IndexOf('\\');
        string hive = i < 0 ? path : path[..i];
        string rest = i < 0 ? "" : path[i..];
        string longHive = hive.ToUpperInvariant() switch
        {
            "HKLM" => "HKEY_LOCAL_MACHINE",
            "HKCU" => "HKEY_CURRENT_USER",
            "HKCR" => "HKEY_CLASSES_ROOT",
            "HKU" => "HKEY_USERS",
            _ => hive,
        };
        return longHive + rest;
    }

    public static RegValue Read(string keyPath, string name)
    {
        var (hive, sub) = Split(keyPath);
        using (hive)
        {
            using var k = hive.OpenSubKey(sub, writable: false);
            if (k is null) return RegValue.Missing;
            object? v = k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (v is null) return RegValue.Missing;
            return new RegValue(true, k.GetValueKind(name), v);
        }
    }

    public static void Write(string keyPath, string name, RegistryValueKind kind, object value)
    {
        var (hive, sub) = Split(keyPath);
        using (hive)
        {
            using var k = hive.CreateSubKey(sub, writable: true)
                ?? throw new InvalidOperationException("Couldn't open " + keyPath + " for writing.");
            k.SetValue(name, value, kind);
        }
    }

    public static void DeleteValue(string keyPath, string name)
    {
        var (hive, sub) = Split(keyPath);
        using (hive)
        {
            using var k = hive.OpenSubKey(sub, writable: true);
            k?.DeleteValue(name, throwOnMissingValue: false);
        }
    }

    public static bool KeyExists(string keyPath)
    {
        var (hive, sub) = Split(keyPath);
        using (hive)
        {
            using var k = hive.OpenSubKey(sub, writable: false);
            return k is not null;
        }
    }

    public static string[] SubKeyNames(string keyPath)
    {
        var (hive, sub) = Split(keyPath);
        using (hive)
        {
            using var k = hive.OpenSubKey(sub, writable: false);
            if (k is null) return Array.Empty<string>();
            try { return k.GetSubKeyNames(); }
            catch { return Array.Empty<string>(); }
        }
    }

    public static string? ReadString(string keyPath, string name)
    {
        var v = Read(keyPath, name);
        return v.Exists ? v.Value?.ToString() : null;
    }

    public static uint? ReadDword(string keyPath, string name)
    {
        var v = Read(keyPath, name);
        return v.Exists && v.Kind == RegistryValueKind.DWord ? v.AsDword() : null;
    }
}
