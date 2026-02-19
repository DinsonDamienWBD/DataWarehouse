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
    /// ODBC connection strategy for legacy and universal database access.
    /// Uses System.Data.Odbc for DSN or connection string-based connectivity,
    /// parameterized queries, and bulk fetch operations.
    /// </summary>
    public class OdbcConnectionStrategy : LegacyConnectionStrategyBase
    {
        public override string StrategyId => "odbc";
        public override string DisplayName => "ODBC";
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to any ODBC-compliant data source via DSN or connection string with parameterized queries and bulk fetch.";
        public override string[] Tags => new[] { "odbc", "database", "legacy", "universal", "sql" };

        public OdbcConnectionStrategy(ILogger? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            // Support both DSN and full connection string
            var connectionString = config.ConnectionString;
            var dsn = GetConfiguration<string>(config, "DSN", "");
            if (!string.IsNullOrEmpty(dsn) && string.IsNullOrEmpty(connectionString))
                connectionString = $"DSN={dsn}";

            var username = GetConfiguration<string>(config, "Username", "");
            var password = GetConfiguration<string>(config, "Password", "");
            if (!string.IsNullOrEmpty(username))
                connectionString += $";UID={username};PWD={password}";

            // Use DbProviderFactory to create ODBC connection portably
            var factory = GetOdbcFactory();
            var connection = factory.CreateConnection()!;
            connection.ConnectionString = connectionString;
            await connection.OpenAsync(ct);

            return new DefaultConnectionHandle(connection, new Dictionary<string, object>
            {
                ["protocol"] = "ODBC",
                ["datasource"] = dsn ?? connectionString,
                ["state"] = connection.State.ToString()
            });
        }

        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            try
            {
                var connection = handle.GetConnection<DbConnection>();
                return connection.State == ConnectionState.Open;
            }
            catch { return false; }
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
            var isHealthy = connection.State == ConnectionState.Open;
            return Task.FromResult(new ConnectionHealth(isHealthy,
                isHealthy ? $"ODBC connected to {connection.Database}" : "ODBC connection lost",
                TimeSpan.Zero, DateTimeOffset.UtcNow));
        }

        public override Task<string> EmulateProtocolAsync(IConnectionHandle handle, string protocolCommand, CancellationToken ct = default)
        {
            return ExecuteScalarQueryAsync(handle, protocolCommand, ct);
        }

        public override Task<string> TranslateCommandAsync(IConnectionHandle handle, string modernCommand, CancellationToken ct = default)
        {
            // ODBC uses standard SQL, minimal translation needed
            return Task.FromResult($"{{\"original\":\"{modernCommand}\",\"translated\":\"{modernCommand}\",\"protocol\":\"ODBC\"}}");
        }

        /// <summary>
        /// Executes a parameterized query and returns results.
        /// </summary>
        public async Task<OdbcQueryResult> ExecuteQueryAsync(IConnectionHandle handle, string sql,
            Dictionary<string, object?>? parameters = null, int? maxRows = null, CancellationToken ct = default)
        {
            var connection = handle.GetConnection<DbConnection>();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = 60;

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
                {
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }
                rows.Add(row);
                rowCount++;
            }

            return new OdbcQueryResult
            {
                Success = true,
                Rows = rows,
                ColumnCount = reader.FieldCount
            };
        }

        /// <summary>
        /// Executes a non-query command (INSERT, UPDATE, DELETE, DDL).
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
        /// Executes a scalar query.
        /// </summary>
        public async Task<string> ExecuteScalarQueryAsync(IConnectionHandle handle, string sql, CancellationToken ct = default)
        {
            var connection = handle.GetConnection<DbConnection>();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            var result = await command.ExecuteScalarAsync(ct);
            return result?.ToString() ?? "";
        }

        /// <summary>
        /// Gets the schema (tables and columns) of the connected data source.
        /// </summary>
        public async Task<OdbcSchemaResult> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default)
        {
            var connection = handle.GetConnection<DbConnection>();
            var tables = new List<OdbcTableSchema>();

            var schemaTable = connection.GetSchema("Tables");
            foreach (DataRow row in schemaTable.Rows)
            {
                var tableName = row["TABLE_NAME"]?.ToString() ?? "";
                var tableType = row["TABLE_TYPE"]?.ToString() ?? "";
                tables.Add(new OdbcTableSchema
                {
                    TableName = tableName,
                    TableType = tableType,
                    Catalog = row["TABLE_CATALOG"]?.ToString(),
                    Schema = row["TABLE_SCHEMA"]?.ToString()
                });
            }

            await Task.CompletedTask;
            return new OdbcSchemaResult { Success = true, Tables = tables };
        }

        private static DbProviderFactory GetOdbcFactory()
        {
            // Use reflection to load System.Data.Odbc factory
            var type = Type.GetType("System.Data.Odbc.OdbcFactory, System.Data.Odbc");
            if (type != null)
            {
                var instanceField = type.GetField("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (instanceField?.GetValue(null) is DbProviderFactory factory)
                    return factory;
            }
            throw new InvalidOperationException("System.Data.Odbc is not available. Install the System.Data.Odbc NuGet package.");
        }
    }

    public sealed record OdbcQueryResult
    {
        public bool Success { get; init; }
        public List<Dictionary<string, object?>> Rows { get; init; } = new();
        public int ColumnCount { get; init; }
        public string? ErrorMessage { get; init; }
    }

    public sealed record OdbcSchemaResult
    {
        public bool Success { get; init; }
        public List<OdbcTableSchema> Tables { get; init; } = new();
    }

    public sealed record OdbcTableSchema
    {
        public required string TableName { get; init; }
        public required string TableType { get; init; }
        public string? Catalog { get; init; }
        public string? Schema { get; init; }
    }
}
