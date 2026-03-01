using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.SpecializedDb
{
    public class HazelcastConnectionStrategy : DatabaseConnectionStrategyBase
    {
        private volatile HttpClient? _httpClient;

        public override string StrategyId => "hazelcast";
        public override string DisplayName => "Hazelcast";
        public override string SemanticDescription => "In-memory data grid providing distributed caching and computing capabilities";
        public override string[] Tags => new[] { "specialized", "hazelcast", "cache", "distributed", "in-memory" };

        public override ConnectionStrategyCapabilities Capabilities => new(
            SupportsPooling: true, SupportsStreaming: true, SupportsTransactions: false,
            SupportsBulkOperations: true, SupportsSchemaDiscovery: false, SupportsSsl: true,
            SupportsCompression: false, SupportsAuthentication: true, MaxConcurrentConnections: 200,
            SupportedAuthMethods: new[] { "basic" }
        );

        public HazelcastConnectionStrategy(ILogger<HazelcastConnectionStrategy>? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var (host, port) = ParseHostPort(config.ConnectionString, 5701);
            _httpClient = new HttpClient { BaseAddress = new Uri($"http://{host}:{port}"), Timeout = config.Timeout };
            using var response = await _httpClient.GetAsync("/hazelcast/rest/cluster", ct);
            response.EnsureSuccessStatusCode();
            return new DefaultConnectionHandle(_httpClient, new Dictionary<string, object> { ["host"] = host, ["port"] = port });
        }

        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            if (_httpClient == null) return false;
            try { using var response = await _httpClient.GetAsync("/hazelcast/rest/cluster", ct); return response.IsSuccessStatusCode; }
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
            return new ConnectionHealth(isHealthy, isHealthy ? "Hazelcast healthy" : "Hazelcast unhealthy", sw.Elapsed, DateTimeOffset.UtcNow);
        }

        /// <summary>
        /// Executes a SQL query via Hazelcast Management Center REST SQL API.
        /// Uses POST /hazelcast/rest/sql with the query payload.
        /// </summary>
        public override async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(
            IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
        {
            var client = handle.GetConnection<HttpClient>();
            // Hazelcast REST SQL: POST /hazelcast/rest/sql with JSON body
            var body = JsonSerializer.Serialize(new { sql = query, cursorBufferSize = 4096 });
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync("/hazelcast/rest/sql", content, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            using var doc = await response.Content.ReadFromJsonAsync<JsonDocument>(ct).ConfigureAwait(false);
            var root = doc!.RootElement;
            var results = new List<Dictionary<string, object?>>();

            // Hazelcast SQL response: {"columnMetadata":[{"name":"..","type":".."}], "rows":[[val,...],...]}
            if (!root.TryGetProperty("rows", out var rows))
                return results;

            var colNames = new List<string>();
            if (root.TryGetProperty("columnMetadata", out var meta))
                foreach (var col in meta.EnumerateArray())
                    colNames.Add(col.TryGetProperty("name", out var n) ? n.GetString() ?? "col" : "col");

            foreach (var row in rows.EnumerateArray())
            {
                var dict = new Dictionary<string, object?>();
                int i = 0;
                foreach (var cell in row.EnumerateArray())
                {
                    var colName = i < colNames.Count ? colNames[i] : $"col{i}";
                    dict[colName] = cell.ValueKind == JsonValueKind.Null ? null : cell.ToString();
                    i++;
                }
                results.Add(dict);
            }
            return results;
        }

        /// <summary>
        /// Executes a non-query SQL statement (DDL/DML) via Hazelcast REST SQL API.
        /// </summary>
        public override async Task<int> ExecuteNonQueryAsync(
            IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
        {
            var client = handle.GetConnection<HttpClient>();
            var body = JsonSerializer.Serialize(new { sql = command });
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync("/hazelcast/rest/sql", content, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return 1;
        }

        public override Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default)
        {
            // Hazelcast does not expose a standard schema discovery REST endpoint.
            // Schema information is available via INFORMATION_SCHEMA SQL queries.
            return Task.FromResult<IReadOnlyList<DataSchema>>(
                new[] { new DataSchema("IMap", new[] { new DataSchemaField("key", "Object", false, null, null), new DataSchemaField("value", "Object", true, null, null) }, new[] { "key" }, new Dictionary<string, object> { ["type"] = "imap", ["note"] = "Use INFORMATION_SCHEMA SQL for detailed schema" }) });
        }

        private (string host, int port) ParseHostPort(string connectionString, int defaultPort)
        {
            var clean = connectionString.Replace("http://", "").Split('/')[0];
            var parts = clean.Split(':');
            return (parts[0], parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : defaultPort);
        }
    }
}
