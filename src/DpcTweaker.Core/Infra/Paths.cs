namespace DpcTweaker.Core.Infra;

/// <summary>Where the app keeps its data. Settable so tests can redirect it.</summary>
public static class Paths
{
    public const string AppName = "Stutter Guard";
    public const string ExeName = "StutterGuard.exe";
    public const string TimerExeName = "DpcTimerRes.exe";

    public static string LegacyDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DPC Latency Tweaker");

    public static string DataDir { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), AppName);

    public static string InstallDir { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppName);

    public static string JournalFile => Path.Combine(DataDir, "journal.json");
    public static string BackupsDir => Path.Combine(DataDir, "Backups");
    public static string LogsDir => Path.Combine(DataDir, "logs");
    public static string DevicePowerDir => Path.Combine(DataDir, "device-power");
    public static string UndoAllScript => Path.Combine(DataDir, "undo-all.ps1");
    public static string InstalledExe => Path.Combine(InstallDir, ExeName);
    public static string InstalledTimerExe => Path.Combine(InstallDir, TimerExeName);

    public static void EnsureDataDirs()
    {
        if (!Directory.Exists(DataDir) && Directory.Exists(LegacyDataDir))
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                foreach (var dir in Directory.GetDirectories(LegacyDataDir, "*", SearchOption.AllDirectories))
                    Directory.CreateDirectory(dir.Replace(LegacyDataDir, DataDir));
                foreach (var file in Directory.GetFiles(LegacyDataDir, "*.*", SearchOption.AllDirectories))
                    File.Copy(file, file.Replace(LegacyDataDir, DataDir), true);
            }
            catch { }
        }

        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(BackupsDir);
        Directory.CreateDirectory(LogsDir);
    }
}
