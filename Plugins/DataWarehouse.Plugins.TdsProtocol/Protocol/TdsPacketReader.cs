using System.Buffers.Binary;
using System.Net.Security;
using System.Text;

namespace DataWarehouse.Plugins.TdsProtocol.Protocol;

/// <summary>
/// Reads TDS protocol packets and messages from a network stream.
/// Handles packet reassembly, encryption negotiation, and message parsing.
/// Thread-safe for single-reader scenarios.
/// </summary>
public sealed class TdsPacketReader : IDisposable
{
    private readonly Stream _stream;
    private readonly byte[] _headerBuffer = new byte[8];
    private readonly object _readLock = new();
    private bool _disposed;

    /// <summary>
    /// TDS packet header size in bytes.
    /// </summary>
    public const int HeaderSize = 8;

    /// <summary>
    /// Initializes a new instance of the <see cref="TdsPacketReader"/> class.
    /// </summary>
    /// <param name="stream">The network stream to read from.</param>
    /// <exception cref="ArgumentNullException">Thrown when stream is null.</exception>
    public TdsPacketReader(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }

    /// <summary>
    /// Reads a complete TDS message, reassembling multiple packets if necessary.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The complete message data including packet type information.</returns>
    /// <exception cref="EndOfStreamException">Thrown when connection is closed.</exception>
    /// <exception cref="InvalidDataException">Thrown when packet data is malformed.</exception>
    public async Task<TdsPacketMessage> ReadMessageAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();

        var packets = new List<byte[]>();
        TdsPacketType messageType = TdsPacketType.SqlBatch;
        bool isComplete = false;

        while (!isComplete)
        {
            ct.ThrowIfCancellationRequested();

            var (header, body) = await ReadPacketAsync(ct);

            messageType = header.PacketType;
            packets.Add(body);

            isComplete = (header.Status & TdsPacketStatus.EndOfMessage) != 0;
        }

        // Reassemble message from packets
        var totalLength = packets.Sum(p => p.Length);
        var messageData = new byte[totalLength];
        var offset = 0;

        foreach (var packet in packets)
        {
            Buffer.BlockCopy(packet, 0, messageData, offset, packet.Length);
            offset += packet.Length;
        }

        return new TdsPacketMessage
        {
            PacketType = messageType,
            Data = messageData
        };
    }

    /// <summary>
    /// Reads a single TDS packet (header + body).
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Tuple of packet header and body data.</returns>
    public async Task<(TdsPacketHeader Header, byte[] Body)> ReadPacketAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();

        // Read 8-byte header
        await ReadExactAsync(_headerBuffer, 0, HeaderSize, ct);

        var header = ParseHeader(_headerBuffer);

        if (header.Length < HeaderSize || header.Length > 32768)
        {
            throw new InvalidDataException($"Invalid TDS packet length: {header.Length}");
        }

        // Read packet body
        var bodyLength = header.Length - HeaderSize;
        var body = new byte[bodyLength];

        if (bodyLength > 0)
        {
            await ReadExactAsync(body, 0, bodyLength, ct);
        }

        return (header, body);
    }

    /// <summary>
    /// Reads a PRELOGIN message and parses the options.
    /// </summary>
    /// <param name="data">The PRELOGIN message data.</param>
    /// <returns>Parsed PRELOGIN options.</returns>
    public PreLoginOptions ParsePreLogin(byte[] data)
    {
        var options = new PreLoginOptions();
        var offset = 0;

        // Parse option headers (token, offset, length triplets)
        var optionHeaders = new List<(PreLoginOption Token, ushort Offset, ushort Length)>();

        while (offset < data.Length)
        {
            var token = (PreLoginOption)data[offset];
            if (token == PreLoginOption.Terminator)
                break;

            var optOffset = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 1, 2));
            var optLength = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 3, 2));
            optionHeaders.Add((token, optOffset, optLength));
            offset += 5;
        }

        // Parse option values
        foreach (var (token, optOffset, optLength) in optionHeaders)
        {
            if (optOffset + optLength > data.Length)
                continue;

            var optData = data.AsSpan(optOffset, optLength);

            switch (token)
            {
                case PreLoginOption.Version:
                    if (optLength >= 6)
                    {
                        options.ClientVersion = new Version(
                            optData[0], optData[1],
                            BinaryPrimitives.ReadUInt16BigEndian(optData.Slice(2, 2)),
                            BinaryPrimitives.ReadUInt16BigEndian(optData.Slice(4, 2)));
                    }
                    break;

                case PreLoginOption.Encryption:
                    if (optLength >= 1)
                    {
                        options.Encryption = (TdsEncryption)optData[0];
                    }
                    break;

                case PreLoginOption.Instance:
                    options.InstanceName = Encoding.ASCII.GetString(optData).TrimEnd('\0');
                    break;

                case PreLoginOption.ThreadId:
                    if (optLength >= 4)
                    {
                        options.ThreadId = BinaryPrimitives.ReadUInt32BigEndian(optData);
                    }
                    break;

                case PreLoginOption.Mars:
                    if (optLength >= 1)
                    {
                        options.Mars = optData[0] != 0;
                    }
                    break;

                case PreLoginOption.TraceId:
                    if (optLength >= 36)
                    {
                        options.TraceId = new Guid(optData.Slice(0, 16).ToArray());
                        options.ActivityId = new Guid(optData.Slice(16, 16).ToArray());
                        options.ActivitySequence = BinaryPrimitives.ReadUInt32LittleEndian(optData.Slice(32, 4));
                    }
                    break;

                case PreLoginOption.FedAuthRequired:
                    if (optLength >= 1)
                    {
                        options.FedAuthRequired = optData[0] != 0;
                    }
                    break;

                case PreLoginOption.Nonce:
                    options.Nonce = optData.ToArray();
                    break;
            }
        }

        return options;
    }

    /// <summary>
    /// Parses a LOGIN7 message.
    /// </summary>
    /// <param name="data">The LOGIN7 message data.</param>
    /// <returns>Parsed login data.</returns>
    public Login7Data ParseLogin7(byte[] data)
    {
        if (data.Length < 36)
        {
            throw new InvalidDataException("LOGIN7 message too short");
        }

        var login = new Login7Data();
        var offset = 0;

        // Fixed portion (36 bytes minimum)
        var length = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
        offset += 4;

        var tdsVersion = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
        offset += 4;

        var packetSize = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
        offset += 4;

        var clientProgVer = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
        offset += 4;

        var clientPid = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
        offset += 4;

        var connectionId = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
        offset += 4;

        var optionFlags1 = data[offset++];
        var optionFlags2 = data[offset++];
        var typeFlags = data[offset++];
        var optionFlags3 = data[offset++];

        var clientTimeZone = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
        offset += 4;

        var clientLcid = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
        offset += 4;

        // Variable length fields offsets and lengths (each is 2+2 bytes)
        var hostnameOffset = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
        var hostnameLength = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 2, 2));
        offset += 4;

        var usernameOffset = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
        var usernameLength = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 2, 2));
        offset += 4;

        var passwordOffset = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
        var passwordLength = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 2, 2));
        offset += 4;

        var appNameOffset = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
        var appNameLength = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 2, 2));
        offset += 4;

        var serverNameOffset = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
        var serverNameLength = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 2, 2));
        offset += 4;

        // Skip unused/extension field
        offset += 4;

        var libraryNameOffset = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
        var libraryNameLength = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 2, 2));
        offset += 4;

        var languageOffset = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
        var languageLength = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 2, 2));
        offset += 4;

        var databaseOffset = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
        var databaseLength = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 2, 2));
        offset += 4;

        // Client MAC address (6 bytes)
        var clientMac = new byte[6];
        Array.Copy(data, offset, clientMac, 0, 6);
        offset += 6;

        var attachDbFileOffset = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
        var attachDbFileLength = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 2, 2));
        offset += 4;

        var newPasswordOffset = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
        var newPasswordLength = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 2, 2));
        offset += 4;

        // SSPI length (for Windows auth)
        var sspiLength = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
        offset += 4;

        // Read variable-length strings
        string ReadUnicodeString(int strOffset, int charLength)
        {
            if (charLength == 0 || strOffset + charLength * 2 > data.Length)
                return "";
            return Encoding.Unicode.GetString(data, strOffset, charLength * 2);
        }

        string DecodePassword(int pwdOffset, int charLength)
        {
            if (charLength == 0 || pwdOffset + charLength * 2 > data.Length)
                return "";

            var pwdBytes = new byte[charLength * 2];
            Array.Copy(data, pwdOffset, pwdBytes, 0, charLength * 2);

            // TDS password is XOR'd with 0xA5 and then nibble-swapped
            for (var i = 0; i < pwdBytes.Length; i++)
            {
                pwdBytes[i] ^= 0xA5;
                pwdBytes[i] = (byte)(((pwdBytes[i] & 0x0F) << 4) | ((pwdBytes[i] & 0xF0) >> 4));
            }

            return Encoding.Unicode.GetString(pwdBytes);
        }

        return new Login7Data
        {
            TdsVersion = tdsVersion,
            PacketSize = packetSize,
            ClientProgVer = clientProgVer,
            ClientPid = clientPid,
            ConnectionId = connectionId,
            OptionFlags1 = optionFlags1,
            OptionFlags2 = optionFlags2,
            TypeFlags = typeFlags,
            OptionFlags3 = optionFlags3,
            ClientTimeZone = clientTimeZone,
            ClientLcid = clientLcid,
            Hostname = ReadUnicodeString(hostnameOffset, hostnameLength),
            Username = ReadUnicodeString(usernameOffset, usernameLength),
            Password = DecodePassword(passwordOffset, passwordLength),
            ApplicationName = ReadUnicodeString(appNameOffset, appNameLength),
            ServerName = ReadUnicodeString(serverNameOffset, serverNameLength),
            LibraryName = ReadUnicodeString(libraryNameOffset, libraryNameLength),
            Language = ReadUnicodeString(languageOffset, languageLength),
            Database = ReadUnicodeString(databaseOffset, databaseLength),
            ClientMac = clientMac,
            AttachDbFile = ReadUnicodeString(attachDbFileOffset, attachDbFileLength),
            NewPassword = DecodePassword(newPasswordOffset, newPasswordLength)
        };
    }

    /// <summary>
    /// Parses an SQL batch message.
    /// </summary>
    /// <param name="data">The SQL batch message data.</param>
    /// <returns>The SQL query string.</returns>
    public string ParseSqlBatch(byte[] data)
    {
        if (data.Length == 0)
            return "";

        // SQL batch may have a header for ALL_HEADERS
        var offset = 0;

        // Check for ALL_HEADERS (starts with total length)
        if (data.Length >= 4)
        {
            var totalLength = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0, 4));
            if (totalLength > 4 && totalLength <= data.Length)
            {
                // Skip headers
                offset = totalLength;
            }
        }

        // Rest is Unicode SQL text
        if (offset >= data.Length)
            return "";

        return Encoding.Unicode.GetString(data, offset, data.Length - offset);
    }

    /// <summary>
    /// Parses an RPC request message.
    /// </summary>
    /// <param name="data">The RPC message data.</param>
    /// <returns>Parsed RPC request.</returns>
    public RpcRequest ParseRpcRequest(byte[] data)
    {
        var request = new RpcRequest();
        var offset = 0;

        // Skip ALL_HEADERS if present
        if (data.Length >= 4)
        {
            var totalLength = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0, 4));
            if (totalLength > 4 && totalLength <= data.Length && totalLength < 10000)
            {
                offset = totalLength;
            }
        }

        if (offset >= data.Length)
            return request;

        // Procedure name or ID
        var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
        offset += 2;

        if (nameLength == 0xFFFF)
        {
            // Special stored procedure by ID
            var procId = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
            offset += 2;
            request.ProcedureId = procId;
            request.ProcedureName = GetSpecialProcName(procId);
        }
        else
        {
            // Procedure name as Unicode string
            request.ProcedureName = Encoding.Unicode.GetString(data, offset, nameLength * 2);
            offset += nameLength * 2;
        }

        // Option flags
        if (offset < data.Length)
        {
            request.OptionFlags = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
            offset += 2;
        }

        // Parse parameters
        while (offset < data.Length)
        {
            var param = ParseRpcParameter(data, ref offset);
            if (param == null)
                break;
            request.Parameters.Add(param);
        }

        return request;
    }

    /// <summary>
    /// Gets the special stored procedure name for a given ID.
    /// </summary>
    private static string GetSpecialProcName(ushort procId)
    {
        return procId switch
        {
            1 => "sp_cursor",
            2 => "sp_cursoropen",
            3 => "sp_cursorprepare",
            4 => "sp_cursorexecute",
            5 => "sp_cursorprepexec",
            6 => "sp_cursorunprepare",
            7 => "sp_cursorfetch",
            8 => "sp_cursoroption",
            9 => "sp_cursorclose",
            10 => "sp_executesql",
            11 => "sp_prepare",
            12 => "sp_execute",
            13 => "sp_prepexec",
            14 => "sp_prepexecrpc",
            15 => "sp_unprepare",
            _ => $"sp_unknown_{procId}"
        };
    }

    /// <summary>
    /// Parses a single RPC parameter.
    /// </summary>
    private RpcParameter? ParseRpcParameter(byte[] data, ref int offset)
    {
        if (offset >= data.Length)
            return null;

        var param = new RpcParameter();

        // Parameter name length (in characters)
        var nameLen = data[offset++];
        if (nameLen > 0 && offset + nameLen * 2 <= data.Length)
        {
            param.Name = Encoding.Unicode.GetString(data, offset, nameLen * 2);
            offset += nameLen * 2;
        }

        if (offset >= data.Length)
            return param;

        // Status flags
        param.StatusFlags = data[offset++];

        if (offset >= data.Length)
            return param;

        // Type info and value
        var typeId = data[offset++];
        param.TypeId = typeId;

        // Parse type-specific metadata and value
        ParseParameterValue(data, ref offset, param, typeId);

        return param;
    }

    /// <summary>
    /// Parses parameter value based on type.
    /// </summary>
    private void ParseParameterValue(byte[] data, ref int offset, RpcParameter param, byte typeId)
    {
        // Handle different type categories
        switch (typeId)
        {
            case (byte)TdsDataType.Int:
                if (offset + 4 <= data.Length)
                {
                    param.Value = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
                    offset += 4;
                }
                break;

            case (byte)TdsDataType.BigInt:
                if (offset + 8 <= data.Length)
                {
                    param.Value = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(offset, 8));
                    offset += 8;
                }
                break;

            case (byte)TdsDataType.SmallInt:
                if (offset + 2 <= data.Length)
                {
                    param.Value = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(offset, 2));
                    offset += 2;
                }
                break;

            case (byte)TdsDataType.TinyInt:
                if (offset < data.Length)
                {
                    param.Value = data[offset++];
                }
                break;

            case (byte)TdsDataType.Bit:
                if (offset < data.Length)
                {
                    param.Value = data[offset++] != 0;
                }
                break;

            case (byte)TdsDataType.IntN:
            case (byte)TdsDataType.BitN:
            case (byte)TdsDataType.FloatN:
            case (byte)TdsDataType.MoneyN:
            case (byte)TdsDataType.DateTimeN:
                ParseNullableFixedType(data, ref offset, param, typeId);
                break;

            case (byte)TdsDataType.NVarChar:
            case (byte)TdsDataType.VarChar:
            case (byte)TdsDataType.NChar:
            case (byte)TdsDataType.Char:
                ParseStringType(data, ref offset, param, typeId);
                break;

            case (byte)TdsDataType.VarBinary:
            case (byte)TdsDataType.Binary:
                ParseBinaryType(data, ref offset, param);
                break;

            case (byte)TdsDataType.UniqueIdentifier:
                if (offset + 16 <= data.Length)
                {
                    // Skip length byte for nullable GUID
                    var len = data[offset++];
                    if (len == 16)
                    {
                        param.Value = new Guid(data.AsSpan(offset, 16));
                        offset += 16;
                    }
                }
                break;

            case (byte)TdsDataType.DecimalN:
            case (byte)TdsDataType.NumericN:
                ParseDecimalType(data, ref offset, param);
                break;

            default:
                // Skip unknown type - try to read length and skip
                if (offset < data.Length)
                {
                    var len = data[offset++];
                    if (len < 255 && offset + len <= data.Length)
                    {
                        offset += len;
                    }
                }
                break;
        }
    }

    /// <summary>
    /// Parses nullable fixed-length types (INTN, BITN, etc.).
    /// </summary>
    private void ParseNullableFixedType(byte[] data, ref int offset, RpcParameter param, byte typeId)
    {
        if (offset >= data.Length)
            return;

        var maxLen = data[offset++];

        if (offset >= data.Length)
            return;

        var actualLen = data[offset++];
        if (actualLen == 0)
        {
            param.Value = null;
            return;
        }

        if (offset + actualLen > data.Length)
            return;

        switch (typeId)
        {
            case (byte)TdsDataType.IntN:
                param.Value = actualLen switch
                {
                    1 => (int)data[offset],
                    2 => BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(offset, 2)),
                    4 => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4)),
                    8 => BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(offset, 8)),
                    _ => null
                };
                break;

            case (byte)TdsDataType.BitN:
                param.Value = data[offset] != 0;
                break;

            case (byte)TdsDataType.FloatN:
                param.Value = actualLen switch
                {
                    4 => BitConverter.ToSingle(data, offset),
                    8 => BitConverter.ToDouble(data, offset),
                    _ => null
                };
                break;
        }

        offset += actualLen;
    }

    /// <summary>
    /// Parses string types (VARCHAR, NVARCHAR, etc.).
    /// </summary>
    private void ParseStringType(byte[] data, ref int offset, RpcParameter param, byte typeId)
    {
        if (offset + 2 > data.Length)
            return;

        var maxLen = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
        offset += 2;

        // Skip collation for string types (5 bytes)
        if (offset + 5 <= data.Length)
        {
            offset += 5;
        }

        if (offset + 2 > data.Length)
            return;

        var actualLen = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
        offset += 2;

        if (actualLen == 0xFFFF)
        {
            param.Value = null;
            return;
        }

        if (offset + actualLen > data.Length)
            return;

        var isUnicode = typeId == (byte)TdsDataType.NVarChar || typeId == (byte)TdsDataType.NChar;
        param.Value = isUnicode
            ? Encoding.Unicode.GetString(data, offset, actualLen)
            : Encoding.ASCII.GetString(data, offset, actualLen);

        offset += actualLen;
    }

    /// <summary>
    /// Parses binary types (VARBINARY, BINARY).
    /// </summary>
    private void ParseBinaryType(byte[] data, ref int offset, RpcParameter param)
    {
        if (offset + 2 > data.Length)
            return;

        var maxLen = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
        offset += 2;

        if (offset + 2 > data.Length)
            return;

        var actualLen = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
        offset += 2;

        if (actualLen == 0xFFFF)
        {
            param.Value = null;
            return;
        }

        if (offset + actualLen > data.Length)
            return;

        var bytes = new byte[actualLen];
        Array.Copy(data, offset, bytes, 0, actualLen);
        param.Value = bytes;
        offset += actualLen;
    }

    /// <summary>
    /// Parses decimal/numeric types.
    /// </summary>
    private void ParseDecimalType(byte[] data, ref int offset, RpcParameter param)
    {
        if (offset + 3 > data.Length)
            return;

        var maxLen = data[offset++];
        var precision = data[offset++];
        var scale = data[offset++];

        if (offset >= data.Length)
            return;

        var actualLen = data[offset++];
        if (actualLen == 0)
        {
            param.Value = null;
            return;
        }

        if (offset + actualLen > data.Length)
            return;

        // Parse decimal value
        var sign = data[offset++];
        var bytes = new byte[actualLen - 1];
        Array.Copy(data, offset, bytes, 0, actualLen - 1);
        offset += actualLen - 1;

        // Convert to decimal
        var intValue = new System.Numerics.BigInteger(bytes, isUnsigned: true, isBigEndian: false);
        var divisor = System.Numerics.BigInteger.Pow(10, scale);
        var result = (decimal)intValue / (decimal)divisor;
        param.Value = sign == 0 ? -result : result;
    }

    /// <summary>
    /// Parses a TDS packet header.
    /// </summary>
    private static TdsPacketHeader ParseHeader(byte[] buffer)
    {
        return new TdsPacketHeader
        {
            PacketType = (TdsPacketType)buffer[0],
            Status = (TdsPacketStatus)buffer[1],
            Length = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(2, 2)),
            Spid = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(4, 2)),
            PacketId = buffer[6],
            Window = buffer[7]
        };
    }

    /// <summary>
    /// Reads exactly the specified number of bytes from the stream.
    /// </summary>
    private async Task ReadExactAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        var totalRead = 0;
        while (totalRead < count)
        {
            var read = await _stream.ReadAsync(buffer.AsMemory(offset + totalRead, count - totalRead), ct);
            if (read == 0)
            {
                throw new EndOfStreamException("Connection closed while reading TDS packet");
            }
            totalRead += read;
        }
    }

    /// <summary>
    /// Throws if the reader has been disposed.
    /// </summary>
    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(TdsPacketReader));
        }
    }

    /// <summary>
    /// Disposes the reader.
    /// </summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
        }
    }
}

/// <summary>
/// TDS packet header structure.
/// </summary>
public readonly struct TdsPacketHeader
{
    /// <summary>Packet type.</summary>
    public TdsPacketType PacketType { get; init; }

    /// <summary>Status flags.</summary>
    public TdsPacketStatus Status { get; init; }

    /// <summary>Total packet length including header.</summary>
    public ushort Length { get; init; }

    /// <summary>Server process ID.</summary>
    public ushort Spid { get; init; }

    /// <summary>Packet sequence number.</summary>
    public byte PacketId { get; init; }

    /// <summary>Window (unused, always 0).</summary>
    public byte Window { get; init; }
}

/// <summary>
/// Complete TDS message (may span multiple packets).
/// </summary>
public sealed class TdsPacketMessage
{
    /// <summary>Message type.</summary>
    public TdsPacketType PacketType { get; init; }

    /// <summary>Complete message data.</summary>
    public byte[] Data { get; init; } = Array.Empty<byte>();
}

/// <summary>
/// PRELOGIN options parsed from client.
/// </summary>
public sealed class PreLoginOptions
{
    /// <summary>Client TDS version.</summary>
    public Version ClientVersion { get; set; } = new(0, 0, 0, 0);

    /// <summary>Encryption preference.</summary>
    public TdsEncryption Encryption { get; set; } = TdsEncryption.Off;

    /// <summary>Instance name.</summary>
    public string InstanceName { get; set; } = "";

    /// <summary>Thread ID.</summary>
    public uint ThreadId { get; set; }

    /// <summary>MARS enabled.</summary>
    public bool Mars { get; set; }

    /// <summary>Trace ID.</summary>
    public Guid TraceId { get; set; }

    /// <summary>Activity ID.</summary>
    public Guid ActivityId { get; set; }

    /// <summary>Activity sequence.</summary>
    public uint ActivitySequence { get; set; }

    /// <summary>Federated auth required.</summary>
    public bool FedAuthRequired { get; set; }

    /// <summary>Encryption nonce.</summary>
    public byte[]? Nonce { get; set; }
}

/// <summary>
/// RPC request data.
/// </summary>
public sealed class RpcRequest
{
    /// <summary>Procedure name.</summary>
    public string ProcedureName { get; set; } = "";

    /// <summary>Procedure ID (for special procedures).</summary>
    public ushort? ProcedureId { get; set; }

    /// <summary>Option flags.</summary>
    public ushort OptionFlags { get; set; }

    /// <summary>Parameters.</summary>
    public List<RpcParameter> Parameters { get; } = new();
}

/// <summary>
/// RPC parameter data.
/// </summary>
public sealed class RpcParameter
{
    /// <summary>Parameter name.</summary>
    public string Name { get; set; } = "";

    /// <summary>Status flags.</summary>
    public byte StatusFlags { get; set; }

    /// <summary>Type ID.</summary>
    public byte TypeId { get; set; }

    /// <summary>Parameter value.</summary>
    public object? Value { get; set; }

    /// <summary>Whether this is an output parameter.</summary>
    public bool IsOutput => (StatusFlags & 0x01) != 0;
}
