using System.Collections.Concurrent;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataQuality.Strategies.Monitoring;

#region Monitoring Types

/// <summary>
/// Quality metric measurement.
/// </summary>
public sealed class QualityMetric
{
    /// <summary>
    /// Metric name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Metric value.
    /// </summary>
    public double Value { get; init; }

    /// <summary>
    /// Measurement timestamp.
    /// </summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Metric unit.
    /// </summary>
    public string? Unit { get; init; }

    /// <summary>
    /// Dimension or category.
    /// </summary>
    public string? Dimension { get; init; }

    /// <summary>
    /// Tags for filtering.
    /// </summary>
    public Dictionary<string, string>? Tags { get; init; }
}

/// <summary>
/// Quality threshold configuration.
/// </summary>
public sealed class QualityThreshold
{
    /// <summary>
    /// Threshold identifier.
    /// </summary>
    public required string ThresholdId { get; init; }

    /// <summary>
    /// Metric name to monitor.
    /// </summary>
    public required string MetricName { get; init; }

    /// <summary>
    /// Minimum acceptable value (null = no minimum).
    /// </summary>
    public double? MinValue { get; init; }

    /// <summary>
    /// Maximum acceptable value (null = no maximum).
    /// </summary>
    public double? MaxValue { get; init; }

    /// <summary>
    /// Warning threshold (percentage of limit).
    /// </summary>
    public double WarningThreshold { get; init; } = 0.9;

    /// <summary>
    /// Whether the threshold is enabled.
    /// </summary>
    public bool Enabled { get; init; } = true;
}

/// <summary>
/// Quality alert raised when threshold is breached.
/// </summary>
public sealed class QualityAlert
{
    /// <summary>
    /// Alert identifier.
    /// </summary>
    public string AlertId { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Alert severity.
    /// </summary>
    public required AlertSeverity Severity { get; init; }

    /// <summary>
    /// Metric that triggered the alert.
    /// </summary>
    public required string MetricName { get; init; }

    /// <summary>
    /// Current metric value.
    /// </summary>
    public double CurrentValue { get; init; }

    /// <summary>
    /// Threshold that was breached.
    /// </summary>
    public required QualityThreshold Threshold { get; init; }

    /// <summary>
    /// Alert message.
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// When the alert was raised.
    /// </summary>
    public DateTime RaisedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Whether the alert has been acknowledged.
    /// </summary>
    public bool Acknowledged { get; set; }

    /// <summary>
    /// When the alert was acknowledged.
    /// </summary>
    public DateTime? AcknowledgedAt { get; set; }

    /// <summary>
    /// Who acknowledged the alert.
    /// </summary>
    public string? AcknowledgedBy { get; set; }
}

/// <summary>
/// Alert severity levels.
/// </summary>
public enum AlertSeverity
{
    /// <summary>Informational alert.</summary>
    Info,
    /// <summary>Warning - approaching threshold.</summary>
    Warning,
    /// <summary>Error - threshold breached.</summary>
    Error,
    /// <summary>Critical - severe quality degradation.</summary>
    Critical
}

/// <summary>
/// Quality trend over time.
/// </summary>
public sealed class QualityTrend
{
    /// <summary>
    /// Metric name.
    /// </summary>
    public required string MetricName { get; init; }

    /// <summary>
    /// Time period.
    /// </summary>
    public required TimeSpan Period { get; init; }

    /// <summary>
    /// Data points.
    /// </summary>
    public List<QualityMetric> DataPoints { get; init; } = new();

    /// <summary>
    /// Trend direction.
    /// </summary>
    public TrendDirection Direction { get; init; }

    /// <summary>
    /// Percentage change over period.
    /// </summary>
    public double PercentChange { get; init; }

    /// <summary>
    /// Average value.
    /// </summary>
    public double Average { get; init; }

    /// <summary>
    /// Minimum value.
    /// </summary>
    public double Min { get; init; }

    /// <summary>
    /// Maximum value.
    /// </summary>
    public double Max { get; init; }

    /// <summary>
    /// Standard deviation.
    /// </summary>
    public double StdDev { get; init; }
}

/// <summary>
/// Trend direction.
/// </summary>
public enum TrendDirection
{
    /// <summary>Improving quality.</summary>
    Improving,
    /// <summary>Stable quality.</summary>
    Stable,
    /// <summary>Degrading quality.</summary>
    Degrading
}

/// <summary>
/// SLA (Service Level Agreement) configuration.
/// </summary>
public sealed class QualitySla
{
    /// <summary>
    /// SLA identifier.
    /// </summary>
    public required string SlaId { get; init; }

    /// <summary>
    /// SLA name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Target quality score (0-100).
    /// </summary>
    public double TargetScore { get; init; } = 95;

    /// <summary>
    /// Minimum acceptable score.
    /// </summary>
    public double MinScore { get; init; } = 85;

    /// <summary>
    /// Evaluation period.
    /// </summary>
    public TimeSpan EvaluationPeriod { get; init; } = TimeSpan.FromDays(30);

    /// <summary>
    /// Required uptime percentage.
    /// </summary>
    public double RequiredUptime { get; init; } = 99.9;

    /// <summary>
    /// Whether the SLA is active.
    /// </summary>
    public bool Active { get; init; } = true;
}

/// <summary>
/// SLA compliance result.
/// </summary>
public sealed class SlaComplianceResult
{
    /// <summary>
    /// SLA being evaluated.
    /// </summary>
    public required QualitySla Sla { get; init; }

    /// <summary>
    /// Whether SLA is being met.
    /// </summary>
    public bool IsCompliant { get; init; }

    /// <summary>
    /// Current average score.
    /// </summary>
    public double CurrentScore { get; init; }

    /// <summary>
    /// Compliance percentage.
    /// </summary>
    public double CompliancePercentage { get; init; }

    /// <summary>
    /// Violations during the period.
    /// </summary>
    public int ViolationCount { get; init; }

    /// <summary>
    /// Evaluation period.
    /// </summary>
    public required DateRange EvaluationPeriod { get; init; }
}

/// <summary>
/// Date range.
/// </summary>
public sealed class DateRange
{
    public DateTime Start { get; init; }
    public DateTime End { get; init; }
}

#endregion

#region Real-Time Monitoring Strategy

/// <summary>
/// Real-time quality monitoring strategy.
/// </summary>
public sealed class RealTimeMonitoringStrategy : DataQualityStrategyBase
{
    private readonly BoundedDictionary<string, List<QualityMetric>> _metrics = new BoundedDictionary<string, List<QualityMetric>>(1000);
    private readonly BoundedDictionary<string, QualityThreshold> _thresholds = new BoundedDictionary<string, QualityThreshold>(1000);
    private readonly ConcurrentQueue<QualityAlert> _alerts = new();
    private readonly BoundedDictionary<string, QualitySla> _slas = new BoundedDictionary<string, QualitySla>(1000);
    private readonly int _maxMetricHistory = 1000;

    /// <inheritdoc/>
    public override string StrategyId => "realtime-monitoring";

    /// <inheritdoc/>
    public override string DisplayName => "Real-Time Quality Monitoring";

    /// <inheritdoc/>
    public override DataQualityCategory Category => DataQualityCategory.Monitoring;

    /// <inheritdoc/>
    public override DataQualityCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = true,
        SupportsDistributed = true,
        SupportsIncremental = true,
        MaxThroughput = 100000,
        TypicalLatencyMs = 0.1
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Provides real-time monitoring of data quality metrics with alerting, " +
        "trend analysis, and SLA compliance tracking.";

    /// <inheritdoc/>
    public override string[] Tags => new[]
    {
        "monitoring", "realtime", "alerts", "metrics", "sla", "trends"
    };

    /// <summary>
    /// Event raised when an alert is triggered.
    /// </summary>
    public event EventHandler<QualityAlert>? AlertRaised;

    /// <summary>
    /// Registers a threshold.
    /// </summary>
    public void RegisterThreshold(QualityThreshold threshold)
    {
        _thresholds[threshold.ThresholdId] = threshold;
    }

    /// <summary>
    /// Registers an SLA.
    /// </summary>
    public void RegisterSla(QualitySla sla)
    {
        _slas[sla.SlaId] = sla;
    }

    /// <summary>
    /// Records a metric measurement.
    /// </summary>
    public void RecordMetric(QualityMetric metric)
    {
        ThrowIfNotInitialized();

        var history = _metrics.GetOrAdd(metric.Name, _ => new List<QualityMetric>());
        lock (history)
        {
            history.Add(metric);
            // Trim history
            while (history.Count > _maxMetricHistory)
            {
                history.RemoveAt(0);
            }
        }

        // Check thresholds
        CheckThresholds(metric);
    }

    /// <summary>
    /// Records multiple metrics.
    /// </summary>
    public void RecordMetrics(IEnumerable<QualityMetric> metrics)
    {
        foreach (var metric in metrics)
        {
            RecordMetric(metric);
        }
    }

    /// <summary>
    /// Gets the current value of a metric.
    /// </summary>
    public QualityMetric? GetCurrentMetric(string metricName)
    {
        if (_metrics.TryGetValue(metricName, out var history) && history.Count > 0)
        {
            lock (history)
            {
                return history[^1];
            }
        }
        return null;
    }

    /// <summary>
    /// Gets metric history.
    /// </summary>
    public List<QualityMetric> GetMetricHistory(string metricName, TimeSpan? period = null)
    {
        if (!_metrics.TryGetValue(metricName, out var history))
            return new List<QualityMetric>();

        lock (history)
        {
            var result = history.ToList();
            if (period.HasValue)
            {
                var cutoff = DateTime.UtcNow - period.Value;
                result = result.Where(m => m.Timestamp >= cutoff).ToList();
            }
            return result;
        }
    }

    /// <summary>
    /// Gets all active alerts.
    /// </summary>
    public List<QualityAlert> GetActiveAlerts()
    {
        return _alerts.Where(a => !a.Acknowledged).ToList();
    }

    /// <summary>
    /// Acknowledges an alert.
    /// </summary>
    public bool AcknowledgeAlert(string alertId, string acknowledgedBy)
    {
        var alert = _alerts.FirstOrDefault(a => a.AlertId == alertId);
        if (alert != null)
        {
            alert.Acknowledged = true;
            alert.AcknowledgedAt = DateTime.UtcNow;
            alert.AcknowledgedBy = acknowledgedBy;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Calculates trend for a metric.
    /// </summary>
    public QualityTrend CalculateTrend(string metricName, TimeSpan period)
    {
        var history = GetMetricHistory(metricName, period);

        if (history.Count == 0)
        {
            return new QualityTrend
            {
                MetricName = metricName,
                Period = period,
                Direction = TrendDirection.Stable
            };
        }

        var values = history.Select(m => m.Value).ToList();
        var average = values.Average();
        var min = values.Min();
        var max = values.Max();

        // Calculate standard deviation
        var variance = values.Sum(v => Math.Pow(v - average, 2)) / values.Count;
        var stdDev = Math.Sqrt(variance);

        // Calculate percent change
        var firstValue = values.First();
        var lastValue = values.Last();
        var percentChange = firstValue != 0
            ? (lastValue - firstValue) / firstValue * 100
            : 0;

        // Determine trend direction
        var direction = TrendDirection.Stable;
        if (percentChange > 5)
            direction = TrendDirection.Improving;
        else if (percentChange < -5)
            direction = TrendDirection.Degrading;

        return new QualityTrend
        {
            MetricName = metricName,
            Period = period,
            DataPoints = history,
            Direction = direction,
            PercentChange = percentChange,
            Average = average,
            Min = min,
            Max = max,
            StdDev = stdDev
        };
    }

    /// <summary>
    /// Evaluates SLA compliance.
    /// </summary>
    public SlaComplianceResult EvaluateSlaCompliance(string slaId, string metricName)
    {
        if (!_slas.TryGetValue(slaId, out var sla))
        {
            throw new ArgumentException($"SLA '{slaId}' not found");
        }

        var history = GetMetricHistory(metricName, sla.EvaluationPeriod);
        var now = DateTime.UtcNow;

        if (history.Count == 0)
        {
            return new SlaComplianceResult
            {
                Sla = sla,
                IsCompliant = true,
                CurrentScore = 0,
                CompliancePercentage = 100,
                ViolationCount = 0,
                EvaluationPeriod = new DateRange
                {
                    Start = now - sla.EvaluationPeriod,
                    End = now
                }
            };
        }

        var averageScore = history.Average(m => m.Value);
        var violations = history.Count(m => m.Value < sla.MinScore);
        var compliancePercentage = (double)(history.Count - violations) / history.Count * 100;
        var isCompliant = averageScore >= sla.TargetScore && compliancePercentage >= sla.RequiredUptime;

        return new SlaComplianceResult
        {
            Sla = sla,
            IsCompliant = isCompliant,
            CurrentScore = averageScore,
            CompliancePercentage = compliancePercentage,
            ViolationCount = violations,
            EvaluationPeriod = new DateRange
            {
                Start = now - sla.EvaluationPeriod,
                End = now
            }
        };
    }

    /// <summary>
    /// Gets a summary of all metrics.
    /// </summary>
    public Dictionary<string, QualityMetricSummary> GetMetricsSummary()
    {
        var summary = new Dictionary<string, QualityMetricSummary>();

        foreach (var (name, history) in _metrics)
        {
            if (history.Count == 0) continue;

            lock (history)
            {
                var values = history.Select(m => m.Value).ToList();
                summary[name] = new QualityMetricSummary
                {
                    MetricName = name,
                    CurrentValue = values.Last(),
                    Average = values.Average(),
                    Min = values.Min(),
                    Max = values.Max(),
                    Count = values.Count,
                    LastUpdated = history.Last().Timestamp
                };
            }
        }

        return summary;
    }

    private void CheckThresholds(QualityMetric metric)
    {
        foreach (var threshold in _thresholds.Values.Where(t => t.Enabled && t.MetricName == metric.Name))
        {
            AlertSeverity? severity = null;
            string? message = null;

            if (threshold.MinValue.HasValue && metric.Value < threshold.MinValue.Value)
            {
                severity = AlertSeverity.Error;
                message = $"Metric '{metric.Name}' ({metric.Value:F2}) is below minimum threshold ({threshold.MinValue:F2})";
            }
            else if (threshold.MaxValue.HasValue && metric.Value > threshold.MaxValue.Value)
            {
                severity = AlertSeverity.Error;
                message = $"Metric '{metric.Name}' ({metric.Value:F2}) exceeds maximum threshold ({threshold.MaxValue:F2})";
            }
            else if (threshold.MinValue.HasValue)
            {
                var warningLevel = threshold.MinValue.Value / threshold.WarningThreshold;
                if (metric.Value < warningLevel)
                {
                    severity = AlertSeverity.Warning;
                    message = $"Metric '{metric.Name}' ({metric.Value:F2}) is approaching minimum threshold";
                }
            }

            if (severity.HasValue && message != null)
            {
                var alert = new QualityAlert
                {
                    Severity = severity.Value,
                    MetricName = metric.Name,
                    CurrentValue = metric.Value,
                    Threshold = threshold,
                    Message = message
                };

                _alerts.Enqueue(alert);
                AlertRaised?.Invoke(this, alert);
            }
        }
    }
}

/// <summary>
/// Summary of a quality metric.
/// </summary>
public sealed class QualityMetricSummary
{
    public required string MetricName { get; init; }
    public double CurrentValue { get; init; }
    public double Average { get; init; }
    public double Min { get; init; }
    public double Max { get; init; }
    public int Count { get; init; }
    public DateTime LastUpdated { get; init; }
}

#endregion

#region Anomaly Detection Strategy

/// <summary>
/// Anomaly detection for quality metrics.
/// </summary>
public sealed class AnomalyDetectionStrategy : DataQualityStrategyBase
{
    private readonly BoundedDictionary<string, AnomalyDetector> _detectors = new BoundedDictionary<string, AnomalyDetector>(1000);

    /// <inheritdoc/>
    public override string StrategyId => "anomaly-detection";

    /// <inheritdoc/>
    public override string DisplayName => "Quality Anomaly Detection";

    /// <inheritdoc/>
    public override DataQualityCategory Category => DataQualityCategory.Monitoring;

    /// <inheritdoc/>
    public override DataQualityCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = true,
        SupportsDistributed = true,
        SupportsIncremental = true,
        MaxThroughput = 50000,
        TypicalLatencyMs = 0.5
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Detects anomalies in quality metrics using statistical methods. " +
        "Identifies unusual patterns and sudden changes in data quality.";

    /// <inheritdoc/>
    public override string[] Tags => new[]
    {
        "monitoring", "anomaly", "detection", "statistical", "outliers"
    };

    /// <summary>
    /// Trains the detector on historical data.
    /// </summary>
    public void Train(string metricName, IEnumerable<double> values)
    {
        var valueList = values.ToList();
        if (valueList.Count < 10)
            throw new ArgumentException("Need at least 10 data points for training");

        var mean = valueList.Average();
        var variance = valueList.Sum(v => Math.Pow(v - mean, 2)) / valueList.Count;
        var stdDev = Math.Sqrt(variance);

        _detectors[metricName] = new AnomalyDetector
        {
            Mean = mean,
            StdDev = stdDev,
            Min = valueList.Min(),
            Max = valueList.Max(),
            TrainingSamples = valueList.Count
        };
    }

    /// <summary>
    /// Detects if a value is anomalous.
    /// </summary>
    public AnomalyResult DetectAnomaly(string metricName, double value)
    {
        if (!_detectors.TryGetValue(metricName, out var detector))
        {
            throw new InvalidOperationException($"No detector trained for metric '{metricName}'");
        }

        var zScore = detector.StdDev > 0
            ? Math.Abs((value - detector.Mean) / detector.StdDev)
            : 0;

        var isAnomaly = zScore > 3; // Beyond 3 standard deviations
        var severity = zScore switch
        {
            > 4 => AnomalySeverity.Critical,
            > 3.5 => AnomalySeverity.High,
            > 3 => AnomalySeverity.Medium,
            > 2.5 => AnomalySeverity.Low,
            _ => AnomalySeverity.None
        };

        return new AnomalyResult
        {
            MetricName = metricName,
            Value = value,
            IsAnomaly = isAnomaly,
            ZScore = zScore,
            Severity = severity,
            ExpectedRange = (detector.Mean - 2 * detector.StdDev, detector.Mean + 2 * detector.StdDev)
        };
    }
}

/// <summary>
/// Internal anomaly detector state.
/// </summary>
internal sealed class AnomalyDetector
{
    public double Mean { get; init; }
    public double StdDev { get; init; }
    public double Min { get; init; }
    public double Max { get; init; }
    public int TrainingSamples { get; init; }
}

/// <summary>
/// Result of anomaly detection.
/// </summary>
public sealed class AnomalyResult
{
    public required string MetricName { get; init; }
    public double Value { get; init; }
    public bool IsAnomaly { get; init; }
    public double ZScore { get; init; }
    public AnomalySeverity Severity { get; init; }
    public (double Low, double High) ExpectedRange { get; init; }
}

/// <summary>
/// Anomaly severity levels.
/// </summary>
public enum AnomalySeverity
{
    None,
    Low,
    Medium,
    High,
    Critical
}

#endregion
