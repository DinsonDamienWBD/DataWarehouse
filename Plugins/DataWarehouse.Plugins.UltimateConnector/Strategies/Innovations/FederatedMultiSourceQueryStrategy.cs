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
    /// Federated multi-source query strategy that executes queries across heterogeneous data
    /// sources with cost-based optimization. Connects to a federation coordinator that manages
    /// query planning, pushdown, and result aggregation across databases, APIs, and files.
    /// </summary>
    /// <remarks>
    /// The strategy supports:
    /// <list type="bullet">
    ///   <item>Cross-source JOIN operations via the federation coordinator</item>
    ///   <item>Cost-based query planning with statistics from each source</item>
    ///   <item>Predicate pushdown to minimize data transfer</item>
    ///   <item>Parallel source access with configurable concurrency</item>
    ///   <item>Schema mapping and type coercion between heterogeneous sources</item>
    /// </list>
    /// </remarks>
    public class FederatedMultiSourceQueryStrategy : ConnectionStrategyBase
    {
        /// <inheritdoc/>
        public override string StrategyId => "innovation-federated-query";

        /// <inheritdoc/>
        public override string DisplayName => "Federated Multi-Source Query";

        /// <inheritdoc/>
        public override ConnectorCategory Category => ConnectorCategory.Protocol;

        /// <inheritdoc/>
        public override ConnectionStrategyCapabilities Capabilities => new(
            SupportsPooling: true,
            SupportsStreaming: true,
            SupportsTransactions: false,
            SupportsBulkOperations: true,
            SupportsSchemaDiscovery: true,
            SupportsSsl: true,
            SupportsCompression: true,
            SupportsHealthCheck: true,
            SupportsConnectionTesting: true,
            MaxConcurrentConnections: 50
        );

        /// <inheritdoc/>
        public override string SemanticDescription =>
            "Federated query engine that executes queries across heterogeneous data sources " +
            "(databases, APIs, files) with cost-based optimization and predicate pushdown";

        /// <inheritdoc/>
        public override string[] Tags => ["federation", "multi-source", "query", "cross-database", "pushdown", "optimization"];

        /// <summary>
        /// Initializes a new instance of <see cref="FederatedMultiSourceQueryStrategy"/>.
        /// </summary>
        /// <param name="logger">Optional logger for diagnostics.</param>
        public FederatedMultiSourceQueryStrategy(ILogger? logger = null) : base(logger) { }

        /// <inheritdoc/>
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var coordinatorUrl = config.ConnectionString
                ?? throw new ArgumentException("Federation coordinator URL is required in ConnectionString.");

            var maxParallelSources = GetConfiguration<int>(config, "max_parallel_sources", 8);
            var queryTimeoutSec = GetConfiguration<int>(config, "query_timeout_seconds", 120);
            var enablePushdown = GetConfiguration<bool>(config, "enable_pushdown", true);
            var costModel = GetConfiguration<string>(config, "cost_model", "adaptive");
            var sourceCatalog = GetConfiguration<string>(config, "source_catalog", "default");

            var handler = new SocketsHttpHandler
            {
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(10),
                MaxConnectionsPerServer = maxParallelSources * 2,
                EnableMultipleHttp2Connections = true
            };

            var client = new HttpClient(handler)
            {
                BaseAddress = new Uri(coordinatorUrl),
                Timeout = TimeSpan.FromSeconds(queryTimeoutSec),
                DefaultRequestVersion = new Version(2, 0)
            };

            if (!string.IsNullOrEmpty(config.AuthCredential))
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", config.AuthCredential);

            var registrationPayload = new
            {
                catalog = sourceCatalog,
                settings = new
                {
                    max_parallel_sources = maxParallelSources,
                    enable_predicate_pushdown = enablePushdown,
                    cost_model = costModel,
                    query_timeout_seconds = queryTimeoutSec,
                    result_spill_threshold_mb = GetConfiguration<int>(config, "spill_threshold_mb", 512),
                    enable_result_caching = GetConfiguration<bool>(config, "enable_caching", true)
                }
            };

            var content = new StringContent(
                JsonSerializer.Serialize(registrationPayload),
                Encoding.UTF8,
                "application/json");

            using var response = await client.PostAsync("/api/v1/federation/sessions", content, ct);
            response.EnsureSuccessStatusCode();

            var sessionResult = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            var sessionId = sessionResult.GetProperty("session_id").GetString()
                ?? throw new InvalidOperationException("Federation coordinator did not return a session_id.");

            var sourcesResponse = await client.GetAsync($"/api/v1/federation/sessions/{sessionId}/sources", ct);
            sourcesResponse.EnsureSuccessStatusCode();
            var sourcesData = await sourcesResponse.Content.ReadFromJsonAsync<JsonElement>(ct);

            var sourceCount = sourcesData.TryGetProperty("sources", out var sources)
                ? sources.GetArrayLength()
                : 0;

            client.DefaultRequestHeaders.Remove("X-Federation-Session");
            client.DefaultRequestHeaders.Add("X-Federation-Session", sessionId);

            var info = new Dictionary<string, object>
            {
                ["coordinator_url"] = coordinatorUrl,
                ["session_id"] = sessionId,
                ["source_count"] = sourceCount,
                ["cost_model"] = costModel,
                ["max_parallel_sources"] = maxParallelSources,
                ["pushdown_enabled"] = enablePushdown,
                ["catalog"] = sourceCatalog,
                ["connected_at"] = DateTimeOffset.UtcNow
            };

            return new DefaultConnectionHandle(client, info, $"fed-{sessionId}");
        }

        /// <inheritdoc/>
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var client = handle.GetConnection<HttpClient>();
            var sessionId = handle.ConnectionInfo["session_id"]?.ToString();

            using var response = await client.GetAsync($"/api/v1/federation/sessions/{sessionId}/status", ct);
            if (!response.IsSuccessStatusCode) return false;

            var status = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            var state = status.GetProperty("state").GetString();
            return state is "active" or "idle";
        }

        /// <inheritdoc/>
        protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var client = handle.GetConnection<HttpClient>();
            var sessionId = handle.ConnectionInfo["session_id"]?.ToString();

            try
            {
                await client.DeleteAsync($"/api/v1/federation/sessions/{sessionId}", ct);
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

            using var response = await client.GetAsync($"/api/v1/federation/sessions/{sessionId}/health", ct);
            sw.Stop();

            if (!response.IsSuccessStatusCode)
            {
                return new ConnectionHealth(
                    IsHealthy: false,
                    StatusMessage: $"Federation session health check returned {response.StatusCode}",
                    Latency: sw.Elapsed,
                    CheckedAt: DateTimeOffset.UtcNow);
            }

            var healthData = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            var healthySources = healthData.TryGetProperty("healthy_sources", out var hs) ? hs.GetInt32() : 0;
            var totalSources = healthData.TryGetProperty("total_sources", out var ts) ? ts.GetInt32() : 0;
            var queriesExecuted = healthData.TryGetProperty("queries_executed", out var qe) ? qe.GetInt64() : 0;

            return new ConnectionHealth(
                IsHealthy: healthySources > 0,
                StatusMessage: $"Federation session active: {healthySources}/{totalSources} sources healthy, {queriesExecuted} queries executed",
                Latency: sw.Elapsed,
                CheckedAt: DateTimeOffset.UtcNow,
                Details: new Dictionary<string, object>
                {
                    ["healthy_sources"] = healthySources,
                    ["total_sources"] = totalSources,
                    ["queries_executed"] = queriesExecuted,
                    ["session_id"] = sessionId ?? ""
                });
        }
    }
}
