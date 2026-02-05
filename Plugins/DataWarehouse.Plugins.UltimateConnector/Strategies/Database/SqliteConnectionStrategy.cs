using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.Database;

/// <summary>
/// SQLite connection strategy using Microsoft.Data.Sqlite driver.
/// Provides production-ready connectivity to SQLite 3 embedded database files.
/// </summary>
public sealed class SqliteConnectionStrategy : DatabaseConnectionStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "sqlite";

    /// <inheritdoc/>
    public override string DisplayName => "SQLite";

    /// <inheritdoc/>
    public override ConnectionStrategyCapabilities Capabilities => new();

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "SQLite embedded relational database connection. Serverless, zero-configuration SQL database engine " +
        "that stores data in a single file. Ideal for embedded applications, mobile apps, testing, and " +
        "lightweight data storage. Supports ACID transactions and full SQL syntax.";

    /// <inheritdoc/>
    public override string[] Tags =>
    [
        "sqlite", "sql", "embedded", "serverless", "file-based", "local",
        "mobile", "lightweight", "relational", "database"
    ];

    /// <summary>
    /// Initializes a new instance of <see cref="SqliteConnectionStrategy"/>.
    /// </summary>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public SqliteConnectionStrategy(ILogger? logger = null) : base(logger) { }

    /// <inheritdoc/>
    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
    {
        var connectionString = config.ConnectionString;

        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("Connection string is required for SQLite connection.");

        var connection = new SqliteConnection(connectionString);

        try
        {
            await connection.OpenAsync(ct);

            var connectionInfo = new Dictionary<string, object>
            {
                ["Provider"] = "Microsoft.Data.Sqlite",
                ["ServerVersion"] = connection.ServerVersion,
                ["DataSource"] = connection.DataSource,
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
        var connection = handle.GetConnection<SqliteConnection>();

        if (connection.State != ConnectionState.Open)
            return false;

        try
        {
            await using var cmd = new SqliteCommand("SELECT 1", connection);
            var result = await cmd.ExecuteScalarAsync(ct);
            return result != null && Convert.ToInt64(result) == 1;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc/>
    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct)
    {
        var connection = handle.GetConnection<SqliteConnection>();

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
        var connection = handle.GetConnection<SqliteConnection>();

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

            await using var cmd = new SqliteCommand("SELECT 1", connection);
            await cmd.ExecuteScalarAsync(ct);
            sw.Stop();

            return new ConnectionHealth(
                IsHealthy: true,
                StatusMessage: $"SQLite {connection.ServerVersion} - DataSource: {connection.DataSource}",
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

        var connection = handle.GetConnection<SqliteConnection>();

        await using var cmd = new SqliteCommand(query, connection);

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

        var connection = handle.GetConnection<SqliteConnection>();

        await using var cmd = new SqliteCommand(command, connection);

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

        var connection = handle.GetConnection<SqliteConnection>();

        // Get all tables
        const string tablesQuery = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";

        var schemas = new List<DataSchema>();

        await using (var tablesCmd = new SqliteCommand(tablesQuery, connection))
        await using (var tablesReader = await tablesCmd.ExecuteReaderAsync(ct))
        {
            while (await tablesReader.ReadAsync(ct))
            {
                var tableName = tablesReader.GetString(0);

                // Get table info
                var tableInfoQuery = $"PRAGMA table_info('{tableName}')";
                var fields = new List<DataSchemaField>();
                var primaryKeys = new List<string>();

                await using (var infoCmd = new SqliteCommand(tableInfoQuery, connection))
                await using (var infoReader = await infoCmd.ExecuteReaderAsync(ct))
                {
                    while (await infoReader.ReadAsync(ct))
                    {
                        var columnName = infoReader.GetString(1);
                        var dataType = infoReader.GetString(2);
                        var notNull = infoReader.GetInt32(3) == 1;
                        var isPrimaryKey = infoReader.GetInt32(5) == 1;

                        var field = new DataSchemaField(
                            Name: columnName,
                            DataType: dataType,
                            Nullable: !notNull,
                            MaxLength: null,
                            Properties: null
                        );

                        fields.Add(field);

                        if (isPrimaryKey)
                        {
                            primaryKeys.Add(columnName);
                        }
                    }
                }

                schemas.Add(new DataSchema(
                    Name: tableName,
                    Fields: fields.ToArray(),
                    PrimaryKeys: primaryKeys.ToArray(),
                    Metadata: null
                ));
            }
        }

        return schemas;
    }

    /// <inheritdoc/>
    public override Task<(bool IsValid, string[] Errors)> ValidateConfigAsync(
        ConnectionConfig config, CancellationToken ct = default)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(config.ConnectionString))
        {
            errors.Add("ConnectionString is required for SQLite connection.");
        }
        else
        {
            // Validate connection string format
            try
            {
                var builder = new SqliteConnectionStringBuilder(config.ConnectionString);

                if (string.IsNullOrWhiteSpace(builder.DataSource))
                    errors.Add("DataSource (file path or :memory:) is required in the connection string.");
            }
            catch (Exception ex)
            {
                errors.Add($"Invalid SQLite connection string format: {ex.Message}");
            }
        }

        if (config.Timeout <= TimeSpan.Zero)
            errors.Add("Timeout must be a positive duration.");

        if (config.MaxRetries < 0)
            errors.Add("MaxRetries must be non-negative.");

        return Task.FromResult((errors.Count == 0, errors.ToArray()));
    }
}
