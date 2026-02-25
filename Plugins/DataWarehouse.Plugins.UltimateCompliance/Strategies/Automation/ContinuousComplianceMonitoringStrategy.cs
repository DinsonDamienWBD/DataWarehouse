using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.Automation
{
    /// <summary>
    /// Continuous compliance monitoring strategy that provides real-time compliance
    /// status tracking, drift detection, and automated alerting.
    /// Monitors compliance posture 24/7 with configurable alerting thresholds.
    /// </summary>
    public sealed class ContinuousComplianceMonitoringStrategy : ComplianceStrategyBase
    {
        private readonly BoundedDictionary<string, ComplianceMetric> _metrics = new BoundedDictionary<string, ComplianceMetric>(1000);
        private readonly BoundedDictionary<string, ComplianceAlert> _alerts = new BoundedDictionary<string, ComplianceAlert>(1000);
        private readonly ConcurrentQueue<ComplianceSnapshot> _snapshots = new();
        private Timer? _monitoringTimer;
        private Timer? _snapshotTimer;
        private MonitoringConfiguration _config = new();

        /// <inheritdoc/>
        public override string StrategyId => "continuous-compliance-monitoring";

        /// <inheritdoc/>
        public override string StrategyName => "Continuous Compliance Monitoring";

        /// <inheritdoc/>
        public override string Framework => "Monitoring-Based";

        /// <inheritdoc/>
        public override async Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken cancellationToken = default)
        {
            await base.InitializeAsync(configuration, cancellationToken);

            // Load monitoring configuration
            if (configuration.TryGetValue("MonitoringConfig", out var configObj) && configObj is MonitoringConfiguration config)
            {
                _config = config;
            }
            else
            {
                _config = new MonitoringConfiguration
                {
                    MonitoringIntervalSeconds = 300, // 5 minutes
                    SnapshotIntervalSeconds = 3600, // 1 hour
                    AlertThresholds = new AlertThresholds
                    {
                        CriticalViolationCount = 1,
                        HighViolationCount = 3,
                        ComplianceScoreThreshold = 0.95,
                        DriftDetectionEnabled = true,
                        DriftThresholdPercent = 5.0
                    }
                };
            }

            // Start monitoring timer
            _monitoringTimer = new Timer(
                async _ => { try { await PerformMonitoringCycleAsync(cancellationToken); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Timer callback failed: {ex.Message}"); } },
                null,
                TimeSpan.FromSeconds(_config.MonitoringIntervalSeconds),
                TimeSpan.FromSeconds(_config.MonitoringIntervalSeconds)
            );

            // Start snapshot timer
            _snapshotTimer = new Timer(
                async _ => { try { await TakeComplianceSnapshotAsync(cancellationToken); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Timer callback failed: {ex.Message}"); } },
                null,
                TimeSpan.FromSeconds(_config.SnapshotIntervalSeconds),
                TimeSpan.FromSeconds(_config.SnapshotIntervalSeconds)
            );

            // Initialize metrics for known frameworks
            InitializeMetrics();

        }

        /// <inheritdoc/>
        protected override async Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("continuous_compliance_monitoring.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            // Update metrics based on current operation
            await UpdateMetricsAsync(context, cancellationToken);

            // Check for active alerts
            var activeAlerts = _alerts.Values.Where(a => !a.Acknowledged).ToList();
            if (activeAlerts.Any())
            {
                foreach (var alert in activeAlerts.Take(5)) // Limit to top 5
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = alert.AlertId,
                        Description = alert.Message,
                        Severity = MapSeverity(alert.Severity),
                        Remediation = alert.RecommendedAction,
                        AffectedResource = alert.Framework
                    });
                }

                if (activeAlerts.Count > 5)
                {
                    recommendations.Add($"{activeAlerts.Count - 5} additional alerts pending. Review monitoring dashboard.");
                }
            }

            // Calculate current compliance score
            var complianceScore = CalculateOverallComplianceScore();
            if (complianceScore < _config.AlertThresholds.ComplianceScoreThreshold)
            {
                recommendations.Add($"Compliance score ({complianceScore:P2}) below threshold ({_config.AlertThresholds.ComplianceScoreThreshold:P2})");
            }

            // Check for compliance drift
            if (_config.AlertThresholds.DriftDetectionEnabled)
            {
                var drift = DetectComplianceDrift();
                if (drift.HasValue && Math.Abs(drift.Value) > _config.AlertThresholds.DriftThresholdPercent)
                {
                    recommendations.Add($"Compliance drift detected: {drift.Value:F2}% change from baseline");
                }
            }

            var isCompliant = violations.Count == 0 && complianceScore >= _config.AlertThresholds.ComplianceScoreThreshold;
            var status = isCompliant ? ComplianceStatus.Compliant :
                        complianceScore < 0.7 ? ComplianceStatus.NonCompliant :
                        ComplianceStatus.PartiallyCompliant;

            return new ComplianceResult
            {
                IsCompliant = isCompliant,
                Framework = Framework,
                Status = status,
                Violations = violations,
                Recommendations = recommendations,
                Metadata = new Dictionary<string, object>
                {
                    ["ComplianceScore"] = complianceScore,
                    ["ActiveAlerts"] = activeAlerts.Count,
                    ["MonitoredFrameworks"] = _metrics.Keys.ToList(),
                    ["SnapshotCount"] = _snapshots.Count,
                    ["MonitoringIntervalSeconds"] = _config.MonitoringIntervalSeconds,
                    ["LastMonitoringCycle"] = DateTime.UtcNow,
                    ["TotalChecks"] = _metrics.Values.Sum(m => m.TotalChecks),
                    ["TotalViolations"] = _metrics.Values.Sum(m => m.ViolationCount)
                }
            };
        }

        private async Task UpdateMetricsAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            var frameworks = DetermineFrameworks(context);

            foreach (var framework in frameworks)
            {
                if (!_metrics.TryGetValue(framework, out var metric))
                {
                    metric = new ComplianceMetric
                    {
                        Framework = framework,
                        FirstCheckAt = DateTime.UtcNow
                    };
                    _metrics[framework] = metric;
                }

                metric.TotalChecks++;
                metric.LastCheckAt = DateTime.UtcNow;

                // Update compliance status based on context
                if (HasViolation(context))
                {
                    metric.ViolationCount++;
                    metric.LastViolationAt = DateTime.UtcNow;
                }
                else
                {
                    metric.CompliantCount++;
                }

                // Update compliance score
                metric.ComplianceScore = metric.TotalChecks > 0
                    ? (double)metric.CompliantCount / metric.TotalChecks
                    : 1.0;
            }

            await Task.CompletedTask;
        }

        private async Task PerformMonitoringCycleAsync(CancellationToken cancellationToken)
        {
            // Check all metrics against thresholds
            foreach (var (framework, metric) in _metrics)
            {
                // Check critical violations
                if (metric.ViolationCount >= _config.AlertThresholds.CriticalViolationCount &&
                    metric.LastViolationAt.HasValue &&
                    (DateTime.UtcNow - metric.LastViolationAt.Value).TotalMinutes < 60)
                {
                    await CreateAlertAsync(framework, AlertSeverity.Critical,
                        $"Critical: {metric.ViolationCount} violations detected in {framework}",
                        "Immediate review and remediation required");
                }

                // Check compliance score threshold
                if (metric.ComplianceScore < _config.AlertThresholds.ComplianceScoreThreshold)
                {
                    await CreateAlertAsync(framework, AlertSeverity.High,
                        $"{framework} compliance score ({metric.ComplianceScore:P2}) below threshold",
                        "Review recent violations and implement corrective actions");
                }

                // Check for stale monitoring
                if (metric.LastCheckAt.HasValue &&
                    (DateTime.UtcNow - metric.LastCheckAt.Value).TotalHours > 24)
                {
                    await CreateAlertAsync(framework, AlertSeverity.Medium,
                        $"{framework} monitoring inactive for 24+ hours",
                        "Verify monitoring systems are operational");
                }
            }

            // Clean up acknowledged alerts older than 7 days
            var cutoff = DateTime.UtcNow.AddDays(-7);
            var oldAlerts = _alerts.Where(kvp => kvp.Value.Acknowledged && kvp.Value.CreatedAt < cutoff)
                                   .Select(kvp => kvp.Key).ToList();
            foreach (var alertId in oldAlerts)
            {
                _alerts.TryRemove(alertId, out _);
            }

            await Task.CompletedTask;
        }

        private async Task TakeComplianceSnapshotAsync(CancellationToken cancellationToken)
        {
            var snapshot = new ComplianceSnapshot
            {
                SnapshotId = $"SNAP-{DateTime.UtcNow:yyyyMMdd-HHmmss}",
                Timestamp = DateTime.UtcNow,
                OverallComplianceScore = CalculateOverallComplianceScore(),
                FrameworkMetrics = new Dictionary<string, FrameworkSnapshot>()
            };

            foreach (var (framework, metric) in _metrics)
            {
                snapshot.FrameworkMetrics[framework] = new FrameworkSnapshot
                {
                    Framework = framework,
                    ComplianceScore = metric.ComplianceScore,
                    TotalChecks = metric.TotalChecks,
                    ViolationCount = metric.ViolationCount,
                    CompliantCount = metric.CompliantCount
                };
            }

            _snapshots.Enqueue(snapshot);

            // Keep only last 168 snapshots (1 week of hourly snapshots)
            while (_snapshots.Count > 168)
            {
                _snapshots.TryDequeue(out _);
            }

            await Task.CompletedTask;
        }

        private async Task CreateAlertAsync(string framework, AlertSeverity severity, string message, string recommendedAction)
        {
            var alertId = $"ALERT-{framework}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";

            var alert = new ComplianceAlert
            {
                AlertId = alertId,
                Framework = framework,
                Severity = severity,
                Message = message,
                RecommendedAction = recommendedAction,
                CreatedAt = DateTime.UtcNow,
                Acknowledged = false
            };

            _alerts[alertId] = alert;

            // In production, this would send notifications (email, SMS, webhook, etc.)
            await Task.CompletedTask;
        }

        private double CalculateOverallComplianceScore()
        {
            if (_metrics.IsEmpty)
                return 1.0;

            return _metrics.Values.Average(m => m.ComplianceScore);
        }

        private double? DetectComplianceDrift()
        {
            if (_snapshots.Count < 2)
                return null;

            var snapshots = _snapshots.ToArray();
            var current = snapshots.Last().OverallComplianceScore;
            var baseline = snapshots.First().OverallComplianceScore;

            if (baseline == 0)
                return null;

            return ((current - baseline) / baseline) * 100.0;
        }

        private void InitializeMetrics()
        {
            var frameworks = new[] { "GDPR", "HIPAA", "SOX", "PCI-DSS", "SOC2", "FedRAMP" };

            foreach (var framework in frameworks)
            {
                _metrics[framework] = new ComplianceMetric
                {
                    Framework = framework,
                    FirstCheckAt = DateTime.UtcNow,
                    ComplianceScore = 1.0
                };
            }
        }

        private List<string> DetermineFrameworks(ComplianceContext context)
        {
            var frameworks = new List<string>();

            if (context.DataClassification.Contains("personal", StringComparison.OrdinalIgnoreCase))
                frameworks.Add("GDPR");
            if (context.DataClassification.Contains("health", StringComparison.OrdinalIgnoreCase))
                frameworks.Add("HIPAA");
            if (context.DataClassification.Contains("financial", StringComparison.OrdinalIgnoreCase))
                frameworks.Add("SOX");
            if (context.DataClassification.Contains("card", StringComparison.OrdinalIgnoreCase))
                frameworks.Add("PCI-DSS");

            return frameworks;
        }

        private bool HasViolation(ComplianceContext context)
        {
            // Simple violation detection
            if (context.DataClassification.Contains("sensitive", StringComparison.OrdinalIgnoreCase) &&
                (!context.Attributes.TryGetValue("Encrypted", out var enc) || enc is not true))
                return true;

            return false;
        }

        private ViolationSeverity MapSeverity(AlertSeverity severity)
        {
            return severity switch
            {
                AlertSeverity.Critical => ViolationSeverity.Critical,
                AlertSeverity.High => ViolationSeverity.High,
                AlertSeverity.Medium => ViolationSeverity.Medium,
                AlertSeverity.Low => ViolationSeverity.Low,
                _ => ViolationSeverity.Medium
            };
        }

        private sealed class ComplianceMetric
        {
            public required string Framework { get; init; }
            public long TotalChecks { get; set; }
            public long ViolationCount { get; set; }
            public long CompliantCount { get; set; }
            public double ComplianceScore { get; set; } = 1.0;
            public DateTime FirstCheckAt { get; set; }
            public DateTime? LastCheckAt { get; set; }
            public DateTime? LastViolationAt { get; set; }
        }

        private sealed class ComplianceAlert
        {
            public required string AlertId { get; init; }
            public required string Framework { get; init; }
            public required AlertSeverity Severity { get; init; }
            public required string Message { get; init; }
            public required string RecommendedAction { get; init; }
            public required DateTime CreatedAt { get; init; }
            public bool Acknowledged { get; set; }
            public DateTime? AcknowledgedAt { get; set; }
            public string? AcknowledgedBy { get; set; }
        }

        private sealed class ComplianceSnapshot
        {
            public required string SnapshotId { get; init; }
            public required DateTime Timestamp { get; init; }
            public required double OverallComplianceScore { get; init; }
            public required Dictionary<string, FrameworkSnapshot> FrameworkMetrics { get; init; }
        }

        private sealed class FrameworkSnapshot
        {
            public required string Framework { get; init; }
            public required double ComplianceScore { get; init; }
            public required long TotalChecks { get; init; }
            public required long ViolationCount { get; init; }
            public required long CompliantCount { get; init; }
        }
    
    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("continuous_compliance_monitoring.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("continuous_compliance_monitoring.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}

    /// <summary>
    /// Monitoring configuration settings.
    /// </summary>
    public sealed class MonitoringConfiguration
    {
        public int MonitoringIntervalSeconds { get; init; } = 300;
        public int SnapshotIntervalSeconds { get; init; } = 3600;
        public AlertThresholds AlertThresholds { get; init; } = new();
    }

    /// <summary>
    /// Alert threshold configuration.
    /// </summary>
    public sealed class AlertThresholds
    {
        public int CriticalViolationCount { get; init; } = 1;
        public int HighViolationCount { get; init; } = 3;
        public double ComplianceScoreThreshold { get; init; } = 0.95;
        public bool DriftDetectionEnabled { get; init; } = true;
        public double DriftThresholdPercent { get; init; } = 5.0;
    }

    /// <summary>
    /// Alert severity levels.
    /// </summary>
    public enum AlertSeverity
    {
        Low,
        Medium,
        High,
        Critical
    }
}
