using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace DataWarehouse.Plugins.UltimateDatabaseProtocol.Strategies.NewSQL;

/// <summary>
/// CockroachDB PostgreSQL wire protocol strategy.
/// CockroachDB uses the PostgreSQL wire protocol with extensions.
/// </summary>
public sealed class CockroachDbProtocolStrategy : DatabaseProtocolStrategyBase
{
    private const int ProtocolVersion3 = 196608; // 3.0
    private int _processId;
    private int _secretKey;
    private string _serverVersion = "";
    private StreamReader? _reader;
    private StreamWriter? _writer;

    /// <inheritdoc/>
    public override string StrategyId => "cockroachdb-pgwire";

    /// <inheritdoc/>
    public override string StrategyName => "CockroachDB PostgreSQL Wire Protocol";

    /// <inheritdoc/>
    public override ProtocolInfo ProtocolInfo => new()
    {
        ProtocolName = "CockroachDB PostgreSQL Wire Protocol",
        ProtocolVersion = "3.0",
        DefaultPort = 26257,
        Family = ProtocolFamily.NewSQL,
        MaxPacketSize = 1024 * 1024 * 1024,
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
            SupportsMultiplexing = false,
            SupportsServerCursors = true,
            SupportsBulkOperations = true,
            SupportsQueryCancellation = true,
            SupportedAuthMethods =
            [
                AuthenticationMethod.ClearText,
                AuthenticationMethod.MD5,
                AuthenticationMethod.ScramSha256,
                AuthenticationMethod.Certificate
            ]
        }
    };

    /// <inheritdoc/>
    protected override async Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        // Build startup message
        var startupParams = new Dictionary<string, string>
        {
            ["user"] = parameters.Username ?? "root",
            ["database"] = parameters.Database ?? "defaultdb",
            ["client_encoding"] = "UTF8",
            ["application_name"] = "DataWarehouse"
        };

        using var ms = new MemoryStream(4096);
        using var bw = new BinaryWriter(ms);

        // Write protocol version
        bw.Write(BinaryPrimitives.ReverseEndianness(ProtocolVersion3));

        // Write parameters
        foreach (var param in startupParams)
        {
            bw.Write(Encoding.UTF8.GetBytes(param.Key));
            bw.Write((byte)0);
            bw.Write(Encoding.UTF8.GetBytes(param.Value));
            bw.Write((byte)0);
        }
        bw.Write((byte)0); // Terminator

        var payload = ms.ToArray();
        var length = payload.Length + 4;

        // Send startup message (no type byte for startup)
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, length);
        await ActiveStream!.WriteAsync(header, ct);
        await ActiveStream.WriteAsync(payload, ct);
        await ActiveStream.FlushAsync(ct);
    }

    /// <inheritdoc/>
    protected override async Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        while (true)
        {
            var (type, data) = await ReadMessageAsync(ct);

            switch ((char)type)
            {
                case 'R': // Authentication
                    await HandleAuthenticationAsync(data, parameters, ct);
                    break;

                case 'K': // BackendKeyData
                    _processId = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(0));
                    _secretKey = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(4));
                    break;

                case 'S': // ParameterStatus
                    ParseParameterStatus(data);
                    break;

                case 'Z': // ReadyForQuery
                    return;

                case 'E': // ErrorResponse
                    throw new Exception($"Authentication failed: {ParseErrorResponse(data)}");
            }
        }
    }

    private async Task HandleAuthenticationAsync(byte[] data, ConnectionParameters parameters, CancellationToken ct)
    {
        var authType = BinaryPrimitives.ReadInt32BigEndian(data);

        switch (authType)
        {
            case 0: // AuthenticationOk
                return;

            case 3: // AuthenticationCleartextPassword
                await SendPasswordAsync(parameters.Password ?? "", ct);
                break;

            case 5: // AuthenticationMD5Password
                var salt = data[4..8];
                var hash = ComputeMd5Password(parameters.Username ?? "", parameters.Password ?? "", salt);
                await SendPasswordAsync(hash, ct);
                break;

            case 10: // AuthenticationSASL
                await PerformScramAuthAsync(data[4..], parameters, ct);
                break;

            default:
                throw new NotSupportedException($"Unsupported authentication type: {authType}");
        }
    }

    private async Task SendPasswordAsync(string password, CancellationToken ct)
    {
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        var length = passwordBytes.Length + 5;

        var buffer = new byte[1 + 4 + passwordBytes.Length + 1];
        buffer[0] = (byte)'p';
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(1), length);
        passwordBytes.CopyTo(buffer.AsSpan(5));
        buffer[^1] = 0;

        await ActiveStream!.WriteAsync(buffer, ct);
        await ActiveStream.FlushAsync(ct);
    }

    private static string ComputeMd5Password(string user, string password, byte[] salt)
    {
        using var md5 = MD5.Create();
        var inner = md5.ComputeHash(Encoding.UTF8.GetBytes(password + user));
        var innerHex = Convert.ToHexString(inner).ToLowerInvariant();
        var outer = md5.ComputeHash(Encoding.UTF8.GetBytes(innerHex).Concat(salt).ToArray());
        return "md5" + Convert.ToHexString(outer).ToLowerInvariant();
    }

    private async Task PerformScramAuthAsync(byte[] mechData, ConnectionParameters parameters, CancellationToken ct)
    {
        // Parse mechanisms
        var mechanisms = new List<string>();
        var pos = 0;
        while (pos < mechData.Length)
        {
            var end = Array.IndexOf(mechData, (byte)0, pos);
            if (end <= pos) break;
            mechanisms.Add(Encoding.UTF8.GetString(mechData, pos, end - pos));
            pos = end + 1;
        }

        if (!mechanisms.Contains("SCRAM-SHA-256"))
            throw new NotSupportedException("Server doesn't support SCRAM-SHA-256");

        // Generate client nonce
        var clientNonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
        var clientFirstBare = $"n=,r={clientNonce}";
        var clientFirst = $"n,,{clientFirstBare}";

        // Send SASLInitialResponse
        await SendSaslInitialResponseAsync("SCRAM-SHA-256", clientFirst, ct);

        // Read SASLContinue
        var (type, data) = await ReadMessageAsync(ct);
        if ((char)type != 'R')
            throw new Exception("Expected authentication response");

        var authType = BinaryPrimitives.ReadInt32BigEndian(data);
        if (authType != 11) // AuthenticationSASLContinue
            throw new Exception($"Expected SASL continue, got {authType}");

        var serverFirst = Encoding.UTF8.GetString(data, 4, data.Length - 4);
        var serverParams = ParseScramMessage(serverFirst);

        var serverNonce = serverParams["r"];
        var salt = Convert.FromBase64String(serverParams["s"]);
        var iterations = int.Parse(serverParams["i"]);

        // Compute client final
        var saltedPassword = ComputeScramSaltedPassword(parameters.Password ?? "", salt, iterations);
        var clientKey = ComputeHmac(saltedPassword, "Client Key");
        var storedKey = SHA256.HashData(clientKey);

        var clientFinalWithoutProof = $"c=biws,r={serverNonce}";
        var authMessage = $"{clientFirstBare},{serverFirst},{clientFinalWithoutProof}";

        var clientSignature = ComputeHmac(storedKey, authMessage);
        var clientProof = new byte[clientKey.Length];
        for (int i = 0; i < clientKey.Length; i++)
            clientProof[i] = (byte)(clientKey[i] ^ clientSignature[i]);

        var clientFinal = $"{clientFinalWithoutProof},p={Convert.ToBase64String(clientProof)}";

        // Send SASLResponse
        await SendSaslResponseAsync(clientFinal, ct);

        // Read SASLFinal
        (type, data) = await ReadMessageAsync(ct);
        if ((char)type == 'E')
            throw new Exception($"SCRAM auth failed: {ParseErrorResponse(data)}");

        if ((char)type == 'R')
        {
            authType = BinaryPrimitives.ReadInt32BigEndian(data);
            if (authType == 12) // AuthenticationSASLFinal
            {
                // Verify server signature (optional)
            }
        }
    }

    private async Task SendSaslInitialResponseAsync(string mechanism, string data, CancellationToken ct)
    {
        var mechBytes = Encoding.UTF8.GetBytes(mechanism);
        var dataBytes = Encoding.UTF8.GetBytes(data);

        var length = 4 + mechBytes.Length + 1 + 4 + dataBytes.Length;
        var buffer = new byte[1 + length];
        buffer[0] = (byte)'p';
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(1), length);

        var pos = 5;
        mechBytes.CopyTo(buffer.AsSpan(pos));
        pos += mechBytes.Length;
        buffer[pos++] = 0;
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(pos), dataBytes.Length);
        pos += 4;
        dataBytes.CopyTo(buffer.AsSpan(pos));

        await ActiveStream!.WriteAsync(buffer, ct);
        await ActiveStream.FlushAsync(ct);
    }

    private async Task SendSaslResponseAsync(string data, CancellationToken ct)
    {
        var dataBytes = Encoding.UTF8.GetBytes(data);
        var length = 4 + dataBytes.Length;

        var buffer = new byte[1 + length];
        buffer[0] = (byte)'p';
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(1), length);
        dataBytes.CopyTo(buffer.AsSpan(5));

        await ActiveStream!.WriteAsync(buffer, ct);
        await ActiveStream.FlushAsync(ct);
    }

    private static Dictionary<string, string> ParseScramMessage(string message)
    {
        var result = new Dictionary<string, string>();
        foreach (var part in message.Split(','))
        {
            var eqIdx = part.IndexOf('=');
            if (eqIdx > 0)
                result[part[..eqIdx]] = part[(eqIdx + 1)..];
        }
        return result;
    }

    private static byte[] ComputeScramSaltedPassword(string password, byte[] salt, int iterations)
    {
        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            32);
    }

    private static byte[] ComputeHmac(byte[] key, string data)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
    }

    private void ParseParameterStatus(byte[] data)
    {
        var parts = Encoding.UTF8.GetString(data).TrimEnd('\0').Split('\0');
        if (parts.Length >= 2 && parts[0] == "server_version")
            _serverVersion = parts[1];
    }

    private static string ParseErrorResponse(byte[] data)
    {
        var message = new StringBuilder();
        var pos = 0;
        while (pos < data.Length && data[pos] != 0)
        {
            var fieldType = (char)data[pos++];
            var end = Array.IndexOf(data, (byte)0, pos);
            if (end < 0) break;
            var value = Encoding.UTF8.GetString(data, pos, end - pos);
            pos = end + 1;

            if (fieldType == 'M')
                message.Append(value);
        }
        return message.ToString();
    }

    private async Task<(byte type, byte[] data)> ReadMessageAsync(CancellationToken ct)
    {
        var header = new byte[5];
        await ActiveStream!.ReadExactlyAsync(header, 0, 5, ct);

        var type = header[0];
        var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1)) - 4;

        var data = new byte[length];
        if (length > 0)
            await ActiveStream.ReadExactlyAsync(data, 0, length, ct);

        return (type, data);
    }

    /// <inheritdoc/>
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(
        string query,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken ct)
    {
        // Send Query message
        var queryBytes = Encoding.UTF8.GetBytes(query);
        var length = queryBytes.Length + 5;

        var buffer = new byte[1 + 4 + queryBytes.Length + 1];
        buffer[0] = (byte)'Q';
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(1), length);
        queryBytes.CopyTo(buffer.AsSpan(5));
        buffer[^1] = 0;

        await ActiveStream!.WriteAsync(buffer, ct);
        await ActiveStream.FlushAsync(ct);

        // Read responses
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        var columns = new List<string>();
        long rowsAffected = 0;
        string? errorMessage = null;

        while (true)
        {
            var (type, data) = await ReadMessageAsync(ct);

            switch ((char)type)
            {
                case 'T': // RowDescription
                    columns = ParseRowDescription(data);
                    break;

                case 'D': // DataRow
                    var row = ParseDataRow(data, columns);
                    rows.Add(row);
                    break;

                case 'C': // CommandComplete
                    var tag = Encoding.UTF8.GetString(data).TrimEnd('\0');
                    rowsAffected = ParseCommandComplete(tag);
                    break;

                case 'E': // ErrorResponse
                    errorMessage = ParseErrorResponse(data);
                    break;

                case 'Z': // ReadyForQuery
                    if (errorMessage != null)
                    {
                        return new QueryResult
                        {
                            Success = false,
                            ErrorMessage = errorMessage
                        };
                    }
                    return new QueryResult
                    {
                        Success = true,
                        RowsAffected = rowsAffected,
                        Rows = rows
                    };

                case 'I': // EmptyQueryResponse
                    break;
            }
        }
    }

    private static List<string> ParseRowDescription(byte[] data)
    {
        var columns = new List<string>();
        var fieldCount = BinaryPrimitives.ReadInt16BigEndian(data);
        var pos = 2;

        for (int i = 0; i < fieldCount; i++)
        {
            var nameEnd = Array.IndexOf(data, (byte)0, pos);
            var name = Encoding.UTF8.GetString(data, pos, nameEnd - pos);
            columns.Add(name);
            pos = nameEnd + 1 + 18; // Skip null terminator and field metadata
        }

        return columns;
    }

    private static Dictionary<string, object?> ParseDataRow(byte[] data, List<string> columns)
    {
        var row = new Dictionary<string, object?>();
        var fieldCount = BinaryPrimitives.ReadInt16BigEndian(data);
        var pos = 2;

        for (int i = 0; i < fieldCount && i < columns.Count; i++)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(pos));
            pos += 4;

            if (length == -1)
            {
                row[columns[i]] = null;
            }
            else
            {
                var value = Encoding.UTF8.GetString(data, pos, length);
                row[columns[i]] = value;
                pos += length;
            }
        }

        return row;
    }

    private static long ParseCommandComplete(string tag)
    {
        var parts = tag.Split(' ');
        if (parts.Length >= 2 && long.TryParse(parts[^1], out var count))
            return count;
        return 0;
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
    protected override async Task<string> BeginTransactionCoreAsync(CancellationToken ct)
    {
        await ExecuteQueryCoreAsync("BEGIN", null, ct);
        return Guid.NewGuid().ToString("N");
    }

    /// <inheritdoc/>
    protected override async Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct)
    {
        await ExecuteQueryCoreAsync("COMMIT", null, ct);
    }

    /// <inheritdoc/>
    protected override async Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct)
    {
        await ExecuteQueryCoreAsync("ROLLBACK", null, ct);
    }

    /// <inheritdoc/>
    protected override async Task SendDisconnectMessageAsync(CancellationToken ct)
    {
        var buffer = new byte[] { (byte)'X', 0, 0, 0, 4 };
        await ActiveStream!.WriteAsync(buffer, ct);
    }

    /// <inheritdoc/>
    protected override async Task<bool> PingCoreAsync(CancellationToken ct)
    {
        try
        {
            var result = await ExecuteQueryCoreAsync("SELECT 1", null, ct);
            return result.Success;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc/>
    protected override async Task CleanupConnectionAsync()
    {
        _reader?.Dispose();
        _writer?.Dispose();
        await base.CleanupConnectionAsync();
    }
}

/// <summary>
/// TiDB MySQL wire protocol strategy.
/// TiDB is MySQL-compatible, uses MySQL protocol.
/// </summary>
public sealed class TiDbProtocolStrategy : DatabaseProtocolStrategyBase
{
    private const int MaxPacketSize = 16 * 1024 * 1024;
    private byte _sequenceId;
    private uint _connectionId;
    private string _serverVersion = "";
    private uint _serverCapabilities;
    private byte _characterSet;

    /// <inheritdoc/>
    public override string StrategyId => "tidb-mysql";

    /// <inheritdoc/>
    public override string StrategyName => "TiDB MySQL Wire Protocol";

    /// <inheritdoc/>
    public override ProtocolInfo ProtocolInfo => new()
    {
        ProtocolName = "TiDB MySQL Wire Protocol",
        ProtocolVersion = "10",
        DefaultPort = 4000,
        Family = ProtocolFamily.NewSQL,
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
                AuthenticationMethod.NativePassword,
                AuthenticationMethod.CachingSha2
            ]
        }
    };

    /// <inheritdoc/>
    protected override async Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        // Read initial handshake packet
        var packet = await ReadPacketAsync(ct);
        ParseHandshakePacket(packet);
    }

    private void ParseHandshakePacket(byte[] packet)
    {
        var pos = 0;
        var protocolVersion = packet[pos++];

        // Server version (null-terminated)
        var versionEnd = Array.IndexOf(packet, (byte)0, pos);
        _serverVersion = Encoding.UTF8.GetString(packet, pos, versionEnd - pos);
        pos = versionEnd + 1;

        // Connection ID
        _connectionId = BitConverter.ToUInt32(packet, pos);
        pos += 4;

        // Auth plugin data part 1 (8 bytes)
        var authData1 = packet[pos..(pos + 8)];
        pos += 8;

        // Filler
        pos++;

        // Capability flags lower 2 bytes
        _serverCapabilities = BitConverter.ToUInt16(packet, pos);
        pos += 2;

        if (pos < packet.Length)
        {
            // Character set
            _characterSet = packet[pos++];

            // Status flags
            pos += 2;

            // Capability flags upper 2 bytes
            _serverCapabilities |= (uint)(BitConverter.ToUInt16(packet, pos) << 16);
        }
    }

    /// <inheritdoc/>
    protected override async Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        // Send handshake response
        var username = parameters.Username ?? "root";
        var password = parameters.Password ?? "";
        var database = parameters.Database ?? "";

        var authResponse = CreateHandshakeResponse(username, password, database);
        await SendPacketAsync(authResponse, ct);

        // Read response
        var response = await ReadPacketAsync(ct);

        if (response[0] == 0xFF) // Error packet
        {
            var errorCode = BitConverter.ToUInt16(response, 1);
            var errorMessage = Encoding.UTF8.GetString(response, 9, response.Length - 9);
            throw new Exception($"Authentication failed ({errorCode}): {errorMessage}");
        }

        if (response[0] == 0xFE) // Auth switch request
        {
            // Handle auth switch
            await HandleAuthSwitchAsync(response, password, ct);
        }

        // OK packet (0x00) means success
    }

    private byte[] CreateHandshakeResponse(string username, string password, string database)
    {
        using var ms = new MemoryStream(4096);
        using var bw = new BinaryWriter(ms);

        // Client capabilities
        uint capabilities = 0x00000001 | // CLIENT_LONG_PASSWORD
                           0x00000200 | // CLIENT_PROTOCOL_41
                           0x00008000 | // CLIENT_SECURE_CONNECTION
                           0x00080000 | // CLIENT_PLUGIN_AUTH
                           0x00100000 | // CLIENT_CONNECT_ATTRS
                           0x00200000;  // CLIENT_PLUGIN_AUTH_LENENC_CLIENT_DATA

        if (!string.IsNullOrEmpty(database))
            capabilities |= 0x00000008; // CLIENT_CONNECT_WITH_DB

        bw.Write(capabilities);
        bw.Write(MaxPacketSize);
        bw.Write(_characterSet);
        bw.Write(new byte[23]); // Reserved

        // Username
        bw.Write(Encoding.UTF8.GetBytes(username));
        bw.Write((byte)0);

        // Auth response
        var authData = ComputeNativePassword(password);
        bw.Write((byte)authData.Length);
        bw.Write(authData);

        // Database
        if (!string.IsNullOrEmpty(database))
        {
            bw.Write(Encoding.UTF8.GetBytes(database));
            bw.Write((byte)0);
        }

        // Auth plugin name
        bw.Write(Encoding.UTF8.GetBytes("mysql_native_password"));
        bw.Write((byte)0);

        return ms.ToArray();
    }

    private static byte[] ComputeNativePassword(string password)
    {
        if (string.IsNullOrEmpty(password))
            return [];

        using var sha1 = SHA1.Create();
        var hash1 = sha1.ComputeHash(Encoding.UTF8.GetBytes(password));
        var hash2 = sha1.ComputeHash(hash1);

        // Would need server's random bytes for full implementation
        return hash1;
    }

    private async Task HandleAuthSwitchAsync(byte[] response, string password, CancellationToken ct)
    {
        var pos = 1;
        var pluginEnd = Array.IndexOf(response, (byte)0, pos);
        var plugin = Encoding.UTF8.GetString(response, pos, pluginEnd - pos);
        pos = pluginEnd + 1;
        var authData = response[pos..];

        byte[] authResponse;
        if (plugin == "caching_sha2_password")
        {
            authResponse = ComputeCachingSha2Password(password, authData);
        }
        else
        {
            authResponse = ComputeNativePassword(password);
        }

        await SendPacketAsync(authResponse, ct);
        await ReadPacketAsync(ct); // OK or error
    }

    private static byte[] ComputeCachingSha2Password(string password, byte[] nonce)
    {
        if (string.IsNullOrEmpty(password))
            return [];

        using var sha256 = SHA256.Create();
        var hash1 = sha256.ComputeHash(Encoding.UTF8.GetBytes(password));
        var hash2 = sha256.ComputeHash(hash1);
        var hash3 = sha256.ComputeHash(hash2.Concat(nonce).ToArray());

        var result = new byte[hash1.Length];
        for (int i = 0; i < hash1.Length; i++)
            result[i] = (byte)(hash1[i] ^ hash3[i]);

        return result;
    }

    private async Task<byte[]> ReadPacketAsync(CancellationToken ct)
    {
        var header = new byte[4];
        await ActiveStream!.ReadExactlyAsync(header, 0, 4, ct);

        var length = header[0] | (header[1] << 8) | (header[2] << 16);
        _sequenceId = header[3];

        var payload = new byte[length];
        if (length > 0)
            await ActiveStream.ReadExactlyAsync(payload, 0, length, ct);

        return payload;
    }

    private async Task SendPacketAsync(byte[] payload, CancellationToken ct)
    {
        var header = new byte[4];
        header[0] = (byte)(payload.Length & 0xFF);
        header[1] = (byte)((payload.Length >> 8) & 0xFF);
        header[2] = (byte)((payload.Length >> 16) & 0xFF);
        header[3] = ++_sequenceId;

        await ActiveStream!.WriteAsync(header, ct);
        await ActiveStream.WriteAsync(payload, ct);
        await ActiveStream.FlushAsync(ct);
    }

    /// <inheritdoc/>
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(
        string query,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken ct)
    {
        _sequenceId = 0xFF; // Will wrap to 0

        // COM_QUERY command
        var queryBytes = Encoding.UTF8.GetBytes(query);
        var packet = new byte[1 + queryBytes.Length];
        packet[0] = 0x03; // COM_QUERY
        queryBytes.CopyTo(packet, 1);

        await SendPacketAsync(packet, ct);

        // Read response
        var response = await ReadPacketAsync(ct);

        if (response[0] == 0xFF) // Error
        {
            var errorCode = BitConverter.ToUInt16(response, 1);
            var errorMessage = Encoding.UTF8.GetString(response, 9, response.Length - 9);
            return new QueryResult
            {
                Success = false,
                ErrorMessage = errorMessage,
                ErrorCode = errorCode.ToString()
            };
        }

        if (response[0] == 0x00) // OK packet
        {
            var affectedRows = ReadLengthEncodedInt(response, 1, out var pos);
            return new QueryResult
            {
                Success = true,
                RowsAffected = (long)affectedRows
            };
        }

        // Result set
        var columnCount = ReadLengthEncodedInt(response, 0, out _);
        var columns = new List<string>();

        // Read column definitions
        for (ulong i = 0; i < columnCount; i++)
        {
            var colPacket = await ReadPacketAsync(ct);
            var columnName = ParseColumnDefinition(colPacket);
            columns.Add(columnName);
        }

        // EOF packet (if not using CLIENT_DEPRECATE_EOF)
        await ReadPacketAsync(ct);

        // Read rows
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        while (true)
        {
            var rowPacket = await ReadPacketAsync(ct);

            if (rowPacket[0] == 0xFE && rowPacket.Length < 9) // EOF
                break;

            if (rowPacket[0] == 0xFF) // Error
                break;

            var row = ParseResultRow(rowPacket, columns);
            rows.Add(row);
        }

        return new QueryResult
        {
            Success = true,
            RowsAffected = rows.Count,
            Rows = rows
        };
    }

    private static ulong ReadLengthEncodedInt(byte[] data, int start, out int newPos)
    {
        var firstByte = data[start];
        if (firstByte < 251)
        {
            newPos = start + 1;
            return firstByte;
        }
        if (firstByte == 0xFC)
        {
            newPos = start + 3;
            return BitConverter.ToUInt16(data, start + 1);
        }
        if (firstByte == 0xFD)
        {
            newPos = start + 4;
            return (ulong)(data[start + 1] | (data[start + 2] << 8) | (data[start + 3] << 16));
        }
        if (firstByte == 0xFE)
        {
            newPos = start + 9;
            return BitConverter.ToUInt64(data, start + 1);
        }
        newPos = start + 1;
        return 0;
    }

    private static string ParseColumnDefinition(byte[] packet)
    {
        var pos = 0;

        // Skip catalog, schema, table, org_table
        for (int i = 0; i < 4; i++)
        {
            var len = (int)ReadLengthEncodedInt(packet, pos, out pos);
            pos += len;
        }

        // Column name
        var nameLen = (int)ReadLengthEncodedInt(packet, pos, out pos);
        var name = Encoding.UTF8.GetString(packet, pos, nameLen);

        return name;
    }

    private static Dictionary<string, object?> ParseResultRow(byte[] packet, List<string> columns)
    {
        var row = new Dictionary<string, object?>();
        var pos = 0;

        for (int i = 0; i < columns.Count; i++)
        {
            if (packet[pos] == 0xFB)
            {
                row[columns[i]] = null;
                pos++;
            }
            else
            {
                var len = (int)ReadLengthEncodedInt(packet, pos, out pos);
                var value = Encoding.UTF8.GetString(packet, pos, len);
                row[columns[i]] = value;
                pos += len;
            }
        }

        return row;
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
    protected override async Task<string> BeginTransactionCoreAsync(CancellationToken ct)
    {
        await ExecuteQueryCoreAsync("START TRANSACTION", null, ct);
        return Guid.NewGuid().ToString("N");
    }

    /// <inheritdoc/>
    protected override async Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct)
    {
        await ExecuteQueryCoreAsync("COMMIT", null, ct);
    }

    /// <inheritdoc/>
    protected override async Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct)
    {
        await ExecuteQueryCoreAsync("ROLLBACK", null, ct);
    }

    /// <inheritdoc/>
    protected override async Task SendDisconnectMessageAsync(CancellationToken ct)
    {
        _sequenceId = 0xFF;
        await SendPacketAsync([0x01], ct); // COM_QUIT
    }

    /// <inheritdoc/>
    protected override async Task<bool> PingCoreAsync(CancellationToken ct)
    {
        try
        {
            _sequenceId = 0xFF;
            await SendPacketAsync([0x0E], ct); // COM_PING
            var response = await ReadPacketAsync(ct);
            return response[0] == 0x00;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// YugabyteDB PostgreSQL wire protocol strategy.
/// YugabyteDB uses PostgreSQL wire protocol with extensions for distributed transactions.
/// </summary>
public sealed class YugabyteDbProtocolStrategy : DatabaseProtocolStrategyBase
{
    private const int ProtocolVersion3 = 196608;
    private int _processId;
    private int _secretKey;
    private string _serverVersion = "";

    /// <inheritdoc/>
    public override string StrategyId => "yugabytedb-pgwire";

    /// <inheritdoc/>
    public override string StrategyName => "YugabyteDB PostgreSQL Wire Protocol";

    /// <inheritdoc/>
    public override ProtocolInfo ProtocolInfo => new()
    {
        ProtocolName = "YugabyteDB PostgreSQL Wire Protocol",
        ProtocolVersion = "3.0",
        DefaultPort = 5433,
        Family = ProtocolFamily.NewSQL,
        MaxPacketSize = 1024 * 1024 * 1024,
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
            SupportsMultiplexing = false,
            SupportsServerCursors = true,
            SupportsBulkOperations = true,
            SupportsQueryCancellation = true,
            SupportedAuthMethods =
            [
                AuthenticationMethod.ClearText,
                AuthenticationMethod.MD5,
                AuthenticationMethod.ScramSha256,
                AuthenticationMethod.Certificate
            ]
        }
    };

    /// <inheritdoc/>
    protected override async Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        var startupParams = new Dictionary<string, string>
        {
            ["user"] = parameters.Username ?? "yugabyte",
            ["database"] = parameters.Database ?? "yugabyte",
            ["client_encoding"] = "UTF8",
            ["application_name"] = "DataWarehouse"
        };

        using var ms = new MemoryStream(4096);
        using var bw = new BinaryWriter(ms);

        bw.Write(BinaryPrimitives.ReverseEndianness(ProtocolVersion3));

        foreach (var param in startupParams)
        {
            bw.Write(Encoding.UTF8.GetBytes(param.Key));
            bw.Write((byte)0);
            bw.Write(Encoding.UTF8.GetBytes(param.Value));
            bw.Write((byte)0);
        }
        bw.Write((byte)0);

        var payload = ms.ToArray();
        var length = payload.Length + 4;

        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, length);
        await ActiveStream!.WriteAsync(header, ct);
        await ActiveStream.WriteAsync(payload, ct);
        await ActiveStream.FlushAsync(ct);
    }

    /// <inheritdoc/>
    protected override async Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        while (true)
        {
            var (type, data) = await ReadMessageAsync(ct);

            switch ((char)type)
            {
                case 'R':
                    await HandleAuthenticationAsync(data, parameters, ct);
                    break;

                case 'K':
                    _processId = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(0));
                    _secretKey = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(4));
                    break;

                case 'S':
                    ParseParameterStatus(data);
                    break;

                case 'Z':
                    return;

                case 'E':
                    throw new Exception($"Authentication failed: {ParseErrorResponse(data)}");
            }
        }
    }

    private async Task HandleAuthenticationAsync(byte[] data, ConnectionParameters parameters, CancellationToken ct)
    {
        var authType = BinaryPrimitives.ReadInt32BigEndian(data);

        switch (authType)
        {
            case 0:
                return;

            case 3:
                await SendPasswordAsync(parameters.Password ?? "", ct);
                break;

            case 5:
                var salt = data[4..8];
                var hash = ComputeMd5Password(parameters.Username ?? "", parameters.Password ?? "", salt);
                await SendPasswordAsync(hash, ct);
                break;

            default:
                throw new NotSupportedException($"Unsupported authentication type: {authType}");
        }
    }

    private async Task SendPasswordAsync(string password, CancellationToken ct)
    {
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        var length = passwordBytes.Length + 5;

        var buffer = new byte[1 + 4 + passwordBytes.Length + 1];
        buffer[0] = (byte)'p';
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(1), length);
        passwordBytes.CopyTo(buffer.AsSpan(5));
        buffer[^1] = 0;

        await ActiveStream!.WriteAsync(buffer, ct);
        await ActiveStream.FlushAsync(ct);
    }

    private static string ComputeMd5Password(string user, string password, byte[] salt)
    {
        using var md5 = MD5.Create();
        var inner = md5.ComputeHash(Encoding.UTF8.GetBytes(password + user));
        var innerHex = Convert.ToHexString(inner).ToLowerInvariant();
        var outer = md5.ComputeHash(Encoding.UTF8.GetBytes(innerHex).Concat(salt).ToArray());
        return "md5" + Convert.ToHexString(outer).ToLowerInvariant();
    }

    private void ParseParameterStatus(byte[] data)
    {
        var parts = Encoding.UTF8.GetString(data).TrimEnd('\0').Split('\0');
        if (parts.Length >= 2 && parts[0] == "server_version")
            _serverVersion = parts[1];
    }

    private static string ParseErrorResponse(byte[] data)
    {
        var message = new StringBuilder();
        var pos = 0;
        while (pos < data.Length && data[pos] != 0)
        {
            var fieldType = (char)data[pos++];
            var end = Array.IndexOf(data, (byte)0, pos);
            if (end < 0) break;
            var value = Encoding.UTF8.GetString(data, pos, end - pos);
            pos = end + 1;

            if (fieldType == 'M')
                message.Append(value);
        }
        return message.ToString();
    }

    private async Task<(byte type, byte[] data)> ReadMessageAsync(CancellationToken ct)
    {
        var header = new byte[5];
        await ActiveStream!.ReadExactlyAsync(header, 0, 5, ct);

        var type = header[0];
        var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1)) - 4;

        var data = new byte[length];
        if (length > 0)
            await ActiveStream.ReadExactlyAsync(data, 0, length, ct);

        return (type, data);
    }

    /// <inheritdoc/>
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(
        string query,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken ct)
    {
        var queryBytes = Encoding.UTF8.GetBytes(query);
        var length = queryBytes.Length + 5;

        var buffer = new byte[1 + 4 + queryBytes.Length + 1];
        buffer[0] = (byte)'Q';
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(1), length);
        queryBytes.CopyTo(buffer.AsSpan(5));
        buffer[^1] = 0;

        await ActiveStream!.WriteAsync(buffer, ct);
        await ActiveStream.FlushAsync(ct);

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        var columns = new List<string>();
        long rowsAffected = 0;
        string? errorMessage = null;

        while (true)
        {
            var (type, data) = await ReadMessageAsync(ct);

            switch ((char)type)
            {
                case 'T':
                    columns = ParseRowDescription(data);
                    break;

                case 'D':
                    var row = ParseDataRow(data, columns);
                    rows.Add(row);
                    break;

                case 'C':
                    var tag = Encoding.UTF8.GetString(data).TrimEnd('\0');
                    rowsAffected = ParseCommandComplete(tag);
                    break;

                case 'E':
                    errorMessage = ParseErrorResponse(data);
                    break;

                case 'Z':
                    if (errorMessage != null)
                    {
                        return new QueryResult
                        {
                            Success = false,
                            ErrorMessage = errorMessage
                        };
                    }
                    return new QueryResult
                    {
                        Success = true,
                        RowsAffected = rowsAffected,
                        Rows = rows
                    };

                case 'I':
                    break;
            }
        }
    }

    private static List<string> ParseRowDescription(byte[] data)
    {
        var columns = new List<string>();
        var fieldCount = BinaryPrimitives.ReadInt16BigEndian(data);
        var pos = 2;

        for (int i = 0; i < fieldCount; i++)
        {
            var nameEnd = Array.IndexOf(data, (byte)0, pos);
            var name = Encoding.UTF8.GetString(data, pos, nameEnd - pos);
            columns.Add(name);
            pos = nameEnd + 1 + 18;
        }

        return columns;
    }

    private static Dictionary<string, object?> ParseDataRow(byte[] data, List<string> columns)
    {
        var row = new Dictionary<string, object?>();
        var fieldCount = BinaryPrimitives.ReadInt16BigEndian(data);
        var pos = 2;

        for (int i = 0; i < fieldCount && i < columns.Count; i++)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(pos));
            pos += 4;

            if (length == -1)
            {
                row[columns[i]] = null;
            }
            else
            {
                var value = Encoding.UTF8.GetString(data, pos, length);
                row[columns[i]] = value;
                pos += length;
            }
        }

        return row;
    }

    private static long ParseCommandComplete(string tag)
    {
        var parts = tag.Split(' ');
        if (parts.Length >= 2 && long.TryParse(parts[^1], out var count))
            return count;
        return 0;
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
    protected override async Task<string> BeginTransactionCoreAsync(CancellationToken ct)
    {
        await ExecuteQueryCoreAsync("BEGIN", null, ct);
        return Guid.NewGuid().ToString("N");
    }

    /// <inheritdoc/>
    protected override async Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct)
    {
        await ExecuteQueryCoreAsync("COMMIT", null, ct);
    }

    /// <inheritdoc/>
    protected override async Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct)
    {
        await ExecuteQueryCoreAsync("ROLLBACK", null, ct);
    }

    /// <inheritdoc/>
    protected override async Task SendDisconnectMessageAsync(CancellationToken ct)
    {
        var buffer = new byte[] { (byte)'X', 0, 0, 0, 4 };
        await ActiveStream!.WriteAsync(buffer, ct);
    }

    /// <inheritdoc/>
    protected override async Task<bool> PingCoreAsync(CancellationToken ct)
    {
        try
        {
            var result = await ExecuteQueryCoreAsync("SELECT 1", null, ct);
            return result.Success;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// VoltDB wire protocol strategy.
/// VoltDB uses a proprietary binary wire protocol.
/// </summary>
public sealed class VoltDbProtocolStrategy : DatabaseProtocolStrategyBase
{
    private const byte ProtocolVersion = 1;
    private long _clientHandle;
    private string _serverVersion = "";

    /// <inheritdoc/>
    public override string StrategyId => "voltdb";

    /// <inheritdoc/>
    public override string StrategyName => "VoltDB Wire Protocol";

    /// <inheritdoc/>
    public override ProtocolInfo ProtocolInfo => new()
    {
        ProtocolName = "VoltDB Wire Protocol",
        ProtocolVersion = "1",
        DefaultPort = 21212,
        Family = ProtocolFamily.NewSQL,
        MaxPacketSize = 50 * 1024 * 1024,
        Capabilities = new ProtocolCapabilities
        {
            SupportsTransactions = true,
            SupportsPreparedStatements = true,
            SupportsCursors = false,
            SupportsStreaming = true,
            SupportsBatch = true,
            SupportsNotifications = false,
            SupportsSsl = true,
            SupportsCompression = false,
            SupportsMultiplexing = true,
            SupportsServerCursors = false,
            SupportsBulkOperations = true,
            SupportsQueryCancellation = false,
            SupportedAuthMethods =
            [
                AuthenticationMethod.ClearText,
                AuthenticationMethod.SHA256
            ]
        }
    };

    /// <inheritdoc/>
    protected override async Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        // VoltDB login message
        using var ms = new MemoryStream(4096);
        using var bw = new BinaryWriter(ms);

        // Service name
        var serviceName = "database";
        bw.Write((short)serviceName.Length);
        bw.Write(Encoding.UTF8.GetBytes(serviceName));

        // Username
        var username = parameters.Username ?? "";
        bw.Write((short)username.Length);
        bw.Write(Encoding.UTF8.GetBytes(username));

        // Password hash (SHA-256)
        var passwordBytes = Encoding.UTF8.GetBytes(parameters.Password ?? "");
        var passwordHash = SHA256.HashData(passwordBytes);
        bw.Write((short)passwordHash.Length);
        bw.Write(passwordHash);

        var payload = ms.ToArray();

        // Send with length prefix
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
        await ActiveStream!.WriteAsync(header, ct);
        await ActiveStream.WriteAsync(payload, ct);
        await ActiveStream.FlushAsync(ct);
    }

    /// <inheritdoc/>
    protected override async Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        // Read login response
        var lengthBytes = new byte[4];
        await ActiveStream!.ReadExactlyAsync(lengthBytes, 0, 4, ct);
        var length = BinaryPrimitives.ReadInt32BigEndian(lengthBytes);

        var response = new byte[length];
        await ActiveStream.ReadExactlyAsync(response, 0, length, ct);

        // Parse response
        using var ms = new MemoryStream(response);
        using var br = new BinaryReader(ms);

        var status = br.ReadByte();
        if (status != 0)
        {
            var msgLen = br.ReadInt16();
            var message = Encoding.UTF8.GetString(br.ReadBytes(msgLen));
            throw new Exception($"Login failed: {message}");
        }

        // Read server info
        var hostIdLen = br.ReadInt16();
        var hostId = br.ReadBytes(hostIdLen);

        var connectionIdLen = br.ReadInt16();
        var connectionId = br.ReadBytes(connectionIdLen);

        // Read build string (version)
        if (ms.Position < ms.Length)
        {
            var buildLen = br.ReadInt16();
            if (buildLen > 0)
                _serverVersion = Encoding.UTF8.GetString(br.ReadBytes(buildLen));
        }
    }

    /// <inheritdoc/>
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(
        string query,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken ct)
    {
        _clientHandle++;

        // Build procedure call
        using var ms = new MemoryStream(4096);
        using var bw = new BinaryWriter(ms);

        bw.Write((byte)0); // Version
        bw.Write((byte)1); // Procedure call type
        bw.Write(BinaryPrimitives.ReverseEndianness(_clientHandle));

        // Procedure name - use @AdHoc for SQL
        var procName = "@AdHoc";
        bw.Write((short)procName.Length);
        bw.Write(Encoding.UTF8.GetBytes(procName));

        // Parameters - first is the SQL query
        bw.Write((short)1); // Parameter count

        // String parameter (type 9)
        bw.Write((byte)9);
        var queryBytes = Encoding.UTF8.GetBytes(query);
        bw.Write(BinaryPrimitives.ReverseEndianness(queryBytes.Length));
        bw.Write(queryBytes);

        var payload = ms.ToArray();

        // Send
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
        await ActiveStream!.WriteAsync(header, ct);
        await ActiveStream.WriteAsync(payload, ct);
        await ActiveStream.FlushAsync(ct);

        // Read response
        var lengthBuf = new byte[4];
        await ActiveStream.ReadExactlyAsync(lengthBuf, 0, 4, ct);
        var responseLength = BinaryPrimitives.ReadInt32BigEndian(lengthBuf);

        var response = new byte[responseLength];
        await ActiveStream.ReadExactlyAsync(response, 0, responseLength, ct);

        return ParseVoltDbResponse(response);
    }

    private QueryResult ParseVoltDbResponse(byte[] response)
    {
        using var ms = new MemoryStream(response);
        using var br = new BinaryReader(ms);

        var version = br.ReadByte();
        var clientHandle = BinaryPrimitives.ReverseEndianness(br.ReadInt64());
        var status = br.ReadByte();

        if (status != 1) // 1 = SUCCESS
        {
            // Read status string
            var statusLen = BinaryPrimitives.ReverseEndianness(br.ReadInt32());
            var statusMsg = statusLen > 0 ? Encoding.UTF8.GetString(br.ReadBytes(statusLen)) : "Unknown error";

            return new QueryResult
            {
                Success = false,
                ErrorMessage = statusMsg,
                ErrorCode = status.ToString()
            };
        }

        // Skip app status
        var appStatus = br.ReadByte();
        var appStatusLen = BinaryPrimitives.ReverseEndianness(br.ReadInt32());
        if (appStatusLen > 0)
            br.ReadBytes(appStatusLen);

        // Read result count
        var resultCount = BinaryPrimitives.ReverseEndianness(br.ReadInt16());

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        long totalRows = 0;

        for (int r = 0; r < resultCount; r++)
        {
            // Read table
            var tableLen = BinaryPrimitives.ReverseEndianness(br.ReadInt32());
            var tableStatusCode = BinaryPrimitives.ReverseEndianness(br.ReadInt32());

            // Column count
            var colCount = BinaryPrimitives.ReverseEndianness(br.ReadInt16());
            var columns = new List<(string name, byte type)>();

            // Column types
            for (int c = 0; c < colCount; c++)
            {
                var colType = br.ReadByte();
                columns.Add(("", colType));
            }

            // Column names
            for (int c = 0; c < colCount; c++)
            {
                var nameLen = BinaryPrimitives.ReverseEndianness(br.ReadInt32());
                var name = Encoding.UTF8.GetString(br.ReadBytes(nameLen));
                columns[c] = (name, columns[c].type);
            }

            // Row count
            var rowCount = BinaryPrimitives.ReverseEndianness(br.ReadInt32());
            totalRows += rowCount;

            // Rows
            for (int i = 0; i < rowCount; i++)
            {
                var rowLen = BinaryPrimitives.ReverseEndianness(br.ReadInt32());
                var row = new Dictionary<string, object?>();

                for (int c = 0; c < colCount; c++)
                {
                    var value = ReadVoltDbValue(br, columns[c].type);
                    row[columns[c].name] = value;
                }

                rows.Add(row);
            }
        }

        return new QueryResult
        {
            Success = true,
            RowsAffected = totalRows,
            Rows = rows
        };
    }

    private static object? ReadVoltDbValue(BinaryReader br, byte type)
    {
        return type switch
        {
            1 => null, // NULL
            3 => br.ReadByte(), // TINYINT
            4 => BinaryPrimitives.ReverseEndianness(br.ReadInt16()), // SMALLINT
            5 => BinaryPrimitives.ReverseEndianness(br.ReadInt32()), // INTEGER
            6 => BinaryPrimitives.ReverseEndianness(br.ReadInt64()), // BIGINT
            8 => BitConverter.Int64BitsToDouble(BinaryPrimitives.ReverseEndianness(br.ReadInt64())), // FLOAT
            9 => ReadVoltDbString(br), // STRING
            11 => new DateTimeOffset(BinaryPrimitives.ReverseEndianness(br.ReadInt64()) / 1000, TimeSpan.Zero).DateTime, // TIMESTAMP
            22 => ReadVoltDbDecimal(br), // DECIMAL
            25 => ReadVoltDbVarBinary(br), // VARBINARY
            _ => null
        };
    }

    private static string? ReadVoltDbString(BinaryReader br)
    {
        var len = BinaryPrimitives.ReverseEndianness(br.ReadInt32());
        if (len < 0) return null;
        return Encoding.UTF8.GetString(br.ReadBytes(len));
    }

    private static decimal ReadVoltDbDecimal(BinaryReader br)
    {
        var bytes = br.ReadBytes(16);
        // VoltDB uses a fixed-point decimal with 12 digits of precision
        var hi = BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(0));
        var lo = BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(8));
        return (decimal)hi * 1_000_000_000_000m + lo;
    }

    private static byte[]? ReadVoltDbVarBinary(BinaryReader br)
    {
        var len = BinaryPrimitives.ReverseEndianness(br.ReadInt32());
        if (len < 0) return null;
        return br.ReadBytes(len);
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
    protected override Task<string> BeginTransactionCoreAsync(CancellationToken ct)
    {
        // VoltDB has implicit transactions per procedure call
        return Task.FromResult(Guid.NewGuid().ToString("N"));
    }

    /// <inheritdoc/>
    protected override Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task SendDisconnectMessageAsync(CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override async Task<bool> PingCoreAsync(CancellationToken ct)
    {
        try
        {
            var result = await ExecuteQueryCoreAsync("SELECT 1", null, ct);
            return result.Success;
        }
        catch
        {
            return false;
        }
    }
}
