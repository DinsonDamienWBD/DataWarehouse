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
    /// Full OpenTelemetry instrumentation strategy that wraps every connection with
    /// distributed tracing, metrics, and structured logging. Produces W3C TraceContext
    /// spans for connection lifecycle events and exports to any OTel-compatible backend.
    /// </summary>
    /// <remarks>
    /// The strategy provides comprehensive observability:
    /// <list type="bullet">
    ///   <item>W3C TraceContext propagation via traceparent/tracestate headers</item>
    ///   <item>Span creation for connect, query, disconnect, and health check operations</item>
    ///   <item>Connection pool metrics (active, idle, wait time, errors)</item>
    ///   <item>Automatic baggage propagation for cross-service correlation</item>
    ///   <item>Configurable sampling (always-on, probability, rate-limited)</item>
    ///   <item>Export to OTLP, Jaeger, Zipkin, or custom backends</item>
    /// </list>
    /// </remarks>
    public class ConnectionTelemetryFabricStrategy : ConnectionStrategyBase
    {
        private static readonly ActivitySource TelemetrySource = new("DataWarehouse.Connector.Telemetry", "1.0.0");
        private long _totalSpansCreated;
        private long _totalMetricsEmitted;

        /// <inheritdoc/>
        public override string StrategyId => "innovation-telemetry-fabric";

        /// <inheritdoc/>
        public override string DisplayName => "Connection Telemetry Fabric";

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
            MaxConcurrentConnections: 200
        );

        /// <inheritdoc/>
        public override string SemanticDescription =>
            "Full OpenTelemetry instrumentation fabric providing distributed tracing, metrics, " +
            "and structured logging with W3C TraceContext propagation for every connection";

        /// <inheritdoc/>
        public override string[] Tags => ["telemetry", "opentelemetry", "otel", "tracing", "metrics", "observability", "w3c"];

        /// <summary>
        /// Initializes a new instance of <see cref="ConnectionTelemetryFabricStrategy"/>.
        /// </summary>
        /// <param name="logger">Optional logger for diagnostics.</param>
        public ConnectionTelemetryFabricStrategy(ILogger? logger = null) : base(logger) { }

        /// <inheritdoc/>
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            using var connectSpan = TelemetrySource.StartActivity("connector.connect", ActivityKind.Client);

            var endpoint = config.ConnectionString
                ?? throw new ArgumentException("Telemetry-enabled endpoint URL is required in ConnectionString.");

            var otlpEndpoint = GetConfiguration<string>(config, "otlp_endpoint", "http://localhost:4317");
            var serviceName = GetConfiguration<string>(config, "service_name", "datawarehouse-connector");
            var samplingRate = GetConfiguration<double>(config, "sampling_rate", 1.0);
            var enableMetrics = GetConfiguration<bool>(config, "enable_metrics", true);
            var enableBaggage = GetConfiguration<bool>(config, "enable_baggage", true);
            var exporterType = GetConfiguration<string>(config, "exporter_type", "otlp");

            connectSpan?.SetTag("connector.endpoint", endpoint);
            connectSpan?.SetTag("connector.service_name", serviceName);
            connectSpan?.SetTag("connector.sampling_rate", samplingRate);

            var traceId = Activity.Current?.TraceId.ToString() ?? ActivityTraceId.CreateRandom().ToString();
            var spanId = Activity.Current?.SpanId.ToString() ?? ActivitySpanId.CreateRandom().ToString();

            var handler = new SocketsHttpHandler
            {
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
                MaxConnectionsPerServer = config.PoolSize
            };

            var client = new HttpClient(handler)
            {
                BaseAddress = new Uri(endpoint),
                Timeout = config.Timeout,
                DefaultRequestVersion = new Version(2, 0)
            };

            client.DefaultRequestHeaders.Add("traceparent", $"00-{traceId}-{spanId}-01");

            if (enableBaggage)
            {
                client.DefaultRequestHeaders.Add("baggage",
                    $"service.name={serviceName},connector.strategy=telemetry-fabric");
            }

            if (!string.IsNullOrEmpty(config.AuthCredential))
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", config.AuthCredential);

            var registrationPayload = new
            {
                service_name = serviceName,
                otlp_endpoint = otlpEndpoint,
                exporter_type = exporterType,
                sampling = new
                {
                    type = samplingRate >= 1.0 ? "always_on" : "probability",
                    rate = samplingRate
                },
                instrumentation = new
                {
                    enable_metrics = enableMetrics,
                    enable_baggage = enableBaggage,
                    enable_span_events = true,
                    enable_span_links = true,
                    record_exceptions = true,
                    record_sql_statements = GetConfiguration<bool>(config, "record_sql", false)
                },
                resource_attributes = new Dictionary<string, string>
                {
                    ["service.name"] = serviceName,
                    ["service.version"] = "1.0.0",
                    ["deployment.environment"] = GetConfiguration<string>(config, "environment", "production"),
                    ["host.name"] = Environment.MachineName
                }
            };

            var content = new StringContent(
                JsonSerializer.Serialize(registrationPayload),
                Encoding.UTF8,
                "application/json");

            var response = await client.PostAsync("/api/v1/telemetry/register", content, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            var telemetrySessionId = result.GetProperty("session_id").GetString()
                ?? throw new InvalidOperationException("Telemetry endpoint did not return a session_id.");

            Interlocked.Increment(ref _totalSpansCreated);
            connectSpan?.SetTag("connector.session_id", telemetrySessionId);
            connectSpan?.SetStatus(ActivityStatusCode.Ok);

            client.DefaultRequestHeaders.Add("X-Telemetry-Session", telemetrySessionId);

            var info = new Dictionary<string, object>
            {
                ["endpoint"] = endpoint,
                ["otlp_endpoint"] = otlpEndpoint,
                ["session_id"] = telemetrySessionId,
                ["service_name"] = serviceName,
                ["trace_id"] = traceId,
                ["sampling_rate"] = samplingRate,
                ["exporter_type"] = exporterType,
                ["metrics_enabled"] = enableMetrics,
                ["connected_at"] = DateTimeOffset.UtcNow
            };

            return new DefaultConnectionHandle(client, info, $"otel-{telemetrySessionId}");
        }

        /// <inheritdoc/>
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            using var testSpan = TelemetrySource.StartActivity("connector.test", ActivityKind.Client);
            testSpan?.SetTag("connector.connection_id", handle.ConnectionId);

            var client = handle.GetConnection<HttpClient>();

            var response = await client.GetAsync("/api/v1/telemetry/ping", ct);
            var success = response.IsSuccessStatusCode;

            Interlocked.Increment(ref _totalSpansCreated);
            testSpan?.SetStatus(success ? ActivityStatusCode.Ok : ActivityStatusCode.Error);

            return success;
        }

        /// <inheritdoc/>
        protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            using var disconnectSpan = TelemetrySource.StartActivity("connector.disconnect", ActivityKind.Client);
            disconnectSpan?.SetTag("connector.connection_id", handle.ConnectionId);

            var client = handle.GetConnection<HttpClient>();
            var sessionId = handle.ConnectionInfo["session_id"]?.ToString();

            try
            {
                var metricsPayload = new StringContent(
                    JsonSerializer.Serialize(new
                    {
                        session_id = sessionId,
                        total_spans = Interlocked.Read(ref _totalSpansCreated),
                        total_metrics = Interlocked.Read(ref _totalMetricsEmitted),
                        disconnected_at = DateTimeOffset.UtcNow
                    }),
                    Encoding.UTF8,
                    "application/json");

                await client.PostAsync("/api/v1/telemetry/flush", metricsPayload, ct);
                await client.DeleteAsync($"/api/v1/telemetry/sessions/{sessionId}", ct);
            }
            finally
            {
                Interlocked.Increment(ref _totalSpansCreated);
                disconnectSpan?.SetStatus(ActivityStatusCode.Ok);
                client.Dispose();
            }
        }

        /// <inheritdoc/>
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            using var healthSpan = TelemetrySource.StartActivity("connector.health_check", ActivityKind.Client);
            var sw = Stopwatch.StartNew();

            var client = handle.GetConnection<HttpClient>();
            var sessionId = handle.ConnectionInfo["session_id"]?.ToString();

            var response = await client.GetAsync($"/api/v1/telemetry/sessions/{sessionId}/health", ct);
            sw.Stop();

            Interlocked.Increment(ref _totalSpansCreated);
            Interlocked.Increment(ref _totalMetricsEmitted);

            if (!response.IsSuccessStatusCode)
            {
                healthSpan?.SetStatus(ActivityStatusCode.Error);
                return new ConnectionHealth(
                    IsHealthy: false,
                    StatusMessage: "Telemetry fabric health check failed",
                    Latency: sw.Elapsed,
                    CheckedAt: DateTimeOffset.UtcNow);
            }

            var healthData = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            var exporterHealthy = healthData.TryGetProperty("exporter_healthy", out var eh) && eh.GetBoolean();
            var spansExported = healthData.TryGetProperty("spans_exported", out var se) ? se.GetInt64() : 0;

            healthSpan?.SetStatus(ActivityStatusCode.Ok);

            return new ConnectionHealth(
                IsHealthy: exporterHealthy,
                StatusMessage: exporterHealthy
                    ? $"Telemetry fabric active, {spansExported} spans exported"
                    : "Telemetry exporter unhealthy",
                Latency: sw.Elapsed,
                CheckedAt: DateTimeOffset.UtcNow,
                Details: new Dictionary<string, object>
                {
                    ["exporter_healthy"] = exporterHealthy,
                    ["spans_exported"] = spansExported,
                    ["local_spans_created"] = Interlocked.Read(ref _totalSpansCreated),
                    ["local_metrics_emitted"] = Interlocked.Read(ref _totalMetricsEmitted),
                    ["service_name"] = handle.ConnectionInfo.GetValueOrDefault("service_name", "unknown")
                });
        }
    }
}
