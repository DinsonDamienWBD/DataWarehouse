namespace DataWarehouse.SDK.Federation;

using System.Collections.Concurrent;

#region H18: Federation Health Dashboard Metrics

/// <summary>
/// Unified health aggregator for federation-wide monitoring.
/// </summary>
public sealed class FederationHealthAggregator : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, NodeHealthInfo> _nodeHealth = new();
    private readonly ConcurrentDictionary<string, FederationMetric> _metrics = new();
    private readonly Timer _aggregationTimer;
    private readonly FederationHealthConfig _config;
    private volatile bool _disposed;

    public event EventHandler<HealthAlertEventArgs>? HealthAlert;

    public FederationHealthAggregator(FederationHealthConfig? config = null)
    {
        _config = config ?? new FederationHealthConfig();
        _aggregationTimer = new Timer(
            AggregateMetrics,
            null,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(_config.AggregationIntervalSeconds));
    }

    /// <summary>
    /// Reports health status from a node.
    /// </summary>
    public void ReportNodeHealth(string nodeId, NodeHealthReport report)
    {
        var info = _nodeHealth.GetOrAdd(nodeId, _ => new NodeHealthInfo { NodeId = nodeId });
        info.LastReport = report;
        info.LastReportedAt = DateTime.UtcNow;
        info.HealthScore = CalculateNodeHealthScore(report);

        // Check for alerts
        if (info.HealthScore < _config.CriticalHealthThreshold)
        {
            HealthAlert?.Invoke(this, new HealthAlertEventArgs
            {
                NodeId = nodeId,
                AlertType = HealthAlertType.NodeCritical,
                Message = $"Node {nodeId} health score: {info.HealthScore:P0}",
                Severity = AlertSeverity.Critical
            });
        }
    }

    /// <summary>
    /// Records a federation-wide metric.
    /// </summary>
    public void RecordMetric(string metricName, double value, Dictionary<string, string>? labels = null)
    {
        var metric = _metrics.GetOrAdd(metricName, _ => new FederationMetric { Name = metricName });
        metric.AddSample(value, labels);
    }

    /// <summary>
    /// Gets health score for a specific node.
    /// </summary>
    public double GetNodeHealthScore(string nodeId) =>
        _nodeHealth.TryGetValue(nodeId, out var info) ? info.HealthScore : 0;

    /// <summary>
    /// Gets aggregated federation health score.
    /// </summary>
    public FederationHealthSummary GetHealthSummary()
    {
        var nodes = _nodeHealth.Values.ToList();
        var healthyCount = nodes.Count(n => n.HealthScore >= _config.HealthyThreshold);
        var degradedCount = nodes.Count(n => n.HealthScore >= _config.CriticalHealthThreshold && n.HealthScore < _config.HealthyThreshold);
        var criticalCount = nodes.Count(n => n.HealthScore < _config.CriticalHealthThreshold);

        return new FederationHealthSummary
        {
            TotalNodes = nodes.Count,
            HealthyNodes = healthyCount,
            DegradedNodes = degradedCount,
            CriticalNodes = criticalCount,
            OverallScore = nodes.Count > 0 ? nodes.Average(n => n.HealthScore) : 0,
            Status = DetermineOverallStatus(healthyCount, degradedCount, criticalCount, nodes.Count),
            Timestamp = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Gets all node health information.
    /// </summary>
    public IReadOnlyDictionary<string, NodeHealthInfo> GetAllNodeHealth() =>
        new Dictionary<string, NodeHealthInfo>(_nodeHealth);

    /// <summary>
    /// Gets federation-wide metrics.
    /// </summary>
    public IReadOnlyDictionary<string, FederationMetric> GetMetrics() =>
        new Dictionary<string, FederationMetric>(_metrics);

    /// <summary>
    /// Analyzes health trends over time.
    /// </summary>
    public HealthTrendAnalysis AnalyzeHealthTrends(TimeSpan window)
    {
        var cutoff = DateTime.UtcNow - window;
        var recentReports = _nodeHealth.Values
            .Where(n => n.LastReportedAt >= cutoff)
            .ToList();

        var avgScore = recentReports.Count > 0 ? recentReports.Average(n => n.HealthScore) : 0;
        var trend = CalculateTrend(recentReports);

        return new HealthTrendAnalysis
        {
            WindowStart = cutoff,
            WindowEnd = DateTime.UtcNow,
            AverageHealthScore = avgScore,
            Trend = trend,
            SampleCount = recentReports.Count
        };
    }

    private double CalculateNodeHealthScore(NodeHealthReport report)
    {
        // Weighted health score calculation
        var cpuScore = Math.Max(0, 1 - report.CpuUsagePercent / 100.0);
        var memoryScore = Math.Max(0, 1 - report.MemoryUsagePercent / 100.0);
        var diskScore = Math.Max(0, 1 - report.DiskUsagePercent / 100.0);
        var latencyScore = Math.Max(0, 1 - Math.Min(1, report.AvgLatencyMs / 1000.0));
        var errorScore = Math.Max(0, 1 - Math.Min(1, report.ErrorRate));

        return (cpuScore * 0.2) + (memoryScore * 0.2) + (diskScore * 0.2) +
               (latencyScore * 0.2) + (errorScore * 0.2);
    }

    private FederationStatus DetermineOverallStatus(int healthy, int degraded, int critical, int total)
    {
        if (total == 0) return FederationStatus.Unknown;
        if (critical > total / 3) return FederationStatus.Critical;
        if (degraded + critical > total / 2) return FederationStatus.Degraded;
        if (healthy == total) return FederationStatus.Healthy;
        return FederationStatus.Warning;
    }

    private HealthTrend CalculateTrend(List<NodeHealthInfo> reports)
    {
        if (reports.Count < 2) return HealthTrend.Stable;

        var avgScore = reports.Average(r => r.HealthScore);
        var recent = reports.OrderByDescending(r => r.LastReportedAt).Take(reports.Count / 2);
        var older = reports.OrderByDescending(r => r.LastReportedAt).Skip(reports.Count / 2);

        var recentAvg = recent.Any() ? recent.Average(r => r.HealthScore) : avgScore;
        var olderAvg = older.Any() ? older.Average(r => r.HealthScore) : avgScore;

        var delta = recentAvg - olderAvg;
        if (delta > 0.05) return HealthTrend.Improving;
        if (delta < -0.05) return HealthTrend.Degrading;
        return HealthTrend.Stable;
    }

    private void AggregateMetrics(object? state)
    {
        if (_disposed) return;

        // Record aggregate metrics
        var summary = GetHealthSummary();
        RecordMetric("federation.overall_health", summary.OverallScore);
        RecordMetric("federation.healthy_nodes", summary.HealthyNodes);
        RecordMetric("federation.degraded_nodes", summary.DegradedNodes);
        RecordMetric("federation.critical_nodes", summary.CriticalNodes);
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        _aggregationTimer.Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed class FederationHealthConfig
{
    public double HealthyThreshold { get; set; } = 0.8;
    public double CriticalHealthThreshold { get; set; } = 0.5;
    public int AggregationIntervalSeconds { get; set; } = 30;
}

public sealed class NodeHealthInfo
{
    public string NodeId { get; init; } = string.Empty;
    public NodeHealthReport? LastReport { get; set; }
    public DateTime LastReportedAt { get; set; }
    public double HealthScore { get; set; }
}

public sealed class NodeHealthReport
{
    public double CpuUsagePercent { get; init; }
    public double MemoryUsagePercent { get; init; }
    public double DiskUsagePercent { get; init; }
    public double AvgLatencyMs { get; init; }
    public double ErrorRate { get; init; }
    public long ActiveConnections { get; init; }
    public long QueuedRequests { get; init; }
}

public sealed class FederationHealthSummary
{
    public int TotalNodes { get; init; }
    public int HealthyNodes { get; init; }
    public int DegradedNodes { get; init; }
    public int CriticalNodes { get; init; }
    public double OverallScore { get; init; }
    public FederationStatus Status { get; init; }
    public DateTime Timestamp { get; init; }
}

public enum FederationStatus { Unknown, Healthy, Warning, Degraded, Critical }

public sealed class FederationMetric
{
    public string Name { get; init; } = string.Empty;
    private readonly ConcurrentQueue<MetricSample> _samples = new();
    private const int MaxSamples = 1000;

    public void AddSample(double value, Dictionary<string, string>? labels = null)
    {
        _samples.Enqueue(new MetricSample
        {
            Value = value,
            Timestamp = DateTime.UtcNow,
            Labels = labels ?? new()
        });

        while (_samples.Count > MaxSamples)
            _samples.TryDequeue(out _);
    }

    public double GetLatestValue() =>
        _samples.TryPeek(out var sample) ? sample.Value : 0;

    public double GetAverage(TimeSpan window)
    {
        var cutoff = DateTime.UtcNow - window;
        var recent = _samples.Where(s => s.Timestamp >= cutoff).ToList();
        return recent.Count > 0 ? recent.Average(s => s.Value) : 0;
    }

    public IReadOnlyList<MetricSample> GetSamples(TimeSpan window)
    {
        var cutoff = DateTime.UtcNow - window;
        return _samples.Where(s => s.Timestamp >= cutoff).ToList();
    }
}

public sealed class MetricSample
{
    public double Value { get; init; }
    public DateTime Timestamp { get; init; }
    public Dictionary<string, string> Labels { get; init; } = new();
}

public sealed class HealthTrendAnalysis
{
    public DateTime WindowStart { get; init; }
    public DateTime WindowEnd { get; init; }
    public double AverageHealthScore { get; init; }
    public HealthTrend Trend { get; init; }
    public int SampleCount { get; init; }
}

public enum HealthTrend { Improving, Stable, Degrading }

public sealed class HealthAlertEventArgs : EventArgs
{
    public string NodeId { get; init; } = string.Empty;
    public HealthAlertType AlertType { get; init; }
    public string Message { get; init; } = string.Empty;
    public AlertSeverity Severity { get; init; }
}

public enum HealthAlertType { NodeCritical, NodeDegraded, QuorumLost, HighLatency, HighErrorRate }
public enum AlertSeverity { Info, Warning, Critical }

#endregion
