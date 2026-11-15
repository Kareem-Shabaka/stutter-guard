using DpcTweaker.Core.Model;

namespace DpcTweaker.UI;

public sealed class GroupHeaderViewModel
{
    public GroupHeaderViewModel(string title) => Title = title;
    public string Title { get; }
}

/// <summary>One tweak row: toggle, status chip, badges, now/planned, details, revert.</summary>
public sealed class TweakRowViewModel : ObservableObject
{
    private bool _isSelected;
    private string _statusText = "checking...";
    private string _statusKind = "unknown";
    private string _current = "";
    private string _planned = "";
    private string _note = "";
    private bool _isApplicable = true;
    private bool _expanded;
    private bool _tickDecided;

    /// <param name="carriedSelection">The tick from the previous list (after an options change), if this row was there.</param>
    private TweakPreset _preset;

    /// <param name="preset">Which cost tier is pre-selected. A preset can only remove from the
    /// tweaks that opt in by default; it never adds one the tweak itself opted out of.</param>
    /// <param name="carriedSelection">The tick from the previous list (after an options change), if this row was there.</param>
    public TweakRowViewModel(ITweak tweak, TweakPreset preset, bool? carriedSelection = null)
    {
        _preset = preset;
        Tweak = tweak;
        // Until the first status read, nothing is ticked. Then: ticked when the preset includes it.
        _isSelected = carriedSelection ?? false;
        _tickDecided = carriedSelection is not null;
    }

    public ITweak Tweak { get; }
    public TweakInfo Info => Tweak.Info;

    public string Title => Info.Title;
    public string Group => Info.Group;

    /// <summary>
    /// Set when one page shows several modules: lets a section say "Startup tasks" while the rows
    /// underneath still say "Boot tasks", so the row keeps its own identity and the page still groups.
    /// Null everywhere else, and a null caption is hidden by the template.
    /// </summary>
    public string? GroupCaption { get; set; }
    public bool HasGroupCaption => !string.IsNullOrEmpty(GroupCaption);
    public string What => Info.What;
    public string Why => Info.Why;
    public string Cost => Info.Cost;
    public string CanBreak => Info.CanBreak;
    public bool HasCanBreak => !string.IsNullOrWhiteSpace(Info.CanBreak);

    public string EvidenceText => Info.Evidence switch
    {
        Evidence.Measured => "Measured",
        Evidence.Documented => "Documented",
        _ => "Anecdotal",
    };

    public string EvidenceTip => Info.Evidence switch
    {
        Evidence.Measured => "Measured on a real rig (the author's desktop or laptop).",
        Evidence.Documented => "Documented by Microsoft or the hardware vendor.",
        _ => "Community reports only. Measure it yourself.",
    };

    public string RiskText => Info.Risk + " risk";
    public string RiskKind => Info.Risk.ToString().ToLowerInvariant();
    public string RebootText => Info.NeedsReboot ? "Needs a restart" : "";
    public bool NeedsReboot => Info.NeedsReboot;

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    /// <summary>
    /// Whether the chosen preset includes this row. This is what the checkbox shows: a preset's
    /// contents stay visible after it has been applied, instead of vanishing as the work gets done.
    /// </summary>
    public bool InPreset => TweakPresetSelector.Ticks(_preset, Info.CostClass, Info.Evidence, Info.DefaultSelected, IsApplicable);

    /// <summary>
    /// Re-decides the tick for a different tier <b>without touching the machine</b>, so picking a preset
    /// from the list shows what it covers before anything is applied. Explicit, so it overrides a carried
    /// tick the same way Load does on a preset change.
    /// </summary>
    public void SetPreset(TweakPreset preset)
    {
        if (_preset == preset) return;
        _preset = preset;
        OnPropertyChanged(nameof(PresetExclusion));
        OnPropertyChanged(nameof(HasPresetExclusion));
        OnPropertyChanged(nameof(InPreset));
        _tickDecided = true;
        IsSelected = InPreset;
    }

    /// <summary>
    /// True when pressing Apply selected would actually change this row. A ticked row that is already
    /// applied is included in its preset but needs no work, so the button leaves it alone.
    /// </summary>
    public bool NeedsWork => Status is TweakStatus.NotApplied or TweakStatus.Drifted or TweakStatus.Failed;

    public bool IsExpanded
    {
        get => _expanded;
        set => Set(ref _expanded, value);
    }

    public bool IsApplicable
    {
        get => _isApplicable;
        private set => Set(ref _isApplicable, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    /// <summary>ok / pending / drift / off / na / bad / unknown (the view maps these to colours).</summary>
    public string StatusKind
    {
        get => _statusKind;
        private set => Set(ref _statusKind, value);
    }

    public string Current
    {
        get => _current;
        private set => Set(ref _current, value);
    }

    public string Planned
    {
        get => _planned;
        private set => Set(ref _planned, value);
    }

    public string Note
    {
        get => _note;
        private set
        {
            if (Set(ref _note, value)) OnPropertyChanged(nameof(HasNote));
        }
    }

    public bool HasNote => !string.IsNullOrWhiteSpace(_note);

    /// <summary>Why the current preset leaves this row unticked, or null when it is ticked.</summary>
    public string PresetExclusion => TweakPresetSelector.ExclusionReason(_preset, Info.CostClass, Info.Evidence) ?? "";
    public bool HasPresetExclusion => PresetExclusion.Length > 0;
    public string CostClassText => Info.CostClass switch
    {
        TweakCost.Free => "no cost",
        TweakCost.Small => "small cost",
        TweakCost.Expensive => "asks a lot",
        _ => "unclassified",
    };
    /// <summary>Which tier this row falls in, so the list reads safe-first with a visible reason.</summary>
    public string RankText => TweakPresetSelector.RankLabel(
        TweakPresetSelector.RecommendationRank(Info.CostClass, Info.Evidence));

    public TweakStatus Status { get; private set; } = TweakStatus.Unknown;
    public bool SetByApp { get; private set; }

    public void Update(TweakState s)
    {
        Status = s.Status;
        SetByApp = s.SetByApp;
        (StatusText, StatusKind) = s.Status switch
        {
            TweakStatus.Applied => (s.SetByApp ? "Applied" : "Already set", "ok"),
            TweakStatus.PendingReboot => ("Pending restart", "pending"),
            TweakStatus.Drifted => ("Drifted (reset by update)", "drift"),
            TweakStatus.NotApplied => ("Not applied", "off"),
            TweakStatus.NotApplicable => ("Not applicable", "na"),
            TweakStatus.NotEffective => ("Not effective", "bad"),
            TweakStatus.Failed => ("Failed", "bad"),
            _ => ("Unknown", "unknown"),
        };
        Current = s.Current;
        Planned = s.Planned;
        Note = s.Note ?? "";
        IsApplicable = s.Status != TweakStatus.NotApplicable;
        if (!IsApplicable) IsSelected = false;

        // The checkbox shows what the preset includes, not what is left to do. An applied row stays
        // ticked, so the preset's contents can be read at any time; the status chip beside it already
        // says whether the value is on the machine. Apply selected uses NeedsWork, so a ticked row
        // that is already applied is not re-applied.
        if (!_tickDecided && s.Status != TweakStatus.Unknown)
        {
            _tickDecided = true;
            IsSelected = InPreset;
        }
    }

    /// <summary>
    /// After a successful apply the row is not unticked: it belongs to the preset either way, and a
    /// box that cleared itself the moment it worked would erase the answer to "what does this preset
    /// cover". The status chip is what changes.
    /// </summary>
    public void MarkDone()
    {
        _tickDecided = true;
    }
}
