using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateCompliance.Features
{
    /// <summary>
    /// Monitors compliance state in real-time using periodic assessment scheduling,
    /// drift detection, and alerting.
    /// </summary>
    public sealed class ContinuousComplianceMonitor
    {
        private readonly BoundedDictionary<string, ComplianceSnapshot> _snapshots = new BoundedDictionary<string, ComplianceSnapshot>(1000);
        private readonly ConcurrentQueue<ComplianceAlert> _alerts = new();
        private Timer? _monitorTimer;
        private bool _isMonitoring;

        /// <summary>
        /// Gets or sets the monitoring interval in seconds.
        /// </summary>
        public int MonitoringIntervalSeconds { get; set; } = 300;

        /// <summary>
        /// Gets or sets the drift threshold percentage for triggering alerts.
        /// </summary>
        public double DriftThresholdPercent { get; set; } = 5.0;

        /// <summary>
        /// Starts continuous monitoring of compliance state.
        /// </summary>
        public void StartMonitoring(IReadOnlyList<IComplianceStrategy> strategies)
        {
            if (_isMonitoring)
                return;

            _isMonitoring = true;
            _monitorTimer = new Timer(
                _ => PerformMonitoringCycle(strategies),
                null,
                TimeSpan.Zero,
                TimeSpan.FromSeconds(MonitoringIntervalSeconds));
        }

        /// <summary>
        /// Stops continuous monitoring.
        /// </summary>
        public void StopMonitoring()
        {
            _isMonitoring = false;
            _monitorTimer?.Dispose();
            _monitorTimer = null;
        }

        /// <summary>
        /// Performs a single monitoring cycle across all strategies.
        /// </summary>
        private void PerformMonitoringCycle(IReadOnlyList<IComplianceStrategy> strategies)
        {
            if (!_isMonitoring)
                return;

            var timestamp = DateTime.UtcNow;

            foreach (var strategy in strategies)
            {
                var stats = strategy.GetStatistics();
                var snapshot = new ComplianceSnapshot
                {
                    StrategyId = strategy.StrategyId,
                    Timestamp = timestamp,
                    TotalChecks = stats.TotalChecks,
                    CompliantCount = stats.CompliantCount,
                    NonCompliantCount = stats.NonCompliantCount,
                    ViolationsFound = stats.ViolationsFound
                };

                if (_snapshots.TryGetValue(strategy.StrategyId, out var previousSnapshot))
                {
                    DetectDrift(previousSnapshot, snapshot);
                }

                _snapshots[strategy.StrategyId] = snapshot;
            }
        }

        /// <summary>
        /// Detects compliance drift between snapshots.
        /// </summary>
        private void DetectDrift(ComplianceSnapshot previous, ComplianceSnapshot current)
        {
            var previousCompliance = CalculateComplianceRate(previous);
            var currentCompliance = CalculateComplianceRate(current);

            var drift = Math.Abs(previousCompliance - currentCompliance);

            if (drift >= DriftThresholdPercent)
            {
                var alert = new ComplianceAlert
                {
                    StrategyId = current.StrategyId,
                    AlertType = currentCompliance < previousCompliance ? AlertType.ComplianceDecreased : AlertType.ComplianceIncreased,
                    Message = $"Compliance rate changed from {previousCompliance:F2}% to {currentCompliance:F2}% (drift: {drift:F2}%)",
                    Timestamp = current.Timestamp,
                    Severity = drift >= 20 ? AlertSeverity.Critical : drift >= 10 ? AlertSeverity.High : AlertSeverity.Medium
                };

                _alerts.Enqueue(alert);
            }

            var violationIncrease = current.ViolationsFound - previous.ViolationsFound;
            if (violationIncrease > 0)
            {
                var violationAlert = new ComplianceAlert
                {
                    StrategyId = current.StrategyId,
                    AlertType = AlertType.ViolationIncreased,
                    Message = $"New violations detected: {violationIncrease} since last check",
                    Timestamp = current.Timestamp,
                    Severity = violationIncrease >= 10 ? AlertSeverity.High : AlertSeverity.Medium
                };

                _alerts.Enqueue(violationAlert);
            }
        }

        /// <summary>
        /// Calculates compliance rate as a percentage.
        /// </summary>
        private static double CalculateComplianceRate(ComplianceSnapshot snapshot)
        {
            if (snapshot.TotalChecks == 0)
                return 100.0;

            return (double)snapshot.CompliantCount / snapshot.TotalChecks * 100.0;
        }

        /// <summary>
        /// Gets all pending alerts.
        /// </summary>
        public IReadOnlyList<ComplianceAlert> GetPendingAlerts()
        {
            var alerts = new List<ComplianceAlert>();
            while (_alerts.TryDequeue(out var alert))
            {
                alerts.Add(alert);
            }
            return alerts;
        }

        /// <summary>
        /// Gets the current compliance snapshot for a strategy.
        /// </summary>
        public ComplianceSnapshot? GetSnapshot(string strategyId)
        {
            _snapshots.TryGetValue(strategyId, out var snapshot);
            return snapshot;
        }
    }

    /// <summary>
    /// Represents a point-in-time compliance state snapshot.
    /// </summary>
    public sealed class ComplianceSnapshot
    {
        public required string StrategyId { get; init; }
        public required DateTime Timestamp { get; init; }
        public required long TotalChecks { get; init; }
        public required long CompliantCount { get; init; }
        public required long NonCompliantCount { get; init; }
        public required long ViolationsFound { get; init; }
    }

    /// <summary>
    /// Represents a compliance monitoring alert.
    /// </summary>
    public sealed class ComplianceAlert
    {
        public required string StrategyId { get; init; }
        public required AlertType AlertType { get; init; }
        public required string Message { get; init; }
        public required DateTime Timestamp { get; init; }
        public required AlertSeverity Severity { get; init; }
    }

    /// <summary>
    /// Alert type classification.
    /// </summary>
    public enum AlertType
    {
        ComplianceDecreased,
        ComplianceIncreased,
        ViolationIncreased,
        DriftDetected
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
