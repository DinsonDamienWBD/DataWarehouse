using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace DataWarehouse.Plugins.JdbcBridge.Protocol;

/// <summary>
/// Writes JDBC wire protocol messages to a network stream.
/// Uses a length-prefixed binary protocol for efficient network transport.
/// Thread-safe for concurrent writes.
/// </summary>
public sealed class MessageWriter : IDisposable
{
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the MessageWriter class.
    /// </summary>
    /// <param name="stream">The network stream to write to.</param>
    /// <exception cref="ArgumentNullException">Thrown when stream is null.</exception>
    public MessageWriter(NetworkStream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }

    /// <summary>
    /// Writes a message to the stream.
    /// </summary>
    /// <param name="type">The message type.</param>
    /// <param name="body">The message body.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task WriteMessageAsync(JdbcMessageType type, byte[] body, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (body.Length > 16 * 1024 * 1024)
        {
            throw new ArgumentException("Message body exceeds maximum size of 16MB", nameof(body));
        }

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Write header: 1 byte type + 4 bytes length (big-endian) + body
            var message = new byte[5 + body.Length];
            message[0] = (byte)type;
            BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(1, 4), body.Length);
            body.CopyTo(message, 5);

            await _stream.WriteAsync(message, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Writes a connect response message.
    /// </summary>
    /// <param name="success">Whether connection was successful.</param>
    /// <param name="sessionId">The session ID.</param>
    /// <param name="serverVersion">The server version.</param>
    /// <param name="errorMessage">Error message if failed.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task WriteConnectResponseAsync(
        bool success,
        string sessionId,
        string serverVersion,
        string? errorMessage = null,
        CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        WriteBoolean(ms, success);
        WriteString(ms, sessionId);
        WriteString(ms, serverVersion);
        WriteNullableString(ms, errorMessage);

        await WriteMessageAsync(JdbcMessageType.ConnectResponse, ms.ToArray(), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes an error response message.
    /// </summary>
    /// <param name="sqlState">The SQL state code.</param>
    /// <param name="errorCode">The vendor error code.</param>
    /// <param name="message">The error message.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task WriteErrorAsync(
        string sqlState,
        int errorCode,
        string message,
        CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        WriteString(ms, sqlState);
        WriteInt32(ms, errorCode);
        WriteString(ms, message);

        await WriteMessageAsync(JdbcMessageType.Error, ms.ToArray(), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes an SQLException response.
    /// </summary>
    /// <param name="sqlState">The SQL state code.</param>
    /// <param name="errorCode">The vendor error code.</param>
    /// <param name="message">The error message.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task WriteSqlExceptionAsync(
        string sqlState,
        int errorCode,
        string message,
        CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        WriteString(ms, sqlState);
        WriteInt32(ms, errorCode);
        WriteString(ms, message);

        await WriteMessageAsync(JdbcMessageType.SQLException, ms.ToArray(), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes a warning message.
    /// </summary>
    /// <param name="sqlState">The SQL state code.</param>
    /// <param name="errorCode">The vendor error code.</param>
    /// <param name="message">The warning message.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task WriteWarningAsync(
        string sqlState,
        int errorCode,
        string message,
        CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        WriteString(ms, sqlState);
        WriteInt32(ms, errorCode);
        WriteString(ms, message);

        await WriteMessageAsync(JdbcMessageType.Warning, ms.ToArray(), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes a pong response to a ping.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task WritePongAsync(CancellationToken ct = default)
    {
        await WriteMessageAsync(JdbcMessageType.Pong, Array.Empty<byte>(), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes result set metadata.
    /// </summary>
    /// <param name="statementId">The statement ID.</param>
    /// <param name="columns">The column metadata.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task WriteResultSetMetadataAsync(
        int statementId,
        IList<JdbcColumnMetadata> columns,
        CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        WriteInt32(ms, statementId);
        WriteInt32(ms, columns.Count);

        foreach (var col in columns)
        {
            WriteString(ms, col.Label);
            WriteString(ms, col.Name);
            WriteString(ms, col.SchemaName);
            WriteString(ms, col.TableName);
            WriteString(ms, col.CatalogName);
            WriteInt32(ms, col.SqlType);
            WriteString(ms, col.TypeName);
            WriteInt32(ms, col.Precision);
            WriteInt32(ms, col.Scale);
            WriteInt32(ms, col.DisplaySize);
            WriteInt32(ms, col.Nullable);
            WriteBoolean(ms, col.AutoIncrement);
            WriteBoolean(ms, col.CaseSensitive);
            WriteBoolean(ms, col.Searchable);
            WriteBoolean(ms, col.Currency);
            WriteBoolean(ms, col.Signed);
            WriteBoolean(ms, col.ReadOnly);
            WriteBoolean(ms, col.Writable);
            WriteBoolean(ms, col.DefinitelyWritable);
            WriteString(ms, col.ClassName);
        }

        await WriteMessageAsync(JdbcMessageType.ResultSetMetadata, ms.ToArray(), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes a data row.
    /// </summary>
    /// <param name="statementId">The statement ID.</param>
    /// <param name="rowIndex">The row index (0-based).</param>
    /// <param name="values">The column values.</param>
    /// <param name="columnTypes">The SQL types for each column.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task WriteResultSetRowAsync(
        int statementId,
        int rowIndex,
        object?[] values,
        int[] columnTypes,
        CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        WriteInt32(ms, statementId);
        WriteInt32(ms, rowIndex);
        WriteInt32(ms, values.Length);

        for (var i = 0; i < values.Length; i++)
        {
            WriteValue(ms, values[i], columnTypes[i]);
        }

        await WriteMessageAsync(JdbcMessageType.ResultSetRow, ms.ToArray(), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes result set completion.
    /// </summary>
    /// <param name="statementId">The statement ID.</param>
    /// <param name="rowCount">The total row count.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task WriteResultSetCompleteAsync(
        int statementId,
        int rowCount,
        CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        WriteInt32(ms, statementId);
        WriteInt32(ms, rowCount);

        await WriteMessageAsync(JdbcMessageType.ResultSetComplete, ms.ToArray(), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes an update count result.
    /// </summary>
    /// <param name="statementId">The statement ID.</param>
    /// <param name="updateCount">The number of rows affected.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task WriteUpdateCountAsync(
        int statementId,
        int updateCount,
        CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        WriteInt32(ms, statementId);
        WriteInt32(ms, updateCount);

        await WriteMessageAsync(JdbcMessageType.UpdateCount, ms.ToArray(), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes a value to the stream based on its SQL type.
    /// </summary>
    /// <param name="ms">The memory stream.</param>
    /// <param name="value">The value to write.</param>
    /// <param name="sqlType">The SQL type.</param>
    private static void WriteValue(MemoryStream ms, object? value, int sqlType)
    {
        // Write null indicator
        if (value == null || value == DBNull.Value)
        {
            ms.WriteByte(1); // is null
            return;
        }

        ms.WriteByte(0); // not null

        switch (sqlType)
        {
            case JdbcSqlTypes.BIT:
            case JdbcSqlTypes.BOOLEAN:
                WriteBoolean(ms, Convert.ToBoolean(value));
                break;

            case JdbcSqlTypes.TINYINT:
                ms.WriteByte(Convert.ToByte(value));
                break;

            case JdbcSqlTypes.SMALLINT:
                WriteInt16(ms, Convert.ToInt16(value));
                break;

            case JdbcSqlTypes.INTEGER:
                WriteInt32(ms, Convert.ToInt32(value));
                break;

            case JdbcSqlTypes.BIGINT:
                WriteInt64(ms, Convert.ToInt64(value));
                break;

            case JdbcSqlTypes.REAL:
            case JdbcSqlTypes.FLOAT:
                WriteFloat(ms, Convert.ToSingle(value));
                break;

            case JdbcSqlTypes.DOUBLE:
                WriteDouble(ms, Convert.ToDouble(value));
                break;

            case JdbcSqlTypes.NUMERIC:
            case JdbcSqlTypes.DECIMAL:
                WriteString(ms, Convert.ToDecimal(value).ToString(System.Globalization.CultureInfo.InvariantCulture));
                break;

            case JdbcSqlTypes.DATE:
                WriteLong(ms, ((DateTime)value).Ticks);
                break;

            case JdbcSqlTypes.TIME:
            case JdbcSqlTypes.TIME_WITH_TIMEZONE:
                WriteLong(ms, ((DateTime)value).TimeOfDay.Ticks);
                break;

            case JdbcSqlTypes.TIMESTAMP:
            case JdbcSqlTypes.TIMESTAMP_WITH_TIMEZONE:
                WriteLong(ms, value is DateTimeOffset dto ? dto.UtcTicks : ((DateTime)value).Ticks);
                break;

            case JdbcSqlTypes.BINARY:
            case JdbcSqlTypes.VARBINARY:
            case JdbcSqlTypes.LONGVARBINARY:
            case JdbcSqlTypes.BLOB:
                WriteBytes(ms, (byte[])value);
                break;

            default:
                // Default to string representation
                WriteString(ms, value.ToString() ?? "");
                break;
        }
    }

    /// <summary>
    /// Writes a string to the stream.
    /// </summary>
    /// <param name="ms">The memory stream.</param>
    /// <param name="value">The string value.</param>
    public static void WriteString(MemoryStream ms, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteInt32(ms, bytes.Length);
        ms.Write(bytes, 0, bytes.Length);
    }

    /// <summary>
    /// Writes a nullable string to the stream.
    /// </summary>
    /// <param name="ms">The memory stream.</param>
    /// <param name="value">The string value or null.</param>
    public static void WriteNullableString(MemoryStream ms, string? value)
    {
        if (value == null)
        {
            WriteInt32(ms, -1);
        }
        else
        {
            WriteString(ms, value);
        }
    }

    /// <summary>
    /// Writes a 32-bit integer to the stream.
    /// </summary>
    /// <param name="ms">The memory stream.</param>
    /// <param name="value">The integer value.</param>
    public static void WriteInt32(MemoryStream ms, int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, value);
        ms.Write(buffer);
    }

    /// <summary>
    /// Writes a 64-bit integer to the stream.
    /// </summary>
    /// <param name="ms">The memory stream.</param>
    /// <param name="value">The long value.</param>
    public static void WriteInt64(MemoryStream ms, long value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(buffer, value);
        ms.Write(buffer);
    }

    /// <summary>
    /// Writes a long to the stream (alias for WriteInt64).
    /// </summary>
    /// <param name="ms">The memory stream.</param>
    /// <param name="value">The long value.</param>
    public static void WriteLong(MemoryStream ms, long value) => WriteInt64(ms, value);

    /// <summary>
    /// Writes a 16-bit integer to the stream.
    /// </summary>
    /// <param name="ms">The memory stream.</param>
    /// <param name="value">The short value.</param>
    public static void WriteInt16(MemoryStream ms, short value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteInt16BigEndian(buffer, value);
        ms.Write(buffer);
    }

    /// <summary>
    /// Writes a boolean to the stream.
    /// </summary>
    /// <param name="ms">The memory stream.</param>
    /// <param name="value">The boolean value.</param>
    public static void WriteBoolean(MemoryStream ms, bool value)
    {
        ms.WriteByte((byte)(value ? 1 : 0));
    }

    /// <summary>
    /// Writes a double to the stream.
    /// </summary>
    /// <param name="ms">The memory stream.</param>
    /// <param name="value">The double value.</param>
    public static void WriteDouble(MemoryStream ms, double value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteDoubleBigEndian(buffer, value);
        ms.Write(buffer);
    }

    /// <summary>
    /// Writes a float to the stream.
    /// </summary>
    /// <param name="ms">The memory stream.</param>
    /// <param name="value">The float value.</param>
    public static void WriteFloat(MemoryStream ms, float value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteSingleBigEndian(buffer, value);
        ms.Write(buffer);
    }

    /// <summary>
    /// Writes a byte array to the stream.
    /// </summary>
    /// <param name="ms">The memory stream.</param>
    /// <param name="value">The byte array.</param>
    public static void WriteBytes(MemoryStream ms, byte[] value)
    {
        WriteInt32(ms, value.Length);
        ms.Write(value, 0, value.Length);
    }

    /// <summary>
    /// Disposes the message writer.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _writeLock.Dispose();
    }
}
