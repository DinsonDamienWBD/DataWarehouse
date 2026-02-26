using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    /// Digital twin connection strategy that creates virtual replicas of production connections
    /// for safe testing, traffic replay, and what-if analysis without impacting live systems.
    /// </summary>
    /// <remarks>
    /// The digital twin provides:
    /// <list type="bullet">
    ///   <item>Traffic recording mode: captures all requests/responses for later replay</item>
    ///   <item>Replay mode: re-executes recorded traffic against test environments</item>
    ///   <item>Shadow mode: mirrors production traffic to a twin for comparison</item>
    ///   <item>Drift detection between twin and production response behaviors</item>
    ///   <item>Latency and error rate simulation based on recorded production patterns</item>
    /// </list>
    /// </remarks>
    public class ConnectionDigitalTwinStrategy : ConnectionStrategyBase
    {
        /// <inheritdoc/>
        public override string StrategyId => "innovation-digital-twin";

        /// <inheritdoc/>
        public override string DisplayName => "Connection Digital Twin";

        /// <inheritdoc/>
        public override ConnectorCategory Category => ConnectorCategory.Protocol;

        /// <inheritdoc/>
        public override ConnectionStrategyCapabilities Capabilities => new(
            SupportsPooling: true,
            SupportsStreaming: true,
            SupportsSsl: true,
            SupportsHealthCheck: true,
            SupportsConnectionTesting: true,
            SupportsCompression: true,
            MaxConcurrentConnections: 100
        );

        /// <inheritdoc/>
        public override string SemanticDescription =>
            "Digital twin connections that create virtual replicas of production for safe testing, " +
            "traffic replay, shadow mirroring, and drift detection without impacting live systems";

        /// <inheritdoc/>
        public override string[] Tags => ["digital-twin", "replay", "shadow", "testing", "drift-detection", "simulation"];

        /// <summary>
        /// Initializes a new instance of <see cref="ConnectionDigitalTwinStrategy"/>.
        /// </summary>
        /// <param name="logger">Optional logger for diagnostics.</param>
        public ConnectionDigitalTwinStrategy(ILogger? logger = null) : base(logger) { }

        /// <inheritdoc/>
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var endpoint = config.ConnectionString
                ?? throw new ArgumentException("Digital twin endpoint URL is required in ConnectionString.");

            var twinMode = GetConfiguration<string>(config, "twin_mode", "shadow");
            var productionEndpoint = GetConfiguration<string>(config, "production_endpoint", "");
            var recordingId = GetConfiguration<string>(config, "recording_id", "");
            var enableDriftDetection = GetConfiguration<bool>(config, "enable_drift_detection", true);
            var driftThresholdPercent = GetConfiguration<double>(config, "drift_threshold_percent", 5.0);
            var replaySpeedMultiplier = GetConfiguration<double>(config, "replay_speed_multiplier", 1.0);
            var captureHeaders = GetConfiguration<bool>(config, "capture_headers", true);
            var captureBody = GetConfiguration<bool>(config, "capture_body", true);

            ValidateTwinMode(twinMode, productionEndpoint, recordingId);

            var handler = new SocketsHttpHandler
            {
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(10),
                MaxConnectionsPerServer = config.PoolSize,
                EnableMultipleHttp2Connections = true
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

            var twinPayload = new
            {
                mode = twinMode,
                production_endpoint = productionEndpoint,
                recording_id = recordingId,
                drift_detection = new
                {
                    enabled = enableDriftDetection,
                    threshold_percent = driftThresholdPercent,
                    compare_status_codes = true,
                    compare_response_shape = true,
                    compare_latency = true
                },
                replay_settings = new
                {
                    speed_multiplier = replaySpeedMultiplier,
                    preserve_timing = replaySpeedMultiplier == 1.0,
                    skip_errors = false
                },
                capture_settings = new
                {
                    capture_headers = captureHeaders,
                    capture_body = captureBody,
                    max_body_size_kb = GetConfiguration<int>(config, "max_body_size_kb", 1024),
                    redact_sensitive_headers = true,
                    sensitive_header_patterns = new[] { "authorization", "cookie", "x-api-key" }
                }
            };

            var content = new StringContent(
                JsonSerializer.Serialize(twinPayload),
                Encoding.UTF8,
                "application/json");

            using var response = await client.PostAsync("/api/v1/twin/sessions", content, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            var twinId = result.GetProperty("twin_id").GetString()
                ?? throw new InvalidOperationException("Digital twin endpoint did not return a twin_id.");
            var twinEndpoint = result.TryGetProperty("twin_endpoint", out var te)
                ? te.GetString() ?? endpoint : endpoint;

            if (twinMode == "shadow" && !string.IsNullOrEmpty(productionEndpoint))
            {
                var mirrorResponse = await client.PostAsync(
                    $"/api/v1/twin/{twinId}/start-mirror", null, ct);
                mirrorResponse.EnsureSuccessStatusCode();
            }

            client.DefaultRequestHeaders.Remove("X-Twin-Id");
            client.DefaultRequestHeaders.Add("X-Twin-Id", twinId);
            client.DefaultRequestHeaders.Remove("X-Twin-Mode");
            client.DefaultRequestHeaders.Add("X-Twin-Mode", twinMode);

            var info = new Dictionary<string, object>
            {
                ["endpoint"] = endpoint,
                ["twin_id"] = twinId,
                ["twin_mode"] = twinMode,
                ["twin_endpoint"] = twinEndpoint,
                ["production_endpoint"] = productionEndpoint,
                ["drift_detection_enabled"] = enableDriftDetection,
                ["drift_threshold_percent"] = driftThresholdPercent,
                ["replay_speed"] = replaySpeedMultiplier,
                ["recording_id"] = recordingId,
                ["connected_at"] = DateTimeOffset.UtcNow
            };

            return new DefaultConnectionHandle(client, info, $"twin-{twinId}");
        }

        /// <inheritdoc/>
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var client = handle.GetConnection<HttpClient>();
            var twinId = handle.ConnectionInfo["twin_id"]?.ToString();

            using var response = await client.GetAsync($"/api/v1/twin/{twinId}/status", ct);
            if (!response.IsSuccessStatusCode) return false;

            var status = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            return status.TryGetProperty("active", out var active) && active.GetBoolean();
        }

        /// <inheritdoc/>
        protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var client = handle.GetConnection<HttpClient>();
            var twinId = handle.ConnectionInfo["twin_id"]?.ToString();
            var twinMode = handle.ConnectionInfo.GetValueOrDefault("twin_mode", "shadow")?.ToString();

            try
            {
                if (twinMode == "shadow")
                {
                    await client.PostAsync($"/api/v1/twin/{twinId}/stop-mirror", null, ct);
                }

                var statsResponse = await client.GetAsync($"/api/v1/twin/{twinId}/stats", ct);
                if (statsResponse.IsSuccessStatusCode)
                {
                    await client.PostAsync($"/api/v1/twin/{twinId}/finalize", null, ct);
                }

                await client.DeleteAsync($"/api/v1/twin/{twinId}", ct);
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
            var twinId = handle.ConnectionInfo["twin_id"]?.ToString();

            using var response = await client.GetAsync($"/api/v1/twin/{twinId}/health", ct);
            sw.Stop();

            if (!response.IsSuccessStatusCode)
            {
                return new ConnectionHealth(
                    IsHealthy: false,
                    StatusMessage: "Digital twin health check failed",
                    Latency: sw.Elapsed,
                    CheckedAt: DateTimeOffset.UtcNow);
            }

            var healthData = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            var isActive = healthData.TryGetProperty("active", out var a) && a.GetBoolean();
            var driftPercent = healthData.TryGetProperty("drift_percent", out var dp) ? dp.GetDouble() : 0;
            var requestsCaptured = healthData.TryGetProperty("requests_captured", out var rc) ? rc.GetInt64() : 0;
            var twinMode = handle.ConnectionInfo.GetValueOrDefault("twin_mode", "unknown");

            var driftThreshold = handle.ConnectionInfo.TryGetValue("drift_threshold_percent", out var dt)
                ? Convert.ToDouble(dt) : 5.0;

            return new ConnectionHealth(
                IsHealthy: isActive && driftPercent <= driftThreshold,
                StatusMessage: isActive
                    ? $"Twin ({twinMode}) active, drift: {driftPercent:F1}%, captured: {requestsCaptured}"
                    : "Twin inactive",
                Latency: sw.Elapsed,
                CheckedAt: DateTimeOffset.UtcNow,
                Details: new Dictionary<string, object>
                {
                    ["active"] = isActive,
                    ["twin_mode"] = twinMode,
                    ["drift_percent"] = driftPercent,
                    ["drift_within_threshold"] = driftPercent <= driftThreshold,
                    ["requests_captured"] = requestsCaptured
                });
        }

        /// <summary>
        /// Validates the twin mode configuration and required parameters.
        /// </summary>
        private static void ValidateTwinMode(string twinMode, string productionEndpoint, string recordingId)
        {
            switch (twinMode)
            {
                case "shadow":
                    if (string.IsNullOrEmpty(productionEndpoint))
                        throw new ArgumentException(
                            "production_endpoint property is required for shadow twin mode.");
                    break;
                case "replay":
                    if (string.IsNullOrEmpty(recordingId))
                        throw new ArgumentException(
                            "recording_id property is required for replay twin mode.");
                    break;
                case "record":
                case "simulation":
                    break;
                default:
                    throw new ArgumentException(
                        $"Unsupported twin mode: {twinMode}. Supported: shadow, replay, record, simulation.");
            }
        }
    }
}
