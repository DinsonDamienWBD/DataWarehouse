using System.Data;
using System.Runtime.CompilerServices;
using System.Text;
using DataWarehouse.SDK.Connectors;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using Snowflake.Data.Client;

namespace DataWarehouse.Plugins.DataConnectors;

/// <summary>
/// Production-ready Snowflake database connector plugin.
/// Provides full CRUD operations with real Snowflake connectivity via Snowflake.Data.
/// Supports schema discovery, parameterized queries, transactions, role-based access, and bulk loading via COPY INTO.
/// </summary>
public class SnowflakeConnectorPlugin : DatabaseConnectorPluginBase
{
    private string? _connectionString;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private readonly Dictionary<string, DataSchema> _schemaCache = new();
    private SnowflakeConnectorConfig _config = new();
    private string? _currentWarehouse;
    private string? _currentDatabase;
    private string? _currentSchema;
    private string? _currentRole;

    /// <inheritdoc />
    public override string Id => "datawarehouse.connector.snowflake";

    /// <inheritdoc />
    public override string Name => "Snowflake Connector";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    public override string ConnectorId => "snowflake";

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
    /// Configures the connector with Snowflake-specific options.
    /// </summary>
    /// <param name="config">Snowflake-specific configuration.</param>
    public void Configure(SnowflakeConnectorConfig config)
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
            var builder = new SnowflakeDbConnectionStringBuilder
            {
                ConnectionString = _connectionString
            };

            // Apply configuration overrides
            if (!string.IsNullOrEmpty(_config.Account))
                builder.Account = _config.Account;
            if (!string.IsNullOrEmpty(_config.User))
                builder.User = _config.User;
            if (!string.IsNullOrEmpty(_config.Password))
                builder.Password = _config.Password;
            if (!string.IsNullOrEmpty(_config.Warehouse))
            {
                builder.Warehouse = _config.Warehouse;
                _currentWarehouse = _config.Warehouse;
            }
            if (!string.IsNullOrEmpty(_config.Database))
            {
                builder.Database = _config.Database;
                _currentDatabase = _config.Database;
            }
            if (!string.IsNullOrEmpty(_config.Schema))
            {
                builder.Schema = _config.Schema;
                _currentSchema = _config.Schema;
            }
            if (!string.IsNullOrEmpty(_config.Role))
            {
                builder.Role = _config.Role;
                _currentRole = _config.Role;
            }
            if (_config.ConnectionTimeout > 0)
                builder.ConnectionTimeout = _config.ConnectionTimeout;

            _connectionString = builder.ToString();

            // Test the connection
            await using var conn = new SnowflakeDbConnection { ConnectionString = _connectionString };
            await conn.OpenAsync(ct);

            // Set session parameters if specified
            if (_config.SessionParameters?.Count > 0)
            {
                foreach (var (key, value) in _config.SessionParameters)
                {
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = $"ALTER SESSION SET {key} = '{value}'";
                    await cmd.ExecuteNonQueryAsync(ct);
                }
            }

            // Retrieve server info
            var serverInfo = new Dictionary<string, object>
            {
                ["ConnectionState"] = conn.State.ToString(),
                ["Warehouse"] = _currentWarehouse ?? "unknown",
                ["Database"] = _currentDatabase ?? "unknown",
                ["Schema"] = _currentSchema ?? "unknown",
                ["Role"] = _currentRole ?? "unknown"
            };

            // Get Snowflake version and session info
            await using var infoCmd = conn.CreateCommand();
            infoCmd.CommandText = @"
                SELECT
                    CURRENT_VERSION() as VERSION,
                    CURRENT_WAREHOUSE() as WAREHOUSE,
                    CURRENT_DATABASE() as DATABASE,
                    CURRENT_SCHEMA() as SCHEMA,
                    CURRENT_ROLE() as ROLE,
                    CURRENT_USER() as USER,
                    CURRENT_ACCOUNT() as ACCOUNT,
                    CURRENT_REGION() as REGION";

            await using var reader = await infoCmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                serverInfo["SnowflakeVersion"] = reader.IsDBNull(0) ? "unknown" : reader.GetString(0);
                serverInfo["CurrentWarehouse"] = reader.IsDBNull(1) ? "unknown" : reader.GetString(1);
                serverInfo["CurrentDatabase"] = reader.IsDBNull(2) ? "unknown" : reader.GetString(2);
                serverInfo["CurrentSchema"] = reader.IsDBNull(3) ? "unknown" : reader.GetString(3);
                serverInfo["CurrentRole"] = reader.IsDBNull(4) ? "unknown" : reader.GetString(4);
                serverInfo["CurrentUser"] = reader.IsDBNull(5) ? "unknown" : reader.GetString(5);
                serverInfo["CurrentAccount"] = reader.IsDBNull(6) ? "unknown" : reader.GetString(6);
                serverInfo["CurrentRegion"] = reader.IsDBNull(7) ? "unknown" : reader.GetString(7);

                // Update current context
                _currentWarehouse = reader.IsDBNull(1) ? _currentWarehouse : reader.GetString(1);
                _currentDatabase = reader.IsDBNull(2) ? _currentDatabase : reader.GetString(2);
                _currentSchema = reader.IsDBNull(3) ? _currentSchema : reader.GetString(3);
                _currentRole = reader.IsDBNull(4) ? _currentRole : reader.GetString(4);
            }

            return new ConnectionResult(true, null, serverInfo);
        }
        catch (SnowflakeDbException ex)
        {
            return new ConnectionResult(false, $"Snowflake connection failed: {ex.Message} (Error Code: {ex.ErrorCode})", null);
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
            _currentWarehouse = null;
            _currentDatabase = null;
            _currentSchema = null;
            _currentRole = null;
            SnowflakeDbConnection.ClearAllPools();
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
            await using var conn = new SnowflakeDbConnection { ConnectionString = _connectionString };
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

        await using var conn = new SnowflakeDbConnection { ConnectionString = _connectionString };
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();

        var database = _currentDatabase ?? "unknown";
        var schema = _currentSchema ?? "PUBLIC";

        // Query INFORMATION_SCHEMA for table and column metadata
        cmd.CommandText = $@"
            SELECT
                c.TABLE_NAME,
                c.COLUMN_NAME,
                c.DATA_TYPE,
                c.IS_NULLABLE,
                c.COLUMN_DEFAULT,
                c.CHARACTER_MAXIMUM_LENGTH,
                c.NUMERIC_PRECISION,
                c.NUMERIC_SCALE,
                c.ORDINAL_POSITION,
                CASE
                    WHEN pk.COLUMN_NAME IS NOT NULL THEN TRUE
                    ELSE FALSE
                END as IS_PRIMARY_KEY
            FROM {QuoteIdentifier(database)}.INFORMATION_SCHEMA.COLUMNS c
            LEFT JOIN (
                SELECT ku.TABLE_NAME, ku.COLUMN_NAME
                FROM {QuoteIdentifier(database)}.INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
                JOIN {QuoteIdentifier(database)}.INFORMATION_SCHEMA.KEY_COLUMN_USAGE ku
                    ON tc.CONSTRAINT_NAME = ku.CONSTRAINT_NAME
                    AND tc.TABLE_SCHEMA = ku.TABLE_SCHEMA
                WHERE tc.CONSTRAINT_TYPE = 'PRIMARY KEY'
                  AND tc.TABLE_SCHEMA = '{schema}'
            ) pk ON c.TABLE_NAME = pk.TABLE_NAME
                AND c.COLUMN_NAME = pk.COLUMN_NAME
            WHERE c.TABLE_SCHEMA = '{schema}'
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
            var isPrimaryKey = reader.GetBoolean(9);

            fields.Add(new DataSchemaField(
                columnName,
                MapSnowflakeType(dataType),
                isNullable,
                null, // MaxLength
                new Dictionary<string, object>
                {
                    ["tableName"] = tableName,
                    ["snowflakeType"] = dataType,
                    ["isPrimaryKey"] = isPrimaryKey,
                    ["defaultValue"] = defaultValue ?? (object)DBNull.Value,
                    ["ordinalPosition"] = reader.GetInt32(8)
                }
            ));

            if (isPrimaryKey)
            {
                primaryKeys.Add(columnName);
            }
        }

        return new DataSchema(
            Name: $"{database}.{schema}",
            Fields: fields.ToArray(),
            PrimaryKeys: primaryKeys.Distinct().ToArray(),
            Metadata: new Dictionary<string, object>
            {
                ["Database"] = database,
                ["Schema"] = schema,
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

        await using var conn = new SnowflakeDbConnection { ConnectionString = _connectionString };
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();

        var database = _currentDatabase ?? "unknown";
        var schema = _currentSchema ?? "PUBLIC";

        cmd.CommandText = $@"
            SELECT
                c.COLUMN_NAME,
                c.DATA_TYPE,
                c.IS_NULLABLE,
                c.COLUMN_DEFAULT,
                c.ORDINAL_POSITION,
                CASE
                    WHEN pk.COLUMN_NAME IS NOT NULL THEN TRUE
                    ELSE FALSE
                END as IS_PRIMARY_KEY
            FROM {QuoteIdentifier(database)}.INFORMATION_SCHEMA.COLUMNS c
            LEFT JOIN (
                SELECT ku.COLUMN_NAME
                FROM {QuoteIdentifier(database)}.INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
                JOIN {QuoteIdentifier(database)}.INFORMATION_SCHEMA.KEY_COLUMN_USAGE ku
                    ON tc.CONSTRAINT_NAME = ku.CONSTRAINT_NAME
                WHERE tc.CONSTRAINT_TYPE = 'PRIMARY KEY'
                  AND tc.TABLE_NAME = :table
                  AND tc.TABLE_SCHEMA = '{schema}'
            ) pk ON c.COLUMN_NAME = pk.COLUMN_NAME
            WHERE c.TABLE_SCHEMA = '{schema}'
              AND c.TABLE_NAME = :table
            ORDER BY c.ORDINAL_POSITION";

        cmd.Parameters.Add(new SnowflakeDbParameter("table", tableName));

        var fields = new List<DataSchemaField>();
        var primaryKeys = new List<string>();

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var columnName = reader.GetString(0);
            var dataType = reader.GetString(1);
            var isNullable = reader.GetString(2) == "YES";
            var defaultValue = reader.IsDBNull(3) ? null : reader.GetString(3);
            var isPrimaryKey = reader.GetBoolean(5);

            fields.Add(new DataSchemaField(
                columnName,
                MapSnowflakeType(dataType),
                isNullable,
                null, // MaxLength
                new Dictionary<string, object>
                {
                    ["snowflakeType"] = dataType,
                    ["defaultValue"] = defaultValue ?? (object)DBNull.Value,
                    ["ordinalPosition"] = reader.GetInt32(4)
                }
            ));

            if (isPrimaryKey)
                primaryKeys.Add(columnName);
        }

        var tableSchema = new DataSchema(
            Name: tableName,
            Fields: fields.ToArray(),
            PrimaryKeys: primaryKeys.ToArray(),
            Metadata: new Dictionary<string, object>
            {
                ["TableName"] = tableName,
                ["Database"] = database,
                ["Schema"] = schema
            }
        );

        _schemaCache[tableName] = tableSchema;
        return tableSchema;
    }

    /// <inheritdoc />
    protected override async IAsyncEnumerable<DataRecord> ExecuteReadAsync(
        DataQuery query,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_connectionString))
            throw new InvalidOperationException("Not connected to database");

        var sql = BuildSelectQuery(query);

        await using var conn = new SnowflakeDbConnection { ConnectionString = _connectionString };
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

        await using var conn = new SnowflakeDbConnection { ConnectionString = _connectionString };
        await conn.OpenAsync(ct);

        // Use transaction for batch writes
        await using var transaction = await conn.BeginTransactionAsync(ct);

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

    /// <summary>
    /// Writes a batch of records to the database.
    /// </summary>
    /// <param name="conn">Open database connection.</param>
    /// <param name="transaction">Active transaction.</param>
    /// <param name="tableName">Target table name.</param>
    /// <param name="columns">Column names in the table.</param>
    /// <param name="batch">Batch of records to write.</param>
    /// <param name="mode">Write mode (Insert or Upsert).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Tuple of (written count, failed count, error messages).</returns>
    private async Task<(long written, long failed, List<string> errors)> WriteBatchAsync(
        SnowflakeDbConnection conn,
        SnowflakeDbTransaction transaction,
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
                    cmd.Parameters.Add(new SnowflakeDbParameter($"p{i}", value ?? DBNull.Value));
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

    /// <summary>
    /// Executes a bulk load operation using Snowflake's COPY INTO command from a stage.
    /// This is the most efficient way to load large datasets into Snowflake.
    /// </summary>
    /// <param name="stageName">Internal or external stage name (e.g., '@my_stage/path/').</param>
    /// <param name="tableName">Target table name.</param>
    /// <param name="fileFormat">File format name or inline format specification.</param>
    /// <param name="options">Additional COPY INTO options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result containing number of rows loaded and any errors.</returns>
    public async Task<WriteResult> BulkLoadFromStageAsync(
        string stageName,
        string tableName,
        string fileFormat,
        CopyIntoOptions? options = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_connectionString))
            throw new InvalidOperationException("Not connected to database");

        await using var conn = new SnowflakeDbConnection { ConnectionString = _connectionString };
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();

        var sql = new StringBuilder();
        sql.Append($"COPY INTO {QuoteIdentifier(tableName)} ");
        sql.Append($"FROM {stageName} ");
        sql.Append($"FILE_FORMAT = ({fileFormat}) ");

        if (options != null)
        {
            if (options.OnError != null)
                sql.Append($"ON_ERROR = {options.OnError} ");
            if (options.SizeLimit.HasValue)
                sql.Append($"SIZE_LIMIT = {options.SizeLimit} ");
            if (options.Purge)
                sql.Append("PURGE = TRUE ");
            if (options.Force)
                sql.Append("FORCE = TRUE ");
            if (!string.IsNullOrEmpty(options.Pattern))
                sql.Append($"PATTERN = '{options.Pattern}' ");
        }

        cmd.CommandText = sql.ToString();

        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            long rowsLoaded = 0;

            // Parse COPY INTO results
            while (await reader.ReadAsync(ct))
            {
                // Snowflake returns columns: file, status, rows_parsed, rows_loaded, error_limit, errors_seen, first_error, first_error_line, first_error_character, first_error_column_name
                if (!reader.IsDBNull(3))
                {
                    rowsLoaded += reader.GetInt64(3);
                }
            }

            return new WriteResult(rowsLoaded, 0, null);
        }
        catch (Exception ex)
        {
            return new WriteResult(0, 0, new[] { $"Bulk load failed: {ex.Message}" });
        }
    }

    /// <summary>
    /// Uploads data to an internal stage for subsequent bulk loading.
    /// </summary>
    /// <param name="stageName">Internal stage name (e.g., '@my_stage' or '@%table_name').</param>
    /// <param name="localFilePath">Path to local file to upload.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if upload succeeded.</returns>
    public async Task<bool> UploadToStageAsync(
        string stageName,
        string localFilePath,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_connectionString))
            throw new InvalidOperationException("Not connected to database");

        await using var conn = new SnowflakeDbConnection { ConnectionString = _connectionString };
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();

        cmd.CommandText = $"PUT 'file://{localFilePath}' {stageName} AUTO_COMPRESS=TRUE";

        try
        {
            await cmd.ExecuteNonQueryAsync(ct);
            return true;
        }
        catch
        {
            return false;
        }
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
            sb.Append($" LIMIT {query.Limit}");
        }

        if (query.Offset.HasValue)
        {
            sb.Append($" OFFSET {query.Offset}");
        }

        return sb.ToString();
    }

    /// <inheritdoc />
    protected override string BuildInsertStatement(string table, string[] columns)
    {
        var columnList = string.Join(", ", columns.Select(QuoteIdentifier));
        var paramList = string.Join(", ", columns.Select((_, i) => $":p{i}"));

        return $"INSERT INTO {QuoteIdentifier(table)} ({columnList}) VALUES ({paramList})";
    }

    /// <summary>
    /// Builds a MERGE statement for upsert operations.
    /// </summary>
    /// <param name="table">Target table name.</param>
    /// <param name="columns">Column names to merge.</param>
    /// <returns>Snowflake MERGE SQL statement.</returns>
    private string BuildMergeStatement(string table, string[] columns)
    {
        if (columns.Length == 0)
            throw new ArgumentException("At least one column is required for merge");

        var columnList = string.Join(", ", columns.Select(QuoteIdentifier));
        var sourceColumns = string.Join(", ", columns.Select((c, i) => $":p{i} AS {QuoteIdentifier(c)}"));
        var matchCondition = $"T.{QuoteIdentifier(columns[0])} = S.{QuoteIdentifier(columns[0])}";
        var updateList = string.Join(", ", columns.Skip(1).Select(c => $"T.{QuoteIdentifier(c)} = S.{QuoteIdentifier(c)}"));
        var insertColumns = string.Join(", ", columns.Select(c => $"S.{QuoteIdentifier(c)}"));

        var sql = new StringBuilder();
        sql.AppendLine($"MERGE INTO {QuoteIdentifier(table)} AS T");
        sql.AppendLine($"USING (SELECT {sourceColumns}) AS S");
        sql.AppendLine($"ON {matchCondition}");

        if (!string.IsNullOrEmpty(updateList))
        {
            sql.AppendLine($"WHEN MATCHED THEN UPDATE SET {updateList}");
        }

        sql.AppendLine($"WHEN NOT MATCHED THEN INSERT ({columnList}) VALUES ({insertColumns})");

        return sql.ToString();
    }

    /// <summary>
    /// Quotes an identifier for safe use in SQL queries.
    /// Snowflake uses double quotes for identifiers.
    /// </summary>
    /// <param name="identifier">Identifier to quote.</param>
    /// <returns>Quoted identifier.</returns>
    private static string QuoteIdentifier(string identifier)
    {
        // Snowflake uses double quotes and they need to be escaped by doubling them
        return $"\"{identifier.Replace("\"", "\"\"")}\"";
    }

    /// <summary>
    /// Maps Snowflake data types to SDK primitive types.
    /// </summary>
    /// <param name="snowflakeType">Snowflake data type name.</param>
    /// <returns>Mapped SDK type.</returns>
    private static string MapSnowflakeType(string snowflakeType)
    {
        var type = snowflakeType.ToUpperInvariant();

        // Handle parameterized types
        if (type.Contains('('))
        {
            type = type[..type.IndexOf('(')];
        }

        return type switch
        {
            "NUMBER" or "NUMERIC" or "INT" or "INTEGER" or "BIGINT" or "SMALLINT" or "TINYINT" or "BYTEINT" => "long",
            "DECIMAL" or "DEC" => "decimal",
            "FLOAT" or "FLOAT4" or "FLOAT8" or "DOUBLE" or "DOUBLE PRECISION" or "REAL" => "double",
            "VARCHAR" or "CHAR" or "CHARACTER" or "STRING" or "TEXT" => "string",
            "BINARY" or "VARBINARY" => "bytes",
            "BOOLEAN" => "bool",
            "DATE" => "date",
            "TIME" => "time",
            "DATETIME" or "TIMESTAMP" or "TIMESTAMP_LTZ" or "TIMESTAMP_NTZ" or "TIMESTAMP_TZ" => "datetime",
            "VARIANT" or "OBJECT" or "ARRAY" => "json",
            "GEOGRAPHY" or "GEOMETRY" => "string",
            _ => snowflakeType
        };
    }

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc />
    public override Task StopAsync() => DisconnectAsync();
}

/// <summary>
/// Configuration options for the Snowflake connector.
/// </summary>
public class SnowflakeConnectorConfig
{
    /// <summary>
    /// Snowflake account identifier (e.g., 'xy12345.us-east-1').
    /// </summary>
    public string? Account { get; set; }

    /// <summary>
    /// Snowflake username.
    /// </summary>
    public string? User { get; set; }

    /// <summary>
    /// Snowflake password.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// Virtual warehouse to use for queries.
    /// </summary>
    public string? Warehouse { get; set; }

    /// <summary>
    /// Database name to connect to.
    /// </summary>
    public string? Database { get; set; }

    /// <summary>
    /// Schema name to use (defaults to PUBLIC).
    /// </summary>
    public string? Schema { get; set; }

    /// <summary>
    /// Role to assume for the session.
    /// </summary>
    public string? Role { get; set; }

    /// <summary>
    /// Connection timeout in seconds.
    /// </summary>
    public int ConnectionTimeout { get; set; } = 120;

    /// <summary>
    /// Command execution timeout in seconds.
    /// </summary>
    public int CommandTimeout { get; set; } = 300;

    /// <summary>
    /// Session parameters to set on connection (e.g., QUERY_TAG, TIMEZONE).
    /// </summary>
    public Dictionary<string, string>? SessionParameters { get; set; }
}

/// <summary>
/// Options for COPY INTO bulk load operations.
/// </summary>
public class CopyIntoOptions
{
    /// <summary>
    /// Error handling mode: 'CONTINUE', 'SKIP_FILE', 'SKIP_FILE_num', 'SKIP_FILE_num%', 'ABORT_STATEMENT'.
    /// </summary>
    public string? OnError { get; set; } = "ABORT_STATEMENT";

    /// <summary>
    /// Maximum size (in bytes) of data to be loaded.
    /// </summary>
    public long? SizeLimit { get; set; }

    /// <summary>
    /// Whether to remove files from stage after successful load.
    /// </summary>
    public bool Purge { get; set; } = false;

    /// <summary>
    /// Force load even if files were already loaded.
    /// </summary>
    public bool Force { get; set; } = false;

    /// <summary>
    /// Regex pattern to filter files in the stage.
    /// </summary>
    public string? Pattern { get; set; }
}
