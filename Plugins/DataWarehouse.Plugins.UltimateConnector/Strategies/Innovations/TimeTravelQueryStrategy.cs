using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
    /// Point-in-time database connection strategy that leverages native temporal features
    /// (SQL:2011 temporal tables, Snowflake Time Travel, BigQuery snapshot decorators,
    /// Delta Lake time travel) to query data as it existed at any past moment.
    /// </summary>
    /// <remarks>
    /// Supports multiple temporal query modes:
    /// <list type="bullet">
    ///   <item>AS OF TIMESTAMP: Query data at a specific point in time</item>
    ///   <item>BETWEEN: Query changes within a time range</item>
    ///   <item>VERSION: Query specific data versions (Delta Lake, Iceberg)</item>
    ///   <item>SYSTEM_TIME: SQL:2011 standard system-versioned temporal queries</item>
    ///   <item>SNAPSHOT: Consistent snapshot reads at a given LSN or transaction ID</item>
    /// </list>
    /// </remarks>
    public class TimeTravelQueryStrategy : ConnectionStrategyBase
    {
        private static readonly Dictionary<string, TemporalEngineConfig> EngineConfigs = new(StringComparer.OrdinalIgnoreCase)
        {
            ["snowflake"] = new("AT(TIMESTAMP => '{0}')", "BEFORE(TIMESTAMP => '{0}')", 90, "timestamp_ltz"),
            ["bigquery"] = new("FOR SYSTEM_TIME AS OF TIMESTAMP('{0}')", "FOR SYSTEM_TIME AS OF TIMESTAMP('{0}')", 7, "timestamp"),
            ["delta"] = new("VERSION AS OF {0}", "TIMESTAMP AS OF '{0}'", 30, "version_or_timestamp"),
            ["iceberg"] = new("FOR SYSTEM_TIME AS OF TIMESTAMP '{0}'", "FOR SYSTEM_VERSION AS OF {0}", 365, "snapshot_id"),
            ["postgresql"] = new("AS OF SYSTEM_TIME '{0}'", "FOR SYSTEM_TIME BETWEEN '{0}' AND '{1}'", -1, "system_time"),
            ["sqlserver"] = new("FOR SYSTEM_TIME AS OF '{0}'", "FOR SYSTEM_TIME BETWEEN '{0}' AND '{1}'", -1, "datetime2"),
            ["mysql"] = new("AS OF TIMESTAMP '{0}'", "BETWEEN TIMESTAMP '{0}' AND TIMESTAMP '{1}'", -1, "datetime")
        };

        /// <inheritdoc/>
        public override string StrategyId => "innovation-time-travel";

        /// <inheritdoc/>
        public override string DisplayName => "Time Travel Query";

        /// <inheritdoc/>
        public override ConnectorCategory Category => ConnectorCategory.Protocol;

        /// <inheritdoc/>
        public override ConnectionStrategyCapabilities Capabilities => new(
            SupportsPooling: true,
            SupportsStreaming: true,
            SupportsTransactions: true,
            SupportsSchemaDiscovery: true,
            SupportsSsl: true,
            SupportsHealthCheck: true,
            SupportsConnectionTesting: true,
            MaxConcurrentConnections: 50
        );

        /// <inheritdoc/>
        public override string SemanticDescription =>
            "Point-in-time database connections using native temporal features (Snowflake Time Travel, " +
            "BigQuery snapshots, Delta Lake versioning, SQL:2011 temporal tables) for historical queries";

        /// <inheritdoc/>
        public override string[] Tags => ["time-travel", "temporal", "point-in-time", "snapshot", "versioning", "historical"];

        /// <summary>
        /// Initializes a new instance of <see cref="TimeTravelQueryStrategy"/>.
        /// </summary>
        /// <param name="logger">Optional logger for diagnostics.</param>
        public TimeTravelQueryStrategy(ILogger? logger = null) : base(logger) { }

        /// <inheritdoc/>
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var endpoint = config.ConnectionString
                ?? throw new ArgumentException("Time travel endpoint URL is required in ConnectionString.");

            var engine = GetConfiguration<string>(config, "temporal_engine", "snowflake");
            var travelTimestamp = GetConfiguration<string>(config, "travel_timestamp", "");
            var travelMode = GetConfiguration<string>(config, "travel_mode", "as_of");
            var rangeEnd = GetConfiguration<string>(config, "range_end", "");
            var version = GetConfiguration<string>(config, "version", "");

            if (!EngineConfigs.TryGetValue(engine, out var engineConfig))
                throw new ArgumentException(
                    $"Unsupported temporal engine: {engine}. Supported: {string.Join(", ", EngineConfigs.Keys)}");

            var resolvedTimestamp = ResolveTimestamp(travelTimestamp);
            ValidateRetentionWindow(engine, engineConfig, resolvedTimestamp);

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

            if (!string.IsNullOrEmpty(config.AuthCredential))
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", config.AuthCredential);

            var sessionPayload = new
            {
                engine,
                travel_mode = travelMode,
                travel_timestamp = resolvedTimestamp?.ToString("O"),
                range_end = string.IsNullOrEmpty(rangeEnd) ? null : rangeEnd,
                version = string.IsNullOrEmpty(version) ? null : version,
                temporal_clause = BuildTemporalClause(engineConfig, travelMode, resolvedTimestamp, rangeEnd, version),
                read_only = true,
                consistent_snapshot = true
            };

            var content = new StringContent(
                JsonSerializer.Serialize(sessionPayload),
                Encoding.UTF8,
                "application/json");

            var response = await client.PostAsync("/api/v1/temporal/sessions", content, ct);
            response.EnsureSuccessStatusCode();

            var sessionResult = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            var sessionId = sessionResult.GetProperty("session_id").GetString()
                ?? throw new InvalidOperationException("Temporal endpoint did not return a session_id.");

            var snapshotLsn = sessionResult.TryGetProperty("snapshot_lsn", out var lsn) ? lsn.GetString() : null;

            client.DefaultRequestHeaders.Add("X-Temporal-Session", sessionId);
            if (resolvedTimestamp.HasValue)
                client.DefaultRequestHeaders.Add("X-Temporal-Timestamp", resolvedTimestamp.Value.ToString("O"));

            var info = new Dictionary<string, object>
            {
                ["endpoint"] = endpoint,
                ["session_id"] = sessionId,
                ["engine"] = engine,
                ["travel_mode"] = travelMode,
                ["travel_timestamp"] = resolvedTimestamp?.ToString("O") ?? "current",
                ["temporal_clause"] = BuildTemporalClause(engineConfig, travelMode, resolvedTimestamp, rangeEnd, version),
                ["retention_days"] = engineConfig.RetentionDays,
                ["connected_at"] = DateTimeOffset.UtcNow
            };

            if (!string.IsNullOrEmpty(snapshotLsn))
                info["snapshot_lsn"] = snapshotLsn;

            return new DefaultConnectionHandle(client, info, $"tt-{engine}-{sessionId}");
        }

        /// <inheritdoc/>
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var client = handle.GetConnection<HttpClient>();
            var sessionId = handle.ConnectionInfo["session_id"]?.ToString();

            var response = await client.GetAsync($"/api/v1/temporal/sessions/{sessionId}/status", ct);
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
                await client.DeleteAsync($"/api/v1/temporal/sessions/{sessionId}", ct);
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

            var response = await client.GetAsync($"/api/v1/temporal/sessions/{sessionId}/health", ct);
            sw.Stop();

            if (!response.IsSuccessStatusCode)
            {
                return new ConnectionHealth(
                    IsHealthy: false,
                    StatusMessage: "Temporal session health check failed",
                    Latency: sw.Elapsed,
                    CheckedAt: DateTimeOffset.UtcNow);
            }

            var healthData = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            var snapshotValid = healthData.TryGetProperty("snapshot_valid", out var sv) && sv.GetBoolean();
            var engine = handle.ConnectionInfo.GetValueOrDefault("engine", "unknown");

            return new ConnectionHealth(
                IsHealthy: snapshotValid,
                StatusMessage: snapshotValid
                    ? $"Temporal session active on {engine}, snapshot consistent"
                    : $"Temporal snapshot may have been invalidated on {engine}",
                Latency: sw.Elapsed,
                CheckedAt: DateTimeOffset.UtcNow,
                Details: new Dictionary<string, object>
                {
                    ["engine"] = engine,
                    ["travel_timestamp"] = handle.ConnectionInfo.GetValueOrDefault("travel_timestamp", "current"),
                    ["snapshot_valid"] = snapshotValid
                });
        }

        /// <summary>
        /// Resolves the travel timestamp from various string formats or relative expressions.
        /// </summary>
        private static DateTimeOffset? ResolveTimestamp(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return null;

            if (DateTimeOffset.TryParse(input, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var parsed))
                return parsed;

            if (input.StartsWith("-") && input.Length > 1)
            {
                var unit = input[^1];
                if (int.TryParse(input[1..^1], out var amount))
                {
                    return unit switch
                    {
                        'm' => DateTimeOffset.UtcNow.AddMinutes(-amount),
                        'h' => DateTimeOffset.UtcNow.AddHours(-amount),
                        'd' => DateTimeOffset.UtcNow.AddDays(-amount),
                        _ => null
                    };
                }
            }

            return null;
        }

        /// <summary>
        /// Validates that the requested timestamp is within the engine's retention window.
        /// </summary>
        private static void ValidateRetentionWindow(
            string engine, TemporalEngineConfig config, DateTimeOffset? timestamp)
        {
            if (!timestamp.HasValue || config.RetentionDays < 0) return;

            var age = DateTimeOffset.UtcNow - timestamp.Value;
            if (age.TotalDays > config.RetentionDays)
                throw new InvalidOperationException(
                    $"Requested timestamp is {age.TotalDays:F1} days ago, but {engine} " +
                    $"only retains temporal data for {config.RetentionDays} days.");
        }

        /// <summary>
        /// Builds the appropriate temporal SQL clause for the engine and mode.
        /// </summary>
        private static string BuildTemporalClause(
            TemporalEngineConfig config, string mode, DateTimeOffset? timestamp, string rangeEnd, string version)
        {
            if (mode == "version" && !string.IsNullOrEmpty(version))
                return string.Format(config.AsOfTemplate, version);

            if (mode == "between" && timestamp.HasValue && !string.IsNullOrEmpty(rangeEnd))
                return string.Format(config.BetweenTemplate, timestamp.Value.ToString("O"), rangeEnd);

            if (timestamp.HasValue)
                return string.Format(config.AsOfTemplate, timestamp.Value.ToString("O"));

            return "";
        }

        private record TemporalEngineConfig(
            string AsOfTemplate,
            string BetweenTemplate,
            int RetentionDays,
            string TimestampType);
    }
}
