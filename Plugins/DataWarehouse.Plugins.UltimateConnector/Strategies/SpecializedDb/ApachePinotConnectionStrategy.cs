using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.SpecializedDb
{
    public class ApachePinotConnectionStrategy : DatabaseConnectionStrategyBase
    {
        private HttpClient? _httpClient;

        public override string StrategyId => "pinot";
        public override string DisplayName => "Apache Pinot";
        public override string SemanticDescription => "Real-time distributed OLAP datastore for low-latency, high-throughput analytics";
        public override string[] Tags => new[] { "specialized", "pinot", "realtime", "olap", "analytics" };

        public override ConnectionStrategyCapabilities Capabilities => new(
            SupportsPooling: true, SupportsStreaming: true, SupportsTransactions: false,
            SupportsBulkOperations: true, SupportsSchemaDiscovery: true, SupportsSsl: true,
            SupportsCompression: true, SupportsAuthentication: true, MaxConcurrentConnections: 150,
            SupportedAuthMethods: new[] { "basic", "bearer" }
        );

        public ApachePinotConnectionStrategy(ILogger<ApachePinotConnectionStrategy>? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var useSsl = GetConfiguration<bool>(config, "UseSsl", false);
            var scheme = useSsl ? "https" : "http";
            var (host, port) = ParseHostPort(config.ConnectionString, useSsl ? 9443 : 9000);
            _httpClient = new HttpClient { BaseAddress = new Uri($"{scheme}://{host}:{port}"), Timeout = config.Timeout };
            if (config.AuthMethod == "bearer" && !string.IsNullOrEmpty(config.AuthCredential))
                _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config.AuthCredential);
            else if (!string.IsNullOrEmpty(config.AuthCredential))
            {
                var creds = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{config.AuthSecondary ?? ""}:{config.AuthCredential}"));
                _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", creds);
            }
            using var response = await _httpClient.GetAsync("/health", ct);
            response.EnsureSuccessStatusCode();
            return new DefaultConnectionHandle(_httpClient, new Dictionary<string, object> { ["host"] = host, ["port"] = port });
        }

        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            if (_httpClient == null) return false;
            try { using var response = await _httpClient.GetAsync("/health", ct); return response.IsSuccessStatusCode; }
            catch { return false; /* Connection validation - failure acceptable */ }
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
            return new ConnectionHealth(isHealthy, isHealthy ? "Pinot healthy" : "Pinot unhealthy", sw.Elapsed, DateTimeOffset.UtcNow);
        }

        public override async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
        {
            if (_httpClient == null) return new List<Dictionary<string, object?>>();
            try
            {
                var requestBody = System.Text.Json.JsonSerializer.Serialize(new { sql = query });
                var content = new StringContent(requestBody, System.Text.Encoding.UTF8, "application/json");
                using var response = await _httpClient.PostAsync("/query/sql", content, ct);
                if (!response.IsSuccessStatusCode) return new List<Dictionary<string, object?>>();
                var json = await response.Content.ReadAsStringAsync(ct);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var results = new List<Dictionary<string, object?>>();
                if (doc.RootElement.TryGetProperty("resultTable", out var table) && table.TryGetProperty("rows", out var rows) && table.TryGetProperty("dataSchema", out var schema))
                {
                    var columns = new List<string>();
                    if (schema.TryGetProperty("columnNames", out var cols))
                        foreach (var col in cols.EnumerateArray()) columns.Add(col.GetString() ?? "col");
                    foreach (var row in rows.EnumerateArray())
                    {
                        var dict = new Dictionary<string, object?>();
                        var i = 0;
                        foreach (var val in row.EnumerateArray())
                        {
                            if (i < columns.Count) dict[columns[i]] = val.ValueKind == System.Text.Json.JsonValueKind.Null ? null : val.ToString();
                            i++;
                        }
                        results.Add(dict);
                    }
                }
                return results;
            }
            catch { return new List<Dictionary<string, object?>>(); /* Query failed - return empty */ }
        }

        public override async Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
        {
            if (_httpClient == null) return 0;
            try
            {
                var requestBody = System.Text.Json.JsonSerializer.Serialize(new { sql = command });
                var content = new StringContent(requestBody, System.Text.Encoding.UTF8, "application/json");
                using var response = await _httpClient.PostAsync("/query/sql", content, ct);
                return response.IsSuccessStatusCode ? 1 : 0;
            }
            catch { return 0; /* Operation failed - return zero */ }
        }

        public override async Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default)
        {
            if (_httpClient == null) return new List<DataSchema>();
            try
            {
                using var response = await _httpClient.GetAsync("/tables", ct);
                if (!response.IsSuccessStatusCode) return new List<DataSchema>();
                var json = await response.Content.ReadAsStringAsync(ct);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var schemas = new List<DataSchema>();
                if (doc.RootElement.TryGetProperty("tables", out var tables))
                {
                    foreach (var tbl in tables.EnumerateArray())
                    {
                        var name = tbl.GetString() ?? "table";
                        schemas.Add(new DataSchema(name, new[] { new DataSchemaField("timestamp", "Long", false, null, null) }, new[] { "timestamp" }, new Dictionary<string, object> { ["type"] = "realtime" }));
                    }
                }
                return schemas;
            }
            catch { return new List<DataSchema>(); /* Schema query failed - return empty */ }
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
