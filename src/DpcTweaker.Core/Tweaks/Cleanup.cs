using System.Globalization;
using System.Text;
using DpcTweaker.Core.Catalog;
using DpcTweaker.Core.Detection;
using DpcTweaker.Core.Infra;
using DpcTweaker.Core.Model;

namespace DpcTweaker.Core.Tweaks;

/// <summary>Group headings and the "why" line per catalog group.</summary>
public static class CleanupGroups
{
    public static int Order(string group) => group switch
    {
        "telemetry" => 0,
        "latency" => 1,
        "vendor" => 2,
        "hardware" => 3,
        "disk" => 4,
        "maintenance" => 5,
        "optional" => 6,
        _ => 7,
    };

    public static string Heading(string group) => group switch
    {
        "telemetry" => "Telemetry",
        "latency" => "Latency (measured)",
        "vendor" => "Vendor updaters and helpers",
        "hardware" => "Hardware this PC doesn't have",
        "disk" => "Disk",
        "maintenance" => "Windows maintenance",
        "optional" => "Optional Windows features",
        "default" => "Back to Windows defaults",
        _ => "Other",
    };

    public static string Why(string group) => group switch
    {
        "telemetry" => "Usage and diagnostic data uploads. No feature you use depends on it; it's background work and network traffic.",
        "latency" => "Measured on the author's desktop: DPS triggered an all-core ~490 µs kernel event every 16 minutes. Disabling it removed the event.",
        "vendor" => "Background updaters and helpers start with Windows and wake up on timers. Manual means they no longer start with Windows. Updaters still run when their scheduled task or app calls them; other helpers (RGB, tuning and OEM utilities) can stop working until you set them back, which is why those are only in Aggressive.",
        "hardware" => "This PC doesn't have the hardware it serves, so it only adds a background process.",
        "disk" => "Only useful on spinning disks; this PC is SSD-only.",
        "maintenance" => "Runs on a timer or when the PC is idle. Harmless, but it's a burst of background work you can skip.",
        "optional" => "A Windows feature a gaming PC rarely uses.",
        "default" => "Microsoft's default start type for this service.",
        _ => "No measured latency effect; this is background-activity hygiene.",
    };

    public static Evidence ParseEvidence(string? e) => e switch
    {
        "Measured" => Evidence.Measured,
        "Documented" => Evidence.Documented,
        _ => Evidence.Anecdotal,
    };
}

/// <summary>Sets a service's start type with sc.exe; the old Start / DelayedAutostart values go to the journal first.</summary>
public sealed class ServiceStartTweak : TweakBase
{
    private readonly ServiceInfo _svc;
    private readonly StartType _target;
    private readonly bool _exact;

    public ServiceStartTweak(ServiceInfo svc, ServiceEntry entry, StartType target, bool exact, string group, Journal journal)
        : base(new TweakInfo
        {
            Id = "svc." + svc.Name,
            Module = ServicesModule.ModuleName,
            Group = CleanupGroups.Heading(group),
            Title = $"{svc.DisplayName} ({svc.Name}) → {StartTypes.Label(target)}",
            What = string.IsNullOrWhiteSpace(entry.What) ? svc.Description : entry.What,
            Why = CleanupGroups.Why(group) + (entry.Ms is { Length: > 0 } ms ? $" Microsoft's server guidance: {ms}." : ""),
            Cost = string.IsNullOrWhiteSpace(entry.IfOff) ? "Unknown." : entry.IfOff,
            CanBreak = entry.IfOff,
            Evidence = CleanupGroups.ParseEvidence(entry.Evidence),
            Risk = group is "hardware" or "disk" or "latency" || target == StartType.Disabled && group == "optional" ? Risk.Medium : Risk.Low,
            NeedsReboot = true,
        }, journal)
    {
        _svc = svc;
        _target = target;
        _exact = exact;
    }

    public static StartType ReadStart(string name)
    {
        string key = @"HKLM\SYSTEM\CurrentControlSet\Services\" + name;
        return StartTypes.FromRegistry(RegistryOps.ReadDword(key, "Start"), RegistryOps.ReadDword(key, "DelayedAutostart"));
    }

    protected override Probe Read()
    {
        if (!RegistryOps.KeyExists(_svc.RegistryKey)) return new Probe(false, "not installed", StartTypes.Label(_target), "The service isn't installed any more.");
        var cur = ReadStart(_svc.Name);
        bool ok = _exact ? cur == _target : StartTypes.Rank(cur) <= StartTypes.Rank(_target);
        string planned = StartTypes.Label(_target) + (_exact || _target == StartType.Disabled ? "" : " (or stricter)");
        return new Probe(ok, StartTypes.Label(cur), planned,
            Note: ok && !_exact && cur != _target ? "Already stricter than this preset; the app never loosens a service." : null);
    }

    public override void Apply(TweakContext ctx)
    {
        string key = _svc.RegistryKey;
        uint? start = RegistryOps.ReadDword(key, "Start");
        uint? delayed = RegistryOps.ReadDword(key, "DelayedAutostart");
        if (start is null) throw new InvalidOperationException("The service isn't installed any more.");
        ctx.Backup.ExportKeyOnce(key);
        ctx.Journal.RecordOriginal(Info.Id, "svc", _svc.Name, true, null,
            start.Value.ToString(CultureInfo.InvariantCulture) + "|" + (delayed?.ToString(CultureInfo.InvariantCulture) ?? ""));
        ServiceControl.SetStart(_svc.Name, _target);
        var back = ReadStart(_svc.Name);
        if (back != _target) throw new InvalidOperationException($"sc.exe ran, but the start type reads back as {StartTypes.Label(back)}.");
    }
}

public static class ServiceControl
{
    public static void SetStart(string name, StartType t)
    {
        var r = ProcessRunner.Run(ProcessRunner.System32("sc.exe"), new[] { "config", name, "start=", StartTypes.ScWord(t) }, 60_000, Encoding.Default);
        if (!r.Ok)
        {
            string msg = r.Combined.Trim();
            if (msg.Contains("5:") || msg.Contains("Access is denied", StringComparison.OrdinalIgnoreCase))
                msg = "Windows refused the change (access denied): this service is protected.";
            throw new InvalidOperationException(msg);
        }
        Log.Info($"Service {name} start = {StartTypes.Label(t)}");
    }

    /// <summary>Journal data "start|delayed" back to a start type.</summary>
    public static StartType FromJournal(string? data)
    {
        if (string.IsNullOrEmpty(data)) return StartType.Unknown;
        var parts = data.Split('|');
        uint? start = uint.TryParse(parts[0], out uint s) ? s : null;
        uint? delayed = parts.Length > 1 && uint.TryParse(parts[1], out uint d) ? d : null;
        return StartTypes.FromRegistry(start, delayed);
    }
}

/// <summary>Disables a scheduled task with schtasks.exe; its enabled state goes to the journal first.</summary>
public sealed class TaskDisableTweak : TweakBase
{
    private readonly string _path;

    public TaskDisableTweak(TaskDetails task, TaskEntry entry, Journal journal) : base(new TweakInfo
    {
        Id = "task." + task.Path,
        Module = TasksModule.ModuleName,
        Group = CleanupGroups.Heading(entry.Group),
        Title = $"{task.Name} → disabled",
        What = (entry.What.Length > 0 ? entry.What : "No description.") + $" ({task.Folder}; runs {task.ActionsText})",
        Why = CleanupGroups.Why(entry.Group) + (entry.Idle ? " It runs when the PC is idle, so it shows up in long hands-off LatencyMon runs." : ""),
        Cost = entry.IfOff,
        CanBreak = entry.IfOff + (string.IsNullOrEmpty(entry.ComesBack) ? "" : " " + entry.ComesBack),
        Evidence = CleanupGroups.ParseEvidence(entry.Evidence),
        Risk = Risk.Low,
    }, journal)
    {
        _path = task.Path;
    }

    protected override Probe Read()
    {
        var t = TaskSched.ListAll().FirstOrDefault(x => string.Equals(x.Path, _path, StringComparison.OrdinalIgnoreCase));
        if (t is null && TaskSched.LastListError is { } err) throw new InvalidOperationException("Task Scheduler couldn't be read: " + err);
        if (t is null) return new Probe(true, "not there any more", "disabled", "The task no longer exists (an update or uninstall removed it).");
        return new Probe(!t.Enabled, t.Enabled ? $"enabled ({t.State})" : "disabled", "disabled");
    }

    public override void Apply(TweakContext ctx)
    {
        var t = TaskSched.ListAll(force: true).FirstOrDefault(x => string.Equals(x.Path, _path, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("The task no longer exists.");
        ctx.Journal.RecordOriginal(Info.Id, "task-enabled", _path, true, null, t.Enabled ? "1" : "0");
        TaskSched.SetEnabled(_path, false);
    }
}

/// <summary>A change the app made earlier that the current preset doesn't include: kept so it can still be reverted.</summary>
public sealed class RevertOnlyTweak : TweakBase
{
    private readonly Func<string> _current;

    public RevertOnlyTweak(string id, string module, string title, Func<string> current, Journal journal) : base(new TweakInfo
    {
        Id = id,
        Module = module,
        Group = "Changed earlier, not in this preset",
        Title = title,
        What = "The app changed this earlier. The preset you picked now doesn't include it.",
        Why = "Kept so every change can be undone one by one.",
        Cost = "None.",
        Evidence = Evidence.Documented,
        Risk = Risk.Low,
        DefaultSelected = false,
    }, journal)
    {
        _current = current;
    }

    protected override Probe Read() =>
        new(true, _current(), "revert to what it was before the app", Note: "Tick it and press Revert selected (or use its Revert button) to put it back.");

    public override void Apply(TweakContext ctx)
    {
        // Nothing to apply: this row exists for its Revert.
    }
}

public static class ServicesModule
{
    public const string ModuleName = "Services";

    public static IEnumerable<ITweak> Build(SystemProfile profile, Journal journal, AppOptions options)
    {
        var preset = options.Cleanup;
        var rows = new List<(int Order, string Name, ITweak Tweak)>();
        foreach (var svc in profile.Services.Where(s => !s.IsPerUserInstance))
        {
            string id = "svc." + svc.Name;
            bool touched = journal.IsApplied(id) || journal.OriginalsFor(id).Count > 0;
            var entry = Catalogs.Services.Find(svc.Name);
            ITweak? row = null;
            int order = 9;

            bool exactMode = preset == CleanupPreset.WindowsDefault;
            // "Windows default" only puts back Windows' own services (and anything the app changed itself):
            // a third-party service's "default" is whatever the user chose when installing it.
            bool allowed = !exactMode || svc.IsWindowsBinary || touched;
            if (entry is not null && allowed && !entry.IsLocked && entry.Group != "peruser" &&
                svc.Start is not (StartType.Boot or StartType.System or StartType.Unknown))
            {
                bool exact = exactMode;
                string? targetText = entry.Target(preset);
                bool hardwarePresent = !exact && entry.OnlyIfAbsent is not null && profile.Hardware.Has(entry.OnlyIfAbsent);
                var target = StartTypes.Parse(targetText);
                if (target != StartType.Unknown && !hardwarePresent)
                {
                    bool differs = exact ? svc.Start != target : StartTypes.Rank(svc.Start) > StartTypes.Rank(target);
                    if (differs || touched)
                    {
                        string group = exact ? "default" : entry.Group;
                        row = new ServiceStartTweak(svc, entry, target, exact, group, journal);
                        order = exact ? 0 : CleanupGroups.Order(entry.Group);
                    }
                }
            }
            if (row is null && touched)
                row = new RevertOnlyTweak(id, ModuleName, $"{svc.DisplayName} ({svc.Name})",
                    () => StartTypes.Label(ServiceStartTweak.ReadStart(svc.Name)), journal);
            if (row is not null) rows.Add((order, svc.DisplayName, row));
        }

        // Changes in the journal for services this scan didn't list (scan failed, or renamed): still revertible.
        var seen = profile.Services.Select(s => "svc." + s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string id in journal.TweakIds().Where(i => i.StartsWith("svc.", StringComparison.OrdinalIgnoreCase) && !seen.Contains(i)))
        {
            string name = id[4..];
            rows.Add((8, name, new RevertOnlyTweak(id, ModuleName, name, () => StartTypes.Label(ServiceStartTweak.ReadStart(name)), journal)));
        }
        return rows.OrderBy(r => r.Order).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).Select(r => r.Tweak);
    }
}

public static class TasksModule
{
    public const string ModuleName = "Tasks";

    public static IEnumerable<ITweak> Build(SystemProfile profile, Journal journal, AppOptions options)
    {
        var preset = options.Cleanup;
        var rows = new List<(int Order, string Path, ITweak Tweak)>();
        foreach (var task in profile.Tasks)
        {
            string id = "task." + task.Path;
            bool touched = journal.IsApplied(id) || journal.OriginalsFor(id).Count > 0;
            var entry = Catalogs.Tasks.Find(task.Path);
            ITweak? row = null;
            int order = 9;
            if (entry is not null && !entry.IsLocked && entry.Group != "app" && entry.Disables(preset) && (task.Enabled || touched))
            {
                row = new TaskDisableTweak(task, entry, journal);
                order = CleanupGroups.Order(entry.Group);
            }
            else if (touched)
            {
                string path = task.Path;
                row = new RevertOnlyTweak(id, ModuleName, task.Name + " (" + task.Folder + ")",
                    () => TaskSched.ListAll().FirstOrDefault(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase)) is { } t
                        ? (t.Enabled ? "enabled" : "disabled")
                        : "not there any more", journal);
            }
            if (row is not null) rows.Add((order, task.Path, row));
        }

        var seen = profile.Tasks.Select(t => "task." + t.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string id in journal.TweakIds().Where(i => i.StartsWith("task.", StringComparison.OrdinalIgnoreCase) && !seen.Contains(i)))
        {
            string path = id[5..];
            rows.Add((8, path, new RevertOnlyTweak(id, ModuleName, path,
                () => TaskSched.ListAll().FirstOrDefault(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase)) is { } t
                    ? (t.Enabled ? "enabled" : "disabled") : "not there any more", journal)));
        }
        return rows.OrderBy(r => r.Order).ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase).Select(r => r.Tweak);
    }
}
