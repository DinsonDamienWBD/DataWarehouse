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
    /// Automatic schema evolution tracker that detects DDL changes across connected data sources,
    /// maintains a version-controlled schema registry, and provides compatibility analysis
    /// between schema versions for safe migration planning.
    /// </summary>
    /// <remarks>
    /// The strategy monitors for:
    /// <list type="bullet">
    ///   <item>Column additions, removals, and type changes via DDL event streams</item>
    ///   <item>Index and constraint modifications</item>
    ///   <item>Table and view creation/deletion</item>
    ///   <item>Schema compatibility classification (backward, forward, full, none)</item>
    ///   <item>Automatic schema diff generation with migration suggestions</item>
    /// </list>
    /// </remarks>
    public class SchemaEvolutionTrackerStrategy : ConnectionStrategyBase
    {
        /// <inheritdoc/>
        public override string StrategyId => "innovation-schema-evolution";

        /// <inheritdoc/>
        public override string DisplayName => "Schema Evolution Tracker";

        /// <inheritdoc/>
        public override ConnectorCategory Category => ConnectorCategory.Protocol;

        /// <inheritdoc/>
        public override ConnectionStrategyCapabilities Capabilities => new(
            SupportsPooling: true,
            SupportsStreaming: true,
            SupportsSchemaDiscovery: true,
            SupportsChangeDataCapture: true,
            SupportsSsl: true,
            SupportsHealthCheck: true,
            SupportsConnectionTesting: true,
            MaxConcurrentConnections: 50
        );

        /// <inheritdoc/>
        public override string SemanticDescription =>
            "Automatic schema evolution tracker that detects DDL changes across data sources, " +
            "maintains a versioned schema registry, and provides compatibility analysis for migrations";

        /// <inheritdoc/>
        public override string[] Tags => ["schema", "evolution", "ddl", "migration", "registry", "compatibility", "tracking"];

        /// <summary>
        /// Initializes a new instance of <see cref="SchemaEvolutionTrackerStrategy"/>.
        /// </summary>
        /// <param name="logger">Optional logger for diagnostics.</param>
        public SchemaEvolutionTrackerStrategy(ILogger? logger = null) : base(logger) { }

        /// <inheritdoc/>
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var endpoint = config.ConnectionString
                ?? throw new ArgumentException("Schema tracker endpoint URL is required in ConnectionString.");

            var registryEndpoint = GetConfiguration<string>(config, "registry_endpoint", $"{endpoint}/registry");
            var monitoredSchemas = GetConfiguration<string>(config, "monitored_schemas", "public");
            var compatibilityMode = GetConfiguration<string>(config, "compatibility_mode", "backward");
            var pollIntervalSec = GetConfiguration<int>(config, "poll_interval_seconds", 30);
            var enableDdlCapture = GetConfiguration<bool>(config, "enable_ddl_capture", true);
            var snapshotOnConnect = GetConfiguration<bool>(config, "snapshot_on_connect", true);

            var handler = new SocketsHttpHandler
            {
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(10),
                MaxConnectionsPerServer = config.PoolSize
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

            var registrationPayload = new
            {
                monitored_schemas = monitoredSchemas.Split(',', StringSplitOptions.RemoveEmptyEntries),
                compatibility_mode = compatibilityMode,
                poll_interval_seconds = pollIntervalSec,
                enable_ddl_capture = enableDdlCapture,
                snapshot_on_connect = snapshotOnConnect,
                notification_settings = new
                {
                    on_breaking_change = true,
                    on_new_column = true,
                    on_dropped_column = true,
                    on_type_change = true,
                    on_index_change = GetConfiguration<bool>(config, "notify_index_changes", false)
                }
            };

            var content = new StringContent(
                JsonSerializer.Serialize(registrationPayload),
                Encoding.UTF8,
                "application/json");

            var response = await client.PostAsync("/api/v1/schema/trackers", content, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            var trackerId = result.GetProperty("tracker_id").GetString()
                ?? throw new InvalidOperationException("Schema tracker endpoint did not return a tracker_id.");

            int schemaVersion = 0;
            int tableCount = 0;

            if (snapshotOnConnect)
            {
                var snapshotResponse = await client.PostAsync(
                    $"/api/v1/schema/trackers/{trackerId}/snapshot", null, ct);

                if (snapshotResponse.IsSuccessStatusCode)
                {
                    var snapshotResult = await snapshotResponse.Content.ReadFromJsonAsync<JsonElement>(ct);
                    schemaVersion = snapshotResult.TryGetProperty("version", out var v) ? v.GetInt32() : 0;
                    tableCount = snapshotResult.TryGetProperty("table_count", out var tc) ? tc.GetInt32() : 0;
                }
            }

            client.DefaultRequestHeaders.Add("X-Schema-Tracker", trackerId);

            var info = new Dictionary<string, object>
            {
                ["endpoint"] = endpoint,
                ["registry_endpoint"] = registryEndpoint,
                ["tracker_id"] = trackerId,
                ["monitored_schemas"] = monitoredSchemas,
                ["compatibility_mode"] = compatibilityMode,
                ["schema_version"] = schemaVersion,
                ["table_count"] = tableCount,
                ["ddl_capture_enabled"] = enableDdlCapture,
                ["poll_interval_seconds"] = pollIntervalSec,
                ["connected_at"] = DateTimeOffset.UtcNow
            };

            return new DefaultConnectionHandle(client, info, $"schema-{trackerId}");
        }

        /// <inheritdoc/>
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var client = handle.GetConnection<HttpClient>();
            var trackerId = handle.ConnectionInfo["tracker_id"]?.ToString();

            var response = await client.GetAsync($"/api/v1/schema/trackers/{trackerId}/status", ct);
            if (!response.IsSuccessStatusCode) return false;

            var status = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            return status.TryGetProperty("active", out var active) && active.GetBoolean();
        }

        /// <inheritdoc/>
        protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var client = handle.GetConnection<HttpClient>();
            var trackerId = handle.ConnectionInfo["tracker_id"]?.ToString();

            try
            {
                await client.PostAsync($"/api/v1/schema/trackers/{trackerId}/snapshot", null, ct);
                await client.DeleteAsync($"/api/v1/schema/trackers/{trackerId}", ct);
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
            var trackerId = handle.ConnectionInfo["tracker_id"]?.ToString();

            var response = await client.GetAsync($"/api/v1/schema/trackers/{trackerId}/health", ct);
            sw.Stop();

            if (!response.IsSuccessStatusCode)
            {
                return new ConnectionHealth(
                    IsHealthy: false,
                    StatusMessage: "Schema tracker health check failed",
                    Latency: sw.Elapsed,
                    CheckedAt: DateTimeOffset.UtcNow);
            }

            var healthData = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            var isTracking = healthData.TryGetProperty("tracking", out var t) && t.GetBoolean();
            var pendingChanges = healthData.TryGetProperty("pending_changes", out var pc) ? pc.GetInt32() : 0;
            var lastDdlEvent = healthData.TryGetProperty("last_ddl_event", out var lde) ? lde.GetString() : null;
            var currentVersion = healthData.TryGetProperty("current_version", out var cv) ? cv.GetInt32() : 0;

            return new ConnectionHealth(
                IsHealthy: isTracking,
                StatusMessage: isTracking
                    ? $"Schema tracker active, version {currentVersion}, {pendingChanges} pending changes"
                    : "Schema tracker inactive",
                Latency: sw.Elapsed,
                CheckedAt: DateTimeOffset.UtcNow,
                Details: new Dictionary<string, object>
                {
                    ["tracking"] = isTracking,
                    ["current_version"] = currentVersion,
                    ["pending_changes"] = pendingChanges,
                    ["last_ddl_event"] = lastDdlEvent ?? "none",
                    ["compatibility_mode"] = handle.ConnectionInfo.GetValueOrDefault("compatibility_mode", "unknown")
                });
        }
    }
}
