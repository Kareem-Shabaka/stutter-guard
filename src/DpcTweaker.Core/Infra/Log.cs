using System.Globalization;

namespace DpcTweaker.Core.Infra;

/// <summary>Plain-text log of every action, one file per day.</summary>
public static class Log
{
    private static readonly object Gate = new();

    public static event Action<string>? LineWritten;

    public static void Info(string message) => Write("INFO ", message);
    public static void Warn(string message) => Write("WARN ", message);
    public static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message)
    {
        string line = string.Create(CultureInfo.InvariantCulture, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {message}");
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Paths.LogsDir);
                File.AppendAllText(Path.Combine(Paths.LogsDir, $"{DateTime.Now:yyyyMMdd}.log"), line + Environment.NewLine);
            }
            catch
            {
                // Logging must never break a tweak.
            }
        }
        LineWritten?.Invoke(line);
    }
}
