using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.RegularExpressions;

namespace DataWarehouse.Plugins.AdoNetProvider;

/// <summary>
/// Represents a SQL statement or stored procedure to execute against a DataWarehouse database.
/// Supports parameterized queries, prepared statements, and async execution.
/// </summary>
/// <remarks>
/// This class is not thread-safe. Each thread should use its own command instance.
/// Commands can be reused by changing the CommandText and/or parameters.
/// </remarks>
public sealed class DataWarehouseCommand : DbCommand
{
    private DataWarehouseConnection? _connection;
    private DataWarehouseTransaction? _transaction;
    private readonly DataWarehouseParameterCollection _parameters;
    private string _commandText = string.Empty;
    private int _commandTimeout = 30;
    private CommandType _commandType = CommandType.Text;
    private UpdateRowSource _updatedRowSource = UpdateRowSource.None;
    private bool _designTimeVisible = true;
    private bool _disposed;
    private bool _prepared;

    /// <inheritdoc/>
    [AllowNull]
    public override string CommandText
    {
        get => _commandText;
        set
        {
            _commandText = value ?? string.Empty;
            _prepared = false;
        }
    }

    /// <inheritdoc/>
    public override int CommandTimeout
    {
        get => _commandTimeout;
        set
        {
            if (value < 0)
                throw new ArgumentOutOfRangeException(nameof(value), "CommandTimeout must be non-negative.");
            _commandTimeout = value;
        }
    }

    /// <inheritdoc/>
    public override CommandType CommandType
    {
        get => _commandType;
        set
        {
            if (value != CommandType.Text && value != CommandType.TableDirect)
            {
                // Note: StoredProcedure would require additional implementation
                throw new NotSupportedException($"CommandType {value} is not supported.");
            }
            _commandType = value;
        }
    }

    /// <inheritdoc/>
    public override bool DesignTimeVisible
    {
        get => _designTimeVisible;
        set => _designTimeVisible = value;
    }

    /// <inheritdoc/>
    public override UpdateRowSource UpdatedRowSource
    {
        get => _updatedRowSource;
        set => _updatedRowSource = value;
    }

    /// <inheritdoc/>
    protected override DbConnection? DbConnection
    {
        get => _connection;
        set
        {
            if (value != null && value is not DataWarehouseConnection)
                throw new ArgumentException("Connection must be a DataWarehouseConnection.", nameof(value));
            _connection = value as DataWarehouseConnection;
        }
    }

    /// <summary>
    /// Gets or sets the connection associated with this command.
    /// </summary>
    public new DataWarehouseConnection? Connection
    {
        get => _connection;
        set => _connection = value;
    }

    /// <inheritdoc/>
    protected override DbTransaction? DbTransaction
    {
        get => _transaction;
        set
        {
            if (value != null && value is not DataWarehouseTransaction)
                throw new ArgumentException("Transaction must be a DataWarehouseTransaction.", nameof(value));
            _transaction = value as DataWarehouseTransaction;
        }
    }

    /// <summary>
    /// Gets or sets the transaction associated with this command.
    /// </summary>
    public new DataWarehouseTransaction? Transaction
    {
        get => _transaction;
        set => _transaction = value;
    }

    /// <inheritdoc/>
    protected override DbParameterCollection DbParameterCollection => _parameters;

    /// <summary>
    /// Gets the collection of DataWarehouseParameter objects.
    /// </summary>
    public new DataWarehouseParameterCollection Parameters => _parameters;

    /// <summary>
    /// Initializes a new instance of the DataWarehouseCommand class.
    /// </summary>
    public DataWarehouseCommand()
    {
        _parameters = new DataWarehouseParameterCollection();
    }

    /// <summary>
    /// Initializes a new instance of the DataWarehouseCommand class with command text.
    /// </summary>
    /// <param name="commandText">The command text to execute.</param>
    public DataWarehouseCommand(string commandText)
        : this()
    {
        CommandText = commandText;
    }

    /// <summary>
    /// Initializes a new instance of the DataWarehouseCommand class with command text and connection.
    /// </summary>
    /// <param name="commandText">The command text to execute.</param>
    /// <param name="connection">The connection to use.</param>
    public DataWarehouseCommand(string commandText, DataWarehouseConnection connection)
        : this(commandText)
    {
        Connection = connection;
    }

    /// <summary>
    /// Initializes a new instance of the DataWarehouseCommand class with command text, connection, and transaction.
    /// </summary>
    /// <param name="commandText">The command text to execute.</param>
    /// <param name="connection">The connection to use.</param>
    /// <param name="transaction">The transaction to use.</param>
    public DataWarehouseCommand(string commandText, DataWarehouseConnection connection, DataWarehouseTransaction transaction)
        : this(commandText, connection)
    {
        Transaction = transaction;
    }

    /// <inheritdoc/>
    public override void Cancel()
    {
        // Send cancel request to server
        // Note: Full implementation would send a CancelRequest message
    }

    /// <inheritdoc/>
    public override int ExecuteNonQuery()
    {
        return ExecuteNonQueryAsync().GetAwaiter().GetResult();
    }

    /// <inheritdoc/>
    public override async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ValidateCommand();

        using var reader = await ExecuteReaderInternalAsync(CommandBehavior.Default, cancellationToken);
        return reader.RecordsAffected;
    }

    /// <inheritdoc/>
    public override object? ExecuteScalar()
    {
        return ExecuteScalarAsync().GetAwaiter().GetResult();
    }

    /// <inheritdoc/>
    public override async Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ValidateCommand();

        await using var reader = await ExecuteReaderInternalAsync(CommandBehavior.SingleResult | CommandBehavior.SingleRow, cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return reader.IsDBNull(0) ? null : reader.GetValue(0);
        }
        return null;
    }

    /// <inheritdoc/>
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
        return ExecuteReaderAsync(behavior).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Executes the command and returns a data reader.
    /// </summary>
    /// <returns>A data reader with the results.</returns>
    public new DataWarehouseDataReader ExecuteReader()
    {
        return ExecuteReader(CommandBehavior.Default);
    }

    /// <summary>
    /// Executes the command and returns a data reader with specified behavior.
    /// </summary>
    /// <param name="behavior">The command behavior.</param>
    /// <returns>A data reader with the results.</returns>
    public new DataWarehouseDataReader ExecuteReader(CommandBehavior behavior)
    {
        return ExecuteReaderAsync(behavior).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Asynchronously executes the command and returns a data reader.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that returns a data reader with the results.</returns>
    public new async Task<DataWarehouseDataReader> ExecuteReaderAsync(CancellationToken cancellationToken = default)
    {
        return await ExecuteReaderAsync(CommandBehavior.Default, cancellationToken);
    }

    /// <summary>
    /// Asynchronously executes the command and returns a data reader with specified behavior.
    /// </summary>
    /// <param name="behavior">The command behavior.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that returns a data reader with the results.</returns>
    public new async Task<DataWarehouseDataReader> ExecuteReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateCommand();

        return await ExecuteReaderInternalAsync(behavior, cancellationToken);
    }

    /// <inheritdoc/>
    protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
    {
        return await ExecuteReaderAsync(behavior, cancellationToken);
    }

    /// <inheritdoc/>
    public override void Prepare()
    {
        PrepareAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Asynchronously prepares the command for execution.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    public override async Task PrepareAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateCommand();

        if (_prepared)
            return;

        // Send Parse message to server
        var stream = _connection!.GetPooledConnection()!.Stream;

        var statementName = $"stmt_{Guid.NewGuid():N}";
        var query = BuildFinalQuery();

        var parseMessage = BuildParseMessage(statementName, query);
        await stream.WriteAsync(parseMessage, cancellationToken);

        // Send Sync and wait for ParseComplete
        var syncMessage = new byte[] { (byte)'S', 0, 0, 0, 4 };
        await stream.WriteAsync(syncMessage, cancellationToken);

        // Read response
        await ReadPrepareResponseAsync(stream, cancellationToken);

        _prepared = true;
    }

    /// <inheritdoc/>
    protected override DbParameter CreateDbParameter()
    {
        return new DataWarehouseParameter();
    }

    /// <summary>
    /// Creates a new DataWarehouseParameter.
    /// </summary>
    /// <returns>A new parameter.</returns>
    public new DataWarehouseParameter CreateParameter()
    {
        return new DataWarehouseParameter();
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing)
        {
            _parameters.Clear();
        }

        _disposed = true;
        base.Dispose(disposing);
    }

    private async Task<DataWarehouseDataReader> ExecuteReaderInternalAsync(CommandBehavior behavior, CancellationToken cancellationToken)
    {
        var reader = new DataWarehouseDataReader(this, behavior);

        var pooledConnection = _connection!.GetPooledConnection();
        if (pooledConnection == null)
        {
            throw new DataWarehouseConnectionException("Connection is not open.");
        }

        var stream = pooledConnection.Stream;
        var query = BuildFinalQuery();

        // Send simple query
        var queryBytes = Encoding.UTF8.GetBytes(query + '\0');
        var message = new byte[1 + 4 + queryBytes.Length];
        message[0] = (byte)'Q';
        BitConverter.GetBytes(queryBytes.Length + 4).Reverse().ToArray().CopyTo(message, 1);
        queryBytes.CopyTo(message, 5);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_commandTimeout > 0)
        {
            cts.CancelAfter(TimeSpan.FromSeconds(_commandTimeout));
        }

        try
        {
            await stream.WriteAsync(message, cts.Token);
            await ReadQueryResponseAsync(stream, reader, cts.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new DataWarehouseCommandException(
                DataWarehouseProviderException.ProviderErrorCode.OperationCancelled,
                "Command execution was cancelled.",
                _commandText);
        }
        catch (OperationCanceledException)
        {
            throw new DataWarehouseCommandException(
                DataWarehouseProviderException.ProviderErrorCode.CommandTimeout,
                $"Command timed out after {_commandTimeout} seconds.",
                _commandText);
        }

        return reader;
    }

    private async Task ReadQueryResponseAsync(System.Net.Sockets.NetworkStream stream, DataWarehouseDataReader reader, CancellationToken cancellationToken)
    {
        var columns = new List<ColumnInfo>();
        var rows = new List<object[]>();
        var recordsAffected = -1;

        var buffer = new byte[1];
        while (await stream.ReadAsync(buffer, cancellationToken) > 0)
        {
            var messageType = (char)buffer[0];

            var lengthBytes = new byte[4];
            await ReadExactAsync(stream, lengthBytes, cancellationToken);
            var length = BitConverter.ToInt32(lengthBytes.Reverse().ToArray(), 0) - 4;

            var body = length > 0 ? new byte[length] : Array.Empty<byte>();
            if (length > 0)
            {
                await ReadExactAsync(stream, body, cancellationToken);
            }

            switch (messageType)
            {
                case 'T': // RowDescription
                    columns = ParseRowDescription(body);
                    break;

                case 'D': // DataRow
                    rows.Add(ParseDataRow(body, columns));
                    break;

                case 'C': // CommandComplete
                    recordsAffected = ParseCommandComplete(body);
                    break;

                case 'E': // ErrorResponse
                    var error = ParseErrorResponse(body);
                    throw new DataWarehouseCommandException(
                        DataWarehouseProviderException.ProviderErrorCode.CommandFailed,
                        error,
                        _commandText);

                case 'Z': // ReadyForQuery
                    if (columns.Count > 0 || rows.Count > 0)
                    {
                        reader.AddResultSet(columns.ToArray(), rows, recordsAffected);
                    }
                    else if (recordsAffected >= 0)
                    {
                        reader.AddResultSet(Array.Empty<ColumnInfo>(), new List<object[]>(), recordsAffected);
                    }
                    return;

                case 'I': // EmptyQueryResponse
                    break;

                case 'N': // NoticeResponse
                    // Ignore notices
                    break;

                default:
                    // Unknown message, skip
                    break;
            }
        }
    }

    private static List<ColumnInfo> ParseRowDescription(byte[] body)
    {
        var columns = new List<ColumnInfo>();
        var offset = 0;

        var fieldCount = ReadInt16BE(body, ref offset);
        for (int i = 0; i < fieldCount; i++)
        {
            var name = ReadString(body, ref offset);
            var tableOid = ReadInt32BE(body, ref offset);
            var columnAttr = ReadInt16BE(body, ref offset);
            var typeOid = ReadInt32BE(body, ref offset);
            var typeSize = ReadInt16BE(body, ref offset);
            var typeModifier = ReadInt32BE(body, ref offset);
            var formatCode = ReadInt16BE(body, ref offset);

            columns.Add(ColumnInfo.FromTypeOid(name, typeOid, typeModifier));
        }

        return columns;
    }

    private static object[] ParseDataRow(byte[] body, List<ColumnInfo> columns)
    {
        var offset = 0;
        var columnCount = ReadInt16BE(body, ref offset);
        var values = new object[columnCount];

        for (int i = 0; i < columnCount; i++)
        {
            var length = ReadInt32BE(body, ref offset);
            if (length == -1)
            {
                values[i] = DBNull.Value;
            }
            else
            {
                var data = body.AsSpan(offset, length).ToArray();
                offset += length;

                // Parse based on column type
                var type = i < columns.Count ? columns[i].DataType : typeof(string);
                values[i] = ParseValue(data, type);
            }
        }

        return values;
    }

    private static object ParseValue(byte[] data, Type targetType)
    {
        var text = Encoding.UTF8.GetString(data);

        if (targetType == typeof(string))
            return text;
        if (targetType == typeof(int) && int.TryParse(text, out var intVal))
            return intVal;
        if (targetType == typeof(long) && long.TryParse(text, out var longVal))
            return longVal;
        if (targetType == typeof(short) && short.TryParse(text, out var shortVal))
            return shortVal;
        if (targetType == typeof(bool))
            return text == "t" || text == "true" || text == "1";
        if (targetType == typeof(double) && double.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var doubleVal))
            return doubleVal;
        if (targetType == typeof(float) && float.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var floatVal))
            return floatVal;
        if (targetType == typeof(decimal) && decimal.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var decVal))
            return decVal;
        if (targetType == typeof(DateTime) && DateTime.TryParse(text, out var dtVal))
            return dtVal;
        if (targetType == typeof(DateTimeOffset) && DateTimeOffset.TryParse(text, out var dtoVal))
            return dtoVal;
        if (targetType == typeof(Guid) && Guid.TryParse(text, out var guidVal))
            return guidVal;
        if (targetType == typeof(byte[]))
            return data;

        return text;
    }

    private static int ParseCommandComplete(byte[] body)
    {
        var text = Encoding.UTF8.GetString(body).TrimEnd('\0');
        // Format: "INSERT 0 1", "UPDATE 5", "DELETE 3", "SELECT 10"
        var parts = text.Split(' ');
        if (parts.Length >= 2 && int.TryParse(parts[^1], out var count))
            return count;
        return -1;
    }

    private static string ParseErrorResponse(byte[] body)
    {
        var message = string.Empty;
        var i = 0;
        while (i < body.Length && body[i] != 0)
        {
            var fieldType = (char)body[i++];
            var end = Array.IndexOf(body, (byte)0, i);
            if (end < 0) end = body.Length;
            var value = Encoding.UTF8.GetString(body, i, end - i);
            i = end + 1;

            if (fieldType == 'M')
                message = value;
        }
        return message;
    }

    private async Task ReadPrepareResponseAsync(System.Net.Sockets.NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[1];
        while (await stream.ReadAsync(buffer, cancellationToken) > 0)
        {
            var messageType = (char)buffer[0];

            var lengthBytes = new byte[4];
            await ReadExactAsync(stream, lengthBytes, cancellationToken);
            var length = BitConverter.ToInt32(lengthBytes.Reverse().ToArray(), 0) - 4;

            if (length > 0)
            {
                var body = new byte[length];
                await ReadExactAsync(stream, body, cancellationToken);

                if (messageType == 'E')
                {
                    var error = ParseErrorResponse(body);
                    throw new DataWarehouseCommandException(
                        DataWarehouseProviderException.ProviderErrorCode.InvalidSyntax,
                        $"Prepare failed: {error}",
                        _commandText);
                }
            }

            if (messageType == 'Z') // ReadyForQuery
                return;
            if (messageType == '1') // ParseComplete
                continue;
        }
    }

    private static async Task ReadExactAsync(System.Net.Sockets.NetworkStream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken);
            if (read == 0)
                throw new DataWarehouseConnectionException("Connection closed unexpectedly.");
            offset += read;
        }
    }

    private string BuildFinalQuery()
    {
        if (_parameters.Count == 0)
            return _commandText;

        // Replace parameters in query
        var query = _commandText;

        // Handle both @param and :param styles
        foreach (DataWarehouseParameter param in _parameters)
        {
            var patterns = new[]
            {
                $"@{param.ParameterName}",
                $":{param.ParameterName}",
                $"${_parameters.IndexOf(param) + 1}"
            };

            foreach (var pattern in patterns)
            {
                query = Regex.Replace(
                    query,
                    Regex.Escape(pattern) + @"(?![a-zA-Z0-9_])",
                    param.GetFormattedValue(),
                    RegexOptions.IgnoreCase);
            }
        }

        return query;
    }

    private static byte[] BuildParseMessage(string statementName, string query)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        // Statement name
        writer.Write(Encoding.UTF8.GetBytes(statementName));
        writer.Write((byte)0);

        // Query
        writer.Write(Encoding.UTF8.GetBytes(query));
        writer.Write((byte)0);

        // Parameter count
        writer.Write((short)0);

        var body = ms.ToArray();
        var message = new byte[1 + 4 + body.Length];
        message[0] = (byte)'P';
        BitConverter.GetBytes(body.Length + 4).Reverse().ToArray().CopyTo(message, 1);
        body.CopyTo(message, 5);

        return message;
    }

    private static short ReadInt16BE(byte[] buffer, ref int offset)
    {
        var value = (short)((buffer[offset] << 8) | buffer[offset + 1]);
        offset += 2;
        return value;
    }

    private static int ReadInt32BE(byte[] buffer, ref int offset)
    {
        var value = (buffer[offset] << 24) | (buffer[offset + 1] << 16) | (buffer[offset + 2] << 8) | buffer[offset + 3];
        offset += 4;
        return value;
    }

    private static string ReadString(byte[] buffer, ref int offset)
    {
        var end = Array.IndexOf(buffer, (byte)0, offset);
        if (end < 0) end = buffer.Length;
        var value = Encoding.UTF8.GetString(buffer, offset, end - offset);
        offset = end + 1;
        return value;
    }

    private void ValidateCommand()
    {
        if (_connection == null)
        {
            throw new InvalidOperationException("Connection property must be set.");
        }

        if (_connection.State != ConnectionState.Open)
        {
            throw new InvalidOperationException("Connection must be open.");
        }

        if (string.IsNullOrWhiteSpace(_commandText))
        {
            throw new InvalidOperationException("CommandText property must be set.");
        }

        if (_transaction != null && _transaction.IsCompleted)
        {
            throw new InvalidOperationException("Transaction has already been committed or rolled back.");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
