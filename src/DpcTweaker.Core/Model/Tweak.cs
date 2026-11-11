using DpcTweaker.Core.Infra;

namespace DpcTweaker.Core.Model;

/// <summary>Live state of one tweak, as shown by the status chip.</summary>
public enum TweakStatus
{
    Unknown,
    Applied,
    NotApplied,
    PendingReboot,
    Drifted,
    NotEffective,
    NotApplicable,
    Failed,
}

/// <summary>How strong the evidence behind a tweak is.</summary>
public enum Evidence
{
    /// <summary>Measured on a real rig (the desktop or the Legion).</summary>
    Measured,
    /// <summary>Documented by Microsoft or the hardware vendor.</summary>
    Documented,
    /// <summary>Community reports only.</summary>
    Anecdotal,
}

public enum Risk { Low, Medium, High }

/// <summary>Everything the UI says about a tweak.</summary>
public sealed record TweakInfo
{
    public required string Id { get; init; }
    public required string Module { get; init; }
    public required string Title { get; init; }
    /// <summary>What it changes, in plain words.</summary>
    public string What { get; init; } = "";
    /// <summary>Why it helps latency, with the evidence.</summary>
    public string Why { get; init; } = "";
    /// <summary>What it costs: power, heat, features.</summary>
    public string Cost { get; init; } = "";
    /// <summary>What can break.</summary>
    public string CanBreak { get; init; } = "";
    public Evidence Evidence { get; init; } = Evidence.Documented;
    public Risk Risk { get; init; } = Risk.Low;
    public bool NeedsReboot { get; init; }
    public bool DefaultSelected { get; init; } = true;
    /// <summary>Optional group label inside a module (for example a GPU name).</summary>
    public string Group { get; init; } = "";
}

/// <param name="SetByApp">True when the journal says the app applied it (false = it was already like this).</param>
public sealed record TweakState(TweakStatus Status, string Current, string Planned, string? Note = null, bool SetByApp = false);

public interface ITweak
{
    TweakInfo Info { get; }
    TweakState Detect();
    void Apply(TweakContext ctx);
    void Revert(TweakContext ctx);
}

/// <summary>
/// A tweak whose changes need one follow-up action per batch, shared by tweaks with the same
/// key: for example restarting a network adapter once after several of its properties change.
/// </summary>
public interface IBatchFinisher
{
    string FinishKey { get; }
    void Finish();
}

/// <summary>What a tweak read from the system, before status rules are applied.</summary>
public sealed record Probe(bool Matches, string Current, string Planned, string? NotApplicableReason = null, string? Note = null);

/// <summary>Shared status rules: Applied / Pending reboot / Drifted / Not applied / Not applicable.</summary>
public abstract class TweakBase : ITweak
{
    protected TweakBase(TweakInfo info, Journal journal)
    {
        Info = info;
        Journal = journal;
    }

    public TweakInfo Info { get; }
    protected Journal Journal { get; }

    protected abstract Probe Read();

    public virtual TweakState Detect()
    {
        Probe p;
        try
        {
            p = Read();
        }
        catch (Exception ex)
        {
            return new TweakState(TweakStatus.Unknown, "?", "?", "Couldn't read it: " + ex.Message);
        }

        if (p.NotApplicableReason is not null)
            return new TweakState(TweakStatus.NotApplicable, p.Current, p.Planned, p.NotApplicableReason);

        if (p.Matches)
        {
            bool byApp = Journal.IsApplied(Info.Id);
            if (Info.NeedsReboot && Journal.AppliedAt(Info.Id) is DateTime appliedAt && appliedAt > BootTime.LastBoot)
                return new TweakState(TweakStatus.PendingReboot, p.Current, p.Planned, p.Note ?? "Restart to put it into effect.", byApp);
            return new TweakState(TweakStatus.Applied, p.Current, p.Planned,
                p.Note ?? (byApp ? null : "Already like this before the app (set by you or another tool). Nothing to apply."), byApp);
        }

        if (Journal.IsApplied(Info.Id))
            return new TweakState(TweakStatus.Drifted, p.Current, p.Planned,
                p.Note ?? "Applied earlier and changed since: usually a driver update, Windows Update or another tool.");

        return new TweakState(TweakStatus.NotApplied, p.Current, p.Planned, p.Note);
    }

    public abstract void Apply(TweakContext ctx);

    /// <summary>Default revert: restore everything this tweak recorded in the journal.</summary>
    public virtual void Revert(TweakContext ctx) => JournalRestorer.RestoreTweak(ctx.Journal, Info.Id);
}

/// <summary>Handed to Apply and Revert.</summary>
public sealed class TweakContext
{
    private readonly Lazy<BackupSet> _backup;

    public TweakContext(Journal journal, Func<BackupSet> backupFactory)
    {
        Journal = journal;
        _backup = new Lazy<BackupSet>(backupFactory);
    }

    public Journal Journal { get; }

    /// <summary>The backup folder for this session, created on first use.</summary>
    public BackupSet Backup => _backup.Value;
}
