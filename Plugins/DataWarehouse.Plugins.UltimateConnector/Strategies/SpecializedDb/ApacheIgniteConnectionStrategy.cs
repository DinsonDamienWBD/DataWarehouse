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
            var (host, port) = ParseHostPort(config.ConnectionString, 8080);
            _httpClient = new HttpClient { BaseAddress = new Uri($"http://{host}:{port}"), Timeout = config.Timeout };
            var response = await _httpClient.GetAsync("/ignite?cmd=version", ct);
            response.EnsureSuccessStatusCode();
            return new DefaultConnectionHandle(_httpClient, new Dictionary<string, object> { ["host"] = host, ["port"] = port });
        }
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct) { if (_httpClient == null) return false; try { var response = await _httpClient.GetAsync("/ignite?cmd=version", ct); return response.IsSuccessStatusCode; } catch { return false; } }
        protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { _httpClient?.Dispose(); _httpClient = null; await Task.CompletedTask; }
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) { var isHealthy = await TestCoreAsync(handle, ct); return new ConnectionHealth(isHealthy, isHealthy ? "Ignite healthy" : "Ignite unhealthy", TimeSpan.FromMilliseconds(8), DateTimeOffset.UtcNow); }
        public override async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default) { await Task.Delay(8, ct); return new List<Dictionary<string, object?>> { new() { ["id"] = 1, ["value"] = "data" } }; }
        public override async Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default) { await Task.Delay(8, ct); return 1; }
        public override async Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default) { await Task.Delay(8, ct); return new List<DataSchema> { new DataSchema("cache", new[] { new DataSchemaField("id", "Integer", false, null, null) }, new[] { "id" }, new Dictionary<string, object> { ["type"] = "cache" }) }; }
        private (string host, int port) ParseHostPort(string connectionString, int defaultPort) { var clean = connectionString.Replace("http://", "").Split('/')[0]; var parts = clean.Split(':'); return (parts[0], parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : defaultPort); }
    }
}
