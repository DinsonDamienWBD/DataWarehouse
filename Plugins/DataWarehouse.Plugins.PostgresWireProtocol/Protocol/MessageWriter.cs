using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace DataWarehouse.Plugins.PostgresWireProtocol.Protocol;

/// <summary>
/// Writes PostgreSQL wire protocol messages to a network stream.
/// </summary>
public sealed class MessageWriter
{
    private readonly NetworkStream _stream;

    public MessageWriter(NetworkStream stream)
    {
        _stream = stream;
    }

    /// <summary>
    /// Writes a simple message with just a type and no body.
    /// </summary>
    public async Task WriteMessageAsync(PgMessageType type, CancellationToken ct = default)
    {
        await WriteMessageAsync(type, Array.Empty<byte>(), ct);
    }

    /// <summary>
    /// Writes a message with a type and body.
    /// </summary>
    public async Task WriteMessageAsync(PgMessageType type, byte[] body, CancellationToken ct = default)
    {
        var length = 4 + body.Length;
        var message = new byte[1 + 4 + body.Length];

        message[0] = (byte)type;
        BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(1, 4), length);
        body.CopyTo(message, 5);

        await _stream.WriteAsync(message, ct);
    }

    /// <summary>
    /// Writes an authentication response.
    /// </summary>
    public async Task WriteAuthenticationAsync(int authType, CancellationToken ct = default)
    {
        var body = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(body, authType);
        await WriteMessageAsync(PgMessageType.Authentication, body, ct);
    }

    /// <summary>
    /// Writes an MD5 authentication request.
    /// </summary>
    /// <remarks>
    /// DEPRECATED: MD5 authentication is deprecated due to collision vulnerabilities.
    /// PostgreSQL recommends using SCRAM-SHA-256 authentication instead.
    /// This method is provided for legacy client compatibility only.
    /// </remarks>
    public async Task WriteAuthenticationMD5Async(byte[] salt, CancellationToken ct = default)
    {
        var body = new byte[8];
        BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(0, 4), PgProtocolConstants.AuthMD5Password);
        salt.CopyTo(body, 4);
        await WriteMessageAsync(PgMessageType.Authentication, body, ct);
    }

    /// <summary>
    /// Writes a parameter status message.
    /// </summary>
    public async Task WriteParameterStatusAsync(string name, string value, CancellationToken ct = default)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var valueBytes = Encoding.UTF8.GetBytes(value);
        var body = new byte[nameBytes.Length + 1 + valueBytes.Length + 1];

        nameBytes.CopyTo(body, 0);
        body[nameBytes.Length] = 0;
        valueBytes.CopyTo(body, nameBytes.Length + 1);
        body[body.Length - 1] = 0;

        await WriteMessageAsync(PgMessageType.ParameterStatus, body, ct);
    }

    /// <summary>
    /// Writes backend key data for connection cancellation.
    /// </summary>
    public async Task WriteBackendKeyDataAsync(int processId, int secretKey, CancellationToken ct = default)
    {
        var body = new byte[8];
        BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(0, 4), processId);
        BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(4, 4), secretKey);
        await WriteMessageAsync(PgMessageType.BackendKeyData, body, ct);
    }

    /// <summary>
    /// Writes ready for query message with transaction status.
    /// </summary>
    public async Task WriteReadyForQueryAsync(byte transactionStatus, CancellationToken ct = default)
    {
        var body = new byte[] { transactionStatus };
        await WriteMessageAsync(PgMessageType.ReadyForQuery, body, ct);
    }

    /// <summary>
    /// Writes command complete message.
    /// </summary>
    public async Task WriteCommandCompleteAsync(string tag, CancellationToken ct = default)
    {
        var tagBytes = Encoding.UTF8.GetBytes(tag);
        var body = new byte[tagBytes.Length + 1];
        tagBytes.CopyTo(body, 0);
        body[body.Length - 1] = 0;
        await WriteMessageAsync(PgMessageType.CommandComplete, body, ct);
    }

    /// <summary>
    /// Writes row description (column metadata).
    /// </summary>
    public async Task WriteRowDescriptionAsync(List<PgColumnDescription> columns, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        // Field count
        writer.Write((byte)(columns.Count >> 8));
        writer.Write((byte)columns.Count);

        foreach (var column in columns)
        {
            // Field name
            var nameBytes = Encoding.UTF8.GetBytes(column.Name);
            writer.Write(nameBytes);
            writer.Write((byte)0);

            // Table OID
            writer.Write((byte)(column.TableOid >> 24));
            writer.Write((byte)(column.TableOid >> 16));
            writer.Write((byte)(column.TableOid >> 8));
            writer.Write((byte)column.TableOid);

            // Column attribute number
            writer.Write((byte)(column.ColumnAttributeNumber >> 8));
            writer.Write((byte)column.ColumnAttributeNumber);

            // Type OID
            writer.Write((byte)(column.TypeOid >> 24));
            writer.Write((byte)(column.TypeOid >> 16));
            writer.Write((byte)(column.TypeOid >> 8));
            writer.Write((byte)column.TypeOid);

            // Type size
            writer.Write((byte)(column.TypeSize >> 8));
            writer.Write((byte)column.TypeSize);

            // Type modifier
            writer.Write((byte)(column.TypeModifier >> 24));
            writer.Write((byte)(column.TypeModifier >> 16));
            writer.Write((byte)(column.TypeModifier >> 8));
            writer.Write((byte)column.TypeModifier);

            // Format code
            writer.Write((byte)(column.FormatCode >> 8));
            writer.Write((byte)column.FormatCode);
        }

        await WriteMessageAsync(PgMessageType.RowDescription, ms.ToArray(), ct);
    }

    /// <summary>
    /// Writes a data row.
    /// </summary>
    public async Task WriteDataRowAsync(List<byte[]?> values, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        // Column count
        writer.Write((byte)(values.Count >> 8));
        writer.Write((byte)values.Count);

        foreach (var value in values)
        {
            if (value == null)
            {
                // NULL value (-1 length)
                writer.Write((byte)0xFF);
                writer.Write((byte)0xFF);
                writer.Write((byte)0xFF);
                writer.Write((byte)0xFF);
            }
            else
            {
                // Value length (big endian)
                writer.Write((byte)(value.Length >> 24));
                writer.Write((byte)(value.Length >> 16));
                writer.Write((byte)(value.Length >> 8));
                writer.Write((byte)value.Length);

                // Value data
                writer.Write(value);
            }
        }

        await WriteMessageAsync(PgMessageType.DataRow, ms.ToArray(), ct);
    }

    /// <summary>
    /// Writes an error response.
    /// </summary>
    public async Task WriteErrorResponseAsync(
        string severity,
        string sqlState,
        string message,
        string? detail = null,
        string? hint = null,
        CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        // Severity
        writer.Write((byte)'S');
        writer.Write(Encoding.UTF8.GetBytes(severity));
        writer.Write((byte)0);

        // SQL state
        writer.Write((byte)'C');
        writer.Write(Encoding.UTF8.GetBytes(sqlState));
        writer.Write((byte)0);

        // Message
        writer.Write((byte)'M');
        writer.Write(Encoding.UTF8.GetBytes(message));
        writer.Write((byte)0);

        // Detail (optional)
        if (!string.IsNullOrEmpty(detail))
        {
            writer.Write((byte)'D');
            writer.Write(Encoding.UTF8.GetBytes(detail));
            writer.Write((byte)0);
        }

        // Hint (optional)
        if (!string.IsNullOrEmpty(hint))
        {
            writer.Write((byte)'H');
            writer.Write(Encoding.UTF8.GetBytes(hint));
            writer.Write((byte)0);
        }

        // Terminator
        writer.Write((byte)0);

        await WriteMessageAsync(PgMessageType.ErrorResponse, ms.ToArray(), ct);
    }

    /// <summary>
    /// Writes a notice response.
    /// </summary>
    public async Task WriteNoticeResponseAsync(string severity, string message, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write((byte)'S');
        writer.Write(Encoding.UTF8.GetBytes(severity));
        writer.Write((byte)0);

        writer.Write((byte)'M');
        writer.Write(Encoding.UTF8.GetBytes(message));
        writer.Write((byte)0);

        writer.Write((byte)0);

        await WriteMessageAsync(PgMessageType.NoticeResponse, ms.ToArray(), ct);
    }

    /// <summary>
    /// Writes parse complete message.
    /// </summary>
    public async Task WriteParseCompleteAsync(CancellationToken ct = default)
    {
        await WriteMessageAsync(PgMessageType.ParseComplete, Array.Empty<byte>(), ct);
    }

    /// <summary>
    /// Writes bind complete message.
    /// </summary>
    public async Task WriteBindCompleteAsync(CancellationToken ct = default)
    {
        await WriteMessageAsync(PgMessageType.BindComplete, Array.Empty<byte>(), ct);
    }

    /// <summary>
    /// Writes close complete message.
    /// </summary>
    public async Task WriteCloseCompleteAsync(CancellationToken ct = default)
    {
        await WriteMessageAsync(PgMessageType.CloseComplete, Array.Empty<byte>(), ct);
    }

    /// <summary>
    /// Writes empty query response.
    /// </summary>
    public async Task WriteEmptyQueryResponseAsync(CancellationToken ct = default)
    {
        await WriteMessageAsync(PgMessageType.EmptyQueryResponse, Array.Empty<byte>(), ct);
    }

    /// <summary>
    /// Writes no data message.
    /// </summary>
    public async Task WriteNoDataAsync(CancellationToken ct = default)
    {
        await WriteMessageAsync(PgMessageType.NoData, Array.Empty<byte>(), ct);
    }

    /// <summary>
    /// Writes a single byte (for SSL negotiation response).
    /// </summary>
    public async Task WriteByteAsync(byte value, CancellationToken ct = default)
    {
        await _stream.WriteAsync(new[] { value }, ct);
    }
}
