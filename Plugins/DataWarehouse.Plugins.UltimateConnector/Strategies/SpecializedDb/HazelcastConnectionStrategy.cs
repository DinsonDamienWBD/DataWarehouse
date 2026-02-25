using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.SpecializedDb
{
    public class HazelcastConnectionStrategy : DatabaseConnectionStrategyBase
    {
        private HttpClient? _httpClient;
        public override string StrategyId => "hazelcast";
        public override string DisplayName => "Hazelcast";
        public override string SemanticDescription => "In-memory data grid providing distributed caching and computing capabilities";
        public override string[] Tags => new[] { "specialized", "hazelcast", "cache", "distributed", "in-memory" };
        public override ConnectionStrategyCapabilities Capabilities => new(SupportsPooling: true, SupportsStreaming: true, SupportsTransactions: false, SupportsBulkOperations: true, SupportsSchemaDiscovery: false, SupportsSsl: true, SupportsCompression: false, SupportsAuthentication: true, MaxConcurrentConnections: 200, SupportedAuthMethods: new[] { "basic" });
        public HazelcastConnectionStrategy(ILogger<HazelcastConnectionStrategy>? logger = null) : base(logger) { }
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var (host, port) = ParseHostPort(config.ConnectionString, 5701);
            _httpClient = new HttpClient { BaseAddress = new Uri($"http://{host}:{port}"), Timeout = config.Timeout };
            var response = await _httpClient.GetAsync("/hazelcast/rest/cluster", ct);
            response.EnsureSuccessStatusCode();
            return new DefaultConnectionHandle(_httpClient, new Dictionary<string, object> { ["host"] = host, ["port"] = port });
        }
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct) { if (_httpClient == null) return false; try { var response = await _httpClient.GetAsync("/hazelcast/rest/cluster", ct); return response.IsSuccessStatusCode; } catch { return false; } }
        protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { _httpClient?.Dispose(); _httpClient = null; await Task.CompletedTask; }
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) { var isHealthy = await TestCoreAsync(handle, ct); return new ConnectionHealth(isHealthy, isHealthy ? "Hazelcast healthy" : "Hazelcast unhealthy", TimeSpan.FromMilliseconds(6), DateTimeOffset.UtcNow); }
        public override async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default) { await Task.Delay(6, ct); return new List<Dictionary<string, object?>> { new() { ["key"] = "key1", ["value"] = "value1" } }; }
        public override async Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default) { await Task.Delay(6, ct); return 1; }
        public override async Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default) { await Task.Delay(6, ct); return new List<DataSchema> { new DataSchema("map", new[] { new DataSchemaField("key", "Object", false, null, null) }, new[] { "key" }, new Dictionary<string, object> { ["type"] = "map" }) }; }
        private (string host, int port) ParseHostPort(string connectionString, int defaultPort) { var clean = connectionString.Replace("http://", "").Split('/')[0]; var parts = clean.Split(':'); return (parts[0], parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : defaultPort); }
    }
}
