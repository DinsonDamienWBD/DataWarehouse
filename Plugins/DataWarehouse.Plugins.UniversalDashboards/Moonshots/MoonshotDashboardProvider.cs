using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Moonshots;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UniversalDashboards.Moonshots;

/// <summary>
/// Production implementation of <see cref="IMoonshotDashboardProvider"/>.
/// Aggregates registration data from the moonshot registry, real-time metrics from the
/// metrics collector, and health reports into dashboard-ready snapshots and trend data.
/// </summary>
public sealed class MoonshotDashboardProvider : IMoonshotDashboardProvider
{
    private readonly IMoonshotRegistry _registry;
    private readonly MoonshotMetricsCollector _metricsCollector;
    private readonly ILogger<MoonshotDashboardProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MoonshotDashboardProvider"/> class.
    /// </summary>
    /// <param name="registry">Central moonshot registry for registration and status data.</param>
    /// <param name="metricsCollector">Metrics collector for real-time counters and latency data.</param>
    /// <param name="logger">Logger for diagnostic output.</param>
    public MoonshotDashboardProvider(
        IMoonshotRegistry registry,
        MoonshotMetricsCollector metricsCollector,
        ILogger<MoonshotDashboardProvider> logger)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _metricsCollector = metricsCollector ?? throw new ArgumentNullException(nameof(metricsCollector));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    /// <summary>
    /// Generates a point-in-time snapshot of all moonshot dashboard data.
    /// Queries the registry for registrations and status, the metrics collector for
    /// throughput/latency, and extracts health reports from each registration.
    /// </summary>
    public Task<MoonshotDashboardSnapshot> GetSnapshotAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        try
        {
            // 1. Get all registrations from registry
            var registrations = _registry.GetAll();

            // 2. Get all metrics from collector
            var metrics = _metricsCollector.GetAllMetrics();

            // 3. Extract latest health reports from registrations
            var healthReports = new List<MoonshotHealthReport>();
            foreach (var reg in registrations)
            {
                if (reg.LastHealthReport is not null)
                {
                    healthReports.Add(reg.LastHealthReport);
                }
            }

            // 4. Compute summary counts by status
            var readyCount = registrations.Count(r => r.Status == MoonshotStatus.Ready);
            var degradedCount = registrations.Count(r => r.Status == MoonshotStatus.Degraded);
            var faultedCount = registrations.Count(r => r.Status == MoonshotStatus.Faulted);

            var snapshot = new MoonshotDashboardSnapshot(
                Registrations: registrations,
                Metrics: metrics,
                HealthReports: healthReports,
                GeneratedAt: DateTimeOffset.UtcNow,
                TotalMoonshots: registrations.Count,
                ReadyCount: readyCount,
                DegradedCount: degradedCount,
                FaultedCount: faultedCount);

            _logger.LogDebug(
                "Generated moonshot dashboard snapshot: {Total} moonshots, {Ready} ready, {Degraded} degraded, {Faulted} faulted",
                snapshot.TotalMoonshots, readyCount, degradedCount, faultedCount);

            return Task.FromResult(snapshot);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate moonshot dashboard snapshot");
            throw;
        }
    }

    /// <inheritdoc />
    /// <summary>
    /// Retrieves trend data for a specific moonshot metric over a time range.
    /// Delegates to the metrics collector's trend buffer.
    /// </summary>
    public Task<IReadOnlyList<MoonshotTrendPoint>> GetTrendsAsync(
        MoonshotId id,
        string metricName,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var trends = _metricsCollector.GetTrends(id, metricName, from, to);

        _logger.LogDebug(
            "Retrieved {Count} trend points for {Moonshot}/{Metric} from {From} to {To}",
            trends.Count, id, metricName, from, to);

        return Task.FromResult(trends);
    }

    /// <inheritdoc />
    /// <summary>
    /// Retrieves current metrics for a specific moonshot.
    /// Delegates to the metrics collector.
    /// </summary>
    public Task<MoonshotMetrics> GetMetricsAsync(MoonshotId id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var metrics = _metricsCollector.GetMetrics(id);
        return Task.FromResult(metrics);
    }
}
