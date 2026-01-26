using System.Buffers.Binary;
using System.Text;

namespace DataWarehouse.Plugins.MySqlProtocol.Protocol;

/// <summary>
/// Reads MySQL wire protocol packets from a network stream.
/// Handles packet framing, length-encoded integers, and null-terminated strings.
/// Thread-safe for single connection use.
/// </summary>
public sealed class MessageReader
{
    private readonly Stream _stream;
    private readonly byte[] _headerBuffer = new byte[4];

    /// <summary>
    /// Initializes a new instance of the <see cref="MessageReader"/> class.
    /// </summary>
    /// <param name="stream">The network stream to read from.</param>
    /// <exception cref="ArgumentNullException">Thrown when stream is null.</exception>
    public MessageReader(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }

    /// <summary>
    /// Reads a complete MySQL packet from the stream.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The packet payload and sequence ID.</returns>
    /// <exception cref="EndOfStreamException">Thrown when connection is closed.</exception>
    /// <exception cref="InvalidDataException">Thrown when packet format is invalid.</exception>
    public async Task<MySqlPacket> ReadPacketAsync(CancellationToken ct = default)
    {
        // Read 4-byte header: 3 bytes length + 1 byte sequence ID
        await ReadExactAsync(_headerBuffer, 0, 4, ct).ConfigureAwait(false);

        var length = _headerBuffer[0] | (_headerBuffer[1] << 8) | (_headerBuffer[2] << 16);
        var sequenceId = _headerBuffer[3];

        if (length < 0 || length > MySqlProtocolConstants.MaxPacketSize)
        {
            throw new InvalidDataException($"Invalid MySQL packet length: {length}");
        }

        var payload = new byte[length];
        if (length > 0)
        {
            await ReadExactAsync(payload, 0, length, ct).ConfigureAwait(false);
        }

        return new MySqlPacket
        {
            Length = length,
            SequenceId = sequenceId,
            Payload = payload
        };
    }

    /// <summary>
    /// Reads the client handshake response after initial handshake.
    /// </summary>
    /// <param name="packet">The received packet.</param>
    /// <param name="serverCapabilities">Server capabilities for parsing.</param>
    /// <returns>Parsed handshake response.</returns>
    public HandshakeResponse ParseHandshakeResponse(MySqlPacket packet, MySqlCapabilities serverCapabilities)
    {
        var reader = new SpanReader(packet.Payload);
        var response = new HandshakeResponse();

        // Client capabilities (4 bytes for 4.1 protocol)
        if (serverCapabilities.HasFlag(MySqlCapabilities.CLIENT_PROTOCOL_41))
        {
            response.Capabilities = (MySqlCapabilities)reader.ReadUInt32();
            response.MaxPacketSize = reader.ReadUInt32();
            response.CharacterSet = reader.ReadByte();

            // Skip 23 reserved bytes
            reader.Skip(23);
        }
        else
        {
            response.Capabilities = (MySqlCapabilities)reader.ReadUInt16();
            response.MaxPacketSize = reader.ReadUInt24();
        }

        // Check for SSL request (no username means SSL request)
        if (response.Capabilities.HasFlag(MySqlCapabilities.CLIENT_SSL) && reader.Remaining == 0)
        {
            response.IsSslRequest = true;
            return response;
        }

        // Username (null-terminated)
        response.Username = reader.ReadNullTerminatedString();

        // Auth response
        if (response.Capabilities.HasFlag(MySqlCapabilities.CLIENT_PLUGIN_AUTH_LENENC_CLIENT_DATA))
        {
            var authLen = reader.ReadLengthEncodedInteger();
            response.AuthResponse = reader.ReadBytes((int)authLen);
        }
        else if (response.Capabilities.HasFlag(MySqlCapabilities.CLIENT_SECURE_CONNECTION))
        {
            var authLen = reader.ReadByte();
            response.AuthResponse = reader.ReadBytes(authLen);
        }
        else
        {
            response.AuthResponse = reader.ReadNullTerminatedBytes();
        }

        // Database (optional)
        if (response.Capabilities.HasFlag(MySqlCapabilities.CLIENT_CONNECT_WITH_DB) && reader.Remaining > 0)
        {
            response.Database = reader.ReadNullTerminatedString();
        }

        // Auth plugin name (optional)
        if (response.Capabilities.HasFlag(MySqlCapabilities.CLIENT_PLUGIN_AUTH) && reader.Remaining > 0)
        {
            response.AuthPluginName = reader.ReadNullTerminatedString();
        }

        // Connection attributes (optional)
        if (response.Capabilities.HasFlag(MySqlCapabilities.CLIENT_CONNECT_ATTRS) && reader.Remaining > 0)
        {
            var attrsLen = reader.ReadLengthEncodedInteger();
            var attrsEnd = reader.Position + (int)attrsLen;

            while (reader.Position < attrsEnd && reader.Remaining > 0)
            {
                var key = reader.ReadLengthEncodedString();
                var value = reader.ReadLengthEncodedString();
                response.ConnectionAttributes[key] = value;
            }
        }

        return response;
    }

    /// <summary>
    /// Parses a COM_QUERY command payload.
    /// </summary>
    /// <param name="packet">The packet containing the query.</param>
    /// <returns>The SQL query string.</returns>
    public string ParseQueryCommand(MySqlPacket packet)
    {
        if (packet.Payload.Length < 1)
        {
            throw new InvalidDataException("Empty query packet");
        }

        // Skip command byte
        return Encoding.UTF8.GetString(packet.Payload, 1, packet.Payload.Length - 1);
    }

    /// <summary>
    /// Parses a COM_STMT_PREPARE command payload.
    /// </summary>
    /// <param name="packet">The packet containing the prepare request.</param>
    /// <returns>The SQL query string to prepare.</returns>
    public string ParsePrepareCommand(MySqlPacket packet)
    {
        if (packet.Payload.Length < 1)
        {
            throw new InvalidDataException("Empty prepare packet");
        }

        // Skip command byte
        return Encoding.UTF8.GetString(packet.Payload, 1, packet.Payload.Length - 1);
    }

    /// <summary>
    /// Parses a COM_STMT_EXECUTE command payload.
    /// </summary>
    /// <param name="packet">The packet containing the execute request.</param>
    /// <param name="statement">The prepared statement being executed.</param>
    /// <returns>Parsed execute request with parameter values.</returns>
    public StmtExecuteRequest ParseExecuteCommand(MySqlPacket packet, MySqlPreparedStatement statement)
    {
        var reader = new SpanReader(packet.Payload);

        // Skip command byte
        reader.Skip(1);

        var request = new StmtExecuteRequest
        {
            StatementId = reader.ReadUInt32(),
            Flags = reader.ReadByte(),
            IterationCount = reader.ReadUInt32()
        };

        if (statement.ParameterCount > 0)
        {
            // Null bitmap
            var nullBitmapLength = (statement.ParameterCount + 7) / 8;
            var nullBitmap = reader.ReadBytes(nullBitmapLength);

            // New params bound flag
            var newParamsBound = reader.ReadByte();

            // Parameter types (if new params bound)
            var parameterTypes = new List<MySqlFieldType>();
            if (newParamsBound == 1)
            {
                for (int i = 0; i < statement.ParameterCount; i++)
                {
                    var type = (MySqlFieldType)reader.ReadByte();
                    var unsignedFlag = reader.ReadByte(); // unsigned flag
                    parameterTypes.Add(type);
                }
            }
            else
            {
                parameterTypes.AddRange(statement.ParameterTypes);
            }

            // Parameter values
            for (int i = 0; i < statement.ParameterCount; i++)
            {
                // Check null bitmap
                if ((nullBitmap[i / 8] & (1 << (i % 8))) != 0)
                {
                    request.ParameterValues.Add(null);
                    continue;
                }

                var type = i < parameterTypes.Count ? parameterTypes[i] : MySqlFieldType.MYSQL_TYPE_STRING;
                var value = ReadBinaryValue(ref reader, type);
                request.ParameterValues.Add(value);
            }
        }

        return request;
    }

    /// <summary>
    /// Parses a COM_STMT_CLOSE command payload.
    /// </summary>
    /// <param name="packet">The packet containing the close request.</param>
    /// <returns>The statement ID to close.</returns>
    public uint ParseCloseCommand(MySqlPacket packet)
    {
        if (packet.Payload.Length < 5)
        {
            throw new InvalidDataException("Invalid close statement packet");
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(packet.Payload.AsSpan(1, 4));
    }

    /// <summary>
    /// Parses a COM_INIT_DB command payload.
    /// </summary>
    /// <param name="packet">The packet containing the init_db request.</param>
    /// <returns>The database name to switch to.</returns>
    public string ParseInitDbCommand(MySqlPacket packet)
    {
        if (packet.Payload.Length < 1)
        {
            throw new InvalidDataException("Empty init_db packet");
        }

        return Encoding.UTF8.GetString(packet.Payload, 1, packet.Payload.Length - 1);
    }

    private object? ReadBinaryValue(ref SpanReader reader, MySqlFieldType type)
    {
        return type switch
        {
            MySqlFieldType.MYSQL_TYPE_TINY => reader.ReadByte(),
            MySqlFieldType.MYSQL_TYPE_SHORT => reader.ReadInt16(),
            MySqlFieldType.MYSQL_TYPE_LONG => reader.ReadInt32(),
            MySqlFieldType.MYSQL_TYPE_LONGLONG => reader.ReadInt64(),
            MySqlFieldType.MYSQL_TYPE_FLOAT => reader.ReadFloat(),
            MySqlFieldType.MYSQL_TYPE_DOUBLE => reader.ReadDouble(),
            MySqlFieldType.MYSQL_TYPE_STRING or
            MySqlFieldType.MYSQL_TYPE_VARCHAR or
            MySqlFieldType.MYSQL_TYPE_VAR_STRING or
            MySqlFieldType.MYSQL_TYPE_BLOB or
            MySqlFieldType.MYSQL_TYPE_TINY_BLOB or
            MySqlFieldType.MYSQL_TYPE_MEDIUM_BLOB or
            MySqlFieldType.MYSQL_TYPE_LONG_BLOB => reader.ReadLengthEncodedString(),
            MySqlFieldType.MYSQL_TYPE_DATE or
            MySqlFieldType.MYSQL_TYPE_DATETIME or
            MySqlFieldType.MYSQL_TYPE_TIMESTAMP => ReadBinaryDateTime(ref reader),
            MySqlFieldType.MYSQL_TYPE_TIME => ReadBinaryTime(ref reader),
            MySqlFieldType.MYSQL_TYPE_NEWDECIMAL or
            MySqlFieldType.MYSQL_TYPE_DECIMAL => reader.ReadLengthEncodedString(),
            _ => reader.ReadLengthEncodedString()
        };
    }

    private DateTime? ReadBinaryDateTime(ref SpanReader reader)
    {
        var length = reader.ReadByte();
        if (length == 0) return null;

        var year = reader.ReadUInt16();
        var month = reader.ReadByte();
        var day = reader.ReadByte();

        int hour = 0, minute = 0, second = 0, microsecond = 0;

        if (length >= 7)
        {
            hour = reader.ReadByte();
            minute = reader.ReadByte();
            second = reader.ReadByte();
        }

        if (length >= 11)
        {
            microsecond = reader.ReadInt32();
        }

        return new DateTime(year, month, day, hour, minute, second, microsecond / 1000);
    }

    private TimeSpan? ReadBinaryTime(ref SpanReader reader)
    {
        var length = reader.ReadByte();
        if (length == 0) return null;

        var isNegative = reader.ReadByte() != 0;
        var days = reader.ReadInt32();
        var hours = reader.ReadByte();
        var minutes = reader.ReadByte();
        var seconds = reader.ReadByte();

        int microseconds = 0;
        if (length >= 12)
        {
            microseconds = reader.ReadInt32();
        }

        var ts = new TimeSpan(days, hours, minutes, seconds, microseconds / 1000);
        return isNegative ? -ts : ts;
    }

    private async Task ReadExactAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        var totalRead = 0;
        while (totalRead < count)
        {
            var read = await _stream.ReadAsync(buffer.AsMemory(offset + totalRead, count - totalRead), ct).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("Connection closed unexpectedly");
            }
            totalRead += read;
        }
    }
}

/// <summary>
/// MySQL packet structure.
/// </summary>
public readonly struct MySqlPacket
{
    /// <summary>
    /// Payload length in bytes.
    /// </summary>
    public required int Length { get; init; }

    /// <summary>
    /// Packet sequence ID.
    /// </summary>
    public required byte SequenceId { get; init; }

    /// <summary>
    /// Packet payload data.
    /// </summary>
    public required byte[] Payload { get; init; }
}

/// <summary>
/// Parsed handshake response from client.
/// </summary>
public sealed class HandshakeResponse
{
    /// <summary>Client capabilities.</summary>
    public MySqlCapabilities Capabilities { get; set; }
    /// <summary>Maximum packet size.</summary>
    public uint MaxPacketSize { get; set; }
    /// <summary>Character set.</summary>
    public byte CharacterSet { get; set; }
    /// <summary>Username.</summary>
    public string Username { get; set; } = string.Empty;
    /// <summary>Authentication response data.</summary>
    public byte[] AuthResponse { get; set; } = Array.Empty<byte>();
    /// <summary>Database name.</summary>
    public string? Database { get; set; }
    /// <summary>Authentication plugin name.</summary>
    public string? AuthPluginName { get; set; }
    /// <summary>Connection attributes.</summary>
    public Dictionary<string, string> ConnectionAttributes { get; } = new();
    /// <summary>Whether this is an SSL upgrade request.</summary>
    public bool IsSslRequest { get; set; }
}

/// <summary>
/// Parsed prepared statement execute request.
/// </summary>
public sealed class StmtExecuteRequest
{
    /// <summary>Statement ID.</summary>
    public uint StatementId { get; set; }
    /// <summary>Execution flags.</summary>
    public byte Flags { get; set; }
    /// <summary>Iteration count (reserved).</summary>
    public uint IterationCount { get; set; }
    /// <summary>Parameter values.</summary>
    public List<object?> ParameterValues { get; } = new();
}

/// <summary>
/// Helper for reading from a byte span.
/// </summary>
internal ref struct SpanReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _position;

    /// <summary>
    /// Current position in the span.
    /// </summary>
    public int Position => _position;

    /// <summary>
    /// Remaining bytes to read.
    /// </summary>
    public int Remaining => _data.Length - _position;

    /// <summary>
    /// Creates a new SpanReader.
    /// </summary>
    /// <param name="data">Data to read from.</param>
    public SpanReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _position = 0;
    }

    /// <summary>
    /// Reads a single byte.
    /// </summary>
    public byte ReadByte()
    {
        if (_position >= _data.Length)
            throw new InvalidDataException("Unexpected end of data");
        return _data[_position++];
    }

    /// <summary>
    /// Reads a 16-bit unsigned integer (little-endian).
    /// </summary>
    public ushort ReadUInt16()
    {
        if (_position + 2 > _data.Length)
            throw new InvalidDataException("Unexpected end of data");
        var value = BinaryPrimitives.ReadUInt16LittleEndian(_data.Slice(_position, 2));
        _position += 2;
        return value;
    }

    /// <summary>
    /// Reads a 16-bit signed integer (little-endian).
    /// </summary>
    public short ReadInt16()
    {
        if (_position + 2 > _data.Length)
            throw new InvalidDataException("Unexpected end of data");
        var value = BinaryPrimitives.ReadInt16LittleEndian(_data.Slice(_position, 2));
        _position += 2;
        return value;
    }

    /// <summary>
    /// Reads a 24-bit unsigned integer (little-endian).
    /// </summary>
    public uint ReadUInt24()
    {
        if (_position + 3 > _data.Length)
            throw new InvalidDataException("Unexpected end of data");
        var value = (uint)(_data[_position] | (_data[_position + 1] << 8) | (_data[_position + 2] << 16));
        _position += 3;
        return value;
    }

    /// <summary>
    /// Reads a 32-bit unsigned integer (little-endian).
    /// </summary>
    public uint ReadUInt32()
    {
        if (_position + 4 > _data.Length)
            throw new InvalidDataException("Unexpected end of data");
        var value = BinaryPrimitives.ReadUInt32LittleEndian(_data.Slice(_position, 4));
        _position += 4;
        return value;
    }

    /// <summary>
    /// Reads a 32-bit signed integer (little-endian).
    /// </summary>
    public int ReadInt32()
    {
        if (_position + 4 > _data.Length)
            throw new InvalidDataException("Unexpected end of data");
        var value = BinaryPrimitives.ReadInt32LittleEndian(_data.Slice(_position, 4));
        _position += 4;
        return value;
    }

    /// <summary>
    /// Reads a 64-bit signed integer (little-endian).
    /// </summary>
    public long ReadInt64()
    {
        if (_position + 8 > _data.Length)
            throw new InvalidDataException("Unexpected end of data");
        var value = BinaryPrimitives.ReadInt64LittleEndian(_data.Slice(_position, 8));
        _position += 8;
        return value;
    }

    /// <summary>
    /// Reads a single-precision float.
    /// </summary>
    public float ReadFloat()
    {
        if (_position + 4 > _data.Length)
            throw new InvalidDataException("Unexpected end of data");
        var value = BinaryPrimitives.ReadSingleLittleEndian(_data.Slice(_position, 4));
        _position += 4;
        return value;
    }

    /// <summary>
    /// Reads a double-precision float.
    /// </summary>
    public double ReadDouble()
    {
        if (_position + 8 > _data.Length)
            throw new InvalidDataException("Unexpected end of data");
        var value = BinaryPrimitives.ReadDoubleLittleEndian(_data.Slice(_position, 8));
        _position += 8;
        return value;
    }

    /// <summary>
    /// Reads a null-terminated string.
    /// </summary>
    public string ReadNullTerminatedString()
    {
        var start = _position;
        while (_position < _data.Length && _data[_position] != 0)
        {
            _position++;
        }
        var str = Encoding.UTF8.GetString(_data.Slice(start, _position - start));
        if (_position < _data.Length)
            _position++; // Skip null terminator
        return str;
    }

    /// <summary>
    /// Reads bytes until null terminator.
    /// </summary>
    public byte[] ReadNullTerminatedBytes()
    {
        var start = _position;
        while (_position < _data.Length && _data[_position] != 0)
        {
            _position++;
        }
        var bytes = _data.Slice(start, _position - start).ToArray();
        if (_position < _data.Length)
            _position++; // Skip null terminator
        return bytes;
    }

    /// <summary>
    /// Reads a length-encoded integer.
    /// </summary>
    public ulong ReadLengthEncodedInteger()
    {
        if (_position >= _data.Length)
            throw new InvalidDataException("Unexpected end of data");

        var firstByte = _data[_position++];

        if (firstByte < 0xFB)
            return firstByte;

        if (firstByte == 0xFB)
            return 0; // NULL

        if (firstByte == 0xFC)
        {
            if (_position + 2 > _data.Length)
                throw new InvalidDataException("Unexpected end of data");
            var value = BinaryPrimitives.ReadUInt16LittleEndian(_data.Slice(_position, 2));
            _position += 2;
            return value;
        }

        if (firstByte == 0xFD)
        {
            if (_position + 3 > _data.Length)
                throw new InvalidDataException("Unexpected end of data");
            var value = (ulong)(_data[_position] | (_data[_position + 1] << 8) | (_data[_position + 2] << 16));
            _position += 3;
            return value;
        }

        if (firstByte == 0xFE)
        {
            if (_position + 8 > _data.Length)
                throw new InvalidDataException("Unexpected end of data");
            var value = BinaryPrimitives.ReadUInt64LittleEndian(_data.Slice(_position, 8));
            _position += 8;
            return value;
        }

        throw new InvalidDataException($"Invalid length-encoded integer prefix: 0x{firstByte:X2}");
    }

    /// <summary>
    /// Reads a length-encoded string.
    /// </summary>
    public string ReadLengthEncodedString()
    {
        var length = (int)ReadLengthEncodedInteger();
        if (length == 0)
            return string.Empty;

        if (_position + length > _data.Length)
            throw new InvalidDataException("Unexpected end of data");

        var str = Encoding.UTF8.GetString(_data.Slice(_position, length));
        _position += length;
        return str;
    }

    /// <summary>
    /// Reads a specified number of bytes.
    /// </summary>
    public byte[] ReadBytes(int count)
    {
        if (_position + count > _data.Length)
            throw new InvalidDataException("Unexpected end of data");

        var bytes = _data.Slice(_position, count).ToArray();
        _position += count;
        return bytes;
    }

    /// <summary>
    /// Skips a specified number of bytes.
    /// </summary>
    public void Skip(int count)
    {
        if (_position + count > _data.Length)
            throw new InvalidDataException("Unexpected end of data");
        _position += count;
    }
}
