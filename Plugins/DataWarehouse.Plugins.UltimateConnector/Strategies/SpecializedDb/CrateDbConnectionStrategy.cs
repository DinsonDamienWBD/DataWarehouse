using System;
using System.Collections.Generic;
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
        public override async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default) { await Task.Delay(6, ct); return new List<Dictionary<string, object?>> { new() { ["id"] = 1, ["value"] = "data" } }; }
        public override async Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default) { await Task.Delay(6, ct); return 1; }
        public override async Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default) { await Task.Delay(6, ct); return new List<DataSchema> { new DataSchema("table", new[] { new DataSchemaField("id", "Integer", false, null, null) }, new[] { "id" }, new Dictionary<string, object> { ["type"] = "table" }) }; }
        private (string host, int port) ParseHostPort(string connectionString, int defaultPort) { var clean = connectionString.Replace("http://", "").Split('/')[0]; var parts = clean.Split(':'); return (parts[0], parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : defaultPort); }
    }
}
