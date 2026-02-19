using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;


namespace DataWarehouse.Plugins.UltimateConnector.Strategies.Legacy
{
    /// <summary>
    /// OLE DB connection strategy for legacy data source access.
    /// Supports connection to Access, Excel (via ACE provider), and other OLE DB data sources
    /// with parameterized queries and schema discovery.
    /// </summary>
    public class OleDbConnectionStrategy : LegacyConnectionStrategyBase
    {
        public override string StrategyId => "oledb";
        public override string DisplayName => "OLE DB";
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to OLE DB data sources (Access, Excel, legacy databases) with parameterized queries and schema discovery.";
        public override string[] Tags => new[] { "oledb", "database", "legacy", "access", "excel" };

        public OleDbConnectionStrategy(ILogger? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            var connectionString = config.ConnectionString;
            var provider = GetConfiguration<string>(config, "Provider", "");
            var dataSource = GetConfiguration<string>(config, "DataSource", "");

            // Build connection string if not fully provided
            if (!string.IsNullOrEmpty(provider) && !string.IsNullOrEmpty(dataSource))
            {
                connectionString = $"Provider={provider};Data Source={dataSource};";
                var username = GetConfiguration<string>(config, "Username", "");
                var password = GetConfiguration<string>(config, "Password", "");
                if (!string.IsNullOrEmpty(username))
                    connectionString += $"User ID={username};Password={password};";
            }

            // Use DbProviderFactory for OLE DB
            var factory = GetOleDbFactory();
            var connection = factory.CreateConnection()!;
            connection.ConnectionString = connectionString;
            await connection.OpenAsync(ct);

            return new DefaultConnectionHandle(connection, new Dictionary<string, object>
            {
                ["protocol"] = "OLE DB",
                ["provider"] = provider,
                ["dataSource"] = dataSource,
                ["state"] = connection.State.ToString()
            });
        }

        protected override Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            try
            {
                var connection = handle.GetConnection<DbConnection>();
                return Task.FromResult(connection.State == System.Data.ConnectionState.Open);
            }
            catch { return Task.FromResult(false); }
        }

        protected override Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var connection = handle.GetConnection<DbConnection>();
            connection.Close();
            connection.Dispose();
            return Task.CompletedTask;
        }

        protected override Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            var connection = handle.GetConnection<DbConnection>();
            var isHealthy = connection.State == System.Data.ConnectionState.Open;
            return Task.FromResult(new ConnectionHealth(isHealthy,
                isHealthy ? $"OLE DB connected to {connection.DataSource}" : "OLE DB connection lost",
                TimeSpan.Zero, DateTimeOffset.UtcNow));
        }

        public override Task<string> EmulateProtocolAsync(IConnectionHandle handle, string protocolCommand, CancellationToken ct = default)
        {
            return ExecuteScalarAsync(handle, protocolCommand, ct);
        }

        public override Task<string> TranslateCommandAsync(IConnectionHandle handle, string modernCommand, CancellationToken ct = default)
        {
            return Task.FromResult($"{{\"original\":\"{modernCommand}\",\"translated\":\"{modernCommand}\",\"protocol\":\"OLE DB\"}}");
        }

        /// <summary>
        /// Executes a query and returns results.
        /// </summary>
        public async Task<OleDbQueryResult> ExecuteQueryAsync(IConnectionHandle handle, string sql,
            Dictionary<string, object?>? parameters = null, int? maxRows = null, CancellationToken ct = default)
        {
            var connection = handle.GetConnection<DbConnection>();
            using var command = connection.CreateCommand();
            command.CommandText = sql;

            if (parameters != null)
            {
                foreach (var (key, value) in parameters)
                {
                    var param = command.CreateParameter();
                    param.ParameterName = key;
                    param.Value = value ?? DBNull.Value;
                    command.Parameters.Add(param);
                }
            }

            var rows = new List<Dictionary<string, object?>>();
            using var reader = await command.ExecuteReaderAsync(ct);
            var rowCount = 0;

            while (await reader.ReadAsync(ct))
            {
                if (maxRows.HasValue && rowCount >= maxRows.Value) break;
                var row = new Dictionary<string, object?>();
                for (var i = 0; i < reader.FieldCount; i++)
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                rows.Add(row);
                rowCount++;
            }

            return new OleDbQueryResult { Success = true, Rows = rows, ColumnCount = reader.FieldCount };
        }

        /// <summary>
        /// Executes a non-query command.
        /// </summary>
        public async Task<int> ExecuteNonQueryAsync(IConnectionHandle handle, string sql,
            Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
        {
            var connection = handle.GetConnection<DbConnection>();
            using var command = connection.CreateCommand();
            command.CommandText = sql;

            if (parameters != null)
            {
                foreach (var (key, value) in parameters)
                {
                    var param = command.CreateParameter();
                    param.ParameterName = key;
                    param.Value = value ?? DBNull.Value;
                    command.Parameters.Add(param);
                }
            }

            return await command.ExecuteNonQueryAsync(ct);
        }

        /// <summary>
        /// Gets schema information.
        /// </summary>
        public Task<OleDbSchemaResult> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default)
        {
            var connection = handle.GetConnection<DbConnection>();
            var tables = new List<OleDbTableInfo>();

            var schemaTable = connection.GetSchema("Tables");
            foreach (DataRow row in schemaTable.Rows)
            {
                tables.Add(new OleDbTableInfo
                {
                    TableName = row["TABLE_NAME"]?.ToString() ?? "",
                    TableType = row["TABLE_TYPE"]?.ToString() ?? "",
                    Catalog = row.Table.Columns.Contains("TABLE_CATALOG") ? row["TABLE_CATALOG"]?.ToString() : null
                });
            }

            return Task.FromResult(new OleDbSchemaResult { Success = true, Tables = tables });
        }

        private async Task<string> ExecuteScalarAsync(IConnectionHandle handle, string sql, CancellationToken ct)
        {
            var connection = handle.GetConnection<DbConnection>();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            var result = await command.ExecuteScalarAsync(ct);
            return result?.ToString() ?? "";
        }

        private static DbProviderFactory GetOleDbFactory()
        {
            var type = Type.GetType("System.Data.OleDb.OleDbFactory, System.Data.OleDb");
            if (type != null)
            {
                var instanceField = type.GetField("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (instanceField?.GetValue(null) is DbProviderFactory factory)
                    return factory;
            }
            throw new InvalidOperationException("System.Data.OleDb is not available. Install the System.Data.OleDb NuGet package.");
        }
    }

    public sealed record OleDbQueryResult
    {
        public bool Success { get; init; }
        public List<Dictionary<string, object?>> Rows { get; init; } = new();
        public int ColumnCount { get; init; }
        public string? ErrorMessage { get; init; }
    }

    public sealed record OleDbSchemaResult
    {
        public bool Success { get; init; }
        public List<OleDbTableInfo> Tables { get; init; } = new();
    }

    public sealed record OleDbTableInfo
    {
        public required string TableName { get; init; }
        public required string TableType { get; init; }
        public string? Catalog { get; init; }
    }
}
