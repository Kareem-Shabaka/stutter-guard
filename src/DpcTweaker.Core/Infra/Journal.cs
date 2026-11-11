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
/// The tweak journal: the exact state before every change, per tweak, plus which
/// tweaks the app applied (so a later mismatch shows as Drifted, not Not applied).
/// Saved to C:\ProgramData\DPC Latency Tweaker\journal.json after every change.
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
    private JournalData _data;

    public Journal(string file)
    {
        _file = file;
        _data = Load(file);
    }

    public static Journal OpenDefault()
    {
        Paths.EnsureDataDirs();
        return new Journal(Paths.JournalFile);
    }

    public string FilePath => _file;

    private static JournalData Load(string file)
    {
        try
        {
            if (File.Exists(file))
                return JsonSerializer.Deserialize<JournalData>(File.ReadAllText(file), JsonOpts) ?? new JournalData();
        }
        catch (Exception ex)
        {
            // Keep the broken file for inspection rather than overwriting it.
            try { File.Copy(file, file + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), overwrite: true); } catch { }
            Log.Error("Journal unreadable, starting a new one: " + ex.Message);
        }
        return new JournalData();
    }

    public void Save()
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            string tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_data, JsonOpts));
            File.Move(tmp, _file, overwrite: true);
        }
    }

    /// <summary>Records the before-state, only the first time this tweak touches this target.</summary>
    public void RecordOriginal(string tweakId, string kind, string target, bool existed, string? valueKind, string? data)
    {
        lock (_gate)
        {
            bool already = _data.Originals.Any(o =>
                o.TweakId == tweakId && o.Kind == kind && string.Equals(o.Target, target, StringComparison.OrdinalIgnoreCase));
            if (already) return;
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
        }
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

    public void ClearAll()
    {
        lock (_gate)
        {
            _data.Originals.Clear();
            _data.Applied.Clear();
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
