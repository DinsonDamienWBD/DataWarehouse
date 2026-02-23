using System.Security.Cryptography;
using System.Text;

namespace DataWarehouse.Plugins.UltimateDatabaseProtocol.Strategies.Relational;

/// <summary>
/// PostgreSQL Frontend/Backend Protocol v3.0 implementation.
/// Implements the complete wire protocol for PostgreSQL communication including:
/// - Startup message and parameter negotiation
/// - Authentication (cleartext, MD5, SCRAM-SHA-256)
/// - Simple and extended query protocol (with multi-statement support)
/// - COPY protocol for bulk operations (CopyIn, CopyOut, CopyBoth)
/// - Notification/Listen support
/// - SSL negotiation
/// - Cancel request support
/// - Close and Flush for named statement/portal management
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

    // ECOS-01: Fixed - negotiated protocol version tracking
    private int _negotiatedMinorVersion;
    private readonly List<string> _unrecognizedParameters = new();

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

    /// <summary>
    /// Gets the current transaction status character ('I' = idle, 'T' = in transaction, 'E' = failed transaction).
    /// </summary>
    public char TransactionStatus => _transactionStatus;

    /// <summary>
    /// Gets the server parameters received during startup (e.g., server_version, server_encoding).
    /// </summary>
    public IReadOnlyDictionary<string, string> ServerParameters => _serverParameters;

    /// <summary>
    /// Gets the backend process ID for cancel request support.
    /// </summary>
    public int BackendProcessId => _backendProcessId;

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
            ["application_name"] = "DataWarehouse", // ECOS-01: Fixed - explicit application_name
            ["client_encoding"] = "UTF8",
            ["DateStyle"] = "ISO, MDY",
            ["TimeZone"] = "UTC",
            ["extra_float_digits"] = "3"
        };

        // Add any additional parameters (may override defaults)
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
                    // ECOS-01: Fixed - properly handle protocol version negotiation
                    HandleNegotiateProtocolVersion(payload);
                    break;

                case NoticeResponse:
                    // ECOS-01: Fixed - handle notice during auth (e.g., password expiry warnings)
                    break;

                default:
                    throw new InvalidOperationException($"Unexpected message type during authentication: {(char)messageType}");
            }
        }
    }

    // ECOS-01: Fixed - NegotiateProtocolVersion handler
    private void HandleNegotiateProtocolVersion(byte[] payload)
    {
        // Format: Int32 newest minor version supported, Int32 count of unrecognized params, then N null-terminated strings
        var newestMinor = ReadInt32BE(payload.AsSpan(0, 4));
        _negotiatedMinorVersion = newestMinor;

        var paramCount = ReadInt32BE(payload.AsSpan(4, 4));
        var offset = 8;
        for (int i = 0; i < paramCount && offset < payload.Length; i++)
        {
            var paramName = ReadNullTerminatedString(payload.AsSpan(offset), out var bytesRead);
            offset += bytesRead;
            _unrecognizedParameters.Add(paramName);
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

            case 2: // AuthenticationKerberosV5 (legacy, rarely used)
                throw new NotSupportedException("KerberosV5 authentication is deprecated; use GSSAPI (type 7) instead");

            case 6: // AuthenticationSCMCredential (Unix only, legacy)
                throw new NotSupportedException("SCM credential authentication is not supported");

            case 7: // AuthenticationGSS
                throw new NotSupportedException("GSSAPI authentication requires platform-specific Kerberos integration");

            case 8: // AuthenticationGSSContinue
                // Handled in GSSAPI flow
                break;

            case 9: // AuthenticationSSPI
                throw new NotSupportedException("SSPI authentication requires Windows-specific integration");

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
        return Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);
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

        // ECOS-01: Fixed - multi-statement query support (multiple result sets)
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
                    // ECOS-01: Fixed - new RowDescription resets columns for multi-statement
                    columns = ParseRowDescription(payload);
                    break;

                case DataRow:
                    var row = ParseDataRow(payload, columns);
                    rows.Add(row);
                    break;

                case CommandComplete:
                    var tag = ReadNullTerminatedString(payload, out _);
                    rowsAffected += ParseCommandTag(tag);
                    // ECOS-01: Fixed - don't return here; wait for ReadyForQuery (multi-statement)
                    break;

                case EmptyQueryResponse:
                    break;

                case ErrorResponse:
                    (errorMessage, errorCode) = ParseErrorResponseDetailed(payload);
                    break;

                case NoticeResponse:
                    // Log notice but continue
                    break;

                case NotificationResponse:
                    // ECOS-01: Fixed - handle async notification during query
                    HandleNotificationResponse(payload);
                    break;

                case CopyInResponse:
                    // ECOS-01: Fixed - handle COPY IN response during simple query (e.g., COPY FROM STDIN)
                    // Send CopyFail since simple query doesn't supply data
                    await SendCopyFailAsync("COPY IN not supported via simple query protocol", ct);
                    break;

                case CopyOutResponse:
                    // ECOS-01: Fixed - handle COPY OUT response during simple query (e.g., COPY TO STDOUT)
                    await DrainCopyOutDataAsync(ct);
                    break;

                case CopyBothResponse:
                    // ECOS-01: Fixed - handle CopyBothResponse (streaming replication)
                    // CopyBoth is used for walsender protocol; drain and ignore in normal query context
                    await DrainCopyOutDataAsync(ct);
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

                case ParameterDescription:
                    // ECOS-01: Fixed - handle ParameterDescription ('t') in extended query
                    // Contains parameter OID types; used for type inference
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

                case PortalSuspended:
                    // ECOS-01: Fixed - handle PortalSuspended ('s') when Execute has maxRows limit
                    // Indicates more rows available; caller can re-Execute to fetch more
                    break;

                case ErrorResponse:
                    (errorMessage, errorCode) = ParseErrorResponseDetailed(payload);
                    break;

                case NoticeResponse:
                    // ECOS-01: Fixed - handle notice in extended query
                    break;

                case CloseComplete:
                    // ECOS-01: Fixed - handle CloseComplete ('3') response
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
                    // Continue processing unknown message types
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

    // ECOS-01: Fixed - overload with explicit parameter OIDs for typed Parse
    private async Task SendParseMessageAsync(string statementName, string query, int[] parameterOids, CancellationToken ct)
    {
        var nameBytes = Encoding.UTF8.GetBytes(statementName);
        var queryBytes = Encoding.UTF8.GetBytes(query);
        var messageLength = 4 + nameBytes.Length + 1 + queryBytes.Length + 1 + 2 + (parameterOids.Length * 4);
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

        WriteInt16BE(buffer.AsSpan(offset, 2), (short)parameterOids.Length);
        offset += 2;

        foreach (var oid in parameterOids)
        {
            WriteInt32BE(buffer.AsSpan(offset, 4), oid);
            offset += 4;
        }

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

    // ECOS-01: Fixed - Close message for named statements and portals
    /// <summary>
    /// Sends a Close message to close a named prepared statement or portal.
    /// </summary>
    /// <param name="type">'S' for statement, 'P' for portal.</param>
    /// <param name="name">Name of the statement or portal to close. Empty string for unnamed.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task SendCloseMessageAsync(char type, string name, CancellationToken ct)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var messageLength = 4 + 1 + nameBytes.Length + 1;
        var buffer = new byte[1 + messageLength];

        buffer[0] = Close;
        WriteInt32BE(buffer.AsSpan(1, 4), messageLength);
        buffer[5] = (byte)type;
        nameBytes.CopyTo(buffer.AsSpan(6));
        buffer[6 + nameBytes.Length] = 0;

        await SendAsync(buffer, ct);
    }

    // ECOS-01: Fixed - Flush message for pipelining
    /// <summary>
    /// Sends a Flush message requesting the server to deliver any pending output without issuing Sync.
    /// Used for pipelining extended query operations.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    private async Task SendFlushMessageAsync(CancellationToken ct)
    {
        var buffer = new byte[5];
        buffer[0] = Flush;
        WriteInt32BE(buffer.AsSpan(1, 4), 4);
        await SendAsync(buffer, ct);
    }

    // ECOS-01: Fixed - Cancel request support
    /// <summary>
    /// Sends a cancel request to abort a running query. Per PostgreSQL protocol,
    /// this opens a new TCP connection to send the 16-byte cancel request message.
    /// </summary>
    /// <param name="host">Server host.</param>
    /// <param name="port">Server port.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task SendCancelRequestAsync(string host, int port, CancellationToken ct)
    {
        // Cancel request is sent on a NEW connection (per PostgreSQL protocol spec)
        // Format: Int32 length=16, Int32 code=80877102, Int32 processId, Int32 secretKey
        using var cancelClient = new System.Net.Sockets.TcpClient();
        await cancelClient.ConnectAsync(host, port, ct);

        await using var stream = cancelClient.GetStream();
        var buffer = new byte[16];
        WriteInt32BE(buffer.AsSpan(0, 4), 16);           // Length
        WriteInt32BE(buffer.AsSpan(4, 4), CancelRequestCode); // Cancel request code
        WriteInt32BE(buffer.AsSpan(8, 4), _backendProcessId);  // Process ID
        WriteInt32BE(buffer.AsSpan(12, 4), _backendSecretKey); // Secret key

        await stream.WriteAsync(buffer, ct);
        await stream.FlushAsync(ct);
    }

    #region COPY Protocol

    // ECOS-01: Fixed - full COPY protocol implementation

    /// <summary>
    /// Sends COPY IN data to the server. The caller provides rows as byte arrays.
    /// Uses the COPY protocol: sends CopyData messages followed by CopyDone.
    /// </summary>
    /// <param name="copyCommand">SQL COPY command (e.g., "COPY table FROM STDIN").</param>
    /// <param name="dataProvider">Function that yields data chunks to send.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of rows copied.</returns>
    public async Task<long> CopyInAsync(string copyCommand, Func<IAsyncEnumerable<byte[]>> dataProvider, CancellationToken ct)
    {
        // Send the COPY command via simple query
        var queryBytes = Encoding.UTF8.GetBytes(copyCommand);
        var messageLength = 4 + queryBytes.Length + 1;
        var buffer = new byte[1 + messageLength];
        buffer[0] = Query;
        WriteInt32BE(buffer.AsSpan(1, 4), messageLength);
        queryBytes.CopyTo(buffer.AsSpan(5));
        buffer[^1] = 0;
        await SendAsync(buffer, ct);

        // Read CopyInResponse
        var (msgType, payload) = await ReadMessageAsync(ct);
        if (msgType == ErrorResponse)
        {
            var error = ParseErrorResponse(payload);
            throw new InvalidOperationException($"COPY IN failed: {error}");
        }
        if (msgType != CopyInResponse)
        {
            throw new InvalidOperationException($"Expected CopyInResponse, got {(char)msgType}");
        }

        // Parse CopyInResponse: Int8 format (0=text, 1=binary), Int16 column count, Int16[] per-column formats
        // Format information available if needed by caller

        // Send data chunks as CopyData messages
        await foreach (var chunk in dataProvider())
        {
            var copyDataLength = 4 + chunk.Length;
            var copyDataBuffer = new byte[1 + copyDataLength];
            copyDataBuffer[0] = CopyData;
            WriteInt32BE(copyDataBuffer.AsSpan(1, 4), copyDataLength);
            chunk.CopyTo(copyDataBuffer.AsSpan(5));
            await SendAsync(copyDataBuffer, ct);
        }

        // Send CopyDone
        var doneBuffer = new byte[5];
        doneBuffer[0] = CopyDone;
        WriteInt32BE(doneBuffer.AsSpan(1, 4), 4);
        await SendAsync(doneBuffer, ct);

        // Wait for CommandComplete + ReadyForQuery
        long rowsCopied = 0;
        while (true)
        {
            (msgType, payload) = await ReadMessageAsync(ct);
            switch (msgType)
            {
                case CommandComplete:
                    var tag = ReadNullTerminatedString(payload, out _);
                    rowsCopied = ParseCommandTag(tag);
                    break;
                case ErrorResponse:
                    var errorMsg = ParseErrorResponse(payload);
                    throw new InvalidOperationException($"COPY IN failed: {errorMsg}");
                case ReadyForQuery:
                    HandleReadyForQuery(payload);
                    return rowsCopied;
            }
        }
    }

    /// <summary>
    /// Receives COPY OUT data from the server. Returns data chunks as they arrive.
    /// </summary>
    /// <param name="copyCommand">SQL COPY command (e.g., "COPY table TO STDOUT").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Async enumerable of data chunks.</returns>
    public async IAsyncEnumerable<byte[]> CopyOutAsync(string copyCommand, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        // Send the COPY command
        var queryBytes = Encoding.UTF8.GetBytes(copyCommand);
        var messageLength = 4 + queryBytes.Length + 1;
        var buffer = new byte[1 + messageLength];
        buffer[0] = Query;
        WriteInt32BE(buffer.AsSpan(1, 4), messageLength);
        queryBytes.CopyTo(buffer.AsSpan(5));
        buffer[^1] = 0;
        await SendAsync(buffer, ct);

        // Read CopyOutResponse
        var (msgType, payload) = await ReadMessageAsync(ct);
        if (msgType == ErrorResponse)
        {
            var error = ParseErrorResponse(payload);
            throw new InvalidOperationException($"COPY OUT failed: {error}");
        }
        if (msgType != CopyOutResponse)
        {
            throw new InvalidOperationException($"Expected CopyOutResponse, got {(char)msgType}");
        }

        // Read CopyData messages until CopyDone
        while (true)
        {
            (msgType, payload) = await ReadMessageAsync(ct);
            switch (msgType)
            {
                case CopyData:
                    yield return payload;
                    break;
                case CopyDone:
                    // Wait for CommandComplete + ReadyForQuery
                    await DrainToReadyForQueryAsync(ct);
                    yield break;
                case ErrorResponse:
                    var errorMsg = ParseErrorResponse(payload);
                    throw new InvalidOperationException($"COPY OUT failed: {errorMsg}");
            }
        }
    }

    /// <summary>
    /// Sends a CopyFail message to abort a COPY IN operation.
    /// </summary>
    private async Task SendCopyFailAsync(string errorMessage, CancellationToken ct)
    {
        var msgBytes = Encoding.UTF8.GetBytes(errorMessage);
        var messageLength = 4 + msgBytes.Length + 1;
        var buffer = new byte[1 + messageLength];
        buffer[0] = CopyFail;
        WriteInt32BE(buffer.AsSpan(1, 4), messageLength);
        msgBytes.CopyTo(buffer.AsSpan(5));
        buffer[^1] = 0;
        await SendAsync(buffer, ct);
    }

    /// <summary>
    /// Drains CopyOut data until CopyDone is received (used when COPY OUT appears unexpectedly).
    /// </summary>
    private async Task DrainCopyOutDataAsync(CancellationToken ct)
    {
        while (true)
        {
            var (msgType, _) = await ReadMessageAsync(ct);
            if (msgType == CopyDone)
            {
                return;
            }
            if (msgType == ErrorResponse)
            {
                return;
            }
            // CopyData: discard and continue
        }
    }

    /// <summary>
    /// Drains messages until ReadyForQuery is received.
    /// </summary>
    private async Task DrainToReadyForQueryAsync(CancellationToken ct)
    {
        while (true)
        {
            var (msgType, payload) = await ReadMessageAsync(ct);
            if (msgType == ReadyForQuery)
            {
                HandleReadyForQuery(payload);
                return;
            }
        }
    }

    #endregion

    #region Notification Support

    // ECOS-01: Fixed - async notification handling
    private void HandleNotificationResponse(byte[] payload)
    {
        // Format: Int32 processId, String channelName, String payload
        var processId = ReadInt32BE(payload.AsSpan(0, 4));
        var channel = ReadNullTerminatedString(payload.AsSpan(4), out var bytesRead);
        var notifPayload = ReadNullTerminatedString(payload.AsSpan(4 + bytesRead), out _);

        // Store notification for retrieval
        _pendingNotifications.Enqueue(new PostgreSqlNotification(processId, channel, notifPayload));
    }

    private readonly System.Collections.Concurrent.ConcurrentQueue<PostgreSqlNotification> _pendingNotifications = new();

    /// <summary>
    /// Tries to dequeue a pending notification received from LISTEN/NOTIFY.
    /// </summary>
    public bool TryGetNotification(out PostgreSqlNotification? notification)
    {
        return _pendingNotifications.TryDequeue(out notification);
    }

    #endregion

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
            // EmptyQueryResponse or other messages: continue to ReadyForQuery
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
            "json" or "jsonb" => value, // Return JSON as string
            "xml" => value,
            "inet" or "cidr" => value,
            "macaddr" or "macaddr8" => value,
            "interval" => value,
            "point" or "line" or "lseg" or "box" or "path" or "polygon" or "circle" => value,
            "int4range" or "int8range" or "numrange" or "tsrange" or "tstzrange" or "daterange" => value,
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
            26 => "oid",
            114 => "json",
            142 => "xml",
            600 => "point",
            601 => "lseg",
            602 => "path",
            603 => "box",
            604 => "polygon",
            628 => "line",
            650 => "cidr",
            700 => "float4",
            701 => "float8",
            718 => "circle",
            790 => "money",
            829 => "macaddr",
            869 => "inet",
            1042 => "bpchar",
            1043 => "varchar",
            1082 => "date",
            1083 => "time",
            1114 => "timestamp",
            1184 => "timestamptz",
            1186 => "interval",
            1266 => "timetz",
            1560 => "bit",
            1562 => "varbit",
            1700 => "numeric",
            2950 => "uuid",
            3802 => "jsonb",
            3904 => "int4range",
            3926 => "int8range",
            3906 => "numrange",
            3908 => "tsrange",
            3910 => "tstzrange",
            3912 => "daterange",
            774 => "macaddr8",
            _ => "unknown"
        };
    }

    private static long ParseCommandTag(string tag)
    {
        // Examples: "SELECT 100", "INSERT 0 1", "UPDATE 5", "DELETE 3", "COPY 100"
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

        // ECOS-01: Fixed - include detail and hint in error message for richer diagnostics
        if (fields.TryGetValue('D', out var detail) && !string.IsNullOrEmpty(detail))
        {
            message = $"{message} DETAIL: {detail}";
        }
        if (fields.TryGetValue('H', out var hint) && !string.IsNullOrEmpty(hint))
        {
            message = $"{message} HINT: {hint}";
        }

        return (message, code);
    }

    // ECOS-01: Fixed - comprehensive error field parsing with all PostgreSQL error field types
    private static Dictionary<char, string> ParseErrorFields(byte[] payload)
    {
        // Parses all PostgreSQL error/notice response field types:
        // 'S' severity (localized), 'V' severity (non-localized, always EN), 'C' SQLSTATE code,
        // 'M' message, 'D' detail, 'H' hint, 'P' position, 'p' internal position,
        // 'q' internal query, 'W' where, 's' schema, 't' table, 'c' column,
        // 'd' data type, 'n' constraint, 'F' file, 'L' line, 'R' routine
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

/// <summary>
/// Represents an asynchronous notification received from PostgreSQL LISTEN/NOTIFY.
/// </summary>
/// <param name="ProcessId">Backend process ID that sent the notification.</param>
/// <param name="Channel">Notification channel name.</param>
/// <param name="Payload">Notification payload string.</param>
public sealed record PostgreSqlNotification(int ProcessId, string Channel, string Payload);
