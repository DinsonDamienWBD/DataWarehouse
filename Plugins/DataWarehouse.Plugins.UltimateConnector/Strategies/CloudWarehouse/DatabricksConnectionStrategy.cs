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
        private volatile HttpClient? _httpClient;
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
            using var response = await _httpClient.GetAsync("/api/2.0/clusters/list", ct);
            response.EnsureSuccessStatusCode();
            return new DefaultConnectionHandle(_httpClient, new Dictionary<string, object> { ["workspace_url"] = config.ConnectionString });
        }
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct) { if (_httpClient == null) return false; try { var response = await _httpClient.GetAsync("/api/2.0/clusters/list", ct); return response.IsSuccessStatusCode; } catch { return false; } }
        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { _httpClient?.Dispose(); _httpClient = null; return Task.CompletedTask; }
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            // P2-2180: Measure actual latency with Stopwatch instead of hardcoded value.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var isHealthy = await TestCoreAsync(handle, ct);
            sw.Stop();
            return new ConnectionHealth(isHealthy, isHealthy ? "Databricks healthy" : "Databricks unhealthy", sw.Elapsed, DateTimeOffset.UtcNow);
        }
        public override Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
            => throw new NotSupportedException("Databricks query execution requires the Databricks.Sql NuGet package or the SQL Statement Execution REST API with a valid personal access token.");
        public override Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
            => throw new NotSupportedException("Databricks DML execution requires the Databricks.Sql NuGet package or the SQL Statement Execution REST API.");
        public override Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default)
            => throw new NotSupportedException("Databricks schema discovery requires the Databricks.Sql NuGet package or the Unity Catalog REST API.");
    }
}
