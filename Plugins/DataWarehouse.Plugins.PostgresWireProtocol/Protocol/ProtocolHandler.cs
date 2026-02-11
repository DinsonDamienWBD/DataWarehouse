using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace DataWarehouse.Plugins.PostgresWireProtocol.Protocol;

/// <summary>
/// Handles PostgreSQL wire protocol communication for a single connection.
/// </summary>
public sealed class ProtocolHandler
{
    private readonly TcpClient _client;
    private readonly PostgresWireProtocolConfig _config;
    private readonly QueryProcessor _queryProcessor;
    private readonly MessageReader _reader;
    private readonly MessageWriter _writer;
    private readonly PgConnectionState _state;
    private CancellationTokenSource? _queryTokenSource;

    public ProtocolHandler(
        TcpClient client,
        PostgresWireProtocolConfig config,
        QueryProcessor queryProcessor)
    {
        _client = client;
        _config = config;
        _queryProcessor = queryProcessor;

        var stream = client.GetStream();
        _reader = new MessageReader(stream);
        _writer = new MessageWriter(stream);

        _state = new PgConnectionState
        {
            ConnectionId = Guid.NewGuid().ToString("N"),
            ProcessId = Random.Shared.Next(1000, 99999),
            SecretKey = Random.Shared.Next(1000, 99999)
        };
    }

    /// <summary>
    /// Handles the entire connection lifecycle.
    /// </summary>
    public async Task HandleConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            // Handle startup
            await HandleStartupAsync(ct);

            // Main message loop
            while (!ct.IsCancellationRequested && _client.Connected)
            {
                var message = await _reader.ReadFrontendMessageAsync(ct);
                await ProcessMessageAsync(message, ct);
            }
        }
        catch (EndOfStreamException)
        {
            // Client disconnected
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[PostgresWireProtocol] Connection error: {ex.Message}");
            try
            {
                await _writer.WriteErrorResponseAsync(
                    PgProtocolConstants.SeverityFatal,
                    PgProtocolConstants.SqlStateConnectionException,
                    ex.Message,
                    ct: ct);
            }
            catch
            {
                // Best effort
            }
        }
        finally
        {
            try { _client.Close(); } catch { }
        }
    }

    /// <summary>
    /// Handles the startup handshake.
    /// </summary>
    private async Task HandleStartupAsync(CancellationToken ct)
    {
        var startup = await _reader.ReadStartupMessageAsync(ct);

        // Handle SSL request
        if (startup.IsSslRequest)
        {
            // Decline SSL (send 'N')
            Console.WriteLine("[PostgresWireProtocol] WARNING: SSL/TLS is not implemented. Connection is NOT encrypted.");
            Console.WriteLine("[PostgresWireProtocol] This is a security risk. Do not use in production without implementing SSL/TLS.");
            await _writer.WriteByteAsync((byte)'N', ct);

            // Read actual startup message
            startup = await _reader.ReadStartupMessageAsync(ct);
        }

        // Handle cancel request
        if (startup.IsCancelRequest)
        {
            // Extract process ID and secret key from the cancel request
            // Cancel request message format: int32 processId, int32 secretKey
            if (startup.Parameters.TryGetValue("processId", out var pidStr) &&
                startup.Parameters.TryGetValue("secretKey", out var keyStr) &&
                int.TryParse(pidStr, out var processId) &&
                int.TryParse(keyStr, out var secretKey))
            {
                // Verify the process ID and secret key match this connection
                if (processId == _state.ProcessId && secretKey == _state.SecretKey)
                {
                    // Cancel the active query if one is running
                    _queryTokenSource?.Cancel();
                }
            }
            // No response is sent for cancel requests per PostgreSQL protocol
            return;
        }

        // Parse connection parameters
        _state.Username = startup.Parameters.GetValueOrDefault("user", "postgres");
        _state.Database = startup.Parameters.GetValueOrDefault("database", "datawarehouse");
        _state.ApplicationName = startup.Parameters.GetValueOrDefault("application_name", "");

        foreach (var param in startup.Parameters)
        {
            _state.Parameters[param.Key] = param.Value;
        }

        // Authenticate
        await AuthenticateAsync(ct);

        // Send parameter status
        await _writer.WriteParameterStatusAsync("server_version", "14.0", ct);
        await _writer.WriteParameterStatusAsync("server_encoding", "UTF8", ct);
        await _writer.WriteParameterStatusAsync("client_encoding", "UTF8", ct);
        await _writer.WriteParameterStatusAsync("DateStyle", "ISO, MDY", ct);
        await _writer.WriteParameterStatusAsync("integer_datetimes", "on", ct);
        await _writer.WriteParameterStatusAsync("TimeZone", "UTC", ct);

        // Send backend key data
        await _writer.WriteBackendKeyDataAsync(_state.ProcessId, _state.SecretKey, ct);

        // Ready for query
        await _writer.WriteReadyForQueryAsync(PgProtocolConstants.TransactionIdle, ct);
    }

    /// <summary>
    /// Performs authentication.
    /// </summary>
    private async Task AuthenticateAsync(CancellationToken ct)
    {
        if (_config.AuthMethod.Equals("trust", StringComparison.OrdinalIgnoreCase))
        {
            // Trust authentication - no password required
            await _writer.WriteAuthenticationAsync(PgProtocolConstants.AuthOk, ct);
            return;
        }

        if (_config.AuthMethod.Equals("md5", StringComparison.OrdinalIgnoreCase))
        {
            // MD5 authentication
            var salt = new byte[4];
            RandomNumberGenerator.Fill(salt);

            await _writer.WriteAuthenticationMD5Async(salt, ct);

            // Read password response
            var passwordMsg = await _reader.ReadFrontendMessageAsync(ct);
            if (passwordMsg.Type != PgMessageType.PasswordMessage)
            {
                throw new InvalidOperationException("Expected password message");
            }

            // Validate password hash
            var receivedHash = Encoding.UTF8.GetString(passwordMsg.Body).TrimEnd('\0');
            if (!ValidatePasswordHash(receivedHash, _state.Username, salt))
            {
                await _writer.WriteErrorResponseAsync(
                    PgProtocolConstants.SeverityFatal,
                    PgProtocolConstants.SqlStateInvalidPassword,
                    "password authentication failed for user \"" + _state.Username + "\"",
                    ct: ct);
                throw new UnauthorizedAccessException("Invalid credentials");
            }

            await _writer.WriteAuthenticationAsync(PgProtocolConstants.AuthOk, ct);
            return;
        }

        // Default: accept
        await _writer.WriteAuthenticationAsync(PgProtocolConstants.AuthOk, ct);
    }

    /// <summary>
    /// Processes a frontend message.
    /// </summary>
    private async Task ProcessMessageAsync(FrontendMessage message, CancellationToken ct)
    {
        try
        {
            switch (message.Type)
            {
                case PgMessageType.Query:
                    await HandleQueryAsync(message.Body, ct);
                    break;

                case PgMessageType.Parse:
                    await HandleParseAsync(message.Body, ct);
                    break;

                case PgMessageType.Bind:
                    await HandleBindAsync(message.Body, ct);
                    break;

                case PgMessageType.Execute:
                    await HandleExecuteAsync(message.Body, ct);
                    break;

                case PgMessageType.Describe:
                    await HandleDescribeAsync(message.Body, ct);
                    break;

                case PgMessageType.Close:
                    await HandleCloseAsync(message.Body, ct);
                    break;

                case PgMessageType.Sync:
                    await HandleSyncAsync(ct);
                    break;

                case PgMessageType.Flush:
                    // No-op for now
                    break;

                case PgMessageType.Terminate:
                    _client.Close();
                    break;

                default:
                    await _writer.WriteErrorResponseAsync(
                        PgProtocolConstants.SeverityError,
                        PgProtocolConstants.SqlStateInternalError,
                        $"Unsupported message type: {message.Type}",
                        ct: ct);
                    break;
            }
        }
        catch (Exception ex)
        {
            await _writer.WriteErrorResponseAsync(
                PgProtocolConstants.SeverityError,
                PgProtocolConstants.SqlStateInternalError,
                ex.Message,
                detail: ex.StackTrace,
                ct: ct);

            await _writer.WriteReadyForQueryAsync(GetTransactionStatus(), ct);
        }
    }

    /// <summary>
    /// Handles simple query protocol (Query message).
    /// </summary>
    private async Task HandleQueryAsync(byte[] body, CancellationToken ct)
    {
        var sql = Encoding.UTF8.GetString(body).TrimEnd('\0');

        // Handle transaction commands
        if (sql.Trim().Equals("BEGIN", StringComparison.OrdinalIgnoreCase) ||
            sql.Trim().StartsWith("BEGIN", StringComparison.OrdinalIgnoreCase))
        {
            _state.InTransaction = true;
            await _writer.WriteCommandCompleteAsync("BEGIN", ct);
            await _writer.WriteReadyForQueryAsync(GetTransactionStatus(), ct);
            return;
        }

        if (sql.Trim().Equals("COMMIT", StringComparison.OrdinalIgnoreCase))
        {
            _state.InTransaction = false;
            _state.TransactionFailed = false;
            await _writer.WriteCommandCompleteAsync("COMMIT", ct);
            await _writer.WriteReadyForQueryAsync(GetTransactionStatus(), ct);
            return;
        }

        if (sql.Trim().Equals("ROLLBACK", StringComparison.OrdinalIgnoreCase))
        {
            _state.InTransaction = false;
            _state.TransactionFailed = false;
            await _writer.WriteCommandCompleteAsync("ROLLBACK", ct);
            await _writer.WriteReadyForQueryAsync(GetTransactionStatus(), ct);
            return;
        }

        // Create a cancellation token source for this query
        _queryTokenSource?.Dispose();
        _queryTokenSource = CancellationTokenSource.CreateLinkedTokenSource(ct);

        try
        {
            // Execute query with cancellation support
            var result = await _queryProcessor.ExecuteQueryAsync(sql, _queryTokenSource.Token);

            if (result.IsEmpty)
            {
                await _writer.WriteEmptyQueryResponseAsync(ct);
            }
            else if (!string.IsNullOrEmpty(result.ErrorMessage))
            {
                await _writer.WriteErrorResponseAsync(
                    PgProtocolConstants.SeverityError,
                    result.ErrorSqlState ?? PgProtocolConstants.SqlStateInternalError,
                    result.ErrorMessage,
                    ct: ct);
            }
            else if (result.Columns.Count > 0)
            {
                // SELECT query - send rows
                await _writer.WriteRowDescriptionAsync(result.Columns, ct);

                foreach (var row in result.Rows)
                {
                    await _writer.WriteDataRowAsync(row, ct);
                }

                await _writer.WriteCommandCompleteAsync(result.CommandTag, ct);
            }
            else
            {
                // Non-SELECT query
                await _writer.WriteCommandCompleteAsync(result.CommandTag, ct);
            }

            await _writer.WriteReadyForQueryAsync(GetTransactionStatus(), ct);
        }
        catch (OperationCanceledException)
        {
            // Query was cancelled
            await _writer.WriteErrorResponseAsync(
                PgProtocolConstants.SeverityError,
                PgProtocolConstants.SqlStateQueryCanceled,
                "Query execution was cancelled",
                ct: ct);
            await _writer.WriteReadyForQueryAsync(GetTransactionStatus(), ct);
        }
        finally
        {
            _queryTokenSource?.Dispose();
            _queryTokenSource = null;
        }
    }

    /// <summary>
    /// Handles Parse message (prepared statement creation).
    /// </summary>
    private async Task HandleParseAsync(byte[] body, CancellationToken ct)
    {
        var offset = 0;

        // Read statement name
        var stmtName = ReadNullTerminatedString(body, ref offset);

        // Read query string
        var query = ReadNullTerminatedString(body, ref offset);

        // Read parameter count
        var paramCount = ReadInt16(body, ref offset);

        // Read parameter type OIDs
        var paramTypes = new List<int>();
        for (var i = 0; i < paramCount; i++)
        {
            paramTypes.Add(ReadInt32(body, ref offset));
        }

        // If no types specified, infer from query
        if (paramTypes.Count == 0 || paramTypes.All(t => t == 0))
        {
            paramTypes = _queryProcessor.ExtractParameterTypes(query);
        }

        // Store prepared statement
        var stmt = new PgPreparedStatement
        {
            Name = stmtName,
            Query = query,
            ParameterTypeOids = paramTypes
        };

        _state.PreparedStatements[stmtName] = stmt;

        await _writer.WriteParseCompleteAsync(ct);
    }

    /// <summary>
    /// Handles Bind message (bind parameters to prepared statement).
    /// </summary>
    private async Task HandleBindAsync(byte[] body, CancellationToken ct)
    {
        var offset = 0;

        // Read portal name
        var portalName = ReadNullTerminatedString(body, ref offset);

        // Read statement name
        var stmtName = ReadNullTerminatedString(body, ref offset);

        // Read parameter format codes count
        var formatCodeCount = ReadInt16(body, ref offset);
        var formatCodes = new List<short>();
        for (var i = 0; i < formatCodeCount; i++)
        {
            formatCodes.Add(ReadInt16(body, ref offset));
        }

        // Read parameter values count
        var paramCount = ReadInt16(body, ref offset);
        var parameters = new List<byte[]?>();

        for (var i = 0; i < paramCount; i++)
        {
            var length = ReadInt32(body, ref offset);
            if (length == -1)
            {
                parameters.Add(null);
            }
            else
            {
                var value = new byte[length];
                Array.Copy(body, offset, value, 0, length);
                offset += length;
                parameters.Add(value);
            }
        }

        // Read result format codes count
        var resultFormatCount = ReadInt16(body, ref offset);
        var resultFormats = new List<short>();
        for (var i = 0; i < resultFormatCount; i++)
        {
            resultFormats.Add(ReadInt16(body, ref offset));
        }

        // Store portal
        var portal = new PgPortal
        {
            Name = portalName,
            StatementName = stmtName,
            Parameters = parameters,
            ParameterFormats = formatCodes,
            ResultFormats = resultFormats
        };

        _state.Portals[portalName] = portal;

        await _writer.WriteBindCompleteAsync(ct);
    }

    /// <summary>
    /// Handles Execute message (execute bound portal).
    /// </summary>
    private async Task HandleExecuteAsync(byte[] body, CancellationToken ct)
    {
        var offset = 0;

        // Read portal name
        var portalName = ReadNullTerminatedString(body, ref offset);

        // Read max rows
        var maxRows = ReadInt32(body, ref offset);

        // Get portal
        if (!_state.Portals.TryGetValue(portalName, out var portal))
        {
            await _writer.WriteErrorResponseAsync(
                PgProtocolConstants.SeverityError,
                PgProtocolConstants.SqlStateInternalError,
                $"Portal not found: {portalName}",
                ct: ct);
            return;
        }

        // Get prepared statement
        if (!_state.PreparedStatements.TryGetValue(portal.StatementName, out var stmt))
        {
            await _writer.WriteErrorResponseAsync(
                PgProtocolConstants.SeverityError,
                PgProtocolConstants.SqlStateInternalError,
                $"Prepared statement not found: {portal.StatementName}",
                ct: ct);
            return;
        }

        // Substitute parameters into query
        var sql = _queryProcessor.SubstituteParameters(stmt.Query, portal.Parameters);

        // Create a cancellation token source for this query
        _queryTokenSource?.Dispose();
        _queryTokenSource = CancellationTokenSource.CreateLinkedTokenSource(ct);

        try
        {
            // Execute query with cancellation support
            var result = await _queryProcessor.ExecuteQueryAsync(sql, _queryTokenSource.Token);

            if (!string.IsNullOrEmpty(result.ErrorMessage))
            {
                await _writer.WriteErrorResponseAsync(
                    PgProtocolConstants.SeverityError,
                    result.ErrorSqlState ?? PgProtocolConstants.SqlStateInternalError,
                    result.ErrorMessage,
                    ct: ct);
            }
            else if (result.Columns.Count > 0)
            {
                // SELECT query
                var rowsToSend = maxRows > 0 ? result.Rows.Take(maxRows).ToList() : result.Rows;

                foreach (var row in rowsToSend)
                {
                    await _writer.WriteDataRowAsync(row, ct);
                }

                await _writer.WriteCommandCompleteAsync(result.CommandTag, ct);
            }
            else
            {
                // Non-SELECT query
                await _writer.WriteCommandCompleteAsync(result.CommandTag, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Query was cancelled
            await _writer.WriteErrorResponseAsync(
                PgProtocolConstants.SeverityError,
                PgProtocolConstants.SqlStateQueryCanceled,
                "Query execution was cancelled",
                ct: ct);
        }
        finally
        {
            _queryTokenSource?.Dispose();
            _queryTokenSource = null;
        }
    }

    /// <summary>
    /// Handles Describe message (describe prepared statement or portal).
    /// </summary>
    private async Task HandleDescribeAsync(byte[] body, CancellationToken ct)
    {
        var describeType = (char)body[0];
        var name = Encoding.UTF8.GetString(body.AsSpan(1).ToArray()).TrimEnd('\0');

        if (describeType == 'S')
        {
            // Describe statement
            if (_state.PreparedStatements.TryGetValue(name, out var stmt))
            {
                // For now, send NoData (full implementation would analyze query)
                await _writer.WriteNoDataAsync(ct);
            }
            else
            {
                await _writer.WriteErrorResponseAsync(
                    PgProtocolConstants.SeverityError,
                    PgProtocolConstants.SqlStateInternalError,
                    $"Prepared statement not found: {name}",
                    ct: ct);
            }
        }
        else if (describeType == 'P')
        {
            // Describe portal
            if (_state.Portals.TryGetValue(name, out var portal))
            {
                // For now, send NoData
                await _writer.WriteNoDataAsync(ct);
            }
            else
            {
                await _writer.WriteErrorResponseAsync(
                    PgProtocolConstants.SeverityError,
                    PgProtocolConstants.SqlStateInternalError,
                    $"Portal not found: {name}",
                    ct: ct);
            }
        }
    }

    /// <summary>
    /// Handles Close message (close prepared statement or portal).
    /// </summary>
    private async Task HandleCloseAsync(byte[] body, CancellationToken ct)
    {
        var closeType = (char)body[0];
        var name = Encoding.UTF8.GetString(body.AsSpan(1).ToArray()).TrimEnd('\0');

        if (closeType == 'S')
        {
            _state.PreparedStatements.Remove(name);
        }
        else if (closeType == 'P')
        {
            _state.Portals.Remove(name);
        }

        await _writer.WriteCloseCompleteAsync(ct);
    }

    /// <summary>
    /// Handles Sync message.
    /// </summary>
    private async Task HandleSyncAsync(CancellationToken ct)
    {
        await _writer.WriteReadyForQueryAsync(GetTransactionStatus(), ct);
    }

    /// <summary>
    /// Gets current transaction status byte.
    /// </summary>
    private byte GetTransactionStatus()
    {
        if (_state.TransactionFailed)
            return PgProtocolConstants.TransactionFailed;
        if (_state.InTransaction)
            return PgProtocolConstants.TransactionInBlock;
        return PgProtocolConstants.TransactionIdle;
    }

    // Helper methods for parsing message body
    private string ReadNullTerminatedString(byte[] data, ref int offset)
    {
        var start = offset;
        while (offset < data.Length && data[offset] != 0)
            offset++;

        var str = Encoding.UTF8.GetString(data, start, offset - start);
        offset++; // Skip null terminator
        return str;
    }

    private short ReadInt16(byte[] data, ref int offset)
    {
        var value = (short)((data[offset] << 8) | data[offset + 1]);
        offset += 2;
        return value;
    }

    private int ReadInt32(byte[] data, ref int offset)
    {
        var value = (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
        offset += 4;
        return value;
    }

    /// <summary>
    /// Validates a PostgreSQL MD5 password hash.
    /// </summary>
    /// <param name="receivedHash">The hash received from the client (format: "md5" + hex(md5(md5(password + username) + salt))).</param>
    /// <param name="username">The username.</param>
    /// <param name="salt">The 4-byte salt sent to the client.</param>
    /// <returns>True if the hash is valid.</returns>
    private bool ValidatePasswordHash(string receivedHash, string username, byte[] salt)
    {
        // PostgreSQL MD5 authentication format:
        // 1. Client computes: md5(password + username) -> gets a hex string
        // 2. Client then computes: md5(hex_string_from_step1 + salt) -> gets another hex string
        // 3. Client sends: "md5" + hex_string_from_step2

        if (!receivedHash.StartsWith("md5", StringComparison.OrdinalIgnoreCase))
            return false;

        var hashPart = receivedHash.Substring(3);

        // Retrieve the stored password hash for the user
        var storedPasswordHash = GetStoredPasswordHash(username);
        if (storedPasswordHash == null)
            return false;

        // Compute expected hash: md5(stored_hash + salt)
        using var md5 = MD5.Create();
        var saltedInput = Encoding.UTF8.GetBytes(storedPasswordHash).Concat(salt).ToArray();
        var computedHash = md5.ComputeHash(saltedInput);
        var expectedHash = BitConverter.ToString(computedHash).Replace("-", "").ToLowerInvariant();

        return string.Equals(hashPart, expectedHash, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Retrieves the stored password hash for a user.
    /// In production, this should query a secure user database with properly hashed passwords.
    /// </summary>
    /// <param name="username">The username.</param>
    /// <returns>The stored password hash (md5(password + username) as hex string), or null if user not found.</returns>
    private string? GetStoredPasswordHash(string username)
    {
        // User database lookup: requires integration with an auth provider (PBKDF2/bcrypt/Argon2).
        // Currently rejects all authentication attempts until a user database is wired in.
        // Production requirements:
        // 1. Query a secure user database
        // 2. Use proper password hashing (PBKDF2, bcrypt, Argon2)
        // 3. Store salts and hashed passwords separately
        // 4. Never store plaintext passwords
        Console.Error.WriteLine($"[PostgresWireProtocol] WARNING: User authentication database not implemented. Rejecting login for user '{username}'.");
        return null;
    }
}
