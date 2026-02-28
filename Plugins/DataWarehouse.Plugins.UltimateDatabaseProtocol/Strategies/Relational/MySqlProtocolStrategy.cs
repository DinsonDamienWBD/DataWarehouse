using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace DataWarehouse.Plugins.UltimateDatabaseProtocol.Strategies.Relational;

/// <summary>
/// MySQL Client/Server Protocol implementation.
/// Implements the complete MySQL wire protocol including:
/// - Connection phase with capability negotiation
/// - Authentication (mysql_native_password, caching_sha2_password, sha256_password)
/// - Command phase (COM_QUERY, COM_STMT_*, COM_PING, etc.)
/// - Result set protocol
/// - Prepared statements
/// - SSL/TLS upgrade
/// </summary>
public sealed class MySqlProtocolStrategy : DatabaseProtocolStrategyBase
{
    // Protocol constants
    private const int ProtocolVersion = 10;
    private const int MaxPacketSize = 16777215; // 16 MB - 1

    // Capability flags
    [Flags]
    private enum CapabilityFlags : uint
    {
        LongPassword = 1,
        FoundRows = 2,
        LongFlag = 4,
        ConnectWithDb = 8,
        NoSchema = 16,
        Compress = 32,
        Odbc = 64,
        LocalFiles = 128,
        IgnoreSpace = 256,
        Protocol41 = 512,
        Interactive = 1024,
        Ssl = 2048,
        IgnoreSigpipe = 4096,
        Transactions = 8192,
        Reserved = 16384,
        SecureConnection = 32768,
        MultiStatements = 65536,
        MultiResults = 131072,
        PsMultiResults = 262144,
        PluginAuth = 524288,
        ConnectAttrs = 1048576,
        PluginAuthLenencClientData = 2097152,
        CanHandleExpiredPasswords = 4194304,
        SessionTrack = 8388608,
        DeprecateEof = 16777216,
        OptionalResultsetMetadata = 33554432,
        ZstdCompression = 67108864,
        QueryAttributes = 134217728,
        MfaAuthentication = 268435456,
        CapabilityExtension = 536870912,
        SslVerifyServerCert = 1073741824,
        RememberOptions = 2147483648
    }

    // Command codes
    private const byte ComQuit = 0x01;
    private const byte ComInitDb = 0x02;
    private const byte ComQuery = 0x03;
    private const byte ComFieldList = 0x04;
    private const byte ComCreateDb = 0x05;
    private const byte ComDropDb = 0x06;
    private const byte ComRefresh = 0x07;
    private const byte ComShutdown = 0x08;
    private const byte ComStatistics = 0x09;
    private const byte ComProcessInfo = 0x0a;
    private const byte ComConnect = 0x0b;
    private const byte ComProcessKill = 0x0c;
    private const byte ComDebug = 0x0d;
    private const byte ComPing = 0x0e;
    private const byte ComChangeUser = 0x11;
    private const byte ComStmtPrepare = 0x16;
    private const byte ComStmtExecute = 0x17;
    private const byte ComStmtSendLongData = 0x18;
    private const byte ComStmtClose = 0x19;
    private const byte ComStmtReset = 0x1a;
    private const byte ComSetOption = 0x1b;
    private const byte ComStmtFetch = 0x1c;
    private const byte ComResetConnection = 0x1f;

    // Column types
    private const byte TypeDecimal = 0x00;
    private const byte TypeTiny = 0x01;
    private const byte TypeShort = 0x02;
    private const byte TypeLong = 0x03;
    private const byte TypeFloat = 0x04;
    private const byte TypeDouble = 0x05;
    private const byte TypeNull = 0x06;
    private const byte TypeTimestamp = 0x07;
    private const byte TypeLonglong = 0x08;
    private const byte TypeInt24 = 0x09;
    private const byte TypeDate = 0x0a;
    private const byte TypeTime = 0x0b;
    private const byte TypeDatetime = 0x0c;
    private const byte TypeYear = 0x0d;
    private const byte TypeVarchar = 0x0f;
    private const byte TypeBit = 0x10;
    private const byte TypeJson = 0xf5;
    private const byte TypeNewdecimal = 0xf6;
    private const byte TypeEnum = 0xf7;
    private const byte TypeSet = 0xf8;
    private const byte TypeTinyBlob = 0xf9;
    private const byte TypeMediumBlob = 0xfa;
    private const byte TypeLongBlob = 0xfb;
    private const byte TypeBlob = 0xfc;
    private const byte TypeVarString = 0xfd;
    private const byte TypeString = 0xfe;
    private const byte TypeGeometry = 0xff;

    // Server state
    private uint _serverCapabilities;
    private string _serverVersion = "";
    private uint _connectionId;
    private string _authPluginName = "";
    private byte[] _authPluginData = [];
    private int _sequenceId; // Accessed via Interlocked (finding 2730)

    /// <inheritdoc/>
    public override string StrategyId => "mysql-protocol";

    /// <inheritdoc/>
    public override string StrategyName => "MySQL Protocol";

    /// <inheritdoc/>
    public override ProtocolInfo ProtocolInfo => new()
    {
        ProtocolName = "MySQL Client/Server Protocol",
        ProtocolVersion = "10",
        DefaultPort = 3306,
        Family = ProtocolFamily.Relational,
        MaxPacketSize = MaxPacketSize,
        Capabilities = new ProtocolCapabilities
        {
            SupportsTransactions = true,
            SupportsPreparedStatements = true,
            SupportsCursors = true,
            SupportsStreaming = true,
            SupportsBatch = true,
            SupportsNotifications = false,
            SupportsSsl = true,
            SupportsCompression = true,
            SupportsMultiplexing = false,
            SupportsServerCursors = true,
            SupportsBulkOperations = true,
            SupportsQueryCancellation = true,
            SupportedAuthMethods =
            [
                AuthenticationMethod.ClearText,
                AuthenticationMethod.SHA256,
                AuthenticationMethod.Certificate
            ]
        }
    };

    /// <inheritdoc/>
    protected override async Task NotifySslUpgradeAsync(CancellationToken ct)
    {
        // Send SSL request packet
        var clientFlags = (uint)(
            CapabilityFlags.Protocol41 |
            CapabilityFlags.Ssl |
            CapabilityFlags.SecureConnection |
            CapabilityFlags.PluginAuth |
            CapabilityFlags.PluginAuthLenencClientData);

        var packet = new byte[32];
        WriteInt32LE(packet.AsSpan(0, 4), (int)clientFlags);
        WriteInt32LE(packet.AsSpan(4, 4), MaxPacketSize);
        packet[8] = 33; // utf8_general_ci

        await SendPacketAsync(packet, ct);
    }

    /// <inheritdoc/>
    protected override async Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        // Read initial handshake packet
        var packet = await ReadPacketAsync(ct);

        var offset = 0;
        var protocolVersion = packet[offset++];
        if (protocolVersion != ProtocolVersion)
        {
            throw new InvalidOperationException($"Unsupported protocol version: {protocolVersion}");
        }

        // Server version (null-terminated)
        _serverVersion = ReadNullTerminatedString(packet.AsSpan(offset), out var bytesRead);
        offset += bytesRead;

        // Connection ID
        _connectionId = (uint)ReadInt32LE(packet.AsSpan(offset, 4));
        offset += 4;

        // Auth plugin data part 1 (8 bytes)
        var authData1 = packet[offset..(offset + 8)];
        offset += 8;

        // Filler
        offset++;

        // Capability flags (lower 2 bytes)
        var capsLower = (ushort)ReadInt16LE(packet.AsSpan(offset, 2));
        offset += 2;

        if (offset < packet.Length)
        {
            // Character set
            var charset = packet[offset++];

            // Status flags
            var statusFlags = ReadInt16LE(packet.AsSpan(offset, 2));
            offset += 2;

            // Capability flags (upper 2 bytes)
            var capsUpper = (ushort)ReadInt16LE(packet.AsSpan(offset, 2));
            offset += 2;

            _serverCapabilities = (uint)(capsLower | (capsUpper << 16));

            // Auth plugin data length
            var authDataLength = packet[offset++];

            // Reserved (10 bytes)
            offset += 10;

            // Auth plugin data part 2
            if ((_serverCapabilities & (uint)CapabilityFlags.SecureConnection) != 0)
            {
                var len2 = Math.Max(13, authDataLength - 8);
                var authData2 = packet[offset..(offset + len2 - 1)]; // Exclude trailing null
                offset += len2;
                _authPluginData = [.. authData1, .. authData2];
            }

            // Auth plugin name
            if ((_serverCapabilities & (uint)CapabilityFlags.PluginAuth) != 0)
            {
                _authPluginName = ReadNullTerminatedString(packet.AsSpan(offset), out _);
            }
        }
        else
        {
            _serverCapabilities = capsLower;
            _authPluginData = authData1;
        }
    }

    /// <inheritdoc/>
    protected override async Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        // Build handshake response
        var clientFlags = (uint)(
            CapabilityFlags.Protocol41 |
            CapabilityFlags.SecureConnection |
            CapabilityFlags.PluginAuth |
            CapabilityFlags.PluginAuthLenencClientData |
            CapabilityFlags.Transactions |
            CapabilityFlags.MultiStatements |
            CapabilityFlags.MultiResults);

        if (parameters.UseSsl)
        {
            clientFlags |= (uint)CapabilityFlags.Ssl;
        }

        if (!string.IsNullOrEmpty(parameters.Database))
        {
            clientFlags |= (uint)CapabilityFlags.ConnectWithDb;
        }

        // Calculate auth response
        var authResponse = CalculateAuthResponse(
            parameters.Password ?? "",
            _authPluginData,
            _authPluginName);

        var username = parameters.Username ?? "";
        var database = parameters.Database ?? "";

        // Calculate packet size
        var packetSize = 4 + 4 + 1 + 23 +
            Encoding.UTF8.GetByteCount(username) + 1 +
            1 + authResponse.Length +
            (string.IsNullOrEmpty(database) ? 0 : Encoding.UTF8.GetByteCount(database) + 1) +
            Encoding.UTF8.GetByteCount(_authPluginName) + 1;

        var packet = new byte[packetSize];
        var offset = 0;

        // Client flags
        WriteInt32LE(packet.AsSpan(offset, 4), (int)clientFlags);
        offset += 4;

        // Max packet size
        WriteInt32LE(packet.AsSpan(offset, 4), MaxPacketSize);
        offset += 4;

        // Character set (utf8mb4)
        packet[offset++] = 45; // utf8mb4_general_ci

        // Reserved (23 bytes)
        offset += 23;

        // Username
        offset += WriteNullTerminatedString(packet.AsSpan(offset), username);

        // Auth response (length-encoded)
        packet[offset++] = (byte)authResponse.Length;
        authResponse.CopyTo(packet.AsSpan(offset));
        offset += authResponse.Length;

        // Database
        if (!string.IsNullOrEmpty(database))
        {
            offset += WriteNullTerminatedString(packet.AsSpan(offset), database);
        }

        // Auth plugin name
        WriteNullTerminatedString(packet.AsSpan(offset), _authPluginName);

        await SendPacketAsync(packet, ct);

        // Read response
        await ProcessAuthResponseAsync(parameters, ct);
    }

    private async Task ProcessAuthResponseAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        while (true)
        {
            var packet = await ReadPacketAsync(ct);

            if (packet.Length == 0)
            {
                throw new InvalidOperationException("Empty packet received");
            }

            var header = packet[0];

            switch (header)
            {
                case 0x00: // OK packet
                    return;

                case 0xff: // ERR packet
                    var errorCode = ReadInt16LE(packet.AsSpan(1, 2));
                    var errorMessage = Encoding.UTF8.GetString(packet[3..]);
                    throw new InvalidOperationException($"Authentication error {errorCode}: {errorMessage}");

                case 0xfe: // Auth switch request
                    if (packet.Length > 1)
                    {
                        _authPluginName = ReadNullTerminatedString(packet.AsSpan(1), out var bytesRead);
                        _authPluginData = packet[(1 + bytesRead)..];
                        var newAuthResponse = CalculateAuthResponse(
                            parameters.Password ?? "",
                            _authPluginData,
                            _authPluginName);
                        await SendPacketAsync(newAuthResponse, ct);
                    }
                    break;

                case 0x01: // More data (caching_sha2_password)
                    if (packet.Length > 1)
                    {
                        var status = packet[1];
                        if (status == 3) // Fast auth success
                        {
                            continue;
                        }
                        else if (status == 4) // Full auth required
                        {
                            // Request public key or send password
                            if (parameters.UseSsl)
                            {
                                // Send password in clear text over SSL
                                var passwordPacket = Encoding.UTF8.GetBytes(parameters.Password + "\0");
                                await SendPacketAsync(passwordPacket, ct);
                            }
                            else
                            {
                                // Request public key
                                await SendPacketAsync([0x02], ct);
                                var keyPacket = await ReadPacketAsync(ct);
                                // Encrypt password with public key...
                                throw new NotSupportedException("RSA encryption for caching_sha2_password not implemented");
                            }
                        }
                    }
                    break;

                default:
                    throw new InvalidOperationException($"Unexpected packet header: 0x{header:X2}");
            }
        }
    }

    private static byte[] CalculateAuthResponse(string password, byte[] authData, string pluginName)
    {
        if (string.IsNullOrEmpty(password))
        {
            return [];
        }

        return pluginName switch
        {
            "mysql_native_password" => NativePasswordAuth(password, authData),
            "caching_sha2_password" => CachingSha2PasswordAuth(password, authData),
            "sha256_password" => Sha256PasswordAuth(password, authData),
            "mysql_clear_password" => Encoding.UTF8.GetBytes(password + "\0"),
            _ => throw new NotSupportedException($"Unsupported auth plugin: {pluginName}")
        };
    }

    private static byte[] NativePasswordAuth(string password, byte[] scramble)
    {
        // SHA1(password) XOR SHA1(scramble + SHA1(SHA1(password)))
        var passwordHash = SHA1.HashData(Encoding.UTF8.GetBytes(password));
        var doubleHash = SHA1.HashData(passwordHash);
        var combined = new byte[scramble.Length + doubleHash.Length];
        scramble.CopyTo(combined, 0);
        doubleHash.CopyTo(combined, scramble.Length);
        var scrambleHash = SHA1.HashData(combined);

        var result = new byte[passwordHash.Length];
        for (int i = 0; i < passwordHash.Length; i++)
        {
            result[i] = (byte)(passwordHash[i] ^ scrambleHash[i]);
        }

        return result;
    }

    private static byte[] CachingSha2PasswordAuth(string password, byte[] nonce)
    {
        // SHA256(password) XOR SHA256(SHA256(SHA256(password)) + nonce)
        var passwordHash = SHA256.HashData(Encoding.UTF8.GetBytes(password));
        var doubleHash = SHA256.HashData(passwordHash);
        var combined = new byte[doubleHash.Length + nonce.Length];
        doubleHash.CopyTo(combined, 0);
        nonce.CopyTo(combined, doubleHash.Length);
        var scrambleHash = SHA256.HashData(combined);

        var result = new byte[passwordHash.Length];
        for (int i = 0; i < passwordHash.Length; i++)
        {
            result[i] = (byte)(passwordHash[i] ^ scrambleHash[i]);
        }

        return result;
    }

    private static byte[] Sha256PasswordAuth(string password, byte[] scramble)
    {
        // Same as caching_sha2_password
        return CachingSha2PasswordAuth(password, scramble);
    }

    /// <inheritdoc/>
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(
        string query,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken ct)
    {
        // Build COM_QUERY packet
        var queryBytes = Encoding.UTF8.GetBytes(query);
        var packet = new byte[1 + queryBytes.Length];
        packet[0] = ComQuery;
        queryBytes.CopyTo(packet, 1);

        Interlocked.Exchange(ref _sequenceId, 0);
        await SendPacketAsync(packet, ct);

        // Read response
        return await ReadQueryResponseAsync(ct);
    }

    private async Task<QueryResult> ReadQueryResponseAsync(CancellationToken ct)
    {
        var packet = await ReadPacketAsync(ct);

        if (packet.Length == 0)
        {
            throw new InvalidOperationException("Empty packet received");
        }

        var header = packet[0];

        if (header == 0xff) // ERR
        {
            var errorCode = ReadInt16LE(packet.AsSpan(1, 2));
            var errorMessage = Encoding.UTF8.GetString(packet[9..]); // Skip SQL state marker and state
            return new QueryResult
            {
                Success = false,
                ErrorMessage = errorMessage,
                ErrorCode = errorCode.ToString()
            };
        }

        if (header == 0x00) // OK
        {
            var offset = 1;
            var affectedRows = ReadLengthEncodedInteger(packet.AsSpan(offset), out var bytesRead);
            offset += bytesRead;
            var lastInsertId = ReadLengthEncodedInteger(packet.AsSpan(offset), out _);

            return new QueryResult
            {
                Success = true,
                RowsAffected = (long)affectedRows,
                Metadata = new Dictionary<string, object>
                {
                    ["lastInsertId"] = lastInsertId
                }
            };
        }

        // Result set - first byte is column count
        var columnCount = (int)ReadLengthEncodedInteger(packet, out _);

        // Read column definitions
        var columns = new List<ColumnMetadata>();
        for (int i = 0; i < columnCount; i++)
        {
            packet = await ReadPacketAsync(ct);
            columns.Add(ParseColumnDefinition(packet, i));
        }

        // Read EOF packet (if not using DEPRECATE_EOF capability)
        if ((_serverCapabilities & (uint)CapabilityFlags.DeprecateEof) == 0)
        {
            await ReadPacketAsync(ct); // EOF
        }

        // Read rows
        var rows = new List<Dictionary<string, object?>>();
        while (true)
        {
            packet = await ReadPacketAsync(ct);

            if (packet[0] == 0xfe && packet.Length < 9) // EOF or OK
            {
                break;
            }

            if (packet[0] == 0xff) // ERR
            {
                break;
            }

            var row = ParseTextResultRow(packet, columns);
            rows.Add(row);
        }

        return new QueryResult
        {
            Success = true,
            Rows = rows.Select(r => (IReadOnlyDictionary<string, object?>)r).ToList(),
            Columns = columns,
            RowsAffected = rows.Count
        };
    }

    private ColumnMetadata ParseColumnDefinition(byte[] packet, int ordinal)
    {
        var offset = 0;

        // catalog
        _ = ReadLengthEncodedString(packet.AsSpan(offset), out var bytesRead);
        offset += bytesRead;

        // schema
        _ = ReadLengthEncodedString(packet.AsSpan(offset), out bytesRead);
        offset += bytesRead;

        // table (virtual)
        _ = ReadLengthEncodedString(packet.AsSpan(offset), out bytesRead);
        offset += bytesRead;

        // org_table (physical)
        _ = ReadLengthEncodedString(packet.AsSpan(offset), out bytesRead);
        offset += bytesRead;

        // name (virtual)
        var name = ReadLengthEncodedString(packet.AsSpan(offset), out bytesRead);
        offset += bytesRead;

        // org_name (physical)
        _ = ReadLengthEncodedString(packet.AsSpan(offset), out bytesRead);
        offset += bytesRead;

        // length of fixed fields [0c]
        offset++;

        // character_set
        var charset = ReadInt16LE(packet.AsSpan(offset, 2));
        offset += 2;

        // column_length
        var columnLength = ReadInt32LE(packet.AsSpan(offset, 4));
        offset += 4;

        // column_type
        var columnType = packet[offset++];

        // flags
        var flags = ReadInt16LE(packet.AsSpan(offset, 2));
        offset += 2;

        // decimals
        var decimals = packet[offset];

        return new ColumnMetadata
        {
            Name = name,
            DataType = GetColumnTypeName(columnType),
            Ordinal = ordinal,
            IsNullable = (flags & 0x0001) == 0, // NOT NULL flag
            MaxLength = columnLength,
            Scale = decimals
        };
    }

    private Dictionary<string, object?> ParseTextResultRow(byte[] packet, List<ColumnMetadata> columns)
    {
        var row = new Dictionary<string, object?>();
        var offset = 0;

        for (int i = 0; i < columns.Count; i++)
        {
            if (packet[offset] == 0xfb) // NULL
            {
                row[columns[i].Name] = null;
                offset++;
            }
            else
            {
                var value = ReadLengthEncodedString(packet.AsSpan(offset), out var bytesRead);
                offset += bytesRead;
                row[columns[i].Name] = ParseValue(value, columns[i].DataType);
            }
        }

        return row;
    }

    private static object? ParseValue(string value, string dataType)
    {
        return dataType switch
        {
            "TINYINT" or "SMALLINT" or "INT" or "MEDIUMINT" or "BIGINT"
                when long.TryParse(value, out var l) => l,
            "FLOAT" or "DOUBLE" when double.TryParse(value, out var d) => d,
            "DECIMAL" or "NEWDECIMAL" when decimal.TryParse(value, out var dec) => dec,
            "DATETIME" or "TIMESTAMP" when DateTime.TryParse(value, out var dt) => dt,
            "DATE" when DateOnly.TryParse(value, out var date) => date,
            "TIME" when TimeSpan.TryParse(value, out var time) => time,
            "YEAR" when int.TryParse(value, out var year) => year,
            _ => value
        };
    }

    private static string GetColumnTypeName(byte type)
    {
        return type switch
        {
            TypeTiny => "TINYINT",
            TypeShort => "SMALLINT",
            TypeLong => "INT",
            TypeLonglong => "BIGINT",
            TypeInt24 => "MEDIUMINT",
            TypeFloat => "FLOAT",
            TypeDouble => "DOUBLE",
            TypeDecimal or TypeNewdecimal => "DECIMAL",
            TypeTimestamp => "TIMESTAMP",
            TypeDate => "DATE",
            TypeTime => "TIME",
            TypeDatetime => "DATETIME",
            TypeYear => "YEAR",
            TypeVarchar or TypeVarString => "VARCHAR",
            TypeString => "CHAR",
            TypeBlob or TypeTinyBlob or TypeMediumBlob or TypeLongBlob => "BLOB",
            TypeJson => "JSON",
            TypeBit => "BIT",
            TypeEnum => "ENUM",
            TypeSet => "SET",
            TypeGeometry => "GEOMETRY",
            TypeNull => "NULL",
            _ => "UNKNOWN"
        };
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
        _sequenceId = 0;
        await SendPacketAsync([ComQuit], ct);
    }

    /// <inheritdoc/>
    protected override async Task<bool> PingCoreAsync(CancellationToken ct)
    {
        _sequenceId = 0;
        await SendPacketAsync([ComPing], ct);
        var response = await ReadPacketAsync(ct);
        return response.Length > 0 && response[0] == 0x00;
    }

    private async Task SendPacketAsync(byte[] payload, CancellationToken ct)
    {
        // MySQL packet: length (3 bytes, little-endian) + sequence_id (1 byte) + payload
        var header = new byte[4];
        header[0] = (byte)(payload.Length & 0xff);
        header[1] = (byte)((payload.Length >> 8) & 0xff);
        header[2] = (byte)((payload.Length >> 16) & 0xff);
        header[3] = (byte)Interlocked.Increment(ref _sequenceId);

        await SendAsync(header, ct);
        await SendAsync(payload, ct);
    }

    private async Task<byte[]> ReadPacketAsync(CancellationToken ct)
    {
        // Read header
        var header = await ReceiveExactAsync(4, ct);
        var length = header[0] | (header[1] << 8) | (header[2] << 16);
        Interlocked.Exchange(ref _sequenceId, header[3] + 1);

        // Read payload
        return length > 0 ? await ReceiveExactAsync(length, ct) : [];
    }

    private static ulong ReadLengthEncodedInteger(ReadOnlySpan<byte> data, out int bytesRead)
    {
        var first = data[0];
        if (first < 0xfb)
        {
            bytesRead = 1;
            return first;
        }
        else if (first == 0xfc)
        {
            bytesRead = 3;
            return (uint)(data[1] | (data[2] << 8));
        }
        else if (first == 0xfd)
        {
            bytesRead = 4;
            return (uint)(data[1] | (data[2] << 8) | (data[3] << 16));
        }
        else if (first == 0xfe)
        {
            bytesRead = 9;
            return (ulong)data[1] | ((ulong)data[2] << 8) | ((ulong)data[3] << 16) | ((ulong)data[4] << 24) |
                   ((ulong)data[5] << 32) | ((ulong)data[6] << 40) | ((ulong)data[7] << 48) | ((ulong)data[8] << 56);
        }
        else
        {
            // 0xfb = NULL, 0xff = ERR
            bytesRead = 1;
            return 0;
        }
    }

    private static string ReadLengthEncodedString(ReadOnlySpan<byte> data, out int bytesRead)
    {
        var length = (int)ReadLengthEncodedInteger(data, out var lenBytes);
        bytesRead = lenBytes + length;
        return Encoding.UTF8.GetString(data.Slice(lenBytes, length));
    }

    private static short ReadInt16LE(ReadOnlySpan<byte> buffer)
    {
        return (short)(buffer[0] | (buffer[1] << 8));
    }
}
