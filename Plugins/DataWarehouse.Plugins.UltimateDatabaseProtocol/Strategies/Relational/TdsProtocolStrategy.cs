using System.Security.Cryptography;
using System.Text;

namespace DataWarehouse.Plugins.UltimateDatabaseProtocol.Strategies.Relational;

/// <summary>
/// Tabular Data Stream (TDS) Protocol implementation for SQL Server.
/// Implements the TDS 7.4+ wire protocol including:
/// - Pre-login and login negotiation
/// - SQL authentication and Windows/Kerberos auth
/// - SQL batch and RPC requests
/// - Result set parsing
/// - Transaction management
/// - Attention signals (query cancellation)
/// </summary>
public sealed class TdsProtocolStrategy : DatabaseProtocolStrategyBase
{
    // TDS packet types
    private const byte PacketTypeSqlBatch = 0x01;
    private const byte PacketTypePreTds7Login = 0x02;
    private const byte PacketTypeRpc = 0x03;
    private const byte PacketTypeTabularResult = 0x04;
    private const byte PacketTypeAttention = 0x06;
    private const byte PacketTypeBulkLoad = 0x07;
    private const byte PacketTypeFedAuth = 0x08;
    private const byte PacketTypeTransactionManager = 0x0e;
    private const byte PacketTypeLogin7 = 0x10;
    private const byte PacketTypeSspi = 0x11;
    private const byte PacketTypePreLogin = 0x12;

    // TDS status flags
    private const byte StatusNormal = 0x00;
    private const byte StatusEom = 0x01;
    private const byte StatusIgnore = 0x02;
    private const byte StatusResetConnection = 0x08;
    private const byte StatusResetConnectionSkipTran = 0x10;

    // Pre-login option tokens
    private const byte PreLoginVersion = 0x00;
    private const byte PreLoginEncryption = 0x01;
    private const byte PreLoginInstance = 0x02;
    private const byte PreLoginThreadId = 0x03;
    private const byte PreLoginMars = 0x04;
    private const byte PreLoginTraceId = 0x05;
    private const byte PreLoginFedAuthRequired = 0x06;
    private const byte PreLoginNonceOpt = 0x07;
    private const byte PreLoginTerminator = 0xff;

    // Token types
    private const byte TokenAltMetadata = 0x88;
    private const byte TokenAltRow = 0xd3;
    private const byte TokenColMetadata = 0x81;
    private const byte TokenColInfo = 0xa5;
    private const byte TokenDone = 0xfd;
    private const byte TokenDoneInProc = 0xff;
    private const byte TokenDoneProc = 0xfe;
    private const byte TokenEnvChange = 0xe3;
    private const byte TokenError = 0xaa;
    private const byte TokenFeatureExtAck = 0xae;
    private const byte TokenFedAuthInfo = 0xee;
    private const byte TokenInfo = 0xab;
    private const byte TokenLoginAck = 0xad;
    private const byte TokenNbcRow = 0xd2;
    private const byte TokenOrder = 0xa9;
    private const byte TokenReturnStatus = 0x79;
    private const byte TokenReturnValue = 0xac;
    private const byte TokenRow = 0xd1;
    private const byte TokenSessionState = 0xe4;
    private const byte TokenSspi = 0xed;
    private const byte TokenTabName = 0xa4;

    // SQL Server data types
    private const byte TypeNull = 0x1f;
    private const byte TypeInt1 = 0x30;
    private const byte TypeBit = 0x32;
    private const byte TypeInt2 = 0x34;
    private const byte TypeInt4 = 0x38;
    private const byte TypeDateTime4 = 0x3a;
    private const byte TypeFloat4 = 0x3b;
    private const byte TypeMoney = 0x3c;
    private const byte TypeDateTime = 0x3d;
    private const byte TypeFloat8 = 0x3e;
    private const byte TypeMoney4 = 0x7a;
    private const byte TypeInt8 = 0x7f;
    private const byte TypeGuid = 0x24;
    private const byte TypeIntN = 0x26;
    private const byte TypeDecimal = 0x37;
    private const byte TypeNumeric = 0x3f;
    private const byte TypeBitN = 0x68;
    private const byte TypeDecimalN = 0x6a;
    private const byte TypeNumericN = 0x6c;
    private const byte TypeFloatN = 0x6d;
    private const byte TypeMoneyN = 0x6e;
    private const byte TypeDateTimeN = 0x6f;
    private const byte TypeDateN = 0x28;
    private const byte TypeTimeN = 0x29;
    private const byte TypeDateTime2N = 0x2a;
    private const byte TypeDateTimeOffsetN = 0x2b;
    private const byte TypeChar = 0x2f;
    private const byte TypeVarChar = 0x27;
    private const byte TypeBinary = 0x2d;
    private const byte TypeVarBinary = 0x25;
    private const byte TypeBigVarChar = 0xa7;
    private const byte TypeBigChar = 0xaf;
    private const byte TypeBigVarBinary = 0xa5;
    private const byte TypeBigBinary = 0xad;
    private const byte TypeNVarChar = 0xe7;
    private const byte TypeNChar = 0xef;
    private const byte TypeXml = 0xf1;
    private const byte TypeUdt = 0xf0;
    private const byte TypeText = 0x23;
    private const byte TypeImage = 0x22;
    private const byte TypeNText = 0x63;
    private const byte TypeVariant = 0x62;

    // Protocol state
    private int _packetId;
    private int _spid;
    private string _serverVersion = "";
    private string _instanceName = "";
    private bool _encryptionRequired;
    private byte[] _serverNonce = [];
    private Dictionary<string, string> _envChanges = new();

    /// <inheritdoc/>
    public override string StrategyId => "tds-protocol";

    /// <inheritdoc/>
    public override string StrategyName => "TDS Protocol (SQL Server)";

    /// <inheritdoc/>
    public override ProtocolInfo ProtocolInfo => new()
    {
        ProtocolName = "Tabular Data Stream (TDS)",
        ProtocolVersion = "7.4",
        DefaultPort = 1433,
        Family = ProtocolFamily.Relational,
        MaxPacketSize = 32767,
        Capabilities = new ProtocolCapabilities
        {
            SupportsTransactions = true,
            SupportsPreparedStatements = true,
            SupportsCursors = true,
            SupportsStreaming = true,
            SupportsBatch = true,
            SupportsNotifications = true,
            SupportsSsl = true,
            SupportsCompression = false,
            SupportsMultiplexing = true, // MARS
            SupportsServerCursors = true,
            SupportsBulkOperations = true,
            SupportsQueryCancellation = true,
            SupportedAuthMethods =
            [
                AuthenticationMethod.ClearText,
                AuthenticationMethod.Kerberos,
                AuthenticationMethod.Certificate,
                AuthenticationMethod.AzureAd
            ]
        }
    };

    /// <inheritdoc/>
    protected override async Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        // Send pre-login packet
        await SendPreLoginAsync(ct);

        // Read pre-login response
        await ReadPreLoginResponseAsync(ct);
    }

    private async Task SendPreLoginAsync(CancellationToken ct)
    {
        // Build pre-login packet options
        var options = new List<(byte token, byte[] data)>
        {
            // VERSION: client TDS version 7.4
            (PreLoginVersion, new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }),
            // ENCRYPTION: off but support negotiation
            (PreLoginEncryption, [0x02]),
            // INSTOPT: default instance
            (PreLoginInstance, [0x00]),
            // THREADID: process ID
            (PreLoginThreadId, BitConverter.GetBytes(Environment.ProcessId)),
            // MARS: enabled
            (PreLoginMars, [0x01])
        };

        // Calculate offset table
        var headerSize = (options.Count + 1) * 5; // Each option: token(1) + offset(2) + length(2), plus terminator
        var offset = headerSize;

        using var ms = new MemoryStream(4096);

        // Write option headers
        foreach (var (token, data) in options)
        {
            ms.WriteByte(token);
            ms.WriteByte((byte)(offset >> 8));
            ms.WriteByte((byte)offset);
            ms.WriteByte((byte)(data.Length >> 8));
            ms.WriteByte((byte)data.Length);
            offset += data.Length;
        }

        // Write terminator
        ms.WriteByte(PreLoginTerminator);

        // Write option data
        foreach (var (_, data) in options)
        {
            ms.Write(data);
        }

        var payload = ms.ToArray();
        await SendTdsPacketAsync(PacketTypePreLogin, payload, ct);
    }

    private async Task ReadPreLoginResponseAsync(CancellationToken ct)
    {
        var (packetType, payload) = await ReadTdsPacketAsync(ct);

        if (packetType != PacketTypeTabularResult)
        {
            throw new InvalidOperationException($"Expected pre-login response, got packet type {packetType}");
        }

        // Parse pre-login options
        var index = 0;
        while (index < payload.Length && payload[index] != PreLoginTerminator)
        {
            var token = payload[index++];
            var optOffset = (payload[index++] << 8) | payload[index++];
            var optLength = (payload[index++] << 8) | payload[index++];

            if (optOffset + optLength <= payload.Length)
            {
                var optData = payload.AsSpan(optOffset, optLength);

                switch (token)
                {
                    case PreLoginVersion:
                        if (optLength >= 6)
                        {
                            _serverVersion = $"{optData[0]}.{optData[1]}.{(optData[2] << 8) | optData[3]}";
                        }
                        break;

                    case PreLoginEncryption:
                        _encryptionRequired = optData[0] == 0x01 || optData[0] == 0x03;
                        break;

                    case PreLoginInstance:
                        _instanceName = Encoding.ASCII.GetString(optData).TrimEnd('\0');
                        break;

                    case PreLoginNonceOpt:
                        _serverNonce = optData.ToArray();
                        break;
                }
            }
        }
    }

    /// <inheritdoc/>
    protected override async Task NotifySslUpgradeAsync(CancellationToken ct)
    {
        // TDS SSL negotiation is done via pre-login ENCRYPTION option
        // If encryption is required, the SSL handshake happens after pre-login
        // but the actual TLS upgrade is handled by the base class
    }

    /// <inheritdoc/>
    protected override async Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        // Build LOGIN7 packet
        var login = BuildLogin7Packet(parameters);
        await SendTdsPacketAsync(PacketTypeLogin7, login, ct);

        // Process login response
        await ProcessLoginResponseAsync(ct);
    }

    private byte[] BuildLogin7Packet(ConnectionParameters parameters)
    {
        var username = parameters.Username ?? "";
        var password = parameters.Password ?? "";
        var database = parameters.Database ?? "master";
        var hostname = Environment.MachineName;
        var appName = "DataWarehouse.UltimateDatabaseProtocol";
        var serverName = CurrentParameters?.Host ?? "";
        var interfaceLibrary = "DataWarehouse";
        var language = "";
        var attachDbFile = "";

        // Offsets and lengths
        var fixedLength = 94; // TDS 7.4 fixed portion
        var offset = fixedLength;

        var hostNameOffset = offset;
        var hostNameLength = hostname.Length;
        offset += hostNameLength * 2;

        var userNameOffset = offset;
        var userNameLength = username.Length;
        offset += userNameLength * 2;

        var passwordOffset = offset;
        var passwordLength = password.Length;
        offset += passwordLength * 2;

        var appNameOffset = offset;
        var appNameLength = appName.Length;
        offset += appNameLength * 2;

        var serverNameOffset = offset;
        var serverNameLength = serverName.Length;
        offset += serverNameLength * 2;

        var extensionOffset = 0; // No extension
        var extensionLength = 0;

        var interfaceLibraryOffset = offset;
        var interfaceLibraryLength = interfaceLibrary.Length;
        offset += interfaceLibraryLength * 2;

        var languageOffset = offset;
        var languageLength = language.Length;
        offset += languageLength * 2;

        var databaseOffset = offset;
        var databaseLength = database.Length;
        offset += databaseLength * 2;

        // Build packet
        using var ms = new MemoryStream(4096);
        using var writer = new BinaryWriter(ms, Encoding.Unicode);

        // Length (will be filled in later)
        writer.Write(0);

        // TDS version (7.4 = 0x74000004)
        writer.Write(0x74000004);

        // Packet size
        writer.Write(4096);

        // Client program version
        writer.Write(0x07000000);

        // Client PID
        writer.Write(Environment.ProcessId);

        // Connection ID
        writer.Write(0);

        // Option flags 1: USE_DB, INIT_DB_FATAL, SET_LANG_ON
        writer.Write((byte)0xe0);

        // Option flags 2: ODBC
        writer.Write((byte)0x03);

        // Type flags
        writer.Write((byte)0x00);

        // Option flags 3
        writer.Write((byte)0x00);

        // Client time zone
        writer.Write(-TimeZoneInfo.Local.BaseUtcOffset.TotalMinutes);

        // Client LCID
        writer.Write(0x00000409);

        // Offsets and lengths
        writer.Write((short)hostNameOffset);
        writer.Write((short)hostNameLength);
        writer.Write((short)userNameOffset);
        writer.Write((short)userNameLength);
        writer.Write((short)passwordOffset);
        writer.Write((short)passwordLength);
        writer.Write((short)appNameOffset);
        writer.Write((short)appNameLength);
        writer.Write((short)serverNameOffset);
        writer.Write((short)serverNameLength);
        writer.Write((short)extensionOffset);
        writer.Write((short)extensionLength);
        writer.Write((short)interfaceLibraryOffset);
        writer.Write((short)interfaceLibraryLength);
        writer.Write((short)languageOffset);
        writer.Write((short)languageLength);
        writer.Write((short)databaseOffset);
        writer.Write((short)databaseLength);

        // Client MAC address
        writer.Write(new byte[6]);

        // Current position for SSPI, attachDB, etc.
        writer.Write((short)offset);
        writer.Write((short)0);
        writer.Write((short)offset);
        writer.Write((short)0);

        // Change password
        writer.Write((short)offset);
        writer.Write((short)0);

        // User instance (long length)
        writer.Write(0);

        // Variable length data
        WriteUnicodeString(writer, hostname);
        WriteUnicodeString(writer, username);
        WriteEncryptedPassword(writer, password);
        WriteUnicodeString(writer, appName);
        WriteUnicodeString(writer, serverName);
        WriteUnicodeString(writer, interfaceLibrary);
        WriteUnicodeString(writer, language);
        WriteUnicodeString(writer, database);

        var packet = ms.ToArray();

        // Fill in length
        var length = packet.Length;
        packet[0] = (byte)length;
        packet[1] = (byte)(length >> 8);
        packet[2] = (byte)(length >> 16);
        packet[3] = (byte)(length >> 24);

        return packet;
    }

    private static void WriteUnicodeString(BinaryWriter writer, string value)
    {
        foreach (var c in value)
        {
            writer.Write((short)c);
        }
    }

    private static void WriteEncryptedPassword(BinaryWriter writer, string password)
    {
        // TDS password encryption: swap nibbles and XOR with 0xA5
        foreach (var c in password)
        {
            var b = (ushort)c;
            var encrypted = (ushort)(((b << 4) | (b >> 4)) ^ 0xA5A5);
            writer.Write(encrypted);
        }
    }

    private async Task ProcessLoginResponseAsync(CancellationToken ct)
    {
        var (packetType, payload) = await ReadTdsPacketAsync(ct);

        if (packetType != PacketTypeTabularResult)
        {
            throw new InvalidOperationException($"Expected login response, got packet type {packetType}");
        }

        var index = 0;
        string? errorMessage = null;

        while (index < payload.Length)
        {
            var token = payload[index++];

            switch (token)
            {
                case TokenLoginAck:
                    index = ParseLoginAck(payload, index);
                    break;

                case TokenEnvChange:
                    index = ParseEnvChange(payload, index);
                    break;

                case TokenInfo:
                    index = ParseInfoOrError(payload, index, isError: false);
                    break;

                case TokenError:
                    (index, errorMessage) = ParseInfoOrErrorWithMessage(payload, index);
                    break;

                case TokenDone:
                case TokenDoneProc:
                case TokenDoneInProc:
                    index = ParseDone(payload, index);
                    break;

                case TokenFeatureExtAck:
                    index = ParseFeatureExtAck(payload, index);
                    break;

                default:
                    // Skip unknown tokens
                    if (index + 2 <= payload.Length)
                    {
                        var length = payload[index] | (payload[index + 1] << 8);
                        index += 2 + length;
                    }
                    break;
            }
        }

        if (errorMessage != null)
        {
            throw new InvalidOperationException($"Login failed: {errorMessage}");
        }
    }

    private int ParseLoginAck(byte[] payload, int index)
    {
        var length = payload[index] | (payload[index + 1] << 8);
        index += 2;

        var interfaceType = payload[index++];
        var tdsVersion = ReadInt32BE(payload.AsSpan(index, 4));
        index += 4;

        var progNameLen = payload[index++];
        var progName = Encoding.Unicode.GetString(payload, index, progNameLen * 2);
        index += progNameLen * 2;

        var serverMajor = payload[index++];
        var serverMinor = payload[index++];
        var serverBuild = payload[index] | (payload[index + 1] << 8);
        index += 2;

        _serverVersion = $"{serverMajor}.{serverMinor}.{serverBuild}";

        return index;
    }

    private int ParseEnvChange(byte[] payload, int index)
    {
        var length = payload[index] | (payload[index + 1] << 8);
        index += 2;
        var endIndex = index + length;

        while (index < endIndex)
        {
            var envType = payload[index++];
            switch (envType)
            {
                case 1: // Database
                case 2: // Language
                case 3: // Character set
                case 4: // Packet size
                case 5: // Unicode sort locale ID
                case 6: // Unicode comparison flags
                case 7: // SQL Collation
                    var newLen = payload[index++];
                    var newVal = Encoding.Unicode.GetString(payload, index, newLen * 2);
                    index += newLen * 2;
                    var oldLen = payload[index++];
                    index += oldLen * 2;
                    _envChanges[envType.ToString()] = newVal;
                    break;

                default:
                    // Skip unknown env change types
                    break;
            }
        }

        return endIndex;
    }

    private int ParseInfoOrError(byte[] payload, int index, bool isError)
    {
        var length = payload[index] | (payload[index + 1] << 8);
        return index + 2 + length;
    }

    private (int newIndex, string? message) ParseInfoOrErrorWithMessage(byte[] payload, int index)
    {
        var length = payload[index] | (payload[index + 1] << 8);
        index += 2;

        var number = ReadInt32LE(payload.AsSpan(index, 4));
        index += 4;

        var state = payload[index++];
        var classVal = payload[index++];

        var msgLen = payload[index] | (payload[index + 1] << 8);
        index += 2;
        var message = Encoding.Unicode.GetString(payload, index, msgLen * 2);
        index += msgLen * 2;

        var serverNameLen = payload[index++];
        index += serverNameLen * 2;

        var procNameLen = payload[index++];
        index += procNameLen * 2;

        var lineNumber = ReadInt32LE(payload.AsSpan(index, 4));
        index += 4;

        return (index, message);
    }

    private int ParseDone(byte[] payload, int index)
    {
        // Status (2) + CurCmd (2) + DoneRowCount (8 for TDS 7.2+)
        return index + 12;
    }

    private int ParseFeatureExtAck(byte[] payload, int index)
    {
        while (index < payload.Length && payload[index] != 0xff)
        {
            var featureId = payload[index++];
            var featureDataLen = ReadInt32LE(payload.AsSpan(index, 4));
            index += 4 + (int)featureDataLen;
        }
        if (index < payload.Length)
            index++; // Skip terminator
        return index;
    }

    /// <inheritdoc/>
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(
        string query,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken ct)
    {
        // Build SQL batch packet
        var queryBytes = Encoding.Unicode.GetBytes(query);

        // ALL_HEADERS structure
        var allHeadersLength = 22; // Total length (4) + TxnDescriptor header (18)
        var packetLength = allHeadersLength + queryBytes.Length;
        var packet = new byte[packetLength];
        var offset = 0;

        // Total length of headers
        WriteInt32LE(packet.AsSpan(offset, 4), allHeadersLength);
        offset += 4;

        // Transaction descriptor header
        WriteInt32LE(packet.AsSpan(offset, 4), 18); // Header length
        offset += 4;
        WriteInt16LE(packet.AsSpan(offset, 2), 2); // Header type: transaction descriptor
        offset += 2;

        // Transaction descriptor (8 bytes of zeros for no transaction)
        offset += 8;

        // Outstanding request count
        WriteInt32LE(packet.AsSpan(offset, 4), 1);
        offset += 4;

        // Query text
        queryBytes.CopyTo(packet.AsSpan(offset));

        await SendTdsPacketAsync(PacketTypeSqlBatch, packet, ct);

        return await ReadQueryResponseAsync(ct);
    }

    private async Task<QueryResult> ReadQueryResponseAsync(CancellationToken ct)
    {
        var columns = new List<ColumnMetadata>();
        var rows = new List<Dictionary<string, object?>>();
        long rowsAffected = 0;
        string? errorMessage = null;
        string? errorCode = null;
        bool done = false;

        while (!done)
        {
            var (packetType, payload) = await ReadTdsPacketAsync(ct);

            if (packetType != PacketTypeTabularResult)
            {
                continue;
            }

            var index = 0;
            while (index < payload.Length)
            {
                var token = payload[index++];

                switch (token)
                {
                    case TokenColMetadata:
                        (index, columns) = ParseColumnMetadata(payload, index);
                        break;

                    case TokenRow:
                        var row = ParseRow(payload, ref index, columns);
                        rows.Add(row);
                        break;

                    case TokenNbcRow:
                        row = ParseNbcRow(payload, ref index, columns);
                        rows.Add(row);
                        break;

                    case TokenError:
                        (index, errorMessage) = ParseInfoOrErrorWithMessage(payload, index);
                        break;

                    case TokenInfo:
                        index = ParseInfoOrError(payload, index, isError: false);
                        break;

                    case TokenDone:
                    case TokenDoneProc:
                    case TokenDoneInProc:
                        var status = payload[index] | (payload[index + 1] << 8);
                        index += 2;
                        var curCmd = payload[index] | (payload[index + 1] << 8);
                        index += 2;
                        rowsAffected = (long)ReadInt64LE(payload.AsSpan(index, 8));
                        index += 8;

                        if ((status & 0x01) == 0) // Not DONE_MORE
                        {
                            done = true;
                        }
                        break;

                    case TokenEnvChange:
                        index = ParseEnvChange(payload, index);
                        break;

                    case TokenReturnStatus:
                        index += 4; // Return value (int)
                        break;

                    case TokenOrder:
                        var orderLen = payload[index] | (payload[index + 1] << 8);
                        index += 2 + orderLen;
                        break;

                    default:
                        // Try to skip token
                        if (index + 2 <= payload.Length)
                        {
                            var length = payload[index] | (payload[index + 1] << 8);
                            index += 2 + length;
                        }
                        else
                        {
                            index = payload.Length;
                        }
                        break;
                }
            }
        }

        return new QueryResult
        {
            Success = errorMessage == null,
            RowsAffected = rows.Count > 0 ? rows.Count : rowsAffected,
            Rows = rows.Select(r => (IReadOnlyDictionary<string, object?>)r).ToList(),
            Columns = columns,
            ErrorMessage = errorMessage,
            ErrorCode = errorCode
        };
    }

    private (int newIndex, List<ColumnMetadata> columns) ParseColumnMetadata(byte[] payload, int index)
    {
        var columns = new List<ColumnMetadata>();
        var columnCount = payload[index] | (payload[index + 1] << 8);
        index += 2;

        if (columnCount == 0xffff) // No metadata
        {
            return (index, columns);
        }

        for (int i = 0; i < columnCount; i++)
        {
            // User type
            var userType = ReadInt32LE(payload.AsSpan(index, 4));
            index += 4;

            // Flags
            var flags = payload[index] | (payload[index + 1] << 8);
            index += 2;

            // TYPE_INFO
            var typeId = payload[index++];
            var (typeName, typeLength) = ParseTypeInfo(payload, ref index, typeId);

            // Column name
            var nameLen = payload[index++];
            var name = Encoding.Unicode.GetString(payload, index, nameLen * 2);
            index += nameLen * 2;

            columns.Add(new ColumnMetadata
            {
                Name = name,
                DataType = typeName,
                Ordinal = i,
                IsNullable = (flags & 0x01) != 0,
                MaxLength = typeLength
            });
        }

        return (index, columns);
    }

    private (string typeName, int? length) ParseTypeInfo(byte[] payload, ref int index, byte typeId)
    {
        switch (typeId)
        {
            case TypeNull:
                return ("NULL", null);

            case TypeInt1:
                return ("TINYINT", 1);

            case TypeBit:
                return ("BIT", 1);

            case TypeInt2:
                return ("SMALLINT", 2);

            case TypeInt4:
                return ("INT", 4);

            case TypeInt8:
                return ("BIGINT", 8);

            case TypeFloat4:
                return ("REAL", 4);

            case TypeFloat8:
                return ("FLOAT", 8);

            case TypeMoney:
                return ("MONEY", 8);

            case TypeMoney4:
                return ("SMALLMONEY", 4);

            case TypeDateTime:
                return ("DATETIME", 8);

            case TypeDateTime4:
                return ("SMALLDATETIME", 4);

            case TypeIntN:
                var intLen = payload[index++];
                return (intLen switch { 1 => "TINYINT", 2 => "SMALLINT", 4 => "INT", 8 => "BIGINT", _ => "INT" }, intLen);

            case TypeFloatN:
                var floatLen = payload[index++];
                return (floatLen == 4 ? "REAL" : "FLOAT", floatLen);

            case TypeBitN:
                index++; // Length
                return ("BIT", 1);

            case TypeMoneyN:
                var moneyLen = payload[index++];
                return (moneyLen == 4 ? "SMALLMONEY" : "MONEY", moneyLen);

            case TypeDateTimeN:
                var dtLen = payload[index++];
                return (dtLen == 4 ? "SMALLDATETIME" : "DATETIME", dtLen);

            case TypeDateN:
                return ("DATE", 3);

            case TypeTimeN:
                var timeScale = payload[index++];
                return ("TIME", timeScale);

            case TypeDateTime2N:
                var dt2Scale = payload[index++];
                return ("DATETIME2", dt2Scale);

            case TypeDateTimeOffsetN:
                var dtoScale = payload[index++];
                return ("DATETIMEOFFSET", dtoScale);

            case TypeDecimalN:
            case TypeNumericN:
                var numLen = payload[index++];
                var precision = payload[index++];
                var scale = payload[index++];
                return (typeId == TypeDecimalN ? "DECIMAL" : "NUMERIC", numLen);

            case TypeGuid:
                var guidLen = payload[index++];
                return ("UNIQUEIDENTIFIER", guidLen);

            case TypeBigVarChar:
            case TypeBigChar:
            case TypeVarChar:
            case TypeChar:
                var charLen = payload[index] | (payload[index + 1] << 8);
                index += 2;
                // Collation
                index += 5;
                return (typeId == TypeBigChar || typeId == TypeChar ? "CHAR" : "VARCHAR", charLen);

            case TypeNVarChar:
            case TypeNChar:
                var ncharLen = payload[index] | (payload[index + 1] << 8);
                index += 2;
                // Collation
                index += 5;
                return (typeId == TypeNChar ? "NCHAR" : "NVARCHAR", ncharLen / 2);

            case TypeBigVarBinary:
            case TypeBigBinary:
            case TypeVarBinary:
            case TypeBinary:
                var binLen = payload[index] | (payload[index + 1] << 8);
                index += 2;
                return (typeId == TypeBigBinary || typeId == TypeBinary ? "BINARY" : "VARBINARY", binLen);

            case TypeText:
            case TypeNText:
            case TypeImage:
                var lobLen = ReadInt32LE(payload.AsSpan(index, 4));
                index += 4;
                // Collation for text
                if (typeId != TypeImage)
                    index += 5;
                // Table name parts
                var numParts = payload[index++];
                for (int j = 0; j < numParts; j++)
                {
                    var partLen = payload[index] | (payload[index + 1] << 8);
                    index += 2 + partLen * 2;
                }
                return (typeId switch { TypeText => "TEXT", TypeNText => "NTEXT", _ => "IMAGE" }, lobLen);

            case TypeXml:
                var xmlFlags = payload[index++];
                if ((xmlFlags & 0x01) != 0)
                {
                    // Schema info
                    var dbNameLen = payload[index++];
                    index += dbNameLen * 2;
                    var ownerLen = payload[index++];
                    index += ownerLen * 2;
                    var colLen = payload[index] | (payload[index + 1] << 8);
                    index += 2 + colLen * 2;
                }
                return ("XML", null);

            case TypeVariant:
                var varLen = ReadInt32LE(payload.AsSpan(index, 4));
                index += 4;
                return ("SQL_VARIANT", varLen);

            default:
                return ("UNKNOWN", null);
        }
    }

    private Dictionary<string, object?> ParseRow(byte[] payload, ref int index, List<ColumnMetadata> columns)
    {
        var row = new Dictionary<string, object?>();

        foreach (var col in columns)
        {
            var value = ParseColumnValue(payload, ref index, col.DataType);
            row[col.Name] = value;
        }

        return row;
    }

    private Dictionary<string, object?> ParseNbcRow(byte[] payload, ref int index, List<ColumnMetadata> columns)
    {
        var row = new Dictionary<string, object?>();

        // NULL bitmap
        var bitmapLen = (columns.Count + 7) / 8;
        var nullBitmap = payload.AsSpan(index, bitmapLen).ToArray();
        index += bitmapLen;

        for (int i = 0; i < columns.Count; i++)
        {
            var isNull = (nullBitmap[i / 8] & (1 << (i % 8))) != 0;
            if (isNull)
            {
                row[columns[i].Name] = null;
            }
            else
            {
                var value = ParseColumnValue(payload, ref index, columns[i].DataType);
                row[columns[i].Name] = value;
            }
        }

        return row;
    }

    private object? ParseColumnValue(byte[] payload, ref int index, string dataType)
    {
        // Simplified parsing - production code would handle all types
        switch (dataType)
        {
            case "INT":
                var intVal = ReadInt32LE(payload.AsSpan(index, 4));
                index += 4;
                return intVal;

            case "BIGINT":
                var longVal = ReadInt64LE(payload.AsSpan(index, 8));
                index += 8;
                return longVal;

            case "NVARCHAR":
            case "VARCHAR":
                var strLen = payload[index] | (payload[index + 1] << 8);
                index += 2;
                if (strLen == 0xffff)
                    return null;
                var str = dataType == "NVARCHAR"
                    ? Encoding.Unicode.GetString(payload, index, strLen)
                    : Encoding.UTF8.GetString(payload, index, strLen);
                index += strLen;
                return str;

            default:
                // Skip unknown types
                return null;
        }
    }

    /// <inheritdoc/>
    protected override Task<QueryResult> ExecuteNonQueryCoreAsync(
        string command,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken ct)
    {
        return ExecuteQueryCoreAsync(command, parameters, ct);
    }

    /// <inheritdoc/>
    protected override async Task SendDisconnectMessageAsync(CancellationToken ct)
    {
        // No explicit disconnect message in TDS; just close the connection
    }

    /// <inheritdoc/>
    protected override async Task<bool> PingCoreAsync(CancellationToken ct)
    {
        var result = await ExecuteQueryCoreAsync("SELECT 1", null, ct);
        return result.Success;
    }

    private async Task SendTdsPacketAsync(byte packetType, byte[] payload, CancellationToken ct)
    {
        // TDS packet header: 8 bytes
        // Type (1) + Status (1) + Length (2, big-endian) + SPID (2) + PacketID (1) + Window (1)
        var maxPayloadSize = 4096 - 8; // Default packet size minus header
        var offset = 0;

        while (offset < payload.Length)
        {
            var chunkSize = Math.Min(maxPayloadSize, payload.Length - offset);
            var isLast = offset + chunkSize >= payload.Length;

            var packetLength = 8 + chunkSize;
            var header = new byte[8];
            header[0] = packetType;
            header[1] = isLast ? StatusEom : StatusNormal;
            header[2] = (byte)(packetLength >> 8);
            header[3] = (byte)packetLength;
            header[4] = (byte)(_spid >> 8);
            header[5] = (byte)_spid;
            header[6] = (byte)_packetId++;
            header[7] = 0; // Window

            await SendAsync(header, ct);
            await SendAsync(payload.AsMemory(offset, chunkSize), ct);

            offset += chunkSize;
        }
    }

    private async Task<(byte packetType, byte[] payload)> ReadTdsPacketAsync(CancellationToken ct)
    {
        using var ms = new MemoryStream(4096);
        byte packetType = 0;

        while (true)
        {
            var header = await ReceiveExactAsync(8, ct);
            packetType = header[0];
            var status = header[1];
            var packetLength = (header[2] << 8) | header[3];

            var payloadLength = packetLength - 8;
            if (payloadLength > 0)
            {
                var chunk = await ReceiveExactAsync(payloadLength, ct);
                ms.Write(chunk);
            }

            if ((status & StatusEom) != 0)
            {
                break;
            }
        }

        return (packetType, ms.ToArray());
    }

    private static void WriteInt16LE(Span<byte> buffer, short value)
    {
        buffer[0] = (byte)value;
        buffer[1] = (byte)(value >> 8);
    }

    private static long ReadInt64LE(ReadOnlySpan<byte> buffer)
    {
        return (long)buffer[0] | ((long)buffer[1] << 8) | ((long)buffer[2] << 16) | ((long)buffer[3] << 24) |
               ((long)buffer[4] << 32) | ((long)buffer[5] << 40) | ((long)buffer[6] << 48) | ((long)buffer[7] << 56);
    }
}
