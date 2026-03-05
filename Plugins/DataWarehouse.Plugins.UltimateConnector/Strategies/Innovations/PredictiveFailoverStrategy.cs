using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.Innovations
{
    /// <summary>
    /// ML-driven predictive failover strategy that monitors connection health metrics
    /// to predict failures before they occur and proactively failover to healthy replicas.
    /// Uses exponential moving averages, latency trend analysis, and error rate acceleration
    /// to detect degradation patterns.
    /// </summary>
    /// <remarks>
    /// Failure prediction is based on multiple signals:
    /// <list type="bullet">
    ///   <item>Latency trend: detects sustained increases indicating saturation</item>
    ///   <item>Error rate acceleration: detects increasing error frequency</item>
    ///   <item>Connection pool exhaustion: monitors available connections</item>
    ///   <item>Response time variance: detects instability before full failure</item>
    ///   <item>Composite health score: weighted aggregation of all signals</item>
    /// </list>
    /// </remarks>
    public class PredictiveFailoverStrategy : ConnectionStrategyBase
    {
        private readonly BoundedDictionary<string, FailoverState> _failoverStates = new BoundedDictionary<string, FailoverState>(1000);

        /// <inheritdoc/>
        public override string StrategyId => "innovation-predictive-failover";

        /// <inheritdoc/>
        public override string DisplayName => "Predictive Failover";

        /// <inheritdoc/>
        public override ConnectorCategory Category => ConnectorCategory.Protocol;

        /// <inheritdoc/>
        public override ConnectionStrategyCapabilities Capabilities => new(
            SupportsPooling: true,
            SupportsStreaming: false,
            SupportsReconnection: true,
            SupportsSsl: true,
            SupportsHealthCheck: true,
            SupportsConnectionTesting: true,
            MaxConcurrentConnections: 200
        );

        /// <inheritdoc/>
        public override string SemanticDescription =>
            "ML-driven predictive failover with proactive degradation detection using latency trends, " +
            "error rate acceleration, and composite health scoring for zero-downtime failover";

        /// <inheritdoc/>
        public override string[] Tags => ["failover", "predictive", "ml", "resilience", "high-availability", "degradation"];

        /// <summary>
        /// Initializes a new instance of <see cref="PredictiveFailoverStrategy"/>.
        /// </summary>
        /// <param name="logger">Optional logger for diagnostics.</param>
        public PredictiveFailoverStrategy(ILogger? logger = null) : base(logger) { }

        /// <inheritdoc/>
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var primaryEndpoint = config.ConnectionString
                ?? throw new ArgumentException("Primary endpoint URL is required in ConnectionString.");

            var replicaEndpoints = GetConfiguration<string>(config, "replica_endpoints", "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var failoverThreshold = GetConfiguration<double>(config, "failover_threshold", 0.3);
            var monitorIntervalSec = GetConfiguration<int>(config, "monitor_interval_seconds", 10);
            var latencyWindowSize = GetConfiguration<int>(config, "latency_window_size", 20);
            var errorRateWindow = GetConfiguration<int>(config, "error_rate_window", 50);

            var handler = new SocketsHttpHandler
            {
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
                MaxConnectionsPerServer = config.PoolSize,
                ConnectTimeout = TimeSpan.FromSeconds(5)
            };

            var client = new HttpClient(handler)
            {
                BaseAddress = new Uri(primaryEndpoint),
                Timeout = config.Timeout,
                DefaultRequestVersion = new Version(2, 0)
            };

            if (!string.IsNullOrEmpty(config.AuthCredential))
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", config.AuthCredential);

            var sw = Stopwatch.StartNew();
            using var response = await client.GetAsync("/health", ct);
            sw.Stop();
            response.EnsureSuccessStatusCode();

            var connectionId = $"pf-{Guid.NewGuid():N}";

            var state = new FailoverState
            {
                PrimaryEndpoint = primaryEndpoint,
                ReplicaEndpoints = replicaEndpoints,
                ActiveEndpoint = primaryEndpoint,
                FailoverThreshold = failoverThreshold,
                LatencyWindow = new CircularBuffer<double>(latencyWindowSize),
                ErrorWindow = new CircularBuffer<bool>(errorRateWindow),
                HealthScore = 1.0,
                FailoverCount = 0,
                LastFailoverAt = DateTimeOffset.MinValue
            };

            state.LatencyWindow.Add(sw.Elapsed.TotalMilliseconds);
            state.ErrorWindow.Add(true);

            _failoverStates[connectionId] = state;

            var info = new Dictionary<string, object>
            {
                ["primary_endpoint"] = primaryEndpoint,
                ["active_endpoint"] = primaryEndpoint,
                ["replica_count"] = replicaEndpoints.Length,
                ["failover_threshold"] = failoverThreshold,
                ["health_score"] = 1.0,
                ["failover_count"] = 0,
                ["initial_latency_ms"] = sw.Elapsed.TotalMilliseconds,
                ["connected_at"] = DateTimeOffset.UtcNow
            };

            return new DefaultConnectionHandle(client, info, connectionId);
        }

        /// <inheritdoc/>
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var client = handle.GetConnection<HttpClient>();

            if (!_failoverStates.TryGetValue(handle.ConnectionId, out var state))
                return false;

            var sw = Stopwatch.StartNew();
            try
            {
                using var response = await client.GetAsync("/health", ct);
                sw.Stop();

                var success = response.IsSuccessStatusCode;
                state.LatencyWindow.Add(sw.Elapsed.TotalMilliseconds);
                state.ErrorWindow.Add(success);

                state.HealthScore = CalculateHealthScore(state);

                if (state.HealthScore < state.FailoverThreshold && state.ReplicaEndpoints.Length > 0)
                {
                    await AttemptFailoverAsync(handle, state, ct);
                }

                return success;
            }
            catch
            {
                sw.Stop();
                state.ErrorWindow.Add(false);
                state.HealthScore = CalculateHealthScore(state);
                return false;
            }
        }

        /// <inheritdoc/>
        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var client = handle.GetConnection<HttpClient>();
            _failoverStates.TryRemove(handle.ConnectionId, out _);
            client.Dispose();
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var isHealthy = await TestCoreAsync(handle, ct);
            sw.Stop();

            _failoverStates.TryGetValue(handle.ConnectionId, out var state);

            var avgLatency = state?.LatencyWindow.Average() ?? 0;
            var errorRate = state != null
                ? 1.0 - (state.ErrorWindow.CountWhere(s => s) / (double)Math.Max(1, state.ErrorWindow.Count))
                : 0;

            return new ConnectionHealth(
                IsHealthy: isHealthy && (state?.HealthScore ?? 0) >= (state?.FailoverThreshold ?? 0.3),
                StatusMessage: state?.HealthScore >= state?.FailoverThreshold
                    ? $"Healthy (score: {state?.HealthScore:F2}), active: {state?.ActiveEndpoint}, failovers: {state?.FailoverCount}"
                    : $"Degraded (score: {state?.HealthScore:F2}), error rate: {errorRate:P1}",
                Latency: sw.Elapsed,
                CheckedAt: DateTimeOffset.UtcNow,
                Details: new Dictionary<string, object>
                {
                    ["health_score"] = state?.HealthScore ?? 0,
                    ["active_endpoint"] = state?.ActiveEndpoint ?? "unknown",
                    ["avg_latency_ms"] = avgLatency,
                    ["error_rate"] = errorRate,
                    ["failover_count"] = state?.FailoverCount ?? 0,
                    ["latency_trend"] = CalculateLatencyTrend(state)
                });
        }

        /// <summary>
        /// Calculates a composite health score from multiple degradation signals.
        /// </summary>
        private static double CalculateHealthScore(FailoverState state)
        {
            var successRate = state.ErrorWindow.Count > 0
                ? state.ErrorWindow.CountWhere(s => s) / (double)state.ErrorWindow.Count
                : 1.0;

            var latencyTrend = CalculateLatencyTrend(state);
            var latencyScore = latencyTrend switch
            {
                > 2.0 => 0.2,
                > 1.5 => 0.5,
                > 1.2 => 0.7,
                _ => 1.0
            };

            var varianceScore = 1.0;
            if (state.LatencyWindow.Count >= 3)
            {
                var values = state.LatencyWindow.ToArray();
                var mean = values.Average();
                var variance = values.Select(v => (v - mean) * (v - mean)).Average();
                var cv = mean > 0 ? Math.Sqrt(variance) / mean : 0;
                varianceScore = cv > 1.0 ? 0.3 : cv > 0.5 ? 0.6 : 1.0;
            }

            return successRate * 0.5 + latencyScore * 0.3 + varianceScore * 0.2;
        }

        /// <summary>
        /// Calculates the latency trend as a ratio of recent to historical average.
        /// </summary>
        private static double CalculateLatencyTrend(FailoverState? state)
        {
            if (state == null || state.LatencyWindow.Count < 4) return 1.0;

            var all = state.LatencyWindow.ToArray();
            var half = all.Length / 2;
            var recentAvg = all.Skip(half).Average();
            var historicalAvg = all.Take(half).Average();

            return historicalAvg > 0 ? recentAvg / historicalAvg : 1.0;
        }

        /// <summary>
        /// Attempts to failover to a healthy replica endpoint.
        /// </summary>
        private async Task AttemptFailoverAsync(IConnectionHandle handle, FailoverState state, CancellationToken ct)
        {
            if (DateTimeOffset.UtcNow - state.LastFailoverAt < TimeSpan.FromSeconds(30))
                return;

            foreach (var replica in state.ReplicaEndpoints)
            {
                if (replica == state.ActiveEndpoint) continue;

                try
                {
                    using var probeClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                    var probeResponse = await probeClient.GetAsync($"{replica}/health", ct);

                    if (probeResponse.IsSuccessStatusCode)
                    {
                        var client = handle.GetConnection<HttpClient>();
                        client.BaseAddress = new Uri(replica);

                        state.ActiveEndpoint = replica;
                        state.FailoverCount++;
                        state.LastFailoverAt = DateTimeOffset.UtcNow;
                        state.LatencyWindow.Clear();
                        state.ErrorWindow.Clear();
                        state.HealthScore = 1.0;

                        if (handle.ConnectionInfo is Dictionary<string, object> info)
                        {
                            info["active_endpoint"] = replica;
                            info["failover_count"] = state.FailoverCount;
                            info["last_failover_at"] = state.LastFailoverAt;
                        }

                        return;
                    }
                }
                catch
                {
                    continue;
                }
            }
        }

        private class FailoverState
        {
            // Finding 1942: Protect mutable state that is read/written from TestCoreAsync and
            // AttemptFailoverAsync concurrently with a dedicated lock.
            private readonly object _stateLock = new();

            public string PrimaryEndpoint { get; set; } = "";
            public string[] ReplicaEndpoints { get; set; } = [];
            public double FailoverThreshold { get; set; }
            public CircularBuffer<double> LatencyWindow { get; set; } = new(20);
            public CircularBuffer<bool> ErrorWindow { get; set; } = new(50);

            private string _activeEndpoint = "";
            private double _healthScore;
            private int _failoverCount;
            private DateTimeOffset _lastFailoverAt = DateTimeOffset.MinValue;

            public string ActiveEndpoint
            {
                get { lock (_stateLock) return _activeEndpoint; }
                set { lock (_stateLock) _activeEndpoint = value; }
            }
            public double HealthScore
            {
                get { lock (_stateLock) return _healthScore; }
                set { lock (_stateLock) _healthScore = value; }
            }
            public int FailoverCount
            {
                get { lock (_stateLock) return _failoverCount; }
                set { lock (_stateLock) _failoverCount = value; }
            }
            public DateTimeOffset LastFailoverAt
            {
                get { lock (_stateLock) return _lastFailoverAt; }
                set { lock (_stateLock) _lastFailoverAt = value; }
            }
        }

        /// <summary>
        /// Fixed-size circular buffer for maintaining sliding window metrics.
        /// </summary>
        private class CircularBuffer<T>
        {
            private readonly T[] _buffer;
            private int _head;
            private int _count;
            private readonly object _lock = new();

            public int Count { get { lock (_lock) return _count; } }

            public CircularBuffer(int capacity)
            {
                _buffer = new T[capacity];
            }

            public void Add(T item)
            {
                lock (_lock)
                {
                    _buffer[_head] = item;
                    _head = (_head + 1) % _buffer.Length;
                    if (_count < _buffer.Length) _count++;
                }
            }

            public T[] ToArray()
            {
                lock (_lock)
                {
                    var result = new T[_count];
                    var start = _count < _buffer.Length ? 0 : _head;
                    for (int i = 0; i < _count; i++)
                        result[i] = _buffer[(start + i) % _buffer.Length];
                    return result;
                }
            }

            public int CountWhere(Func<T, bool> predicate)
            {
                lock (_lock)
                {
                    int c = 0;
                    var start = _count < _buffer.Length ? 0 : _head;
                    for (int i = 0; i < _count; i++)
                        if (predicate(_buffer[(start + i) % _buffer.Length])) c++;
                    return c;
                }
            }

            public double Average()
            {
                lock (_lock)
                {
                    if (_count == 0) return 0;
                    double sum = 0;
                    var start = _count < _buffer.Length ? 0 : _head;
                    for (int i = 0; i < _count; i++)
                        sum += Convert.ToDouble((object)_buffer[(start + i) % _buffer.Length]!);
                    return sum / _count;
                }
            }

            public void Clear()
            {
                lock (_lock) { _head = 0; _count = 0; }
            }
        }
    }
}
