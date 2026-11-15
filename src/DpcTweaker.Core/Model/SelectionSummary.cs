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