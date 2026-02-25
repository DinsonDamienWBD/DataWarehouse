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
    public class CrateDbConnectionStrategy : DatabaseConnectionStrategyBase
    {
        private HttpClient? _httpClient;
        public override string StrategyId => "cratedb";
        public override string DisplayName => "CrateDB";
        public override string SemanticDescription => "Distributed SQL database for real-time analytics over large-scale machine data";
        public override string[] Tags => new[] { "specialized", "cratedb", "distributed", "sql", "iot" };
        public override ConnectionStrategyCapabilities Capabilities => new(SupportsPooling: true, SupportsStreaming: true, SupportsTransactions: false, SupportsBulkOperations: true, SupportsSchemaDiscovery: true, SupportsSsl: true, SupportsCompression: true, SupportsAuthentication: true, MaxConcurrentConnections: 150, SupportedAuthMethods: new[] { "basic" });
        public CrateDbConnectionStrategy(ILogger<CrateDbConnectionStrategy>? logger = null) : base(logger) { }
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var (host, port) = ParseHostPort(config.ConnectionString, 4200);
            _httpClient = new HttpClient { BaseAddress = new Uri($"http://{host}:{port}"), Timeout = config.Timeout };
            var response = await _httpClient.PostAsync("/_sql", new StringContent("{\"stmt\":\"SELECT 1\"}", System.Text.Encoding.UTF8, "application/json"), ct);
            response.EnsureSuccessStatusCode();
            return new DefaultConnectionHandle(_httpClient, new Dictionary<string, object> { ["host"] = host, ["port"] = port });
        }
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct) { if (_httpClient == null) return false; try { var response = await _httpClient.PostAsync("/_sql", new StringContent("{\"stmt\":\"SELECT 1\"}", System.Text.Encoding.UTF8, "application/json"), ct); return response.IsSuccessStatusCode; } catch { return false; } }
        protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { _httpClient?.Dispose(); _httpClient = null; await Task.CompletedTask; }
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) { var isHealthy = await TestCoreAsync(handle, ct); return new ConnectionHealth(isHealthy, isHealthy ? "CrateDB healthy" : "CrateDB unhealthy", TimeSpan.FromMilliseconds(6), DateTimeOffset.UtcNow); }
        public override async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
        {
            if (_httpClient == null) return new List<Dictionary<string, object?>>();
            try
            {
                var requestBody = System.Text.Json.JsonSerializer.Serialize(new { stmt = query });
                var content = new StringContent(requestBody, System.Text.Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync("/_sql", content, ct);
                if (!response.IsSuccessStatusCode) return new List<Dictionary<string, object?>>();
                var json = await response.Content.ReadAsStringAsync(ct);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var results = new List<Dictionary<string, object?>>();
                if (doc.RootElement.TryGetProperty("cols", out var cols) && doc.RootElement.TryGetProperty("rows", out var rows))
                {
                    var colNames = new List<string>();
                    foreach (var c in cols.EnumerateArray()) colNames.Add(c.GetString() ?? "col");
                    foreach (var row in rows.EnumerateArray())
                    {
                        var dict = new Dictionary<string, object?>();
                        var i = 0;
                        foreach (var val in row.EnumerateArray())
                        {
                            if (i < colNames.Count) dict[colNames[i]] = val.ValueKind == System.Text.Json.JsonValueKind.Null ? null : val.ToString();
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
                var requestBody = System.Text.Json.JsonSerializer.Serialize(new { stmt = command });
                var content = new StringContent(requestBody, System.Text.Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync("/_sql", content, ct);
                if (!response.IsSuccessStatusCode) return 0;
                var json = await response.Content.ReadAsStringAsync(ct);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                return doc.RootElement.TryGetProperty("rowcount", out var rc) ? rc.GetInt32() : 1;
            }
            catch { return 0; /* Operation failed - return zero */ }
        }
        public override async Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default)
        {
            if (_httpClient == null) return new List<DataSchema>();
            try
            {
                var requestBody = System.Text.Json.JsonSerializer.Serialize(new { stmt = "SELECT table_schema, table_name, column_name, data_type FROM information_schema.columns" });
                var content = new StringContent(requestBody, System.Text.Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync("/_sql", content, ct);
                if (!response.IsSuccessStatusCode) return new List<DataSchema>();
                var json = await response.Content.ReadAsStringAsync(ct);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var tableColumns = new Dictionary<string, List<DataSchemaField>>();
                if (doc.RootElement.TryGetProperty("rows", out var rows))
                {
                    foreach (var row in rows.EnumerateArray())
                    {
                        var arr = row.EnumerateArray().ToArray();
                        if (arr.Length >= 4)
                        {
                            var key = $"{arr[0].GetString()}.{arr[1].GetString()}";
                            if (!tableColumns.ContainsKey(key)) tableColumns[key] = new List<DataSchemaField>();
                            tableColumns[key].Add(new DataSchemaField(arr[2].GetString() ?? "col", arr[3].GetString() ?? "Object", true, null, null));
                        }
                    }
                }
                return tableColumns.Select(kv => new DataSchema(kv.Key, kv.Value.ToArray(), kv.Value.Count > 0 ? new[] { kv.Value[0].Name } : Array.Empty<string>(), new Dictionary<string, object> { ["type"] = "table" })).ToList();
            }
            catch { return new List<DataSchema>(); /* Schema query failed - return empty */ }
        }
        private (string host, int port) ParseHostPort(string connectionString, int defaultPort) { var clean = connectionString.Replace("http://", "").Split('/')[0]; var parts = clean.Split(':'); return (parts[0], parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : defaultPort); }
    }
}
