using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Moonshots;

/// <summary>
/// Aggregated metrics for a single moonshot over a time window.
/// Captures invocation counts, success/failure rates, and latency statistics
/// for monitoring and alerting.
/// </summary>
/// <param name="Id">The moonshot these metrics describe.</param>
/// <param name="TotalInvocations">Total number of pipeline invocations in the window.</param>
/// <param name="SuccessCount">Number of successful invocations.</param>
/// <param name="FailureCount">Number of failed invocations.</param>
/// <param name="AverageLatencyMs">Average execution latency in milliseconds.</param>
/// <param name="P99LatencyMs">99th percentile execution latency in milliseconds.</param>
/// <param name="WindowStart">Start of the metrics aggregation window.</param>
/// <param name="WindowEnd">End of the metrics aggregation window.</param>
public sealed record MoonshotMetrics(
    MoonshotId Id,
    long TotalInvocations,
    long SuccessCount,
    long FailureCount,
    double AverageLatencyMs,
    double P99LatencyMs,
    DateTimeOffset WindowStart,
    DateTimeOffset WindowEnd);

/// <summary>
/// A single data point in a moonshot metric time series.
/// Used for trend visualization on the moonshot dashboard.
/// </summary>
/// <param name="Timestamp">UTC timestamp of this data point.</param>
/// <param name="Value">The metric value at this point in time.</param>
/// <param name="MetricName">Name of the metric (e.g. "Latency", "SuccessRate", "Invocations").</param>
public sealed record MoonshotTrendPoint(
    DateTimeOffset Timestamp,
    double Value,
    string MetricName);

/// <summary>
/// Point-in-time snapshot of the entire moonshot dashboard state.
/// Aggregates registrations, metrics, and health reports across all moonshots
/// for unified dashboard rendering.
/// </summary>
/// <param name="Registrations">All moonshot registrations from the registry.</param>
/// <param name="Metrics">Current metrics for each moonshot.</param>
/// <param name="HealthReports">Latest health reports for each moonshot.</param>
/// <param name="GeneratedAt">UTC timestamp when this snapshot was generated.</param>
/// <param name="TotalMoonshots">Total number of registered moonshots.</param>
/// <param name="ReadyCount">Number of moonshots in Ready status.</param>
/// <param name="DegradedCount">Number of moonshots in Degraded status.</param>
/// <param name="FaultedCount">Number of moonshots in Faulted status.</param>
public sealed record MoonshotDashboardSnapshot(
    IReadOnlyList<MoonshotRegistration> Registrations,
    IReadOnlyList<MoonshotMetrics> Metrics,
    IReadOnlyList<MoonshotHealthReport> HealthReports,
    DateTimeOffset GeneratedAt,
    int TotalMoonshots,
    int ReadyCount,
    int DegradedCount,
    int FaultedCount);

/// <summary>
/// Provider interface for moonshot dashboard data. Aggregates metrics, health,
/// and registration data into dashboard-ready formats.
///
/// Implementations typically query the <see cref="IMoonshotRegistry"/> and
/// metrics storage to assemble snapshots and trends.
/// </summary>
public interface IMoonshotDashboardProvider
{
    /// <summary>
    /// Generates a point-in-time snapshot of all moonshot dashboard data.
    /// Includes registrations, metrics, and health reports.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A complete dashboard snapshot.</returns>
    Task<MoonshotDashboardSnapshot> GetSnapshotAsync(CancellationToken ct);

    /// <summary>
    /// Retrieves trend data for a specific moonshot metric over a time range.
    /// Used for time-series charts on the dashboard.
    /// </summary>
    /// <param name="id">The moonshot to query trends for.</param>
    /// <param name="metricName">The metric name (e.g. "Latency", "SuccessRate").</param>
    /// <param name="from">Start of the time range (inclusive).</param>
    /// <param name="to">End of the time range (inclusive).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Ordered list of trend points within the time range.</returns>
    Task<IReadOnlyList<MoonshotTrendPoint>> GetTrendsAsync(
        MoonshotId id,
        string metricName,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct);

    /// <summary>
    /// Retrieves current metrics for a specific moonshot.
    /// </summary>
    /// <param name="id">The moonshot to query metrics for.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Current metrics for the specified moonshot.</returns>
    Task<MoonshotMetrics> GetMetricsAsync(MoonshotId id, CancellationToken ct);
}
