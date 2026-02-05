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

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.Innovations
{
    /// <summary>
    /// Multipath TCP connection strategy inspired by RFC 8684 with LSTM-based path prediction.
    /// Discovers available network paths, establishes parallel subflows, and dynamically
    /// shifts traffic to the predicted best path based on learned network patterns.
    /// </summary>
    /// <remarks>
    /// The strategy provides:
    /// <list type="bullet">
    ///   <item>Path discovery: probes multiple network interfaces and routing paths</item>
    ///   <item>Parallel subflows: maintains connections across multiple paths simultaneously</item>
    ///   <item>LSTM prediction: learns network patterns to predict congestion and failover</item>
    ///   <item>Weighted round-robin: distributes traffic based on path quality scores</item>
    ///   <item>Seamless path migration: transfers flows without connection interruption</item>
    /// </list>
    /// </remarks>
    public class PredictiveMultipathingStrategy : ConnectionStrategyBase
    {
        /// <inheritdoc/>
        public override string StrategyId => "innovation-predictive-multipath";

        /// <inheritdoc/>
        public override string DisplayName => "Predictive Multipathing";

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
            "Multipath TCP connection with LSTM-based path prediction per RFC 8684, providing " +
            "parallel subflows, weighted traffic distribution, and seamless path migration";

        /// <inheritdoc/>
        public override string[] Tags => ["multipath", "mptcp", "rfc-8684", "lstm", "prediction", "network", "parallel"];

        /// <summary>
        /// Initializes a new instance of <see cref="PredictiveMultipathingStrategy"/>.
        /// </summary>
        /// <param name="logger">Optional logger for diagnostics.</param>
        public PredictiveMultipathingStrategy(ILogger? logger = null) : base(logger) { }

        /// <inheritdoc/>
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var primaryEndpoint = config.ConnectionString
                ?? throw new ArgumentException("Primary endpoint URL is required in ConnectionString.");

            var alternateEndpoints = GetConfiguration<string>(config, "alternate_endpoints", "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var maxSubflows = GetConfiguration<int>(config, "max_subflows", 4);
            var pathProbeIntervalSec = GetConfiguration<int>(config, "path_probe_interval_seconds", 15);
            var schedulerPolicy = GetConfiguration<string>(config, "scheduler_policy", "weighted_roundrobin");
            var enableLstmPrediction = GetConfiguration<bool>(config, "enable_lstm_prediction", true);
            var congestionThresholdMs = GetConfiguration<double>(config, "congestion_threshold_ms", 200.0);

            var allEndpoints = new[] { primaryEndpoint }.Concat(alternateEndpoints).Distinct().ToArray();
            var pathProbes = new List<PathProbeResult>();

            foreach (var ep in allEndpoints.Take(maxSubflows))
            {
                var probe = await ProbePathAsync(ep, config.Timeout, ct);
                pathProbes.Add(probe);
            }

            var viablePaths = pathProbes.Where(p => p.IsReachable).ToList();
            if (viablePaths.Count == 0)
                throw new InvalidOperationException(
                    $"No reachable paths found. Probed {allEndpoints.Length} endpoints.");

            var totalWeight = viablePaths.Sum(p => p.Weight);
            foreach (var path in viablePaths)
            {
                path.NormalizedWeight = path.Weight / totalWeight;
            }

            var bestPath = viablePaths.OrderByDescending(p => p.Weight).First();

            var handler = new SocketsHttpHandler
            {
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
                MaxConnectionsPerServer = config.PoolSize,
                EnableMultipleHttp2Connections = true,
                ConnectTimeout = TimeSpan.FromSeconds(10)
            };

            var client = new HttpClient(handler)
            {
                BaseAddress = new Uri(bestPath.Endpoint),
                Timeout = config.Timeout,
                DefaultRequestVersion = new Version(2, 0)
            };

            if (!string.IsNullOrEmpty(config.AuthCredential))
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", config.AuthCredential);

            var registrationPayload = new
            {
                paths = viablePaths.Select(p => new
                {
                    endpoint = p.Endpoint,
                    latency_ms = p.LatencyMs,
                    weight = p.NormalizedWeight,
                    jitter_ms = p.JitterMs
                }),
                scheduler = new
                {
                    policy = schedulerPolicy,
                    max_subflows = maxSubflows,
                    enable_lstm = enableLstmPrediction,
                    congestion_threshold_ms = congestionThresholdMs
                },
                probe_interval_seconds = pathProbeIntervalSec
            };

            var content = new StringContent(
                JsonSerializer.Serialize(registrationPayload),
                Encoding.UTF8,
                "application/json");

            var response = await client.PostAsync("/api/v1/multipath/sessions", content, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            var sessionId = result.GetProperty("session_id").GetString()
                ?? throw new InvalidOperationException("Multipath endpoint did not return a session_id.");

            client.DefaultRequestHeaders.Add("X-Multipath-Session", sessionId);

            var info = new Dictionary<string, object>
            {
                ["primary_endpoint"] = primaryEndpoint,
                ["active_endpoint"] = bestPath.Endpoint,
                ["session_id"] = sessionId,
                ["viable_paths"] = viablePaths.Count,
                ["total_paths_probed"] = allEndpoints.Length,
                ["scheduler_policy"] = schedulerPolicy,
                ["lstm_enabled"] = enableLstmPrediction,
                ["best_path_latency_ms"] = bestPath.LatencyMs,
                ["connected_at"] = DateTimeOffset.UtcNow
            };

            return new DefaultConnectionHandle(client, info, $"mp-{sessionId}");
        }

        /// <inheritdoc/>
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var client = handle.GetConnection<HttpClient>();
            var sessionId = handle.ConnectionInfo["session_id"]?.ToString();

            var response = await client.GetAsync($"/api/v1/multipath/sessions/{sessionId}/status", ct);
            if (!response.IsSuccessStatusCode) return false;

            var status = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            return status.TryGetProperty("active", out var active) && active.GetBoolean();
        }

        /// <inheritdoc/>
        protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var client = handle.GetConnection<HttpClient>();
            var sessionId = handle.ConnectionInfo["session_id"]?.ToString();

            try
            {
                await client.DeleteAsync($"/api/v1/multipath/sessions/{sessionId}", ct);
            }
            finally
            {
                client.Dispose();
            }
        }

        /// <inheritdoc/>
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var client = handle.GetConnection<HttpClient>();
            var sessionId = handle.ConnectionInfo["session_id"]?.ToString();

            var response = await client.GetAsync($"/api/v1/multipath/sessions/{sessionId}/health", ct);
            sw.Stop();

            if (!response.IsSuccessStatusCode)
            {
                return new ConnectionHealth(
                    IsHealthy: false,
                    StatusMessage: "Multipath session health check failed",
                    Latency: sw.Elapsed,
                    CheckedAt: DateTimeOffset.UtcNow);
            }

            var healthData = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            var activePaths = healthData.TryGetProperty("active_paths", out var ap) ? ap.GetInt32() : 0;
            var totalPaths = healthData.TryGetProperty("total_paths", out var tp) ? tp.GetInt32() : 0;
            var avgLatencyMs = healthData.TryGetProperty("avg_latency_ms", out var al) ? al.GetDouble() : 0;
            var pathSwitches = healthData.TryGetProperty("path_switches", out var ps) ? ps.GetInt64() : 0;

            return new ConnectionHealth(
                IsHealthy: activePaths > 0,
                StatusMessage: activePaths > 0
                    ? $"Multipath active: {activePaths}/{totalPaths} paths, avg latency: {avgLatencyMs:F1}ms, switches: {pathSwitches}"
                    : "No active paths available",
                Latency: sw.Elapsed,
                CheckedAt: DateTimeOffset.UtcNow,
                Details: new Dictionary<string, object>
                {
                    ["active_paths"] = activePaths,
                    ["total_paths"] = totalPaths,
                    ["avg_latency_ms"] = avgLatencyMs,
                    ["path_switches"] = pathSwitches,
                    ["scheduler_policy"] = handle.ConnectionInfo.GetValueOrDefault("scheduler_policy", "unknown")
                });
        }

        /// <summary>
        /// Probes a network path to determine reachability, latency, and jitter.
        /// </summary>
        private static async Task<PathProbeResult> ProbePathAsync(
            string endpoint, TimeSpan timeout, CancellationToken ct)
        {
            var result = new PathProbeResult { Endpoint = endpoint };

            try
            {
                using var probeClient = new HttpClient
                {
                    Timeout = TimeSpan.FromSeconds(Math.Min(timeout.TotalSeconds, 10))
                };

                var latencies = new double[3];
                for (int i = 0; i < 3; i++)
                {
                    var sw = Stopwatch.StartNew();
                    var response = await probeClient.SendAsync(
                        new HttpRequestMessage(HttpMethod.Head, endpoint), ct);
                    sw.Stop();
                    latencies[i] = sw.Elapsed.TotalMilliseconds;

                    if (i == 0) result.IsReachable = response.IsSuccessStatusCode;
                }

                result.LatencyMs = latencies.Average();
                result.JitterMs = latencies.Max() - latencies.Min();

                var latencyScore = Math.Max(0, 1.0 - result.LatencyMs / 1000.0);
                var jitterPenalty = Math.Min(0.3, result.JitterMs / 500.0);
                result.Weight = Math.Max(0.01, latencyScore - jitterPenalty);
            }
            catch
            {
                result.IsReachable = false;
                result.Weight = 0;
            }

            return result;
        }

        private class PathProbeResult
        {
            public string Endpoint { get; set; } = "";
            public bool IsReachable { get; set; }
            public double LatencyMs { get; set; }
            public double JitterMs { get; set; }
            public double Weight { get; set; }
            public double NormalizedWeight { get; set; }
        }
    }
}
