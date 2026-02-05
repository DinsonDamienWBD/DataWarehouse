using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using ConnectionState = System.Data.ConnectionState;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.Database;

/// <summary>
/// Microsoft SQL Server connection strategy using Microsoft.Data.SqlClient driver.
/// Provides production-ready connectivity to SQL Server 2012+ and Azure SQL Database.
/// </summary>
public sealed class SqlServerConnectionStrategy : DatabaseConnectionStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "sqlserver";

    /// <inheritdoc/>
    public override string DisplayName => "SQL Server";

    /// <inheritdoc/>
    public override ConnectionStrategyCapabilities Capabilities => new();

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Microsoft SQL Server relational database connection. Enterprise-grade RDBMS with support for " +
        "ACID transactions, stored procedures, triggers, T-SQL, columnstore indexes, and in-memory OLTP. " +
        "Compatible with SQL Server 2012+, Azure SQL Database, and Azure SQL Managed Instance.";

    /// <inheritdoc/>
    public override string[] Tags =>
    [
        "sqlserver", "mssql", "sql", "microsoft", "tsql", "relational",
        "azure", "enterprise", "database", "windows"
    ];

    /// <summary>
    /// Initializes a new instance of <see cref="SqlServerConnectionStrategy"/>.
    /// </summary>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public SqlServerConnectionStrategy(ILogger? logger = null) : base(logger) { }

    /// <inheritdoc/>
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
    {
        var connectionString = config.ConnectionString;

        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("Connection string is required for SQL Server connection.");

        var connection = new SqlConnection(connectionString);

        try
        {
            await connection.OpenAsync(ct);

            var connectionInfo = new Dictionary<string, object>
            {
                ["Provider"] = "Microsoft.Data.SqlClient",
                ["ServerVersion"] = connection.ServerVersion,
                ["Database"] = connection.Database!,
                ["DataSource"] = connection.DataSource!,
                ["State"] = connection.State.ToString(),
                ["WorkstationId"] = connection.WorkstationId
            };

            return new DefaultConnectionHandle(connection, connectionInfo);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <inheritdoc/>
    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
    {
        var connection = handle.GetConnection<SqlConnection>();

        if (connection.State != ConnectionState.Open)
            return false;

        try
        {
            await using var cmd = new SqlCommand("SELECT 1", connection);
            var result = await cmd.ExecuteScalarAsync(ct);
            return result != null && Convert.ToInt32(result) == 1;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc/>
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct)
    {
        var connection = handle.GetConnection<SqlConnection>();

        if (connection.State != ConnectionState.Closed)
        {
            await connection.CloseAsync();
        }

        await connection.DisposeAsync();

        if (handle is DefaultConnectionHandle defaultHandle)
        {
            defaultHandle.MarkDisconnected();
        }
    }

    /// <inheritdoc/>
    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var connection = handle.GetConnection<SqlConnection>();

        try
        {
            if (connection.State != ConnectionState.Open)
            {
                sw.Stop();
                return new ConnectionHealth(
                    IsHealthy: false,
                    StatusMessage: $"Connection is not open (State: {connection.State})",
                    Latency: sw.Elapsed,
                    CheckedAt: DateTimeOffset.UtcNow);
            }

            await using var cmd = new SqlCommand("SELECT 1", connection);
            await cmd.ExecuteScalarAsync(ct);
            sw.Stop();

            return new ConnectionHealth(
                IsHealthy: true,
                StatusMessage: $"SQL Server {connection.ServerVersion} - Database: {connection.Database}",
                Latency: sw.Elapsed,
                CheckedAt: DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ConnectionHealth(
                IsHealthy: false,
                StatusMessage: $"Health check failed: {ex.Message}",
                Latency: sw.Elapsed,
                CheckedAt: DateTimeOffset.UtcNow);
        }
    }

    /// <inheritdoc/>
    public override async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(
        IConnectionHandle handle,
        string query,
        Dictionary<string, object?>? parameters = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        var connection = handle.GetConnection<SqlConnection>();

        await using var cmd = new SqlCommand(query, connection);

        // Add parameters if provided
        if (parameters != null)
        {
            foreach (var (key, value) in parameters)
            {
                cmd.Parameters.AddWithValue(key, value ?? DBNull.Value);
            }
        }

        var results = new List<Dictionary<string, object?>>();

        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, object?>();
            for (int i = 0; i < reader.FieldCount; i++)
            {
                var columnName = reader.GetName(i);
                var value = reader.IsDBNull(i) ? null : reader.GetValue(i);
                row[columnName] = value;
            }
            results.Add(row);
        }

        return results;
    }

    /// <inheritdoc/>
    public override async Task<int> ExecuteNonQueryAsync(
        IConnectionHandle handle,
        string command,
        Dictionary<string, object?>? parameters = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        var connection = handle.GetConnection<SqlConnection>();

        await using var cmd = new SqlCommand(command, connection);

        // Add parameters if provided
        if (parameters != null)
        {
            foreach (var (key, value) in parameters)
            {
                cmd.Parameters.AddWithValue(key, value ?? DBNull.Value);
            }
        }

        return await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <inheritdoc/>
    public override async Task<IReadOnlyList<DataSchema>> GetSchemaAsync(
        IConnectionHandle handle,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);

        var connection = handle.GetConnection<SqlConnection>();

        const string schemaQuery = @"
            SELECT
                t.TABLE_NAME,
                c.COLUMN_NAME,
                c.DATA_TYPE,
                c.IS_NULLABLE,
                c.CHARACTER_MAXIMUM_LENGTH,
                CASE WHEN pk.COLUMN_NAME IS NOT NULL THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END as IS_PRIMARY_KEY
            FROM INFORMATION_SCHEMA.TABLES t
            INNER JOIN INFORMATION_SCHEMA.COLUMNS c
                ON t.TABLE_NAME = c.TABLE_NAME AND t.TABLE_SCHEMA = c.TABLE_SCHEMA
            LEFT JOIN (
                SELECT ku.TABLE_NAME, ku.COLUMN_NAME
                FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
                INNER JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE ku
                    ON tc.CONSTRAINT_NAME = ku.CONSTRAINT_NAME
                WHERE tc.CONSTRAINT_TYPE = 'PRIMARY KEY'
            ) pk ON c.TABLE_NAME = pk.TABLE_NAME AND c.COLUMN_NAME = pk.COLUMN_NAME
            WHERE t.TABLE_SCHEMA = 'dbo' AND t.TABLE_TYPE = 'BASE TABLE'
            ORDER BY t.TABLE_NAME, c.ORDINAL_POSITION";

        var schemas = new Dictionary<string, (List<DataSchemaField> Fields, List<string> PrimaryKeys)>();

        await using var cmd = new SqlCommand(schemaQuery, connection);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            var tableName = reader.GetString(0);
            var columnName = reader.GetString(1);
            var dataType = reader.GetString(2);
            var isNullable = reader.GetString(3) == "YES";
            var maxLength = reader.IsDBNull(4) ? (int?)null : reader.GetInt32(4);
            var isPrimaryKey = reader.GetBoolean(5);

            if (!schemas.ContainsKey(tableName))
            {
                schemas[tableName] = (new List<DataSchemaField>(), new List<string>());
            }

            var field = new DataSchemaField(
                Name: columnName,
                DataType: dataType,
                Nullable: isNullable,
                MaxLength: maxLength,
                Properties: null
            );

            schemas[tableName].Fields.Add(field);

            if (isPrimaryKey)
            {
                schemas[tableName].PrimaryKeys.Add(columnName);
            }
        }

        return schemas.Select(kvp => new DataSchema(
            Name: kvp.Key,
            Fields: kvp.Value.Fields.ToArray(),
            PrimaryKeys: kvp.Value.PrimaryKeys.ToArray(),
            Metadata: null
        )).ToList();
    }

    /// <inheritdoc/>
    public override Task<(bool IsValid, string[] Errors)> ValidateConfigAsync(
        ConnectionConfig config, CancellationToken ct = default)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(config.ConnectionString))
        {
            errors.Add("ConnectionString is required for SQL Server connection.");
        }
        else
        {
            // Validate connection string format
            try
            {
                var builder = new SqlConnectionStringBuilder(config.ConnectionString);

                if (string.IsNullOrWhiteSpace(builder.DataSource))
                    errors.Add("DataSource (server) is required in the connection string.");

                if (string.IsNullOrWhiteSpace(builder.InitialCatalog))
                    errors.Add("InitialCatalog (database) is required in the connection string.");
            }
            catch (Exception ex)
            {
                errors.Add($"Invalid SQL Server connection string format: {ex.Message}");
            }
        }

        if (config.Timeout <= TimeSpan.Zero)
            errors.Add("Timeout must be a positive duration.");

        if (config.MaxRetries < 0)
            errors.Add("MaxRetries must be non-negative.");

        return Task.FromResult((errors.Count == 0, errors.ToArray()));
    }
}
