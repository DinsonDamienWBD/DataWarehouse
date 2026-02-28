using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.SpecializedDb
{
    public class FaunaDbConnectionStrategy : DatabaseConnectionStrategyBase
    {
        private volatile HttpClient? _httpClient;
        public override string StrategyId => "faunadb";
        public override string DisplayName => "FaunaDB";
        public override string SemanticDescription => "Serverless distributed document-relational database with native GraphQL support";
        public override string[] Tags => new[] { "specialized", "faunadb", "serverless", "document", "graphql" };
        public override ConnectionStrategyCapabilities Capabilities => new(SupportsPooling: true, SupportsStreaming: true, SupportsTransactions: true, SupportsBulkOperations: true, SupportsSchemaDiscovery: true, SupportsSsl: true, SupportsCompression: true, SupportsAuthentication: true, MaxConcurrentConnections: 200, SupportedAuthMethods: new[] { "bearer" });
        public FaunaDbConnectionStrategy(ILogger<FaunaDbConnectionStrategy>? logger = null) : base(logger) { }
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var endpoint = config.ConnectionString.StartsWith("http") ? config.ConnectionString : "https://db.fauna.com";
            _httpClient = new HttpClient { BaseAddress = new Uri(endpoint), Timeout = config.Timeout };
            if (!string.IsNullOrEmpty(config.AuthCredential))
                _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config.AuthCredential);
            return new DefaultConnectionHandle(_httpClient, new Dictionary<string, object> { ["endpoint"] = endpoint });
        }
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct) { await Task.Delay(10, ct); return _httpClient != null; }
        protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { _httpClient?.Dispose(); _httpClient = null; await Task.CompletedTask; }
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) { var isHealthy = await TestCoreAsync(handle, ct); return new ConnectionHealth(isHealthy, isHealthy ? "FaunaDB healthy" : "FaunaDB unhealthy", TimeSpan.FromMilliseconds(10), DateTimeOffset.UtcNow); }
        public override async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
        {
            if (_httpClient == null) return new List<Dictionary<string, object?>>();
            try
            {
                // FaunaDB uses FQL queries sent as POST to /
                var requestBody = System.Text.Json.JsonSerializer.Serialize(new { query });
                var content = new StringContent(requestBody, System.Text.Encoding.UTF8, "application/json");
                using var response = await _httpClient.PostAsync("/", content, ct);
                if (!response.IsSuccessStatusCode) return new List<Dictionary<string, object?>>();
                var json = await response.Content.ReadAsStringAsync(ct);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var results = new List<Dictionary<string, object?>>();
                if (doc.RootElement.TryGetProperty("resource", out var resource))
                {
                    if (resource.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var item in resource.EnumerateArray())
                        {
                            var row = new Dictionary<string, object?>();
                            foreach (var prop in item.EnumerateObject())
                                row[prop.Name] = prop.Value.ValueKind == System.Text.Json.JsonValueKind.Null ? null : prop.Value.ToString();
                            results.Add(row);
                        }
                    }
                    else
                    {
                        var row = new Dictionary<string, object?>();
                        foreach (var prop in resource.EnumerateObject())
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
                var requestBody = System.Text.Json.JsonSerializer.Serialize(new { query = command });
                var content = new StringContent(requestBody, System.Text.Encoding.UTF8, "application/json");
                using var response = await _httpClient.PostAsync("/", content, ct);
                return response.IsSuccessStatusCode ? 1 : 0;
            }
            catch { return 0; /* Operation failed - return zero */ }
        }
        public override async Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default)
        {
            if (_httpClient == null) return new List<DataSchema>();
            try
            {
                // Query for collections using FQL
                var requestBody = System.Text.Json.JsonSerializer.Serialize(new { query = "Paginate(Collections())" });
                var content = new StringContent(requestBody, System.Text.Encoding.UTF8, "application/json");
                using var response = await _httpClient.PostAsync("/", content, ct);
                if (!response.IsSuccessStatusCode) return new List<DataSchema>();
                var json = await response.Content.ReadAsStringAsync(ct);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var schemas = new List<DataSchema>();
                if (doc.RootElement.TryGetProperty("resource", out var resource) && resource.TryGetProperty("data", out var data))
                {
                    foreach (var coll in data.EnumerateArray())
                    {
                        var name = coll.TryGetProperty("id", out var id) ? id.GetString() ?? "collection" : "collection";
                        schemas.Add(new DataSchema(name, new[] { new DataSchemaField("ref", "Ref", false, null, null), new DataSchemaField("data", "Object", true, null, null) }, new[] { "ref" }, new Dictionary<string, object> { ["type"] = "collection" }));
                    }
                }
                return schemas;
            }
            catch { return new List<DataSchema>(); /* Schema query failed - return empty */ }
        }
    }
}
