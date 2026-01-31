using System.Data;
using System.Data.Odbc;
using System.Runtime.CompilerServices;
using System.Text;
using DataWarehouse.SDK.Connectors;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;

namespace DataWarehouse.Plugins.DataConnectors;

/// <summary>
/// Production-ready IBM AS/400 (iSeries) database connector plugin.
/// Provides comprehensive access to AS/400 systems via ODBC driver.
/// Supports DB2/400 SQL queries, program calls (QCMDEXC), data queues, message queues,
/// spool file access, record-level access, and physical/logical file operations.
/// </summary>
public class As400ConnectorPlugin : DatabaseConnectorPluginBase
{
    private string? _connectionString;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private readonly Dictionary<string, DataSchema> _schemaCache = new();
    private As400ConnectorConfig _config = new();

    /// <inheritdoc />
    public override string Id => "datawarehouse.connector.as400";

    /// <inheritdoc />
    public override string Name => "IBM AS/400 Connector";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    public override string ConnectorId => "as400";

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
    /// Configures the connector with AS/400-specific options.
    /// </summary>
    /// <param name="config">AS/400-specific configuration.</param>
    public void Configure(As400ConnectorConfig config)
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

            // Build ODBC connection string for AS/400
            var builder = new OdbcConnectionStringBuilder(_connectionString);

            // Apply AS/400-specific configuration
            if (!string.IsNullOrEmpty(_config.DefaultLibrary))
            {
                builder["DBQ"] = _config.DefaultLibrary;
            }

            if (!string.IsNullOrEmpty(_config.LibraryList))
            {
                builder["LIBL"] = _config.LibraryList;
            }

            if (_config.NamingConvention == As400NamingConvention.System)
            {
                builder["NAM"] = "1"; // System naming (library/file)
            }
            else
            {
                builder["NAM"] = "0"; // SQL naming (schema.table)
            }

            if (_config.DateFormat != As400DateFormat.Default)
            {
                builder["DFT"] = MapDateFormat(_config.DateFormat);
            }

            if (_config.DateSeparator != As400DateSeparator.Default)
            {
                builder["DSP"] = MapDateSeparator(_config.DateSeparator);
            }

            if (_config.CommitMode != As400CommitMode.None)
            {
                builder["CMT"] = MapCommitMode(_config.CommitMode);
            }

            _connectionString = builder.ConnectionString;

            // Test the connection
            using var conn = new OdbcConnection(_connectionString);
            await Task.Run(() => conn.Open(), ct);

            // Retrieve AS/400 system info
            var serverInfo = new Dictionary<string, object>
            {
                ["DataSource"] = conn.DataSource ?? "unknown",
                ["Database"] = conn.Database ?? "unknown",
                ["Driver"] = conn.Driver ?? "unknown",
                ["ServerVersion"] = conn.ServerVersion ?? "unknown",
                ["ConnectionState"] = conn.State.ToString()
            };

            // Get OS/400 version and system information
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT OS_VERSION, OS_RELEASE FROM QSYS2.SYSTEM_STATUS_INFO";
            cmd.CommandTimeout = 30;

            try
            {
                using var reader = await Task.Run(() => cmd.ExecuteReader(), ct);
                if (await Task.Run(() => reader.Read(), ct))
                {
                    serverInfo["OS400_Version"] = reader.IsDBNull(0) ? "unknown" : reader.GetString(0);
                    serverInfo["OS400_Release"] = reader.IsDBNull(1) ? "unknown" : reader.GetString(1);
                }
            }
            catch
            {
                // Fallback if QSYS2.SYSTEM_STATUS_INFO is not available
                serverInfo["OS400_Version"] = "unavailable";
                serverInfo["OS400_Release"] = "unavailable";
            }

            // Get current user and library
            cmd.CommandText = "SELECT CURRENT_USER, CURRENT_SCHEMA FROM SYSIBM.SYSDUMMY1";
            try
            {
                using var reader = await Task.Run(() => cmd.ExecuteReader(), ct);
                if (await Task.Run(() => reader.Read(), ct))
                {
                    serverInfo["CurrentUser"] = reader.IsDBNull(0) ? "unknown" : reader.GetString(0);
                    serverInfo["CurrentLibrary"] = reader.IsDBNull(1) ? "unknown" : reader.GetString(1);
                }
            }
            catch
            {
                serverInfo["CurrentUser"] = "unavailable";
                serverInfo["CurrentLibrary"] = "unavailable";
            }

            return new ConnectionResult(true, null, serverInfo);
        }
        catch (OdbcException ex)
        {
            return new ConnectionResult(false, $"AS/400 ODBC connection failed: {ex.Message} (SQL State: {ex.Errors[0]?.SQLState})", null);
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
            using var conn = new OdbcConnection(_connectionString);
            await Task.Run(() => conn.Open());
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM SYSIBM.SYSDUMMY1";
            cmd.CommandTimeout = 5;
            await Task.Run(() => cmd.ExecuteScalar());
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
            throw new InvalidOperationException("Not connected to AS/400");

        using var conn = new OdbcConnection(_connectionString);
        await Task.Run(() => conn.Open());
        using var cmd = conn.CreateCommand();

        // Query QSYS2.SYSCOLUMNS for table and column metadata
        var libraryFilter = string.IsNullOrEmpty(_config.DefaultLibrary)
            ? "AND TABLE_SCHEMA NOT IN ('QSYS', 'QSYS2', 'SYSIBM')"
            : $"AND TABLE_SCHEMA = '{_config.DefaultLibrary}'";

        cmd.CommandText = $@"
            SELECT
                c.TABLE_SCHEMA,
                c.TABLE_NAME,
                c.COLUMN_NAME,
                c.DATA_TYPE,
                c.IS_NULLABLE,
                c.COLUMN_DEFAULT,
                c.CHARACTER_MAXIMUM_LENGTH,
                c.NUMERIC_PRECISION,
                c.NUMERIC_SCALE,
                c.ORDINAL_POSITION,
                CASE WHEN k.CONSTRAINT_NAME IS NOT NULL THEN 'YES' ELSE 'NO' END AS IS_PRIMARY_KEY
            FROM QSYS2.SYSCOLUMNS c
            LEFT JOIN QSYS2.SYSKEYCST k
                ON c.TABLE_SCHEMA = k.TABLE_SCHEMA
                AND c.TABLE_NAME = k.TABLE_NAME
                AND c.COLUMN_NAME = k.COLUMN_NAME
                AND k.CONSTRAINT_TYPE = 'PRIMARY KEY'
            WHERE c.TABLE_TYPE = 'T'
            {libraryFilter}
            ORDER BY c.TABLE_SCHEMA, c.TABLE_NAME, c.ORDINAL_POSITION";

        var fields = new List<DataSchemaField>();
        var primaryKeys = new List<string>();
        var tableCount = 0;
        var currentTable = "";

        using var reader = await Task.Run(() => cmd.ExecuteReader());
        while (await Task.Run(() => reader.Read()))
        {
            var schemaName = reader.GetString(0).Trim();
            var tableName = reader.GetString(1).Trim();
            var fullTableName = $"{schemaName}.{tableName}";

            if (fullTableName != currentTable)
            {
                currentTable = fullTableName;
                tableCount++;
            }

            var columnName = reader.GetString(2).Trim();
            var dataType = reader.GetString(3).Trim();
            var isNullable = reader.GetString(4).Trim() == "Y";
            var defaultValue = reader.IsDBNull(5) ? null : reader.GetString(5);
            var isPrimaryKey = reader.GetString(10).Trim() == "YES";

            fields.Add(new DataSchemaField(
                columnName,
                MapAs400Type(dataType),
                isNullable,
                null, // MaxLength
                new Dictionary<string, object>
                {
                    ["schema"] = schemaName,
                    ["tableName"] = tableName,
                    ["fullTableName"] = fullTableName,
                    ["as400Type"] = dataType,
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
            Name: conn.Database ?? "AS400",
            Fields: fields.ToArray(),
            PrimaryKeys: primaryKeys.Distinct().ToArray(),
            Metadata: new Dictionary<string, object>
            {
                ["TableCount"] = tableCount,
                ["FieldCount"] = fields.Count,
                ["SchemaVersion"] = "1.0",
                ["SystemType"] = "IBM AS/400 (iSeries)"
            }
        );
    }

    /// <summary>
    /// Gets the schema for a specific physical or logical file (table).
    /// </summary>
    /// <param name="library">Library name (schema).</param>
    /// <param name="fileName">File name (table).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>File schema.</returns>
    public async Task<DataSchema> GetFileSchemaAsync(string library, string fileName, CancellationToken ct = default)
    {
        var cacheKey = $"{library}.{fileName}";
        if (_schemaCache.TryGetValue(cacheKey, out var cached))
            return cached;

        if (string.IsNullOrEmpty(_connectionString))
            throw new InvalidOperationException("Not connected to AS/400");

        using var conn = new OdbcConnection(_connectionString);
        await Task.Run(() => conn.Open(), ct);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = @"
            SELECT
                c.COLUMN_NAME,
                c.DATA_TYPE,
                c.IS_NULLABLE,
                c.COLUMN_DEFAULT,
                c.CHARACTER_MAXIMUM_LENGTH,
                c.NUMERIC_PRECISION,
                c.NUMERIC_SCALE,
                CASE WHEN k.CONSTRAINT_NAME IS NOT NULL THEN 'YES' ELSE 'NO' END AS IS_PRIMARY_KEY
            FROM QSYS2.SYSCOLUMNS c
            LEFT JOIN QSYS2.SYSKEYCST k
                ON c.TABLE_SCHEMA = k.TABLE_SCHEMA
                AND c.TABLE_NAME = k.TABLE_NAME
                AND c.COLUMN_NAME = k.COLUMN_NAME
                AND k.CONSTRAINT_TYPE = 'PRIMARY KEY'
            WHERE c.TABLE_SCHEMA = ? AND c.TABLE_NAME = ?
            ORDER BY c.ORDINAL_POSITION";

        cmd.Parameters.Add(new OdbcParameter("@library", library));
        cmd.Parameters.Add(new OdbcParameter("@file", fileName));

        var fields = new List<DataSchemaField>();
        var primaryKeys = new List<string>();

        using var reader = await Task.Run(() => cmd.ExecuteReader(), ct);
        while (await Task.Run(() => reader.Read(), ct))
        {
            var columnName = reader.GetString(0).Trim();
            var dataType = reader.GetString(1).Trim();
            var isNullable = reader.GetString(2).Trim() == "Y";
            var defaultValue = reader.IsDBNull(3) ? null : reader.GetString(3);
            var isPrimaryKey = reader.GetString(7).Trim() == "YES";

            fields.Add(new DataSchemaField(
                columnName,
                MapAs400Type(dataType),
                isNullable,
                null,
                new Dictionary<string, object>
                {
                    ["defaultValue"] = defaultValue ?? (object)DBNull.Value,
                    ["as400Type"] = dataType
                }
            ));

            if (isPrimaryKey)
                primaryKeys.Add(columnName);
        }

        var schema = new DataSchema(
            Name: $"{library}.{fileName}",
            Fields: fields.ToArray(),
            PrimaryKeys: primaryKeys.ToArray(),
            Metadata: new Dictionary<string, object>
            {
                ["Library"] = library,
                ["FileName"] = fileName
            }
        );

        _schemaCache[cacheKey] = schema;
        return schema;
    }

    /// <inheritdoc />
    protected override async IAsyncEnumerable<DataRecord> ExecuteReadAsync(
        DataQuery query,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_connectionString))
            throw new InvalidOperationException("Not connected to AS/400");

        var sql = BuildSelectQuery(query);

        using var conn = new OdbcConnection(_connectionString);
        await Task.Run(() => conn.Open(), ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;

        if (_config.CommandTimeout > 0)
            cmd.CommandTimeout = _config.CommandTimeout;

        long position = query.Offset ?? 0;

        using var reader = await Task.Run(() => cmd.ExecuteReader(), ct);

        while (await Task.Run(() => reader.Read(), ct))
        {
            if (ct.IsCancellationRequested) yield break;

            var values = new Dictionary<string, object?>();
            for (int i = 0; i < reader.FieldCount; i++)
            {
                var columnName = reader.GetName(i).Trim();
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
            throw new InvalidOperationException("Not connected to AS/400");

        long written = 0;
        long failed = 0;
        var errors = new List<string>();

        var targetFile = options.TargetTable ?? throw new ArgumentException("TargetTable (file name) is required");

        // Parse library and file name
        var parts = targetFile.Split('.');
        string library, fileName;
        if (parts.Length == 2)
        {
            library = parts[0];
            fileName = parts[1];
        }
        else
        {
            library = _config.DefaultLibrary ?? throw new InvalidOperationException("Default library not configured");
            fileName = targetFile;
        }

        // Get schema for the target file
        var schema = await GetFileSchemaAsync(library, fileName, ct);
        var columns = schema.Fields.Select(f => f.Name).ToArray();

        using var conn = new OdbcConnection(_connectionString);
        await Task.Run(() => conn.Open(), ct);

        // Use transaction for batch writes if commit mode is enabled
        OdbcTransaction? transaction = null;
        if (_config.CommitMode != As400CommitMode.None)
        {
            transaction = conn.BeginTransaction();
        }

        try
        {
            var batch = new List<DataRecord>();

            await foreach (var record in records.WithCancellation(ct))
            {
                batch.Add(record);

                if (batch.Count >= options.BatchSize)
                {
                    var (w, f, e) = await WriteBatchAsync(conn, transaction, library, fileName, columns, batch, options.Mode, ct);
                    written += w;
                    failed += f;
                    errors.AddRange(e);
                    batch.Clear();
                }
            }

            // Write remaining records
            if (batch.Count > 0)
            {
                var (w, f, e) = await WriteBatchAsync(conn, transaction, library, fileName, columns, batch, options.Mode, ct);
                written += w;
                failed += f;
                errors.AddRange(e);
            }

            if (transaction != null)
            {
                await Task.Run(() => transaction.Commit(), ct);
            }
        }
        catch (Exception ex)
        {
            if (transaction != null)
            {
                await Task.Run(() => transaction.Rollback(), ct);
            }
            errors.Add($"Transaction failed: {ex.Message}");
            failed += written;
            written = 0;
        }
        finally
        {
            transaction?.Dispose();
        }

        return new WriteResult(written, failed, errors.Count > 0 ? errors.ToArray() : null);
    }

    private async Task<(long written, long failed, List<string> errors)> WriteBatchAsync(
        OdbcConnection conn,
        OdbcTransaction? transaction,
        string library,
        string fileName,
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
                using var cmd = conn.CreateCommand();
                if (transaction != null)
                    cmd.Transaction = transaction;

                var recordColumns = record.Values.Keys.Where(k => columns.Contains(k)).ToArray();
                var fullTableName = $"{library}.{fileName}";

                if (mode == SDK.Connectors.WriteMode.Upsert)
                {
                    // AS/400 doesn't support MERGE, use INSERT or UPDATE pattern
                    cmd.CommandText = BuildUpsertStatement(fullTableName, recordColumns);
                }
                else
                {
                    cmd.CommandText = BuildInsertStatement(fullTableName, recordColumns);
                }

                for (int i = 0; i < recordColumns.Length; i++)
                {
                    var value = record.Values[recordColumns[i]];
                    cmd.Parameters.Add(new OdbcParameter($"@p{i}", value ?? DBNull.Value));
                }

                await Task.Run(() => cmd.ExecuteNonQuery(), ct);
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
    /// Executes an AS/400 program call using QCMDEXC.
    /// </summary>
    /// <param name="command">CL command to execute.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if command executed successfully.</returns>
    public async Task<bool> ExecuteProgramCallAsync(string command, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_connectionString))
            throw new InvalidOperationException("Not connected to AS/400");

        using var conn = new OdbcConnection(_connectionString);
        await Task.Run(() => conn.Open(), ct);
        using var cmd = conn.CreateCommand();

        // Use QCMDEXC to execute CL commands
        cmd.CommandText = $"CALL QSYS.QCMDEXC('{command}', {command.Length:D15}.00000)";

        try
        {
            await Task.Run(() => cmd.ExecuteNonQuery(), ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Writes a message to an AS/400 data queue.
    /// </summary>
    /// <param name="library">Library containing the data queue.</param>
    /// <param name="queueName">Data queue name.</param>
    /// <param name="message">Message data.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Task representing the operation.</returns>
    public async Task WriteDataQueueAsync(string library, string queueName, string message, CancellationToken ct = default)
    {
        var command = $"CALL QSYS.QSNDDTAQ('{queueName}', '{library}', '{message.Length}', '{message}')";
        await ExecuteProgramCallAsync(command, ct);
    }

    /// <summary>
    /// Reads a message from an AS/400 data queue.
    /// </summary>
    /// <param name="library">Library containing the data queue.</param>
    /// <param name="queueName">Data queue name.</param>
    /// <param name="waitTime">Wait time in seconds (-1 for infinite).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Message data if available.</returns>
    public async Task<string?> ReadDataQueueAsync(string library, string queueName, int waitTime = 0, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_connectionString))
            throw new InvalidOperationException("Not connected to AS/400");

        using var conn = new OdbcConnection(_connectionString);
        await Task.Run(() => conn.Open(), ct);
        using var cmd = conn.CreateCommand();

        // Use QRCVDTAQ to receive from data queue
        cmd.CommandText = $@"
            DECLARE @msgData CHAR(32766);
            DECLARE @msgLen DECIMAL(5,0);
            CALL QSYS.QRCVDTAQ('{queueName}', '{library}', '{32766}', @msgData, @msgLen, '{waitTime}');
            SELECT @msgData, @msgLen";

        try
        {
            using var reader = await Task.Run(() => cmd.ExecuteReader(), ct);
            if (await Task.Run(() => reader.Read(), ct))
            {
                var data = reader.IsDBNull(0) ? null : reader.GetString(0);
                var length = reader.IsDBNull(1) ? 0 : reader.GetDecimal(1);
                return data?.Substring(0, (int)length);
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Sends a message to an AS/400 message queue.
    /// </summary>
    /// <param name="messageQueue">Message queue name (e.g., "QSYSOPR").</param>
    /// <param name="library">Library containing the message queue.</param>
    /// <param name="message">Message text.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Task representing the operation.</returns>
    public async Task SendMessageAsync(string messageQueue, string library, string message, CancellationToken ct = default)
    {
        var command = $"SNDMSG MSG('{message}') TOUSR({messageQueue}) MSGTYPE(*INFO)";
        await ExecuteProgramCallAsync(command, ct);
    }

    /// <summary>
    /// Retrieves spool file data from AS/400.
    /// </summary>
    /// <param name="spoolFileName">Spool file name.</param>
    /// <param name="jobName">Job name that created the spool file.</param>
    /// <param name="jobUser">Job user.</param>
    /// <param name="jobNumber">Job number.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Spool file records.</returns>
    public async IAsyncEnumerable<string> GetSpoolFileDataAsync(
        string spoolFileName,
        string jobName,
        string jobUser,
        string jobNumber,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_connectionString))
            throw new InvalidOperationException("Not connected to AS/400");

        using var conn = new OdbcConnection(_connectionString);
        await Task.Run(() => conn.Open(), ct);
        using var cmd = conn.CreateCommand();

        // Query spool file data from QSYS2.OUTPUT_QUEUE_ENTRIES
        cmd.CommandText = $@"
            SELECT SPOOLED_DATA
            FROM QSYS2.OUTPUT_QUEUE_ENTRIES_BASIC
            WHERE SPOOLED_FILE_NAME = '{spoolFileName}'
              AND JOB_NAME = '{jobName}/{jobUser}/{jobNumber}'
            ORDER BY FILE_NUMBER";

        using var reader = await Task.Run(() => cmd.ExecuteReader(), ct);
        while (await Task.Run(() => reader.Read(), ct))
        {
            if (ct.IsCancellationRequested) yield break;

            var data = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            yield return data;
        }
    }

    /// <summary>
    /// Performs record-level access on an AS/400 physical file.
    /// </summary>
    /// <param name="library">Library name.</param>
    /// <param name="fileName">Physical file name.</param>
    /// <param name="recordNumber">Record number to read (1-based).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Record data.</returns>
    public async Task<DataRecord?> ReadRecordByNumberAsync(string library, string fileName, long recordNumber, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_connectionString))
            throw new InvalidOperationException("Not connected to AS/400");

        using var conn = new OdbcConnection(_connectionString);
        await Task.Run(() => conn.Open(), ct);
        using var cmd = conn.CreateCommand();

        // Use RRN (relative record number) to access specific record
        cmd.CommandText = $@"
            SELECT *
            FROM {library}.{fileName}
            WHERE RRN(RECORD) = {recordNumber}";

        try
        {
            using var reader = await Task.Run(() => cmd.ExecuteReader(), ct);
            if (await Task.Run(() => reader.Read(), ct))
            {
                var values = new Dictionary<string, object?>();
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    var columnName = reader.GetName(i).Trim();
                    values[columnName] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }

                return new DataRecord(
                    Values: values,
                    Position: recordNumber,
                    Timestamp: DateTimeOffset.UtcNow
                );
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Lists all physical and logical files in a library.
    /// </summary>
    /// <param name="library">Library name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of file names with types.</returns>
    public async Task<List<(string FileName, string FileType, string Description)>> ListFilesAsync(string library, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_connectionString))
            throw new InvalidOperationException("Not connected to AS/400");

        using var conn = new OdbcConnection(_connectionString);
        await Task.Run(() => conn.Open(), ct);
        using var cmd = conn.CreateCommand();

        cmd.CommandText = $@"
            SELECT TABLE_NAME, TABLE_TYPE, TABLE_TEXT
            FROM QSYS2.SYSTABLES
            WHERE TABLE_SCHEMA = '{library}'
              AND TABLE_TYPE IN ('P', 'L')
            ORDER BY TABLE_NAME";

        var files = new List<(string, string, string)>();

        using var reader = await Task.Run(() => cmd.ExecuteReader(), ct);
        while (await Task.Run(() => reader.Read(), ct))
        {
            var fileName = reader.GetString(0).Trim();
            var fileType = reader.GetString(1).Trim() == "P" ? "Physical" : "Logical";
            var description = reader.IsDBNull(2) ? "" : reader.GetString(2).Trim();
            files.Add((fileName, fileType, description));
        }

        return files;
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
                sb.Append(" ORDER BY 1");
            }
            sb.Append($" OFFSET {query.Offset} ROWS");
            if (query.Limit.HasValue)
            {
                sb.Append($" FETCH FIRST {query.Limit} ROWS ONLY");
            }
        }

        return sb.ToString();
    }

    /// <inheritdoc />
    protected override string BuildInsertStatement(string table, string[] columns)
    {
        var columnList = string.Join(", ", columns.Select(QuoteIdentifier));
        var paramList = string.Join(", ", columns.Select((_, i) => "?"));

        return $"INSERT INTO {QuoteIdentifier(table)} ({columnList}) VALUES ({paramList})";
    }

    private string BuildUpsertStatement(string table, string[] columns)
    {
        // AS/400 DB2 doesn't support MERGE like SQL Server, use UPDATE or INSERT pattern
        var updateList = string.Join(", ", columns.Select((c, i) => $"{QuoteIdentifier(c)} = ?"));
        var columnList = string.Join(", ", columns.Select(QuoteIdentifier));
        var paramList = string.Join(", ", columns.Select((_, i) => "?"));

        // Note: This requires primary key to exist and be the first column
        return $@"
            UPDATE {QuoteIdentifier(table)}
            SET {updateList}
            WHERE {QuoteIdentifier(columns[0])} = ?;
            INSERT INTO {QuoteIdentifier(table)} ({columnList})
            SELECT {paramList}
            FROM SYSIBM.SYSDUMMY1
            WHERE NOT EXISTS (SELECT 1 FROM {QuoteIdentifier(table)} WHERE {QuoteIdentifier(columns[0])} = ?)";
    }

    private static string QuoteIdentifier(string identifier)
    {
        // AS/400 uses double quotes for SQL naming or no quotes for system naming
        return identifier.Contains('.')
            ? string.Join(".", identifier.Split('.').Select(p => $"\"{p.Trim()}\""))
            : $"\"{identifier.Trim()}\"";
    }

    private static string MapAs400Type(string as400Type)
    {
        var upperType = as400Type.ToUpperInvariant();
        return upperType switch
        {
            "INTEGER" or "INT" or "SMALLINT" => "int",
            "BIGINT" => "long",
            "DECIMAL" or "NUMERIC" or "DEC" => "decimal",
            "REAL" or "FLOAT" => "double",
            "DOUBLE" or "DOUBLE PRECISION" => "double",
            "CHAR" or "CHARACTER" or "VARCHAR" or "GRAPHIC" or "VARGRAPHIC" => "string",
            "CLOB" or "DBCLOB" => "string",
            "BINARY" or "VARBINARY" or "BLOB" => "bytes",
            "DATE" => "date",
            "TIME" => "time",
            "TIMESTAMP" => "datetime",
            "DATALINK" => "string",
            "ROWID" => "string",
            _ => as400Type
        };
    }

    private static string MapDateFormat(As400DateFormat format)
    {
        return format switch
        {
            As400DateFormat.Iso => "ISO",
            As400DateFormat.Usa => "USA",
            As400DateFormat.Eur => "EUR",
            As400DateFormat.Jis => "JIS",
            As400DateFormat.Mdy => "MDY",
            As400DateFormat.Dmy => "DMY",
            As400DateFormat.Ymd => "YMD",
            As400DateFormat.Jul => "JUL",
            _ => "ISO"
        };
    }

    private static string MapDateSeparator(As400DateSeparator separator)
    {
        return separator switch
        {
            As400DateSeparator.Slash => "/",
            As400DateSeparator.Dash => "-",
            As400DateSeparator.Period => ".",
            As400DateSeparator.Comma => ",",
            As400DateSeparator.Blank => " ",
            _ => "/"
        };
    }

    private static string MapCommitMode(As400CommitMode mode)
    {
        return mode switch
        {
            As400CommitMode.None => "0",
            As400CommitMode.CHG => "1",
            As400CommitMode.CS => "2",
            As400CommitMode.ALL => "3",
            As400CommitMode.RR => "4",
            _ => "0"
        };
    }

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc />
    public override Task StopAsync() => DisconnectAsync();
}

/// <summary>
/// Configuration options for the AS/400 connector.
/// </summary>
public class As400ConnectorConfig
{
    /// <summary>
    /// Default library (schema) to use for operations.
    /// </summary>
    public string? DefaultLibrary { get; set; }

    /// <summary>
    /// Library list to set for the connection (comma-separated).
    /// </summary>
    public string? LibraryList { get; set; }

    /// <summary>
    /// Naming convention: System (library/file) or SQL (schema.table).
    /// </summary>
    public As400NamingConvention NamingConvention { get; set; } = As400NamingConvention.Sql;

    /// <summary>
    /// Date format for the connection.
    /// </summary>
    public As400DateFormat DateFormat { get; set; } = As400DateFormat.Default;

    /// <summary>
    /// Date separator character.
    /// </summary>
    public As400DateSeparator DateSeparator { get; set; } = As400DateSeparator.Default;

    /// <summary>
    /// Commitment control mode.
    /// </summary>
    public As400CommitMode CommitMode { get; set; } = As400CommitMode.None;

    /// <summary>
    /// Command timeout in seconds.
    /// </summary>
    public int CommandTimeout { get; set; } = 30;

    /// <summary>
    /// Whether to use binary large objects for BLOB/CLOB fields.
    /// </summary>
    public bool UseBinaryLargeObjects { get; set; } = true;

    /// <summary>
    /// Whether to trim character field values.
    /// </summary>
    public bool TrimCharFields { get; set; } = true;
}

/// <summary>
/// AS/400 naming convention options.
/// </summary>
public enum As400NamingConvention
{
    /// <summary>
    /// SQL naming: schema.table format.
    /// </summary>
    Sql = 0,

    /// <summary>
    /// System naming: library/file format.
    /// </summary>
    System = 1
}

/// <summary>
/// AS/400 date format options.
/// </summary>
public enum As400DateFormat
{
    /// <summary>
    /// Default format (ISO).
    /// </summary>
    Default = 0,

    /// <summary>
    /// ISO format: yyyy-mm-dd.
    /// </summary>
    Iso = 1,

    /// <summary>
    /// USA format: mm/dd/yyyy.
    /// </summary>
    Usa = 2,

    /// <summary>
    /// European format: dd.mm.yyyy.
    /// </summary>
    Eur = 3,

    /// <summary>
    /// Japanese Industrial Standard: yyyy-mm-dd.
    /// </summary>
    Jis = 4,

    /// <summary>
    /// Month/Day/Year: mm/dd/yy.
    /// </summary>
    Mdy = 5,

    /// <summary>
    /// Day/Month/Year: dd/mm/yy.
    /// </summary>
    Dmy = 6,

    /// <summary>
    /// Year/Month/Day: yy/mm/dd.
    /// </summary>
    Ymd = 7,

    /// <summary>
    /// Julian: yy/ddd.
    /// </summary>
    Jul = 8
}

/// <summary>
/// AS/400 date separator options.
/// </summary>
public enum As400DateSeparator
{
    /// <summary>
    /// Default separator (slash).
    /// </summary>
    Default = 0,

    /// <summary>
    /// Slash separator: /.
    /// </summary>
    Slash = 1,

    /// <summary>
    /// Dash separator: -.
    /// </summary>
    Dash = 2,

    /// <summary>
    /// Period separator: .
    /// </summary>
    Period = 3,

    /// <summary>
    /// Comma separator: ,.
    /// </summary>
    Comma = 4,

    /// <summary>
    /// Blank separator (space).
    /// </summary>
    Blank = 5
}

/// <summary>
/// AS/400 commitment control modes.
/// </summary>
public enum As400CommitMode
{
    /// <summary>
    /// No commitment control.
    /// </summary>
    None = 0,

    /// <summary>
    /// Change commitment control (*CHG).
    /// </summary>
    CHG = 1,

    /// <summary>
    /// Cursor stability (*CS).
    /// </summary>
    CS = 2,

    /// <summary>
    /// All commitment control (*ALL).
    /// </summary>
    ALL = 3,

    /// <summary>
    /// Repeatable read (*RR).
    /// </summary>
    RR = 4
}
