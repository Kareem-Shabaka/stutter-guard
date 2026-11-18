using DpcTweaker.Core.Infra;
using DpcTweaker.Core.Model;

namespace DpcTweaker.Core.Tweaks;

/// <summary>
/// A change the app made earlier that is no longer part of the current plan or path. Revert-only: Apply
/// does nothing, Read reports the live state, and Revert restores whatever the journal recorded.
/// <para>
/// Two cases produce one of these, and both are about not losing a user's way back:
/// </para>
/// <list type="bullet">
/// <item>A path stopped building a kind of tweak - device pins on Balanced, or the CPU settings that
/// path no longer writes. The values are still on the machine, so the rows have to stay.</item>
/// <item>The catalog no longer offers a setting the user applied, for example after a CPU swap.</item>
/// </list>
/// <para>
/// This is deliberately not <see cref="TweakStatus.NotEffective"/>: these values are live, not failed,
/// and the row exists to undo them.
/// </para>
/// </summary>
public sealed class LeftoverTweak : TweakBase
{
    private readonly Func<Probe> _read;

    public LeftoverTweak(TweakInfo info, Journal journal, Func<Probe> read) : base(info, journal)
    {
        _read = read;
    }

    protected override Probe Read() => _read();

    public override void Apply(TweakContext ctx)
    {
        // Nothing to apply: this row exists so an earlier change can be reverted.
    }

    /// <summary>
    /// Shared info shape, so every leftover row reads the same way. Group and NeedsReboot are the
    /// caller's, because a leftover has to sit in the same section and carry the same restart advice as
    /// the row it replaces: an RSS revert is a registry keyword change that needs a restart, and a CPU
    /// power setting belongs under "CPU".
    /// </summary>
    public static TweakInfo MakeInfo(string id, string module, string title, string what,
        string group = "", bool needsReboot = false) => new()
    {
        Id = id,
        Module = module,
        Group = group,
        Title = title,
        What = what,
        Why = "Kept so every change the app made can be undone one by one.",
        Cost = "None.",
        Evidence = Evidence.Documented,
        CostClass = TweakCost.Free,
        Risk = Risk.Low,
        NeedsReboot = needsReboot,
        DefaultSelected = false,
    };
}
