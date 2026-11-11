using System.Text;
using DpcTweaker.Core.Infra;

namespace DpcTweaker.Core.Reports;

/// <summary>Submits optional anonymous hardware reports and user bug reports directly to the developer's Discord channels.</summary>
public static class TelemetryClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private static readonly string DataWebhookUrl =
        Encoding.UTF8.GetString(Convert.FromBase64String("aHR0cHM6Ly9kaXNjb3JkLmNvbS9hcGkvd2ViaG9va3MvMTU2OTg2MTY0MzM4MTUxMDE0NC8xRS1TazlQMVlpTUpOWkViLS1xai16cjVjdThDMlRUQ0NlbkxHRnF5eVQ5ZFFaSnJBQmxId2ltTmEwYk1MazBvdnk5Ug=="));

    private static readonly string BugWebhookUrl =
        Encoding.UTF8.GetString(Convert.FromBase64String("aHR0cHM6Ly9kaXNjb3JkLmNvbS9hcGkvd2ViaG9va3MvMTU2OTg2MTc2MjM0ODQ4Njc4OC9KdVB5by1WSTkxR0lDYTZSck9LNUxrWFpkN2lHMElOQ1ZINDhINEppRFRnQk1LY0ZZUjY1Mjh2NG9MLW9iLUZQZVVkUg=="));

    /// <summary>Submits an anonymous hardware scan report to help expand Stutter Guard's driver and hardware support.</summary>
    public static async Task<(bool ok, string message)> SubmitDataReportAsync(TweakEngine engine, CancellationToken ct = default)
    {
        try
        {
            var p = engine.Profile;
            string reportText = ScanReport.Build(engine);
            string gpus = p.Gpus.Count > 0 ? string.Join(", ", p.Gpus.Select(g => g.Label)) : "None detected";
            string benchText = engine.Bench is not null && engine.Bench.Cores.Count > 0
                ? $"Completed ({engine.Bench.Cores.Count} cores, best wake-up median: {engine.Bench.Cores.Min(c => c.WakeMedianUs):0.0} µs)"
                : "Not run";

            var summary = new StringBuilder();
            summary.AppendLine("📊 **New Anonymous Hardware Scan Report**");
            summary.AppendLine($"• **CPU:** {p.CpuName} ({p.CpuKind})");
            summary.AppendLine($"• **GPU(s):** {gpus}");
            summary.AppendLine($"• **OS:** {p.Windows.Display} ({p.FormFactor})");
            if (!string.IsNullOrWhiteSpace(p.Manufacturer + p.Model))
                summary.AppendLine($"• **System:** {p.Manufacturer} {p.Model}".Trim());
            summary.AppendLine($"• **Timer:** {(p.TimerResolution is { } tr ? $"{tr.Current / 10000.0:0.000} ms" : "Unknown")} | **WHEA:** {p.WheaErrors14Days ?? 0} in 14d");
            summary.AppendLine($"• **Core Benchmark:** {benchText}");

            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(summary.ToString()), "content");
            form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(reportText)), "file", "scan-report.txt");

            using var resp = await Http.PostAsync(DataWebhookUrl, form, ct);
            if (resp.IsSuccessStatusCode)
                return (true, "Hardware report submitted anonymously! Thank you for helping improve Stutter Guard.");

            return (false, $"Submission returned status {resp.StatusCode}.");
        }
        catch (OperationCanceledException)
        {
            return (false, "Submission was cancelled.");
        }
        catch (Exception ex)
        {
            return (false, "Could not send report: " + ex.Message);
        }
    }

    /// <summary>Submits user feedback, suggestions, performance results, or a bug report along with the hardware profile.</summary>
    public static async Task<(bool ok, string message)> SubmitFeedbackAsync(TweakEngine engine, string userMessage, string? userContact = null, CancellationToken ct = default)
    {
        try
        {
            var p = engine.Profile;
            string reportText = ScanReport.Build(engine);
            string gpus = p.Gpus.Count > 0 ? string.Join(", ", p.Gpus.Select(g => g.Label)) : "None detected";

            var summary = new StringBuilder();
            summary.AppendLine("💬 **New User Feedback / Report**");
            summary.AppendLine("**Message:**");
            summary.AppendLine($"> {userMessage.Replace("\n", "\n> ")}");
            if (!string.IsNullOrWhiteSpace(userContact))
                summary.AppendLine($"• **Contact:** {userContact.Trim()}");
            summary.AppendLine($"• **Hardware:** {p.CpuName} | {gpus}");
            summary.AppendLine($"• **OS:** {p.Windows.Display} (v{BuildInfo.Version})");

            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(summary.ToString()), "content");
            form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(reportText)), "file1", "scan-report.txt");

            // Attach latest application log if available
            try
            {
                string todayLog = Path.Combine(Paths.LogsDir, $"{DateTime.Now:yyyyMMdd}.log");
                if (File.Exists(todayLog))
                {
                    byte[] logBytes = File.ReadAllBytes(todayLog);
                    form.Add(new ByteArrayContent(logBytes), "file2", "stutterguard.log");
                }
            }
            catch { }

            using var resp = await Http.PostAsync(BugWebhookUrl, form, ct);
            if (resp.IsSuccessStatusCode)
                return (true, "Feedback sent directly to the developer! Thank you for helping improve Stutter Guard.");

            return (false, $"Submission returned status {resp.StatusCode}.");
        }
        catch (OperationCanceledException)
        {
            return (false, "Submission was cancelled.");
        }
        catch (Exception ex)
        {
            return (false, "Could not send feedback: " + ex.Message);
        }
    }
}
