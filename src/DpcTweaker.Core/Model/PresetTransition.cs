namespace DpcTweaker.Core.Model;

/// <summary>What switching to a preset should do to the machine, resolved per row.</summary>
public sealed record TransitionPlan(
    IReadOnlyList<string> ToApply,
    IReadOnlyList<string> ToRevert)
{
    public bool IsNoOp => ToApply.Count == 0 && ToRevert.Count == 0;
}

/// <summary>
/// Turns "I want this preset" into concrete apply and revert lists, so a preset is a declared desired
/// state rather than only a filter on what to tick.
/// <para>
/// The rule is deliberately one-directional per row, and <b>revert wins over tick</b>: a row the chosen
/// preset leaves out is reverted even if it happens to be ticked, because pressing "use this preset" is an
/// explicit declaration by the user. That is what makes going Everything -> Safe actually undo the costly
/// tweaks instead of leaving them stranded.
/// </para>
/// </summary>
public static class PresetTransition
{
    public static TransitionPlan Plan(TweakPreset preset, IEnumerable<RowFacts> rows)
    {
        var apply = new List<string>();
        var revert = new List<string>();

        foreach (var r in rows)
        {
            if (!r.IsApplicable) continue;

            bool inPreset = TweakPresetSelector.Selects(preset, r.CostClass, r.Evidence);
            bool applied = r.Status is TweakStatus.Applied or TweakStatus.PendingReboot;
            bool needsApply = r.Status is TweakStatus.NotApplied or TweakStatus.Drifted or TweakStatus.Failed;

            // Outside the preset and already on the machine: put it back.
            if (!inPreset)
            {
                if (applied) revert.Add(r.Id);
                continue;
            }

            // Inside the preset: apply when it is needed and the row is ticked (or the user selected it).
            if (needsApply && r.IsSelected) apply.Add(r.Id);
        }

        return new TransitionPlan(apply, revert);
    }

    /// <summary>The confirmation text. Never apply or revert without showing this first.</summary>
    /// <param name="scope">
    /// The tab this applies to, so the confirmation names it. The same wording used to describe a
    /// change to everything; with a tier per tab, "Switch to Safe" alone does not say of what.
    /// Null means the whole app, which keeps the old wording.
    /// </param>
    public static string Describe(TweakPreset preset, TransitionPlan p, string? scope = null)
    {
        string name = TweakPresetSelector.Label(preset);
        string of = string.IsNullOrWhiteSpace(scope) ? "the machine" : scope;
        if (p.IsNoOp) return $"Nothing to change: {of} already matches \"{name}\".";

        var parts = new List<string>();
        if (p.ToApply.Count > 0) parts.Add($"apply {p.ToApply.Count}");
        if (p.ToRevert.Count > 0) parts.Add($"put back {p.ToRevert.Count} that this preset leaves out");
        string what = string.Join(" and ", parts);

        return p.ToRevert.Count > 0
            ? $"Switch {of} to \"{name}\": {what}.\n\nEverything put back is restored to the value it had before the app changed it, and the journal keeps that record either way."
            : $"Switch {of} to \"{name}\": {what}.";
    }
}