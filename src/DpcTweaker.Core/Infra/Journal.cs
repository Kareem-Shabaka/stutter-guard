using System.Text.Json;
using System.Text.Json.Serialization;

namespace DpcTweaker.Core.Infra;

/// <summary>
/// One recorded "before" state. Kinds:
///   reg            Target = "HKLM\path|ValueName"; Existed/ValueKind/Data = the old value
///   power          Target = "scheme|subgroup|setting|AC" (or DC); Data = old value index
///   power-active   Target = "active"; Data = the scheme GUID that was active
///   plan-created   Target = scheme GUID the app created (revert deletes it)
///   task           Target = full task path the app created (revert deletes it)
///   file           Target = file the app created (revert deletes it)
///   devicepower    Target = "devices"; revert re-ticks from device-power\backup.csv
///   svc            Target = service name; Data = "Start|DelayedAutostart" registry values before the change
///   task-enabled   Target = full task path; Data = "1" if it was enabled before the app disabled it
/// </summary>
public sealed class JournalEntry
{
    public string TweakId { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Target { get; set; } = "";
    public bool Existed { get; set; }
    public string? ValueKind { get; set; }
    public string? Data { get; set; }
    public DateTime When { get; set; }
}

public sealed class JournalData
{
    public int Version { get; set; } = 1;
    public List<JournalEntry> Originals { get; set; } = new();
    public Dictionary<string, DateTime> Applied { get; set; } = new();
    public Dictionary<string, string> State { get; set; } = new();
}

/// <summary>
/// An open write-ahead transaction for one tweak. While it is open every RecordOriginal is
/// flushed to disk (and to undo-all.ps1) before the caller changes anything, so a crash can
/// never leave the machine changed with no record of its previous state.
/// </summary>
public sealed class JournalTransaction
{
    internal JournalTransaction(string tweakId, int originalsMark, bool hadApplied)
    {
        TweakId = tweakId;
        OriginalsMark = originalsMark;
        HadApplied = hadApplied;
    }

    public string TweakId { get; }

    /// <summary>How many entries existed when the transaction opened: everything after it is the transaction's.</summary>
    internal int OriginalsMark { get; }

    /// <summary>True when the tweak was already marked applied by an earlier run.</summary>
    internal bool HadApplied { get; }
}

/// <summary>
/// The tweak journal: the exact state before every change, per tweak, plus which
/// tweaks the app applied (so a later mismatch shows as Drifted, not Not applied).
/// Saved to C:\ProgramData\Stutter Guard\journal.json after every change, before the
/// change itself is made.
/// </summary>
public sealed class Journal
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _gate = new();
    private readonly string _file;
    private readonly List<JournalTransaction> _txns = new();
    private JournalData _data;

    public Journal(string file)
    {
        _file = file;
        _data = Load(file, out bool degraded);
        IsDegraded = degraded;
    }

    public static Journal OpenDefault()
    {
        Paths.EnsureDataDirs();
        return new Journal(Paths.JournalFile);
    }

    public string FilePath => _file;

    /// <summary>
    /// True when journal.json existed but couldn't be read at startup. The app keeps working,
    /// but it must not overwrite a good undo-all.ps1 with the empty state it recovered with:
    /// that script is the only way back when the app itself won't run.
    /// </summary>
    public bool IsDegraded { get; private set; }

    /// <summary>
    /// Called after every durable flush so undo-all.ps1 is regenerated in lockstep with
    /// journal.json. The engine points this at <see cref="JournalRestorer.WriteUndoScripts"/>.
    /// </summary>
    public Action<Journal>? SidecarWriter { get; set; }

    private static JournalData Load(string file, out bool degraded)
    {
        degraded = false;
        if (!File.Exists(file)) return new JournalData();

        string? text = null;
        try
        {
            text = File.ReadAllText(file);
            if (text.Trim().Length == 0)
            {
                // An empty file is a half-written save: keep a copy and start over.
                degraded = true;
                PreserveBroken(file);
                Log.Error("Journal was empty, starting a new one.");
                return new JournalData();
            }
            var data = JsonSerializer.Deserialize<JournalData>(text, JsonOpts);
            if (data is null)
            {
                degraded = true;
                PreserveBroken(file);
                Log.Error("Journal deserialized to null, starting a new one.");
                return new JournalData();
            }
            // JsonSerializer assigns an explicit null over an initialized default, so a journal
            // containing "Originals": null would deserialize "successfully" and then throw a
            // NullReferenceException on the first read. Coalesce before anything touches it.
            bool repaired = false;
            if (data.Originals is null) { data.Originals = new(); repaired = true; }
            if (data.Applied is null) { data.Applied = new(); repaired = true; }
            if (data.State is null) { data.State = new(); repaired = true; }
            if (repaired) Log.Warn("Journal had null collections; treated them as empty.");
            return data;
        }
        catch (Exception ex)
        {
            degraded = true;
            PreserveBroken(file);
            Log.Error("Journal unreadable, starting a new one: " + ex.Message);
            return new JournalData();
        }
    }

    /// <summary>Keep the broken file for inspection rather than overwriting it.</summary>
    private static void PreserveBroken(string file)
    {
        try { File.Copy(file, file + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), overwrite: true); } catch { }
    }

    /// <summary>
    /// Explicit recovery: accept the recovered (empty) journal and allow journal.json and
    /// undo-all.ps1 to be written again. The .broken copy is never deleted, so the old
    /// before-states stay on disk.
    /// </summary>
    public void RecoverDegraded()
    {
        lock (_gate) IsDegraded = false;
        Log.Warn("Journal degradation latch cleared by explicit user recovery. The .broken copy is kept.");
    }

    /// <summary>
    /// Atomic: write a temp file, then swap it in, so a crash never leaves half a journal.
    /// Refuses while the journal is degraded: the in-memory document came up empty, so writing it
    /// would destroy the recorded before-states (they survive only in the .broken copy).
    /// </summary>
    public void Save()
    {
        lock (_gate)
        {
            if (IsDegraded)
                throw new InvalidOperationException(
                    "Journal is in degraded protection mode: it couldn't be read at startup, so it won't be " +
                    "overwritten. Restore from the .broken copy or call RecoverDegraded() first.");
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            string json = JsonSerializer.Serialize(_data, JsonOpts);
            string tmp = _file + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _file, overwrite: true);
        }
    }

    /// <summary>
    /// Persist the journal (and optionally undo-all.ps1) from it.
    /// <para>
    /// Inside a tweak transaction this THROWS when the write fails: the caller is about to change
    /// the system and must not do so without a durable before-state. Outside a transaction it only
    /// logs, because it runs from finally blocks where throwing would mask the real error.
    /// </para>
    /// <param name="includeSidecar">
    /// False for the write-ahead step (journal.json alone is the fast WAL record); true once per
    /// tweak on commit, so undo-all.ps1 isn't rewritten for every single entry.
    /// </param>
    /// </summary>
    public void Flush(bool includeSidecar = true)
    {
        bool saved = true;
        try
        {
            Save();
        }
        catch (Exception ex)
        {
            if (_txns.Count > 0) throw;   // WAL: the caller must abort instead of mutating the system
            saved = false;
            Log.Error("Journal save failed: " + ex.Message);
        }
        if (!includeSidecar || !saved) return;   // never let the two files drift apart
        try { SidecarWriter?.Invoke(this); } catch (Exception ex) { Log.Warn("undo-all.ps1 refresh failed: " + ex.Message); }
    }

    // ---------------------------------------------------------------- write-ahead transactions

    public JournalTransaction BeginTransaction(string tweakId)
    {
        lock (_gate)
        {
            var txn = new JournalTransaction(tweakId, _data.Originals.Count, _data.Applied.ContainsKey(tweakId));
            _txns.Add(txn);
            return txn;
        }
    }

    public void CommitTransaction(JournalTransaction txn)
    {
        lock (_gate) _txns.Remove(txn);
    }

    /// <summary>
    /// The tweak failed, so its before-states are phantom records: drop everything this
    /// transaction added and leave journal.json / undo-all.ps1 as they were. Only the newest
    /// transaction can be rolled back (that is the only shape the engine uses).
    /// </summary>
    public bool RollbackTransaction(JournalTransaction txn)
    {
        lock (_gate)
        {
            if (_txns.Count == 0 || !ReferenceEquals(_txns[^1], txn))
            {
                Log.Warn("Rollback ignored: journal transaction for " + txn.TweakId + " is not the newest one.");
                return false;
            }
            _txns.RemoveAt(_txns.Count - 1);

            bool changed = false;
            if (txn.OriginalsMark < _data.Originals.Count)
            {
                _data.Originals.RemoveRange(txn.OriginalsMark, _data.Originals.Count - txn.OriginalsMark);
                changed = true;
            }
            if (!txn.HadApplied && _data.Applied.Remove(txn.TweakId)) changed = true;
            return changed;
        }
    }

    /// <summary>
    /// Records the before-state, only the first time this tweak touches this target.
    /// Inside a transaction the record is flushed to disk (and to undo-all.ps1) before
    /// this method returns, so the caller may mutate the system afterwards.
    /// </summary>
    public void RecordOriginal(string tweakId, string kind, string target, bool existed, string? valueKind, string? data)
    {
        bool added = false;
        bool inTransaction;
        lock (_gate)
        {
            inTransaction = _txns.Count > 0;
            bool already = _data.Originals.Any(o =>
                o.TweakId == tweakId && o.Kind == kind && string.Equals(o.Target, target, StringComparison.OrdinalIgnoreCase));
            if (!already)
            {
                _data.Originals.Add(new JournalEntry
                {
                    TweakId = tweakId,
                    Kind = kind,
                    Target = target,
                    Existed = existed,
                    ValueKind = valueKind,
                    Data = data,
                    When = DateTime.Now,
                });
                added = true;
            }
        }
        // Write-ahead: the before-state is durable before the change happens. journal.json only;
        // undo-all.ps1 is regenerated once per tweak on commit instead of once per entry.
        if (added && inTransaction) Flush(includeSidecar: false);
    }

    public IReadOnlyList<JournalEntry> OriginalsFor(string tweakId)
    {
        lock (_gate) return _data.Originals.Where(o => o.TweakId == tweakId).ToList();
    }

    /// <summary>For revert-all: the oldest entry per (kind, target), newest change undone first.</summary>
    public IReadOnlyList<JournalEntry> OldestPerTarget()
    {
        lock (_gate)
        {
            return _data.Originals
                .GroupBy(o => (o.Kind, Target: o.Target.ToUpperInvariant()))
                .Select(g => g.OrderBy(o => o.When).First())
                .OrderByDescending(o => o.When)
                .ToList();
        }
    }

    public IReadOnlyList<string> TweakIds()
    {
        lock (_gate)
            return _data.Originals.Select(o => o.TweakId).Concat(_data.Applied.Keys).Distinct().ToList();
    }

    /// <summary>
    /// Moves every entry from one tweak id to another. Used to repair an id no tweak owns
    /// any more (its entries would be unrevertible and would keep showing up in undo-all.ps1).
    /// </summary>
    public int RebindTweak(string from, string to)
    {
        if (string.Equals(from, to, StringComparison.Ordinal)) return 0;
        lock (_gate)
        {
            int moved = 0;
            foreach (var e in _data.Originals.Where(o => o.TweakId == from).ToList())
            {
                bool duplicate = _data.Originals.Any(o => !ReferenceEquals(o, e) && o.TweakId == to && o.Kind == e.Kind &&
                    string.Equals(o.Target, e.Target, StringComparison.OrdinalIgnoreCase));
                if (duplicate) continue;
                e.TweakId = to;
                moved++;
            }
            if (moved > 0) _data.Originals.RemoveAll(o => o.TweakId == from);

            if (_data.Applied.TryGetValue(from, out var when))
            {
                _data.Applied.Remove(from);
                if (!_data.Applied.ContainsKey(to)) _data.Applied[to] = when;
                moved++;
            }
            return moved;
        }
    }

    public void MarkApplied(string tweakId)
    {
        lock (_gate) _data.Applied[tweakId] = DateTime.Now;
    }

    public bool IsApplied(string tweakId)
    {
        lock (_gate) return _data.Applied.ContainsKey(tweakId);
    }

    public DateTime? AppliedAt(string tweakId)
    {
        lock (_gate) return _data.Applied.TryGetValue(tweakId, out var t) ? t : null;
    }

    /// <summary>After a successful revert: forget the tweak.</summary>
    public void ClearTweak(string tweakId)
    {
        lock (_gate)
        {
            _data.Originals.RemoveAll(o => o.TweakId == tweakId);
            _data.Applied.Remove(tweakId);
        }
    }

    /// <summary>
    /// Forget everything. Refuses on a journal that failed to load, unless recovery was
    /// explicit: wiping it would also wipe the evidence of what still has to be undone.
    /// </summary>
    public bool ClearAll(bool force = false)
    {
        lock (_gate)
        {
            if (IsDegraded && !force)
            {
                Log.Warn("ClearAll refused: the journal was unreadable at startup.");
                return false;
            }
            _data.Originals.Clear();
            _data.Applied.Clear();
            return true;
        }
    }

    public string? GetState(string key)
    {
        lock (_gate) return _data.State.TryGetValue(key, out var v) ? v : null;
    }

    public void SetState(string key, string? value)
    {
        lock (_gate)
        {
            if (value is null) _data.State.Remove(key);
            else _data.State[key] = value;
        }
    }

    public int EntryCount
    {
        get { lock (_gate) return _data.Originals.Count; }
    }

    public int AppliedCount
    {
        get { lock (_gate) return _data.Applied.Count; }
    }
}