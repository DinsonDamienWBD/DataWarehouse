using System.Data;
using System.Runtime.CompilerServices;
using System.Text;
using DataWarehouse.SDK.Connectors;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using Microsoft.Data.Sqlite;
using SdkConnectionState = DataWarehouse.SDK.Connectors.ConnectionState;
using AdoConnectionState = System.Data.ConnectionState;

namespace DataWarehouse.Plugins.DataConnectors;

/// <summary>
/// Production-ready SQLite database connector plugin.
/// Provides full CRUD operations with real database connectivity via Microsoft.Data.Sqlite.
/// Supports file-based and in-memory databases, schema discovery, parameterized queries, transactions, and bulk operations.
/// </summary>
public class SqliteConnectorPlugin : DatabaseConnectorPluginBase
{
    private SqliteConnection? _connection;
    private string? _connectionString;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private readonly Dictionary<string, DataSchema> _schemaCache = new();
    private SqliteConnectorConfig _config = new();
    private bool _isInMemory;

    /// <inheritdoc />
    public override string Id => "datawarehouse.connector.sqlite";

    /// <inheritdoc />
    public override string Name => "SQLite Connector";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    public override string ConnectorId => "sqlite";

    /// <inheritdoc />
    public override PluginCategory Category => PluginCategory.InfrastructureProvider;

    /// <inheritdoc />
    public override ConnectorCapabilities Capabilities =>
        ConnectorCapabilities.Read |
        ConnectorCapabilities.Write |
        ConnectorCapabilities.Schema |
        ConnectorCapabilities.Transactions |
        ConnectorCapabilities.BulkOperations;

    /// <summary>
    /// Configures the connector with additional options.
    /// </summary>
    /// <param name="config">SQLite-specific configuration.</param>
    public void Configure(SqliteConnectorConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <inheritdoc />
    protected override async Task<ConnectionResult> EstablishConnectionAsync(ConnectorConfig config, CancellationToken ct)
    {
        await _connectionLock.WaitAsync(ct);
        try
        {
            _connectionString = config.ConnectionString;

            if (string.IsNullOrWhiteSpace(_connectionString))
            {
                return new ConnectionResult(false, "Connection string is required", null);
            }

            // Check if in-memory database
            _isInMemory = _connectionString.Contains(":memory:", StringComparison.OrdinalIgnoreCase);

            // Create connection
            _connection = new SqliteConnection(_connectionString);

            // Configure connection based on config
            var connectionStringBuilder = new SqliteConnectionStringBuilder(_connectionString);

            if (_config.CacheSize > 0)
            {
                connectionStringBuilder.Cache = SqliteCacheMode.Default;
            }

            if (_config.EnableForeignKeys)
            {
                // Foreign keys will be enabled after connection opens
            }

            _connection.ConnectionString = connectionStringBuilder.ToString();

            // Open the connection
            await _connection.OpenAsync(ct);

            // Configure connection settings
            if (_config.EnableForeignKeys)
            {
                await using var cmd = _connection.CreateCommand();
                cmd.CommandText = "PRAGMA foreign_keys = ON";
                await cmd.ExecuteNonQueryAsync(ct);
            }

            if (_config.EnableWalMode && !_isInMemory)
            {
                await using var cmd = _connection.CreateCommand();
                cmd.CommandText = "PRAGMA journal_mode = WAL";
                await cmd.ExecuteNonQueryAsync(ct);
            }

            if (_config.CacheSize > 0)
            {
                await using var cmd = _connection.CreateCommand();
                cmd.CommandText = $"PRAGMA cache_size = {_config.CacheSize}";
                await cmd.ExecuteNonQueryAsync(ct);
            }

            // Retrieve database info
            var serverInfo = new Dictionary<string, object>
            {
                ["DatabaseType"] = "SQLite",
                ["DataSource"] = _connection.DataSource ?? "unknown",
                ["IsInMemory"] = _isInMemory,
                ["ConnectionState"] = _connection.State.ToString()
            };

            // Get SQLite version
            await using var versionCmd = _connection.CreateCommand();
            versionCmd.CommandText = "SELECT sqlite_version()";
            var version = await versionCmd.ExecuteScalarAsync(ct);
            if (version != null)
            {
                serverInfo["SQLiteVersion"] = version.ToString() ?? "unknown";
            }

            return new ConnectionResult(true, null, serverInfo);
        }
        catch (SqliteException ex)
        {
            return new ConnectionResult(false, $"SQLite connection failed: {ex.Message}", null);
        }
        catch (Exception ex)
        {
            return new ConnectionResult(false, $"Connection failed: {ex.Message}", null);
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <inheritdoc />
    protected override async Task CloseConnectionAsync()
    {
        await _connectionLock.WaitAsync();
        try
        {
            if (_connection != null)
            {
                await _connection.CloseAsync();
                await _connection.DisposeAsync();
                _connection = null;
            }
            _connectionString = null;
            _schemaCache.Clear();
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <inheritdoc />
    protected override async Task<bool> PingAsync()
    {
        if (_connection == null || _connection.State != AdoConnectionState.Open)
            return false;

        try
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT 1";
            cmd.CommandTimeout = 5;
            await cmd.ExecuteScalarAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
    protected override async Task<DataSchema> FetchSchemaAsync()
    {
        if (_connection == null || _connection.State != AdoConnectionState.Open)
            throw new InvalidOperationException("Not connected to database");

        await using var cmd = _connection.CreateCommand();

        // Query sqlite_master for all tables
        cmd.CommandText = @"
            SELECT name FROM sqlite_master
            WHERE type='table' AND name NOT LIKE 'sqlite_%'
            ORDER BY name";

        var fields = new List<DataSchemaField>();
        var primaryKeys = new List<string>();
        var tableCount = 0;

        await using var reader = await cmd.ExecuteReaderAsync();
        var tables = new List<string>();
        while (await reader.ReadAsync())
        {
            tables.Add(reader.GetString(0));
        }
        reader.Close();

        // Get column information for each table
        foreach (var tableName in tables)
        {
            tableCount++;
            await using var tableCmd = _connection.CreateCommand();
            tableCmd.CommandText = $"PRAGMA table_info({QuoteIdentifier(tableName)})";

            await using var tableReader = await tableCmd.ExecuteReaderAsync();
            while (await tableReader.ReadAsync())
            {
                var columnName = tableReader.GetString(1);
                var dataType = tableReader.GetString(2);
                var notNull = tableReader.GetInt32(3) == 1;
                var defaultValue = tableReader.IsDBNull(4) ? null : tableReader.GetString(4);
                var isPrimaryKey = tableReader.GetInt32(5) == 1;

                fields.Add(new DataSchemaField(
                    columnName,
                    MapSqliteType(dataType),
                    !notNull,
                    null, // MaxLength
                    new Dictionary<string, object>
                    {
                        ["tableName"] = tableName,
                        ["sqliteType"] = dataType,
                        ["isPrimaryKey"] = isPrimaryKey,
                        ["defaultValue"] = defaultValue ?? (object)DBNull.Value
                    }
                ));

                if (isPrimaryKey)
                {
                    primaryKeys.Add(columnName);
                }
            }
        }

        return new DataSchema(
            Name: _connection.DataSource ?? "main",
            Fields: fields.ToArray(),
            PrimaryKeys: primaryKeys.Distinct().ToArray(),
            Metadata: new Dictionary<string, object>
            {
                ["TableCount"] = tableCount,
                ["FieldCount"] = fields.Count,
                ["SchemaVersion"] = "1.0",
                ["IsInMemory"] = _isInMemory
            }
        );
    }

    /// <summary>
    /// Gets the schema for a specific table.
    /// </summary>
    /// <param name="tableName">Name of the table.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Table schema.</returns>
    public async Task<DataSchema> GetTableSchemaAsync(string tableName, CancellationToken ct = default)
    {
        if (_schemaCache.TryGetValue(tableName, out var cached))
            return cached;

        if (_connection == null || _connection.State != AdoConnectionState.Open)
            throw new InvalidOperationException("Not connected to database");

        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({QuoteIdentifier(tableName)})";

        var fields = new List<DataSchemaField>();
        var primaryKeys = new List<string>();

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var columnName = reader.GetString(1);
            var dataType = reader.GetString(2);
            var notNull = reader.GetInt32(3) == 1;
            var defaultValue = reader.IsDBNull(4) ? null : reader.GetString(4);
            var isPrimaryKey = reader.GetInt32(5) == 1;

            fields.Add(new DataSchemaField(
                columnName,
                MapSqliteType(dataType),
                !notNull,
                null, // MaxLength
                new Dictionary<string, object>
                {
                    ["sqliteType"] = dataType,
                    ["isPrimaryKey"] = isPrimaryKey,
                    ["defaultValue"] = defaultValue ?? (object)DBNull.Value
                }
            ));

            if (isPrimaryKey)
                primaryKeys.Add(columnName);
        }

        var schema = new DataSchema(
            Name: tableName,
            Fields: fields.ToArray(),
            PrimaryKeys: primaryKeys.ToArray(),
            Metadata: new Dictionary<string, object>
            {
                ["TableName"] = tableName
            }
        );

        _schemaCache[tableName] = schema;
        return schema;
    }

    /// <inheritdoc />
    protected override async IAsyncEnumerable<DataRecord> ExecuteReadAsync(
        DataQuery query,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (_connection == null || _connection.State != AdoConnectionState.Open)
            throw new InvalidOperationException("Not connected to database");

        var sql = BuildSelectQuery(query);

        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;

        if (_config.CommandTimeout > 0)
            cmd.CommandTimeout = _config.CommandTimeout;

        long position = query.Offset ?? 0;

        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            if (ct.IsCancellationRequested) yield break;

            var values = new Dictionary<string, object?>();
            for (int i = 0; i < reader.FieldCount; i++)
            {
                var columnName = reader.GetName(i);
                values[columnName] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            yield return new DataRecord(
                Values: values,
                Position: position++,
                Timestamp: DateTimeOffset.UtcNow
            );
        }
    }

    /// <inheritdoc />
    protected override async Task<WriteResult> ExecuteWriteAsync(
        IAsyncEnumerable<DataRecord> records,
        WriteOptions options,
        CancellationToken ct)
    {
        if (_connection == null || _connection.State != AdoConnectionState.Open)
            throw new InvalidOperationException("Not connected to database");

        long written = 0;
        long failed = 0;
        var errors = new List<string>();

        var tableName = options.TargetTable ?? throw new ArgumentException("TargetTable is required");

        // Get schema for the target table
        var schema = await GetTableSchemaAsync(tableName, ct);
        var columns = schema.Fields.Select(f => f.Name).ToArray();

        // Use transaction for batch writes
        var transaction = (SqliteTransaction)await _connection.BeginTransactionAsync(ct);

        try
        {
            var batch = new List<DataRecord>();

            await foreach (var record in records.WithCancellation(ct))
            {
                batch.Add(record);

                if (batch.Count >= options.BatchSize)
                {
                    var (w, f, e) = await WriteBatchAsync(_connection, transaction, tableName, columns, batch, options.Mode, ct);
                    written += w;
                    failed += f;
                    errors.AddRange(e);
                    batch.Clear();
                }
            }

            // Write remaining records
            if (batch.Count > 0)
            {
                var (w, f, e) = await WriteBatchAsync(_connection, transaction, tableName, columns, batch, options.Mode, ct);
                written += w;
                failed += f;
                errors.AddRange(e);
            }

            await transaction.CommitAsync(ct);
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(ct);
            errors.Add($"Transaction failed: {ex.Message}");
            failed += written;
            written = 0;
        }
        finally
        {
            await transaction.DisposeAsync();
        }

        return new WriteResult(written, failed, errors.Count > 0 ? errors.ToArray() : null);
    }

    private async Task<(long written, long failed, List<string> errors)> WriteBatchAsync(
        SqliteConnection conn,
        SqliteTransaction transaction,
        string tableName,
        string[] columns,
        List<DataRecord> batch,
        SDK.Connectors.WriteMode mode,
        CancellationToken ct)
    {
        long written = 0;
        long failed = 0;
        var errors = new List<string>();

        foreach (var record in batch)
        {
            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = transaction;

                var recordColumns = record.Values.Keys.Where(k => columns.Contains(k)).ToArray();

                if (mode == SDK.Connectors.WriteMode.Upsert)
                {
                    cmd.CommandText = BuildUpsertStatement(tableName, recordColumns);
                }
                else
                {
                    cmd.CommandText = BuildInsertStatement(tableName, recordColumns);
                }

                for (int i = 0; i < recordColumns.Length; i++)
                {
                    var value = record.Values[recordColumns[i]];
                    cmd.Parameters.AddWithValue($"@p{i}", value ?? DBNull.Value);
                }

                await cmd.ExecuteNonQueryAsync(ct);
                written++;
            }
            catch (Exception ex)
            {
                failed++;
                errors.Add($"Record at position {record.Position}: {ex.Message}");
            }
        }

        return (written, failed, errors);
    }

    /// <inheritdoc />
    protected override string BuildSelectQuery(DataQuery query)
    {
        var sb = new StringBuilder();
        sb.Append("SELECT ");

        if (query.Fields?.Length > 0)
        {
            sb.Append(string.Join(", ", query.Fields.Select(QuoteIdentifier)));
        }
        else
        {
            sb.Append('*');
        }

        sb.Append(" FROM ");
        sb.Append(QuoteIdentifier(query.TableOrCollection ?? "data"));

        if (!string.IsNullOrWhiteSpace(query.Filter))
        {
            sb.Append(" WHERE ");
            sb.Append(query.Filter);
        }

        if (!string.IsNullOrEmpty(query.OrderBy))
        {
            sb.Append(" ORDER BY ");
            sb.Append(query.OrderBy);
        }

        if (query.Limit.HasValue)
        {
            sb.Append(" LIMIT ");
            sb.Append(query.Limit);
        }

        if (query.Offset.HasValue)
        {
            sb.Append(" OFFSET ");
            sb.Append(query.Offset);
        }

        return sb.ToString();
    }

    /// <inheritdoc />
    protected override string BuildInsertStatement(string table, string[] columns)
    {
        var columnList = string.Join(", ", columns.Select(QuoteIdentifier));
        var paramList = string.Join(", ", columns.Select((_, i) => $"@p{i}"));

        return $"INSERT INTO {QuoteIdentifier(table)} ({columnList}) VALUES ({paramList})";
    }

    private string BuildUpsertStatement(string table, string[] columns)
    {
        var columnList = string.Join(", ", columns.Select(QuoteIdentifier));
        var paramList = string.Join(", ", columns.Select((_, i) => $"@p{i}"));
        var updateList = string.Join(", ", columns.Select((c, i) => $"{QuoteIdentifier(c)} = @p{i}"));

        return $@"INSERT INTO {QuoteIdentifier(table)} ({columnList}) VALUES ({paramList})
                  ON CONFLICT DO UPDATE SET {updateList}";
    }

    /// <summary>
    /// Executes a raw SQL query.
    /// </summary>
    /// <param name="sql">SQL query to execute.</param>
    /// <param name="parameters">Query parameters.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Query results.</returns>
    public async IAsyncEnumerable<DataRecord> ExecuteRawQueryAsync(
        string sql,
        Dictionary<string, object>? parameters = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_connection == null || _connection.State != AdoConnectionState.Open)
            throw new InvalidOperationException("Not connected to database");

        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;

        if (parameters != null)
        {
            foreach (var (key, value) in parameters)
            {
                cmd.Parameters.AddWithValue(key, value ?? DBNull.Value);
            }
        }

        long position = 0;
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            var values = new Dictionary<string, object?>();
            for (int i = 0; i < reader.FieldCount; i++)
            {
                values[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            yield return new DataRecord(values, position++, DateTimeOffset.UtcNow);
        }
    }

    /// <summary>
    /// Executes a non-query SQL command.
    /// </summary>
    /// <param name="sql">SQL command to execute.</param>
    /// <param name="parameters">Command parameters.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of rows affected.</returns>
    public async Task<int> ExecuteNonQueryAsync(
        string sql,
        Dictionary<string, object>? parameters = null,
        CancellationToken ct = default)
    {
        if (_connection == null || _connection.State != AdoConnectionState.Open)
            throw new InvalidOperationException("Not connected to database");

        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;

        if (parameters != null)
        {
            foreach (var (key, value) in parameters)
            {
                cmd.Parameters.AddWithValue(key, value ?? DBNull.Value);
            }
        }

        return await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Executes VACUUM command to optimize the database.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Task representing the async operation.</returns>
    public async Task VacuumAsync(CancellationToken ct = default)
    {
        if (_connection == null || _connection.State != AdoConnectionState.Open)
            throw new InvalidOperationException("Not connected to database");

        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = "VACUUM";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string QuoteIdentifier(string identifier)
    {
        // SQLite uses double quotes for identifiers
        return $"\"{identifier.Replace("\"", "\"\"")}\"";
    }

    private static string MapSqliteType(string sqliteType)
    {
        // SQLite has dynamic typing with type affinity
        var lowerType = sqliteType.ToLowerInvariant();

        // Check for type affinity rules
        if (lowerType.Contains("int"))
            return "long";
        if (lowerType.Contains("char") || lowerType.Contains("clob") || lowerType.Contains("text"))
            return "string";
        if (lowerType.Contains("blob") || string.IsNullOrEmpty(lowerType))
            return "bytes";
        if (lowerType.Contains("real") || lowerType.Contains("floa") || lowerType.Contains("doub"))
            return "double";
        if (lowerType.Contains("decimal") || lowerType.Contains("numeric"))
            return "decimal";
        if (lowerType.Contains("bool"))
            return "bool";
        if (lowerType.Contains("date") || lowerType.Contains("time"))
            return "datetime";

        // Default to string for unknown types
        return "string";
    }

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc />
    public override Task StopAsync() => DisconnectAsync();
}

/// <summary>
/// Configuration options for the SQLite connector.
/// </summary>
public class SqliteConnectorConfig
{
    /// <summary>
    /// Command timeout in seconds.
    /// </summary>
    public int CommandTimeout { get; set; } = 30;

    /// <summary>
    /// Whether to enable Write-Ahead Logging (WAL) mode for better concurrency.
    /// Not applicable to in-memory databases.
    /// </summary>
    public bool EnableWalMode { get; set; } = true;

    /// <summary>
    /// Cache size in pages (negative values are in KB).
    /// Default is 0 (use SQLite default).
    /// </summary>
    public int CacheSize { get; set; } = -2000; // 2MB cache

    /// <summary>
    /// Whether to enable foreign key constraints.
    /// </summary>
    public bool EnableForeignKeys { get; set; } = true;
}
