using System.Diagnostics;
using System.Text;

namespace DpcTweaker.Core.Infra;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, bool TimedOut)
{
    public bool Ok => ExitCode == 0 && !TimedOut;
    public string Combined => (StdOut + Environment.NewLine + StdErr).Trim();
}

public static class ProcessRunner
{
    /// <summary>Runs a console program hidden and captures its output.</summary>
    public static ProcessResult Run(string exe, IEnumerable<string> args, int timeoutMs = 120_000, Encoding? outputEncoding = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = outputEncoding ?? Encoding.UTF8,
            StandardErrorEncoding = outputEncoding ?? Encoding.UTF8,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (stdout) stdout.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (stderr) stderr.AppendLine(e.Data); };

        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return new ProcessResult(-1, stdout.ToString(), stderr.ToString(), TimedOut: true);
        }
        p.WaitForExit(); // flush the async readers
        return new ProcessResult(p.ExitCode, stdout.ToString(), stderr.ToString(), TimedOut: false);
    }

    public static string System32(string exe) => Path.Combine(Environment.SystemDirectory, exe);
}
