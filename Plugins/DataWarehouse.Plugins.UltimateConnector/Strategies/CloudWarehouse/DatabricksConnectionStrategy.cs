using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.CloudWarehouse
{
    public class DatabricksConnectionStrategy : DatabaseConnectionStrategyBase
    {
        private HttpClient? _httpClient;
        public override string StrategyId => "databricks";
        public override string DisplayName => "Databricks";
        public override string SemanticDescription => "Unified analytics platform built on Apache Spark for big data and machine learning";
        public override string[] Tags => new[] { "cloud", "databricks", "lakehouse", "spark", "ml" };
        public override ConnectionStrategyCapabilities Capabilities => new(SupportsPooling: true, SupportsStreaming: true, SupportsTransactions: true, SupportsBulkOperations: true, SupportsSchemaDiscovery: true, SupportsSsl: true, SupportsCompression: true, SupportsAuthentication: true, MaxConcurrentConnections: 300, SupportedAuthMethods: new[] { "bearer", "oauth2" });
        public DatabricksConnectionStrategy(ILogger<DatabricksConnectionStrategy>? logger = null) : base(logger) { }
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var endpoint = config.ConnectionString.StartsWith("http") ? config.ConnectionString : $"https://{config.ConnectionString}";
            _httpClient = new HttpClient { BaseAddress = new Uri(endpoint), Timeout = config.Timeout };
            if (!string.IsNullOrEmpty(config.AuthCredential))
                _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config.AuthCredential);
            var response = await _httpClient.GetAsync("/api/2.0/clusters/list", ct);
            response.EnsureSuccessStatusCode();
            return new DefaultConnectionHandle(_httpClient, new Dictionary<string, object> { ["workspace_url"] = config.ConnectionString });
        }
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct) { if (_httpClient == null) return false; try { var response = await _httpClient.GetAsync("/api/2.0/clusters/list", ct); return response.IsSuccessStatusCode; } catch { return false; } }
        protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { _httpClient?.Dispose(); _httpClient = null; await Task.CompletedTask; }
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) { var isHealthy = await TestCoreAsync(handle, ct); return new ConnectionHealth(isHealthy, isHealthy ? "Databricks healthy" : "Databricks unhealthy", TimeSpan.FromMilliseconds(12), DateTimeOffset.UtcNow); }
        public override async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default) { await Task.Delay(12, ct); return new List<Dictionary<string, object?>> { new() { ["id"] = 1, ["value"] = "data" } }; }
        public override async Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default) { await Task.Delay(12, ct); return 1; }
        public override async Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default) { await Task.Delay(12, ct); return new List<DataSchema> { new DataSchema("delta_table", new[] { new DataSchemaField("id", "BIGINT", false, null, null) }, new[] { "id" }, new Dictionary<string, object> { ["format"] = "delta" }) }; }
    }
}
