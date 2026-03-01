using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.SpecializedDb
{
    /// <summary>
    /// Connection strategy for ClickHouse OLAP database.
    /// </summary>
    public class ClickHouseConnectionStrategy : DatabaseConnectionStrategyBase
    {
        private HttpClient? _httpClient;

        public override string StrategyId => "clickhouse";
        public override string DisplayName => "ClickHouse";
        public override string SemanticDescription => "Fast open-source column-oriented database management system for OLAP workloads";
        public override string[] Tags => new[] { "specialized", "clickhouse", "olap", "columnar", "analytics" };

        public override ConnectionStrategyCapabilities Capabilities => new(
            SupportsPooling: true,
            SupportsStreaming: true,
            SupportsTransactions: false,
            SupportsBulkOperations: true,
            SupportsSchemaDiscovery: true,
            SupportsSsl: true,
            SupportsCompression: true,
            SupportsAuthentication: true,
            MaxConcurrentConnections: 200,
            SupportedAuthMethods: new[] { "basic", "none" }
        );

        public ClickHouseConnectionStrategy(ILogger<ClickHouseConnectionStrategy>? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var useSsl = GetConfiguration<bool>(config, "UseSsl", false)
                || config.ConnectionString.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            var scheme = useSsl ? "https" : "http";
            var (host, port) = ParseHostPort(config.ConnectionString, useSsl ? 8443 : 8123);
            _httpClient = new HttpClient
            {
                BaseAddress = new Uri($"{scheme}://{host}:{port}"),
                Timeout = config.Timeout
            };

            if (config.AuthMethod == "basic" && !string.IsNullOrEmpty(config.AuthCredential))
            {
                var authBytes = System.Text.Encoding.UTF8.GetBytes($"{config.AuthSecondary ?? "default"}:{config.AuthCredential}");
                _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic",
                    Convert.ToBase64String(authBytes));
            }

            using var response = await _httpClient.GetAsync("/?query=SELECT%201", ct);
            response.EnsureSuccessStatusCode();

            return new DefaultConnectionHandle(_httpClient, new Dictionary<string, object>
            {
                ["host"] = host,
                ["port"] = port
            });
        }

        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            if (_httpClient == null) return false;
            try
            {
                using var response = await _httpClient.GetAsync("/?query=SELECT%201", ct);
                return response.IsSuccessStatusCode;
            }
            catch { /* Connection test failed - return false */ return false; }
        }

        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) {
            _httpClient?.Dispose();
            _httpClient = null; return Task.CompletedTask; }

        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            // P2-2180: Measure actual latency with Stopwatch instead of hardcoded value.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var isHealthy = await TestCoreAsync(handle, ct);
            sw.Stop();
            return new ConnectionHealth(isHealthy, isHealthy ? "ClickHouse healthy" : "ClickHouse unhealthy",
                sw.Elapsed, DateTimeOffset.UtcNow);
        }

        public override async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(
            IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
        {
            if (_httpClient == null) return new List<Dictionary<string, object?>>();
            try
            {
                var encodedQuery = Uri.EscapeDataString(query + (query.Contains("FORMAT", StringComparison.OrdinalIgnoreCase) ? "" : " FORMAT JSONEachRow"));
                using var response = await _httpClient.GetAsync($"/?query={encodedQuery}", ct);
                if (!response.IsSuccessStatusCode) return new List<Dictionary<string, object?>>();
                var json = await response.Content.ReadAsStringAsync(ct);
                var results = new List<Dictionary<string, object?>>();
                foreach (var line in json.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    try
                    {
                        var row = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(line);
                        if (row != null) results.Add(row);
                    }
                    catch { /* Best-effort JSON parsing */ }
                }
                return results;
            }
            catch { /* Query execution failed - return empty result */ return new List<Dictionary<string, object?>>(); }
        }

        public override async Task<int> ExecuteNonQueryAsync(
            IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
        {
            if (_httpClient == null) return 0;
            try
            {
                var content = new StringContent(command, System.Text.Encoding.UTF8, "text/plain");
                using var response = await _httpClient.PostAsync("/", content, ct);
                return response.IsSuccessStatusCode ? 1 : 0;
            }
            catch { /* Non-query execution failed - return 0 */ return 0; }
        }

        public override async Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default)
        {
            if (_httpClient == null) return new List<DataSchema>();
            try
            {
                var query = Uri.EscapeDataString("SELECT database, table, name, type FROM system.columns FORMAT JSONEachRow");
                using var response = await _httpClient.GetAsync($"/?query={query}", ct);
                if (!response.IsSuccessStatusCode) return new List<DataSchema>();
                var json = await response.Content.ReadAsStringAsync(ct);
                var tableColumns = new Dictionary<string, List<DataSchemaField>>();
                foreach (var line in json.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(line);
                        var db = doc.RootElement.TryGetProperty("database", out var d) ? d.GetString() : "default";
                        var tbl = doc.RootElement.TryGetProperty("table", out var t) ? t.GetString() : "table";
                        var col = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() : "col";
                        var typ = doc.RootElement.TryGetProperty("type", out var tp) ? tp.GetString() : "String";
                        var key = $"{db}.{tbl}";
                        if (!tableColumns.ContainsKey(key)) tableColumns[key] = new List<DataSchemaField>();
                        tableColumns[key].Add(new DataSchemaField(col ?? "col", typ ?? "String", true, null, null));
                    }
                    catch { /* Best-effort JSON parsing */ }
                }
                return tableColumns.Select(kv => new DataSchema(kv.Key, kv.Value.ToArray(), kv.Value.Count > 0 ? new[] { kv.Value[0].Name } : Array.Empty<string>(), new Dictionary<string, object> { ["engine"] = "MergeTree" })).ToList();
            }
            catch { /* Schema discovery failed - return empty list */ return new List<DataSchema>(); }
        }

        private (string host, int port) ParseHostPort(string connectionString, int defaultPort)
        {
            var clean = connectionString.Replace("http://", "").Replace("https://", "").Split('/')[0];
            var parts = clean.Split(':');
            return (parts[0], parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : defaultPort);
        }
    }
}
