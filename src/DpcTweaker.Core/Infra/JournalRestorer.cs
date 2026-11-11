using System.Globalization;
using System.Text;
using DpcTweaker.Core.Native;
using DpcTweaker.Core.Tweaks;
using Microsoft.Win32;

namespace DpcTweaker.Core.Infra;

/// <summary>Puts recorded before-states back, per tweak or all at once.</summary>
public static class JournalRestorer
{
    public static void RestoreTweak(Journal journal, string tweakId)
    {
        var entries = journal.OriginalsFor(tweakId).Reverse().ToList();
        var errors = new List<string>();
        foreach (var e in entries)
        {
            try { Restore(e); }
            catch (Exception ex) { errors.Add(ex.Message); Log.Error($"Revert {tweakId} {e.Kind} {e.Target}: {ex.Message}"); }
        }
        if (errors.Count > 0) throw new InvalidOperationException(string.Join(" ", errors));
        journal.ClearTweak(tweakId);
        journal.Save();
        Log.Info("Reverted " + tweakId);
    }

    /// <summary>Restores the oldest recorded state of every target, newest change first.</summary>
    public static (int Restored, List<string> Errors) RestoreAll(Journal journal)
    {
        var errors = new List<string>();
        int ok = 0;
        foreach (var e in journal.OldestPerTarget())
        {
            try { Restore(e); ok++; }
            catch (Exception ex) { errors.Add($"{e.Kind} {e.Target}: {ex.Message}"); }
        }
        if (errors.Count == 0)
        {
            journal.ClearAll();
            journal.Save();
        }
        Log.Info($"Revert all: {ok} restored, {errors.Count} failed");
        return (ok, errors);
    }

    public static void Restore(JournalEntry e)
    {
        switch (e.Kind)
        {
            case "reg":
                RestoreReg(e);
                break;
            case "power":
                RestorePower(e);
                break;
            case "power-active":
                if (Guid.TryParse(e.Data, out var active) && PowerApi.Exists(active)) PowerApi.SetActive(active);
                break;
            case "plan-created":
                if (Guid.TryParse(e.Target, out var plan) && PowerApi.Exists(plan))
                {
                    if (PowerApi.GetActive() == plan) PowerApi.SetActive(PowerApi.Balanced);
                    PowerApi.Delete(plan);
                }
                break;
            case "task":
                if (TaskSched.Query(e.Target).Exists) TaskSched.Delete(e.Target);
                break;
            case "file":
                if (!e.Existed && File.Exists(e.Target)) File.Delete(e.Target);
                break;
            case "devicepower":
                DevicePower.RestoreFromBackup();
                break;
            case "svc":
            {
                var t = ServiceControl.FromJournal(e.Data);
                if (t is DpcTweaker.Core.Detection.StartType.Auto or DpcTweaker.Core.Detection.StartType.AutoDelayed
                    or DpcTweaker.Core.Detection.StartType.Manual or DpcTweaker.Core.Detection.StartType.Disabled
                    && RegistryOps.KeyExists(@"HKLM\SYSTEM\CurrentControlSet\Services\" + e.Target))
                    ServiceControl.SetStart(e.Target, t);
                break;
            }
            case "task-enabled":
                if (e.Data == "1" && TaskSched.Query(e.Target).Exists) TaskSched.SetEnabled(e.Target, true);
                break;
            default:
                Log.Warn("Unknown journal entry kind: " + e.Kind);
                break;
        }
    }

    private static (string Key, string Name) SplitRegTarget(string target)
    {
        int bar = target.LastIndexOf('|');
        return (target[..bar], target[(bar + 1)..]);
    }

    private static void RestoreReg(JournalEntry e)
    {
        var (key, name) = SplitRegTarget(e.Target);
        if (e.Existed && e.ValueKind is not null && e.Data is not null)
        {
            var kind = Enum.Parse<RegistryValueKind>(e.ValueKind);
            RegistryOps.Write(key, name, kind, RegValue.Deserialize(kind, e.Data));
        }
        else
        {
            RegistryOps.DeleteValue(key, name);
        }
    }

    public static string PowerTarget(Guid scheme, Guid sub, Guid setting, bool dc) =>
        $"{scheme}|{sub}|{setting}|{(dc ? "DC" : "AC")}";

    private static void RestorePower(JournalEntry e)
    {
        var parts = e.Target.Split('|');
        var scheme = Guid.Parse(parts[0]);
        var sub = Guid.Parse(parts[1]);
        var setting = Guid.Parse(parts[2]);
        bool dc = parts[3] == "DC";
        if (!PowerApi.Exists(scheme) || e.Data is null) return;
        uint value = uint.Parse(e.Data, CultureInfo.InvariantCulture);
        if (dc) PowerApi.WriteDc(scheme, sub, setting, value);
        else PowerApi.WriteAc(scheme, sub, setting, value);
        if (PowerApi.GetActive() == scheme) PowerApi.SetActive(scheme);
    }

    // ------------------------------------------------------------------ undo.ps1

    /// <summary>A PowerShell script that undoes everything in the journal, without the app.</summary>
    public static string BuildUndoScript(Journal journal)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Stutter Guard - undo script, generated " + DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
        sb.AppendLine("# Undoes every change the app recorded. Run as administrator (also works in Safe Mode), then restart.");
        sb.AppendLine("$ErrorActionPreference = 'Continue'");
        sb.AppendLine("$p = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())");
        sb.AppendLine("if (-not $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { Start-Process powershell.exe -Verb RunAs -ArgumentList \"-NoProfile -ExecutionPolicy Bypass -File `\"$PSCommandPath`\"\"; exit }");
        sb.AppendLine("function RegSet($path, $name, $type, $value) { if (-not (Test-Path -LiteralPath $path)) { New-Item -Path $path -Force | Out-Null }; New-ItemProperty -LiteralPath $path -Name $name -PropertyType $type -Value $value -Force | Out-Null; Write-Host \"restored $name\" }");
        sb.AppendLine("function RegDel($path, $name) { Remove-ItemProperty -LiteralPath $path -Name $name -ErrorAction SilentlyContinue; Write-Host \"removed $name\" }");
        sb.AppendLine();

        foreach (var e in journal.OldestPerTarget())
        {
            sb.AppendLine("# " + e.TweakId);
            switch (e.Kind)
            {
                case "reg":
                {
                    var (key, name) = SplitRegTarget(e.Target);
                    string path = PowerShellBridge.Quote("Registry::" + RegistryOps.LongForm(key));
                    if (e.Existed && e.ValueKind is not null && e.Data is not null)
                        sb.AppendLine($"RegSet {path} {PowerShellBridge.Quote(name)} '{PsType(e.ValueKind)}' {PsValue(e.ValueKind, e.Data)}");
                    else
                        sb.AppendLine($"RegDel {path} {PowerShellBridge.Quote(name)}");
                    break;
                }
                case "power":
                {
                    var parts = e.Target.Split('|');
                    string verb = parts[3] == "DC" ? "/setdcvalueindex" : "/setacvalueindex";
                    sb.AppendLine($"powercfg {verb} {parts[0]} {parts[1]} {parts[2]} {e.Data}");
                    sb.AppendLine($"if ((powercfg /getactivescheme) -match '{parts[0]}') {{ powercfg /setactive {parts[0]} }}");
                    break;
                }
                case "power-active":
                    sb.AppendLine($"powercfg /setactive {e.Data}");
                    break;
                case "plan-created":
                    sb.AppendLine($"if ((powercfg /getactivescheme) -match '{e.Target}') {{ powercfg /setactive {PowerApi.Balanced} }}");
                    sb.AppendLine($"powercfg /delete {e.Target}");
                    break;
                case "task":
                    sb.AppendLine($"schtasks /Delete /TN {PowerShellBridge.Quote(e.Target)} /F");
                    break;
                case "file":
                    if (!e.Existed) sb.AppendLine($"Remove-Item -LiteralPath {PowerShellBridge.Quote(e.Target)} -Force -ErrorAction SilentlyContinue");
                    break;
                case "devicepower":
                    sb.AppendLine(DevicePower.RestoreScript);
                    break;
                case "svc":
                {
                    var t = ServiceControl.FromJournal(e.Data);
                    if (t is DpcTweaker.Core.Detection.StartType.Auto or DpcTweaker.Core.Detection.StartType.AutoDelayed
                        or DpcTweaker.Core.Detection.StartType.Manual or DpcTweaker.Core.Detection.StartType.Disabled)
                        sb.AppendLine($"sc.exe config {PowerShellBridge.Quote(e.Target)} start= {DpcTweaker.Core.Detection.StartTypes.ScWord(t)}");
                    break;
                }
                case "task-enabled":
                    if (e.Data == "1") sb.AppendLine($"schtasks /Change /TN {PowerShellBridge.Quote(e.Target)} /ENABLE");
                    break;
            }
        }
        sb.AppendLine();
        sb.AppendLine("Write-Host 'Done. Restart Windows to finish.'");
        sb.AppendLine("Read-Host 'Press Enter to close'");
        return sb.ToString();
    }

    public static void WriteUndoScripts(Journal journal, BackupSet? backup)
    {
        try
        {
            string script = BuildUndoScript(journal);
            File.WriteAllText(Paths.UndoAllScript, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            if (backup is not null)
                File.WriteAllText(Path.Combine(backup.Folder, "undo.ps1"), script, new UTF8Encoding(true));
        }
        catch (Exception ex)
        {
            Log.Warn("Couldn't write undo.ps1: " + ex.Message);
        }
    }

    private static string PsType(string valueKind) => valueKind switch
    {
        nameof(RegistryValueKind.DWord) => "DWord",
        nameof(RegistryValueKind.QWord) => "QWord",
        nameof(RegistryValueKind.Binary) => "Binary",
        nameof(RegistryValueKind.MultiString) => "MultiString",
        nameof(RegistryValueKind.ExpandString) => "ExpandString",
        _ => "String",
    };

    private static string PsValue(string valueKind, string data) => valueKind switch
    {
        nameof(RegistryValueKind.DWord) => unchecked((int)uint.Parse(data, CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture),
        nameof(RegistryValueKind.QWord) => unchecked((long)ulong.Parse(data, CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture),
        nameof(RegistryValueKind.Binary) => data.Length == 0
            ? "([byte[]]@())"
            : "([byte[]](" + string.Join(",", Convert.FromHexString(data).Select(b => "0x" + b.ToString("X2"))) + "))",
        nameof(RegistryValueKind.MultiString) => "@(" + string.Join(",", data.Split('\n').Select(PowerShellBridge.Quote)) + ")",
        _ => PowerShellBridge.Quote(data),
    };
}
