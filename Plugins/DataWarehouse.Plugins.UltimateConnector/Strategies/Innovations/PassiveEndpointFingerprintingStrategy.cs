using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.Innovations
{
    /// <summary>
    /// Passive endpoint fingerprinting strategy that continuously monitors TCP/protocol-level
    /// health signals to detect degradation trends before they cause failures.
    /// </summary>
    /// <remarks>
    /// The strategy tracks five vitals for each endpoint:
    /// <list type="bullet">
    ///   <item>TCP window size: inferred from throughput measurements</item>
    ///   <item>TTFB (time-to-first-byte): measured on periodic lightweight probes</item>
    ///   <item>Retransmission rate: estimated from request failure/retry patterns</item>
    ///   <item>RST frequency: tracked via abrupt connection terminations</item>
    ///   <item>Round-trip time: measured via HEAD request latency</item>
    /// </list>
    /// Each vital is tracked in a circular buffer with linear regression to compute trend slopes.
    /// A composite health score aggregates weighted trends to detect early degradation.
    /// </remarks>
    public class PassiveEndpointFingerprintingStrategy : ConnectionStrategyBase
    {
        private readonly ConcurrentDictionary<string, FingerprintState> _states = new();

        /// <inheritdoc/>
        public override string StrategyId => "innovation-passive-fingerprint";

        /// <inheritdoc/>
        public override string DisplayName => "Passive Endpoint Fingerprinting";

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
            MaxConcurrentConnections: 200
        );

        /// <inheritdoc/>
        public override string SemanticDescription =>
            "Passive endpoint health fingerprinting using TCP-level vitals (TTFB, RTT, window size, " +
            "retransmission, RST frequency) with linear regression trend detection for early degradation warning";

        /// <inheritdoc/>
        public override string[] Tags => ["fingerprint", "passive", "health", "monitoring", "degradation", "trend"];

        /// <summary>
        /// Initializes a new instance of <see cref="PassiveEndpointFingerprintingStrategy"/>.
        /// </summary>
        /// <param name="logger">Optional logger for diagnostics.</param>
        public PassiveEndpointFingerprintingStrategy(ILogger? logger = null) : base(logger) { }

        /// <inheritdoc/>
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var endpoint = config.ConnectionString
                ?? throw new ArgumentException("Endpoint URL is required in ConnectionString.");

            var probeIntervalMs = GetConfiguration<int>(config, "probe_interval_ms", 2000);
            var degradationThreshold = GetConfiguration<double>(config, "degradation_threshold", 0.4);
            var bufferSize = GetConfiguration<int>(config, "vitals_buffer_size", 100);

            var handler = new SocketsHttpHandler
            {
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
                MaxConnectionsPerServer = config.PoolSize,
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

            // Initial probe to establish baseline
            var sw = Stopwatch.StartNew();
            var response = await client.GetAsync("/health", ct);
            sw.Stop();
            response.EnsureSuccessStatusCode();

            var initialTtfb = sw.Elapsed.TotalMilliseconds;
            var connectionId = $"fp-{Guid.NewGuid():N}";

            var vitals = new EndpointVitals(bufferSize);
            vitals.TtfbMs.Add(initialTtfb);
            vitals.RoundTripTimeMs.Add(initialTtfb);
            vitals.TcpWindowSize.Add(config.PoolSize * 64.0); // Inferred initial window
            vitals.RetransmissionRate.Add(0.0);
            vitals.RstFrequency.Add(0.0);

            var state = new FingerprintState
            {
                Vitals = vitals,
                DegradationThreshold = degradationThreshold,
                ProbeIntervalMs = probeIntervalMs,
                CompositeScore = 1.0,
                TotalProbes = 1,
                DegradationEvents = 0,
                IsDegraded = false,
                ConsecutiveFailures = 0,
                MonitorCts = CancellationTokenSource.CreateLinkedTokenSource(ct)
            };

            _states[connectionId] = state;

            // Start passive monitoring loop
            _ = Task.Run(() => PassiveMonitorLoopAsync(connectionId, client, state), CancellationToken.None);

            var info = new Dictionary<string, object>
            {
                ["endpoint"] = endpoint,
                ["probe_interval_ms"] = probeIntervalMs,
                ["degradation_threshold"] = degradationThreshold,
                ["initial_ttfb_ms"] = initialTtfb,
                ["composite_score"] = 1.0,
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
                var sw = Stopwatch.StartNew();
                var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/"), ct);
                sw.Stop();

                if (_states.TryGetValue(handle.ConnectionId, out var state))
                {
                    state.Vitals.RoundTripTimeMs.Add(sw.Elapsed.TotalMilliseconds);
                    state.ConsecutiveFailures = 0;
                }

                return response.IsSuccessStatusCode;
            }
            catch
            {
                if (_states.TryGetValue(handle.ConnectionId, out var state))
                {
                    state.ConsecutiveFailures++;
                    state.Vitals.RetransmissionRate.Add(
                        Math.Min(1.0, state.ConsecutiveFailures / 10.0));
                }
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

            var compositeScore = state?.CompositeScore ?? 0;
            var isDegraded = state?.IsDegraded ?? true;
            var ttfbValues = state?.Vitals.TtfbMs.ToArray() ?? [];
            var avgTtfb = ttfbValues.Length > 0 ? ttfbValues.Average() : 0;
            var ttfbTrend = state != null ? VitalsTrend.ComputeSlope(state.Vitals.TtfbMs) : 0;

            var trendLabel = ttfbTrend switch
            {
                < -0.5 => "improving",
                > 0.5 => "degrading",
                _ => "stable"
            };

            var windowValues = state?.Vitals.TcpWindowSize.ToArray() ?? [];
            var avgWindow = windowValues.Length > 0 ? windowValues.Average() : 0;
            var retransValues = state?.Vitals.RetransmissionRate.ToArray() ?? [];
            var avgRetrans = retransValues.Length > 0 ? retransValues.Average() : 0;

            return new ConnectionHealth(
                IsHealthy: isHealthy && !isDegraded,
                StatusMessage: isDegraded
                    ? $"DEGRADED: composite={compositeScore:F2}, ttfb={avgTtfb:F1}ms ({trendLabel})"
                    : $"Healthy: composite={compositeScore:F2}, ttfb={avgTtfb:F1}ms ({trendLabel})",
                Latency: sw.Elapsed,
                CheckedAt: DateTimeOffset.UtcNow,
                Details: new Dictionary<string, object>
                {
                    ["composite_score"] = compositeScore,
                    ["window_size"] = avgWindow,
                    ["ttfb_ms"] = avgTtfb,
                    ["ttfb_trend"] = trendLabel,
                    ["retransmission_rate"] = avgRetrans,
                    ["degradation_events"] = state?.DegradationEvents ?? 0,
                    ["total_probes"] = state?.TotalProbes ?? 0
                });
        }

        /// <summary>
        /// Background loop that periodically probes the endpoint and updates vitals.
        /// </summary>
        private async Task PassiveMonitorLoopAsync(
            string connectionId, HttpClient client, FingerprintState state)
        {
            var token = state.MonitorCts.Token;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(state.ProbeIntervalMs, token);

                    try
                    {
                        // Measure TTFB and RTT via lightweight probe
                        var sw = Stopwatch.StartNew();
                        using var request = new HttpRequestMessage(HttpMethod.Head, "/");
                        var response = await client.SendAsync(request, token);
                        sw.Stop();

                        var latencyMs = sw.Elapsed.TotalMilliseconds;

                        state.Vitals.TtfbMs.Add(latencyMs);
                        state.Vitals.RoundTripTimeMs.Add(latencyMs);

                        // Infer window size from response content length or status
                        if (response.Content.Headers.ContentLength is long contentLen and > 0)
                        {
                            var throughputKbps = (contentLen / 1024.0) / Math.Max(0.001, latencyMs / 1000.0);
                            state.Vitals.TcpWindowSize.Add(throughputKbps);
                        }

                        if (response.IsSuccessStatusCode)
                        {
                            state.Vitals.RetransmissionRate.Add(0.0);
                            state.Vitals.RstFrequency.Add(0.0);
                            state.ConsecutiveFailures = 0;
                        }
                        else
                        {
                            state.Vitals.RetransmissionRate.Add(0.1);
                            if ((int)response.StatusCode >= 500)
                                state.Vitals.RstFrequency.Add(1.0);
                        }
                    }
                    catch
                    {
                        // Connection failure: record as RST event and retransmission
                        state.ConsecutiveFailures++;
                        state.Vitals.RstFrequency.Add(1.0);
                        state.Vitals.RetransmissionRate.Add(
                            Math.Min(1.0, state.ConsecutiveFailures / 5.0));
                    }

                    state.TotalProbes++;

                    // Compute composite health score from all vitals trends
                    state.CompositeScore = ComputeCompositeScore(state.Vitals);

                    // Check for degradation
                    var wasDegraded = state.IsDegraded;
                    state.IsDegraded = state.CompositeScore < state.DegradationThreshold;

                    if (state.IsDegraded && !wasDegraded)
                        state.DegradationEvents++;
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown
            }
        }

        /// <summary>
        /// Computes a composite health score from weighted vital trend slopes.
        /// Score ranges from 0 (critically degraded) to 1 (perfectly healthy).
        /// Negative slopes in TTFB/RTT are good (improving), positive are bad (degrading).
        /// </summary>
        private static double ComputeCompositeScore(EndpointVitals vitals)
        {
            // Compute slopes (positive = increasing, negative = decreasing)
            var ttfbSlope = VitalsTrend.ComputeSlope(vitals.TtfbMs);
            var rttSlope = VitalsTrend.ComputeSlope(vitals.RoundTripTimeMs);
            var windowSlope = VitalsTrend.ComputeSlope(vitals.TcpWindowSize);
            var retransSlope = VitalsTrend.ComputeSlope(vitals.RetransmissionRate);
            var rstSlope = VitalsTrend.ComputeSlope(vitals.RstFrequency);

            // Convert slopes to 0-1 scores where 1 = healthy direction
            // For TTFB, RTT, retrans, RST: lower/decreasing is better
            var ttfbScore = Math.Clamp(1.0 - (ttfbSlope / 100.0), 0.0, 1.0);
            var rttScore = Math.Clamp(1.0 - (rttSlope / 100.0), 0.0, 1.0);
            var retransScore = Math.Clamp(1.0 - retransSlope * 5.0, 0.0, 1.0);
            var rstScore = Math.Clamp(1.0 - rstSlope * 5.0, 0.0, 1.0);

            // For window size: higher/increasing is better
            var windowScore = Math.Clamp(0.5 + (windowSlope / 200.0), 0.0, 1.0);

            // Also factor in current absolute values
            var currentTtfb = vitals.TtfbMs.ToArray();
            var absoluteTtfbScore = currentTtfb.Length > 0
                ? Math.Clamp(1.0 - (currentTtfb.Last() / 5000.0), 0.0, 1.0)
                : 0.5;

            var currentRetrans = vitals.RetransmissionRate.ToArray();
            var absoluteRetransScore = currentRetrans.Length > 0
                ? Math.Clamp(1.0 - currentRetrans.Last(), 0.0, 1.0)
                : 1.0;

            // Weighted composite: trend signals + absolute values
            return ttfbScore * 0.15
                + rttScore * 0.10
                + windowScore * 0.10
                + retransScore * 0.10
                + rstScore * 0.10
                + absoluteTtfbScore * 0.25
                + absoluteRetransScore * 0.20;
        }

        /// <summary>
        /// Tracks five endpoint vitals using circular buffers.
        /// </summary>
        private class EndpointVitals
        {
            public VitalsTrend TcpWindowSize { get; }
            public VitalsTrend TtfbMs { get; }
            public VitalsTrend RetransmissionRate { get; }
            public VitalsTrend RstFrequency { get; }
            public VitalsTrend RoundTripTimeMs { get; }

            public EndpointVitals(int bufferSize)
            {
                TcpWindowSize = new VitalsTrend(bufferSize);
                TtfbMs = new VitalsTrend(bufferSize);
                RetransmissionRate = new VitalsTrend(bufferSize);
                RstFrequency = new VitalsTrend(bufferSize);
                RoundTripTimeMs = new VitalsTrend(bufferSize);
            }
        }

        /// <summary>
        /// Circular buffer with linear regression slope computation for trend detection.
        /// </summary>
        private class VitalsTrend
        {
            private readonly double[] _buffer;
            private int _head;
            private int _count;
            private readonly object _lock = new();

            public int Count { get { lock (_lock) return _count; } }

            public VitalsTrend(int capacity) => _buffer = new double[capacity];

            public void Add(double value)
            {
                lock (_lock)
                {
                    _buffer[_head] = value;
                    _head = (_head + 1) % _buffer.Length;
                    if (_count < _buffer.Length) _count++;
                }
            }

            public double[] ToArray()
            {
                lock (_lock)
                {
                    var result = new double[_count];
                    var start = _count < _buffer.Length ? 0 : _head;
                    for (int i = 0; i < _count; i++)
                        result[i] = _buffer[(start + i) % _buffer.Length];
                    return result;
                }
            }

            /// <summary>
            /// Computes the linear regression slope over the buffer contents.
            /// Positive slope means values are increasing; negative means decreasing.
            /// </summary>
            public static double ComputeSlope(VitalsTrend trend)
            {
                var values = trend.ToArray();
                if (values.Length < 3) return 0.0;

                var n = values.Length;
                double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0;

                for (int i = 0; i < n; i++)
                {
                    sumX += i;
                    sumY += values[i];
                    sumXY += i * values[i];
                    sumX2 += (double)i * i;
                }

                var denominator = n * sumX2 - sumX * sumX;
                if (Math.Abs(denominator) < 1e-10) return 0.0;

                return (n * sumXY - sumX * sumY) / denominator;
            }
        }

        private class FingerprintState
        {
            public EndpointVitals Vitals { get; set; } = null!;
            public double DegradationThreshold { get; set; }
            public int ProbeIntervalMs { get; set; }
            public double CompositeScore { get; set; }
            public long TotalProbes { get; set; }
            public long DegradationEvents { get; set; }
            public bool IsDegraded { get; set; }
            public int ConsecutiveFailures { get; set; }
            public CancellationTokenSource MonitorCts { get; set; } = new();
        }
    }
}
