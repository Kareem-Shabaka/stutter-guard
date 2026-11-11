using System.Xml.Linq;
using DpcTweaker.Core.Infra;

namespace DpcTweaker.Core.Detection;

/// <summary>One scheduled task, as read through the Task Scheduler API as administrator.</summary>
public sealed class TaskDetails
{
    public required string Path { get; init; }
    public bool Enabled { get; init; }
    public string State { get; init; } = "";
    public string Author { get; init; } = "";
    public string RunAs { get; init; } = "";
    public List<string> Actions { get; init; } = new();
    public List<string> Triggers { get; init; } = new();
    /// <summary>Has an idle trigger, runs only when idle, or is automatic maintenance.</summary>
    public bool IdleTriggered { get; init; }
    public bool Hidden { get; init; }
    public DateTime? LastRun { get; init; }
    public int? LastResult { get; init; }
    public DateTime? NextRun { get; init; }

    public string Name => Path[(Path.LastIndexOf('\\') + 1)..];
    public string Folder => Path.LastIndexOf('\\') <= 0 ? "\\" : Path[..Path.LastIndexOf('\\')];
    public bool IsMicrosoft => Path.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase);

    public string ActionsText => Actions.Count == 0 ? "-" : string.Join(" | ", Actions);
    public string TriggersText => Triggers.Count == 0 ? "on demand" : string.Join(", ", Triggers);
}

public static class TaskScanner
{
    public static List<TaskDetails> Scan()
    {
        var list = new List<TaskDetails>();
        foreach (var t in TaskSched.ListAll(force: true))
        {
            try { list.Add(Parse(t)); }
            catch (Exception ex) { Log.Warn($"Couldn't read task {t.Path}: {ex.Message}"); }
        }
        return list.OrderBy(t => t.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Reads author, account, actions and triggers from the task's XML. Public for the tests.</summary>
    public static TaskDetails Parse(TaskListing t)
    {
        var doc = XDocument.Parse(t.Xml);
        XNamespace ns = doc.Root?.Name.Namespace ?? XNamespace.None;
        XElement? E(XElement? parent, string name) => parent?.Element(ns + name);
        string V(XElement? parent, string name) => E(parent, name)?.Value.Trim() ?? "";

        var root = doc.Root;
        var reg = E(root, "RegistrationInfo");
        var principal = E(E(root, "Principals"), "Principal");
        var settings = E(root, "Settings");

        var actions = new List<string>();
        foreach (var a in E(root, "Actions")?.Elements() ?? Enumerable.Empty<XElement>())
        {
            if (a.Name.LocalName == "Exec") actions.Add((V(a, "Command") + " " + V(a, "Arguments")).Trim());
            else if (a.Name.LocalName == "ComHandler") actions.Add("COM " + V(a, "ClassId"));
            else actions.Add(a.Name.LocalName);
        }

        var triggers = new List<string>();
        bool idle = false;
        foreach (var tr in E(root, "Triggers")?.Elements() ?? Enumerable.Empty<XElement>())
        {
            string n = tr.Name.LocalName.Replace("Trigger", "");
            if (n == "Idle") idle = true;
            if (V(tr, "Enabled") == "false") n += " (off)";
            triggers.Add(n);
        }
        if (V(settings, "RunOnlyIfIdle") == "true") idle = true;
        if (E(settings, "MaintenanceSettings") is not null) { idle = true; triggers.Add("Automatic maintenance"); }

        string runAs = V(principal, "UserId");
        if (runAs.Length == 0) runAs = V(principal, "GroupId");
        runAs = runAs switch
        {
            "S-1-5-18" => "SYSTEM",
            "S-1-5-19" => "LOCAL SERVICE",
            "S-1-5-20" => "NETWORK SERVICE",
            "S-1-5-32-545" => "Users",
            "S-1-5-32-544" => "Administrators",
            _ => runAs,
        };

        return new TaskDetails
        {
            Path = t.Path,
            Enabled = t.Enabled,
            State = t.State,
            Author = V(reg, "Author"),
            RunAs = runAs,
            Actions = actions,
            Triggers = triggers,
            IdleTriggered = idle,
            Hidden = V(settings, "Hidden") == "true",
            LastRun = t.LastRun,
            LastResult = t.LastResult,
            NextRun = t.NextRun,
        };
    }
}
