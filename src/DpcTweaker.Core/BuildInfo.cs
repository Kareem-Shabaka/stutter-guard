using System.Reflection;

namespace DpcTweaker.Core;

/// <summary>
/// The one place the running app reads its own version from. Sourced from the assembly, which is
/// stamped by Directory.Build.props, so the UI, the scan report and the telemetry summary can
/// never disagree with the binary.
/// </summary>
public static class BuildInfo
{
    /// <summary>"1.0.0" — the marketing version.</summary>
    public static string Version { get; } = ReadVersion();

    /// <summary>"1.0.0.0" — the file version stamped into the PE header.</summary>
    public static string FileVersion { get; } = ReadFileVersion();

    /// <summary>When this .exe was published: makes an old copy obvious.</summary>
    public static string BuiltAt
    {
        get
        {
            try
            {
                return Environment.ProcessPath is { } p
                    ? File.GetLastWriteTime(p).ToString("yyyy-MM-dd HH:mm")
                    : "?";
            }
            catch { return "?"; }
        }
    }

    private static string ReadVersion()
    {
        try
        {
            var asm = typeof(BuildInfo).Assembly;
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
            {
                int plus = info.IndexOf('+');   // strip the +<commit> suffix when a CI adds it
                return plus > 0 ? info[..plus] : info;
            }
            return asm.GetName().Version?.ToString(3) ?? "1.0.0";
        }
        catch { return "1.0.0"; }
    }

    private static string ReadFileVersion()
    {
        try
        {
            var asm = typeof(BuildInfo).Assembly;
            return asm.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version
                   ?? asm.GetName().Version?.ToString(4) ?? "1.0.0.0";
        }
        catch { return "1.0.0.0"; }
    }
}