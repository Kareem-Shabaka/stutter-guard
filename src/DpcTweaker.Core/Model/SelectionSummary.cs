namespace DpcTweaker.Core.Model;

/// <summary>What one row contributes to the "what will be used" preview.</summary>
public readonly record struct RowFacts(
    string Id,
    string Title,
    bool IsSelected,
    bool IsApplicable,
    bool DefaultSelected,
    TweakCost CostClass,
    Evidence Evidence,
    TweakStatus Status);

/// <summary>The tally behind the preview, so the wording and the counting are separable and testable.</summary>
public sealed record SelectionCounts(
    IReadOnlyList<string> WillApply,
    int AlreadyDone,
    int SkippedByPreset,
    int OptInOnly,
    int Idle,
    int NotApplicable);

/// <summary>
/// Answers "what will actually be used if I press Apply selected?".
/// <para>
/// A tick alone cannot answer it: a ticked row that is already applied changes nothing, and an unticked row
/// can be unticked because it is done, because the preset excludes it, or because the tweak opts in
/// individually. So the classification is status-first, and it is deliberately the same precedence the row
/// uses to decide its own tick — one model of the row, two consumers.
/// </para>
/// </summary>
public static class SelectionSummary
{
    public static SelectionCounts Compute(TweakPreset preset, IEnumerable<RowFacts> rows)
    {
        var willApply = new List<string>();
        int done = 0, skipped = 0, optIn = 0, idle = 0, notApplicable = 0;

        foreach (var r in rows)
        {
            if (!r.IsApplicable) { notApplicable++; continue; }
            // Status BEFORE the tick. Re-applying an already-applied value changes nothing, so counting a
            // ticked-but-done row as "will be applied" would overstate what pressing the button does.
            if (r.Status is TweakStatus.Applied or TweakStatus.PendingReboot)
            {
                done++;
                continue;
            }
            if (r.IsSelected) { willApply.Add(r.Title); continue; }
            if (!TweakPresetSelector.Selects(preset, r.CostClass, r.Evidence)) { skipped++; continue; }
            if (!r.DefaultSelected) { optIn++; continue; }
            idle++;
        }

        return new SelectionCounts(willApply, done, skipped, optIn, idle, notApplicable);
    }

    /// <summary>The one-line tally, e.g. "6 will be applied when you press Apply selected · 9 already done".</summary>
    public static string Describe(TweakPreset preset, SelectionCounts c)
    {
        var parts = new List<string>
        {
            c.WillApply.Count == 0
                ? "nothing will be applied"
                : $"{c.WillApply.Count} will be applied when you press Apply selected",
        };
        if (c.AlreadyDone > 0) parts.Add($"{c.AlreadyDone} already done");
        if (c.SkippedByPreset > 0) parts.Add($"{c.SkippedByPreset} skipped by {ShortLabel(preset)}");
        if (c.OptInOnly > 0) parts.Add($"{c.OptInOnly} opt-in only");
        if (c.NotApplicable > 0) parts.Add($"{c.NotApplicable} not applicable");
        return string.Join(" · ", parts);
    }

    /// <summary>The first few titles that will be applied, or an explanation when none will be.</summary>
    public static string Preview(SelectionCounts c, int show = 6)
    {
        if (c.WillApply.Count == 0)
        {
            if (c.NotApplicable > 0 && c.AlreadyDone == 0 && c.SkippedByPreset == 0 && c.OptInOnly == 0 && c.Idle == 0)
                return "";
            if (c.AlreadyDone > 0)
                return "Everything here is already applied, so there is nothing to tick. On a fresh machine the " +
                       "preset decides what would be ticked; here it still sets the order of the list below.";
            return "Nothing to do here right now. Pick a wider preset to include more, or press Select all.";
        }

        string head = string.Join(", ", c.WillApply.Take(show));
        return c.WillApply.Count > show
            ? $"Will be applied: {head} and {c.WillApply.Count - show} more."
            : $"Will be applied: {head}.";
    }

    /// <summary>"Safe (recommended)" -> "Safe", for use mid-sentence.</summary>
    public static string ShortLabel(TweakPreset p)
    {
        string label = TweakPresetSelector.Label(p);
        int i = label.IndexOf(" (", StringComparison.Ordinal);
        return i > 0 ? label[..i] : label;
    }
}
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
    public static string Describe(TweakPreset preset, TransitionPlan p)
    {
        string name = TweakPresetSelector.Label(preset);
        if (p.IsNoOp) return $"Nothing to change: the machine already matches \"{name}\".";

        var parts = new List<string>();
        if (p.ToApply.Count > 0) parts.Add($"apply {p.ToApply.Count}");
        if (p.ToRevert.Count > 0) parts.Add($"put back {p.ToRevert.Count} that this preset leaves out");
        string what = string.Join(" and ", parts);

        return p.ToRevert.Count > 0
            ? $"Switch to \"{name}\": {what}.\n\nEverything put back is restored to the value it had before the app changed it, and the journal keeps that record either way."
            : $"Switch to \"{name}\": {what}.";
    }
}
