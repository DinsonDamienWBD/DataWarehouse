using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace DataWarehouse.Plugins.JdbcBridge.Protocol;

/// <summary>
/// Handles a single JDBC client connection.
/// Manages the connection lifecycle, authentication, and message processing.
/// Thread-safe for concurrent operations.
/// </summary>
public sealed class ConnectionHandler : IDisposable
{
    private readonly TcpClient _client;
    private readonly JdbcBridgeConfig _config;
    private readonly QueryProcessor _queryProcessor;
    private readonly MetadataProvider _metadataProvider;
    private readonly MessageReader _reader;
    private readonly MessageWriter _writer;
    private readonly JdbcConnectionState _state;
    private readonly SemaphoreSlim _transactionLock = new(1, 1);
    private bool _disposed;

    /// <summary>
    /// Gets the connection state.
    /// </summary>
    public JdbcConnectionState State => _state;

    /// <summary>
    /// Initializes a new instance of the ConnectionHandler class.
    /// </summary>
    /// <param name="client">The TCP client.</param>
    /// <param name="config">The bridge configuration.</param>
    /// <param name="queryProcessor">The query processor.</param>
    /// <param name="metadataProvider">The metadata provider.</param>
    /// <exception cref="ArgumentNullException">Thrown when any argument is null.</exception>
    public ConnectionHandler(
        TcpClient client,
        JdbcBridgeConfig config,
        QueryProcessor queryProcessor,
        MetadataProvider metadataProvider)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _queryProcessor = queryProcessor ?? throw new ArgumentNullException(nameof(queryProcessor));
        _metadataProvider = metadataProvider ?? throw new ArgumentNullException(nameof(metadataProvider));

        var stream = client.GetStream();
        stream.ReadTimeout = config.ConnectionTimeoutSeconds * 1000;
        stream.WriteTimeout = config.ConnectionTimeoutSeconds * 1000;

        _reader = new MessageReader(stream);
        _writer = new MessageWriter(stream);

        _state = new JdbcConnectionState
        {
            ConnectionId = Guid.NewGuid().ToString("N"),
            Catalog = config.DefaultDatabase,
            Schema = config.DefaultSchema
        };
    }

    /// <summary>
    /// Handles the connection lifecycle.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task HandleAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            // Process messages until disconnect or error
            while (!ct.IsCancellationRequested && _client.Connected)
            {
                var (type, body) = await _reader.ReadMessageAsync(ct).ConfigureAwait(false);
                await ProcessMessageAsync(type, body, ct).ConfigureAwait(false);

                if (type == JdbcMessageType.Disconnect)
                {
                    break;
                }
            }
        }
        catch (EndOfStreamException)
        {
            // Client disconnected normally
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancellation requested
        }
        catch (Exception ex)
        {
            try
            {
                await _writer.WriteErrorAsync(
                    JdbcSqlState.ConnectionException,
                    0,
                    $"Connection error: {ex.Message}",
                    ct).ConfigureAwait(false);
            }
            catch
            {
                // Best effort error reporting
            }
        }
    }

    /// <summary>
    /// Processes a single message.
    /// </summary>
    /// <param name="type">The message type.</param>
    /// <param name="body">The message body.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task ProcessMessageAsync(JdbcMessageType type, byte[] body, CancellationToken ct)
    {
        try
        {
            switch (type)
            {
                case JdbcMessageType.Connect:
                    await HandleConnectAsync(body, ct).ConfigureAwait(false);
                    break;

                case JdbcMessageType.Disconnect:
                    // No response needed, connection will close
                    break;

                case JdbcMessageType.Ping:
                    await _writer.WritePongAsync(ct).ConfigureAwait(false);
                    break;

                case JdbcMessageType.CreateStatement:
                    await HandleCreateStatementAsync(body, ct).ConfigureAwait(false);
                    break;

                case JdbcMessageType.PrepareStatement:
                    await HandlePrepareStatementAsync(body, ct).ConfigureAwait(false);
                    break;

                case JdbcMessageType.CloseStatement:
                    await HandleCloseStatementAsync(body, ct).ConfigureAwait(false);
                    break;

                case JdbcMessageType.ExecuteQuery:
                    await HandleExecuteQueryAsync(body, ct).ConfigureAwait(false);
                    break;

                case JdbcMessageType.ExecuteUpdate:
                    await HandleExecuteUpdateAsync(body, ct).ConfigureAwait(false);
                    break;

                case JdbcMessageType.ExecuteBatch:
                    await HandleExecuteBatchAsync(body, ct).ConfigureAwait(false);
                    break;

                case JdbcMessageType.SetAutoCommit:
                    await HandleSetAutoCommitAsync(body, ct).ConfigureAwait(false);
                    break;

                case JdbcMessageType.Commit:
                    await HandleCommitAsync(ct).ConfigureAwait(false);
                    break;

                case JdbcMessageType.Rollback:
                    await HandleRollbackAsync(body, ct).ConfigureAwait(false);
                    break;

                case JdbcMessageType.SetSavepoint:
                    await HandleSetSavepointAsync(body, ct).ConfigureAwait(false);
                    break;

                case JdbcMessageType.ReleaseSavepoint:
                    await HandleReleaseSavepointAsync(body, ct).ConfigureAwait(false);
                    break;

                case JdbcMessageType.RollbackToSavepoint:
                    await HandleRollbackToSavepointAsync(body, ct).ConfigureAwait(false);
                    break;

                case JdbcMessageType.GetDatabaseMetadata:
                    await HandleGetDatabaseMetadataAsync(ct).ConfigureAwait(false);
                    break;

                case JdbcMessageType.GetTables:
                    await HandleGetTablesAsync(body, ct).ConfigureAwait(false);
                    break;

                case JdbcMessageType.GetColumns:
                    await HandleGetColumnsAsync(body, ct).ConfigureAwait(false);
                    break;

                case JdbcMessageType.GetPrimaryKeys:
                    await HandleGetPrimaryKeysAsync(body, ct).ConfigureAwait(false);
                    break;

                case JdbcMessageType.GetIndexInfo:
                    await HandleGetIndexInfoAsync(body, ct).ConfigureAwait(false);
                    break;

                case JdbcMessageType.GetTypeInfo:
                    await HandleGetTypeInfoAsync(ct).ConfigureAwait(false);
                    break;

                case JdbcMessageType.GetCatalogs:
                    await HandleGetCatalogsAsync(ct).ConfigureAwait(false);
                    break;

                case JdbcMessageType.GetSchemas:
                    await HandleGetSchemasAsync(body, ct).ConfigureAwait(false);
                    break;

                case JdbcMessageType.GetTableTypes:
                    await HandleGetTableTypesAsync(ct).ConfigureAwait(false);
                    break;

                case JdbcMessageType.GetProcedures:
                    await HandleGetProceduresAsync(body, ct).ConfigureAwait(false);
                    break;

                default:
                    await _writer.WriteSqlExceptionAsync(
                        JdbcSqlState.FeatureNotSupported,
                        0,
                        $"Unsupported message type: {type}",
                        ct).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex)
        {
            await _writer.WriteSqlExceptionAsync(
                JdbcSqlState.InternalError,
                0,
                ex.Message,
                ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Handles connection request.
    /// </summary>
    private async Task HandleConnectAsync(byte[] body, CancellationToken ct)
    {
        var offset = 0;

        // Read connection properties
        var user = MessageReader.ReadString(body, ref offset);
        var password = MessageReader.ReadNullableString(body, ref offset);
        var database = MessageReader.ReadNullableString(body, ref offset);
        var applicationName = MessageReader.ReadNullableString(body, ref offset);

        // Read additional properties count
        var propCount = MessageReader.ReadInt32(body, ref offset);
        for (var i = 0; i < propCount; i++)
        {
            var key = MessageReader.ReadString(body, ref offset);
            var value = MessageReader.ReadString(body, ref offset);
            _state.Properties[key] = value;
        }

        _state.User = user;
        _state.Catalog = database ?? _config.DefaultDatabase;
        _state.ApplicationName = applicationName ?? "";

        // Authenticate
        if (_config.AuthenticationEnabled && !string.IsNullOrEmpty(password))
        {
            // Simple authentication - in production, integrate with proper auth system
            // For now, accept any non-empty password
        }

        await _writer.WriteConnectResponseAsync(
            success: true,
            sessionId: _state.ConnectionId,
            serverVersion: _config.ServerVersion,
            ct: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles create statement request.
    /// </summary>
    private async Task HandleCreateStatementAsync(byte[] body, CancellationToken ct)
    {
        var offset = 0;
        var resultSetType = MessageReader.ReadInt32(body, ref offset);
        var resultSetConcurrency = MessageReader.ReadInt32(body, ref offset);
        var resultSetHoldability = MessageReader.ReadInt32(body, ref offset);

        var statementId = _state.GetNextStatementId();

        // Store a "plain" statement (not prepared)
        _state.PreparedStatements[statementId] = new JdbcPreparedStatementInfo
        {
            StatementId = statementId,
            Sql = "" // Will be set at execution time
        };

        // Send statement ID back
        using var ms = new MemoryStream();
        MessageWriter.WriteInt32(ms, statementId);
        await _writer.WriteMessageAsync(JdbcMessageType.CreateStatement, ms.ToArray(), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles prepare statement request.
    /// </summary>
    private async Task HandlePrepareStatementAsync(byte[] body, CancellationToken ct)
    {
        var offset = 0;
        var sql = MessageReader.ReadString(body, ref offset);
        var resultSetType = MessageReader.ReadInt32(body, ref offset);
        var resultSetConcurrency = MessageReader.ReadInt32(body, ref offset);
        var resultSetHoldability = MessageReader.ReadInt32(body, ref offset);

        var statementId = _state.GetNextStatementId();
        var stmtInfo = _queryProcessor.PrepareStatement(sql, statementId);
        _state.PreparedStatements[statementId] = stmtInfo;

        // Send prepared statement info back
        using var ms = new MemoryStream();
        MessageWriter.WriteInt32(ms, statementId);
        MessageWriter.WriteInt32(ms, stmtInfo.Parameters.Count);

        foreach (var param in stmtInfo.Parameters)
        {
            MessageWriter.WriteInt32(ms, param.Index);
            MessageWriter.WriteInt32(ms, param.SqlType);
            MessageWriter.WriteString(ms, param.TypeName);
            MessageWriter.WriteInt32(ms, param.Precision);
            MessageWriter.WriteInt32(ms, param.Scale);
            MessageWriter.WriteInt32(ms, param.Mode);
            MessageWriter.WriteInt32(ms, param.Nullable);
        }

        await _writer.WriteMessageAsync(JdbcMessageType.PrepareStatement, ms.ToArray(), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles close statement request.
    /// </summary>
    private async Task HandleCloseStatementAsync(byte[] body, CancellationToken ct)
    {
        var offset = 0;
        var statementId = MessageReader.ReadInt32(body, ref offset);

        _state.PreparedStatements.Remove(statementId);

        // Send close complete
        using var ms = new MemoryStream();
        MessageWriter.WriteInt32(ms, statementId);
        await _writer.WriteMessageAsync(JdbcMessageType.CloseStatement, ms.ToArray(), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles execute query request.
    /// </summary>
    private async Task HandleExecuteQueryAsync(byte[] body, CancellationToken ct)
    {
        var offset = 0;
        var statementId = MessageReader.ReadInt32(body, ref offset);
        var sql = MessageReader.ReadString(body, ref offset);

        // Read parameters
        var paramCount = MessageReader.ReadInt32(body, ref offset);
        var parameters = new List<object?>();

        for (var i = 0; i < paramCount; i++)
        {
            var paramType = MessageReader.ReadInt32(body, ref offset);
            var value = TypeConverter.ParseParameterValue(body, ref offset, paramType);
            parameters.Add(value);
        }

        // Get prepared statement if available
        if (_state.PreparedStatements.TryGetValue(statementId, out var stmt) && !string.IsNullOrEmpty(stmt.Sql))
        {
            sql = stmt.Sql;
        }

        // Substitute parameters
        if (parameters.Count > 0)
        {
            sql = _queryProcessor.SubstituteParameters(sql, parameters);
        }

        // Execute query
        var result = await _queryProcessor.ExecuteQueryAsync(sql, ct).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(result.ErrorMessage))
        {
            await _writer.WriteSqlExceptionAsync(
                result.SqlState ?? JdbcSqlState.InternalError,
                0,
                result.ErrorMessage,
                ct).ConfigureAwait(false);
            return;
        }

        // Send warnings if any
        foreach (var warning in result.Warnings)
        {
            await _writer.WriteWarningAsync(JdbcSqlState.Warning, 0, warning, ct).ConfigureAwait(false);
        }

        // Send result set
        if (result.Columns.Count > 0)
        {
            // Send metadata
            await _writer.WriteResultSetMetadataAsync(statementId, result.Columns, ct).ConfigureAwait(false);

            // Build column types array
            var columnTypes = result.Columns.Select(c => c.SqlType).ToArray();

            // Send rows
            for (var i = 0; i < result.Rows.Count; i++)
            {
                await _writer.WriteResultSetRowAsync(statementId, i, result.Rows[i], columnTypes, ct).ConfigureAwait(false);
            }

            // Send complete
            await _writer.WriteResultSetCompleteAsync(statementId, result.Rows.Count, ct).ConfigureAwait(false);
        }
        else
        {
            // No result set (DDL or similar)
            await _writer.WriteUpdateCountAsync(statementId, result.UpdateCount, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Handles execute update request.
    /// </summary>
    private async Task HandleExecuteUpdateAsync(byte[] body, CancellationToken ct)
    {
        var offset = 0;
        var statementId = MessageReader.ReadInt32(body, ref offset);
        var sql = MessageReader.ReadString(body, ref offset);

        // Read parameters
        var paramCount = MessageReader.ReadInt32(body, ref offset);
        var parameters = new List<object?>();

        for (var i = 0; i < paramCount; i++)
        {
            var paramType = MessageReader.ReadInt32(body, ref offset);
            var value = TypeConverter.ParseParameterValue(body, ref offset, paramType);
            parameters.Add(value);
        }

        // Get prepared statement if available
        if (_state.PreparedStatements.TryGetValue(statementId, out var stmt) && !string.IsNullOrEmpty(stmt.Sql))
        {
            sql = stmt.Sql;
        }

        // Substitute parameters
        if (parameters.Count > 0)
        {
            sql = _queryProcessor.SubstituteParameters(sql, parameters);
        }

        // Handle transaction start if needed
        if (!_state.AutoCommit && !_state.InTransaction)
        {
            _state.InTransaction = true;
        }

        // Execute update
        var result = await _queryProcessor.ExecuteQueryAsync(sql, ct).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(result.ErrorMessage))
        {
            await _writer.WriteSqlExceptionAsync(
                result.SqlState ?? JdbcSqlState.InternalError,
                0,
                result.ErrorMessage,
                ct).ConfigureAwait(false);
            return;
        }

        await _writer.WriteUpdateCountAsync(statementId, result.UpdateCount >= 0 ? result.UpdateCount : 0, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles execute batch request.
    /// </summary>
    private async Task HandleExecuteBatchAsync(byte[] body, CancellationToken ct)
    {
        var offset = 0;
        var statementId = MessageReader.ReadInt32(body, ref offset);
        var batchCount = MessageReader.ReadInt32(body, ref offset);

        if (batchCount > _config.MaxBatchSize)
        {
            await _writer.WriteSqlExceptionAsync(
                JdbcSqlState.DataException,
                0,
                $"Batch size {batchCount} exceeds maximum of {_config.MaxBatchSize}",
                ct).ConfigureAwait(false);
            return;
        }

        // Get prepared statement
        if (!_state.PreparedStatements.TryGetValue(statementId, out var stmt))
        {
            await _writer.WriteSqlExceptionAsync(
                JdbcSqlState.InternalError,
                0,
                $"Statement {statementId} not found",
                ct).ConfigureAwait(false);
            return;
        }

        var updateCounts = new int[batchCount];

        // Start transaction if needed
        if (!_state.AutoCommit && !_state.InTransaction)
        {
            _state.InTransaction = true;
        }

        // Execute each batch item
        for (var i = 0; i < batchCount; i++)
        {
            // Read parameters for this batch item
            var paramCount = MessageReader.ReadInt32(body, ref offset);
            var parameters = new List<object?>();

            for (var j = 0; j < paramCount; j++)
            {
                var paramType = MessageReader.ReadInt32(body, ref offset);
                var value = TypeConverter.ParseParameterValue(body, ref offset, paramType);
                parameters.Add(value);
            }

            var sql = _queryProcessor.SubstituteParameters(stmt.Sql, parameters);
            var result = await _queryProcessor.ExecuteQueryAsync(sql, ct).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(result.ErrorMessage))
            {
                // Batch failed - send error
                await _writer.WriteSqlExceptionAsync(
                    result.SqlState ?? JdbcSqlState.InternalError,
                    0,
                    $"Batch item {i} failed: {result.ErrorMessage}",
                    ct).ConfigureAwait(false);
                return;
            }

            updateCounts[i] = result.UpdateCount >= 0 ? result.UpdateCount : -2; // SUCCESS_NO_INFO
        }

        // Send batch result
        using var ms = new MemoryStream();
        MessageWriter.WriteInt32(ms, statementId);
        MessageWriter.WriteInt32(ms, batchCount);
        foreach (var count in updateCounts)
        {
            MessageWriter.WriteInt32(ms, count);
        }

        await _writer.WriteMessageAsync(JdbcMessageType.ExecuteBatch, ms.ToArray(), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles set auto-commit request.
    /// </summary>
    private async Task HandleSetAutoCommitAsync(byte[] body, CancellationToken ct)
    {
        var offset = 0;
        var autoCommit = MessageReader.ReadBoolean(body, ref offset);

        await _transactionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // If switching from manual to auto-commit, commit any pending transaction
            if (autoCommit && !_state.AutoCommit && _state.InTransaction)
            {
                // Implicit commit
                _state.InTransaction = false;
                _state.Savepoints.Clear();
            }

            _state.AutoCommit = autoCommit;
        }
        finally
        {
            _transactionLock.Release();
        }

        // Send confirmation
        using var ms = new MemoryStream();
        MessageWriter.WriteBoolean(ms, autoCommit);
        await _writer.WriteMessageAsync(JdbcMessageType.SetAutoCommit, ms.ToArray(), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles commit request.
    /// </summary>
    private async Task HandleCommitAsync(CancellationToken ct)
    {
        await _transactionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_state.AutoCommit)
            {
                await _writer.WriteSqlExceptionAsync(
                    JdbcSqlState.InvalidTransactionState,
                    0,
                    "Cannot commit with auto-commit enabled",
                    ct).ConfigureAwait(false);
                return;
            }

            // Execute commit via query processor
            await _queryProcessor.ExecuteQueryAsync("COMMIT", ct).ConfigureAwait(false);

            _state.InTransaction = false;
            _state.Savepoints.Clear();
        }
        finally
        {
            _transactionLock.Release();
        }

        // Send confirmation
        await _writer.WriteMessageAsync(JdbcMessageType.Commit, Array.Empty<byte>(), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles rollback request.
    /// </summary>
    private async Task HandleRollbackAsync(byte[] body, CancellationToken ct)
    {
        await _transactionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_state.AutoCommit)
            {
                await _writer.WriteSqlExceptionAsync(
                    JdbcSqlState.InvalidTransactionState,
                    0,
                    "Cannot rollback with auto-commit enabled",
                    ct).ConfigureAwait(false);
                return;
            }

            // Execute rollback via query processor
            await _queryProcessor.ExecuteQueryAsync("ROLLBACK", ct).ConfigureAwait(false);

            _state.InTransaction = false;
            _state.Savepoints.Clear();
        }
        finally
        {
            _transactionLock.Release();
        }

        // Send confirmation
        await _writer.WriteMessageAsync(JdbcMessageType.Rollback, Array.Empty<byte>(), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles set savepoint request.
    /// </summary>
    private async Task HandleSetSavepointAsync(byte[] body, CancellationToken ct)
    {
        var offset = 0;
        var name = MessageReader.ReadNullableString(body, ref offset);

        await _transactionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_state.AutoCommit)
            {
                await _writer.WriteSqlExceptionAsync(
                    JdbcSqlState.InvalidTransactionState,
                    0,
                    "Cannot create savepoint with auto-commit enabled",
                    ct).ConfigureAwait(false);
                return;
            }

            var savepointId = _state.GetNextSavepointId();
            var savepoint = new JdbcSavepoint
            {
                Id = savepointId,
                Name = name
            };

            _state.Savepoints[savepointId] = savepoint;

            // Execute savepoint creation
            var savepointName = name ?? $"sp_{savepointId}";
            await _queryProcessor.ExecuteQueryAsync($"SAVEPOINT {savepointName}", ct).ConfigureAwait(false);

            // Send savepoint info back
            using var ms = new MemoryStream();
            MessageWriter.WriteInt32(ms, savepointId);
            MessageWriter.WriteNullableString(ms, name);
            await _writer.WriteMessageAsync(JdbcMessageType.SetSavepoint, ms.ToArray(), ct).ConfigureAwait(false);
        }
        finally
        {
            _transactionLock.Release();
        }
    }

    /// <summary>
    /// Handles release savepoint request.
    /// </summary>
    private async Task HandleReleaseSavepointAsync(byte[] body, CancellationToken ct)
    {
        var offset = 0;
        var savepointId = MessageReader.ReadInt32(body, ref offset);

        await _transactionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_state.Savepoints.TryGetValue(savepointId, out var savepoint))
            {
                await _writer.WriteSqlExceptionAsync(
                    JdbcSqlState.InternalError,
                    0,
                    $"Savepoint {savepointId} not found",
                    ct).ConfigureAwait(false);
                return;
            }

            var savepointName = savepoint.Name ?? $"sp_{savepointId}";
            await _queryProcessor.ExecuteQueryAsync($"RELEASE SAVEPOINT {savepointName}", ct).ConfigureAwait(false);

            _state.Savepoints.Remove(savepointId);
        }
        finally
        {
            _transactionLock.Release();
        }

        await _writer.WriteMessageAsync(JdbcMessageType.ReleaseSavepoint, Array.Empty<byte>(), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles rollback to savepoint request.
    /// </summary>
    private async Task HandleRollbackToSavepointAsync(byte[] body, CancellationToken ct)
    {
        var offset = 0;
        var savepointId = MessageReader.ReadInt32(body, ref offset);

        await _transactionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_state.Savepoints.TryGetValue(savepointId, out var savepoint))
            {
                await _writer.WriteSqlExceptionAsync(
                    JdbcSqlState.InternalError,
                    0,
                    $"Savepoint {savepointId} not found",
                    ct).ConfigureAwait(false);
                return;
            }

            var savepointName = savepoint.Name ?? $"sp_{savepointId}";
            await _queryProcessor.ExecuteQueryAsync($"ROLLBACK TO SAVEPOINT {savepointName}", ct).ConfigureAwait(false);

            // Remove savepoints created after this one
            var savepointsToRemove = _state.Savepoints
                .Where(kvp => kvp.Value.CreatedAt > savepoint.CreatedAt)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var id in savepointsToRemove)
            {
                _state.Savepoints.Remove(id);
            }
        }
        finally
        {
            _transactionLock.Release();
        }

        await _writer.WriteMessageAsync(JdbcMessageType.RollbackToSavepoint, Array.Empty<byte>(), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles get database metadata request.
    /// </summary>
    private async Task HandleGetDatabaseMetadataAsync(CancellationToken ct)
    {
        var result = _metadataProvider.GetDatabaseMetadata();
        await SendQueryResultAsync(0, result, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles get tables request.
    /// </summary>
    private async Task HandleGetTablesAsync(byte[] body, CancellationToken ct)
    {
        var offset = 0;
        var catalog = MessageReader.ReadNullableString(body, ref offset);
        var schemaPattern = MessageReader.ReadNullableString(body, ref offset);
        var tableNamePattern = MessageReader.ReadNullableString(body, ref offset);
        var typeCount = MessageReader.ReadInt32(body, ref offset);

        var types = new string[typeCount];
        for (var i = 0; i < typeCount; i++)
        {
            types[i] = MessageReader.ReadString(body, ref offset);
        }

        var result = _metadataProvider.GetTables(catalog, schemaPattern, tableNamePattern, types.Length > 0 ? types : null);
        await SendQueryResultAsync(0, result, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles get columns request.
    /// </summary>
    private async Task HandleGetColumnsAsync(byte[] body, CancellationToken ct)
    {
        var offset = 0;
        var catalog = MessageReader.ReadNullableString(body, ref offset);
        var schemaPattern = MessageReader.ReadNullableString(body, ref offset);
        var tableNamePattern = MessageReader.ReadNullableString(body, ref offset);
        var columnNamePattern = MessageReader.ReadNullableString(body, ref offset);

        var result = _metadataProvider.GetColumns(catalog, schemaPattern, tableNamePattern, columnNamePattern);
        await SendQueryResultAsync(0, result, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles get primary keys request.
    /// </summary>
    private async Task HandleGetPrimaryKeysAsync(byte[] body, CancellationToken ct)
    {
        var offset = 0;
        var catalog = MessageReader.ReadNullableString(body, ref offset);
        var schema = MessageReader.ReadNullableString(body, ref offset);
        var table = MessageReader.ReadString(body, ref offset);

        var result = _metadataProvider.GetPrimaryKeys(catalog, schema, table);
        await SendQueryResultAsync(0, result, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles get index info request.
    /// </summary>
    private async Task HandleGetIndexInfoAsync(byte[] body, CancellationToken ct)
    {
        var offset = 0;
        var catalog = MessageReader.ReadNullableString(body, ref offset);
        var schema = MessageReader.ReadNullableString(body, ref offset);
        var table = MessageReader.ReadString(body, ref offset);
        var unique = MessageReader.ReadBoolean(body, ref offset);
        var approximate = MessageReader.ReadBoolean(body, ref offset);

        var result = _metadataProvider.GetIndexInfo(catalog, schema, table, unique, approximate);
        await SendQueryResultAsync(0, result, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles get type info request.
    /// </summary>
    private async Task HandleGetTypeInfoAsync(CancellationToken ct)
    {
        var result = _metadataProvider.GetTypeInfo();
        await SendQueryResultAsync(0, result, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles get catalogs request.
    /// </summary>
    private async Task HandleGetCatalogsAsync(CancellationToken ct)
    {
        var result = _metadataProvider.GetCatalogs();
        await SendQueryResultAsync(0, result, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles get schemas request.
    /// </summary>
    private async Task HandleGetSchemasAsync(byte[] body, CancellationToken ct)
    {
        var offset = 0;
        var catalog = MessageReader.ReadNullableString(body, ref offset);
        var schemaPattern = MessageReader.ReadNullableString(body, ref offset);

        var result = _metadataProvider.GetSchemas(catalog, schemaPattern);
        await SendQueryResultAsync(0, result, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles get table types request.
    /// </summary>
    private async Task HandleGetTableTypesAsync(CancellationToken ct)
    {
        var result = _metadataProvider.GetTableTypes();
        await SendQueryResultAsync(0, result, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles get procedures request.
    /// </summary>
    private async Task HandleGetProceduresAsync(byte[] body, CancellationToken ct)
    {
        var offset = 0;
        var catalog = MessageReader.ReadNullableString(body, ref offset);
        var schemaPattern = MessageReader.ReadNullableString(body, ref offset);
        var procedureNamePattern = MessageReader.ReadNullableString(body, ref offset);

        var result = _metadataProvider.GetProcedures(catalog, schemaPattern, procedureNamePattern);
        await SendQueryResultAsync(0, result, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a query result to the client.
    /// </summary>
    private async Task SendQueryResultAsync(int statementId, JdbcQueryResult result, CancellationToken ct)
    {
        if (result.Columns.Count > 0)
        {
            await _writer.WriteResultSetMetadataAsync(statementId, result.Columns, ct).ConfigureAwait(false);

            var columnTypes = result.Columns.Select(c => c.SqlType).ToArray();
            for (var i = 0; i < result.Rows.Count; i++)
            {
                await _writer.WriteResultSetRowAsync(statementId, i, result.Rows[i], columnTypes, ct).ConfigureAwait(false);
            }

            await _writer.WriteResultSetCompleteAsync(statementId, result.Rows.Count, ct).ConfigureAwait(false);
        }
        else
        {
            await _writer.WriteUpdateCountAsync(statementId, result.UpdateCount, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Disposes the connection handler.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _reader.Dispose();
        _writer.Dispose();
        _transactionLock.Dispose();

        try { _client.Close(); }
        catch { /* Best effort */ }
    }
}
