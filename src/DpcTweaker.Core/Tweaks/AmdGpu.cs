using DpcTweaker.Core.Detection;
using DpcTweaker.Core.Infra;
using DpcTweaker.Core.Model;
using Microsoft.Win32;

namespace DpcTweaker.Core.Tweaks;

public sealed record GpuKeyDef(
    string Name,
    RegistryValueKind Kind,
    object? Value,
    string InstallerDefault,
    string Title,
    string What,
    string Measured,
    string Cost,
    Evidence Evidence,
    Risk Risk,
    bool DefaultSelected = true);

/// <summary>
/// The key set of apply-gpu-powertweaks-2.ps1, measured on an RX 9070 XT
/// (amdkmdag.sys 32.0.32015.2008, Adrenalin 26.9.2) on Oct 12-15 2026.
/// </summary>
public static class AmdGpuKeys
{
    private const string SevenKeys =
        "Part of the 7-key set: Mortal Kombat 1 at 60 fps went from swinging power to a steady 100-120 W, and its 1% lows stopped dipping (RX 9070 XT, Oct 12 2026).";

    public static readonly GpuKeyDef[] Keys =
    {
        new("PP_GPUPowerDownEnabled", RegistryValueKind.DWord, 0, "1",
            "GPU deep power-down off",
            "Stops the GPU powering down deeply when nothing is drawn.",
            SevenKeys, "More idle power.", Evidence.Measured, Risk.Low),
        new("KMD_EnableContextBasedPowerManagement", RegistryValueKind.DWord, 0, "1",
            "Context-based power management off",
            "Stops the driver changing power level based on what's running.",
            SevenKeys, "Slightly more power in light games.", Evidence.Measured, Risk.Low),
        new("KMD_EnableVcnRTPM", RegistryValueKind.DWord, 0, "not written",
            "Video engine runtime power management off",
            "Stops the video engine (VCN) powering up and down, for example every time a browser video starts.",
            SevenKeys, "Small.", Evidence.Measured, Risk.Low),
        new("EnableUlps", RegistryValueKind.DWord, 0, "1",
            "Ultra Low Power State off",
            "Turns off ULPS at idle. Waking from it is a heavy driver job.",
            SevenKeys, "More idle power.", Evidence.Measured, Risk.Low),
        new("EnableUlps_NA", RegistryValueKind.String, "0", "\"1\"",
            "ULPS text value (EnableUlps_NA)",
            "A text copy of the ULPS switch that the installer writes. It's in the v2 script, but newer drivers never read it; the driver check greys it out when that's the case.",
            "No effect on the RX 9070 XT's driver (it never reads this name).", "None.", Evidence.Measured, Risk.Low),
        new("EnableAspmL0s", RegistryValueKind.DWord, 0, "1",
            "GPU PCIe link sleep L0s off",
            "Stops the driver putting the GPU's own PCIe link into L0s. The Windows power plan doesn't stop the driver doing this itself.",
            SevenKeys, "Small.", Evidence.Measured, Risk.Low),
        new("EnableAspmL1", RegistryValueKind.DWord, 0, "1",
            "GPU PCIe link sleep L1 off",
            "Same as above, for the deeper L1 link state.",
            SevenKeys, "Small.", Evidence.Measured, Risk.Low),
        new("DalDisableIdlePowerOptimizations", RegistryValueKind.DWord, 1, "not set",
            "Display idle power mode off",
            "Turns off the display's idle cache mode (MALL), which switches on and off every time the screen goes still and wakes. Each switch was two long driver DPCs.",
            "Built for exactly the idle spike. With the new keys on, the per-wake routine over 150 µs went from 217/min to 2.8/min (RX 9070 XT, Oct 15 2026).",
            "More idle power.", Evidence.Measured, Risk.Low),
        new("DalDisableStutter", RegistryValueKind.DWord, 1, "not set",
            "Display memory stutter off",
            "Turns off display memory stutter (VRAM self-refresh behind a still screen).",
            "Mortal Kombat 1 1% lows better with it on (RX 9070 XT, Oct 15 2026).",
            "More idle power.", Evidence.Measured, Risk.Low),
        new("PP_MclkDpmDisabled", RegistryValueKind.DWord, 1, "not set",
            "VRAM clock locked",
            "Locks the memory clock at one speed, so it never switches.",
            "Mortal Kombat 1 1% lows better on top of the two display keys (RX 9070 XT, Oct 15 2026).",
            "Clearly more idle power and heat. After a reboot, check in Adrenalin that the memory clock still reaches full speed in a game.",
            Evidence.Measured, Risk.Medium),
    };

    /// <summary>Tested and rejected: removed if found.</summary>
    public const string FamsKey = "DalFamsDisable";

    /// <summary>The driver ignores StutterMode; the app puts the installer's own value back.</summary>
    public const string StutterModeKey = "StutterMode";
    public const int StutterModeInstallerValue = 0x200A;

    public static IEnumerable<string> AllNames() =>
        Keys.Select(k => k.Name).Append(FamsKey).Append(StutterModeKey);

    public static IEnumerable<ITweak> Build(SystemProfile profile, Journal journal)
    {
        foreach (var gpu in profile.Gpus.Where(g => g.Vendor == GpuVendor.Amd))
        {
            string group = gpu.Label;
            string idBase = "amdgpu." + gpu.ClassSubkey + ".";
            string driverName = gpu.DriverFile is null ? "the driver" : Path.GetFileName(gpu.DriverFile);
            string ver = gpu.DriverVersion ?? "?";

            Func<string?> NotRead(string name) => () =>
                gpu.DriverKeyScan is { } scan && scan.TryGetValue(name, out bool found) && !found
                    ? $"Your driver ({driverName} {ver}) never reads {name}, so it would do nothing."
                    : null;

            foreach (var k in AmdGpuKeys.Keys)
            {
                var info = new TweakInfo
                {
                    Id = idBase + k.Name,
                    Module = "GPU",
                    Group = group,
                    Title = $"{k.Title} ({k.Name} = {FormatValue(k)})",
                    What = k.What + $" Installer default: {k.InstallerDefault}.",
                    Why = k.Measured,
                    Cost = k.Cost,
                    CanBreak = "Nothing found so far. A driver update removes it again (the watcher will re-apply it).",
                    Evidence = k.Evidence,
                    Risk = k.Risk,
                    NeedsReboot = true,
                    DefaultSelected = k.DefaultSelected && !gpu.IsIntegrated,
                };
                yield return new RegistryValueTweak(info, journal, gpu.ClassKeyPath, k.Name, k.Kind, k.Value, NotRead(k.Name));
            }

            yield return new RegistryValueTweak(new TweakInfo
            {
                Id = idBase + FamsKey,
                Module = "GPU",
                Group = group,
                Title = "DalFamsDisable removed (tested and rejected)",
                What = "Removes DalFamsDisable if it's set. FAMS lets the firmware change the VRAM clock without upsetting a FreeSync screen, and it's active while gaming.",
                Why = "Turning FAMS off made Mortal Kombat 1 1% lows worse on the RX 9070 XT (Oct 15 2026), so the app never sets it.",
                Cost = "None.",
                Evidence = Evidence.Measured,
                Risk = Risk.Low,
                NeedsReboot = true,
                DefaultSelected = !gpu.IsIntegrated,
            }, journal, gpu.ClassKeyPath, FamsKey, RegistryValueKind.DWord, null);

            yield return new RegistryValueTweak(new TweakInfo
            {
                Id = idBase + StutterModeKey,
                Module = "GPU",
                Group = group,
                Title = "StutterMode at the installer value (0x200A)",
                What = "Puts StutterMode back to what AMD's installer writes. Community lists say to set it to 0.",
                Why = "Tested on the RX 9070 XT: StutterMode = 0 changed nothing, and the driver doesn't read the name at all. This only keeps the key as AMD shipped it.",
                Cost = "None.",
                Evidence = Evidence.Measured,
                Risk = Risk.Low,
                NeedsReboot = false,
                DefaultSelected = false,
            }, journal, gpu.ClassKeyPath, StutterModeKey, RegistryValueKind.DWord, StutterModeInstallerValue);
        }
    }

    private static string FormatValue(GpuKeyDef k) => k.Kind == RegistryValueKind.String ? $"\"{k.Value}\"" : $"{k.Value}";
}
