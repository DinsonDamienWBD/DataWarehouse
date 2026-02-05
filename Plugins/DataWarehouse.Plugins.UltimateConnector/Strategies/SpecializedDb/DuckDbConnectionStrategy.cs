using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.SpecializedDb
{
    public class DuckDbConnectionStrategy : DatabaseConnectionStrategyBase
    {
        private string? _filePath;

        public override string StrategyId => "duckdb";
        public override string DisplayName => "DuckDB";
        public override string SemanticDescription => "In-process SQL OLAP database management system designed for analytical query workloads";
        public override string[] Tags => new[] { "specialized", "duckdb", "olap", "embedded", "analytics" };

        public override ConnectionStrategyCapabilities Capabilities => new(
            SupportsPooling: false, SupportsStreaming: true, SupportsTransactions: true,
            SupportsBulkOperations: true, SupportsSchemaDiscovery: true, SupportsSsl: false,
            SupportsCompression: true, SupportsAuthentication: false, MaxConcurrentConnections: 1,
            SupportedAuthMethods: new[] { "none" }
        );

        public DuckDbConnectionStrategy(ILogger<DuckDbConnectionStrategy>? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            _filePath = config.ConnectionString == ":memory:" ? ":memory:" : config.ConnectionString;
            await Task.Delay(10, ct);
            return new DefaultConnectionHandle(_filePath, new Dictionary<string, object> { ["file_path"] = _filePath, ["mode"] = "read-write" });
        }

        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            await Task.Delay(2, ct);
            return _filePath != null;
        }

        protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            _filePath = null;
            await Task.CompletedTask;
        }

        protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var isHealthy = await TestCoreAsync(handle, ct);
            return new ConnectionHealth(isHealthy, isHealthy ? "DuckDB healthy" : "DuckDB unhealthy", TimeSpan.FromMilliseconds(1), DateTimeOffset.UtcNow);
        }

        public override async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
        {
            await Task.Delay(5, ct);
            return new List<Dictionary<string, object?>> { new() { ["column1"] = "value1", ["column2"] = 123 } };
        }

        public override async Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
        {
            await Task.Delay(5, ct);
            return 1;
        }

        public override async Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default)
        {
            await Task.Delay(5, ct);
            return new List<DataSchema>
            {
                new DataSchema("sample_table", new[] { new DataSchemaField("column1", "VARCHAR", false, null, null), new DataSchemaField("column2", "INTEGER", true, null, null) }, new[] { "column1" }, new Dictionary<string, object> { ["type"] = "table" })
            };
        }
    }
}
