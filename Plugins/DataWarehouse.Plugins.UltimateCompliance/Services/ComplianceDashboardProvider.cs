using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateCompliance.Services
{
    /// <summary>
    /// Real-time compliance dashboard data provider. Aggregates compliance scores per framework,
    /// active violations, evidence collection status, and trending metrics. Publishes status updates
    /// via message bus topic "compliance.dashboard.update". Subscribes to "compliance.status.request"
    /// for on-demand dashboard queries.
    /// </summary>
    public sealed class ComplianceDashboardProvider
    {
        private readonly IReadOnlyCollection<IComplianceStrategy> _strategies;
        private readonly IMessageBus? _messageBus;
        private readonly string _pluginId;
        private readonly BoundedDictionary<string, FrameworkDashboardStatus> _frameworkStatuses = new BoundedDictionary<string, FrameworkDashboardStatus>(1000);
        private readonly ConcurrentQueue<ComplianceTrendPoint> _trendHistory = new();
        // _lastCheckTimestampUtcTicks is accessed via Interlocked for thread-safe read/write of DateTime.Ticks
        private long _lastCheckTimestampUtcTicks = DateTime.UtcNow.Ticks;
        private static readonly int MaxTrendPoints = 1000;

        public ComplianceDashboardProvider(
            IReadOnlyCollection<IComplianceStrategy> strategies,
            IMessageBus? messageBus,
            string pluginId)
        {
            _strategies = strategies ?? throw new ArgumentNullException(nameof(strategies));
            _messageBus = messageBus;
            _pluginId = pluginId;
        }

        /// <summary>
        /// Returns aggregated dashboard data including per-framework scores, violations,
        /// evidence collection status, and trending metrics.
        /// </summary>
        public async Task<ComplianceDashboardData> GetDashboardDataAsync(
            CancellationToken cancellationToken = default)
        {
            var frameworkStatuses = new List<FrameworkDashboardStatus>();
            var totalViolations = 0;
            var totalChecks = 0L;
            var totalCompliant = 0L;

            // Collect per-framework status from strategies
            var frameworkGroups = _strategies.GroupBy(s => s.Framework);
            foreach (var group in frameworkGroups)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var strategies = group.ToList();
                var frameworkViolations = 0L;
                var frameworkChecks = 0L;
                var frameworkCompliant = 0L;

                foreach (var strategy in strategies)
                {
                    var stats = strategy.GetStatistics();
                    frameworkViolations += stats.ViolationsFound;
                    frameworkChecks += stats.TotalChecks;
                    frameworkCompliant += stats.CompliantCount;
                }

                var complianceRate = frameworkChecks > 0
                    ? (double)frameworkCompliant / frameworkChecks * 100.0
                    : 100.0;

                var status = new FrameworkDashboardStatus
                {
                    Framework = group.Key,
                    ComplianceScore = complianceRate,
                    StrategyCount = strategies.Count,
                    TotalChecks = frameworkChecks,
                    CompliantChecks = frameworkCompliant,
                    ActiveViolations = (int)frameworkViolations,
                    Status = complianceRate >= 95.0 ? FrameworkHealthStatus.Healthy
                           : complianceRate >= 70.0 ? FrameworkHealthStatus.Warning
                           : FrameworkHealthStatus.Critical,
                    LastCheckedUtc = strategies.Max(s => s.GetStatistics().LastCheckTime) is var lastCheck && lastCheck != default
                        ? lastCheck
                        : DateTime.UtcNow
                };

                frameworkStatuses.Add(status);
                _frameworkStatuses[group.Key] = status;

                totalViolations += (int)frameworkViolations;
                totalChecks += frameworkChecks;
                totalCompliant += frameworkCompliant;
            }

            var overallScore = totalChecks > 0
                ? (double)totalCompliant / totalChecks * 100.0
                : 100.0;

            Interlocked.Exchange(ref _lastCheckTimestampUtcTicks, DateTime.UtcNow.Ticks);

            // Record trend point
            var trendPoint = new ComplianceTrendPoint
            {
                Timestamp = DateTime.UtcNow,
                OverallScore = overallScore,
                ActiveViolations = totalViolations,
                TotalChecks = totalChecks
            };
            _trendHistory.Enqueue(trendPoint);

            // Trim trend history
            while (_trendHistory.Count > MaxTrendPoints)
                _trendHistory.TryDequeue(out _);

            var dashboardData = new ComplianceDashboardData
            {
                GeneratedAtUtc = DateTime.UtcNow,
                OverallComplianceScore = overallScore,
                ActiveViolationsCount = totalViolations,
                TotalStrategies = _strategies.Count,
                TotalChecksPerformed = totalChecks,
                FrameworkStatuses = frameworkStatuses,
                EvidenceCollectionStatus = BuildEvidenceCollectionStatus(),
                TrendingMetrics = BuildTrendingMetrics(),
                IntegrityStatus = BuildIntegrityStatus(frameworkStatuses),
                LastCheckTimestamp = new DateTime(Interlocked.Read(ref _lastCheckTimestampUtcTicks), DateTimeKind.Utc)
            };

            // Publish dashboard update via message bus
            if (_messageBus != null)
            {
                await _messageBus.PublishAsync("compliance.dashboard.update", new PluginMessage
                {
                    Type = "compliance.dashboard.update",
                    Source = _pluginId,
                    Payload = new Dictionary<string, object>
                    {
                        ["overallScore"] = dashboardData.OverallComplianceScore,
                        ["activeViolations"] = dashboardData.ActiveViolationsCount,
                        ["totalStrategies"] = dashboardData.TotalStrategies,
                        ["frameworkCount"] = dashboardData.FrameworkStatuses.Count,
                        ["healthStatus"] = dashboardData.IntegrityStatus.OverallHealth.ToString(),
                        ["timestamp"] = dashboardData.GeneratedAtUtc.ToString("O")
                    }
                }, cancellationToken);
            }

            return dashboardData;
        }

        /// <summary>
        /// Returns the current integrity status without full dashboard refresh.
        /// </summary>
        public IntegrityStatusSummary GetCurrentIntegrityStatus()
        {
            var statuses = _frameworkStatuses.Values.ToList();
            return BuildIntegrityStatus(statuses);
        }

        private EvidenceCollectionStatus BuildEvidenceCollectionStatus()
        {
            var strategyStats = _strategies.Select(s => s.GetStatistics()).ToList();
            var activeStrategies = strategyStats.Count(s => s.TotalChecks > 0);
            var totalEvidence = strategyStats.Sum(s => s.TotalChecks);

            return new EvidenceCollectionStatus
            {
                TotalStrategies = _strategies.Count,
                ActiveStrategies = activeStrategies,
                InactiveStrategies = _strategies.Count - activeStrategies,
                TotalEvidenceItemsCollected = totalEvidence,
                CollectionRate = _strategies.Count > 0
                    ? (double)activeStrategies / _strategies.Count * 100.0
                    : 0.0,
                LastCollectionUtc = strategyStats
                    .Where(s => s.LastCheckTime != default)
                    .Select(s => s.LastCheckTime)
                    .DefaultIfEmpty(DateTime.UtcNow)
                    .Max()
            };
        }

        private TrendingMetrics BuildTrendingMetrics()
        {
            var points = _trendHistory.ToArray();

            if (points.Length < 2)
            {
                return new TrendingMetrics
                {
                    DataPointCount = points.Length,
                    ScoreTrend = TrendDirection.Stable,
                    ViolationTrend = TrendDirection.Stable,
                    AverageScore = points.Length > 0 ? points.Average(p => p.OverallScore) : 100.0,
                    MinScore = points.Length > 0 ? points.Min(p => p.OverallScore) : 100.0,
                    MaxScore = points.Length > 0 ? points.Max(p => p.OverallScore) : 100.0
                };
            }

            var recentHalf = points.Skip(points.Length / 2).ToArray();
            var olderHalf = points.Take(points.Length / 2).ToArray();

            var recentAvgScore = recentHalf.Average(p => p.OverallScore);
            var olderAvgScore = olderHalf.Average(p => p.OverallScore);

            var recentAvgViolations = recentHalf.Average(p => p.ActiveViolations);
            var olderAvgViolations = olderHalf.Average(p => p.ActiveViolations);

            var scoreDelta = recentAvgScore - olderAvgScore;
            var violationDelta = recentAvgViolations - olderAvgViolations;

            return new TrendingMetrics
            {
                DataPointCount = points.Length,
                ScoreTrend = scoreDelta > 1.0 ? TrendDirection.Improving
                           : scoreDelta < -1.0 ? TrendDirection.Declining
                           : TrendDirection.Stable,
                ViolationTrend = violationDelta < -0.5 ? TrendDirection.Improving
                               : violationDelta > 0.5 ? TrendDirection.Declining
                               : TrendDirection.Stable,
                AverageScore = points.Average(p => p.OverallScore),
                MinScore = points.Min(p => p.OverallScore),
                MaxScore = points.Max(p => p.OverallScore)
            };
        }

        private IntegrityStatusSummary BuildIntegrityStatus(List<FrameworkDashboardStatus> statuses)
        {
            var overallHealth = statuses.Count == 0 ? OverallHealthStatus.Unknown
                : statuses.All(s => s.Status == FrameworkHealthStatus.Healthy) ? OverallHealthStatus.Healthy
                : statuses.Any(s => s.Status == FrameworkHealthStatus.Critical) ? OverallHealthStatus.Critical
                : OverallHealthStatus.Degraded;

            return new IntegrityStatusSummary
            {
                OverallHealth = overallHealth,
                FrameworkHealthMap = statuses.ToDictionary(s => s.Framework, s => s.Status),
                LastCheckUtc = new DateTime(Interlocked.Read(ref _lastCheckTimestampUtcTicks), DateTimeKind.Utc),
                HealthyFrameworks = statuses.Count(s => s.Status == FrameworkHealthStatus.Healthy),
                WarningFrameworks = statuses.Count(s => s.Status == FrameworkHealthStatus.Warning),
                CriticalFrameworks = statuses.Count(s => s.Status == FrameworkHealthStatus.Critical)
            };
        }
    }

    #region Dashboard Types

    /// <summary>
    /// Complete dashboard data for compliance status visualization.
    /// </summary>
    public sealed class ComplianceDashboardData
    {
        public required DateTime GeneratedAtUtc { get; init; }
        public required double OverallComplianceScore { get; init; }
        public required int ActiveViolationsCount { get; init; }
        public required int TotalStrategies { get; init; }
        public required long TotalChecksPerformed { get; init; }
        public required List<FrameworkDashboardStatus> FrameworkStatuses { get; init; }
        public required EvidenceCollectionStatus EvidenceCollectionStatus { get; init; }
        public required TrendingMetrics TrendingMetrics { get; init; }
        public required IntegrityStatusSummary IntegrityStatus { get; init; }
        public required DateTime LastCheckTimestamp { get; init; }
    }

    /// <summary>
    /// Per-framework compliance status for dashboard display.
    /// </summary>
    public sealed class FrameworkDashboardStatus
    {
        public required string Framework { get; init; }
        public required double ComplianceScore { get; init; }
        public required int StrategyCount { get; init; }
        public required long TotalChecks { get; init; }
        public required long CompliantChecks { get; init; }
        public required int ActiveViolations { get; init; }
        public required FrameworkHealthStatus Status { get; init; }
        public required DateTime LastCheckedUtc { get; init; }
    }

    /// <summary>
    /// Evidence collection progress status.
    /// </summary>
    public sealed class EvidenceCollectionStatus
    {
        public required int TotalStrategies { get; init; }
        public required int ActiveStrategies { get; init; }
        public required int InactiveStrategies { get; init; }
        public required long TotalEvidenceItemsCollected { get; init; }
        public required double CollectionRate { get; init; }
        public required DateTime LastCollectionUtc { get; init; }
    }

    /// <summary>
    /// Trending compliance metrics over time.
    /// </summary>
    public sealed class TrendingMetrics
    {
        public required int DataPointCount { get; init; }
        public required TrendDirection ScoreTrend { get; init; }
        public required TrendDirection ViolationTrend { get; init; }
        public required double AverageScore { get; init; }
        public required double MinScore { get; init; }
        public required double MaxScore { get; init; }
    }

    /// <summary>
    /// Overall system integrity status.
    /// </summary>
    public sealed class IntegrityStatusSummary
    {
        public required OverallHealthStatus OverallHealth { get; init; }
        public required Dictionary<string, FrameworkHealthStatus> FrameworkHealthMap { get; init; }
        public required DateTime LastCheckUtc { get; init; }
        public required int HealthyFrameworks { get; init; }
        public required int WarningFrameworks { get; init; }
        public required int CriticalFrameworks { get; init; }
    }

    /// <summary>
    /// Trend point for historical tracking.
    /// </summary>
    public sealed class ComplianceTrendPoint
    {
        public required DateTime Timestamp { get; init; }
        public required double OverallScore { get; init; }
        public required int ActiveViolations { get; init; }
        public required long TotalChecks { get; init; }
    }

    public enum FrameworkHealthStatus { Healthy, Warning, Critical }
    public enum OverallHealthStatus { Healthy, Degraded, Critical, Unknown }
    public enum TrendDirection { Improving, Stable, Declining }

    #endregion
}
