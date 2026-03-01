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
/// Citus connection strategy using Npgsql driver (PostgreSQL wire protocol).
/// Provides production-ready connectivity to Citus distributed PostgreSQL database.
/// </summary>
public sealed class CitusConnectionStrategy : DatabaseConnectionStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "citus";

    /// <inheritdoc/>
    public override string DisplayName => "Citus";

    /// <inheritdoc/>
    public override ConnectionStrategyCapabilities Capabilities => new();

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Citus distributed PostgreSQL database connection. Transforms PostgreSQL into a distributed database " +
        "with horizontal scaling, sharding, and parallel query execution. Maintains full PostgreSQL compatibility " +
        "while providing distributed tables, reference tables, and columnar storage for analytics.";

    /// <inheritdoc/>
    public override string[] Tags =>
    [
        "citus", "distributed", "sql", "postgresql-compatible", "sharding",
        "scalable", "analytics", "parallel", "database", "microsoft"
    ];

    /// <summary>
    /// Initializes a new instance of <see cref="CitusConnectionStrategy"/>.
    /// </summary>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public CitusConnectionStrategy(ILogger? logger = null) : base(logger) { }

    /// <inheritdoc/>
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
    {
        var connectionString = config.ConnectionString;

        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("Connection string is required for Citus connection.");

        var connection = new NpgsqlConnection(connectionString);

        try
        {
            await connection.OpenAsync(ct);

            var connectionInfo = new Dictionary<string, object>
            {
                ["Provider"] = "Npgsql/Citus",
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
        if (connection.State != ConnectionState.Open) return false;

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
            await connection.CloseAsync();

        await connection.DisposeAsync();

        if (handle is DefaultConnectionHandle defaultHandle)
            defaultHandle.MarkDisconnected();
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
                return new ConnectionHealth(false, $"Connection not open (State: {connection.State})", sw.Elapsed, DateTimeOffset.UtcNow);
            }

            await using var cmd = new NpgsqlCommand("SELECT extversion FROM pg_extension WHERE extname='citus'", connection);
            // Finding 1900: Null result means extension not installed — report as unhealthy.
            var citusVersionObj = await cmd.ExecuteScalarAsync(ct);
            sw.Stop();

            if (citusVersionObj == null || citusVersionObj == DBNull.Value)
            {
                return new ConnectionHealth(false, "Citus extension not installed on this PostgreSQL server", sw.Elapsed, DateTimeOffset.UtcNow);
            }

            return new ConnectionHealth(true, $"Citus {citusVersionObj} (PostgreSQL {connection.ServerVersion}) - Database: {connection.Database}", sw.Elapsed, DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ConnectionHealth(false, $"Health check failed: {ex.Message}", sw.Elapsed, DateTimeOffset.UtcNow);
        }
    }

    /// <inheritdoc/>
    public override async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(
        IConnectionHandle handle, string query, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        var connection = handle.GetConnection<NpgsqlConnection>();
        await using var cmd = new NpgsqlCommand(query, connection);

        if (parameters != null)
            foreach (var (key, value) in parameters)
                cmd.Parameters.AddWithValue(key, value ?? DBNull.Value);

        var results = new List<Dictionary<string, object?>>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, object?>();
            for (int i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            results.Add(row);
        }

        return results;
    }

    /// <inheritdoc/>
    public override async Task<int> ExecuteNonQueryAsync(
        IConnectionHandle handle, string command, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        var connection = handle.GetConnection<NpgsqlConnection>();
        await using var cmd = new NpgsqlCommand(command, connection);

        if (parameters != null)
            foreach (var (key, value) in parameters)
                cmd.Parameters.AddWithValue(key, value ?? DBNull.Value);

        return await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <inheritdoc/>
    public override async Task<IReadOnlyList<DataSchema>> GetSchemaAsync(IConnectionHandle handle, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        var connection = handle.GetConnection<NpgsqlConnection>();

        const string schemaQuery = @"
            SELECT t.table_name, c.column_name, c.data_type, c.is_nullable, c.character_maximum_length,
                   CASE WHEN pk.column_name IS NOT NULL THEN true ELSE false END as is_primary_key
            FROM information_schema.tables t
            INNER JOIN information_schema.columns c ON t.table_name = c.table_name AND t.table_schema = c.table_schema
            LEFT JOIN (SELECT ku.table_name, ku.column_name FROM information_schema.table_constraints tc
                       INNER JOIN information_schema.key_column_usage ku ON tc.constraint_name = ku.constraint_name
                       WHERE tc.constraint_type = 'PRIMARY KEY') pk ON c.table_name = pk.table_name AND c.column_name = pk.column_name
            WHERE t.table_schema = 'public' AND t.table_type = 'BASE TABLE'
            ORDER BY t.table_name, c.ordinal_position";

        var schemas = new Dictionary<string, (List<DataSchemaField> Fields, List<string> PrimaryKeys)>();
        await using var cmd = new NpgsqlCommand(schemaQuery, connection);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            var tableName = reader.GetString(0);
            if (!schemas.ContainsKey(tableName))
                schemas[tableName] = (new List<DataSchemaField>(), new List<string>());

            schemas[tableName].Fields.Add(new DataSchemaField(
                reader.GetString(1), reader.GetString(2), reader.GetString(3) == "YES",
                reader.IsDBNull(4) ? null : reader.GetInt32(4), null));

            if (reader.GetBoolean(5))
                schemas[tableName].PrimaryKeys.Add(reader.GetString(1));
        }

        return schemas.Select(kvp => new DataSchema(kvp.Key, kvp.Value.Fields.ToArray(), kvp.Value.PrimaryKeys.ToArray(), null)).ToList();
    }

    /// <inheritdoc/>
    public override Task<(bool IsValid, string[] Errors)> ValidateConfigAsync(ConnectionConfig config, CancellationToken ct = default)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(config.ConnectionString))
            errors.Add("ConnectionString is required for Citus connection.");
        else
        {
            try
            {
                var builder = new NpgsqlConnectionStringBuilder(config.ConnectionString);
                if (string.IsNullOrWhiteSpace(builder.Host)) errors.Add("Host is required.");
                if (string.IsNullOrWhiteSpace(builder.Database)) errors.Add("Database name is required.");
            }
            catch (Exception ex) { errors.Add($"Invalid connection string: {ex.Message}"); }
        }

        if (config.Timeout <= TimeSpan.Zero) errors.Add("Timeout must be positive.");
        if (config.MaxRetries < 0) errors.Add("MaxRetries must be non-negative.");

        return Task.FromResult((errors.Count == 0, errors.ToArray()));
    }
}
