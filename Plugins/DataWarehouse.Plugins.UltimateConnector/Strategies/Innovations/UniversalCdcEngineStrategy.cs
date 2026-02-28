using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
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
    /// Universal Change Data Capture engine that auto-detects the database type from
    /// the connection string and configures the appropriate CDC method. Supports
    /// PostgreSQL WAL (logical replication), MySQL binlog, SQL Server Change Tracking,
    /// MongoDB oplog, and Oracle LogMiner.
    /// </summary>
    /// <remarks>
    /// The strategy communicates with a CDC coordinator endpoint that manages the
    /// replication slots, binlog readers, and change stream cursors. It negotiates
    /// the CDC method during the initial handshake, ensuring the most efficient
    /// capture mechanism is selected for the detected database engine.
    /// </remarks>
    public class UniversalCdcEngineStrategy : ConnectionStrategyBase
    {
        // Finding 1966: Replace brittle substring matching with structured detection.
        // "host=" is present in ALL ADO.NET connection strings, and "server=" is too generic.
        // Use scheme-prefix checks first (unambiguous), then key=value parsing for semi-colon formats.
        private static readonly Dictionary<string, string> UriSchemeSignatures = new(StringComparer.OrdinalIgnoreCase)
        {
            ["mongodb://"] = "mongodb",
            ["mongodb+srv://"] = "mongodb",
            ["mysql://"] = "mysql",
            ["postgresql://"] = "postgresql",
            ["postgres://"] = "postgresql",
            ["mssql://"] = "sqlserver",
            ["sqlserver://"] = "sqlserver"
        };

        private static readonly Dictionary<string, string> CdcMethods = new(StringComparer.OrdinalIgnoreCase)
        {
            ["postgresql"] = "wal_logical_replication",
            ["mysql"] = "binlog_streaming",
            ["sqlserver"] = "change_tracking",
            ["mongodb"] = "oplog_tailing",
            ["oracle"] = "logminer"
        };

        /// <inheritdoc/>
        public override string StrategyId => "innovation-universal-cdc";

        /// <inheritdoc/>
        public override string DisplayName => "Universal CDC Engine";

        /// <inheritdoc/>
        public override ConnectorCategory Category => ConnectorCategory.Protocol;

        /// <inheritdoc/>
        public override ConnectionStrategyCapabilities Capabilities => new(
            SupportsPooling: true,
            SupportsStreaming: true,
            SupportsChangeDataCapture: true,
            SupportsReconnection: true,
            SupportsHealthCheck: true,
            SupportsConnectionTesting: true,
            MaxConcurrentConnections: 50
        );

        /// <inheritdoc/>
        public override string SemanticDescription =>
            "Universal change data capture engine that auto-detects database type and configures " +
            "the optimal CDC method (WAL, binlog, change tracking, oplog, LogMiner) automatically";

        /// <inheritdoc/>
        public override string[] Tags => ["cdc", "change-data-capture", "replication", "wal", "binlog", "oplog", "streaming"];

        /// <summary>
        /// Initializes a new instance of <see cref="UniversalCdcEngineStrategy"/>.
        /// </summary>
        /// <param name="logger">Optional logger for diagnostics.</param>
        public UniversalCdcEngineStrategy(ILogger? logger = null) : base(logger) { }

        /// <inheritdoc/>
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var connectionString = config.ConnectionString
                ?? throw new ArgumentException("ConnectionString is required for CDC engine.");

            var cdcEndpoint = GetConfiguration<string>(config, "cdc_endpoint", "http://localhost:8089");
            var slotName = GetConfiguration<string>(config, "slot_name", $"dw_cdc_{Guid.NewGuid():N}");
            var publicationName = GetConfiguration<string>(config, "publication_name", "dw_publication");

            var detectedDb = DetectDatabaseType(connectionString);
            if (string.IsNullOrEmpty(detectedDb))
                throw new InvalidOperationException(
                    "Unable to auto-detect database type from connection string. " +
                    "Set 'db_type' property to one of: postgresql, mysql, sqlserver, mongodb, oracle.");

            var cdcMethod = CdcMethods.GetValueOrDefault(detectedDb, "generic_polling");

            var handler = new SocketsHttpHandler
            {
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
                KeepAlivePingDelay = TimeSpan.FromSeconds(30),
                KeepAlivePingTimeout = TimeSpan.FromSeconds(15),
                MaxConnectionsPerServer = config.PoolSize
            };

            var client = new HttpClient(handler)
            {
                BaseAddress = new Uri(cdcEndpoint),
                Timeout = config.Timeout
            };

            var registrationPayload = new
            {
                database_type = detectedDb,
                cdc_method = cdcMethod,
                connection_string = connectionString,
                slot_name = slotName,
                publication_name = publicationName,
                tables = GetConfiguration<string>(config, "tables", "*"),
                start_lsn = GetConfiguration<string>(config, "start_lsn", "latest"),
                batch_size = GetConfiguration<int>(config, "batch_size", 1000)
            };

            var content = new StringContent(
                JsonSerializer.Serialize(registrationPayload),
                Encoding.UTF8,
                "application/json");

            using var response = await client.PostAsync("/api/v1/cdc/register", content, ct);
            response.EnsureSuccessStatusCode();

            var registrationResult = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            var streamId = registrationResult.GetProperty("stream_id").GetString()
                ?? throw new InvalidOperationException("CDC coordinator did not return a stream_id.");

            var info = new Dictionary<string, object>
            {
                ["database_type"] = detectedDb,
                ["cdc_method"] = cdcMethod,
                ["stream_id"] = streamId,
                ["slot_name"] = slotName,
                ["cdc_endpoint"] = cdcEndpoint,
                ["connected_at"] = DateTimeOffset.UtcNow,
                ["publication_name"] = publicationName
            };

            return new DefaultConnectionHandle(client, info, $"cdc-{detectedDb}-{streamId}");
        }

        /// <inheritdoc/>
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var client = handle.GetConnection<HttpClient>();
            var streamId = handle.ConnectionInfo["stream_id"]?.ToString();

            using var response = await client.GetAsync($"/api/v1/cdc/streams/{streamId}/status", ct);
            if (!response.IsSuccessStatusCode) return false;

            var status = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            var state = status.GetProperty("state").GetString();
            return state is "active" or "streaming" or "idle";
        }

        /// <inheritdoc/>
        protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var client = handle.GetConnection<HttpClient>();
            var streamId = handle.ConnectionInfo["stream_id"]?.ToString();

            try
            {
                await client.DeleteAsync($"/api/v1/cdc/streams/{streamId}", ct);
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
            var streamId = handle.ConnectionInfo["stream_id"]?.ToString();

            using var response = await client.GetAsync($"/api/v1/cdc/streams/{streamId}/health", ct);
            sw.Stop();

            if (!response.IsSuccessStatusCode)
            {
                return new ConnectionHealth(
                    IsHealthy: false,
                    StatusMessage: $"CDC stream health check returned {response.StatusCode}",
                    Latency: sw.Elapsed,
                    CheckedAt: DateTimeOffset.UtcNow);
            }

            var health = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            var lag = health.TryGetProperty("replication_lag_ms", out var lagProp) ? lagProp.GetInt64() : -1;

            return new ConnectionHealth(
                IsHealthy: true,
                StatusMessage: $"CDC stream active, replication lag: {lag}ms",
                Latency: sw.Elapsed,
                CheckedAt: DateTimeOffset.UtcNow,
                Details: new Dictionary<string, object>
                {
                    ["replication_lag_ms"] = lag,
                    ["cdc_method"] = handle.ConnectionInfo["cdc_method"],
                    ["database_type"] = handle.ConnectionInfo["database_type"]
                });
        }

        /// <summary>
        /// Detects the database type from a connection string by using URI scheme prefixes
        /// first (unambiguous), then structured key=value parsing for ADO.NET-style strings.
        /// Finding 1966: Replaced brittle substring matching that falsely matched "host=" in all
        /// connection strings and "server=" in SQL Server and MySQL simultaneously.
        /// </summary>
        private string DetectDatabaseType(string connectionString)
        {
            // 1) Explicit override via db_type key
            var explicitType = connectionString.Split(';')
                .Select(p => p.Trim().Split('='))
                .Where(p => p.Length == 2 && p[0].Trim().Equals("db_type", StringComparison.OrdinalIgnoreCase))
                .Select(p => p[1].Trim())
                .FirstOrDefault();
            if (!string.IsNullOrEmpty(explicitType))
                return explicitType;

            // 2) URI-scheme detection (mongodb://, mysql://, postgresql://, etc.)
            foreach (var (scheme, dbType) in UriSchemeSignatures)
            {
                if (connectionString.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
                    return dbType;
            }

            // 3) ADO.NET key=value parsing — look for provider-specific discriminators
            var kvPairs = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim().Split('=', 2, StringSplitOptions.TrimEntries))
                .Where(p => p.Length == 2)
                .ToDictionary(p => p[0].ToLowerInvariant(), p => p[1], StringComparer.OrdinalIgnoreCase);

            // Port-based detection as a tiebreaker
            if (kvPairs.TryGetValue("port", out var portStr) && int.TryParse(portStr, out var port))
            {
                return port switch
                {
                    5432 => "postgresql",
                    3306 => "mysql",
                    1433 => "sqlserver",
                    1521 => "oracle",
                    27017 or 27018 or 27019 => "mongodb",
                    _ => string.Empty
                };
            }

            // Oracle-specific discriminators ("data source" without host/server keys → TNS alias)
            if (kvPairs.ContainsKey("data source") && !kvPairs.ContainsKey("host") && !kvPairs.ContainsKey("server"))
                return "oracle";

            return string.Empty;
        }
    }
}
