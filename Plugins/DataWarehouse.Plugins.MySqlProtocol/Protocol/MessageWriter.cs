using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace DataWarehouse.Plugins.MySqlProtocol.Protocol;

/// <summary>
/// Writes MySQL wire protocol packets to a network stream.
/// Handles packet framing, authentication packets, and result sets.
/// Thread-safe for single connection use.
/// </summary>
public sealed class MessageWriter
{
    private readonly Stream _stream;
    private readonly object _writeLock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="MessageWriter"/> class.
    /// </summary>
    /// <param name="stream">The network stream to write to.</param>
    /// <exception cref="ArgumentNullException">Thrown when stream is null.</exception>
    public MessageWriter(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }

    /// <summary>
    /// Writes a raw packet to the stream.
    /// </summary>
    /// <param name="payload">Packet payload.</param>
    /// <param name="sequenceId">Packet sequence ID.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task WritePacketAsync(byte[] payload, byte sequenceId, CancellationToken ct = default)
    {
        var packet = new byte[4 + payload.Length];

        // Length (3 bytes, little-endian)
        packet[0] = (byte)(payload.Length & 0xFF);
        packet[1] = (byte)((payload.Length >> 8) & 0xFF);
        packet[2] = (byte)((payload.Length >> 16) & 0xFF);
        packet[3] = sequenceId;

        Array.Copy(payload, 0, packet, 4, payload.Length);

        await _stream.WriteAsync(packet, ct).ConfigureAwait(false);
        await _stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the initial handshake packet to the client.
    /// </summary>
    /// <param name="config">Protocol configuration.</param>
    /// <param name="connectionId">Connection thread ID.</param>
    /// <param name="authData">Authentication challenge data (20 bytes).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task WriteHandshakeAsync(
        MySqlProtocolConfig config,
        uint connectionId,
        byte[] authData,
        CancellationToken ct = default)
    {
        using var ms = new MemoryStream();

        // Protocol version
        ms.WriteByte(MySqlProtocolConstants.ProtocolVersion);

        // Server version (null-terminated)
        var versionBytes = Encoding.UTF8.GetBytes(config.ServerVersion);
        ms.Write(versionBytes);
        ms.WriteByte(0);

        // Connection ID (4 bytes, little-endian)
        var connIdBytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(connIdBytes, connectionId);
        ms.Write(connIdBytes);

        // Auth plugin data part 1 (8 bytes)
        ms.Write(authData, 0, 8);

        // Filler
        ms.WriteByte(0);

        // Capability flags lower 2 bytes
        var capabilities = MySqlCapabilities.SERVER_DEFAULT;
        if (config.SslMode != "disabled")
        {
            capabilities |= MySqlCapabilities.CLIENT_SSL;
        }
        var capBytes = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(capBytes, (ushort)((uint)capabilities & 0xFFFF));
        ms.Write(capBytes);

        // Character set
        ms.WriteByte(config.AuthMethod == "caching_sha2_password"
            ? MySqlProtocolConstants.Utf8Mb4GeneralCi
            : MySqlProtocolConstants.DefaultCharset);

        // Status flags
        var statusBytes = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(statusBytes, (ushort)MySqlServerStatus.SERVER_STATUS_AUTOCOMMIT);
        ms.Write(statusBytes);

        // Capability flags upper 2 bytes
        BinaryPrimitives.WriteUInt16LittleEndian(capBytes, (ushort)(((uint)capabilities >> 16) & 0xFFFF));
        ms.Write(capBytes);

        // Length of auth plugin data (21 for mysql_native_password, includes null terminator)
        ms.WriteByte((byte)(authData.Length + 1));

        // Reserved (10 bytes of 0)
        ms.Write(new byte[10]);

        // Auth plugin data part 2 (remaining bytes + null terminator)
        ms.Write(authData, 8, authData.Length - 8);
        ms.WriteByte(0);

        // Auth plugin name (null-terminated)
        var authPluginName = config.AuthMethod switch
        {
            "caching_sha2_password" => MySqlProtocolConstants.AuthPluginCachingSha2Password,
            "mysql_native_password" => MySqlProtocolConstants.AuthPluginMySqlNativePassword,
            _ => MySqlProtocolConstants.AuthPluginMySqlNativePassword
        };
        var authPluginBytes = Encoding.UTF8.GetBytes(authPluginName);
        ms.Write(authPluginBytes);
        ms.WriteByte(0);

        await WritePacketAsync(ms.ToArray(), 0, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes an OK packet to the client.
    /// </summary>
    /// <param name="sequenceId">Packet sequence ID.</param>
    /// <param name="affectedRows">Number of affected rows.</param>
    /// <param name="lastInsertId">Last insert ID.</param>
    /// <param name="serverStatus">Server status flags.</param>
    /// <param name="warnings">Number of warnings.</param>
    /// <param name="info">Optional info string.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task WriteOkPacketAsync(
        byte sequenceId,
        ulong affectedRows = 0,
        ulong lastInsertId = 0,
        MySqlServerStatus serverStatus = MySqlServerStatus.SERVER_STATUS_AUTOCOMMIT,
        ushort warnings = 0,
        string? info = null,
        CancellationToken ct = default)
    {
        using var ms = new MemoryStream();

        // OK header
        ms.WriteByte(MySqlProtocolConstants.OkPacketHeader);

        // Affected rows (length-encoded)
        WriteLengthEncodedInteger(ms, affectedRows);

        // Last insert ID (length-encoded)
        WriteLengthEncodedInteger(ms, lastInsertId);

        // Server status (2 bytes)
        var statusBytes = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(statusBytes, (ushort)serverStatus);
        ms.Write(statusBytes);

        // Warnings (2 bytes)
        var warningBytes = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(warningBytes, warnings);
        ms.Write(warningBytes);

        // Info (optional)
        if (!string.IsNullOrEmpty(info))
        {
            var infoBytes = Encoding.UTF8.GetBytes(info);
            ms.Write(infoBytes);
        }

        await WritePacketAsync(ms.ToArray(), sequenceId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes an error packet to the client.
    /// </summary>
    /// <param name="sequenceId">Packet sequence ID.</param>
    /// <param name="errorCode">MySQL error code.</param>
    /// <param name="sqlState">SQL state (5 characters).</param>
    /// <param name="message">Error message.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task WriteErrorPacketAsync(
        byte sequenceId,
        ushort errorCode,
        string sqlState,
        string message,
        CancellationToken ct = default)
    {
        using var ms = new MemoryStream();

        // Error header
        ms.WriteByte(MySqlProtocolConstants.ErrPacketHeader);

        // Error code (2 bytes)
        var codeBytes = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(codeBytes, errorCode);
        ms.Write(codeBytes);

        // SQL state marker
        ms.WriteByte((byte)'#');

        // SQL state (5 bytes)
        var stateBytes = Encoding.ASCII.GetBytes(sqlState.PadRight(5)[..5]);
        ms.Write(stateBytes);

        // Error message
        var msgBytes = Encoding.UTF8.GetBytes(message);
        ms.Write(msgBytes);

        await WritePacketAsync(ms.ToArray(), sequenceId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes an EOF packet to the client.
    /// </summary>
    /// <param name="sequenceId">Packet sequence ID.</param>
    /// <param name="warnings">Number of warnings.</param>
    /// <param name="serverStatus">Server status flags.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task WriteEofPacketAsync(
        byte sequenceId,
        ushort warnings = 0,
        MySqlServerStatus serverStatus = MySqlServerStatus.SERVER_STATUS_AUTOCOMMIT,
        CancellationToken ct = default)
    {
        using var ms = new MemoryStream();

        // EOF header
        ms.WriteByte(MySqlProtocolConstants.EofPacketHeader);

        // Warnings (2 bytes)
        var warningBytes = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(warningBytes, warnings);
        ms.Write(warningBytes);

        // Server status (2 bytes)
        var statusBytes = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(statusBytes, (ushort)serverStatus);
        ms.Write(statusBytes);

        await WritePacketAsync(ms.ToArray(), sequenceId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the column count packet for a result set.
    /// </summary>
    /// <param name="sequenceId">Packet sequence ID.</param>
    /// <param name="columnCount">Number of columns.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Next sequence ID.</returns>
    public async Task<byte> WriteColumnCountAsync(byte sequenceId, int columnCount, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        WriteLengthEncodedInteger(ms, (ulong)columnCount);
        await WritePacketAsync(ms.ToArray(), sequenceId, ct).ConfigureAwait(false);
        return (byte)(sequenceId + 1);
    }

    /// <summary>
    /// Writes a column definition packet.
    /// </summary>
    /// <param name="sequenceId">Packet sequence ID.</param>
    /// <param name="column">Column description.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Next sequence ID.</returns>
    public async Task<byte> WriteColumnDefinitionAsync(
        byte sequenceId,
        MySqlColumnDescription column,
        CancellationToken ct = default)
    {
        using var ms = new MemoryStream();

        // Catalog (length-encoded string) - always "def"
        WriteLengthEncodedString(ms, column.Catalog);

        // Schema (length-encoded string)
        WriteLengthEncodedString(ms, column.Schema);

        // Virtual table (length-encoded string)
        WriteLengthEncodedString(ms, column.VirtualTable);

        // Physical table (length-encoded string)
        WriteLengthEncodedString(ms, column.PhysicalTable);

        // Virtual name (length-encoded string)
        WriteLengthEncodedString(ms, column.VirtualName);

        // Physical name (length-encoded string)
        WriteLengthEncodedString(ms, column.PhysicalName);

        // Length of fixed fields (always 0x0C)
        ms.WriteByte(0x0C);

        // Character set (2 bytes)
        var charsetBytes = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(charsetBytes, column.CharacterSet);
        ms.Write(charsetBytes);

        // Column length (4 bytes)
        var lengthBytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(lengthBytes, column.ColumnLength);
        ms.Write(lengthBytes);

        // Column type (1 byte)
        ms.WriteByte((byte)column.ColumnType);

        // Flags (2 bytes)
        var flagBytes = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(flagBytes, (ushort)column.Flags);
        ms.Write(flagBytes);

        // Decimals (1 byte)
        ms.WriteByte(column.Decimals);

        // Filler (2 bytes)
        ms.Write(new byte[2]);

        await WritePacketAsync(ms.ToArray(), sequenceId, ct).ConfigureAwait(false);
        return (byte)(sequenceId + 1);
    }

    /// <summary>
    /// Writes a text protocol data row packet.
    /// </summary>
    /// <param name="sequenceId">Packet sequence ID.</param>
    /// <param name="values">Row values (null for NULL, byte[] for text values).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Next sequence ID.</returns>
    public async Task<byte> WriteTextRowAsync(
        byte sequenceId,
        IReadOnlyList<byte[]?> values,
        CancellationToken ct = default)
    {
        using var ms = new MemoryStream();

        foreach (var value in values)
        {
            if (value == null)
            {
                // NULL value
                ms.WriteByte(0xFB);
            }
            else
            {
                // Length-encoded string
                WriteLengthEncodedInteger(ms, (ulong)value.Length);
                ms.Write(value);
            }
        }

        await WritePacketAsync(ms.ToArray(), sequenceId, ct).ConfigureAwait(false);
        return (byte)(sequenceId + 1);
    }

    /// <summary>
    /// Writes a binary protocol data row packet for prepared statement execution.
    /// </summary>
    /// <param name="sequenceId">Packet sequence ID.</param>
    /// <param name="values">Row values.</param>
    /// <param name="columns">Column definitions for type information.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Next sequence ID.</returns>
    public async Task<byte> WriteBinaryRowAsync(
        byte sequenceId,
        IReadOnlyList<object?> values,
        IReadOnlyList<MySqlColumnDescription> columns,
        CancellationToken ct = default)
    {
        using var ms = new MemoryStream();

        // Packet header for binary row
        ms.WriteByte(0x00);

        // NULL bitmap (column_count + 7 + 2) / 8 bytes
        var nullBitmapLength = (values.Count + 7 + 2) / 8;
        var nullBitmap = new byte[nullBitmapLength];

        for (int i = 0; i < values.Count; i++)
        {
            if (values[i] == null)
            {
                var bytePos = (i + 2) / 8;
                var bitPos = (i + 2) % 8;
                nullBitmap[bytePos] |= (byte)(1 << bitPos);
            }
        }
        ms.Write(nullBitmap);

        // Values (non-NULL only)
        for (int i = 0; i < values.Count; i++)
        {
            var value = values[i];
            if (value == null) continue;

            var column = i < columns.Count ? columns[i] : null;
            WriteBinaryValue(ms, value, column?.ColumnType ?? MySqlFieldType.MYSQL_TYPE_STRING);
        }

        await WritePacketAsync(ms.ToArray(), sequenceId, ct).ConfigureAwait(false);
        return (byte)(sequenceId + 1);
    }

    /// <summary>
    /// Writes a prepared statement response packet.
    /// </summary>
    /// <param name="sequenceId">Packet sequence ID.</param>
    /// <param name="statementId">Statement ID.</param>
    /// <param name="columnCount">Number of columns.</param>
    /// <param name="parameterCount">Number of parameters.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Next sequence ID.</returns>
    public async Task<byte> WritePrepareResponseAsync(
        byte sequenceId,
        uint statementId,
        ushort columnCount,
        ushort parameterCount,
        CancellationToken ct = default)
    {
        using var ms = new MemoryStream();

        // Status (0 = OK)
        ms.WriteByte(0x00);

        // Statement ID (4 bytes)
        var stmtIdBytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(stmtIdBytes, statementId);
        ms.Write(stmtIdBytes);

        // Number of columns (2 bytes)
        var colCountBytes = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(colCountBytes, columnCount);
        ms.Write(colCountBytes);

        // Number of parameters (2 bytes)
        var paramCountBytes = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(paramCountBytes, parameterCount);
        ms.Write(paramCountBytes);

        // Reserved (1 byte)
        ms.WriteByte(0x00);

        // Warning count (2 bytes)
        ms.Write(new byte[2]);

        await WritePacketAsync(ms.ToArray(), sequenceId, ct).ConfigureAwait(false);
        return (byte)(sequenceId + 1);
    }

    /// <summary>
    /// Writes an auth switch request packet.
    /// </summary>
    /// <param name="sequenceId">Packet sequence ID.</param>
    /// <param name="pluginName">Authentication plugin name.</param>
    /// <param name="authData">Authentication data.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task WriteAuthSwitchRequestAsync(
        byte sequenceId,
        string pluginName,
        byte[] authData,
        CancellationToken ct = default)
    {
        using var ms = new MemoryStream();

        // Auth switch header
        ms.WriteByte(MySqlProtocolConstants.AuthSwitchRequest);

        // Plugin name (null-terminated)
        var pluginBytes = Encoding.UTF8.GetBytes(pluginName);
        ms.Write(pluginBytes);
        ms.WriteByte(0);

        // Auth data
        ms.Write(authData);

        await WritePacketAsync(ms.ToArray(), sequenceId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes an auth more data packet for caching_sha2_password.
    /// </summary>
    /// <param name="sequenceId">Packet sequence ID.</param>
    /// <param name="status">Status byte (0x03 = fast auth success, 0x04 = full auth required).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task WriteAuthMoreDataAsync(byte sequenceId, byte status, CancellationToken ct = default)
    {
        var payload = new byte[] { MySqlProtocolConstants.AuthMoreData, status };
        await WritePacketAsync(payload, sequenceId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Generates authentication challenge data (20 bytes).
    /// </summary>
    /// <returns>Random 20-byte challenge.</returns>
    public static byte[] GenerateAuthData()
    {
        var authData = new byte[20];
        RandomNumberGenerator.Fill(authData);
        return authData;
    }

    /// <summary>
    /// Computes mysql_native_password authentication response.
    /// DEPRECATED: This method uses SHA-1 which is cryptographically weak.
    /// Prefer using caching_sha2_password for MySQL 8.0+.
    /// </summary>
    /// <param name="password">User password.</param>
    /// <param name="authData">Server authentication challenge.</param>
    /// <returns>Authentication response bytes.</returns>
    public static byte[] ComputeNativePasswordAuth(string password, byte[] authData)
    {
        if (string.IsNullOrEmpty(password))
        {
            return Array.Empty<byte>();
        }

        Console.WriteLine("[MySqlProtocol] WARNING: Using deprecated mysql_native_password (SHA-1 based). " +
                          "Consider upgrading to caching_sha2_password for MySQL 8.0+.");

        // SHA1(password) XOR SHA1(auth_data + SHA1(SHA1(password)))
        using var sha1 = SHA1.Create();
        var passwordBytes = Encoding.UTF8.GetBytes(password);

        var hash1 = sha1.ComputeHash(passwordBytes);
        var hash2 = sha1.ComputeHash(hash1);

        var combined = new byte[authData.Length + hash2.Length];
        Array.Copy(authData, 0, combined, 0, authData.Length);
        Array.Copy(hash2, 0, combined, authData.Length, hash2.Length);

        var hash3 = sha1.ComputeHash(combined);

        var result = new byte[20];
        for (int i = 0; i < 20; i++)
        {
            result[i] = (byte)(hash1[i] ^ hash3[i]);
        }

        return result;
    }

    /// <summary>
    /// Computes caching_sha2_password authentication response.
    /// </summary>
    /// <param name="password">User password.</param>
    /// <param name="authData">Server authentication challenge.</param>
    /// <returns>Authentication response bytes.</returns>
    public static byte[] ComputeCachingSha2Auth(string password, byte[] authData)
    {
        if (string.IsNullOrEmpty(password))
        {
            return Array.Empty<byte>();
        }

        // SHA256(password) XOR SHA256(SHA256(SHA256(password)) + auth_data)
        using var sha256 = SHA256.Create();
        var passwordBytes = Encoding.UTF8.GetBytes(password);

        var hash1 = sha256.ComputeHash(passwordBytes);
        var hash2 = sha256.ComputeHash(hash1);

        var combined = new byte[hash2.Length + authData.Length];
        Array.Copy(hash2, 0, combined, 0, hash2.Length);
        Array.Copy(authData, 0, combined, hash2.Length, authData.Length);

        var hash3 = sha256.ComputeHash(combined);

        var result = new byte[32];
        for (int i = 0; i < 32; i++)
        {
            result[i] = (byte)(hash1[i] ^ hash3[i]);
        }

        return result;
    }

    private static void WriteLengthEncodedInteger(Stream stream, ulong value)
    {
        if (value < 251)
        {
            stream.WriteByte((byte)value);
        }
        else if (value < 65536)
        {
            stream.WriteByte(0xFC);
            var bytes = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)value);
            stream.Write(bytes);
        }
        else if (value < 16777216)
        {
            stream.WriteByte(0xFD);
            stream.WriteByte((byte)(value & 0xFF));
            stream.WriteByte((byte)((value >> 8) & 0xFF));
            stream.WriteByte((byte)((value >> 16) & 0xFF));
        }
        else
        {
            stream.WriteByte(0xFE);
            var bytes = new byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
            stream.Write(bytes);
        }
    }

    private static void WriteLengthEncodedString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteLengthEncodedInteger(stream, (ulong)bytes.Length);
        stream.Write(bytes);
    }

    private static void WriteBinaryValue(Stream stream, object value, MySqlFieldType type)
    {
        switch (value)
        {
            case null:
                // Already handled in null bitmap
                break;
            case sbyte sb:
                stream.WriteByte((byte)sb);
                break;
            case byte b:
                stream.WriteByte(b);
                break;
            case short s:
                var shortBytes = new byte[2];
                BinaryPrimitives.WriteInt16LittleEndian(shortBytes, s);
                stream.Write(shortBytes);
                break;
            case ushort us:
                var ushortBytes = new byte[2];
                BinaryPrimitives.WriteUInt16LittleEndian(ushortBytes, us);
                stream.Write(ushortBytes);
                break;
            case int i:
                var intBytes = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(intBytes, i);
                stream.Write(intBytes);
                break;
            case uint ui:
                var uintBytes = new byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(uintBytes, ui);
                stream.Write(uintBytes);
                break;
            case long l:
                var longBytes = new byte[8];
                BinaryPrimitives.WriteInt64LittleEndian(longBytes, l);
                stream.Write(longBytes);
                break;
            case ulong ul:
                var ulongBytes = new byte[8];
                BinaryPrimitives.WriteUInt64LittleEndian(ulongBytes, ul);
                stream.Write(ulongBytes);
                break;
            case float f:
                var floatBytes = new byte[4];
                BinaryPrimitives.WriteSingleLittleEndian(floatBytes, f);
                stream.Write(floatBytes);
                break;
            case double d:
                var doubleBytes = new byte[8];
                BinaryPrimitives.WriteDoubleLittleEndian(doubleBytes, d);
                stream.Write(doubleBytes);
                break;
            case decimal dec:
                var decStr = dec.ToString(System.Globalization.CultureInfo.InvariantCulture);
                WriteLengthEncodedString(stream, decStr);
                break;
            case DateTime dt:
                WriteBinaryDateTime(stream, dt);
                break;
            case TimeSpan ts:
                WriteBinaryTime(stream, ts);
                break;
            case byte[] bytes:
                WriteLengthEncodedInteger(stream, (ulong)bytes.Length);
                stream.Write(bytes);
                break;
            case string str:
                WriteLengthEncodedString(stream, str);
                break;
            default:
                var strValue = value.ToString() ?? string.Empty;
                WriteLengthEncodedString(stream, strValue);
                break;
        }
    }

    private static void WriteBinaryDateTime(Stream stream, DateTime dt)
    {
        if (dt == DateTime.MinValue)
        {
            stream.WriteByte(0); // length = 0 means NULL datetime
            return;
        }

        var hasMicroseconds = dt.Millisecond > 0;
        var hasTime = dt.Hour > 0 || dt.Minute > 0 || dt.Second > 0 || hasMicroseconds;

        if (!hasTime && dt.Year == 0 && dt.Month == 0 && dt.Day == 0)
        {
            stream.WriteByte(0);
            return;
        }

        byte length = (byte)(hasTime ? (hasMicroseconds ? 11 : 7) : 4);
        stream.WriteByte(length);

        // Year (2 bytes)
        var yearBytes = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(yearBytes, (ushort)dt.Year);
        stream.Write(yearBytes);

        // Month and Day (1 byte each)
        stream.WriteByte((byte)dt.Month);
        stream.WriteByte((byte)dt.Day);

        if (hasTime)
        {
            stream.WriteByte((byte)dt.Hour);
            stream.WriteByte((byte)dt.Minute);
            stream.WriteByte((byte)dt.Second);

            if (hasMicroseconds)
            {
                var microseconds = dt.Millisecond * 1000;
                var microBytes = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(microBytes, microseconds);
                stream.Write(microBytes);
            }
        }
    }

    private static void WriteBinaryTime(Stream stream, TimeSpan ts)
    {
        if (ts == TimeSpan.Zero)
        {
            stream.WriteByte(0);
            return;
        }

        var hasMicroseconds = ts.Milliseconds > 0;
        byte length = (byte)(hasMicroseconds ? 12 : 8);
        stream.WriteByte(length);

        // Is negative
        stream.WriteByte((byte)(ts < TimeSpan.Zero ? 1 : 0));

        // Days (4 bytes)
        var absTs = ts < TimeSpan.Zero ? -ts : ts;
        var daysBytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(daysBytes, absTs.Days);
        stream.Write(daysBytes);

        // Hours, Minutes, Seconds
        stream.WriteByte((byte)absTs.Hours);
        stream.WriteByte((byte)absTs.Minutes);
        stream.WriteByte((byte)absTs.Seconds);

        if (hasMicroseconds)
        {
            var microseconds = absTs.Milliseconds * 1000;
            var microBytes = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(microBytes, microseconds);
            stream.Write(microBytes);
        }
    }
}
