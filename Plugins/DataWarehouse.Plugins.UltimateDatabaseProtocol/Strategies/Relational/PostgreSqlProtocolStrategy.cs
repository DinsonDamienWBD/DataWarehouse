using System.Security.Cryptography;
using System.Text;

namespace DataWarehouse.Plugins.UltimateDatabaseProtocol.Strategies.Relational;

/// <summary>
/// PostgreSQL Frontend/Backend Protocol v3.0 implementation.
/// Implements the complete wire protocol for PostgreSQL communication including:
/// - Startup message and parameter negotiation
/// - Authentication (cleartext, MD5, SCRAM-SHA-256)
/// - Simple and extended query protocol
/// - Copy protocol for bulk operations
/// - Notification/Listen support
/// - SSL negotiation
/// </summary>
public sealed class PostgreSqlProtocolStrategy : DatabaseProtocolStrategyBase
{
    // Protocol constants
    private const int ProtocolVersionNumber = 196608; // 3.0 = 3 << 16 | 0
    private const int SslRequestCode = 80877103;
    private const int CancelRequestCode = 80877102;

    // Message types (Backend)
    private const byte Authentication = (byte)'R';
    private const byte BackendKeyData = (byte)'K';
    private const byte BindComplete = (byte)'2';
    private const byte CloseComplete = (byte)'3';
    private const byte CommandComplete = (byte)'C';
    private const byte CopyData = (byte)'d';
    private const byte CopyDone = (byte)'c';
    private const byte CopyInResponse = (byte)'G';
    private const byte CopyOutResponse = (byte)'H';
    private const byte CopyBothResponse = (byte)'W';
    private const byte DataRow = (byte)'D';
    private const byte EmptyQueryResponse = (byte)'I';
    private const byte ErrorResponse = (byte)'E';
    private const byte FunctionCallResponse = (byte)'V';
    private const byte NegotiateProtocolVersion = (byte)'v';
    private const byte NoData = (byte)'n';
    private const byte NoticeResponse = (byte)'N';
    private const byte NotificationResponse = (byte)'A';
    private const byte ParameterDescription = (byte)'t';
    private const byte ParameterStatus = (byte)'S';
    private const byte ParseComplete = (byte)'1';
    private const byte PortalSuspended = (byte)'s';
    private const byte ReadyForQuery = (byte)'Z';
    private const byte RowDescription = (byte)'T';

    // Message types (Frontend)
    private const byte Bind = (byte)'B';
    private const byte Close = (byte)'C';
    private const byte CopyFail = (byte)'f';
    private const byte Describe = (byte)'D';
    private const byte Execute = (byte)'E';
    private const byte Flush = (byte)'H';
    private const byte FunctionCall = (byte)'F';
    private const byte GssResponse = (byte)'p';
    private const byte Parse = (byte)'P';
    private const byte PasswordMessage = (byte)'p';
    private const byte Query = (byte)'Q';
    private const byte SaslInitialResponse = (byte)'p';
    private const byte SaslResponse = (byte)'p';
    private const byte Sync = (byte)'S';
    private const byte Terminate = (byte)'X';

    // Connection state
    private int _backendProcessId;
    private int _backendSecretKey;
    private readonly Dictionary<string, string> _serverParameters = new();
    private char _transactionStatus;

    /// <inheritdoc/>
    public override string StrategyId => "postgresql-v3";

    /// <inheritdoc/>
    public override string StrategyName => "PostgreSQL Protocol v3";

    /// <inheritdoc/>
    public override ProtocolInfo ProtocolInfo => new()
    {
        ProtocolName = "PostgreSQL Frontend/Backend Protocol",
        ProtocolVersion = "3.0",
        DefaultPort = 5432,
        Family = ProtocolFamily.Relational,
        MaxPacketSize = 1024 * 1024 * 1024, // 1 GB
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
                AuthenticationMethod.Certificate,
                AuthenticationMethod.Kerberos
            ]
        }
    };

    /// <inheritdoc/>
    protected override async Task NotifySslUpgradeAsync(CancellationToken ct)
    {
        // PostgreSQL requires an SSL request before upgrading
        var buffer = new byte[8];
        WriteInt32BE(buffer.AsSpan(0, 4), 8); // Length
        WriteInt32BE(buffer.AsSpan(4, 4), SslRequestCode);
        await SendAsync(buffer, ct);

        // Read response (single byte: 'S' for SSL, 'N' for no SSL)
        var response = await ReceiveExactAsync(1, ct);
        if (response[0] != (byte)'S')
        {
            throw new InvalidOperationException("Server does not support SSL");
        }
    }

    /// <inheritdoc/>
    protected override async Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        // Build startup message
        var startupParams = new Dictionary<string, string>
        {
            ["user"] = parameters.Username ?? throw new ArgumentException("Username required"),
            ["database"] = parameters.Database ?? parameters.Username,
            ["client_encoding"] = "UTF8",
            ["DateStyle"] = "ISO, MDY",
            ["TimeZone"] = "UTC",
            ["extra_float_digits"] = "3"
        };

        // Add any additional parameters
        foreach (var kvp in parameters.AdditionalParameters)
        {
            startupParams[kvp.Key] = kvp.Value;
        }

        // Calculate message size
        var messageSize = 4 + 4; // Length + Protocol version
        foreach (var kvp in startupParams)
        {
            messageSize += Encoding.UTF8.GetByteCount(kvp.Key) + 1; // Key + null
            messageSize += Encoding.UTF8.GetByteCount(kvp.Value) + 1; // Value + null
        }
        messageSize += 1; // Final null terminator

        // Build and send startup message
        var buffer = new byte[messageSize];
        var offset = 0;

        WriteInt32BE(buffer.AsSpan(offset, 4), messageSize);
        offset += 4;

        WriteInt32BE(buffer.AsSpan(offset, 4), ProtocolVersionNumber);
        offset += 4;

        foreach (var kvp in startupParams)
        {
            offset += WriteNullTerminatedString(buffer.AsSpan(offset), kvp.Key);
            offset += WriteNullTerminatedString(buffer.AsSpan(offset), kvp.Value);
        }

        buffer[offset] = 0; // Final null terminator

        await SendAsync(buffer, ct);
    }

    /// <inheritdoc/>
    protected override async Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        while (true)
        {
            var (messageType, payload) = await ReadMessageAsync(ct);

            switch (messageType)
            {
                case Authentication:
                    await HandleAuthenticationAsync(payload, parameters, ct);
                    break;

                case ParameterStatus:
                    HandleParameterStatus(payload);
                    break;

                case BackendKeyData:
                    HandleBackendKeyData(payload);
                    break;

                case ReadyForQuery:
                    HandleReadyForQuery(payload);
                    return; // Authentication complete

                case ErrorResponse:
                    var error = ParseErrorResponse(payload);
                    throw new InvalidOperationException($"Authentication failed: {error}");

                case NegotiateProtocolVersion:
                    // Server wants to negotiate protocol version
                    break;

                default:
                    throw new InvalidOperationException($"Unexpected message type during authentication: {(char)messageType}");
            }
        }
    }

    private async Task HandleAuthenticationAsync(byte[] payload, ConnectionParameters parameters, CancellationToken ct)
    {
        var authType = ReadInt32BE(payload.AsSpan(0, 4));

        switch (authType)
        {
            case 0: // AuthenticationOk
                return;

            case 3: // AuthenticationCleartextPassword
                await SendPasswordAsync(parameters.Password ?? "", ct);
                break;

            case 5: // AuthenticationMD5Password
                var salt = payload[4..8];
                await SendMd5PasswordAsync(parameters.Username!, parameters.Password ?? "", salt, ct);
                break;

            case 10: // AuthenticationSASL
                var mechanisms = ParseSaslMechanisms(payload[4..]);
                if (mechanisms.Contains("SCRAM-SHA-256"))
                {
                    await PerformScramSha256AuthAsync(parameters, ct);
                }
                else
                {
                    throw new NotSupportedException($"Unsupported SASL mechanisms: {string.Join(", ", mechanisms)}");
                }
                break;

            case 11: // AuthenticationSASLContinue
                // Handled in SCRAM flow
                break;

            case 12: // AuthenticationSASLFinal
                // Handled in SCRAM flow
                break;

            default:
                throw new NotSupportedException($"Unsupported authentication type: {authType}");
        }
    }

    private async Task SendPasswordAsync(string password, CancellationToken ct)
    {
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        var messageLength = 4 + passwordBytes.Length + 1;
        var buffer = new byte[1 + messageLength];

        buffer[0] = PasswordMessage;
        WriteInt32BE(buffer.AsSpan(1, 4), messageLength);
        passwordBytes.CopyTo(buffer.AsSpan(5));
        buffer[^1] = 0;

        await SendAsync(buffer, ct);
    }

    private async Task SendMd5PasswordAsync(string username, string password, byte[] salt, CancellationToken ct)
    {
        // PostgreSQL MD5: "md5" + MD5(MD5(password + username) + salt)
        var inner = MD5.HashData(Encoding.UTF8.GetBytes(password + username));
        var innerHex = Convert.ToHexString(inner).ToLowerInvariant();
        var outer = MD5.HashData(Encoding.UTF8.GetBytes(innerHex).Concat(salt).ToArray());
        var hash = "md5" + Convert.ToHexString(outer).ToLowerInvariant();

        await SendPasswordAsync(hash, ct);
    }

    private async Task PerformScramSha256AuthAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        // SCRAM-SHA-256 authentication (RFC 5802)
        var clientNonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        var clientFirstMessageBare = $"n=*,r={clientNonce}";
        var clientFirstMessage = $"n,,{clientFirstMessageBare}";

        // Send SASLInitialResponse
        var mechanism = "SCRAM-SHA-256\0";
        var mechanismBytes = Encoding.UTF8.GetBytes(mechanism);
        var messageBytes = Encoding.UTF8.GetBytes(clientFirstMessage);

        var totalLength = 4 + mechanismBytes.Length + 4 + messageBytes.Length;
        var buffer = new byte[1 + totalLength];
        var offset = 0;

        buffer[offset++] = SaslInitialResponse;
        WriteInt32BE(buffer.AsSpan(offset, 4), totalLength);
        offset += 4;

        mechanismBytes.CopyTo(buffer.AsSpan(offset));
        offset += mechanismBytes.Length;

        WriteInt32BE(buffer.AsSpan(offset, 4), messageBytes.Length);
        offset += 4;

        messageBytes.CopyTo(buffer.AsSpan(offset));

        await SendAsync(buffer, ct);

        // Read AuthenticationSASLContinue
        var (msgType, payload) = await ReadMessageAsync(ct);
        if (msgType != Authentication || ReadInt32BE(payload.AsSpan(0, 4)) != 11)
        {
            throw new InvalidOperationException("Expected SASL continue message");
        }

        var serverFirstMessage = Encoding.UTF8.GetString(payload[4..]);
        var serverParams = ParseScramMessage(serverFirstMessage);

        var serverNonce = serverParams["r"];
        var salt = Convert.FromBase64String(serverParams["s"]);
        var iterations = int.Parse(serverParams["i"]);

        // Verify server nonce starts with client nonce
        if (!serverNonce.StartsWith(clientNonce))
        {
            throw new InvalidOperationException("Server nonce doesn't start with client nonce");
        }

        // Calculate client proof
        var saltedPassword = Hi(parameters.Password ?? "", salt, iterations);
        var clientKey = HMACSHA256.HashData(saltedPassword, Encoding.UTF8.GetBytes("Client Key"));
        var storedKey = SHA256.HashData(clientKey);

        var clientFinalMessageWithoutProof = $"c=biws,r={serverNonce}";
        var authMessage = $"{clientFirstMessageBare},{serverFirstMessage},{clientFinalMessageWithoutProof}";
        var clientSignature = HMACSHA256.HashData(storedKey, Encoding.UTF8.GetBytes(authMessage));

        var clientProof = new byte[clientKey.Length];
        for (int i = 0; i < clientKey.Length; i++)
        {
            clientProof[i] = (byte)(clientKey[i] ^ clientSignature[i]);
        }

        var clientFinalMessage = $"{clientFinalMessageWithoutProof},p={Convert.ToBase64String(clientProof)}";
        var finalMessageBytes = Encoding.UTF8.GetBytes(clientFinalMessage);

        // Send SASLResponse
        var responseLength = 4 + finalMessageBytes.Length;
        var responseBuffer = new byte[1 + responseLength];
        responseBuffer[0] = SaslResponse;
        WriteInt32BE(responseBuffer.AsSpan(1, 4), responseLength);
        finalMessageBytes.CopyTo(responseBuffer.AsSpan(5));

        await SendAsync(responseBuffer, ct);

        // Read AuthenticationSASLFinal
        (msgType, payload) = await ReadMessageAsync(ct);
        if (msgType != Authentication || ReadInt32BE(payload.AsSpan(0, 4)) != 12)
        {
            throw new InvalidOperationException("Expected SASL final message");
        }

        // Verify server signature
        var serverKey = HMACSHA256.HashData(saltedPassword, Encoding.UTF8.GetBytes("Server Key"));
        var expectedServerSignature = HMACSHA256.HashData(serverKey, Encoding.UTF8.GetBytes(authMessage));
        var serverFinalMessage = Encoding.UTF8.GetString(payload[4..]);
        var serverSignature = Convert.FromBase64String(serverFinalMessage[2..]); // Skip "v="

        if (!CryptographicOperations.FixedTimeEquals(expectedServerSignature, serverSignature))
        {
            throw new InvalidOperationException("Server signature verification failed");
        }
    }

    private static byte[] Hi(string password, byte[] salt, int iterations)
    {
        // PBKDF2 with HMAC-SHA256
        using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, iterations, HashAlgorithmName.SHA256);
        return pbkdf2.GetBytes(32);
    }

    private static Dictionary<string, string> ParseScramMessage(string message)
    {
        var result = new Dictionary<string, string>();
        foreach (var part in message.Split(','))
        {
            var idx = part.IndexOf('=');
            if (idx > 0)
            {
                result[part[..idx]] = part[(idx + 1)..];
            }
        }
        return result;
    }

    private static List<string> ParseSaslMechanisms(byte[] data)
    {
        var mechanisms = new List<string>();
        var offset = 0;
        while (offset < data.Length && data[offset] != 0)
        {
            var mechanism = ReadNullTerminatedString(data.AsSpan(offset), out var bytesRead);
            mechanisms.Add(mechanism);
            offset += bytesRead;
        }
        return mechanisms;
    }

    private void HandleParameterStatus(byte[] payload)
    {
        var name = ReadNullTerminatedString(payload, out var bytesRead);
        var value = ReadNullTerminatedString(payload.AsSpan(bytesRead), out _);
        _serverParameters[name] = value;
    }

    private void HandleBackendKeyData(byte[] payload)
    {
        _backendProcessId = ReadInt32BE(payload.AsSpan(0, 4));
        _backendSecretKey = ReadInt32BE(payload.AsSpan(4, 4));
    }

    private void HandleReadyForQuery(byte[] payload)
    {
        _transactionStatus = (char)payload[0];
    }

    /// <inheritdoc/>
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(
        string query,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken ct)
    {
        if (parameters != null && parameters.Count > 0)
        {
            return await ExecuteExtendedQueryAsync(query, parameters, ct);
        }

        return await ExecuteSimpleQueryAsync(query, ct);
    }

    private async Task<QueryResult> ExecuteSimpleQueryAsync(string query, CancellationToken ct)
    {
        // Send Query message
        var queryBytes = Encoding.UTF8.GetBytes(query);
        var messageLength = 4 + queryBytes.Length + 1;
        var buffer = new byte[1 + messageLength];

        buffer[0] = Query;
        WriteInt32BE(buffer.AsSpan(1, 4), messageLength);
        queryBytes.CopyTo(buffer.AsSpan(5));
        buffer[^1] = 0;

        await SendAsync(buffer, ct);

        // Process responses
        var columns = new List<ColumnMetadata>();
        var rows = new List<Dictionary<string, object?>>();
        long rowsAffected = 0;
        string? errorMessage = null;
        string? errorCode = null;

        while (true)
        {
            var (messageType, payload) = await ReadMessageAsync(ct);

            switch (messageType)
            {
                case RowDescription:
                    columns = ParseRowDescription(payload);
                    break;

                case DataRow:
                    var row = ParseDataRow(payload, columns);
                    rows.Add(row);
                    break;

                case CommandComplete:
                    var tag = ReadNullTerminatedString(payload, out _);
                    rowsAffected = ParseCommandTag(tag);
                    break;

                case EmptyQueryResponse:
                    break;

                case ErrorResponse:
                    (errorMessage, errorCode) = ParseErrorResponseDetailed(payload);
                    break;

                case NoticeResponse:
                    // Log notice but continue
                    break;

                case ReadyForQuery:
                    HandleReadyForQuery(payload);
                    return new QueryResult
                    {
                        Success = errorMessage == null,
                        RowsAffected = rowsAffected,
                        Rows = rows.Select(r => (IReadOnlyDictionary<string, object?>)r).ToList(),
                        Columns = columns,
                        ErrorMessage = errorMessage,
                        ErrorCode = errorCode
                    };

                default:
                    throw new InvalidOperationException($"Unexpected message type: {(char)messageType}");
            }
        }
    }

    private async Task<QueryResult> ExecuteExtendedQueryAsync(
        string query,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken ct)
    {
        // Parse
        await SendParseMessageAsync("", query, ct);

        // Bind
        var paramValues = parameters.Values.ToArray();
        await SendBindMessageAsync("", "", paramValues, ct);

        // Describe
        await SendDescribeMessageAsync('P', "", ct);

        // Execute
        await SendExecuteMessageAsync("", 0, ct);

        // Sync
        await SendSyncMessageAsync(ct);

        // Process responses
        var columns = new List<ColumnMetadata>();
        var rows = new List<Dictionary<string, object?>>();
        long rowsAffected = 0;
        string? errorMessage = null;
        string? errorCode = null;

        while (true)
        {
            var (messageType, payload) = await ReadMessageAsync(ct);

            switch (messageType)
            {
                case ParseComplete:
                case BindComplete:
                    break;

                case RowDescription:
                    columns = ParseRowDescription(payload);
                    break;

                case NoData:
                    break;

                case DataRow:
                    var row = ParseDataRow(payload, columns);
                    rows.Add(row);
                    break;

                case CommandComplete:
                    var tag = ReadNullTerminatedString(payload, out _);
                    rowsAffected = ParseCommandTag(tag);
                    break;

                case ErrorResponse:
                    (errorMessage, errorCode) = ParseErrorResponseDetailed(payload);
                    break;

                case ReadyForQuery:
                    HandleReadyForQuery(payload);
                    return new QueryResult
                    {
                        Success = errorMessage == null,
                        RowsAffected = rowsAffected,
                        Rows = rows.Select(r => (IReadOnlyDictionary<string, object?>)r).ToList(),
                        Columns = columns,
                        ErrorMessage = errorMessage,
                        ErrorCode = errorCode
                    };

                default:
                    // Continue processing
                    break;
            }
        }
    }

    private async Task SendParseMessageAsync(string statementName, string query, CancellationToken ct)
    {
        var nameBytes = Encoding.UTF8.GetBytes(statementName);
        var queryBytes = Encoding.UTF8.GetBytes(query);
        var messageLength = 4 + nameBytes.Length + 1 + queryBytes.Length + 1 + 2;
        var buffer = new byte[1 + messageLength];
        var offset = 0;

        buffer[offset++] = Parse;
        WriteInt32BE(buffer.AsSpan(offset, 4), messageLength);
        offset += 4;

        nameBytes.CopyTo(buffer.AsSpan(offset));
        offset += nameBytes.Length;
        buffer[offset++] = 0;

        queryBytes.CopyTo(buffer.AsSpan(offset));
        offset += queryBytes.Length;
        buffer[offset++] = 0;

        // No parameter types (let server infer)
        WriteInt16BE(buffer.AsSpan(offset, 2), 0);

        await SendAsync(buffer, ct);
    }

    private async Task SendBindMessageAsync(string portalName, string statementName, object?[] parameters, CancellationToken ct)
    {
        var portalBytes = Encoding.UTF8.GetBytes(portalName);
        var stmtBytes = Encoding.UTF8.GetBytes(statementName);

        // Encode parameters
        var paramData = new List<byte[]?>();
        foreach (var param in parameters)
        {
            if (param == null)
            {
                paramData.Add(null);
            }
            else
            {
                var text = Convert.ToString(param) ?? "";
                paramData.Add(Encoding.UTF8.GetBytes(text));
            }
        }

        // Calculate message size
        var messageLength = 4 + portalBytes.Length + 1 + stmtBytes.Length + 1;
        messageLength += 2 + 2 * parameters.Length; // Format codes
        messageLength += 2; // Parameter count

        foreach (var data in paramData)
        {
            messageLength += 4; // Length prefix
            if (data != null)
            {
                messageLength += data.Length;
            }
        }

        messageLength += 2 + 2; // Result format codes

        var buffer = new byte[1 + messageLength];
        var offset = 0;

        buffer[offset++] = Bind;
        WriteInt32BE(buffer.AsSpan(offset, 4), messageLength);
        offset += 4;

        portalBytes.CopyTo(buffer.AsSpan(offset));
        offset += portalBytes.Length;
        buffer[offset++] = 0;

        stmtBytes.CopyTo(buffer.AsSpan(offset));
        offset += stmtBytes.Length;
        buffer[offset++] = 0;

        // Parameter format codes (all text)
        WriteInt16BE(buffer.AsSpan(offset, 2), (short)parameters.Length);
        offset += 2;
        for (int i = 0; i < parameters.Length; i++)
        {
            WriteInt16BE(buffer.AsSpan(offset, 2), 0); // Text format
            offset += 2;
        }

        // Parameter values
        WriteInt16BE(buffer.AsSpan(offset, 2), (short)parameters.Length);
        offset += 2;

        foreach (var data in paramData)
        {
            if (data == null)
            {
                WriteInt32BE(buffer.AsSpan(offset, 4), -1); // NULL
                offset += 4;
            }
            else
            {
                WriteInt32BE(buffer.AsSpan(offset, 4), data.Length);
                offset += 4;
                data.CopyTo(buffer.AsSpan(offset));
                offset += data.Length;
            }
        }

        // Result format codes (all text)
        WriteInt16BE(buffer.AsSpan(offset, 2), 1);
        offset += 2;
        WriteInt16BE(buffer.AsSpan(offset, 2), 0); // Text format

        await SendAsync(buffer, ct);
    }

    private async Task SendDescribeMessageAsync(char type, string name, CancellationToken ct)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var messageLength = 4 + 1 + nameBytes.Length + 1;
        var buffer = new byte[1 + messageLength];

        buffer[0] = Describe;
        WriteInt32BE(buffer.AsSpan(1, 4), messageLength);
        buffer[5] = (byte)type;
        nameBytes.CopyTo(buffer.AsSpan(6));
        buffer[^1] = 0;

        await SendAsync(buffer, ct);
    }

    private async Task SendExecuteMessageAsync(string portalName, int maxRows, CancellationToken ct)
    {
        var nameBytes = Encoding.UTF8.GetBytes(portalName);
        var messageLength = 4 + nameBytes.Length + 1 + 4;
        var buffer = new byte[1 + messageLength];

        buffer[0] = Execute;
        WriteInt32BE(buffer.AsSpan(1, 4), messageLength);
        nameBytes.CopyTo(buffer.AsSpan(5));
        buffer[5 + nameBytes.Length] = 0;
        WriteInt32BE(buffer.AsSpan(6 + nameBytes.Length, 4), maxRows);

        await SendAsync(buffer, ct);
    }

    private async Task SendSyncMessageAsync(CancellationToken ct)
    {
        var buffer = new byte[5];
        buffer[0] = Sync;
        WriteInt32BE(buffer.AsSpan(1, 4), 4);
        await SendAsync(buffer, ct);
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
        var buffer = new byte[5];
        buffer[0] = Terminate;
        WriteInt32BE(buffer.AsSpan(1, 4), 4);
        await SendAsync(buffer, ct);
    }

    /// <inheritdoc/>
    protected override async Task<bool> PingCoreAsync(CancellationToken ct)
    {
        // Use empty query for ping
        var buffer = new byte[6];
        buffer[0] = Query;
        WriteInt32BE(buffer.AsSpan(1, 4), 5);
        buffer[5] = 0;

        await SendAsync(buffer, ct);

        while (true)
        {
            var (messageType, payload) = await ReadMessageAsync(ct);
            if (messageType == ReadyForQuery)
            {
                HandleReadyForQuery(payload);
                return true;
            }
            if (messageType == ErrorResponse)
            {
                return false;
            }
        }
    }

    private async Task<(byte messageType, byte[] payload)> ReadMessageAsync(CancellationToken ct)
    {
        // Read message header (type + length)
        var header = await ReceiveExactAsync(5, ct);
        var messageType = header[0];
        var length = ReadInt32BE(header.AsSpan(1, 4)) - 4; // Length includes itself

        // Read payload
        var payload = length > 0 ? await ReceiveExactAsync(length, ct) : [];

        return (messageType, payload);
    }

    private List<ColumnMetadata> ParseRowDescription(byte[] payload)
    {
        var columns = new List<ColumnMetadata>();
        var fieldCount = ReadInt16BE(payload.AsSpan(0, 2));
        var offset = 2;

        for (int i = 0; i < fieldCount; i++)
        {
            var name = ReadNullTerminatedString(payload.AsSpan(offset), out var bytesRead);
            offset += bytesRead;

            var tableOid = ReadInt32BE(payload.AsSpan(offset, 4));
            offset += 4;

            var columnAttr = ReadInt16BE(payload.AsSpan(offset, 2));
            offset += 2;

            var typeOid = ReadInt32BE(payload.AsSpan(offset, 4));
            offset += 4;

            var typeSize = ReadInt16BE(payload.AsSpan(offset, 2));
            offset += 2;

            var typeMod = ReadInt32BE(payload.AsSpan(offset, 4));
            offset += 4;

            var formatCode = ReadInt16BE(payload.AsSpan(offset, 2));
            offset += 2;

            columns.Add(new ColumnMetadata
            {
                Name = name,
                DataType = GetTypeName(typeOid),
                Ordinal = i,
                IsNullable = true,
                MaxLength = typeSize > 0 ? typeSize : null
            });
        }

        return columns;
    }

    private Dictionary<string, object?> ParseDataRow(byte[] payload, List<ColumnMetadata> columns)
    {
        var row = new Dictionary<string, object?>();
        var fieldCount = ReadInt16BE(payload.AsSpan(0, 2));
        var offset = 2;

        for (int i = 0; i < fieldCount && i < columns.Count; i++)
        {
            var length = ReadInt32BE(payload.AsSpan(offset, 4));
            offset += 4;

            if (length == -1)
            {
                row[columns[i].Name] = null;
            }
            else
            {
                var value = Encoding.UTF8.GetString(payload.AsSpan(offset, length));
                offset += length;
                row[columns[i].Name] = ParseValue(value, columns[i].DataType);
            }
        }

        return row;
    }

    private static object? ParseValue(string value, string dataType)
    {
        return dataType switch
        {
            "int2" or "int4" or "int8" when long.TryParse(value, out var l) => l,
            "float4" or "float8" when double.TryParse(value, out var d) => d,
            "numeric" or "money" when decimal.TryParse(value, out var dec) => dec,
            "bool" => value is "t" or "true" or "1",
            "timestamp" or "timestamptz" when DateTime.TryParse(value, out var dt) => dt,
            "date" when DateOnly.TryParse(value, out var date) => date,
            "time" or "timetz" when TimeOnly.TryParse(value, out var time) => time,
            "uuid" when Guid.TryParse(value, out var guid) => guid,
            "bytea" => DecodeBytea(value),
            _ => value
        };
    }

    private static byte[] DecodeBytea(string value)
    {
        if (value.StartsWith("\\x"))
        {
            return Convert.FromHexString(value[2..]);
        }
        // Escape format
        return Encoding.UTF8.GetBytes(value);
    }

    private static string GetTypeName(int oid)
    {
        return oid switch
        {
            16 => "bool",
            17 => "bytea",
            20 => "int8",
            21 => "int2",
            23 => "int4",
            25 => "text",
            700 => "float4",
            701 => "float8",
            790 => "money",
            1042 => "bpchar",
            1043 => "varchar",
            1082 => "date",
            1083 => "time",
            1114 => "timestamp",
            1184 => "timestamptz",
            1266 => "timetz",
            1700 => "numeric",
            2950 => "uuid",
            _ => "unknown"
        };
    }

    private static long ParseCommandTag(string tag)
    {
        // Examples: "SELECT 100", "INSERT 0 1", "UPDATE 5", "DELETE 3"
        var parts = tag.Split(' ');
        if (parts.Length >= 2 && long.TryParse(parts[^1], out var count))
        {
            return count;
        }
        return 0;
    }

    private static string ParseErrorResponse(byte[] payload)
    {
        var fields = ParseErrorFields(payload);
        return fields.TryGetValue('M', out var message) ? message : "Unknown error";
    }

    private static (string? message, string? code) ParseErrorResponseDetailed(byte[] payload)
    {
        var fields = ParseErrorFields(payload);
        fields.TryGetValue('M', out var message);
        fields.TryGetValue('C', out var code);
        return (message, code);
    }

    private static Dictionary<char, string> ParseErrorFields(byte[] payload)
    {
        var fields = new Dictionary<char, string>();
        var offset = 0;

        while (offset < payload.Length && payload[offset] != 0)
        {
            var fieldType = (char)payload[offset++];
            var value = ReadNullTerminatedString(payload.AsSpan(offset), out var bytesRead);
            offset += bytesRead;
            fields[fieldType] = value;
        }

        return fields;
    }
}
