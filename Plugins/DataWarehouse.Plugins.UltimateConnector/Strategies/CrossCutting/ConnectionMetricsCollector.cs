using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.CrossCutting
{
    /// <summary>
    /// Collects per-strategy connection metrics including latency percentiles, throughput,
    /// error rates, and connection counts. Publishes aggregated metrics snapshots to the
    /// message bus under "connector.metrics.*" topics for dashboard integration.
    /// </summary>
    /// <remarks>
    /// Metrics are collected in lock-free histograms and counters using interlocked operations.
    /// A background timer periodically computes aggregates and publishes them. The collector
    /// maintains a sliding window of recent latency samples for percentile computation.
    /// </remarks>
    public sealed class ConnectionMetricsCollector : IAsyncDisposable
    {
        private readonly BoundedDictionary<string, StrategyMetrics> _metrics = new BoundedDictionary<string, StrategyMetrics>(1000);
        private readonly IMessageBus? _messageBus;
        private readonly ILogger? _logger;
        private readonly Timer _publishTimer;
        private volatile bool _disposed;

        /// <summary>
        /// Base topic prefix for all connector metrics events.
        /// </summary>
        public const string MetricsTopicPrefix = "connector.metrics";

        /// <summary>
        /// Initializes a new instance of <see cref="ConnectionMetricsCollector"/>.
        /// </summary>
        /// <param name="messageBus">Message bus for publishing metrics snapshots. May be null.</param>
        /// <param name="logger">Optional logger for diagnostics.</param>
        /// <param name="publishInterval">Interval between metrics publications. Defaults to 15 seconds.</param>
        public ConnectionMetricsCollector(
            IMessageBus? messageBus = null,
            ILogger? logger = null,
            TimeSpan? publishInterval = null)
        {
            _messageBus = messageBus;
            _logger = logger;

            var interval = publishInterval ?? TimeSpan.FromSeconds(15);
            _publishTimer = new Timer(_ => _ = PublishMetricsAsync(), null, interval, interval);
        }

        /// <summary>
        /// Records a connection attempt for a strategy.
        /// </summary>
        /// <param name="strategyId">Strategy identifier.</param>
        /// <param name="latency">Connection latency.</param>
        /// <param name="success">Whether the connection succeeded.</param>
        public void RecordConnection(string strategyId, TimeSpan latency, bool success)
        {
            var metrics = GetOrCreateMetrics(strategyId);

            Interlocked.Increment(ref metrics.TotalConnections);
            if (success)
            {
                Interlocked.Increment(ref metrics.SuccessfulConnections);
                metrics.LatencySamples.Enqueue(latency.TotalMilliseconds);
                TrimSamples(metrics.LatencySamples, metrics.MaxSamples);
            }
            else
            {
                Interlocked.Increment(ref metrics.FailedConnections);
            }
        }

        /// <summary>
        /// Records bytes transferred for a strategy.
        /// </summary>
        /// <param name="strategyId">Strategy identifier.</param>
        /// <param name="bytesSent">Bytes sent in this transfer.</param>
        /// <param name="bytesReceived">Bytes received in this transfer.</param>
        public void RecordTransfer(string strategyId, long bytesSent, long bytesReceived)
        {
            var metrics = GetOrCreateMetrics(strategyId);
            Interlocked.Add(ref metrics.BytesSent, bytesSent);
            Interlocked.Add(ref metrics.BytesReceived, bytesReceived);
            Interlocked.Increment(ref metrics.TransferCount);
        }

        /// <summary>
        /// Records a connection error for a strategy.
        /// </summary>
        /// <param name="strategyId">Strategy identifier.</param>
        /// <param name="errorType">Classification of the error.</param>
        public void RecordError(string strategyId, string errorType)
        {
            var metrics = GetOrCreateMetrics(strategyId);
            Interlocked.Increment(ref metrics.ErrorCount);
            metrics.ErrorsByType.AddOrUpdate(errorType, 1, (_, count) => count + 1);
        }

        /// <summary>
        /// Records the current active connection count for a strategy.
        /// </summary>
        /// <param name="strategyId">Strategy identifier.</param>
        /// <param name="activeCount">Current active connection count.</param>
        public void SetActiveConnections(string strategyId, int activeCount)
        {
            var metrics = GetOrCreateMetrics(strategyId);
            Interlocked.Exchange(ref metrics.ActiveConnections, activeCount);
        }

        /// <summary>
        /// Returns a metrics snapshot for a specific strategy.
        /// </summary>
        /// <param name="strategyId">Strategy identifier.</param>
        /// <returns>Metrics snapshot, or null if the strategy has no recorded metrics.</returns>
        public MetricsSnapshot? GetSnapshot(string strategyId)
        {
            if (!_metrics.TryGetValue(strategyId, out var metrics))
                return null;

            return BuildSnapshot(strategyId, metrics);
        }

        /// <summary>
        /// Returns metrics snapshots for all tracked strategies.
        /// </summary>
        /// <returns>Dictionary mapping strategy ID to metrics snapshot.</returns>
        public Dictionary<string, MetricsSnapshot> GetAllSnapshots()
        {
            var result = new Dictionary<string, MetricsSnapshot>();
            foreach (var (strategyId, metrics) in _metrics)
                result[strategyId] = BuildSnapshot(strategyId, metrics);
            return result;
        }

        /// <summary>
        /// Publishes current metrics snapshots to the message bus.
        /// </summary>
        public async Task PublishMetricsAsync()
        {
            if (_messageBus == null || _disposed) return;

            foreach (var (strategyId, metrics) in _metrics)
            {
                try
                {
                    var snapshot = BuildSnapshot(strategyId, metrics);
                    var topic = $"{MetricsTopicPrefix}.{strategyId}";

                    var payload = new Dictionary<string, object>
                    {
                        ["strategy_id"] = strategyId,
                        ["total_connections"] = snapshot.TotalConnections,
                        ["successful_connections"] = snapshot.SuccessfulConnections,
                        ["failed_connections"] = snapshot.FailedConnections,
                        ["active_connections"] = snapshot.ActiveConnections,
                        ["error_count"] = snapshot.ErrorCount,
                        ["bytes_sent"] = snapshot.BytesSent,
                        ["bytes_received"] = snapshot.BytesReceived,
                        ["avg_latency_ms"] = snapshot.AverageLatencyMs,
                        ["p50_latency_ms"] = snapshot.P50LatencyMs,
                        ["p95_latency_ms"] = snapshot.P95LatencyMs,
                        ["p99_latency_ms"] = snapshot.P99LatencyMs,
                        ["timestamp"] = snapshot.Timestamp.ToString("O")
                    };

                    var message = PluginMessage.Create(topic, payload);
                    await _messageBus.PublishAsync(topic, message);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex,
                        "Failed to publish metrics for strategy {StrategyId}", strategyId);
                }
            }
        }

        private StrategyMetrics GetOrCreateMetrics(string strategyId) =>
            _metrics.GetOrAdd(strategyId, _ => new StrategyMetrics());

        private static MetricsSnapshot BuildSnapshot(string strategyId, StrategyMetrics m)
        {
            var samples = m.LatencySamples.ToArray();
            Array.Sort(samples);

            return new MetricsSnapshot(
                StrategyId: strategyId,
                TotalConnections: Interlocked.Read(ref m.TotalConnections),
                SuccessfulConnections: Interlocked.Read(ref m.SuccessfulConnections),
                FailedConnections: Interlocked.Read(ref m.FailedConnections),
                ActiveConnections: (int)Interlocked.Read(ref m.ActiveConnections),
                ErrorCount: Interlocked.Read(ref m.ErrorCount),
                BytesSent: Interlocked.Read(ref m.BytesSent),
                BytesReceived: Interlocked.Read(ref m.BytesReceived),
                TransferCount: Interlocked.Read(ref m.TransferCount),
                AverageLatencyMs: samples.Length > 0 ? samples.Average() : 0,
                P50LatencyMs: Percentile(samples, 50),
                P95LatencyMs: Percentile(samples, 95),
                P99LatencyMs: Percentile(samples, 99),
                ErrorsByType: new Dictionary<string, long>(m.ErrorsByType),
                Timestamp: DateTimeOffset.UtcNow);
        }

        private static double Percentile(double[] sortedSamples, int percentile)
        {
            if (sortedSamples.Length == 0) return 0;
            var index = (int)Math.Ceiling(percentile / 100.0 * sortedSamples.Length) - 1;
            return sortedSamples[Math.Clamp(index, 0, sortedSamples.Length - 1)];
        }

        private static void TrimSamples(ConcurrentQueue<double> queue, int maxSize)
        {
            while (queue.Count > maxSize)
                queue.TryDequeue(out _);
        }

        /// <inheritdoc/>
        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;

            await _publishTimer.DisposeAsync();
            await PublishMetricsAsync();
            _metrics.Clear();
        }
    }

    /// <summary>
    /// Point-in-time metrics snapshot for a single strategy.
    /// </summary>
    /// <param name="StrategyId">Strategy identifier.</param>
    /// <param name="TotalConnections">Total connection attempts.</param>
    /// <param name="SuccessfulConnections">Successful connection count.</param>
    /// <param name="FailedConnections">Failed connection count.</param>
    /// <param name="ActiveConnections">Currently active connections.</param>
    /// <param name="ErrorCount">Total errors recorded.</param>
    /// <param name="BytesSent">Total bytes sent.</param>
    /// <param name="BytesReceived">Total bytes received.</param>
    /// <param name="TransferCount">Total transfer operations.</param>
    /// <param name="AverageLatencyMs">Average connection latency in milliseconds.</param>
    /// <param name="P50LatencyMs">50th percentile latency.</param>
    /// <param name="P95LatencyMs">95th percentile latency.</param>
    /// <param name="P99LatencyMs">99th percentile latency.</param>
    /// <param name="ErrorsByType">Error counts by type classification.</param>
    /// <param name="Timestamp">When this snapshot was taken.</param>
    public sealed record MetricsSnapshot(
        string StrategyId,
        long TotalConnections,
        long SuccessfulConnections,
        long FailedConnections,
        int ActiveConnections,
        long ErrorCount,
        long BytesSent,
        long BytesReceived,
        long TransferCount,
        double AverageLatencyMs,
        double P50LatencyMs,
        double P95LatencyMs,
        double P99LatencyMs,
        Dictionary<string, long> ErrorsByType,
        DateTimeOffset Timestamp)
    {
        /// <summary>
        /// Success rate as a percentage (0-100).
        /// </summary>
        public double SuccessRate => TotalConnections > 0
            ? (double)SuccessfulConnections / TotalConnections * 100.0
            : 100.0;
    }

    /// <summary>
    /// Internal mutable metrics state for a single strategy.
    /// </summary>
    internal sealed class StrategyMetrics
    {
        public long TotalConnections;
        public long SuccessfulConnections;
        public long FailedConnections;
        public long ActiveConnections;
        public long ErrorCount;
        public long BytesSent;
        public long BytesReceived;
        public long TransferCount;
        public readonly ConcurrentQueue<double> LatencySamples = new();
        public readonly BoundedDictionary<string, long> ErrorsByType = new BoundedDictionary<string, long>(1000);
        public readonly int MaxSamples = 1000;
    }
}
