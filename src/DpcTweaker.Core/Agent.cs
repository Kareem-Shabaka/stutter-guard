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
