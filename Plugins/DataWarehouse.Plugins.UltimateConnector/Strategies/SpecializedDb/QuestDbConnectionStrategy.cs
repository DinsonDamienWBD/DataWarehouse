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
    public class QuestDbConnectionStrategy : DatabaseConnectionStrategyBase
    {
        private volatile HttpClient? _httpClient;

        public override string StrategyId => "questdb";
        public override string DisplayName => "QuestDB";
        public override string SemanticDescription => "High-performance time-series database with SQL support and minimal operational overhead";
        public override string[] Tags => new[] { "specialized", "questdb", "timeseries", "sql", "performance" };

        public override ConnectionStrategyCapabilities Capabilities => new(
            SupportsPooling: true, SupportsStreaming: true, SupportsTransactions: false,
            SupportsBulkOperations: true, SupportsSchemaDiscovery: true, SupportsSsl: false,
            SupportsCompression: false, SupportsAuthentication: false, MaxConcurrentConnections: 100,
            SupportedAuthMethods: new[] { "none" }
        );

        public QuestDbConnectionStrategy(ILogger<QuestDbConnectionStrategy>? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var (host, port) = ParseHostPort(config.ConnectionString, 9000);
            _httpClient = new HttpClient { BaseAddress = new Uri($"http://{host}:{port}"), Timeout = config.Timeout };
            using var response = await _httpClient.GetAsync("/exec?query=SELECT%201", ct);
            response.EnsureSuccessStatusCode();
            return new DefaultConnectionHandle(_httpClient, new Dictionary<string, object> { ["host"] = host, ["port"] = port });
        }

        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            if (_httpClient == null) return false;
            try { using var response = await _httpClient.GetAsync("/exec?query=SELECT%201", ct); return response.IsSuccessStatusCode; }
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
            return new ConnectionHealth(isHealthy, isHealthy ? "QuestDB healthy" : "QuestDB unhealthy", sw.Elapsed, DateTimeOffset.UtcNow);
        }

        /// <summary>
        /// Executes a SQL query against QuestDB using the /exec HTTP endpoint.
        /// Returns rows from the JSON response dataset.
        /// </summary>
        public override async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(
            IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
        {
            var client = handle.GetConnection<HttpClient>();
            var encodedQuery = Uri.EscapeDataString(query);
            using var response = await client.GetAsync($"/exec?query={encodedQuery}", ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            using var doc = await response.Content.ReadFromJsonAsync<JsonDocument>(ct).ConfigureAwait(false);
            var root = doc!.RootElement;

            // QuestDB response: { "columns": [{"name":"col","type":"INT"},...], "dataset": [[val,...],...] }
            if (!root.TryGetProperty("dataset", out var dataset) ||
                !root.TryGetProperty("columns", out var columns))
                return Array.Empty<Dictionary<string, object?>>();

            var colNames = new List<string>();
            foreach (var col in columns.EnumerateArray())
                colNames.Add(col.GetProperty("name").GetString() ?? "col");

            var results = new List<Dictionary<string, object?>>();
            foreach (var row in dataset.EnumerateArray())
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
        /// Executes a non-query SQL statement (DDL/DML) against QuestDB using the /exec endpoint.
        /// </summary>
        public override async Task<int> ExecuteNonQueryAsync(
            IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
        {
            var client = handle.GetConnection<HttpClient>();
            var encodedQuery = Uri.EscapeDataString(command);
            using var response = await client.GetAsync($"/exec?query={encodedQuery}", ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return 1;
        }

        /// <summary>
        /// Retrieves table schema from QuestDB using SHOW TABLES and SHOW COLUMNS queries.
        /// </summary>
        public override async Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default)
        {
            var client = handle.GetConnection<HttpClient>();
            using var tablesResponse = await client.GetAsync("/exec?query=SHOW%20TABLES", ct).ConfigureAwait(false);
            if (!tablesResponse.IsSuccessStatusCode)
                return Array.Empty<DataSchema>();

            using var doc = await tablesResponse.Content.ReadFromJsonAsync<JsonDocument>(ct).ConfigureAwait(false);
            var root = doc!.RootElement;
            if (!root.TryGetProperty("dataset", out var dataset))
                return Array.Empty<DataSchema>();

            var schemas = new List<DataSchema>();
            foreach (var row in dataset.EnumerateArray())
            {
                var tableName = row[0].GetString() ?? "unknown";
                var fields = new List<DataSchemaField>();

                try
                {
                    var encodedCols = Uri.EscapeDataString($"SHOW COLUMNS FROM {tableName}");
                    using var colsResponse = await client.GetAsync($"/exec?query={encodedCols}", ct).ConfigureAwait(false);
                    if (colsResponse.IsSuccessStatusCode)
                    {
                        using var colsDoc = await colsResponse.Content.ReadFromJsonAsync<JsonDocument>(ct).ConfigureAwait(false);
                        if (colsDoc!.RootElement.TryGetProperty("dataset", out var colsDataset))
                        {
                            foreach (var col in colsDataset.EnumerateArray())
                            {
                                var colName = col[0].GetString() ?? "col";
                                var colType = col[1].GetString() ?? "UNKNOWN";
                                fields.Add(new DataSchemaField(colName, colType, true, null, null));
                            }
                        }
                    }
                }
                catch { /* Schema discovery best-effort; skip inaccessible tables */ }

                schemas.Add(new DataSchema(tableName, fields.ToArray(), new[] { "timestamp" },
                    new Dictionary<string, object> { ["type"] = "table", ["engine"] = "questdb" }));
            }
            return schemas;
        }

        private (string host, int port) ParseHostPort(string connectionString, int defaultPort)
        {
            // P2-2132: Use Uri parsing to correctly handle IPv6 addresses like http://[::1]:PORT/
            var uriString = connectionString.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                            connectionString.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? connectionString : "http://" + connectionString;
            if (Uri.TryCreate(uriString, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host) && uri.Port > 0)
                return (uri.Host, uri.Port);
            return ParseHostPortSafe(connectionString, defaultPort);
        }
    }
}
