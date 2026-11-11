using System.Security;
using System.Text;

namespace DpcTweaker.Core.Infra;

public sealed record ScheduledTaskInfo(bool Exists, bool Enabled, string State, DateTime? LastRun, int? LastResult, string? Xml);

/// <summary>One task from the whole Task Scheduler library (for spotting the user's own equivalents).</summary>
public sealed record TaskListing(string Path, bool Enabled, string Xml)
{
    /// <summary>Ready / Running / Disabled / Queued.</summary>
    public string State { get; init; } = "";
    public DateTime? LastRun { get; init; }
    public int? LastResult { get; init; }
    public DateTime? NextRun { get; init; }
}

/// <summary>
/// The app's own tasks live in the Task Scheduler folder \Stutter Guard\.
/// Created with schtasks.exe /XML (documented, no dependencies); read through the
/// Task Scheduler COM API ("Schedule.Service"), with a schtasks fallback.
/// </summary>
public static class TaskSched
{
    public const string Folder = "\\Stutter Guard";
    public const string LegacyFolder = "\\DPC Latency Tweaker";

    public static string FullPath(string name) => Folder + "\\" + name;

    public static void Register(string name, string xml)
    {
        string tmp = Path.Combine(Path.GetTempPath(), "dpctweaker-task-" + Guid.NewGuid().ToString("N") + ".xml");
        // schtasks wants UTF-16 with a BOM for XML declared as UTF-16.
        File.WriteAllText(tmp, xml, Encoding.Unicode);
        try
        {
            var r = ProcessRunner.Run(ProcessRunner.System32("schtasks.exe"),
                new[] { "/Create", "/TN", FullPath(name), "/XML", tmp, "/F" }, 60_000, ProcessRunner.ConsoleEncoding);
            if (!r.Ok) throw new InvalidOperationException("schtasks /Create failed: " + r.Combined);
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
        Log.Info("Task registered: " + FullPath(name));
    }

    public static void Delete(string fullPath)
    {
        var r = ProcessRunner.Run(ProcessRunner.System32("schtasks.exe"),
            new[] { "/Delete", "/TN", fullPath, "/F" }, 60_000, ProcessRunner.ConsoleEncoding);
        if (!r.Ok && Query(fullPath).Exists) throw new InvalidOperationException("schtasks /Delete failed: " + r.Combined);
        Log.Info("Task deleted: " + fullPath);
    }

    public static void Run(string fullPath) =>
        ProcessRunner.Run(ProcessRunner.System32("schtasks.exe"), new[] { "/Run", "/TN", fullPath }, 30_000, ProcessRunner.ConsoleEncoding);

    public static void End(string fullPath) =>
        ProcessRunner.Run(ProcessRunner.System32("schtasks.exe"), new[] { "/End", "/TN", fullPath }, 30_000, ProcessRunner.ConsoleEncoding);

    public static ScheduledTaskInfo Query(string fullPath)
    {
        try
        {
            return QueryCom(fullPath);
        }
        catch
        {
            var r = ProcessRunner.Run(ProcessRunner.System32("schtasks.exe"),
                new[] { "/Query", "/TN", fullPath, "/XML" }, 30_000, ProcessRunner.ConsoleEncoding);
            if (!r.Ok) return new ScheduledTaskInfo(false, false, "missing", null, null, null);
            bool enabled = !r.StdOut.Contains("<Enabled>false</Enabled>", StringComparison.OrdinalIgnoreCase);
            return new ScheduledTaskInfo(true, enabled, "present", null, null, r.StdOut);
        }
    }

    private static ScheduledTaskInfo QueryCom(string fullPath)
    {
        Type t = Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException("No Task Scheduler COM.");
        dynamic svc = Activator.CreateInstance(t)!;
        svc.Connect();
        int cut = fullPath.LastIndexOf('\\');
        string folderPath = cut <= 0 ? "\\" : fullPath[..cut];
        string name = fullPath[(cut + 1)..];
        dynamic folder;
        try { folder = svc.GetFolder(folderPath); }
        catch (Exception ex) { return FromHResult(ex, "folder"); }
        dynamic task;
        try { task = folder.GetTask(name); }
        catch (Exception ex) { return FromHResult(ex, "task"); }

        int state = (int)task.State;
        bool enabled = (bool)task.Enabled;
        DateTime last = (DateTime)task.LastRunTime;
        int result = (int)task.LastTaskResult;
        string xml = (string)task.Xml;
        string stateText = state switch { 1 => "Disabled", 2 => "Queued", 3 => "Ready", 4 => "Running", _ => "Unknown" };
        DateTime? lastRun = last.Year < 2000 ? null : last;
        return new ScheduledTaskInfo(true, enabled, stateText, lastRun, lastRun is null ? null : result, xml);
    }

    /// <summary>
    /// How a failed Task Scheduler lookup should be reported. "Missing" and "access denied" must not
    /// collapse into one answer: the revert paths act on Exists, and deleting a task we simply couldn't
    /// read would hit a real system task.
    /// </summary>
    internal enum TaskLookup { Found, Missing, AccessDenied, Unknown }

    private const int E_ACCESSDENIED = unchecked((int)0x80070005);
    private const int HRESULT_FILE_NOT_FOUND = unchecked((int)0x80070002);
    private const int HRESULT_PATH_NOT_FOUND = unchecked((int)0x80070003);
    private const int HRESULT_RPC_E_DISCONNECTED = unchecked((int)0x80010108);

    internal static TaskLookup ClassifyLookupFailure(int hr) => hr switch
    {
        E_ACCESSDENIED => TaskLookup.AccessDenied,
        HRESULT_FILE_NOT_FOUND or HRESULT_PATH_NOT_FOUND => TaskLookup.Missing,
        HRESULT_RPC_E_DISCONNECTED => TaskLookup.Unknown,
        _ => TaskLookup.Unknown,
    };

    private static ScheduledTaskInfo FromHResult(Exception ex, string what)
    {
        int hr = ex.HResult;
        var kind = ClassifyLookupFailure(hr);
        if (kind == TaskLookup.Missing)
            return new ScheduledTaskInfo(false, false, "missing", null, null, null);

        // Keep Exists = true so a revert never deletes a task that is merely unreadable.
        string state = kind == TaskLookup.AccessDenied ? "access-denied" : "unreadable";
        Log.Warn($"Task Scheduler couldn't read the {what}: {ex.Message} (0x{hr:X8}). Reported as {state}, not as missing.");
        return new ScheduledTaskInfo(true, false, state, null, null, null);
    }

    private static readonly object CacheGate = new();
    private static (DateTime When, List<TaskListing> Tasks)? _all;

    /// <summary>Why the last full listing failed, or null.</summary>
    public static string? LastListError { get; private set; }

    /// <summary>Every task in the library, all folders, hidden ones too. Cached for a minute.</summary>
    public static IReadOnlyList<TaskListing> ListAll(bool force = false)
    {
        lock (CacheGate)
        {
            if (!force && _all is { } c && DateTime.UtcNow - c.When < TimeSpan.FromMinutes(1)) return c.Tasks;
        }
        var list = new List<TaskListing>();
        try
        {
            Type t = Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException("No Task Scheduler COM.");
            dynamic svc = Activator.CreateInstance(t)!;
            svc.Connect();
            void Walk(dynamic folder, int depth)
            {
                try
                {
                    dynamic tasks = folder.GetTasks(1); // TASK_ENUM_HIDDEN
                    int n = (int)tasks.Count;
                    for (int i = 1; i <= n; i++) // COM collections are 1-based
                    {
                        try
                        {
                            dynamic task = tasks[i];
                            int state = 0; DateTime last = default, next = default; int result = 0;
                            try { state = (int)task.State; } catch { }
                            try { last = (DateTime)task.LastRunTime; result = (int)task.LastTaskResult; } catch { }
                            try { next = (DateTime)task.NextRunTime; } catch { }
                            list.Add(new TaskListing((string)task.Path, (bool)task.Enabled, (string)task.Xml)
                            {
                                State = state switch { 1 => "Disabled", 2 => "Queued", 3 => "Ready", 4 => "Running", _ => "Unknown" },
                                LastRun = last.Year < 2000 ? null : last,
                                LastResult = last.Year < 2000 ? null : result,
                                NextRun = next.Year < 2000 ? null : next,
                            });
                        }
                        catch { /* one unreadable task */ }
                    }
                }
                catch { /* folder without access */ }
                if (depth > 8) return;
                try
                {
                    dynamic subs = folder.GetFolders(0);
                    int n = (int)subs.Count;
                    for (int i = 1; i <= n; i++) Walk(subs[i], depth + 1);
                }
                catch { }
            }
            Walk(svc.GetFolder("\\"), 0);
        }
        catch (Exception ex)
        {
            Log.Warn("Couldn't list scheduled tasks: " + ex.Message);
            LastListError = ex.Message;
            lock (CacheGate) return _all?.Tasks ?? list; // keep the last good list; don't cache the failure
        }
        LastListError = null;
        lock (CacheGate) _all = (DateTime.UtcNow, list);
        return list;
    }

    /// <summary>Tasks outside the app's folder whose path or actions match, enabled ones first.</summary>
    public static IReadOnlyList<TaskListing> FindOthers(System.Text.RegularExpressions.Regex pattern) =>
        ListAll()
            .Where(t => !t.Path.StartsWith(Folder + "\\", StringComparison.OrdinalIgnoreCase))
            .Where(t => pattern.IsMatch(t.Path) || pattern.IsMatch(ActionsOf(t.Xml)))
            .OrderByDescending(t => t.Enabled)
            .ToList();

    /// <summary>The Actions element of a task's XML (so descriptions and authors don't match).</summary>
    public static string ActionsOf(string xml)
    {
        int a = xml.IndexOf("<Actions", StringComparison.OrdinalIgnoreCase);
        int b = xml.IndexOf("</Actions>", StringComparison.OrdinalIgnoreCase);
        return a >= 0 && b > a ? xml[a..b] : "";
    }

    public static void SetEnabled(string fullPath, bool enabled)
    {
        var r = ProcessRunner.Run(ProcessRunner.System32("schtasks.exe"),
            new[] { "/Change", "/TN", fullPath, enabled ? "/ENABLE" : "/DISABLE" }, 60_000, ProcessRunner.ConsoleEncoding);
        if (!r.Ok) throw new InvalidOperationException($"schtasks /Change {(enabled ? "/ENABLE" : "/DISABLE")} failed: " + r.Combined.Trim());
        InvalidateList();
        Log.Info($"Task {(enabled ? "enabled" : "disabled")}: {fullPath}");
    }

    public static void InvalidateList()
    {
        lock (CacheGate) _all = null;
    }

    public static string Esc(string s) => SecurityElement.Escape(s) ?? "";

    /// <summary>Boot trigger, SYSTEM, highest privileges.</summary>
    public static string BootTaskXml(string description, string command, string arguments, string delay = "PT30S", string timeLimit = "PT5M") => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Author>Stutter Guard</Author>
            <Description>{Esc(description)}</Description>
          </RegistrationInfo>
          <Triggers>
            <BootTrigger>
              <Enabled>true</Enabled>
              <Delay>{delay}</Delay>
            </BootTrigger>
          </Triggers>
          <Principals>
            <Principal id="Author">
              <UserId>S-1-5-18</UserId>
              <RunLevel>HighestAvailable</RunLevel>
            </Principal>
          </Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <AllowHardTerminate>true</AllowHardTerminate>
            <StartWhenAvailable>true</StartWhenAvailable>
            <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
            <IdleSettings>
              <StopOnIdleEnd>false</StopOnIdleEnd>
              <RestartOnIdle>false</RestartOnIdle>
            </IdleSettings>
            <AllowStartOnDemand>true</AllowStartOnDemand>
            <Enabled>true</Enabled>
            <Hidden>false</Hidden>
            <RunOnlyIfIdle>false</RunOnlyIfIdle>
            <WakeToRun>false</WakeToRun>
            <ExecutionTimeLimit>{timeLimit}</ExecutionTimeLimit>
            <Priority>7</Priority>
          </Settings>
          <Actions Context="Author">
            <Exec>
              <Command>{Esc(command)}</Command>
              <Arguments>{Esc(arguments)}</Arguments>
            </Exec>
          </Actions>
        </Task>
        """;

    /// <summary>Logon trigger for one user, runs as that user without elevation, no time limit.</summary>
    public static string LogonTaskXml(string description, string user, string command, string arguments) => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Author>Stutter Guard</Author>
            <Description>{Esc(description)}</Description>
          </RegistrationInfo>
          <Triggers>
            <LogonTrigger>
              <Enabled>true</Enabled>
              <UserId>{Esc(user)}</UserId>
            </LogonTrigger>
          </Triggers>
          <Principals>
            <Principal id="Author">
              <UserId>{Esc(user)}</UserId>
              <LogonType>InteractiveToken</LogonType>
              <RunLevel>LeastPrivilege</RunLevel>
            </Principal>
          </Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <AllowHardTerminate>true</AllowHardTerminate>
            <StartWhenAvailable>false</StartWhenAvailable>
            <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
            <IdleSettings>
              <StopOnIdleEnd>false</StopOnIdleEnd>
              <RestartOnIdle>false</RestartOnIdle>
            </IdleSettings>
            <AllowStartOnDemand>true</AllowStartOnDemand>
            <Enabled>true</Enabled>
            <Hidden>false</Hidden>
            <RunOnlyIfIdle>false</RunOnlyIfIdle>
            <WakeToRun>false</WakeToRun>
            <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
            <Priority>7</Priority>
            <RestartOnFailure>
              <Interval>PT1M</Interval>
              <Count>3</Count>
            </RestartOnFailure>
          </Settings>
          <Actions Context="Author">
            <Exec>
              <Command>{Esc(command)}</Command>
              <Arguments>{Esc(arguments)}</Arguments>
            </Exec>
          </Actions>
        </Task>
        """;
}
