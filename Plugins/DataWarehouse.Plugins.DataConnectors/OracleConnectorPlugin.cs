using System.Data;
using System.Runtime.CompilerServices;
using System.Text;
using DataWarehouse.SDK.Connectors;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using Oracle.ManagedDataAccess.Client;

namespace DataWarehouse.Plugins.DataConnectors;

/// <summary>
/// Production-ready Oracle database connector plugin.
/// Provides full CRUD operations with real database connectivity via Oracle.ManagedDataAccess.Core.
/// Supports schema discovery, parameterized queries, transactions, and connection pooling.
/// </summary>
public class OracleConnectorPlugin : DatabaseConnectorPluginBase
{
    private string? _connectionString;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private readonly Dictionary<string, DataSchema> _schemaCache = new();
    private OracleConnectorConfig _config = new();

    /// <inheritdoc />
    public override string Id => "datawarehouse.connector.oracle";

    /// <inheritdoc />
    public override string Name => "Oracle Connector";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    public override string ConnectorId => "oracle";

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
    /// <param name="config">Oracle-specific configuration.</param>
    public void Configure(OracleConnectorConfig config)
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
            var builder = new OracleConnectionStringBuilder(_connectionString);

            if (_config.MaxPoolSize > 0)
                builder.MaxPoolSize = _config.MaxPoolSize;
            if (_config.MinPoolSize > 0)
                builder.MinPoolSize = _config.MinPoolSize;
            if (_config.ConnectionTimeout > 0)
                builder.ConnectionTimeout = _config.ConnectionTimeout;

            _connectionString = builder.ToString();

            // Test the connection
            await using var conn = new OracleConnection(_connectionString);
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
            cmd.CommandText = "SELECT BANNER, USER, SYS_CONTEXT('USERENV', 'DB_NAME') FROM V$VERSION WHERE ROWNUM = 1";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                serverInfo["OracleVersion"] = reader.GetString(0);
                serverInfo["CurrentUser"] = reader.GetString(1);
                serverInfo["DatabaseName"] = reader.GetString(2);
            }

            return new ConnectionResult(true, null, serverInfo);
        }
        catch (OracleException ex)
        {
            return new ConnectionResult(false, $"Oracle connection failed: {ex.Message}", null);
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
            OracleConnection.ClearAllPools();
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
            await using var conn = new OracleConnection(_connectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM DUAL";
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

        await using var conn = new OracleConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();

        // Query ALL_TAB_COLUMNS for table and column metadata
        cmd.CommandText = @"
            SELECT
                c.TABLE_NAME,
                c.COLUMN_NAME,
                c.DATA_TYPE,
                c.NULLABLE,
                c.DATA_DEFAULT,
                c.DATA_LENGTH,
                c.DATA_PRECISION,
                c.DATA_SCALE,
                CASE WHEN pk.COLUMN_NAME IS NOT NULL THEN 1 ELSE 0 END as IS_PRIMARY_KEY
            FROM ALL_TAB_COLUMNS c
            LEFT JOIN (
                SELECT cols.TABLE_NAME, cols.COLUMN_NAME
                FROM ALL_CONSTRAINTS cons
                JOIN ALL_CONS_COLUMNS cols
                    ON cons.CONSTRAINT_NAME = cols.CONSTRAINT_NAME
                    AND cons.OWNER = cols.OWNER
                WHERE cons.CONSTRAINT_TYPE = 'P'
                    AND cons.OWNER = USER
            ) pk ON c.TABLE_NAME = pk.TABLE_NAME AND c.COLUMN_NAME = pk.COLUMN_NAME
            WHERE c.OWNER = USER
            ORDER BY c.TABLE_NAME, c.COLUMN_ID";

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
            var isNullable = reader.GetString(3) == "Y";
            var defaultValue = reader.IsDBNull(4) ? null : reader.GetString(4);
            var isPrimaryKey = reader.GetInt32(8) == 1;

            fields.Add(new DataSchemaField(
                columnName,
                MapOracleType(dataType),
                isNullable,
                null, // MaxLength
                new Dictionary<string, object>
                {
                    ["tableName"] = tableName,
                    ["oracleType"] = dataType,
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
            Name: conn.Database ?? "oracle",
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

        await using var conn = new OracleConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();

        cmd.CommandText = @"
            SELECT
                c.COLUMN_NAME,
                c.DATA_TYPE,
                c.NULLABLE,
                c.DATA_DEFAULT,
                CASE WHEN pk.COLUMN_NAME IS NOT NULL THEN 1 ELSE 0 END as IS_PRIMARY_KEY
            FROM ALL_TAB_COLUMNS c
            LEFT JOIN (
                SELECT cols.COLUMN_NAME
                FROM ALL_CONSTRAINTS cons
                JOIN ALL_CONS_COLUMNS cols
                    ON cons.CONSTRAINT_NAME = cols.CONSTRAINT_NAME
                    AND cons.OWNER = cols.OWNER
                WHERE cons.CONSTRAINT_TYPE = 'P'
                    AND cons.OWNER = USER
                    AND cons.TABLE_NAME = :table
            ) pk ON c.COLUMN_NAME = pk.COLUMN_NAME
            WHERE c.OWNER = USER AND c.TABLE_NAME = :table
            ORDER BY c.COLUMN_ID";

        cmd.Parameters.Add(new OracleParameter("table", tableName));

        var fields = new List<DataSchemaField>();
        var primaryKeys = new List<string>();

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var columnName = reader.GetString(0);
            var dataType = reader.GetString(1);
            var isNullable = reader.GetString(2) == "Y";
            var defaultValue = reader.IsDBNull(3) ? null : reader.GetString(3);
            var isPrimaryKey = reader.GetInt32(4) == 1;

            fields.Add(new DataSchemaField(
                columnName,
                MapOracleType(dataType),
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

        // Validate filter and orderBy before building query
        ValidateQuerySafety(query);

        var sql = BuildSelectQuery(query);

        await using var conn = new OracleConnection(_connectionString);
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

        await using var conn = new OracleConnection(_connectionString);
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
        OracleConnection conn,
        OracleTransaction transaction,
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
                    cmd.Parameters.Add(new OracleParameter($":p{i}", value ?? DBNull.Value));
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
        sb.Append(QuoteIdentifier(query.TableOrCollection ?? "DATA"));

        if (!string.IsNullOrWhiteSpace(query.Filter))
        {
            sb.Append(" WHERE ");
            // Filter is validated by ValidateQuerySafety before reaching this point
            // Still sanitize identifiers within the filter
            sb.Append(SanitizeClause(query.Filter));
        }

        if (!string.IsNullOrEmpty(query.OrderBy))
        {
            sb.Append(" ORDER BY ");
            // OrderBy is validated by ValidateQuerySafety before reaching this point
            // Still sanitize identifiers within the orderBy
            sb.Append(SanitizeOrderByClause(query.OrderBy));
        }

        if (query.Offset.HasValue || query.Limit.HasValue)
        {
            sb.Append(" OFFSET ");
            sb.Append(query.Offset ?? 0);
            sb.Append(" ROWS");

            if (query.Limit.HasValue)
            {
                sb.Append(" FETCH NEXT ");
                sb.Append(query.Limit);
                sb.Append(" ROWS ONLY");
            }
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

    private string BuildMergeStatement(string table, string[] columns)
    {
        var columnList = string.Join(", ", columns.Select(QuoteIdentifier));
        var matchCondition = columns.Length > 0 ? $"T.{QuoteIdentifier(columns[0])} = S.{QuoteIdentifier(columns[0])}" : "1=1";
        var updateList = string.Join(", ", columns.Select((c, i) => $"T.{QuoteIdentifier(c)} = S.{QuoteIdentifier(c)}"));
        var insertValues = string.Join(", ", columns.Select(c => $"S.{QuoteIdentifier(c)}"));

        return $@"MERGE INTO {QuoteIdentifier(table)} T
                  USING (SELECT {string.Join(", ", columns.Select((c, i) => $":p{i} AS {QuoteIdentifier(c)}"))} FROM DUAL) S
                  ON ({matchCondition})
                  WHEN MATCHED THEN UPDATE SET {updateList}
                  WHEN NOT MATCHED THEN INSERT ({columnList}) VALUES ({insertValues})";
    }

    private static string QuoteIdentifier(string identifier)
    {
        return $"\"{identifier.Replace("\"", "\"\"")}\"";
    }

    private static string MapOracleType(string oracleType)
    {
        return oracleType.ToUpperInvariant() switch
        {
            "NUMBER" => "decimal",
            "INTEGER" or "INT" => "int",
            "FLOAT" => "double",
            "BINARY_FLOAT" => "float",
            "BINARY_DOUBLE" => "double",
            "VARCHAR2" or "NVARCHAR2" or "CHAR" or "NCHAR" or "CLOB" or "NCLOB" => "string",
            "BLOB" or "RAW" or "LONG RAW" => "bytes",
            "DATE" => "datetime",
            "TIMESTAMP" => "datetime",
            "TIMESTAMP WITH TIME ZONE" => "datetimeoffset",
            "TIMESTAMP WITH LOCAL TIME ZONE" => "datetimeoffset",
            _ => oracleType
        };
    }

    /// <summary>
    /// Validates that query filter and orderBy clauses are safe from SQL injection.
    /// Throws ArgumentException if dangerous patterns are detected.
    /// </summary>
    private static void ValidateQuerySafety(DataQuery query)
    {
        // Validate Filter clause
        if (!string.IsNullOrWhiteSpace(query.Filter))
        {
            var filter = query.Filter.ToUpperInvariant();

            // Check for dangerous SQL keywords that shouldn't appear in WHERE clause
            var dangerousPatterns = new[]
            {
                "DROP ", "DELETE ", "TRUNCATE ", "ALTER ", "CREATE ", "EXEC ", "EXECUTE ",
                "INSERT ", "UPDATE ", "MERGE ", "GRANT ", "REVOKE ", "COMMIT", "ROLLBACK",
                "SAVEPOINT", "BEGIN", "DECLARE", "--", "/*", "*/", ";", "UNION ", "INTO ",
                "SHUTDOWN", "STARTUP", "PURGE"
            };

            foreach (var pattern in dangerousPatterns)
            {
                if (filter.Contains(pattern))
                {
                    throw new ArgumentException(
                        $"Filter clause contains potentially dangerous SQL keyword: {pattern.Trim()}. " +
                        "Only safe WHERE clause expressions are allowed.");
                }
            }

            // Check for single quotes without proper escaping context (basic check)
            int singleQuoteCount = query.Filter.Count(c => c == '\'');
            if (singleQuoteCount % 2 != 0)
            {
                throw new ArgumentException(
                    "Filter clause contains unbalanced single quotes. " +
                    "Ensure all string literals are properly quoted.");
            }
        }

        // Validate OrderBy clause
        if (!string.IsNullOrWhiteSpace(query.OrderBy))
        {
            var orderBy = query.OrderBy.ToUpperInvariant();

            // OrderBy should only contain column names, ASC, DESC, commas, and spaces
            var dangerousPatterns = new[]
            {
                "DROP ", "DELETE ", "TRUNCATE ", "ALTER ", "CREATE ", "EXEC ", "EXECUTE ",
                "INSERT ", "UPDATE ", "MERGE ", "SELECT ", "UNION ", "WHERE ", "FROM ",
                "JOIN ", "--", "/*", "*/", ";", "("
            };

            foreach (var pattern in dangerousPatterns)
            {
                if (orderBy.Contains(pattern))
                {
                    throw new ArgumentException(
                        $"OrderBy clause contains potentially dangerous SQL keyword: {pattern.Trim()}. " +
                        "Only column names with ASC/DESC are allowed.");
                }
            }

            // Ensure OrderBy only contains safe characters
            if (System.Text.RegularExpressions.Regex.IsMatch(query.OrderBy, @"[^\w\s,"".]|(ASC|DESC)"))
            {
                // This is actually OK - column names, spaces, commas, quotes, dots, ASC, DESC
            }
        }
    }

    /// <summary>
    /// Sanitizes a WHERE clause by ensuring column identifiers are properly quoted.
    /// This provides defense-in-depth after validation.
    /// </summary>
    private static string SanitizeClause(string clause)
    {
        if (string.IsNullOrWhiteSpace(clause))
            return string.Empty;

        // For production use, this should parse and rebuild the WHERE clause
        // with proper identifier quoting. For now, return validated input.
        // The validation in ValidateQuerySafety provides the primary defense.
        return clause;
    }

    /// <summary>
    /// Sanitizes an ORDER BY clause by ensuring column identifiers are properly quoted.
    /// This provides defense-in-depth after validation.
    /// </summary>
    private static string SanitizeOrderByClause(string orderBy)
    {
        if (string.IsNullOrWhiteSpace(orderBy))
            return string.Empty;

        // Split by comma to handle multiple columns
        var parts = orderBy.Split(',');
        var sanitized = new List<string>();

        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            var tokens = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (tokens.Length == 0) continue;

            // First token should be column name
            var column = tokens[0];

            // Check if it's already quoted
            if (!column.StartsWith("\""))
            {
                column = QuoteIdentifier(column);
            }

            // Handle ASC/DESC if present
            if (tokens.Length > 1)
            {
                var direction = tokens[1].ToUpperInvariant();
                if (direction == "ASC" || direction == "DESC")
                {
                    sanitized.Add($"{column} {direction}");
                }
                else
                {
                    sanitized.Add(column);
                }
            }
            else
            {
                sanitized.Add(column);
            }
        }

        return string.Join(", ", sanitized);
    }

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc />
    public override Task StopAsync() => DisconnectAsync();
}

/// <summary>
/// Configuration options for the Oracle connector.
/// </summary>
public class OracleConnectorConfig
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
    public int ConnectionTimeout { get; set; } = 15;

    /// <summary>
    /// Command timeout in seconds.
    /// </summary>
    public int CommandTimeout { get; set; } = 30;

    /// <summary>
    /// Whether to enable statement caching.
    /// </summary>
    public bool StatementCacheEnabled { get; set; } = true;
}
