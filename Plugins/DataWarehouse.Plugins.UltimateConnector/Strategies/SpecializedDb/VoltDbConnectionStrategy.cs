using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.SpecializedDb
{
    public class VoltDbConnectionStrategy : DatabaseConnectionStrategyBase
    {
        private volatile HttpClient? _httpClient;

        public override string StrategyId => "voltdb";
        public override string DisplayName => "VoltDB";
        public override string SemanticDescription => "In-memory relational database for high-velocity applications requiring ACID guarantees";
        public override string[] Tags => new[] { "specialized", "voltdb", "in-memory", "acid", "high-velocity" };

        public override ConnectionStrategyCapabilities Capabilities => new(
            SupportsPooling: true, SupportsStreaming: true, SupportsTransactions: true,
            SupportsBulkOperations: true, SupportsSchemaDiscovery: true, SupportsSsl: true,
            SupportsCompression: false, SupportsAuthentication: true, MaxConcurrentConnections: 100,
            SupportedAuthMethods: new[] { "basic" }
        );

        public VoltDbConnectionStrategy(ILogger<VoltDbConnectionStrategy>? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var (host, port) = ParseHostPort(config.ConnectionString, 8080);
            _httpClient = new HttpClient { BaseAddress = new Uri($"http://{host}:{port}"), Timeout = config.Timeout };
            using var response = await _httpClient.GetAsync("/api/1.0/?Procedure=%40Ping", ct);
            response.EnsureSuccessStatusCode();
            return new DefaultConnectionHandle(_httpClient, new Dictionary<string, object> { ["host"] = host, ["port"] = port });
        }

        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            if (_httpClient == null) return false;
            try { using var response = await _httpClient.GetAsync("/api/1.0/?Procedure=%40Ping", ct); return response.IsSuccessStatusCode; }
            catch { return false; }
        }

        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) {
            _httpClient?.Dispose();
            _httpClient = null; return Task.CompletedTask; }

        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var isHealthy = await TestCoreAsync(handle, ct);
            sw.Stop();
            return new ConnectionHealth(isHealthy, isHealthy ? "VoltDB healthy" : "VoltDB unhealthy", sw.Elapsed, DateTimeOffset.UtcNow);
        }

        /// <summary>
        /// Executes an ad-hoc SQL query via VoltDB's HTTP @AdHoc procedure.
        /// Returns rows from the first result table.
        /// </summary>
        public override async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(
            IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
        {
            var client = handle.GetConnection<HttpClient>();
            var encodedQuery = Uri.EscapeDataString(query);
            var url = $"/api/1.0/?Procedure=%40AdHoc&Parameters=%5B%22{encodedQuery}%22%5D";
            using var response = await client.GetAsync(url, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            // VoltDB response: {"status":1,"results":[{"schema":[{"name":"col","type":5}],"data":[[val,...],...]}]}
            using var doc = await response.Content.ReadFromJsonAsync<JsonDocument>(ct).ConfigureAwait(false);
            var root = doc!.RootElement;
            var results = new List<Dictionary<string, object?>>();

            if (!root.TryGetProperty("results", out var resultsProp) ||
                resultsProp.ValueKind != JsonValueKind.Array ||
                resultsProp.GetArrayLength() == 0)
                return results;

            var firstResult = resultsProp[0];
            if (!firstResult.TryGetProperty("schema", out var schema) ||
                !firstResult.TryGetProperty("data", out var data))
                return results;

            var colNames = new List<string>();
            foreach (var col in schema.EnumerateArray())
                colNames.Add(col.TryGetProperty("name", out var name) ? name.GetString() ?? "col" : "col");

            foreach (var row in data.EnumerateArray())
            {
                var dict = new Dictionary<string, object?>();
                int i = 0;
                foreach (var cell in row.EnumerateArray())
                {
                    if (i < colNames.Count)
                        dict[colNames[i]] = cell.ValueKind == JsonValueKind.Null ? null : cell.ToString();
                    i++;
                }
                results.Add(dict);
            }
            return results;
        }

        /// <summary>
        /// Executes a non-query SQL statement via VoltDB's @AdHoc procedure.
        /// </summary>
        public override async Task<int> ExecuteNonQueryAsync(
            IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
        {
            var client = handle.GetConnection<HttpClient>();
            var encodedQuery = Uri.EscapeDataString(command);
            var url = $"/api/1.0/?Procedure=%40AdHoc&Parameters=%5B%22{encodedQuery}%22%5D";
            using var response = await client.GetAsync(url, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return 1;
        }

        /// <summary>
        /// Retrieves schema by querying VoltDB system tables (INFORMATION_SCHEMA).
        /// </summary>
        public override async Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default)
        {
            var client = handle.GetConnection<HttpClient>();
            var query = "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE='TABLE'";
            try
            {
                var rows = await ExecuteQueryAsync(handle, query, null, ct).ConfigureAwait(false);
                var schemas = new List<DataSchema>();
                foreach (var row in rows)
                {
                    var tableName = row.TryGetValue("TABLE_NAME", out var tn) ? tn?.ToString() ?? "unknown" : "unknown";
                    schemas.Add(new DataSchema(tableName,
                        new[] { new DataSchemaField("id", "BIGINT", false, null, null) },
                        new[] { "id" },
                        new Dictionary<string, object> { ["type"] = "table", ["engine"] = "voltdb" }));
                }
                return schemas;
            }
            catch
            {
                return Array.Empty<DataSchema>();
            }
        }

        private (string host, int port) ParseHostPort(string connectionString, int defaultPort)
        {
            var clean = connectionString.Replace("http://", "").Split('/')[0];
            var parts = clean.Split(':');
            return (parts[0], parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : defaultPort);
        }
    }
}
