using System.Text;

namespace DpcTweaker.Core.Infra;

/// <summary>
/// Plain backup files for one session: C:\ProgramData\DPC Latency Tweaker\Backups\yyyyMMdd-HHmmss\
/// .reg exports of every key touched, .pow exports of every plan touched, a README
/// and an undo.ps1 that restores the set without the app (works in Safe Mode).
/// </summary>
public sealed class BackupSet
{
    private readonly HashSet<string> _exportedKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Guid> _exportedPlans = new();
    private readonly object _gate = new();

    private BackupSet(string folder)
    {
        Folder = folder;
    }

    public string Folder { get; }

    public static BackupSet Create()
    {
        Paths.EnsureDataDirs();
        string folder = Path.Combine(Paths.BackupsDir, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(folder);
        var set = new BackupSet(folder);
        set.WriteReadme();
        Log.Info("Backup set created: " + folder);
        return set;
    }

    /// <summary>reg export of a whole key, once per session. Missing keys are skipped.</summary>
    public void ExportKeyOnce(string keyPath)
    {
        lock (_gate)
        {
            if (!_exportedKeys.Add(keyPath)) return;
        }
        if (!RegistryOps.KeyExists(keyPath)) return;
        string file = Path.Combine(Folder, SafeName(keyPath) + ".reg");
        var r = ProcessRunner.Run(ProcessRunner.System32("reg.exe"),
            new[] { "export", RegistryOps.LongForm(keyPath), file, "/y" }, 60_000, Encoding.Default);
        if (!r.Ok) Log.Warn("reg export failed for " + keyPath + ": " + r.Combined);
    }

    /// <summary>powercfg /export of a plan, once per session.</summary>
    public void ExportPlanOnce(Guid scheme)
    {
        lock (_gate)
        {
            if (!_exportedPlans.Add(scheme)) return;
        }
        string file = Path.Combine(Folder, "power-plan-" + scheme + ".pow");
        var r = ProcessRunner.Run(ProcessRunner.System32("powercfg.exe"),
            new[] { "/export", file, scheme.ToString() }, 60_000, Encoding.Default);
        if (!r.Ok) Log.Warn("powercfg /export failed for " + scheme + ": " + r.Combined);
    }

    public void CopyFile(string source, string name)
    {
        try
        {
            if (File.Exists(source)) File.Copy(source, Path.Combine(Folder, name), overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn("Backup copy failed: " + ex.Message);
        }
    }

    private void WriteReadme()
    {
        File.WriteAllText(Path.Combine(Folder, "README.txt"), """
            Stutter Guard - backup set
            ==========================

            What is in this folder:
              *.reg        Registry keys exactly as they were before the app changed them.
                           Double-click one to put that key back (then restart).
              *.pow        Power plans as they were. Restore one with:
                             powercfg /import "<file>.pow"
              undo.ps1     Undoes every change the app has recorded so far, without the app.
                           Right-click > Run with PowerShell, as administrator, then restart.
                           It also works in Safe Mode.

            The app's own revert buttons do the same thing per tweak, and the journal at
            C:\ProgramData\Stutter Guard\journal.json holds every value it changed.

            Before tuning, a full disk image made with the backup tool of your choice is
            still the best safety net: these files only cover what this app changed.
            """);
    }

    private static string SafeName(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s) sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_');
        string name = sb.ToString();
        return name.Length > 150 ? name[..150] : name;
    }
}
