using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateReplication.Features
{
    /// <summary>
    /// Replication Lag Monitoring Feature (C6).
    /// Provides real-time lag monitoring across replication nodes, publishes
    /// "replication.lag.alert" when configurable thresholds are exceeded,
    /// and tracks historical lag trends.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Alert levels:
    /// </para>
    /// <list type="bullet">
    ///   <item><b>Warning</b>: Lag exceeds warning threshold (default: 1000ms)</item>
    ///   <item><b>Critical</b>: Lag exceeds critical threshold (default: 5000ms)</item>
    ///   <item><b>Emergency</b>: Lag exceeds emergency threshold (default: 30000ms)</item>
    /// </list>
    /// <para>
    /// Publishes alerts to "replication.ultimate.lag.alert" topic with node details,
    /// current lag, trend direction, and recommendations.
    /// </para>
    /// </remarks>
    public sealed class ReplicationLagMonitoringFeature : IDisposable
    {
        private readonly ReplicationStrategyRegistry _registry;
        private readonly IMessageBus _messageBus;
        private readonly BoundedDictionary<string, LagNodeStatus> _nodeStatus = new BoundedDictionary<string, LagNodeStatus>(1000);
        private readonly BoundedDictionary<string, List<LagSample>> _lagHistory = new BoundedDictionary<string, List<LagSample>>(1000);
        private readonly int _maxHistorySamples;
        private bool _disposed;
        private IDisposable? _lagSubscription;

        // Thresholds
        private readonly long _warningThresholdMs;
        private readonly long _criticalThresholdMs;
        private readonly long _emergencyThresholdMs;

        // Topics
        private const string LagMeasurementTopic = "replication.ultimate.lag";
        private const string LagAlertTopic = "replication.ultimate.lag.alert";
        private const string LagStatusTopic = "replication.ultimate.lag.status";

        // Statistics
        private long _totalMeasurements;
        private long _warningAlerts;
        private long _criticalAlerts;
        private long _emergencyAlerts;

        /// <summary>
        /// Initializes a new instance of the ReplicationLagMonitoringFeature.
        /// </summary>
        /// <param name="registry">The replication strategy registry.</param>
        /// <param name="messageBus">Message bus for communication.</param>
        /// <param name="warningThresholdMs">Warning alert threshold in ms. Default: 1000.</param>
        /// <param name="criticalThresholdMs">Critical alert threshold in ms. Default: 5000.</param>
        /// <param name="emergencyThresholdMs">Emergency alert threshold in ms. Default: 30000.</param>
        /// <param name="maxHistorySamples">Max history samples per node. Default: 1000.</param>
        public ReplicationLagMonitoringFeature(
            ReplicationStrategyRegistry registry,
            IMessageBus messageBus,
            long warningThresholdMs = 1000,
            long criticalThresholdMs = 5000,
            long emergencyThresholdMs = 30000,
            int maxHistorySamples = 1000)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
            _warningThresholdMs = warningThresholdMs;
            _criticalThresholdMs = criticalThresholdMs;
            _emergencyThresholdMs = emergencyThresholdMs;
            _maxHistorySamples = maxHistorySamples;

            _lagSubscription = _messageBus.Subscribe(LagMeasurementTopic, HandleLagMeasurementAsync);
        }

        /// <summary>Gets total lag measurements recorded.</summary>
        public long TotalMeasurements => Interlocked.Read(ref _totalMeasurements);

        /// <summary>Gets total warning alerts published.</summary>
        public long WarningAlerts => Interlocked.Read(ref _warningAlerts);

        /// <summary>Gets total critical alerts published.</summary>
        public long CriticalAlerts => Interlocked.Read(ref _criticalAlerts);

        /// <summary>
        /// Records a lag measurement for a source-target node pair.
        /// Evaluates thresholds and publishes alerts if exceeded.
        /// </summary>
        /// <param name="sourceNode">Source node ID.</param>
        /// <param name="targetNode">Target node ID.</param>
        /// <param name="lagMs">Current replication lag in milliseconds.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Alert level if threshold exceeded, otherwise None.</returns>
        public async Task<LagAlertLevel> RecordLagMeasurementAsync(
            string sourceNode,
            string targetNode,
            long lagMs,
            CancellationToken ct = default)
        {
            Interlocked.Increment(ref _totalMeasurements);

            var nodeKey = $"{sourceNode}->{targetNode}";
            var sample = new LagSample
            {
                LagMs = lagMs,
                SampledAt = DateTimeOffset.UtcNow
            };

            // Update node status
            var status = _nodeStatus.GetOrAdd(nodeKey, _ => new LagNodeStatus
            {
                NodeKey = nodeKey,
                SourceNode = sourceNode,
                TargetNode = targetNode
            });

            lock (status.SyncLock)
            {
                status.CurrentLagMs = lagMs;
                status.LastUpdated = DateTimeOffset.UtcNow;
                status.MaxLagMs = Math.Max(status.MaxLagMs, lagMs);
                status.MinLagMs = Math.Min(status.MinLagMs, lagMs);
                status.SampleCount++;
                status.AverageLagMs = (status.AverageLagMs * (status.SampleCount - 1) + lagMs) / status.SampleCount;
            }

            // Record history
            var history = _lagHistory.GetOrAdd(nodeKey, _ => new List<LagSample>());
            lock (history)
            {
                history.Add(sample);
                if (history.Count > _maxHistorySamples)
                    history.RemoveAt(0);
            }

            // Evaluate alert thresholds
            var alertLevel = EvaluateAlertLevel(lagMs);
            status.CurrentAlertLevel = alertLevel;

            if (alertLevel != LagAlertLevel.None)
            {
                var trend = ComputeTrend(history);
                await PublishAlertAsync(status, alertLevel, trend, ct);
            }

            return alertLevel;
        }

        /// <summary>
        /// Gets the current lag status for all monitored node pairs.
        /// </summary>
        public IReadOnlyDictionary<string, LagNodeStatus> GetAllNodeStatus()
        {
            return _nodeStatus;
        }

        /// <summary>
        /// Gets the lag status for a specific node pair.
        /// </summary>
        public LagNodeStatus? GetNodeStatus(string sourceNode, string targetNode)
        {
            var key = $"{sourceNode}->{targetNode}";
            return _nodeStatus.GetValueOrDefault(key);
        }

        /// <summary>
        /// Gets nodes currently exceeding the warning threshold.
        /// </summary>
        public IReadOnlyList<LagNodeStatus> GetNodesInAlert()
        {
            return _nodeStatus.Values
                .Where(s => s.CurrentAlertLevel != LagAlertLevel.None)
                .OrderByDescending(s => s.CurrentLagMs)
                .ToList();
        }

        /// <summary>
        /// Gets the lag history for a specific node pair.
        /// </summary>
        public IReadOnlyList<LagSample> GetLagHistory(string sourceNode, string targetNode)
        {
            var key = $"{sourceNode}->{targetNode}";
            if (_lagHistory.TryGetValue(key, out var history))
            {
                lock (history)
                {
                    return history.ToList();
                }
            }
            return Array.Empty<LagSample>();
        }

        #region Private Methods

        private LagAlertLevel EvaluateAlertLevel(long lagMs)
        {
            if (lagMs >= _emergencyThresholdMs)
                return LagAlertLevel.Emergency;
            if (lagMs >= _criticalThresholdMs)
                return LagAlertLevel.Critical;
            if (lagMs >= _warningThresholdMs)
                return LagAlertLevel.Warning;
            return LagAlertLevel.None;
        }

        private static LagTrend ComputeTrend(List<LagSample> history)
        {
            if (history.Count < 3) return LagTrend.Stable;

            lock (history)
            {
                var recent = history.TakeLast(5).ToList();
                if (recent.Count < 2) return LagTrend.Stable;

                var first = recent.First().LagMs;
                var last = recent.Last().LagMs;
                var delta = last - first;
                var percentChange = first > 0 ? (double)delta / first : 0;

                if (percentChange > 0.1) return LagTrend.Increasing;
                if (percentChange < -0.1) return LagTrend.Decreasing;
                return LagTrend.Stable;
            }
        }

        private async Task PublishAlertAsync(
            LagNodeStatus status,
            LagAlertLevel level,
            LagTrend trend,
            CancellationToken ct)
        {
            switch (level)
            {
                case LagAlertLevel.Warning: Interlocked.Increment(ref _warningAlerts); break;
                case LagAlertLevel.Critical: Interlocked.Increment(ref _criticalAlerts); break;
                case LagAlertLevel.Emergency: Interlocked.Increment(ref _emergencyAlerts); break;
            }

            var recommendation = level switch
            {
                LagAlertLevel.Warning => "Monitor closely. Consider switching to a lower-latency strategy.",
                LagAlertLevel.Critical => "Investigate immediately. Data consistency may be affected.",
                LagAlertLevel.Emergency => "Emergency: replication significantly behind. Consider failover or strategy change.",
                _ => ""
            };

            await _messageBus.PublishAsync(LagAlertTopic, new PluginMessage
            {
                Type = LagAlertTopic,
                Source = "replication.ultimate.lag-monitor",
                Payload = new Dictionary<string, object>
                {
                    ["sourceNode"] = status.SourceNode,
                    ["targetNode"] = status.TargetNode,
                    ["currentLagMs"] = status.CurrentLagMs,
                    ["averageLagMs"] = status.AverageLagMs,
                    ["maxLagMs"] = status.MaxLagMs,
                    ["alertLevel"] = level.ToString(),
                    ["trend"] = trend.ToString(),
                    ["recommendation"] = recommendation,
                    ["timestamp"] = DateTimeOffset.UtcNow
                }
            }, ct);
        }

        private async Task HandleLagMeasurementAsync(PluginMessage message)
        {
            var sourceNode = message.Payload.GetValueOrDefault("sourceNode")?.ToString();
            var targetNode = message.Payload.GetValueOrDefault("targetNode")?.ToString();
            var lagMsStr = message.Payload.GetValueOrDefault("lagMs")?.ToString();

            if (!string.IsNullOrEmpty(sourceNode) && !string.IsNullOrEmpty(targetNode) &&
                long.TryParse(lagMsStr, out var lagMs))
            {
                await RecordLagMeasurementAsync(sourceNode, targetNode, lagMs);
            }
        }

        #endregion

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _lagSubscription?.Dispose();
        }
    }

    #region Lag Monitoring Types

    /// <summary>
    /// Alert severity levels for lag monitoring.
    /// </summary>
    public enum LagAlertLevel
    {
        /// <summary>No alert - lag within normal range.</summary>
        None,
        /// <summary>Warning: lag exceeds warning threshold.</summary>
        Warning,
        /// <summary>Critical: lag exceeds critical threshold.</summary>
        Critical,
        /// <summary>Emergency: lag exceeds emergency threshold.</summary>
        Emergency
    }

    /// <summary>
    /// Lag trend direction.
    /// </summary>
    public enum LagTrend
    {
        /// <summary>Lag is stable.</summary>
        Stable,
        /// <summary>Lag is increasing.</summary>
        Increasing,
        /// <summary>Lag is decreasing.</summary>
        Decreasing
    }

    /// <summary>
    /// Lag status for a monitored node pair.
    /// </summary>
    public sealed class LagNodeStatus
    {
        // Internal lock to protect concurrent stat updates from multiple monitoring threads.
        internal readonly object SyncLock = new();

        /// <summary>Node pair key (source->target).</summary>
        public required string NodeKey { get; init; }
        /// <summary>Source node ID.</summary>
        public required string SourceNode { get; init; }
        /// <summary>Target node ID.</summary>
        public required string TargetNode { get; init; }
        /// <summary>Current lag in milliseconds.</summary>
        public long CurrentLagMs { get; set; }
        /// <summary>Maximum observed lag.</summary>
        public long MaxLagMs { get; set; }
        /// <summary>Minimum observed lag.</summary>
        public long MinLagMs { get; set; } = long.MaxValue;
        /// <summary>Average lag across all samples.</summary>
        public double AverageLagMs { get; set; }
        /// <summary>Total measurement samples.</summary>
        public long SampleCount { get; set; }
        /// <summary>Current alert level.</summary>
        public LagAlertLevel CurrentAlertLevel { get; set; }
        /// <summary>When last updated.</summary>
        public DateTimeOffset LastUpdated { get; set; }
    }

    /// <summary>
    /// Single lag measurement sample.
    /// </summary>
    public sealed class LagSample
    {
        /// <summary>Lag in milliseconds.</summary>
        public required long LagMs { get; init; }
        /// <summary>When this sample was taken.</summary>
        public required DateTimeOffset SampledAt { get; init; }
    }

    #endregion
}
