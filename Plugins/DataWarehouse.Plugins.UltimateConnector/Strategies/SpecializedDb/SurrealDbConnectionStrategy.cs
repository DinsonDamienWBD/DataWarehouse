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
    public class SurrealDbConnectionStrategy : DatabaseConnectionStrategyBase
    {
        private volatile HttpClient? _httpClient;

        public override string StrategyId => "surrealdb";
        public override string DisplayName => "SurrealDB";
        public override string SemanticDescription => "Multi-model database combining graph, document, and key-value capabilities with SQL-like queries";
        public override string[] Tags => new[] { "specialized", "surrealdb", "multi-model", "graph", "realtime" };

        public override ConnectionStrategyCapabilities Capabilities => new(
            SupportsPooling: true, SupportsStreaming: true, SupportsTransactions: true,
            SupportsBulkOperations: true, SupportsSchemaDiscovery: true, SupportsSsl: true,
            SupportsCompression: true, SupportsAuthentication: true, MaxConcurrentConnections: 100,
            SupportedAuthMethods: new[] { "basic", "bearer" }
        );

        public SurrealDbConnectionStrategy(ILogger<SurrealDbConnectionStrategy>? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var (host, port) = ParseHostPort(config.ConnectionString, 8000);
            _httpClient = new HttpClient { BaseAddress = new Uri($"http://{host}:{port}"), Timeout = config.Timeout };
            if (!string.IsNullOrEmpty(config.AuthCredential))
                _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config.AuthCredential);
            // Set SurrealDB required headers for namespace and database
            var ns = GetConfiguration(config, "Namespace", "default");
            var db = GetConfiguration(config, "Database", "default");
            _httpClient.DefaultRequestHeaders.Add("NS", ns);
            _httpClient.DefaultRequestHeaders.Add("DB", db);
            _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
            using var response = await _httpClient.GetAsync("/health", ct);
            response.EnsureSuccessStatusCode();
            return new DefaultConnectionHandle(_httpClient, new Dictionary<string, object>
            {
                ["host"] = host, ["port"] = port, ["namespace"] = ns, ["database"] = db
            });
        }

        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            if (_httpClient == null) return false;
            try { using var response = await _httpClient.GetAsync("/health", ct); return response.IsSuccessStatusCode; }
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
            return new ConnectionHealth(isHealthy, isHealthy ? "SurrealDB healthy" : "SurrealDB unhealthy", sw.Elapsed, DateTimeOffset.UtcNow);
        }

        /// <summary>
        /// Executes a SurrealQL query using the POST /sql endpoint.
        /// Returns all records from the first result set.
        /// </summary>
        public override async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(
            IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
        {
            var client = handle.GetConnection<HttpClient>();
            using var content = new StringContent(query, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync("/sql", content, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            // SurrealDB /sql returns: [{"status":"OK","result":[{...},...]}]
            using var doc = await response.Content.ReadFromJsonAsync<JsonDocument>(ct).ConfigureAwait(false);
            var root = doc!.RootElement;
            var results = new List<Dictionary<string, object?>>();

            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var stmt in root.EnumerateArray())
                {
                    if (!stmt.TryGetProperty("result", out var resultProp)) continue;
                    if (resultProp.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var row in resultProp.EnumerateArray())
                        {
                            var dict = new Dictionary<string, object?>();
                            if (row.ValueKind == JsonValueKind.Object)
                            {
                                foreach (var prop in row.EnumerateObject())
                                    dict[prop.Name] = prop.Value.ValueKind == JsonValueKind.Null ? null : prop.Value.ToString();
                            }
                            results.Add(dict);
                        }
                    }
                    break; // Return first statement results
                }
            }
            return results;
        }

        /// <summary>
        /// Executes a non-query SurrealQL statement (DDL/DML).
        /// </summary>
        public override async Task<int> ExecuteNonQueryAsync(
            IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
        {
            var client = handle.GetConnection<HttpClient>();
            using var content = new StringContent(command, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync("/sql", content, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return 1;
        }

        /// <summary>
        /// Retrieves table schema by querying SurrealDB INFO FOR DB.
        /// </summary>
        public override async Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default)
        {
            var client = handle.GetConnection<HttpClient>();
            using var content = new StringContent("INFO FOR DB;", Encoding.UTF8, "application/json");
            using var response = await client.PostAsync("/sql", content, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return Array.Empty<DataSchema>();

            using var doc = await response.Content.ReadFromJsonAsync<JsonDocument>(ct).ConfigureAwait(false);
            var root = doc!.RootElement;
            var schemas = new List<DataSchema>();

            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var stmt in root.EnumerateArray())
                {
                    if (!stmt.TryGetProperty("result", out var resultProp)) continue;
                    if (resultProp.ValueKind == JsonValueKind.Object &&
                        resultProp.TryGetProperty("tb", out var tables))
                    {
                        foreach (var table in tables.EnumerateObject())
                        {
                            schemas.Add(new DataSchema(
                                table.Name,
                                new[] { new DataSchemaField("id", "Record", false, null, null) },
                                new[] { "id" },
                                new Dictionary<string, object> { ["type"] = "table", ["definition"] = table.Value.ToString() }));
                        }
                    }
                    break;
                }
            }
            return schemas;
        }

        private (string host, int port) ParseHostPort(string connectionString, int defaultPort)
        {
            // P2-2132: Use Uri parsing to correctly handle IPv6 addresses like http://[::1]:8123/
            var uriString = connectionString.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                            connectionString.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? connectionString : "http://" + connectionString;
            if (Uri.TryCreate(uriString, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host) && uri.Port > 0)
                return (uri.Host, uri.Port);
            return ParseHostPortSafe(connectionString, defaultPort);
        }
    }
}
