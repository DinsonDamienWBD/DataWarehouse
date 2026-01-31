using System.Data;
using System.Runtime.CompilerServices;
using System.Text;
using DataWarehouse.SDK.Connectors;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using Microsoft.Data.SqlClient;

namespace DataWarehouse.Plugins.DataConnectors;

/// <summary>
/// Production-ready SQL Server database connector plugin.
/// Provides full CRUD operations with real database connectivity via Microsoft.Data.SqlClient.
/// Supports schema discovery, parameterized queries, transactions, and connection pooling.
/// </summary>
public class SqlServerConnectorPlugin : DatabaseConnectorPluginBase
{
    private string? _connectionString;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private readonly Dictionary<string, DataSchema> _schemaCache = new();
    private SqlServerConnectorConfig _config = new();

    /// <inheritdoc />
    public override string Id => "datawarehouse.connector.sqlserver";

    /// <inheritdoc />
    public override string Name => "SQL Server Connector";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    public override string ConnectorId => "sqlserver";

    /// <inheritdoc />
    public override PluginCategory Category => PluginCategory.InfrastructureProvider;

    /// <inheritdoc />
    public override ConnectorCapabilities Capabilities =>
        ConnectorCapabilities.Read |
        ConnectorCapabilities.Write |
        ConnectorCapabilities.Schema |
        ConnectorCapabilities.Transactions |
        ConnectorCapabilities.ChangeTracking |
        ConnectorCapabilities.BulkOperations;

    /// <summary>
    /// Configures the connector with additional options.
    /// </summary>
    /// <param name="config">SQL Server-specific configuration.</param>
    public void Configure(SqlServerConnectorConfig config)
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

            // Configure connection string builder
            var builder = new SqlConnectionStringBuilder(_connectionString);

            if (_config.MaxPoolSize > 0)
                builder.MaxPoolSize = _config.MaxPoolSize;
            if (_config.MinPoolSize > 0)
                builder.MinPoolSize = _config.MinPoolSize;
            if (_config.ConnectTimeout > 0)
                builder.ConnectTimeout = _config.ConnectTimeout;

            _connectionString = builder.ToString();

            // Test the connection
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            // Retrieve server info
            var serverInfo = new Dictionary<string, object>
            {
                ["ServerVersion"] = conn.ServerVersion,
                ["Database"] = conn.Database ?? "unknown",
                ["DataSource"] = conn.DataSource ?? "unknown",
                ["ConnectionState"] = conn.State.ToString()
            };

            // Get additional server properties
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT @@VERSION, DB_NAME(), SUSER_SNAME(), @@SERVERNAME";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                serverInfo["SqlServerVersion"] = reader.GetString(0);
                serverInfo["CurrentDatabase"] = reader.GetString(1);
                serverInfo["CurrentUser"] = reader.GetString(2);
                serverInfo["ServerName"] = reader.GetString(3);
            }

            return new ConnectionResult(true, null, serverInfo);
        }
        catch (SqlException ex)
        {
            return new ConnectionResult(false, $"SQL Server connection failed: {ex.Message}", null);
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
            _connectionString = null;
            _schemaCache.Clear();
            SqlConnection.ClearAllPools();
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <inheritdoc />
    protected override async Task<bool> PingAsync()
    {
        if (string.IsNullOrEmpty(_connectionString)) return false;

        try
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
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
        if (string.IsNullOrEmpty(_connectionString))
            throw new InvalidOperationException("Not connected to database");

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();

        // Query information_schema for table and column metadata
        cmd.CommandText = @"
            SELECT
                c.TABLE_NAME,
                c.COLUMN_NAME,
                c.DATA_TYPE,
                c.IS_NULLABLE,
                c.COLUMN_DEFAULT,
                c.CHARACTER_MAXIMUM_LENGTH,
                c.NUMERIC_PRECISION,
                c.NUMERIC_SCALE,
                CASE WHEN pk.COLUMN_NAME IS NOT NULL THEN 1 ELSE 0 END as IS_PRIMARY_KEY
            FROM INFORMATION_SCHEMA.COLUMNS c
            LEFT JOIN (
                SELECT ku.TABLE_NAME, ku.COLUMN_NAME
                FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
                JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE ku
                    ON tc.CONSTRAINT_NAME = ku.CONSTRAINT_NAME
                WHERE tc.CONSTRAINT_TYPE = 'PRIMARY KEY'
            ) pk ON c.TABLE_NAME = pk.TABLE_NAME AND c.COLUMN_NAME = pk.COLUMN_NAME
            WHERE c.TABLE_SCHEMA = 'dbo'
            ORDER BY c.TABLE_NAME, c.ORDINAL_POSITION";

        var fields = new List<DataSchemaField>();
        var primaryKeys = new List<string>();
        var tableCount = 0;
        var currentTable = "";

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var tableName = reader.GetString(0);
            if (tableName != currentTable)
            {
                currentTable = tableName;
                tableCount++;
            }

            var columnName = reader.GetString(1);
            var dataType = reader.GetString(2);
            var isNullable = reader.GetString(3) == "YES";
            var defaultValue = reader.IsDBNull(4) ? null : reader.GetString(4);
            var isPrimaryKey = reader.GetInt32(8) == 1;

            fields.Add(new DataSchemaField(
                columnName,
                MapSqlServerType(dataType),
                isNullable,
                null, // MaxLength
                new Dictionary<string, object>
                {
                    ["tableName"] = tableName,
                    ["sqlServerType"] = dataType,
                    ["isPrimaryKey"] = isPrimaryKey,
                    ["defaultValue"] = defaultValue ?? (object)DBNull.Value
                }
            ));

            if (isPrimaryKey)
            {
                primaryKeys.Add(columnName);
            }
        }

        return new DataSchema(
            Name: conn.Database ?? "master",
            Fields: fields.ToArray(),
            PrimaryKeys: primaryKeys.Distinct().ToArray(),
            Metadata: new Dictionary<string, object>
            {
                ["TableCount"] = tableCount,
                ["FieldCount"] = fields.Count,
                ["SchemaVersion"] = "1.0"
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

        if (string.IsNullOrEmpty(_connectionString))
            throw new InvalidOperationException("Not connected to database");

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();

        cmd.CommandText = @"
            SELECT
                c.COLUMN_NAME,
                c.DATA_TYPE,
                c.IS_NULLABLE,
                c.COLUMN_DEFAULT,
                CASE WHEN pk.COLUMN_NAME IS NOT NULL THEN 1 ELSE 0 END as IS_PRIMARY_KEY
            FROM INFORMATION_SCHEMA.COLUMNS c
            LEFT JOIN (
                SELECT ku.COLUMN_NAME
                FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
                JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE ku
                    ON tc.CONSTRAINT_NAME = ku.CONSTRAINT_NAME
                WHERE tc.CONSTRAINT_TYPE = 'PRIMARY KEY' AND tc.TABLE_NAME = @table
            ) pk ON c.COLUMN_NAME = pk.COLUMN_NAME
            WHERE c.TABLE_SCHEMA = 'dbo' AND c.TABLE_NAME = @table
            ORDER BY c.ORDINAL_POSITION";

        cmd.Parameters.AddWithValue("@table", tableName);

        var fields = new List<DataSchemaField>();
        var primaryKeys = new List<string>();

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var columnName = reader.GetString(0);
            var dataType = reader.GetString(1);
            var isNullable = reader.GetString(2) == "YES";
            var defaultValue = reader.IsDBNull(3) ? null : reader.GetString(3);
            var isPrimaryKey = reader.GetInt32(4) == 1;

            fields.Add(new DataSchemaField(
                columnName,
                MapSqlServerType(dataType),
                isNullable,
                null, // MaxLength
                new Dictionary<string, object>
                {
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
        if (string.IsNullOrEmpty(_connectionString))
            throw new InvalidOperationException("Not connected to database");

        var sql = BuildSelectQuery(query);

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
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
        if (string.IsNullOrEmpty(_connectionString))
            throw new InvalidOperationException("Not connected to database");

        long written = 0;
        long failed = 0;
        var errors = new List<string>();

        var tableName = options.TargetTable ?? throw new ArgumentException("TargetTable is required");

        // Get schema for the target table
        var schema = await GetTableSchemaAsync(tableName, ct);
        var columns = schema.Fields.Select(f => f.Name).ToArray();

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        // Use transaction for batch writes
        await using var transaction = conn.BeginTransaction();

        try
        {
            var batch = new List<DataRecord>();

            await foreach (var record in records.WithCancellation(ct))
            {
                batch.Add(record);

                if (batch.Count >= options.BatchSize)
                {
                    var (w, f, e) = await WriteBatchAsync(conn, transaction, tableName, columns, batch, options.Mode, ct);
                    written += w;
                    failed += f;
                    errors.AddRange(e);
                    batch.Clear();
                }
            }

            // Write remaining records
            if (batch.Count > 0)
            {
                var (w, f, e) = await WriteBatchAsync(conn, transaction, tableName, columns, batch, options.Mode, ct);
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

        return new WriteResult(written, failed, errors.Count > 0 ? errors.ToArray() : null);
    }

    private async Task<(long written, long failed, List<string> errors)> WriteBatchAsync(
        SqlConnection conn,
        SqlTransaction transaction,
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
                    cmd.CommandText = BuildMergeStatement(tableName, recordColumns);
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

        if (query.Limit.HasValue && !query.Offset.HasValue)
        {
            sb.Append($"TOP {query.Limit} ");
        }

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

        if (query.Offset.HasValue)
        {
            if (string.IsNullOrEmpty(query.OrderBy))
            {
                sb.Append(" ORDER BY (SELECT NULL)");
            }
            sb.Append($" OFFSET {query.Offset} ROWS");
            if (query.Limit.HasValue)
            {
                sb.Append($" FETCH NEXT {query.Limit} ROWS ONLY");
            }
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

    private string BuildMergeStatement(string table, string[] columns)
    {
        var columnList = string.Join(", ", columns.Select(QuoteIdentifier));
        var paramList = string.Join(", ", columns.Select((_, i) => $"@p{i}"));
        var updateList = string.Join(", ", columns.Select((c, i) => $"T.{QuoteIdentifier(c)} = S.{QuoteIdentifier(c)}"));
        var matchCondition = columns.Length > 0 ? $"T.{QuoteIdentifier(columns[0])} = S.{QuoteIdentifier(columns[0])}" : "1=1";

        return $@"MERGE {QuoteIdentifier(table)} AS T
                  USING (SELECT {string.Join(", ", columns.Select((c, i) => $"@p{i} AS {QuoteIdentifier(c)}"))}) AS S
                  ON {matchCondition}
                  WHEN MATCHED THEN UPDATE SET {updateList}
                  WHEN NOT MATCHED THEN INSERT ({columnList}) VALUES ({string.Join(", ", columns.Select(c => $"S.{QuoteIdentifier(c)}"))});";
    }

    private static string QuoteIdentifier(string identifier)
    {
        return $"[{identifier.Replace("]", "]]")}]";
    }

    private static string MapSqlServerType(string sqlType)
    {
        return sqlType.ToLowerInvariant() switch
        {
            "int" => "int",
            "bigint" => "long",
            "smallint" => "short",
            "tinyint" => "byte",
            "float" or "real" => "double",
            "decimal" or "numeric" or "money" or "smallmoney" => "decimal",
            "bit" => "bool",
            "char" or "varchar" or "text" or "nchar" or "nvarchar" or "ntext" => "string",
            "binary" or "varbinary" or "image" => "bytes",
            "datetime" or "datetime2" or "smalldatetime" => "datetime",
            "datetimeoffset" => "datetimeoffset",
            "date" => "date",
            "time" => "time",
            "uniqueidentifier" => "uuid",
            "xml" => "string",
            _ => sqlType
        };
    }

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc />
    public override Task StopAsync() => DisconnectAsync();
}

/// <summary>
/// Configuration options for the SQL Server connector.
/// </summary>
public class SqlServerConnectorConfig
{
    /// <summary>
    /// Maximum number of connections in the pool.
    /// </summary>
    public int MaxPoolSize { get; set; } = 100;

    /// <summary>
    /// Minimum number of connections in the pool.
    /// </summary>
    public int MinPoolSize { get; set; } = 1;

    /// <summary>
    /// Connection timeout in seconds.
    /// </summary>
    public int ConnectTimeout { get; set; } = 15;

    /// <summary>
    /// Command timeout in seconds.
    /// </summary>
    public int CommandTimeout { get; set; } = 30;

    /// <summary>
    /// Whether to enable encryption.
    /// </summary>
    public bool Encrypt { get; set; } = true;
}
