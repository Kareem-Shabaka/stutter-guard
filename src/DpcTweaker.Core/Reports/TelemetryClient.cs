using System.Text;
using DpcTweaker.Core.Infra;

namespace DpcTweaker.Core.Reports;

/// <summary>
/// Exactly what a submission would contain. Built once, then both previewed and sent, so what the user
/// reads in the preview cannot drift from what leaves the machine.
/// </summary>
public sealed record TelemetryPayload(
    string Destination,
    string Summary,
    string ReportText,
    string ReportFileName,
    string? LogFileName,
    byte[]? LogBytes)
{
    public string ReportSizeText => $"{Encoding.UTF8.GetByteCount(ReportText):N0} bytes";
    public string LogSizeText => LogBytes is null ? "" : $"{LogBytes.Length:N0} bytes";
}

/// <summary>
/// Submits optional anonymous hardware reports and user bug reports to the developer's Discord channels.
/// <para>
/// The webhook URLs are written out in full rather than obfuscated. A base64 string in a binary looks like
/// something hidden, and this app's whole case rests on being inspectable: anyone can read the destination
/// below and see where a report goes. It is a webhook, not an invite, and it only receives what
/// <see cref="TelemetryPayload"/> shows.
/// </para>
/// </summary>
public static class TelemetryClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public const string DataWebhookUrl =
        "https://discord.com/api/webhooks/1569861643381510144/1E-Sk9P1YiMJNZEb--qj-zr5cu8C2TTCCenLGFqyyT9dQZJrABlHwimNa0bMLk0ovy9R";

    public const string BugWebhookUrl =
        "https://discord.com/api/webhooks/1569861762348486788/JuPyo-VI91GICa6RrOK5LkXZd7iG0INCVH48H4JiDTgBMKcFYR6528v4oL-ob-FPeUdR";

    /// <summary>Host and path, without the token part of the URL, for display next to a preview.</summary>
    private static string DisplayDestination(string url)
    {
        int i = url.IndexOf("/api/webhooks/", StringComparison.Ordinal);
        if (i < 0) return url;
        string prefix = url[..(i + "/api/webhooks/".Length)];
        string rest = url[(i + "/api/webhooks/".Length)..];
        int slash = rest.IndexOf('/');
        return slash < 0 ? prefix + rest : prefix + rest[..slash] + "/…";
    }

    private static string GpuText(TweakEngine engine) =>
        engine.Profile.Gpus.Count > 0 ? string.Join(", ", engine.Profile.Gpus.Select(g => g.Label)) : "None detected";

    /// <summary>The hardware report payload: the Discord summary line plus the scan report as a file.</summary>
    public static TelemetryPayload BuildDataReportPayload(TweakEngine engine)
    {
        var p = engine.Profile;
        string reportText = ScanReport.Build(engine);
        string benchText = engine.Bench is not null && engine.Bench.Cores.Count > 0
            ? $"Completed ({engine.Bench.Cores.Count} cores, best wake-up median: {engine.Bench.Cores.Min(c => c.WakeMedianUs):0.0} us)"
            : "Not run";

        var summary = new StringBuilder();
        summary.AppendLine("**New Anonymous Hardware Scan Report**");
        summary.AppendLine($"- **CPU:** {p.CpuName} ({p.CpuKind})");
        summary.AppendLine($"- **GPU(s):** {GpuText(engine)}");
        summary.AppendLine($"- **OS:** {p.Windows.Display} ({p.FormFactor})");
        if (!string.IsNullOrWhiteSpace(p.Manufacturer + p.Model))
            summary.AppendLine($"- **System:** {p.Manufacturer} {p.Model}".Trim());
        summary.AppendLine($"- **Timer:** {(p.TimerResolution is { } tr ? $"{tr.Current / 10000.0:0.000} ms" : "Unknown")} | **WHEA:** {p.WheaErrors14Days ?? 0} in 14d");
        summary.AppendLine($"- **Core Benchmark:** {benchText}");

        return new TelemetryPayload(DisplayDestination(DataWebhookUrl), summary.ToString().TrimEnd(), reportText,
            "scan-report.txt", null, null);
    }

    /// <summary>The feedback payload: the user's message plus the same hardware report, and today's log if present.</summary>
    public static TelemetryPayload BuildFeedbackPayload(TweakEngine engine, string userMessage, string? userContact = null)
    {
        var p = engine.Profile;
        string reportText = ScanReport.Build(engine);

        var summary = new StringBuilder();
        summary.AppendLine("**New User Feedback / Report**");
        summary.AppendLine("**Message:**");
        summary.AppendLine($"> {userMessage.Replace("\n", "\n> ")}");
        if (!string.IsNullOrWhiteSpace(userContact))
            summary.AppendLine($"- **Contact:** {userContact.Trim()}");
        summary.AppendLine($"- **Hardware:** {p.CpuName} | {GpuText(engine)}");
        summary.AppendLine($"- **OS:** {p.Windows.Display} (v{BuildInfo.Version})");

        // The log is attached, so it must be visible in the preview: it is the part a user is least likely
        // to expect, and the one most likely to contain machine names or paths.
        string? logName = null;
        byte[]? logBytes = null;
        try
        {
            string todayLog = Path.Combine(Paths.LogsDir, $"{DateTime.Now:yyyyMMdd}.log");
            if (File.Exists(todayLog)) { logName = "stutterguard.log"; logBytes = File.ReadAllBytes(todayLog); }
        }
        catch { }

        return new TelemetryPayload(DisplayDestination(BugWebhookUrl), summary.ToString().TrimEnd(), reportText,
            "scan-report.txt", logName, logBytes);
    }

    /// <summary>
    /// The full text the user reviews before sending: destination, every field, every attachment, and the
    /// report file itself. Nothing is summarised away.
    /// </summary>
    public static string Preview(TelemetryPayload p)
    {
        var sb = new StringBuilder();
        sb.AppendLine("SENT TO");
        sb.AppendLine("  " + p.Destination);
        sb.AppendLine();
        sb.AppendLine("MESSAGE TEXT");
        foreach (string line in p.Summary.Split('\n'))
            sb.AppendLine("  " + line.TrimEnd());
        sb.AppendLine();
        sb.AppendLine("ATTACHED FILES");
        sb.AppendLine($"  {p.ReportFileName} — {p.ReportSizeText} (the hardware scan report, shown in full below)");
        if (p.LogFileName is not null)
            sb.AppendLine($"  {p.LogFileName} — {p.LogSizeText} (today's application log)");
        else
            sb.AppendLine("  (no log file attached: none written today)");
        sb.AppendLine();
        sb.AppendLine("NOT SENT");
        sb.AppendLine("  Your name or Windows user name, passwords, files, IP address, MAC address or Discord tag");
        sb.AppendLine("  (unless you typed one above).");
        sb.AppendLine();
        sb.AppendLine("──────── scan-report.txt, in full ────────");
        sb.AppendLine(p.ReportText);
        return sb.ToString();
    }

    private static async Task<(bool ok, string message)> PostAsync(TelemetryPayload p, bool withLog, string url, CancellationToken ct)
    {
        try
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(p.Summary), "content");
            form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(p.ReportText)), withLog ? "file1" : "file", p.ReportFileName);
            if (withLog && p.LogFileName is not null && p.LogBytes is not null)
                form.Add(new ByteArrayContent(p.LogBytes), "file2", p.LogFileName);

            using var resp = await Http.PostAsync(url, form, ct);
            if (resp.IsSuccessStatusCode) return (true, "Sent. Thank you for helping improve Stutter Guard.");
            return (false, $"Submission returned status {resp.StatusCode}.");
        }
        catch (OperationCanceledException) { return (false, "Submission was cancelled."); }
        catch (Exception ex) { return (false, "Could not send: " + ex.Message); }
    }

    /// <summary>Sends a payload that was already shown to the user. Sending anything else would be a lie.</summary>
    public static Task<(bool ok, string message)> SendDataReportAsync(TelemetryPayload p, CancellationToken ct = default) =>
        PostAsync(p, withLog: false, DataWebhookUrl, ct);

    public static Task<(bool ok, string message)> SendFeedbackAsync(TelemetryPayload p, CancellationToken ct = default) =>
        PostAsync(p, withLog: true, BugWebhookUrl, ct);
}