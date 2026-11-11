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
    public TweakRowViewModel(ITweak tweak, bool? carriedSelection = null)
    {
        Tweak = tweak;
        // Until the first status read, nothing is ticked. Then: ticked only if it still needs doing.
        _isSelected = carriedSelection ?? false;
        _tickDecided = carriedSelection is not null;
    }

    public ITweak Tweak { get; }
    public TweakInfo Info => Tweak.Info;

    public string Title => Info.Title;
    public string Group => Info.Group;
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

        // The checkbox means "include in Apply selected", so on the first read only rows
        // that still need doing get ticked (if the tweak is on by default for this PC).
        if (!_tickDecided && s.Status != TweakStatus.Unknown)
        {
            _tickDecided = true;
            IsSelected = IsApplicable && Info.DefaultSelected &&
                         s.Status is TweakStatus.NotApplied or TweakStatus.Drifted or TweakStatus.Failed;
        }
    }

    /// <summary>After a successful apply the row is done: untick it.</summary>
    public void MarkDone()
    {
        _tickDecided = true;
        IsSelected = false;
    }
}
