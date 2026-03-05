using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;
using Npgsql;
using ConnectionState = System.Data.ConnectionState;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.Database;

/// <summary>
/// TimescaleDB connection strategy using Npgsql driver (PostgreSQL wire protocol).
/// Provides production-ready connectivity to TimescaleDB time-series database.
/// </summary>
public sealed class TimescaleDbConnectionStrategy : DatabaseConnectionStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "timescaledb";

    /// <inheritdoc/>
    public override string DisplayName => "TimescaleDB";

    /// <inheritdoc/>
    public override ConnectionStrategyCapabilities Capabilities => new();

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "TimescaleDB time-series database connection. PostgreSQL extension optimized for time-series data with " +
        "automatic partitioning (hypertables), columnar compression, continuous aggregates, and retention policies. " +
        "Ideal for IoT, monitoring, analytics, and any time-stamped data workloads.";

    /// <inheritdoc/>
    public override string[] Tags =>
    [
        "timescaledb", "timeseries", "sql", "postgresql-compatible", "iot",
        "monitoring", "analytics", "hypertable", "database", "compression"
    ];

    /// <summary>
    /// Initializes a new instance of <see cref="TimescaleDbConnectionStrategy"/>.
    /// </summary>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public TimescaleDbConnectionStrategy(ILogger? logger = null) : base(logger) { }

    /// <inheritdoc/>
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
    {
        var connectionString = config.ConnectionString;

        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("Connection string is required for TimescaleDB connection.");

        var connection = new NpgsqlConnection(connectionString);

        try
        {
            await connection.OpenAsync(ct);

            var connectionInfo = new Dictionary<string, object>
            {
                ["Provider"] = "Npgsql/TimescaleDB",
                ["ServerVersion"] = connection.ServerVersion,
                ["Database"] = connection.Database!,
                ["Host"] = connection.Host!,
                ["Port"] = connection.Port,
                ["State"] = connection.State.ToString()
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
        var connection = handle.GetConnection<NpgsqlConnection>();

        if (connection.State != ConnectionState.Open)
            return false;

        try
        {
            await using var cmd = new NpgsqlCommand("SELECT 1", connection);
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
        var connection = handle.GetConnection<NpgsqlConnection>();

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
        var connection = handle.GetConnection<NpgsqlConnection>();

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

            await using var cmd = new NpgsqlCommand("SELECT extversion FROM pg_extension WHERE extname='timescaledb'", connection);
            // Finding 1899: Null result means extension not installed — report as unhealthy.
            var tsVersionObj = await cmd.ExecuteScalarAsync(ct);
            sw.Stop();

            if (tsVersionObj == null || tsVersionObj == DBNull.Value)
            {
                return new ConnectionHealth(
                    IsHealthy: false,
                    StatusMessage: "TimescaleDB extension not installed on this PostgreSQL server",
                    Latency: sw.Elapsed,
                    CheckedAt: DateTimeOffset.UtcNow);
            }

            return new ConnectionHealth(
                IsHealthy: true,
                StatusMessage: $"TimescaleDB {tsVersionObj} (PostgreSQL {connection.ServerVersion}) - Database: {connection.Database}",
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

        var connection = handle.GetConnection<NpgsqlConnection>();
        await using var cmd = new NpgsqlCommand(query, connection);

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
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
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

        var connection = handle.GetConnection<NpgsqlConnection>();
        await using var cmd = new NpgsqlCommand(command, connection);

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

        var connection = handle.GetConnection<NpgsqlConnection>();

        const string schemaQuery = @"
            SELECT
                t.table_name,
                c.column_name,
                c.data_type,
                c.is_nullable,
                c.character_maximum_length,
                CASE WHEN pk.column_name IS NOT NULL THEN true ELSE false END as is_primary_key
            FROM information_schema.tables t
            INNER JOIN information_schema.columns c
                ON t.table_name = c.table_name AND t.table_schema = c.table_schema
            LEFT JOIN (
                SELECT ku.table_name, ku.column_name
                FROM information_schema.table_constraints tc
                INNER JOIN information_schema.key_column_usage ku
                    ON tc.constraint_name = ku.constraint_name
                WHERE tc.constraint_type = 'PRIMARY KEY'
            ) pk ON c.table_name = pk.table_name AND c.column_name = pk.column_name
            WHERE t.table_schema = 'public' AND t.table_type = 'BASE TABLE'
            ORDER BY t.table_name, c.ordinal_position";

        var schemas = new Dictionary<string, (List<DataSchemaField> Fields, List<string> PrimaryKeys)>();

        await using var cmd = new NpgsqlCommand(schemaQuery, connection);
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

            schemas[tableName].Fields.Add(new DataSchemaField(
                Name: columnName,
                DataType: dataType,
                Nullable: isNullable,
                MaxLength: maxLength,
                Properties: null
            ));

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
            errors.Add("ConnectionString is required for TimescaleDB connection.");
        }
        else
        {
            try
            {
                var builder = new NpgsqlConnectionStringBuilder(config.ConnectionString);

                if (string.IsNullOrWhiteSpace(builder.Host))
                    errors.Add("Host is required in the connection string.");

                if (string.IsNullOrWhiteSpace(builder.Database))
                    errors.Add("Database name is required in the connection string.");
            }
            catch (Exception ex)
            {
                errors.Add($"Invalid TimescaleDB connection string format: {ex.Message}");
            }
        }

        if (config.Timeout <= TimeSpan.Zero)
            errors.Add("Timeout must be a positive duration.");

        if (config.MaxRetries < 0)
            errors.Add("MaxRetries must be non-negative.");

        return Task.FromResult((errors.Count == 0, errors.ToArray()));
    }
}
