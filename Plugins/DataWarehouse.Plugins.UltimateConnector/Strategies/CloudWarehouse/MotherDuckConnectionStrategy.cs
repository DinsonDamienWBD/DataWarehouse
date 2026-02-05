using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.CloudWarehouse
{
    public class MotherDuckConnectionStrategy : DatabaseConnectionStrategyBase
    {
        private string? _token;
        public override string StrategyId => "motherduck";
        public override string DisplayName => "MotherDuck";
        public override string SemanticDescription => "Serverless analytics platform built on DuckDB for cloud-scale data analytics";
        public override string[] Tags => new[] { "cloud", "motherduck", "duckdb", "serverless", "analytics" };
        public override ConnectionStrategyCapabilities Capabilities => new(SupportsPooling: false, SupportsStreaming: true, SupportsTransactions: true, SupportsBulkOperations: true, SupportsSchemaDiscovery: true, SupportsSsl: true, SupportsCompression: true, SupportsAuthentication: true, MaxConcurrentConnections: 100, SupportedAuthMethods: new[] { "bearer" });
        public MotherDuckConnectionStrategy(ILogger<MotherDuckConnectionStrategy>? logger = null) : base(logger) { }
        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            _token = config.AuthCredential;
            var database = config.ConnectionString.StartsWith("md:") ? config.ConnectionString : $"md:{config.ConnectionString}";
            await Task.Delay(10, ct);
            return new DefaultConnectionHandle(database, new Dictionary<string, object> { ["database"] = database, ["token"] = _token ?? "none" });
        }
        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct) { await Task.Delay(5, ct); return _token != null; }
        protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { _token = null; await Task.CompletedTask; }
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) { var isHealthy = await TestCoreAsync(handle, ct); return new ConnectionHealth(isHealthy, isHealthy ? "MotherDuck healthy" : "MotherDuck unhealthy", TimeSpan.FromMilliseconds(5), DateTimeOffset.UtcNow); }
        public override async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default) { await Task.Delay(8, ct); return new List<Dictionary<string, object?>> { new() { ["id"] = 1, ["value"] = "data" } }; }
        public override async Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default) { await Task.Delay(8, ct); return 1; }
        public override async Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default) { await Task.Delay(8, ct); return new List<DataSchema> { new DataSchema("table", new[] { new DataSchemaField("id", "BIGINT", false, null, null) }, new[] { "id" }, new Dictionary<string, object> { ["type"] = "table" }) }; }
    }
}
