namespace DpcTweaker.Core.Model;

/// <summary>
/// Which tweaks are pre-selected. A ladder: each step admits one more class of cost, so a user can
/// stop where they are comfortable instead of choosing tweaks one by one.
/// <para>
/// Two rules are absolute and are enforced by <see cref="TweakPresetSelector"/>, not by convention:
/// a tweak graded <see cref="Evidence.Anecdotal"/> is never selected below <see cref="Everything"/>,
/// and a tweak nobody has classified is never selected at all.
/// </para>
/// </summary>
public enum TweakPreset
{
    /// <summary>Only tweaks with no measurable cost: nothing that spends power, heat or a feature.</summary>
    Essential,

    /// <summary>Essential plus tweaks with a real but small cost. The default recommendation.</summary>
    Safe,

    /// <summary>
    /// Safe plus the tweaks that ask a lot for what they offer. Their own step on purpose: they are
    /// worth having available and worth keeping out of a recommendation.
    /// </summary>
    Costly,

    /// <summary>Everything, including tweaks supported only by community reports.</summary>
    Everything,
}

/// <summary>
/// The preset rule. Pure, so every branch is testable without building a tweak list.
/// </summary>
public static class TweakPresetSelector
{
    /// <summary>Whether a tweak of this cost and evidence is pre-selected by the given preset.</summary>
    public static bool Selects(TweakPreset preset, TweakCost cost, Evidence evidence)
    {
        // Community-only evidence is never chosen for the user by a preset. That is the whole reason
        // the Evidence field exists; using it only in the UI left it unenforced.
        if (evidence == Evidence.Anecdotal && preset != TweakPreset.Everything) return false;

        // A tweak nobody has classified must never be ticked: silence is not consent.
        if (cost == TweakCost.Unclassified) return false;

        return preset switch
        {
            TweakPreset.Essential => cost == TweakCost.Free,
            TweakPreset.Safe => cost is TweakCost.Free or TweakCost.Small,
            // Costly and Everything admit every classified cost; only Evidence differs between them.
            _ => true,
        };
    }

    /// <summary>
    /// Whether the preset <b>ticks</b> this row. The tick answers "does this preset include it", not
    /// "does it still need doing": a row that is already applied belongs to the preset just as much as
    /// one that is not, and showing it unticked made a preset's contents impossible to read once
    /// applied. Whether pressing Apply changes anything is answered separately, from the row's status.
    /// </summary>
    /// <param name="defaultSelected">The tweak's own opt-in choice. A preset never adds a tweak that
    /// opted out of being on by default, and never drops one that opted in on cost grounds alone.</param>
    /// <param name="applicable">False for a row this machine cannot use at all.</param>
    public static bool Ticks(TweakPreset preset, TweakCost cost, Evidence evidence, bool defaultSelected, bool applicable) =>
        applicable && defaultSelected && Selects(preset, cost, evidence);
    /// <summary>Why this tweak is not ticked by the preset, or null when it is.</summary>
    public static string? ExclusionReason(TweakPreset preset, TweakCost cost, Evidence evidence)
    {
        if (Selects(preset, cost, evidence)) return null;
        if (cost == TweakCost.Unclassified) return "Not classified yet, so no preset ticks it.";
        if (evidence == Evidence.Anecdotal) return "Community reports only: tick it yourself if you have measured a benefit.";
        return cost == TweakCost.Expensive
            ? "Asks more than it gives: use the \"costly\" preset if you want it."
            : "Costs a little more than this preset allows.";
    }

    /// <summary>
    /// Where a tweak belongs in a list, best-first. This is what puts the Safe set at the top of every
    /// section, so the page reads "safe first, then the ones that ask more, then the unproven" instead of
    /// in registry-key order.
    /// <para>
    /// It is also what makes the preset picker visible on a machine where everything is already applied:
    /// nothing can be ticked there, but the order still reflects the choice.
    /// </para>
    /// </summary>
    /// <returns>0 Safe (free/small, non-anecdotal), 1 Costly, 2 unproven (anecdotal), 3 unclassified.</returns>
    public static int RecommendationRank(TweakCost cost, Evidence evidence)
    {
        if (cost == TweakCost.Unclassified) return 3;
        if (evidence == Evidence.Anecdotal) return 2;
        return cost is TweakCost.Free or TweakCost.Small ? 0 : 1;
    }

    /// <summary>A short heading fragment for the rank, used in the row's badge.</summary>
    public static string RankLabel(int rank) => rank switch
    {
        0 => "Safe set",
        1 => "Asks more",
        2 => "Unproven",
        _ => "Unclassified",
    };
    public static string Label(TweakPreset p) => p switch
    {
        TweakPreset.Essential => "Essential (free only)",
        TweakPreset.Safe => "Safe (recommended)",
        TweakPreset.Costly => "Costly (adds the expensive ones)",
        _ => "Everything (includes unproven)",
    };

    public static string Description(TweakPreset p) => p switch
    {
        TweakPreset.Essential => "Only tweaks with no measurable cost: no idle power, no heat, no lost feature. The smallest set that still does something.",
        TweakPreset.Safe => "Essential plus tweaks with a small, real cost such as a little idle power or one adapter restart. Community-only tweaks are never included.",
        TweakPreset.Costly => "Safe plus the tweaks that ask a lot for what they offer: locked memory clocks, a held timer, flow control off. Worth having, not worth recommending.",
        _ => "Every tweak, including the ones supported only by community reports. Nothing is measured for you here.",
    };
}