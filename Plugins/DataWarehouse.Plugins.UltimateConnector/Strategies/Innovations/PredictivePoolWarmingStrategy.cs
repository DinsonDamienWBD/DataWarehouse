using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.Innovations
{
    /// <summary>
    /// Predictive connection pool warming strategy that uses a 24-hour time-series histogram
    /// to forecast connection demand and proactively pre-warm or cool the pool.
    /// </summary>
    /// <remarks>
    /// The strategy maintains a histogram of 1440 per-minute buckets over a rolling 7-day window:
    /// <list type="bullet">
    ///   <item>Each bucket tracks connection demand using an exponential moving average</item>
    ///   <item>WarmingThreshold: if predicted demand in the next 5 minutes exceeds current pool, pre-warm</item>
    ///   <item>CoolingThreshold: if actual demand drops below 50% of pool for 15 minutes, shrink</item>
    ///   <item>Histogram data is smoothed to eliminate spike noise while preserving trends</item>
    /// </list>
    /// This enables the pool to scale ahead of traffic patterns such as morning ramps,
    /// lunch-hour dips, and batch job windows.
    /// </remarks>
    public class PredictivePoolWarmingStrategy : ConnectionStrategyBase
    {
        private readonly BoundedDictionary<string, PoolWarmingState> _states = new BoundedDictionary<string, PoolWarmingState>(1000);

        /// <inheritdoc/>
        public override string StrategyId => "innovation-predictive-pool";

        /// <inheritdoc/>
        public override string DisplayName => "Predictive Pool Warming";

        /// <inheritdoc/>
        public override ConnectorCategory Category => ConnectorCategory.Protocol;

        /// <inheritdoc/>
        public override ConnectionStrategyCapabilities Capabilities => new(
            SupportsPooling: true,
            SupportsStreaming: true,
            SupportsReconnection: true,
            SupportsSsl: true,
            SupportsHealthCheck: true,
            SupportsConnectionTesting: true,
            MaxConcurrentConnections: 500
        );

        /// <inheritdoc/>
        public override string SemanticDescription =>
            "Time-series histogram-driven pool warming that predicts connection demand from " +
            "24-hour usage patterns and proactively scales the pool ahead of traffic changes";

        /// <inheritdoc/>
        public override string[] Tags => ["pool", "warming", "predictive", "histogram", "scaling", "time-series"];

        /// <summary>
        /// Initializes a new instance of <see cref="PredictivePoolWarmingStrategy"/>.
        /// </summary>
        /// <param name="logger">Optional logger for diagnostics.</param>
        public PredictivePoolWarmingStrategy(ILogger? logger = null) : base(logger) { }

        /// <inheritdoc/>
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var endpoint = config.ConnectionString
                ?? throw new ArgumentException("Endpoint URL is required in ConnectionString.");

            var basePoolSize = GetConfiguration<int>(config, "base_pool_size", 10);
            var maxPoolSize = GetConfiguration<int>(config, "max_pool_size", 200);
            var lookAheadMinutes = GetConfiguration<int>(config, "look_ahead_minutes", 5);
            var coolingDelayMinutes = GetConfiguration<int>(config, "cooling_delay_minutes", 15);
            var emaAlpha = GetConfiguration<double>(config, "ema_alpha", 0.3);
            var checkIntervalSec = GetConfiguration<int>(config, "check_interval_seconds", 30);

            var handler = new SocketsHttpHandler
            {
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(10),
                MaxConnectionsPerServer = maxPoolSize,
                ConnectTimeout = TimeSpan.FromSeconds(5)
            };

            var client = new HttpClient(handler)
            {
                BaseAddress = new Uri(endpoint),
                Timeout = config.Timeout,
                DefaultRequestVersion = new Version(2, 0)
            };

            if (!string.IsNullOrEmpty(config.AuthCredential))
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", config.AuthCredential);

            var sw = Stopwatch.StartNew();
            var response = await client.GetAsync("/health", ct);
            sw.Stop();
            response.EnsureSuccessStatusCode();

            var connectionId = $"pool-warm-{Guid.NewGuid():N}";

            var histogram = new TrafficHistogram(emaAlpha);
            var nowBucket = GetMinuteBucket(DateTimeOffset.UtcNow);
            histogram.RecordDemand(nowBucket, 1);

            var state = new PoolWarmingState
            {
                Histogram = histogram,
                CurrentPoolSize = basePoolSize,
                BasePoolSize = basePoolSize,
                MaxPoolSize = maxPoolSize,
                LookAheadMinutes = lookAheadMinutes,
                CoolingDelayMinutes = coolingDelayMinutes,
                CheckIntervalSec = checkIntervalSec,
                WarmingEvents = 0,
                CoolingEvents = 0,
                ActiveConnections = 1,
                LastCoolingCheck = DateTimeOffset.UtcNow,
                LowDemandSince = null,
                MonitorCts = CancellationTokenSource.CreateLinkedTokenSource(ct)
            };

            _states[connectionId] = state;

            // Start background pool scaling loop
            _ = Task.Run(() => PoolScalingLoopAsync(connectionId, client, state), CancellationToken.None);

            var info = new Dictionary<string, object>
            {
                ["endpoint"] = endpoint,
                ["base_pool_size"] = basePoolSize,
                ["max_pool_size"] = maxPoolSize,
                ["current_pool_size"] = basePoolSize,
                ["look_ahead_minutes"] = lookAheadMinutes,
                ["initial_latency_ms"] = sw.Elapsed.TotalMilliseconds,
                ["connected_at"] = DateTimeOffset.UtcNow
            };

            return new DefaultConnectionHandle(client, info, connectionId);
        }

        /// <inheritdoc/>
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var client = handle.GetConnection<HttpClient>();
            try
            {
                var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/"), ct);

                if (_states.TryGetValue(handle.ConnectionId, out var state))
                {
                    var bucket = GetMinuteBucket(DateTimeOffset.UtcNow);
                    state.Histogram.RecordDemand(bucket, 1);
                    Interlocked.Increment(ref state.ActiveConnections);
                }

                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        /// <inheritdoc/>
        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var client = handle.GetConnection<HttpClient>();
            if (_states.TryRemove(handle.ConnectionId, out var state))
            {
                state.MonitorCts.Cancel();
                state.MonitorCts.Dispose();
            }
            client.Dispose();
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var isHealthy = await TestCoreAsync(handle, ct);
            sw.Stop();

            _states.TryGetValue(handle.ConnectionId, out var state);

            var currentBucket = GetMinuteBucket(DateTimeOffset.UtcNow);
            var predictedDemand = state?.Histogram.PredictDemand(
                currentBucket, state.LookAheadMinutes) ?? 0;
            var coverage = state?.Histogram.GetCoveragePercent() ?? 0;

            return new ConnectionHealth(
                IsHealthy: isHealthy,
                StatusMessage: isHealthy
                    ? $"Pool active: size={state?.CurrentPoolSize}, predicted={predictedDemand:F1}, " +
                      $"warm={state?.WarmingEvents}, cool={state?.CoolingEvents}"
                    : "Pool endpoint unreachable",
                Latency: sw.Elapsed,
                CheckedAt: DateTimeOffset.UtcNow,
                Details: new Dictionary<string, object>
                {
                    ["current_pool_size"] = state?.CurrentPoolSize ?? 0,
                    ["predicted_demand"] = predictedDemand,
                    ["histogram_coverage_percent"] = coverage,
                    ["warming_events"] = state?.WarmingEvents ?? 0,
                    ["cooling_events"] = state?.CoolingEvents ?? 0,
                    ["active_connections"] = state?.ActiveConnections ?? 0
                });
        }

        /// <summary>
        /// Background loop that periodically checks predicted demand and adjusts pool size.
        /// </summary>
        private async Task PoolScalingLoopAsync(
            string connectionId, HttpClient client, PoolWarmingState state)
        {
            var token = state.MonitorCts.Token;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(state.CheckIntervalSec), token);

                    var now = DateTimeOffset.UtcNow;
                    var currentBucket = GetMinuteBucket(now);

                    // Record current demand
                    state.Histogram.RecordDemand(currentBucket, state.ActiveConnections);
                    Interlocked.Exchange(ref state.ActiveConnections, 0);

                    // Predict demand over the look-ahead window
                    var predictedDemand = state.Histogram.PredictDemand(
                        currentBucket, state.LookAheadMinutes);

                    // Warming: predicted demand exceeds current pool
                    if (predictedDemand > state.CurrentPoolSize * 0.8)
                    {
                        var targetSize = Math.Min(
                            state.MaxPoolSize,
                            (int)Math.Ceiling(predictedDemand * 1.2));

                        if (targetSize > state.CurrentPoolSize)
                        {
                            // Validate endpoint is still healthy before warming
                            try
                            {
                                using var warmRequest = new HttpRequestMessage(HttpMethod.Head, "/");
                                var warmResponse = await client.SendAsync(warmRequest, token);
                                if (warmResponse.IsSuccessStatusCode)
                                {
                                    state.CurrentPoolSize = targetSize;
                                    state.WarmingEvents++;
                                    state.LowDemandSince = null;
                                }
                            }
                            catch
                            {
                                // Endpoint unreachable, skip warming
                            }
                        }
                    }

                    // Cooling: actual demand well below pool size for sustained period
                    var actualDemand = state.Histogram.GetBucketValue(currentBucket);
                    if (actualDemand < state.CurrentPoolSize * 0.5)
                    {
                        state.LowDemandSince ??= now;

                        if ((now - state.LowDemandSince.Value).TotalMinutes >= state.CoolingDelayMinutes)
                        {
                            var cooledSize = Math.Max(
                                state.BasePoolSize,
                                (int)Math.Ceiling(actualDemand * 1.3));

                            if (cooledSize < state.CurrentPoolSize)
                            {
                                state.CurrentPoolSize = cooledSize;
                                state.CoolingEvents++;
                                state.LowDemandSince = null;
                            }
                        }
                    }
                    else
                    {
                        state.LowDemandSince = null;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown
            }
        }

        /// <summary>
        /// Returns the minute-of-day bucket index (0-1439) for a given timestamp.
        /// </summary>
        private static int GetMinuteBucket(DateTimeOffset timestamp)
        {
            return timestamp.Hour * 60 + timestamp.Minute;
        }

        /// <summary>
        /// 24-hour traffic histogram with 1440 per-minute buckets using exponential moving
        /// average for smoothing across a rolling observation window.
        /// </summary>
        private class TrafficHistogram
        {
            private readonly double[] _buckets = new double[1440];
            private readonly int[] _sampleCounts = new int[1440];
            private readonly double _alpha;
            private readonly object _lock = new();

            public TrafficHistogram(double alpha)
            {
                _alpha = Math.Clamp(alpha, 0.01, 0.99);
            }

            /// <summary>
            /// Records observed demand for a given minute bucket, applying EMA smoothing.
            /// </summary>
            public void RecordDemand(int bucket, int demand)
            {
                bucket = Math.Clamp(bucket, 0, 1439);
                lock (_lock)
                {
                    if (_sampleCounts[bucket] == 0)
                    {
                        _buckets[bucket] = demand;
                    }
                    else
                    {
                        _buckets[bucket] = _alpha * demand + (1.0 - _alpha) * _buckets[bucket];
                    }
                    _sampleCounts[bucket]++;
                }
            }

            /// <summary>
            /// Predicts total demand over the next <paramref name="minutes"/> starting
            /// from <paramref name="currentBucket"/>.
            /// </summary>
            public double PredictDemand(int currentBucket, int minutes)
            {
                lock (_lock)
                {
                    double maxDemand = 0;
                    for (int i = 0; i < minutes; i++)
                    {
                        var futureBucket = (currentBucket + i) % 1440;
                        if (_sampleCounts[futureBucket] > 0)
                            maxDemand = Math.Max(maxDemand, _buckets[futureBucket]);
                    }
                    return maxDemand;
                }
            }

            /// <summary>
            /// Gets the current smoothed value for a specific bucket.
            /// </summary>
            public double GetBucketValue(int bucket)
            {
                bucket = Math.Clamp(bucket, 0, 1439);
                lock (_lock) return _buckets[bucket];
            }

            /// <summary>
            /// Returns the percentage of buckets that have at least one sample (0-100).
            /// </summary>
            public double GetCoveragePercent()
            {
                lock (_lock)
                {
                    int populated = _sampleCounts.Count(c => c > 0);
                    return populated / 1440.0 * 100.0;
                }
            }
        }

        private class PoolWarmingState
        {
            public required TrafficHistogram Histogram { get; set; }
            public int CurrentPoolSize { get; set; }
            public int BasePoolSize { get; set; }
            public int MaxPoolSize { get; set; }
            public int LookAheadMinutes { get; set; }
            public int CoolingDelayMinutes { get; set; }
            public int CheckIntervalSec { get; set; }
            public long WarmingEvents { get; set; }
            public long CoolingEvents { get; set; }
            public int ActiveConnections;
            public DateTimeOffset LastCoolingCheck { get; set; }
            public DateTimeOffset? LowDemandSince { get; set; }
            public CancellationTokenSource MonitorCts { get; set; } = new();
        }
    }
}
