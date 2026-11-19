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
/// Uploads an optional report, when - and only when - a destination has been configured on this machine.
/// <para>
/// There is deliberately no URL in this file. Two were, and both were deleted by Discord: a webhook URL is
/// a credential, and one committed to a public repository is a leaked credential. A scanner found them in
/// this project's GitHub tree on 2026-11-15, and that is the documented reason they stopped working. They
/// also ship inside every binary built from that source, so a local copy cannot be un-leaked either.
/// </para>
/// <para>
/// So the destination is per machine and stored in the local options, never in the repository. What the app
/// still guarantees is the part that matters to a user: the payload is built once, previewed in full, and
/// the object approved is the object posted.
/// </para>
/// </summary>
public static class TelemetryClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>
    /// Whether a string can be used as a destination: an absolute https URL with a host, and nothing else
    /// asked of it. https is not negotiable - the payload can contain a machine name, a task author and a
    /// log file, and none of that goes over plaintext.
    /// </summary>
    public static bool IsUsableDestination(string? url) =>
        Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var u)
        && u.Scheme == Uri.UriSchemeHttps
        && !string.IsNullOrEmpty(u.Host);

    /// <summary>The trimmed destination, or null when the string is not usable (see <see cref="IsUsableDestination"/>).</summary>
    public static string? NormaliseDestination(string? url) => IsUsableDestination(url) ? url!.Trim() : null;

    /// <summary>
    /// The destination as it may be shown: scheme, host and the path down to the secret, with the secret
    /// itself replaced. The preview is what a user copies into a chat or an issue when asking for help, so
    /// printing the whole address there would publish their own credential - which is the mistake this
    /// class is being fixed for.
    /// <para>
    /// A Discord webhook keeps its numeric id visible, because the id is not the secret and it is what
    /// identifies the channel. Every other shape keeps every path segment except the last.
    /// </para>
    /// </summary>
    public static string RedactDestination(string url)
    {
        const string mask = "/\u2026";
        int i = url.IndexOf("/api/webhooks/", StringComparison.Ordinal);
        if (i >= 0)
        {
            string prefix = url[..(i + "/api/webhooks/".Length)];
            string rest = url[(i + "/api/webhooks/".Length)..];
            int slash = rest.IndexOf('/');
            return slash < 0 ? prefix + rest : prefix + rest[..slash] + mask;
        }

        // Any other address: keep scheme + host + every segment but the last. A single-segment path is
        // shown as it is, because masking that would hide where the report goes without hiding a secret.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return url;
        var segments = u.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length <= 1) return u.GetLeftPart(UriPartial.Path);
        return u.GetLeftPart(UriPartial.Authority) + "/" + string.Join("/", segments[..^1]) + mask;
    }
    private static string GpuText(TweakEngine engine) =>
        engine.Profile.Gpus.Count > 0 ? string.Join(", ", engine.Profile.Gpus.Select(g => g.Label)) : "None detected";

    /// <summary>The hardware report payload: the summary line plus the scan report as a file.</summary>
    /// <param name="destination">Where it will be sent. Carried into the payload so the preview shows the
    /// real address rather than a placeholder, and so what is reviewed is what is sent.</param>
    public static TelemetryPayload BuildDataReportPayload(TweakEngine engine, string destination)
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

        return new TelemetryPayload(RedactDestination(destination), summary.ToString().TrimEnd(), reportText,
            "scan-report.txt", null, null);
    }

    /// <summary>The feedback payload: the user's message plus the same hardware report, and today's log if present.</summary>
    public static TelemetryPayload BuildFeedbackPayload(TweakEngine engine, string userMessage, string destination, string? userContact = null)
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

        return new TelemetryPayload(RedactDestination(destination), summary.ToString().TrimEnd(), reportText,
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
            // A 404 here is the specific failure this app has already lived through: the service deletes a
            // webhook that has been posted publicly, permanently, and nothing the user does can revive it.
            // Reporting "status NotFound" would send them looking for a problem on their own machine.
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                return (false, "That destination no longer exists (HTTP 404). A webhook is deleted permanently once it has " +
                               "been posted publicly, and two this app used to ship were deleted that way. Set a new address and " +
                               "try again - nothing was sent.");
            return (false, $"Submission returned status {resp.StatusCode}.");
        }
        catch (OperationCanceledException) { return (false, "Submission was cancelled."); }
        catch (Exception ex) { return (false, "Could not send: " + ex.Message); }
    }

    /// <summary>Sends a payload that was already shown to the user. Sending anything else would be a lie.</summary>
    public static Task<(bool ok, string message)> SendDataReportAsync(TelemetryPayload p, string destination, CancellationToken ct = default) =>
        PostAsync(p, withLog: false, destination, ct);

    public static Task<(bool ok, string message)> SendFeedbackAsync(TelemetryPayload p, string destination, CancellationToken ct = default) =>
        PostAsync(p, withLog: true, destination, ct);
}