using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Principal;
using System.Text.RegularExpressions;
using DpcTweaker.Core.Detection;
using DpcTweaker.Core.Infra;
using DpcTweaker.Core.Model;
using DpcTweaker.Core.Native;
using Microsoft.Win32;

namespace DpcTweaker.Core.Tweaks;

/// <summary>Copies the app's binaries to C:\Program Files\Stutter Guard for the tasks.</summary>
public static class Installer
{
    public static void EnsureFile(TweakContext ctx, string tweakId, string target, Func<Stream> source)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        bool existed = File.Exists(target);
        ctx.Journal.RecordOriginal(tweakId, "file", target, existed, null, null);
        try
        {
            using var src = source();
            using var dst = File.Create(target);
            src.CopyTo(dst);
        }
        catch (IOException) when (existed)
        {
            // In use (the task is running it). The installed copy stays.
            Log.Warn("Couldn't update " + target + " (in use); keeping the installed copy.");
        }
    }

    public static void EnsureTimerExe(TweakContext ctx, string tweakId) =>
        EnsureFile(ctx, tweakId, Paths.InstalledTimerExe, () =>
            Assembly.GetExecutingAssembly().GetManifestResourceStream(Paths.TimerExeName)
            ?? throw new InvalidOperationException("The timer tool isn't embedded in this build."));

    public static void EnsureAppExe(TweakContext ctx, string tweakId)
    {
        string? self = Environment.ProcessPath;
        if (self is null) throw new InvalidOperationException("Can't find the app's own .exe.");
        if (string.Equals(Path.GetFullPath(self), Path.GetFullPath(Paths.InstalledExe), StringComparison.OrdinalIgnoreCase)) return;
        EnsureFile(ctx, tweakId, Paths.InstalledExe, () => File.OpenRead(self));
    }
}

/// <summary>Task 1: device power saving off at every boot.</summary>
public sealed class DevicePowerTaskTweak : TweakBase
{
    public const string TaskName = "Device power saving off";
    public static string TaskPath => TaskSched.FullPath(TaskName);

    public DevicePowerTaskTweak(Journal journal) : base(new TweakInfo
    {
        Id = "boot.device-power",
        Module = "Boot tasks",
        Title = "Device power saving off, at every boot",
        What = "Unticks \"Allow the computer to turn off this device to save power\" on every device (WMI MSPower_DeviceEnable) and sets PnPCapabilities = 24 on physical network adapters. A SYSTEM task re-applies it 30 s after every boot, because Windows re-ticks the box whenever a device is re-plugged or its driver reinstalls.",
        Why = "Devices napping between uses add wake-up latency. Part of the desktop's 11.6 µs LatencyMon run (Oct 12 2026).",
        Cost = "A little idle power.",
        CanBreak = "Nothing known. \"Allow this device to wake the computer\" is left as it is.",
        Evidence = Evidence.Measured,
        Risk = Risk.Low,
    }, journal)
    { }

    /// <summary>The user's own task for the same job, e.g. \NoDevicePowerSave running a script.</summary>
    internal static readonly Regex Equivalent = new(@"MSPower_DeviceEnable|device.?power|power.?sav|PnPCapabilities", RegexOptions.IgnoreCase);

    protected override Probe Read()
    {
        var task = TaskSched.Query(TaskPath);
        var devices = DevicePower.Query();
        string taskText = task.Exists ? $"task {task.State}{(task.LastRun is null ? "" : ", last run " + task.LastRun.Value.ToString("g", CultureInfo.CurrentCulture))}" : "no task";
        string devText = devices is null ? "device count unknown" : $"{devices.Value.Allowed} of {devices.Value.Total} devices still allowed to power down";
        bool devicesOff = devices is null || devices.Value.Allowed == 0;
        bool ok = task.Exists && task.Enabled && devicesOff;
        if (!task.Exists && devices is not null && devicesOff &&
            TaskSched.FindOthers(Equivalent).FirstOrDefault(t => t.Enabled) is { } own)
        {
            return new Probe(true, $"your own task {own.Path}; {devText}", "task at boot; 0 devices allowed to power down",
                Note: $"Already handled by your own task {own.Path}: every device is already off. Leave this unticked, or apply it to switch to the app's task (then disable yours, two tasks doing the same job is harmless but pointless).");
        }
        return new Probe(ok, taskText + "; " + devText, "task at boot; 0 devices allowed to power down");
    }

    public override void Apply(TweakContext ctx)
    {
        Installer.EnsureAppExe(ctx, Info.Id);
        ctx.Backup.CopyFile(DevicePower.BackupCsv, "device-power-backup-before.csv");
        ctx.Journal.RecordOriginal(Info.Id, "devicepower", "devices", true, null, null);
        DevicePower.ApplyNow();
        DevicePower.Invalidate();

        bool existed = TaskSched.Query(TaskPath).Exists;
        ctx.Journal.RecordOriginal(Info.Id, "task", TaskPath, existed, null, null);
        TaskSched.Register(TaskName, TaskSched.BootTaskXml(
            "Stutter Guard: unticks 'Allow the computer to turn off this device' on every device, 30 s after boot.",
            Paths.InstalledExe, "--agent device-power"));
    }

    public override void Revert(TweakContext ctx)
    {
        base.Revert(ctx);
        DevicePower.Invalidate();
    }
}

/// <summary>Task 2: hold the timer at 0.5 ms from every logon, with our own tool.</summary>
public sealed class TimerTaskTweak : TweakBase
{
    public const string TaskName = "Timer resolution";
    public static string TaskPath => TaskSched.FullPath(TaskName);
    private readonly Func<AppOptions> _options;

    public TimerTaskTweak(Journal journal, Func<AppOptions> options, bool defaultOn) : base(new TweakInfo
    {
        Id = "boot.timer",
        Module = "Boot tasks",
        Title = "Hold the timer at 0.5 ms from every logon",
        What = "Installs DpcTimerRes.exe (the app's own 40 KB tool) and a logon task that starts it hidden, with no time limit and restart on failure. It asks Windows for the chosen resolution and tells Windows 11 not to drop that request just because it has no window.",
        Why = "Mixed evidence. The desktop runs it; the keep-or-drop test (MK1 1% lows with it on vs off) is still open. LatencyMon showed the CCD1 spikes weren't caused by it.",
        Cost = "Doubles timer interrupts on every core. On laptops it also stops the CPU resting on battery.",
        CanBreak = "Nothing known. Needs \"Global timer requests\" on to apply system-wide.",
        Evidence = Evidence.Anecdotal,
        Risk = Risk.Low,
        DefaultSelected = defaultOn,
    }, journal)
    {
        _options = options;
    }

    private string Args => _options().TimerResolution.ToString(CultureInfo.InvariantCulture);

    /// <summary>Other timer tools and the tasks that start them (SetTimerResolution, ISLC, TimerTool).</summary>
    internal static readonly Regex Equivalent = new(@"SetTimerResolution|TimerResolution|timerres|ISLC|TimerTool", RegexOptions.IgnoreCase);

    protected override Probe Read()
    {
        var task = TaskSched.Query(TaskPath);
        bool running = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Paths.TimerExeName)).Length > 0;
        var res = Kernel.TimerResolution();
        string live = res is null ? "?" : (res.Value.Current / 10000.0).ToString("0.000", CultureInfo.InvariantCulture) + " ms";
        bool argsMatch = task.Xml?.Contains("<Arguments>" + Args + "</Arguments>", StringComparison.Ordinal) ?? false;
        bool ok = task.Exists && task.Enabled && argsMatch && File.Exists(Paths.InstalledTimerExe);
        string current = task.Exists
            ? $"task {task.State}, tool {(running ? "running" : "not running")}, timer now {live}"
            : $"no task, timer now {live}";
        string planned = (_options().TimerResolution / 10000.0).ToString("0.000", CultureInfo.InvariantCulture) + " ms from every logon";

        // Someone else already holds the timer at (or below) the chosen value from their own task.
        bool heldLowEnough = res is not null && res.Value.Current <= _options().TimerResolution * 102 / 100;
        if (!task.Exists && heldLowEnough &&
            TaskSched.FindOthers(Equivalent).FirstOrDefault(t => t.Enabled) is { } own)
        {
            return new Probe(true, $"your own task {own.Path}, timer now {live}", planned,
                Note: $"Already handled by your own task {own.Path} (timer now {live}). Leave this unticked, or apply it to switch to the app's own tool (then disable yours: two timer tools are harmless but pointless).");
        }
        return new Probe(ok, current, planned);
    }

    public override void Apply(TweakContext ctx)
    {
        StopTool();
        Installer.EnsureTimerExe(ctx, Info.Id);
        bool existed = TaskSched.Query(TaskPath).Exists;
        ctx.Journal.RecordOriginal(Info.Id, "task", TaskPath, existed, null, null);
        string user = WindowsIdentity.GetCurrent().Name;
        TaskSched.Register(TaskName, TaskSched.LogonTaskXml(
            "Stutter Guard: holds the Windows timer at a fixed resolution while you're logged on.",
            user, Paths.InstalledTimerExe, Args));
        TaskSched.Run(TaskPath);
    }

    public override void Revert(TweakContext ctx)
    {
        try { TaskSched.End(TaskPath); } catch { }
        StopTool();
        base.Revert(ctx);
    }

    private static void StopTool()
    {
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Paths.TimerExeName)))
        {
            try { p.Kill(); p.WaitForExit(3000); } catch { }
        }
    }
}

public static class BootTasksModule
{
    public static IEnumerable<ITweak> Build(SystemProfile profile, Journal journal, Func<AppOptions> options)
    {
        yield return new DevicePowerTaskTweak(journal);

        yield return new RegistryValueTweak(new TweakInfo
        {
            Id = "boot.global-timer",
            Module = "Boot tasks",
            Title = "Global timer requests (GlobalTimerResolutionRequests = 1)",
            What = "Since Windows 10 2004 a timer request only applies to the process that made it. This value restores the global behaviour, so the timer tool's request applies system-wide.",
            Why = "Needed for the 0.5 ms timer task to do anything outside its own process.",
            Cost = "None by itself.",
            CanBreak = "Nothing known.",
            Evidence = Evidence.Documented,
            Risk = Risk.Low,
            NeedsReboot = true,
            DefaultSelected = !profile.IsLaptop,
        }, journal, @"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\kernel", "GlobalTimerResolutionRequests",
            RegistryValueKind.DWord, 1,
            () => profile.Windows.SupportsGlobalTimer ? null : "Only Windows 11 and Server 2022+ have this switch; Windows 10 can't make timer requests global.");

        yield return new TimerTaskTweak(journal, options, defaultOn: !profile.IsLaptop);
    }
}
