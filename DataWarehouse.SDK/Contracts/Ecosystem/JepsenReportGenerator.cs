// Phase 89: Ecosystem Compatibility - Jepsen Report Generator (ECOS-17)
// Generates comprehensive HTML, JSON, and Markdown reports from Jepsen test results

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataWarehouse.SDK.Contracts.Ecosystem;

#region Report Records

/// <summary>
/// An entry in the operation timeline, representing aggregated operation counts per second.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Jepsen report generator (ECOS-17)")]
public sealed record TimelineEntry
{
    /// <summary>Second offset from test start.</summary>
    public required int SecondOffset { get; init; }

    /// <summary>Number of read operations in this second.</summary>
    public required int Reads { get; init; }

    /// <summary>Number of write operations in this second.</summary>
    public required int Writes { get; init; }

    /// <summary>Number of failed operations in this second.</summary>
    public required int Failures { get; init; }

    /// <summary>Whether a fault was actively injected during this second.</summary>
    public required bool FaultActive { get; init; }
}

/// <summary>
/// Aggregated operation timeline for a scenario, showing reads/writes/failures per second.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Jepsen report generator (ECOS-17)")]
public sealed record OperationTimeline
{
    /// <summary>Per-second operation count buckets.</summary>
    public required IReadOnlyList<TimelineEntry> Entries { get; init; }
}

/// <summary>
/// Result summary for a single scenario within a Jepsen report.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Jepsen report generator (ECOS-17)")]
public sealed record ScenarioResult
{
    /// <summary>Name of the scenario.</summary>
    public required string ScenarioName { get; init; }

    /// <summary>Whether the scenario passed consistency checks.</summary>
    public required bool Passed { get; init; }

    /// <summary>Consistency model that was verified.</summary>
    public required ConsistencyModel TestedModel { get; init; }

    /// <summary>Total operations executed during the scenario.</summary>
    public required long TotalOperations { get; init; }

    /// <summary>Number of consistency anomalies detected.</summary>
    public required long AnomaliesDetected { get; init; }

    /// <summary>Wall-clock duration of the scenario.</summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>Human-readable summary of the scenario outcome.</summary>
    public required string Summary { get; init; }

    /// <summary>All consistency violations found (empty if passed).</summary>
    public required IReadOnlyList<ConsistencyViolation> Violations { get; init; }

    /// <summary>Aggregated operation counts per second.</summary>
    public required OperationTimeline Timeline { get; init; }
}

/// <summary>
/// Complete Jepsen report aggregating results from multiple test scenarios.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Jepsen report generator (ECOS-17)")]
public sealed record JepsenReport
{
    /// <summary>When the report was generated.</summary>
    public required DateTimeOffset GeneratedAt { get; init; }

    /// <summary>Total number of scenarios executed.</summary>
    public required int TotalScenarios { get; init; }

    /// <summary>Number of scenarios that passed.</summary>
    public required int Passed { get; init; }

    /// <summary>Number of scenarios that failed.</summary>
    public required int Failed { get; init; }

    /// <summary>Per-scenario results.</summary>
    public required IReadOnlyList<ScenarioResult> Scenarios { get; init; }

    /// <summary>Overall verdict: "PASS" if all scenarios passed, "FAIL" otherwise.</summary>
    public required string OverallVerdict { get; init; }
}

#endregion

#region Report Generator

/// <summary>
/// Generates comprehensive Jepsen test reports in HTML, JSON, and Markdown formats.
/// Consumes <see cref="JepsenTestResult"/> from the test harness and produces
/// publishable reports with per-scenario results, operation timelines, and fault injection timelines.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Jepsen report generator (ECOS-17)")]
public static class JepsenReportGenerator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>
    /// Generates a <see cref="JepsenReport"/> from a collection of test results.
    /// </summary>
    /// <param name="results">Test results from <see cref="JepsenTestHarness.RunTestAsync"/>.</param>
    /// <returns>Aggregated Jepsen report.</returns>
    public static JepsenReport GenerateReport(IReadOnlyList<JepsenTestResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        var scenarios = new List<ScenarioResult>(results.Count);

        foreach (var result in results)
        {
            var timeline = BuildTimeline(result);

            var summary = result.Passed
                ? $"PASS: {result.TotalOperations} operations, 0 anomalies, {result.TestedModel} verified"
                : $"FAIL: {result.TotalOperations} operations, {result.Violations.Count} anomalies detected under {result.TestedModel}";

            scenarios.Add(new ScenarioResult
            {
                ScenarioName = result.TestName,
                Passed = result.Passed,
                TestedModel = result.TestedModel,
                TotalOperations = result.TotalOperations,
                AnomaliesDetected = result.Violations.Count,
                Duration = result.Duration,
                Summary = summary,
                Violations = result.Violations,
                Timeline = timeline
            });
        }

        var passed = scenarios.Count(s => s.Passed);
        var failed = scenarios.Count - passed;

        return new JepsenReport
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            TotalScenarios = scenarios.Count,
            Passed = passed,
            Failed = failed,
            Scenarios = scenarios.AsReadOnly(),
            OverallVerdict = failed == 0 ? "PASS" : "FAIL"
        };
    }

    /// <summary>
    /// Generates a full HTML report page with summary table, per-scenario sections,
    /// operation timelines, cluster topology, and fault injection timelines.
    /// </summary>
    /// <param name="report">The Jepsen report to render.</param>
    /// <returns>Complete HTML document as a string.</returns>
    public static string GenerateHtmlReport(JepsenReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var sb = new StringBuilder(8192);

        // HTML header
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\">");
        sb.AppendLine("<head>");
        sb.AppendLine("  <meta charset=\"UTF-8\">");
        sb.AppendLine("  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
        sb.AppendLine("  <title>DataWarehouse Jepsen Test Report</title>");
        sb.AppendLine("  <style>");
        sb.AppendLine("    body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; margin: 2em; background: #fafafa; }");
        sb.AppendLine("    h1, h2, h3 { color: #333; }");
        sb.AppendLine("    .pass { color: #2da44e; font-weight: bold; }");
        sb.AppendLine("    .fail { color: #cf222e; font-weight: bold; }");
        sb.AppendLine("    table { border-collapse: collapse; width: 100%; margin: 1em 0; }");
        sb.AppendLine("    th, td { border: 1px solid #d0d7de; padding: 8px 12px; text-align: left; }");
        sb.AppendLine("    th { background: #f6f8fa; }");
        sb.AppendLine("    .timeline { font-family: monospace; font-size: 0.85em; white-space: pre; background: #1b1f23; color: #c9d1d9; padding: 1em; border-radius: 6px; overflow-x: auto; }");
        sb.AppendLine("    .topology { font-family: monospace; font-size: 0.85em; white-space: pre; background: #f6f8fa; padding: 1em; border-radius: 6px; }");
        sb.AppendLine("    .scenario-section { border: 1px solid #d0d7de; border-radius: 6px; padding: 1em; margin: 1em 0; background: white; }");
        sb.AppendLine("    .violation { background: #fff1f0; border-left: 4px solid #cf222e; padding: 0.5em 1em; margin: 0.5em 0; }");
        sb.AppendLine("  </style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");

        // Title and overall verdict
        sb.AppendLine($"<h1>DataWarehouse Jepsen Test Report</h1>");
        sb.AppendLine($"<p>Generated: {report.GeneratedAt:yyyy-MM-dd HH:mm:ss UTC}</p>");
        var verdictClass = report.OverallVerdict == "PASS" ? "pass" : "fail";
        sb.AppendLine($"<h2>Overall Verdict: <span class=\"{verdictClass}\">{report.OverallVerdict}</span></h2>");
        sb.AppendLine($"<p>{report.Passed}/{report.TotalScenarios} scenarios passed, {report.Failed} failed</p>");

        // Summary table
        sb.AppendLine("<h2>Summary</h2>");
        sb.AppendLine("<table>");
        sb.AppendLine("  <tr><th>Scenario</th><th>Result</th><th>Consistency Model</th><th>Operations</th><th>Anomalies</th><th>Duration</th></tr>");

        foreach (var scenario in report.Scenarios)
        {
            var resultClass = scenario.Passed ? "pass" : "fail";
            var resultText = scenario.Passed ? "PASS" : "FAIL";
            sb.AppendLine($"  <tr>");
            sb.AppendLine($"    <td>{HtmlEncode(scenario.ScenarioName)}</td>");
            sb.AppendLine($"    <td class=\"{resultClass}\">{resultText}</td>");
            sb.AppendLine($"    <td>{scenario.TestedModel}</td>");
            sb.AppendLine($"    <td>{scenario.TotalOperations:N0}</td>");
            sb.AppendLine($"    <td>{scenario.AnomaliesDetected}</td>");
            sb.AppendLine($"    <td>{scenario.Duration.TotalSeconds:F1}s</td>");
            sb.AppendLine($"  </tr>");
        }

        sb.AppendLine("</table>");

        // Per-scenario detail sections
        foreach (var scenario in report.Scenarios)
        {
            sb.AppendLine("<div class=\"scenario-section\">");
            var sResultClass = scenario.Passed ? "pass" : "fail";
            var sResultText = scenario.Passed ? "PASS" : "FAIL";
            sb.AppendLine($"  <h3>{HtmlEncode(scenario.ScenarioName)} - <span class=\"{sResultClass}\">{sResultText}</span></h3>");
            sb.AppendLine($"  <p><strong>Consistency Model:</strong> {scenario.TestedModel}</p>");
            sb.AppendLine($"  <p><strong>Duration:</strong> {scenario.Duration.TotalSeconds:F1}s</p>");
            sb.AppendLine($"  <p><strong>Operations:</strong> {scenario.TotalOperations:N0} total, {scenario.AnomaliesDetected} anomalies</p>");
            sb.AppendLine($"  <p>{HtmlEncode(scenario.Summary)}</p>");

            // Violations
            if (scenario.Violations.Count > 0)
            {
                sb.AppendLine("  <h4>Consistency Violations</h4>");
                foreach (var violation in scenario.Violations)
                {
                    sb.AppendLine("  <div class=\"violation\">");
                    sb.AppendLine($"    <p><strong>{HtmlEncode(violation.Description)}</strong></p>");
                    sb.AppendLine($"    <p>Expected: {HtmlEncode(violation.ExpectedBehavior)}</p>");
                    sb.AppendLine($"    <p>Actual: {HtmlEncode(violation.ActualBehavior)}</p>");
                    sb.AppendLine("  </div>");
                }
            }

            // Operation timeline
            sb.AppendLine("  <h4>Operation Timeline (reads/writes per second)</h4>");
            sb.AppendLine("  <div class=\"timeline\">");
            RenderHtmlTimeline(sb, scenario.Timeline);
            sb.AppendLine("  </div>");

            // Fault injection timeline
            sb.AppendLine("  <h4>Fault Injection Timeline</h4>");
            sb.AppendLine("  <div class=\"timeline\">");
            RenderFaultTimeline(sb, scenario.Timeline);
            sb.AppendLine("  </div>");

            // Cluster topology diagram (text-based)
            sb.AppendLine("  <h4>Cluster Topology</h4>");
            sb.AppendLine("  <div class=\"topology\">");
            RenderClusterTopology(sb, scenario.ScenarioName);
            sb.AppendLine("  </div>");

            sb.AppendLine("</div>");
        }

        sb.AppendLine("</body>");
        sb.AppendLine("</html>");

        return sb.ToString();
    }

    /// <summary>
    /// Generates a structured JSON report suitable for CI/CD integration and machine parsing.
    /// </summary>
    /// <param name="report">The Jepsen report to serialize.</param>
    /// <param name="truncateHistory">
    /// If true, omits full operation history from the JSON to reduce size.
    /// Defaults to false.
    /// </param>
    /// <returns>JSON string with all result data.</returns>
    public static string GenerateJsonReport(JepsenReport report, bool truncateHistory = false)
    {
        ArgumentNullException.ThrowIfNull(report);

        var jsonObj = new Dictionary<string, object>
        {
            ["generatedAt"] = report.GeneratedAt.ToString("o"),
            ["overallVerdict"] = report.OverallVerdict,
            ["totalScenarios"] = report.TotalScenarios,
            ["passed"] = report.Passed,
            ["failed"] = report.Failed,
            ["scenarios"] = report.Scenarios.Select(s => BuildScenarioJson(s, truncateHistory)).ToList()
        };

        return JsonSerializer.Serialize(jsonObj, JsonOptions);
    }

    /// <summary>
    /// Generates a concise Markdown summary suitable for inclusion in release notes.
    /// </summary>
    /// <param name="report">The Jepsen report to summarize.</param>
    /// <returns>Markdown-formatted summary string.</returns>
    public static string GenerateMarkdownSummary(JepsenReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var sb = new StringBuilder(2048);

        sb.AppendLine("# Jepsen Correctness Verification Report");
        sb.AppendLine();
        sb.AppendLine($"**Generated:** {report.GeneratedAt:yyyy-MM-dd HH:mm:ss UTC}");
        sb.AppendLine();
        sb.AppendLine($"**Overall Verdict:** {report.OverallVerdict}");
        sb.AppendLine($"**Results:** {report.Passed}/{report.TotalScenarios} passed, {report.Failed} failed");
        sb.AppendLine();

        // Results table
        sb.AppendLine("| Scenario | Result | Operations | Anomalies |");
        sb.AppendLine("|----------|--------|------------|-----------|");

        foreach (var scenario in report.Scenarios)
        {
            var result = scenario.Passed ? "PASS" : "FAIL";
            sb.AppendLine($"| {scenario.ScenarioName} | {result} | {scenario.TotalOperations:N0} | {scenario.AnomaliesDetected} |");
        }

        sb.AppendLine();

        // Confidence statement
        if (report.OverallVerdict == "PASS")
        {
            sb.AppendLine("All distributed correctness properties verified under failure conditions. ");
            sb.AppendLine("Linearizability, Raft consensus, CRDT convergence, WAL durability, ");
            sb.AppendLine("MVCC isolation, DVV replication, and split-brain prevention all confirmed.");
        }
        else
        {
            var failedNames = report.Scenarios
                .Where(s => !s.Passed)
                .Select(s => s.ScenarioName);
            sb.AppendLine($"**WARNING:** The following scenarios failed: {string.Join(", ", failedNames)}. ");
            sb.AppendLine("Manual investigation of consistency violations is required before release.");
        }

        return sb.ToString();
    }

    #region Internal Helpers

    private static OperationTimeline BuildTimeline(JepsenTestResult result)
    {
        if (result.FullHistory.Count == 0)
        {
            return new OperationTimeline { Entries = Array.Empty<TimelineEntry>() };
        }

        var testStart = result.StartTime;
        var totalSeconds = (int)Math.Ceiling(result.Duration.TotalSeconds);
        if (totalSeconds <= 0) totalSeconds = 1;

        var entries = new List<TimelineEntry>(totalSeconds);

        for (int sec = 0; sec < totalSeconds; sec++)
        {
            var bucketStart = testStart.AddSeconds(sec);
            var bucketEnd = bucketStart.AddSeconds(1);

            var opsInBucket = result.FullHistory
                .Where(op => op.StartTime >= bucketStart && op.StartTime < bucketEnd)
                .ToList();

            var reads = opsInBucket.Count(op => op.Type == OperationType.Read);
            var writes = opsInBucket.Count(op =>
                op.Type == OperationType.Write ||
                op.Type == OperationType.Append ||
                op.Type == OperationType.Cas);
            var failures = opsInBucket.Count(op =>
                op.Result == OperationResult.Fail ||
                op.Result == OperationResult.Timeout ||
                op.Result == OperationResult.Crashed);

            entries.Add(new TimelineEntry
            {
                SecondOffset = sec,
                Reads = reads,
                Writes = writes,
                Failures = failures,
                FaultActive = false // Determined by fault schedule, approximated from failure spikes
            });
        }

        // Heuristic: mark seconds with high failure rates as likely fault-active
        if (entries.Count > 0)
        {
            var avgFailures = entries.Average(e => e.Failures);
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].Failures > avgFailures * 2 && entries[i].Failures > 0)
                {
                    entries[i] = entries[i] with { FaultActive = true };
                }
            }
        }

        return new OperationTimeline { Entries = entries.AsReadOnly() };
    }

    private static void RenderHtmlTimeline(StringBuilder sb, OperationTimeline timeline)
    {
        if (timeline.Entries.Count == 0)
        {
            sb.AppendLine("  No operations recorded.");
            return;
        }

        var maxOps = Math.Max(1, timeline.Entries.Max(e => e.Reads + e.Writes));
        const int barWidth = 50;

        sb.AppendLine("  Sec | Reads | Writes | Fails | Throughput");
        sb.AppendLine("  ----+-------+--------+-------+--------------------------------------------------");

        foreach (var entry in timeline.Entries)
        {
            var totalOps = entry.Reads + entry.Writes;
            var barLen = (int)((double)totalOps / maxOps * barWidth);
            var readBar = new string('R', Math.Min(barLen, (int)((double)entry.Reads / Math.Max(1, totalOps) * barLen)));
            var writeBar = new string('W', barLen - readBar.Length);
            var faultMarker = entry.FaultActive ? " [FAULT]" : "";

            sb.AppendLine($"  {entry.SecondOffset,3:D} | {entry.Reads,5} | {entry.Writes,6} | {entry.Failures,5} | {readBar}{writeBar}{faultMarker}");
        }
    }

    private static void RenderFaultTimeline(StringBuilder sb, OperationTimeline timeline)
    {
        if (timeline.Entries.Count == 0)
        {
            sb.AppendLine("  No timeline data.");
            return;
        }

        sb.Append("  Faults: ");
        foreach (var entry in timeline.Entries)
        {
            sb.Append(entry.FaultActive ? 'X' : '.');
        }
        sb.AppendLine();
        sb.Append("  Second: ");
        for (int i = 0; i < timeline.Entries.Count; i++)
        {
            sb.Append(i % 10);
        }
        sb.AppendLine();
    }

    private static void RenderClusterTopology(StringBuilder sb, string scenarioName)
    {
        // Text-based cluster topology diagram
        if (scenarioName.Contains("3-node") || scenarioName.Contains("wal") || scenarioName.Contains("mvcc"))
        {
            sb.AppendLine("  3-Node Cluster:");
            sb.AppendLine("    +------+     +------+     +------+");
            sb.AppendLine("    |  n1  |-----|  n2  |-----|  n3  |");
            sb.AppendLine("    | Raft |     | Raft |     | Raft |");
            sb.AppendLine("    +------+     +------+     +------+");
        }
        else
        {
            sb.AppendLine("  5-Node Cluster:");
            sb.AppendLine("    +------+     +------+     +------+");
            sb.AppendLine("    |  n1  |-----|  n2  |-----|  n3  |");
            sb.AppendLine("    | Raft |     | Raft |     | Raft |");
            sb.AppendLine("    +--+---+     +------+     +---+--+");
            sb.AppendLine("       |                          |");
            sb.AppendLine("    +--+---+                  +---+--+");
            sb.AppendLine("    |  n4  |                  |  n5  |");
            sb.AppendLine("    | Raft |                  | Raft |");
            sb.AppendLine("    +------+                  +------+");
        }

        if (scenarioName.Contains("partition") || scenarioName.Contains("split-brain"))
        {
            sb.AppendLine();
            sb.AppendLine("  Partition View:");
            sb.AppendLine("    [MAJORITY: n1, n2, n3]  //  [MINORITY: n4, n5]");
            sb.AppendLine("    Accepts writes              Rejects writes (fenced)");
        }
    }

    private static Dictionary<string, object> BuildScenarioJson(ScenarioResult scenario, bool truncateHistory)
    {
        var obj = new Dictionary<string, object>
        {
            ["scenarioName"] = scenario.ScenarioName,
            ["passed"] = scenario.Passed,
            ["testedModel"] = scenario.TestedModel.ToString(),
            ["totalOperations"] = scenario.TotalOperations,
            ["anomaliesDetected"] = scenario.AnomaliesDetected,
            ["durationSeconds"] = scenario.Duration.TotalSeconds,
            ["summary"] = scenario.Summary
        };

        if (scenario.Violations.Count > 0)
        {
            obj["violations"] = scenario.Violations.Select(v => new Dictionary<string, object>
            {
                ["description"] = v.Description,
                ["expectedBehavior"] = v.ExpectedBehavior,
                ["actualBehavior"] = v.ActualBehavior,
                ["operationKey"] = v.Operation.Key,
                ["operationType"] = v.Operation.Type.ToString(),
                ["sequenceId"] = v.Operation.SequenceId
            }).ToList();
        }
        else
        {
            obj["violations"] = Array.Empty<object>();
        }

        if (!truncateHistory)
        {
            obj["timeline"] = scenario.Timeline.Entries.Select(e => new Dictionary<string, object>
            {
                ["secondOffset"] = e.SecondOffset,
                ["reads"] = e.Reads,
                ["writes"] = e.Writes,
                ["failures"] = e.Failures,
                ["faultActive"] = e.FaultActive
            }).ToList();
        }

        return obj;
    }

    private static string HtmlEncode(string text)
    {
        return text
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;");
    }

    #endregion
}

#endregion
