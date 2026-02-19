using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Moonshots;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UniversalDashboards.Moonshots;

/// <summary>
/// Dashboard rendering strategy for the unified moonshot overview.
/// Transforms raw moonshot data into structured dashboard panels:
/// status grid, metrics table, pipeline flow, and health summary.
///
/// Strategy metadata:
///   Name = "MoonshotOverview"
///   Description = "Unified view of all 10 moonshot features"
///   Category = "Moonshots"
/// </summary>
public sealed class MoonshotDashboardStrategy
{
    private readonly MoonshotDashboardProvider _provider;
    private readonly ILogger<MoonshotDashboardStrategy> _logger;

    /// <summary>Strategy display name.</summary>
    public string Name => "MoonshotOverview";

    /// <summary>Strategy description.</summary>
    public string Description => "Unified view of all 10 moonshot features";

    /// <summary>Strategy category.</summary>
    public string Category => "Moonshots";

    /// <summary>
    /// Initializes a new instance of the <see cref="MoonshotDashboardStrategy"/> class.
    /// </summary>
    /// <param name="provider">Dashboard provider for snapshot and trend data.</param>
    /// <param name="logger">Logger for diagnostic output.</param>
    public MoonshotDashboardStrategy(
        MoonshotDashboardProvider provider,
        ILogger<MoonshotDashboardStrategy> logger)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Renders the moonshot dashboard as structured panel data.
    /// Returns four panels: StatusGrid, MetricsTable, PipelineFlow, and HealthSummary.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="MoonshotDashboardRenderResult"/> containing all dashboard panels.</returns>
    public async Task<MoonshotDashboardRenderResult> RenderAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var snapshot = await _provider.GetSnapshotAsync(ct);

        _logger.LogDebug(
            "Rendering moonshot dashboard: {Total} moonshots, {Ready} ready",
            snapshot.TotalMoonshots, snapshot.ReadyCount);

        // Panel A: Status Grid -- one card per moonshot with status color coding
        var statusCards = new List<MoonshotStatusCard>();
        foreach (var reg in snapshot.Registrations)
        {
            var statusColor = reg.Status switch
            {
                MoonshotStatus.Ready => "green",
                MoonshotStatus.Degraded => "yellow",
                MoonshotStatus.Faulted => "red",
                MoonshotStatus.Initializing => "blue",
                MoonshotStatus.Disabled => "gray",
                MoonshotStatus.NotInstalled => "gray",
                _ => "gray"
            };

            statusCards.Add(new MoonshotStatusCard(
                Id: reg.Id,
                DisplayName: reg.DisplayName,
                Status: reg.Status.ToString(),
                StatusColor: statusColor,
                LastHealthCheckTime: reg.LastHealthCheck));
        }

        // Panel B: Metrics Table -- one row per moonshot with key metrics
        var metricsRows = new List<MoonshotMetricsRow>();
        foreach (var metrics in snapshot.Metrics)
        {
            var successRate = metrics.TotalInvocations > 0
                ? Math.Round((double)metrics.SuccessCount / metrics.TotalInvocations * 100.0, 2)
                : 100.0;

            metricsRows.Add(new MoonshotMetricsRow(
                Id: metrics.Id,
                TotalInvocations: metrics.TotalInvocations,
                SuccessRatePercent: successRate,
                AverageLatencyMs: metrics.AverageLatencyMs,
                P99LatencyMs: metrics.P99LatencyMs));
        }

        // Panel C: Pipeline Flow -- 10-stage pipeline with per-stage success/failure counts
        var pipelineStages = new List<PipelineStageInfo>();
        foreach (MoonshotId id in Enum.GetValues(typeof(MoonshotId)))
        {
            var metrics = snapshot.Metrics.FirstOrDefault(m => m.Id == id);
            pipelineStages.Add(new PipelineStageInfo(
                StageId: id,
                StageName: id.ToString(),
                StageOrder: (int)id,
                SuccessCount: metrics?.SuccessCount ?? 0,
                FailureCount: metrics?.FailureCount ?? 0));
        }

        // Panel D: Health Summary -- overview + list of degraded/faulted with reasons
        var issueList = new List<MoonshotHealthIssue>();
        foreach (var reg in snapshot.Registrations.Where(
            r => r.Status == MoonshotStatus.Degraded || r.Status == MoonshotStatus.Faulted))
        {
            var reason = reg.LastHealthReport?.Summary ?? "No health report available";
            issueList.Add(new MoonshotHealthIssue(
                Id: reg.Id,
                DisplayName: reg.DisplayName,
                Status: reg.Status.ToString(),
                Reason: reason));
        }

        var healthSummary = new MoonshotHealthSummaryPanel(
            ReadyCount: snapshot.ReadyCount,
            TotalMoonshots: snapshot.TotalMoonshots,
            DegradedCount: snapshot.DegradedCount,
            FaultedCount: snapshot.FaultedCount,
            Issues: issueList);

        var result = new MoonshotDashboardRenderResult(
            StrategyName: Name,
            GeneratedAt: snapshot.GeneratedAt,
            StatusGrid: statusCards,
            MetricsTable: metricsRows,
            PipelineFlow: pipelineStages,
            HealthSummary: healthSummary);

        _logger.LogInformation(
            "Moonshot dashboard rendered: {Total} moonshots, {Issues} issues",
            snapshot.TotalMoonshots, issueList.Count);

        return result;
    }
}

/// <summary>
/// Complete render result for the moonshot dashboard, containing all four panels.
/// </summary>
/// <param name="StrategyName">Name of the rendering strategy.</param>
/// <param name="GeneratedAt">UTC timestamp when the dashboard was rendered.</param>
/// <param name="StatusGrid">Status cards for all moonshots.</param>
/// <param name="MetricsTable">Metrics rows for all moonshots.</param>
/// <param name="PipelineFlow">Pipeline stage info for the 10-stage flow.</param>
/// <param name="HealthSummary">Health summary with issue list.</param>
public sealed record MoonshotDashboardRenderResult(
    string StrategyName,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<MoonshotStatusCard> StatusGrid,
    IReadOnlyList<MoonshotMetricsRow> MetricsTable,
    IReadOnlyList<PipelineStageInfo> PipelineFlow,
    MoonshotHealthSummaryPanel HealthSummary);

/// <summary>
/// A status card for a single moonshot in the status grid panel.
/// </summary>
/// <param name="Id">Moonshot identifier.</param>
/// <param name="DisplayName">Human-readable moonshot name.</param>
/// <param name="Status">Current status string (Ready, Degraded, Faulted, etc.).</param>
/// <param name="StatusColor">Color code for rendering (green, yellow, red, gray, blue).</param>
/// <param name="LastHealthCheckTime">When the last health check completed, if any.</param>
public sealed record MoonshotStatusCard(
    MoonshotId Id,
    string DisplayName,
    string Status,
    string StatusColor,
    DateTimeOffset? LastHealthCheckTime);

/// <summary>
/// A metrics row for a single moonshot in the metrics table panel.
/// </summary>
/// <param name="Id">Moonshot identifier.</param>
/// <param name="TotalInvocations">Total pipeline invocations.</param>
/// <param name="SuccessRatePercent">Success rate as a percentage (0-100).</param>
/// <param name="AverageLatencyMs">Average latency in milliseconds.</param>
/// <param name="P99LatencyMs">99th percentile latency in milliseconds.</param>
public sealed record MoonshotMetricsRow(
    MoonshotId Id,
    long TotalInvocations,
    double SuccessRatePercent,
    double AverageLatencyMs,
    double P99LatencyMs);

/// <summary>
/// Pipeline stage information for the pipeline flow panel.
/// </summary>
/// <param name="StageId">Moonshot identifier for this stage.</param>
/// <param name="StageName">Display name of the pipeline stage.</param>
/// <param name="StageOrder">Order in the pipeline (1-10).</param>
/// <param name="SuccessCount">Total successful executions of this stage.</param>
/// <param name="FailureCount">Total failed executions of this stage.</param>
public sealed record PipelineStageInfo(
    MoonshotId StageId,
    string StageName,
    int StageOrder,
    long SuccessCount,
    long FailureCount);

/// <summary>
/// Health summary panel showing overall health and issues.
/// </summary>
/// <param name="ReadyCount">Number of moonshots in Ready status.</param>
/// <param name="TotalMoonshots">Total number of registered moonshots.</param>
/// <param name="DegradedCount">Number of moonshots in Degraded status.</param>
/// <param name="FaultedCount">Number of moonshots in Faulted status.</param>
/// <param name="Issues">List of degraded or faulted moonshots with reasons.</param>
public sealed record MoonshotHealthSummaryPanel(
    int ReadyCount,
    int TotalMoonshots,
    int DegradedCount,
    int FaultedCount,
    IReadOnlyList<MoonshotHealthIssue> Issues);

/// <summary>
/// A single health issue for a degraded or faulted moonshot.
/// </summary>
/// <param name="Id">Moonshot identifier.</param>
/// <param name="DisplayName">Human-readable moonshot name.</param>
/// <param name="Status">Current status (Degraded or Faulted).</param>
/// <param name="Reason">Reason or health report summary.</param>
public sealed record MoonshotHealthIssue(
    MoonshotId Id,
    string DisplayName,
    string Status,
    string Reason);
