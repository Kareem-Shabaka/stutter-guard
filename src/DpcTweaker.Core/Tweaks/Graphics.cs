using DpcTweaker.Core.Detection;
using DpcTweaker.Core.Infra;
using DpcTweaker.Core.Model;
using Microsoft.Win32;

namespace DpcTweaker.Core.Tweaks;

/// <summary>Graphics-stack switches that apply to every GPU vendor.</summary>
public static class GraphicsModule
{
    public static IEnumerable<ITweak> Build(SystemProfile profile, Journal journal)
    {
        yield return new RegistryValueTweak(new TweakInfo
        {
            Id = "graphics.mpo-off",
            Module = "GPU",
            Group = "Graphics stack (all GPUs)",
            Title = "Multiplane overlay (MPO) off (DisableOverlays = 1)",
            What = "Sets DisableOverlays = 1 under HKLM\\SYSTEM\\CurrentControlSet\\Control\\GraphicsDrivers. The older OverlayTestMode = 5 no longer works on 25H2 and later.",
            Why = "Applied on the desktop. One RX 9070 XT owner went from 5,000-8,000 µs to an 800 µs max with it, on a badly broken system.",
            Cost = "Desktop composition does a little more work.",
            CanBreak = "HDR, FreeSync or overlays on some systems; one RX 7900 XTX owner got flashing black screens. Revert at the first flicker. Check with dxdiag: Display > MPO MaxPlanes = 1.",
            Evidence = Evidence.Anecdotal,
            Risk = Risk.Medium,
            NeedsReboot = true,
            DefaultSelected = false,
        }, journal, @"HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "DisableOverlays", RegistryValueKind.DWord, 1);
    }
}
