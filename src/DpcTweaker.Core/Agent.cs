using DpcTweaker.Core.Infra;
using DpcTweaker.Core.Tweaks;

namespace DpcTweaker.Core;

/// <summary>Headless mode, run by the boot tasks: StutterGuard.exe --agent device-power</summary>
public static class Agent
{
    public static bool IsAgentCall(string[] args) =>
        args.Length >= 1 && string.Equals(args[0], "--agent", StringComparison.OrdinalIgnoreCase);

    public static int Run(string[] args)
    {
        string verb = args.Length >= 2 ? args[1].ToLowerInvariant() : "";
        try
        {
            switch (verb)
            {
                case "device-power":
                    DevicePower.ApplyNow();
                    return 0;
                case "restore-device-power":
                    DevicePower.RestoreFromBackup();
                    return 0;
                case "drift-check":
                {
                    // Read-only: reports drift to the log and through the exit code, so a logon
                    // task or CI step can notice a driver update without opening the GUI.
                    var profile = Detection.Detector.Detect();
                    var journal = Journal.OpenDefault();
                    var engine = new TweakEngine(profile, journal);
                    var report = engine.ScanDrift();
                    if (!report.HasDrift) { Log.Info("Drift check: nothing drifted."); return 0; }
                    Log.Warn($"Drift check: {report.Headline}. {report.Detail}");
                    return 10;   // 10 = drift found, distinct from the generic failure code 1
                }
                default:
                    Log.Warn("Agent: unknown verb '" + verb + "'");
                    return 2;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Agent " + verb + " failed: " + ex);
            return 1;
        }
    }
}
