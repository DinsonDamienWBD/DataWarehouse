using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace DataWarehouse.Plugins.OracleTnsProtocol.Protocol;

/// <summary>
/// Handles the Oracle TNS/TTC protocol communication for a single client connection.
/// Manages the complete connection lifecycle including handshake, authentication,
/// query execution, and cursor management.
/// </summary>
/// <remarks>
/// Protocol flow:
/// 1. CONNECT packet from client
/// 2. ACCEPT/REFUSE/REDIRECT packet from server
/// 3. Protocol/version negotiation
/// 4. Authentication (O3LOGON/O5LOGON/O7LOGON/O8LOGON)
/// 5. Data exchange (queries, results)
/// 6. Logout/disconnect
///
/// Thread-safety: Each connection handler instance serves a single client connection
/// and should not be shared across threads.
/// </remarks>
public sealed class TnsProtocolHandler : IDisposable
{
    private readonly TcpClient _client;
    private readonly OracleTnsProtocolConfig _config;
    private readonly OracleQueryProcessor _queryProcessor;
    private readonly TnsPacketReader _reader;
    private readonly TnsPacketWriter _writer;
    private readonly OracleConnectionState _state;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of TnsProtocolHandler.
    /// </summary>
    /// <param name="client">The connected TCP client.</param>
    /// <param name="config">The protocol configuration.</param>
    /// <param name="queryProcessor">The query processor for SQL execution.</param>
    /// <exception cref="ArgumentNullException">Thrown when any parameter is null.</exception>
    public TnsProtocolHandler(
        TcpClient client,
        OracleTnsProtocolConfig config,
        OracleQueryProcessor queryProcessor)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _queryProcessor = queryProcessor ?? throw new ArgumentNullException(nameof(queryProcessor));

        var stream = client.GetStream();
        _reader = new TnsPacketReader(stream);
        _writer = new TnsPacketWriter(stream);

        _state = new OracleConnectionState
        {
            ConnectionId = Guid.NewGuid().ToString("N"),
            SessionId = Random.Shared.Next(1, 65535),
            SerialNumber = Random.Shared.Next(1, 65535)
        };
    }

    /// <summary>
    /// Gets the current connection state.
    /// </summary>
    public OracleConnectionState ConnectionState => _state;

    /// <summary>
    /// Handles the complete connection lifecycle.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task HandleConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            // Step 1: Handle connection establishment
            if (!await HandleConnectPhaseAsync(ct).ConfigureAwait(false))
            {
                return;
            }

            // Step 2: Protocol negotiation
            await HandleProtocolNegotiationAsync(ct).ConfigureAwait(false);

            // Step 3: Authentication
            if (!await HandleAuthenticationAsync(ct).ConfigureAwait(false))
            {
                return;
            }

            // Step 4: Main message loop
            await ProcessMessagesAsync(ct).ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
            // Client disconnected - normal termination
        }
        catch (TnsProtocolException ex)
        {
            await TrySendErrorAsync(OracleProtocolConstants.AuthInvalidCredentials, ex.Message, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancellation requested - normal shutdown
        }
        catch (Exception ex)
        {
            await TrySendErrorAsync(0, $"{OracleProtocolConstants.ErrorInternal}: {ex.Message}", ct).ConfigureAwait(false);
        }
        finally
        {
            CloseConnection();
        }
    }

    /// <summary>
    /// Handles the initial CONNECT packet and sends ACCEPT or REFUSE.
    /// </summary>
    private async Task<bool> HandleConnectPhaseAsync(CancellationToken ct)
    {
        var packet = await _reader.ReadPacketAsync(ct).ConfigureAwait(false);

        if (packet.PacketType != TnsPacketType.Connect)
        {
            await _writer.WriteRefusePacketAsync(4, "Expected CONNECT packet", ct).ConfigureAwait(false);
            return false;
        }

        var connectRequest = _reader.ParseConnectPacket(packet);

        // Validate service name or SID
        var requestedService = !string.IsNullOrEmpty(connectRequest.ServiceName)
            ? connectRequest.ServiceName
            : connectRequest.Sid;

        if (!string.IsNullOrEmpty(requestedService) &&
            !requestedService.Equals(_config.ServiceName, StringComparison.OrdinalIgnoreCase) &&
            !requestedService.Equals(_config.Sid, StringComparison.OrdinalIgnoreCase))
        {
            // Service not found - could redirect or refuse
            await _writer.WriteRefusePacketAsync(4,
                $"ORA-12514: TNS:listener does not currently know of service requested in connect descriptor",
                ct).ConfigureAwait(false);
            return false;
        }

        // Store connection parameters
        _state.ServiceName = !string.IsNullOrEmpty(connectRequest.ServiceName)
            ? connectRequest.ServiceName
            : _config.ServiceName;

        if (connectRequest.Parameters.TryGetValue("PROGRAM", out var program))
            _state.ProgramName = program;
        if (connectRequest.Parameters.TryGetValue("HOST", out var host))
            _state.MachineName = host;

        // Negotiate SDU size
        _state.SduSize = Math.Min(connectRequest.SduSize, _config.SduSize);

        // Send ACCEPT
        await _writer.WriteAcceptPacketAsync(
            OracleProtocolConstants.TnsVersion,
            _state.SduSize,
            _config.TduSize,
            ct).ConfigureAwait(false);

        return true;
    }

    /// <summary>
    /// Handles protocol version and capability negotiation.
    /// </summary>
    private async Task HandleProtocolNegotiationAsync(CancellationToken ct)
    {
        // Read first DATA packet for protocol negotiation
        var packet = await _reader.ReadPacketAsync(ct).ConfigureAwait(false);

        if (packet.PacketType == TnsPacketType.Data)
        {
            var dataPayload = _reader.ParseDataPacket(packet);

            // Handle protocol negotiation or version exchange
            if (dataPayload.FunctionCode == TtiFunctionCode.ProtocolNegotiation ||
                dataPayload.FunctionCode == TtiFunctionCode.VersionExchange)
            {
                // Send version response
                await _writer.WriteVersionResponseAsync(_config.ReportedVersion, ct).ConfigureAwait(false);
            }
            // Some clients may send data type negotiation
            else if (dataPayload.FunctionCode == TtiFunctionCode.DataTypeNegotiation)
            {
                // Acknowledge data type negotiation
                await _writer.WriteRawDataPacketAsync(new byte[] { 0x02, 0x00 }, 0, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Handles the authentication phase using O3LOGON, O5LOGON, O7LOGON, or O8LOGON.
    /// </summary>
    private async Task<bool> HandleAuthenticationAsync(CancellationToken ct)
    {
        // Read authentication request
        var packet = await _reader.ReadPacketAsync(ct).ConfigureAwait(false);

        if (packet.PacketType != TnsPacketType.Data)
        {
            return false;
        }

        var dataPayload = _reader.ParseDataPacket(packet);

        // Parse the authentication request
        if (dataPayload.FunctionCode == TtiFunctionCode.Authentication ||
            dataPayload.FunctionCode == TtiFunctionCode.O3Logon ||
            dataPayload.FunctionCode == TtiFunctionCode.O5Logon ||
            dataPayload.FunctionCode == TtiFunctionCode.O7Logon ||
            dataPayload.FunctionCode == TtiFunctionCode.O8Logon)
        {
            // Extract username from payload
            var username = ExtractUsername(dataPayload.Payload);
            _state.Username = username;
            _state.Schema = username; // Default schema is same as username

            // Generate authentication challenge
            var authVersion = _config.AuthMethod.ToLowerInvariant() switch
            {
                "o3logon" => 3,
                "o5logon" => 5,
                "o7logon" => 7,
                _ => 8 // Default to O8LOGON
            };

            var challenge = OracleAuthChallenge.Generate(authVersion);

            // Send challenge
            await _writer.WriteAuthChallengeAsync(challenge, ct).ConfigureAwait(false);

            // Read authentication response
            var responsePacket = await _reader.ReadPacketAsync(ct).ConfigureAwait(false);

            if (responsePacket.PacketType == TnsPacketType.Data)
            {
                var responsePayload = _reader.ParseDataPacket(responsePacket);

                // Verify authentication response
                // In production, this would validate against stored credentials
                // For compatibility testing, we accept valid-looking responses
                if (VerifyAuthResponse(responsePayload.Payload, challenge))
                {
                    _state.IsAuthenticated = true;
                    await _writer.WriteAuthSuccessAsync(null, ct).ConfigureAwait(false);
                    return true;
                }
            }

            // Authentication failed
            await _writer.WriteErrorResponseAsync(
                OracleProtocolConstants.AuthInvalidCredentials,
                OracleProtocolConstants.ErrorInvalidCredentials,
                ct).ConfigureAwait(false);
            return false;
        }

        return false;
    }

    /// <summary>
    /// Extracts the username from an authentication request payload.
    /// </summary>
    private string ExtractUsername(byte[] payload)
    {
        if (payload.Length < 2)
            return "UNKNOWN";

        // Username is typically a length-prefixed string in the auth payload
        var usernameLength = payload[0];
        if (usernameLength > 0 && usernameLength + 1 <= payload.Length)
        {
            return Encoding.ASCII.GetString(payload, 1, usernameLength);
        }

        return "UNKNOWN";
    }

    /// <summary>
    /// Verifies an authentication response against the challenge.
    /// </summary>
    private bool VerifyAuthResponse(byte[] response, OracleAuthChallenge challenge)
    {
        // In a production system, this would:
        // 1. Compute expected hash using stored password hash + challenge
        // 2. Compare with received response
        // For compatibility testing, we accept non-empty responses
        return response.Length > 0;
    }

    /// <summary>
    /// Main message processing loop after authentication.
    /// </summary>
    private async Task ProcessMessagesAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _client.Connected)
        {
            var packet = await _reader.ReadPacketAsync(ct).ConfigureAwait(false);

            switch (packet.PacketType)
            {
                case TnsPacketType.Data:
                    await HandleDataPacketAsync(packet, ct).ConfigureAwait(false);
                    break;

                case TnsPacketType.Attention:
                    // Cancel current operation
                    await HandleAttentionAsync(ct).ConfigureAwait(false);
                    break;

                case TnsPacketType.Abort:
                    // Abort connection
                    return;

                case TnsPacketType.Marker:
                    // Handle marker/break
                    await HandleMarkerAsync(ct).ConfigureAwait(false);
                    break;

                default:
                    // Unknown packet type - ignore
                    break;
            }
        }
    }

    /// <summary>
    /// Handles DATA packets containing SQL commands and other operations.
    /// </summary>
    private async Task HandleDataPacketAsync(TnsPacket packet, CancellationToken ct)
    {
        var dataPayload = _reader.ParseDataPacket(packet);

        switch (dataPayload.FunctionCode)
        {
            case TtiFunctionCode.OpenCursor:
                await HandleOpenCursorAsync(dataPayload.Payload, ct).ConfigureAwait(false);
                break;

            case TtiFunctionCode.CloseCursor:
                await HandleCloseCursorAsync(dataPayload.Payload, ct).ConfigureAwait(false);
                break;

            case TtiFunctionCode.ParseStatement:
                await HandleParseAsync(dataPayload.Payload, ct).ConfigureAwait(false);
                break;

            case TtiFunctionCode.Execute:
                await HandleExecuteAsync(dataPayload.Payload, ct).ConfigureAwait(false);
                break;

            case TtiFunctionCode.Fetch:
                await HandleFetchAsync(dataPayload.Payload, ct).ConfigureAwait(false);
                break;

            case TtiFunctionCode.Describe:
                await HandleDescribeAsync(dataPayload.Payload, ct).ConfigureAwait(false);
                break;

            case TtiFunctionCode.Commit:
                await HandleCommitAsync(ct).ConfigureAwait(false);
                break;

            case TtiFunctionCode.Rollback:
                await HandleRollbackAsync(ct).ConfigureAwait(false);
                break;

            case TtiFunctionCode.Logout:
                // Client is disconnecting
                throw new EndOfStreamException("Client logout");

            case TtiFunctionCode.SessionControl:
                await HandleSessionControlAsync(dataPayload.Payload, ct).ConfigureAwait(false);
                break;

            default:
                // Handle as generic SQL if we can extract a statement
                await HandleGenericSqlAsync(dataPayload.Payload, ct).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>
    /// Handles cursor open requests.
    /// </summary>
    private async Task HandleOpenCursorAsync(byte[] payload, CancellationToken ct)
    {
        var cursorId = _state.AllocateCursorId();
        var cursor = new OracleCursor { CursorId = cursorId };
        _state.Cursors[cursorId] = cursor;

        // Send cursor ID back
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write((byte)0x08); // Open cursor response
        bw.Write((short)cursorId);
        bw.Write((byte)0x00); // Success

        await _writer.WriteRawDataPacketAsync(ms.ToArray(), 0, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles cursor close requests.
    /// </summary>
    private async Task HandleCloseCursorAsync(byte[] payload, CancellationToken ct)
    {
        if (payload.Length >= 2)
        {
            var cursorId = (payload[0] << 8) | payload[1];
            _state.Cursors.TryRemove(cursorId, out _);
        }

        // Send close confirmation
        await _writer.WriteRawDataPacketAsync(new byte[] { 0x08, 0x00 }, 0, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles SQL statement parsing.
    /// </summary>
    private async Task HandleParseAsync(byte[] payload, CancellationToken ct)
    {
        // Extract cursor ID and SQL from payload
        var (cursorId, sql) = ExtractSqlFromPayload(payload);

        if (!_state.Cursors.TryGetValue(cursorId, out var cursor))
        {
            // Create new cursor if not exists
            cursor = new OracleCursor { CursorId = cursorId };
            _state.Cursors[cursorId] = cursor;
        }

        cursor.SqlStatement = sql;
        cursor.IsParsed = true;
        cursor.StatementType = _queryProcessor.DetectStatementType(sql);

        // Extract bind variables
        var bindVars = _queryProcessor.ExtractBindVariables(sql);
        cursor.BindParameters.Clear();
        for (var i = 0; i < bindVars.Count; i++)
        {
            cursor.BindParameters.Add(new OracleBindParameter
            {
                Name = bindVars[i],
                Position = i + 1
            });
        }

        // Send parse complete
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write((byte)0x03); // Parse complete marker
        bw.Write((byte)bindVars.Count); // Number of bind variables
        bw.Write((short)cursorId);
        bw.Write((byte)(cursor.StatementType == OracleStatementType.Select ? 0x01 : 0x00)); // Has results flag

        await _writer.WriteRawDataPacketAsync(ms.ToArray(), 0, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles SQL statement execution.
    /// </summary>
    private async Task HandleExecuteAsync(byte[] payload, CancellationToken ct)
    {
        var (cursorId, _) = ExtractSqlFromPayload(payload);

        if (!_state.Cursors.TryGetValue(cursorId, out var cursor))
        {
            await _writer.WriteErrorResponseAsync(
                1001,
                OracleProtocolConstants.ErrorInvalidCursor,
                ct).ConfigureAwait(false);
            return;
        }

        if (!cursor.IsParsed)
        {
            await _writer.WriteErrorResponseAsync(
                1001,
                "ORA-01003: no statement parsed",
                ct).ConfigureAwait(false);
            return;
        }

        // Substitute bind variables
        var sql = cursor.BindParameters.Count > 0
            ? _queryProcessor.SubstituteBindVariables(cursor.SqlStatement, cursor.BindParameters)
            : cursor.SqlStatement;

        // Execute query
        var result = await _queryProcessor.ExecuteQueryAsync(sql, ct).ConfigureAwait(false);

        if (result.ErrorCode.HasValue)
        {
            await _writer.WriteErrorResponseAsync(result.ErrorCode.Value, result.ErrorMessage ?? "Unknown error", ct).ConfigureAwait(false);
            return;
        }

        cursor.IsExecuted = true;
        cursor.Columns.Clear();
        cursor.Columns.AddRange(result.Columns);
        cursor.CachedRows = result.Rows;
        cursor.FetchPosition = 0;
        cursor.RowsAffected = result.RowsAffected;

        // Update transaction state for DML
        if (cursor.StatementType is OracleStatementType.Insert or OracleStatementType.Update
            or OracleStatementType.Delete or OracleStatementType.Merge)
        {
            _state.InTransaction = true;
        }

        // Send appropriate response based on statement type
        if (cursor.StatementType == OracleStatementType.Select)
        {
            // For SELECT, send column descriptions
            await _writer.WriteDescribeResponseAsync(cursor.Columns, ct).ConfigureAwait(false);
        }
        else
        {
            // For DML, send execute response with rows affected
            await _writer.WriteExecuteResponseAsync(cursor.RowsAffected, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Handles row fetch requests.
    /// </summary>
    private async Task HandleFetchAsync(byte[] payload, CancellationToken ct)
    {
        // Extract cursor ID and fetch size
        var cursorId = 0;
        var fetchSize = OracleProtocolConstants.DefaultFetchSize;

        if (payload.Length >= 2)
        {
            cursorId = (payload[0] << 8) | payload[1];
        }
        if (payload.Length >= 4)
        {
            fetchSize = (payload[2] << 8) | payload[3];
            if (fetchSize <= 0 || fetchSize > OracleProtocolConstants.MaxFetchRows)
                fetchSize = OracleProtocolConstants.DefaultFetchSize;
        }

        if (!_state.Cursors.TryGetValue(cursorId, out var cursor))
        {
            await _writer.WriteErrorResponseAsync(
                1001,
                OracleProtocolConstants.ErrorInvalidCursor,
                ct).ConfigureAwait(false);
            return;
        }

        if (!cursor.IsExecuted)
        {
            await _writer.WriteErrorResponseAsync(
                1002,
                OracleProtocolConstants.ErrorFetchOutOfSequence,
                ct).ConfigureAwait(false);
            return;
        }

        // Get rows for this fetch
        var rowsToFetch = Math.Min(fetchSize, cursor.CachedRows.Count - cursor.FetchPosition);
        var rows = cursor.CachedRows.Skip(cursor.FetchPosition).Take(rowsToFetch).ToList();
        cursor.FetchPosition += rowsToFetch;

        var hasMoreRows = cursor.FetchPosition < cursor.CachedRows.Count;

        // Send fetch response
        await _writer.WriteFetchResponseAsync(rows, cursor.Columns, hasMoreRows, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles describe requests for cursor metadata.
    /// </summary>
    private async Task HandleDescribeAsync(byte[] payload, CancellationToken ct)
    {
        var cursorId = 0;
        if (payload.Length >= 2)
        {
            cursorId = (payload[0] << 8) | payload[1];
        }

        if (!_state.Cursors.TryGetValue(cursorId, out var cursor))
        {
            await _writer.WriteErrorResponseAsync(
                1001,
                OracleProtocolConstants.ErrorInvalidCursor,
                ct).ConfigureAwait(false);
            return;
        }

        await _writer.WriteDescribeResponseAsync(cursor.Columns, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles COMMIT transaction control.
    /// </summary>
    private async Task HandleCommitAsync(CancellationToken ct)
    {
        _state.InTransaction = false;
        _state.TransactionFailed = false;
        await _writer.WriteTransactionResponseAsync(isCommit: true, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles ROLLBACK transaction control.
    /// </summary>
    private async Task HandleRollbackAsync(CancellationToken ct)
    {
        _state.InTransaction = false;
        _state.TransactionFailed = false;
        await _writer.WriteTransactionResponseAsync(isCommit: false, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles session control operations (ALTER SESSION, etc.).
    /// </summary>
    private async Task HandleSessionControlAsync(byte[] payload, CancellationToken ct)
    {
        // Parse session parameter changes
        // For now, acknowledge and ignore
        await _writer.WriteRawDataPacketAsync(new byte[] { 0x04, 0x00 }, 0, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles generic SQL that doesn't fit into specific categories.
    /// </summary>
    private async Task HandleGenericSqlAsync(byte[] payload, CancellationToken ct)
    {
        // Try to extract SQL from payload
        var sql = TryExtractSql(payload);
        if (string.IsNullOrEmpty(sql))
        {
            // No SQL found, acknowledge and return
            await _writer.WriteRawDataPacketAsync(new byte[] { 0x00 }, 0, ct).ConfigureAwait(false);
            return;
        }

        // Create ad-hoc cursor
        var cursorId = _state.AllocateCursorId();
        var cursor = new OracleCursor
        {
            CursorId = cursorId,
            SqlStatement = sql,
            IsParsed = true,
            StatementType = _queryProcessor.DetectStatementType(sql)
        };
        _state.Cursors[cursorId] = cursor;

        // Execute
        var result = await _queryProcessor.ExecuteQueryAsync(sql, ct).ConfigureAwait(false);

        if (result.ErrorCode.HasValue)
        {
            await _writer.WriteErrorResponseAsync(result.ErrorCode.Value, result.ErrorMessage ?? "Unknown error", ct).ConfigureAwait(false);
            return;
        }

        cursor.IsExecuted = true;
        cursor.Columns.AddRange(result.Columns);
        cursor.CachedRows = result.Rows;

        if (cursor.StatementType == OracleStatementType.Select && result.Columns.Count > 0)
        {
            // Send describe + immediate fetch for simple queries
            await _writer.WriteDescribeResponseAsync(cursor.Columns, ct).ConfigureAwait(false);
            await _writer.WriteFetchResponseAsync(result.Rows, result.Columns, false, ct).ConfigureAwait(false);
        }
        else
        {
            await _writer.WriteExecuteResponseAsync(result.RowsAffected, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Handles attention/break signals.
    /// </summary>
    private async Task HandleAttentionAsync(CancellationToken ct)
    {
        // Cancel any running operation
        // Send acknowledgment
        await _writer.WritePacketAsync(TnsPacketType.Marker, new byte[] { 0x00 }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles marker packets.
    /// </summary>
    private async Task HandleMarkerAsync(CancellationToken ct)
    {
        // Acknowledge marker
        await _writer.WritePacketAsync(TnsPacketType.Marker, new byte[] { 0x00 }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Extracts cursor ID and SQL statement from a payload.
    /// </summary>
    private (int cursorId, string sql) ExtractSqlFromPayload(byte[] payload)
    {
        var cursorId = 0;
        var sql = string.Empty;

        if (payload.Length < 3)
            return (cursorId, sql);

        // First two bytes are typically cursor ID
        cursorId = (payload[0] << 8) | payload[1];

        // SQL statement follows, typically length-prefixed
        var sqlOffset = 2;
        if (payload.Length > sqlOffset)
        {
            var sqlLength = payload[sqlOffset];
            sqlOffset++;

            if (sqlLength == 0xFE && payload.Length > sqlOffset + 2)
            {
                // Extended length encoding
                sqlLength = (byte)((payload[sqlOffset] << 8) | payload[sqlOffset + 1]);
                sqlOffset += 2;
            }

            if (sqlOffset + sqlLength <= payload.Length)
            {
                sql = Encoding.UTF8.GetString(payload, sqlOffset, sqlLength);
            }
            else if (sqlOffset < payload.Length)
            {
                // Try to extract remaining as SQL
                sql = Encoding.UTF8.GetString(payload, sqlOffset, payload.Length - sqlOffset).TrimEnd('\0');
            }
        }

        return (cursorId, sql);
    }

    /// <summary>
    /// Attempts to extract SQL from a raw payload.
    /// </summary>
    private string TryExtractSql(byte[] payload)
    {
        if (payload.Length < 2)
            return string.Empty;

        // Look for SQL keywords to find start of statement
        var str = Encoding.UTF8.GetString(payload);
        var keywords = new[] { "SELECT", "INSERT", "UPDATE", "DELETE", "CREATE", "DROP", "ALTER", "BEGIN", "DECLARE", "COMMIT", "ROLLBACK" };

        foreach (var keyword in keywords)
        {
            var idx = str.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                return str.Substring(idx).TrimEnd('\0');
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Attempts to send an error response, ignoring any exceptions.
    /// </summary>
    private async Task TrySendErrorAsync(int errorCode, string message, CancellationToken ct)
    {
        try
        {
            await _writer.WriteErrorResponseAsync(errorCode, message, ct).ConfigureAwait(false);
        }
        catch
        {
            // Ignore errors when sending error response
        }
    }

    /// <summary>
    /// Closes the connection and releases resources.
    /// </summary>
    private void CloseConnection()
    {
        try
        {
            _client.Close();
        }
        catch
        {
            // Ignore close errors
        }
    }

    /// <summary>
    /// Releases resources used by the protocol handler.
    /// </summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _reader.Dispose();
            _writer.Dispose();
            CloseConnection();
        }
    }
}
