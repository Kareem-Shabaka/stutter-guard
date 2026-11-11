using System.Text;

namespace DpcTweaker.Core.Detection;

/// <summary>
/// Checks whether a driver binary contains a registry value name, as a whole
/// string, in UTF-16 or plain ASCII. Found = the driver can read it. Missing =
/// it never does. This is how StutterMode turned out to be ignored by the
/// desktop's amdkmdag.sys after a full test cycle.
///
/// Whole-string match: the name must end at a NUL terminator and must not be
/// preceded by another identifier character, so "EnableUlps" doesn't count as
/// found just because "EnableUlps_NA" or "XEnableUlps" is in the file.
/// </summary>
public static class DriverStringScanner
{
    public static Dictionary<string, bool> Scan(string filePath, IEnumerable<string> names)
    {
        byte[] data = File.ReadAllBytes(filePath);
        return Scan(data, names);
    }

    public static Dictionary<string, bool> Scan(ReadOnlySpan<byte> data, IEnumerable<string> names)
    {
        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (string name in names.Distinct())
            result[name] = ContainsAscii(data, name) || ContainsUtf16(data, name);
        return result;
    }

    private static bool IsIdent(int c) =>
        c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_';

    internal static bool ContainsAscii(ReadOnlySpan<byte> data, string name)
    {
        byte[] needle = Encoding.ASCII.GetBytes(name);
        int start = 0;
        while (start <= data.Length - needle.Length)
        {
            int i = data[start..].IndexOf(needle);
            if (i < 0) return false;
            int at = start + i;
            int end = at + needle.Length;
            bool endOk = end < data.Length && data[end] == 0;
            bool startOk = at == 0 || !IsIdent(data[at - 1]);
            if (endOk && startOk) return true;
            start = at + 1;
        }
        return false;
    }

    internal static bool ContainsUtf16(ReadOnlySpan<byte> data, string name)
    {
        byte[] needle = Encoding.Unicode.GetBytes(name);
        int start = 0;
        while (start <= data.Length - needle.Length)
        {
            int i = data[start..].IndexOf(needle);
            if (i < 0) return false;
            int at = start + i;
            int end = at + needle.Length;
            bool endOk = end + 1 < data.Length && data[end] == 0 && data[end + 1] == 0;
            // Even offset: the two bytes before are one UTF-16 code unit; it must not be an identifier char.
            // Odd offset (unaligned): accept only right after a NUL byte.
            bool startOk = at < 2
                ? true
                : at % 2 == 0
                    ? !(data[at - 1] == 0 && IsIdent(data[at - 2]))
                    : data[at - 1] == 0;
            if (endOk && startOk) return true;
            start = at + 1;
        }
        return false;
    }
}
