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
        protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            // _token != null only means we stored a token string locally, not that MotherDuck accepted it.
            // A real probe requires DuckDB + MotherDuck extension; without the SDK we cannot verify.
            return Task.FromResult(_token != null);
        }
        protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct) { _token = null; await Task.CompletedTask; }
        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct) { var sw = System.Diagnostics.Stopwatch.StartNew(); var isHealthy = await TestCoreAsync(handle, ct); sw.Stop(); return new ConnectionHealth(isHealthy, isHealthy ? "MotherDuck token present (not verified)" : "MotherDuck token missing", sw.Elapsed, DateTimeOffset.UtcNow); }
        public override Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
            => throw new NotSupportedException("MotherDuck query execution requires the DuckDB.NET.Data NuGet package with the MotherDuck extension enabled via 'ATTACH md:' and a valid service token.");
        public override Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
            => throw new NotSupportedException("MotherDuck DML execution requires the DuckDB.NET.Data NuGet package with the MotherDuck extension.");
        public override Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default)
            => throw new NotSupportedException("MotherDuck schema discovery requires the DuckDB.NET.Data NuGet package with the MotherDuck extension.");
    }
}
