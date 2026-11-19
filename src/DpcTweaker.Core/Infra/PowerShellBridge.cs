using System.Text;
using System.Text.Json;

namespace DpcTweaker.Core.Infra;

/// <summary>
/// Runs short Windows PowerShell 5.1 scripts for the parts of Windows that are
/// only reachable through WMI/CIM (chassis, device power, VBS, WHEA). The script's
/// last output line must be one JSON value.
/// <para>
/// The script is handed over as a temporary .ps1 file. It used to go through
/// <c>-EncodedCommand</c>, which is base64 text on a command line, and that single detail is the strongest
/// thing in this program for a scanner to match: "unsigned executable spawns PowerShell with a
/// base64-encoded script and an execution-policy bypass" is the canonical dropper signature, and it is
/// half of what earned this build a Trojan-Downloader verdict. A .ps1 on disk that the user can read
/// before it runs says the same thing to a human and much less to a heuristic.
/// </para>
/// <para>
/// The temporary file is removed in a finally block, so a crash does not leave scripts behind. Its
/// contents are ordinary PowerShell with no secrets: everything it touches is local WMI/CIM state.
/// </para>
/// </summary>
public static class PowerShellBridge
{
    public static string PowerShellExe =>
        Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    public static JsonElement RunJson(string script, int timeoutMs = 120_000)
    {
        string full =
            "$ProgressPreference = 'SilentlyContinue'\n" +
            "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8\n" +
            script;

        // A .ps1 needs a BOM for Windows PowerShell 5.1 to read it as UTF-8; without one a non-ASCII
        // device name in the output round-trips as mojibake.
        string path = Path.Combine(Path.GetTempPath(), "stutterguard-" + Guid.NewGuid().ToString("N") + ".ps1");
        ProcessResult r;
        try
        {
            File.WriteAllText(path, full, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            r = ProcessRunner.Run(PowerShellExe,
                new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", path },
                timeoutMs);
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* a locked temp file is not an error */ }
        }

        if (r.TimedOut) throw new TimeoutException("PowerShell didn't finish in time.");

        string? json = r.StdOut
            .Split('\n')
            .Select(l => l.Trim())
            .LastOrDefault(l => l.StartsWith('{') || l.StartsWith('['));

        if (json is null)
            throw new InvalidOperationException("PowerShell returned no JSON. " + r.Combined);

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    /// <summary>Single-quoted PowerShell string literal.</summary>
    public static string Quote(string s) => "'" + s.Replace("'", "''") + "'";
}

public static class JsonExt
{
    public static string? Str(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null
            ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString())
            : null;

    public static long? Num(this JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n)) return n;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out n)) return n;
        return null;
    }

    public static bool? Bool(this JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    /// <summary>Array property, or a single value wrapped as one element (PowerShell unrolls 1-item arrays).</summary>
    public static IEnumerable<JsonElement> Arr(this JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) yield break;
        if (v.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in v.EnumerateArray()) yield return x;
        }
        else if (v.ValueKind != JsonValueKind.Null)
        {
            yield return v;
        }
    }
}
