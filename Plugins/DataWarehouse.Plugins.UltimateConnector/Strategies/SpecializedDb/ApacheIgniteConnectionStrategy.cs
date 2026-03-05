using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.SpecializedDb
{
    public class ApacheIgniteConnectionStrategy : DatabaseConnectionStrategyBase
    {
        private HttpClient? _httpClient;
        public override string StrategyId => "ignite";
        public override string DisplayName => "Apache Ignite";
        public override string SemanticDescription => "Distributed database, caching, and processing platform for transactional and analytical workloads";
        public override string[] Tags => new[] { "specialized", "ignite", "distributed", "cache", "compute" };
        public override ConnectionStrategyCapabilities Capabilities => new(SupportsPooling: true, SupportsStreaming: true, SupportsTransactions: true, SupportsBulkOperations: true, SupportsSchemaDiscovery: true, SupportsSsl: true, SupportsCompression: true, SupportsAuthentication: true, MaxConcurrentConnections: 150, SupportedAuthMethods: new[] { "basic" });
        public ApacheIgniteConnectionStrategy(ILogger<ApacheIgniteConnectionStrategy>? logger = null) : base(logger) { }
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var useSsl = GetConfiguration<bool>(config, "UseSsl", false);
            var scheme = useSsl ? "https" : "http";
            var (host, port) = ParseHostPort(config.ConnectionString, useSsl ? 8443 : 8080);
            _httpClient = new HttpClient { BaseAddress = new Uri($"{scheme}://{host}:{port}"), Timeout = config.Timeout };
            if (!string.IsNullOrEmpty(config.AuthCredential))
            {
                var creds = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{config.AuthSecondary ?? ""}:{config.AuthCredential}"));
                _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", creds);
            }
            using var response = await _httpClient.GetAsync("/ignite?cmd=version", ct);
            response.EnsureSuccessStatusCode();
            return new DefaultConnectionHandle(_httpClient, new Dictionary<string, object> { ["host"] = host, ["port"] = port });
        }
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct) { if (_httpClient == null) return false; try { using var response = await _httpClient.GetAsync("/ignite?cmd=version", ct); return response.IsSuccessStatusCode; } catch { return false; } }
        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { _httpClient?.Dispose(); _httpClient = null; return Task.CompletedTask; }
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            // P2-2180: Measure actual latency with Stopwatch instead of hardcoded value.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var isHealthy = await TestCoreAsync(handle, ct);
            sw.Stop();
            return new ConnectionHealth(isHealthy, isHealthy ? "Ignite healthy" : "Ignite unhealthy", sw.Elapsed, DateTimeOffset.UtcNow);
        }
        public override async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
        {
            if (_httpClient == null) return new List<Dictionary<string, object?>>();
            try
            {
                var encodedQuery = Uri.EscapeDataString(query);
                using var response = await _httpClient.GetAsync($"/ignite?cmd=qryfld&cacheName=" + Uri.EscapeDataString(parameters?.GetValueOrDefault("cacheName")?.ToString() ?? "default") + "&exe&pageSize=1000&qry={encodedQuery}", ct);
                if (!response.IsSuccessStatusCode) return new List<Dictionary<string, object?>>();
                var json = await response.Content.ReadAsStringAsync(ct);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var results = new List<Dictionary<string, object?>>();
                if (doc.RootElement.TryGetProperty("response", out var resp) && resp.TryGetProperty("items", out var items))
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        var row = new Dictionary<string, object?>();
                        foreach (var prop in item.EnumerateObject())
                            row[prop.Name] = prop.Value.ValueKind == System.Text.Json.JsonValueKind.Null ? null : prop.Value.ToString();
                        results.Add(row);
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
                var encodedCmd = Uri.EscapeDataString(command);
                using var response = await _httpClient.GetAsync($"/ignite?cmd=qryfld&cacheName=" + Uri.EscapeDataString(parameters?.GetValueOrDefault("cacheName")?.ToString() ?? "default") + "&exe&qry={encodedCmd}", ct);
                return response.IsSuccessStatusCode ? 1 : 0;
            }
            catch { return 0; /* Operation failed - return zero */ }
        }
        public override async Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default)
        {
            if (_httpClient == null) return new List<DataSchema>();
            try
            {
                using var response = await _httpClient.GetAsync("/ignite?cmd=top&attr=true", ct);
                if (!response.IsSuccessStatusCode) return new List<DataSchema>();
                var json = await response.Content.ReadAsStringAsync(ct);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var schemas = new List<DataSchema>();
                if (doc.RootElement.TryGetProperty("response", out var resp))
                {
                    foreach (var cache in resp.EnumerateArray())
                    {
                        var name = cache.TryGetProperty("cacheName", out var cn) ? cn.GetString() ?? "cache" : "cache";
                        schemas.Add(new DataSchema(name, new[] { new DataSchemaField("key", "Object", false, null, null), new DataSchemaField("value", "Object", true, null, null) }, new[] { "key" }, new Dictionary<string, object> { ["type"] = "cache" }));
                    }
                }
                return schemas.Count > 0 ? schemas : new List<DataSchema> { new DataSchema("default", new[] { new DataSchemaField("key", "Object", false, null, null) }, new[] { "key" }, new Dictionary<string, object> { ["type"] = "cache" }) };
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
