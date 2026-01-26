using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace DataWarehouse.Plugins.MySqlProtocol.Protocol;

/// <summary>
/// Handles MySQL wire protocol communication for a single connection.
/// Manages the complete connection lifecycle including handshake, authentication,
/// query execution, and prepared statement handling.
/// Thread-safe for single connection use.
/// </summary>
public sealed class ProtocolHandler : IDisposable
{
    private readonly TcpClient _client;
    private readonly MySqlProtocolConfig _config;
    private readonly QueryProcessor _queryProcessor;
    private readonly MySqlConnectionState _state;

    private Stream _stream;
    private MessageReader _reader;
    private MessageWriter _writer;
    private byte[] _authData;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProtocolHandler"/> class.
    /// </summary>
    /// <param name="client">The TCP client for this connection.</param>
    /// <param name="config">Protocol configuration.</param>
    /// <param name="queryProcessor">Query processor for executing SQL.</param>
    /// <param name="connectionId">Unique connection thread ID.</param>
    /// <exception cref="ArgumentNullException">Thrown when any parameter is null.</exception>
    public ProtocolHandler(
        TcpClient client,
        MySqlProtocolConfig config,
        QueryProcessor queryProcessor,
        uint connectionId)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _queryProcessor = queryProcessor ?? throw new ArgumentNullException(nameof(queryProcessor));

        _stream = client.GetStream();
        _reader = new MessageReader(_stream);
        _writer = new MessageWriter(_stream);
        _authData = MessageWriter.GenerateAuthData();

        _state = new MySqlConnectionState
        {
            ConnectionId = Guid.NewGuid().ToString("N"),
            ThreadId = connectionId,
            ClientAddress = client.Client.RemoteEndPoint?.ToString()
        };
    }

    /// <summary>
    /// Gets the connection state.
    /// </summary>
    public MySqlConnectionState State => _state;

    /// <summary>
    /// Handles the entire connection lifecycle.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task HandleConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            // Send initial handshake
            await _writer.WriteHandshakeAsync(_config, _state.ThreadId, _authData, ct).ConfigureAwait(false);

            // Handle handshake response
            var handshakePacket = await _reader.ReadPacketAsync(ct).ConfigureAwait(false);
            _state.SequenceId = (byte)(handshakePacket.SequenceId + 1);

            var handshakeResponse = _reader.ParseHandshakeResponse(handshakePacket, MySqlCapabilities.SERVER_DEFAULT);
            _state.ClientCapabilities = handshakeResponse.Capabilities;
            _state.CharacterSet = handshakeResponse.CharacterSet;

            // Handle SSL upgrade if requested
            if (handshakeResponse.IsSslRequest)
            {
                await HandleSslUpgradeAsync(ct).ConfigureAwait(false);

                // Read actual handshake response after SSL
                handshakePacket = await _reader.ReadPacketAsync(ct).ConfigureAwait(false);
                _state.SequenceId = (byte)(handshakePacket.SequenceId + 1);
                handshakeResponse = _reader.ParseHandshakeResponse(handshakePacket, MySqlCapabilities.SERVER_DEFAULT);
                _state.ClientCapabilities = handshakeResponse.Capabilities;
            }

            // Store connection info
            _state.Username = handshakeResponse.Username;
            if (!string.IsNullOrEmpty(handshakeResponse.Database))
            {
                _state.Database = handshakeResponse.Database;
            }

            foreach (var attr in handshakeResponse.ConnectionAttributes)
            {
                _state.ConnectionAttributes[attr.Key] = attr.Value;
            }

            // Authenticate
            var authSuccess = await AuthenticateAsync(handshakeResponse, ct).ConfigureAwait(false);
            if (!authSuccess)
            {
                return;
            }

            // Main command loop
            while (!ct.IsCancellationRequested && _client.Connected)
            {
                try
                {
                    var packet = await _reader.ReadPacketAsync(ct).ConfigureAwait(false);
                    _state.SequenceId = 0;

                    await ProcessCommandAsync(packet, ct).ConfigureAwait(false);
                }
                catch (EndOfStreamException)
                {
                    // Client disconnected gracefully
                    break;
                }
            }
        }
        catch (EndOfStreamException)
        {
            // Client disconnected
        }
        catch (IOException)
        {
            // Connection reset or timeout
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            try
            {
                await _writer.WriteErrorPacketAsync(
                    _state.SequenceId,
                    MySqlErrorCode.ER_INTERNAL_ERROR,
                    MySqlSqlState.ServerError,
                    $"Internal error: {ex.Message}",
                    ct).ConfigureAwait(false);
            }
            catch
            {
                // Best effort error response
            }
        }
    }

    private async Task HandleSslUpgradeAsync(CancellationToken ct)
    {
        if (_config.SslMode == "disabled")
        {
            throw new InvalidOperationException("SSL requested but disabled in configuration");
        }

        X509Certificate2? certificate = null;

        if (!string.IsNullOrEmpty(_config.SslCertificatePath) && !string.IsNullOrEmpty(_config.SslKeyPath))
        {
            certificate = X509Certificate2.CreateFromPemFile(_config.SslCertificatePath, _config.SslKeyPath);
        }
        else
        {
            // Generate a self-signed certificate for development
            certificate = GenerateSelfSignedCertificate();
        }

        var sslStream = new SslStream(_stream, false);
        await sslStream.AuthenticateAsServerAsync(
            certificate,
            clientCertificateRequired: false,
            enabledSslProtocols: SslProtocols.Tls12 | SslProtocols.Tls13,
            checkCertificateRevocation: false).ConfigureAwait(false);

        _stream = sslStream;
        _reader = new MessageReader(_stream);
        _writer = new MessageWriter(_stream);
        _state.SslEnabled = true;
    }

    private static X509Certificate2 GenerateSelfSignedCertificate()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=DataWarehouse MySQL",
            rsa,
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);

        var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddYears(1));

        return new X509Certificate2(
            certificate.Export(X509ContentType.Pfx),
            (string?)null,
            X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.Exportable);
    }

    private async Task<bool> AuthenticateAsync(HandshakeResponse response, CancellationToken ct)
    {
        // Trust authentication
        if (_config.AuthMethod.Equals("trust", StringComparison.OrdinalIgnoreCase))
        {
            await _writer.WriteOkPacketAsync(_state.SequenceId, serverStatus: _state.ServerStatus, ct: ct).ConfigureAwait(false);
            return true;
        }

        var authPluginName = response.AuthPluginName ?? MySqlProtocolConstants.AuthPluginMySqlNativePassword;

        // Handle auth plugin mismatch
        if (_config.AuthMethod.Equals("caching_sha2_password", StringComparison.OrdinalIgnoreCase) &&
            authPluginName != MySqlProtocolConstants.AuthPluginCachingSha2Password)
        {
            // Send auth switch request
            _authData = MessageWriter.GenerateAuthData();
            await _writer.WriteAuthSwitchRequestAsync(
                _state.SequenceId++,
                MySqlProtocolConstants.AuthPluginCachingSha2Password,
                _authData,
                ct).ConfigureAwait(false);

            // Read auth response
            var authPacket = await _reader.ReadPacketAsync(ct).ConfigureAwait(false);
            _state.SequenceId = (byte)(authPacket.SequenceId + 1);
            response.AuthResponse = authPacket.Payload;
            authPluginName = MySqlProtocolConstants.AuthPluginCachingSha2Password;
        }
        else if (_config.AuthMethod.Equals("mysql_native_password", StringComparison.OrdinalIgnoreCase) &&
                 authPluginName != MySqlProtocolConstants.AuthPluginMySqlNativePassword)
        {
            // Send auth switch request
            _authData = MessageWriter.GenerateAuthData();
            await _writer.WriteAuthSwitchRequestAsync(
                _state.SequenceId++,
                MySqlProtocolConstants.AuthPluginMySqlNativePassword,
                _authData,
                ct).ConfigureAwait(false);

            // Read auth response
            var authPacket = await _reader.ReadPacketAsync(ct).ConfigureAwait(false);
            _state.SequenceId = (byte)(authPacket.SequenceId + 1);
            response.AuthResponse = authPacket.Payload;
            authPluginName = MySqlProtocolConstants.AuthPluginMySqlNativePassword;
        }

        // For caching_sha2_password, we may need to do full auth
        if (authPluginName == MySqlProtocolConstants.AuthPluginCachingSha2Password)
        {
            // Send fast auth success (we accept any password in this implementation)
            await _writer.WriteAuthMoreDataAsync(_state.SequenceId++, 0x03, ct).ConfigureAwait(false);
        }

        // Accept authentication (in production, validate against user database)
        await _writer.WriteOkPacketAsync(_state.SequenceId, serverStatus: _state.ServerStatus, ct: ct).ConfigureAwait(false);
        return true;
    }

    private async Task ProcessCommandAsync(MySqlPacket packet, CancellationToken ct)
    {
        if (packet.Payload.Length == 0)
        {
            return;
        }

        var command = (MySqlCommand)packet.Payload[0];

        switch (command)
        {
            case MySqlCommand.COM_QUIT:
                _client.Close();
                break;

            case MySqlCommand.COM_INIT_DB:
                await HandleInitDbAsync(packet, ct).ConfigureAwait(false);
                break;

            case MySqlCommand.COM_QUERY:
                await HandleQueryAsync(packet, ct).ConfigureAwait(false);
                break;

            case MySqlCommand.COM_PING:
                await HandlePingAsync(ct).ConfigureAwait(false);
                break;

            case MySqlCommand.COM_STMT_PREPARE:
                await HandlePrepareAsync(packet, ct).ConfigureAwait(false);
                break;

            case MySqlCommand.COM_STMT_EXECUTE:
                await HandleExecuteAsync(packet, ct).ConfigureAwait(false);
                break;

            case MySqlCommand.COM_STMT_CLOSE:
                HandleStmtClose(packet);
                break;

            case MySqlCommand.COM_STMT_RESET:
                await HandleStmtResetAsync(packet, ct).ConfigureAwait(false);
                break;

            case MySqlCommand.COM_RESET_CONNECTION:
                await HandleResetConnectionAsync(ct).ConfigureAwait(false);
                break;

            case MySqlCommand.COM_SET_OPTION:
                await HandleSetOptionAsync(packet, ct).ConfigureAwait(false);
                break;

            case MySqlCommand.COM_STATISTICS:
                await HandleStatisticsAsync(ct).ConfigureAwait(false);
                break;

            case MySqlCommand.COM_FIELD_LIST:
                await HandleFieldListAsync(packet, ct).ConfigureAwait(false);
                break;

            default:
                await _writer.WriteErrorPacketAsync(
                    _state.SequenceId++,
                    MySqlErrorCode.ER_UNKNOWN_COM_ERROR,
                    MySqlSqlState.ServerError,
                    $"Unknown command: {command}",
                    ct).ConfigureAwait(false);
                break;
        }
    }

    private async Task HandleInitDbAsync(MySqlPacket packet, CancellationToken ct)
    {
        var database = _reader.ParseInitDbCommand(packet);
        _state.Database = database;
        await _writer.WriteOkPacketAsync(_state.SequenceId++, serverStatus: _state.ServerStatus, ct: ct).ConfigureAwait(false);
    }

    private async Task HandleQueryAsync(MySqlPacket packet, CancellationToken ct)
    {
        try
        {
            var sql = _reader.ParseQueryCommand(packet);

            // Handle transaction statements
            var lowerSql = sql.ToLowerInvariant().Trim();

            if (lowerSql == "begin" || lowerSql == "start transaction")
            {
                _state.InTransaction = true;
                _state.ServerStatus |= MySqlServerStatus.SERVER_STATUS_IN_TRANS;
                _state.ServerStatus &= ~MySqlServerStatus.SERVER_STATUS_AUTOCOMMIT;
                await _writer.WriteOkPacketAsync(_state.SequenceId++, serverStatus: _state.ServerStatus, ct: ct).ConfigureAwait(false);
                return;
            }

            if (lowerSql == "commit")
            {
                _state.InTransaction = false;
                _state.ServerStatus &= ~MySqlServerStatus.SERVER_STATUS_IN_TRANS;
                if (_state.AutoCommit)
                {
                    _state.ServerStatus |= MySqlServerStatus.SERVER_STATUS_AUTOCOMMIT;
                }
                await _writer.WriteOkPacketAsync(_state.SequenceId++, serverStatus: _state.ServerStatus, ct: ct).ConfigureAwait(false);
                return;
            }

            if (lowerSql == "rollback")
            {
                _state.InTransaction = false;
                _state.ServerStatus &= ~MySqlServerStatus.SERVER_STATUS_IN_TRANS;
                if (_state.AutoCommit)
                {
                    _state.ServerStatus |= MySqlServerStatus.SERVER_STATUS_AUTOCOMMIT;
                }
                await _writer.WriteOkPacketAsync(_state.SequenceId++, serverStatus: _state.ServerStatus, ct: ct).ConfigureAwait(false);
                return;
            }

            // Execute query
            var result = await _queryProcessor.ExecuteQueryAsync(sql, ct).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(result.ErrorMessage))
            {
                await _writer.WriteErrorPacketAsync(
                    _state.SequenceId++,
                    result.ErrorCode ?? MySqlErrorCode.ER_INTERNAL_ERROR,
                    result.SqlState ?? MySqlSqlState.ServerError,
                    result.ErrorMessage,
                    ct).ConfigureAwait(false);
                return;
            }

            if (result.Columns.Count > 0)
            {
                // Result set
                await SendResultSetAsync(result, ct).ConfigureAwait(false);
            }
            else
            {
                // OK packet for non-SELECT
                await _writer.WriteOkPacketAsync(
                    _state.SequenceId++,
                    result.AffectedRows,
                    result.LastInsertId,
                    _state.ServerStatus,
                    result.Warnings,
                    ct: ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            await _writer.WriteErrorPacketAsync(
                _state.SequenceId++,
                MySqlErrorCode.ER_INTERNAL_ERROR,
                MySqlSqlState.ServerError,
                ex.Message,
                ct).ConfigureAwait(false);
        }
    }

    private async Task SendResultSetAsync(MySqlQueryResult result, CancellationToken ct)
    {
        // Column count
        _state.SequenceId = await _writer.WriteColumnCountAsync(_state.SequenceId, result.Columns.Count, ct).ConfigureAwait(false);

        // Column definitions
        foreach (var column in result.Columns)
        {
            _state.SequenceId = await _writer.WriteColumnDefinitionAsync(_state.SequenceId, column, ct).ConfigureAwait(false);
        }

        // EOF after columns (unless CLIENT_DEPRECATE_EOF)
        if (!_state.ClientCapabilities.HasFlag(MySqlCapabilities.CLIENT_DEPRECATE_EOF))
        {
            await _writer.WriteEofPacketAsync(_state.SequenceId++, serverStatus: _state.ServerStatus, ct: ct).ConfigureAwait(false);
        }

        // Rows
        foreach (var row in result.Rows)
        {
            _state.SequenceId = await _writer.WriteTextRowAsync(_state.SequenceId, row, ct).ConfigureAwait(false);
        }

        // EOF after rows (or OK with CLIENT_DEPRECATE_EOF)
        if (_state.ClientCapabilities.HasFlag(MySqlCapabilities.CLIENT_DEPRECATE_EOF))
        {
            await _writer.WriteOkPacketAsync(
                _state.SequenceId++,
                result.AffectedRows,
                result.LastInsertId,
                _state.ServerStatus | MySqlServerStatus.SERVER_STATUS_LAST_ROW_SENT,
                result.Warnings,
                ct: ct).ConfigureAwait(false);
        }
        else
        {
            await _writer.WriteEofPacketAsync(_state.SequenceId++, result.Warnings, _state.ServerStatus, ct).ConfigureAwait(false);
        }
    }

    private async Task HandlePingAsync(CancellationToken ct)
    {
        await _writer.WriteOkPacketAsync(_state.SequenceId++, serverStatus: _state.ServerStatus, ct: ct).ConfigureAwait(false);
    }

    private async Task HandlePrepareAsync(MySqlPacket packet, CancellationToken ct)
    {
        try
        {
            var sql = _reader.ParsePrepareCommand(packet);
            var parameterCount = (ushort)_queryProcessor.ExtractParameterCount(sql);

            var statementId = _state.NextStatementId++;
            var statement = new MySqlPreparedStatement
            {
                StatementId = statementId,
                Query = sql,
                ParameterCount = parameterCount,
                ColumnCount = 0 // Will be populated when executed
            };

            _state.PreparedStatements[statementId] = statement;

            // Send prepare response
            _state.SequenceId = await _writer.WritePrepareResponseAsync(
                _state.SequenceId,
                statementId,
                0, // Column count (unknown until execution)
                parameterCount,
                ct).ConfigureAwait(false);

            // Send parameter definitions if any
            if (parameterCount > 0)
            {
                for (int i = 0; i < parameterCount; i++)
                {
                    var paramDef = new MySqlColumnDescription
                    {
                        Catalog = "def",
                        Schema = "",
                        VirtualTable = "",
                        PhysicalTable = "",
                        VirtualName = $"?",
                        PhysicalName = "",
                        CharacterSet = MySqlProtocolConstants.BinaryCollation,
                        ColumnLength = 0,
                        ColumnType = MySqlFieldType.MYSQL_TYPE_VAR_STRING,
                        Flags = MySqlFieldFlags.None,
                        Decimals = 0
                    };

                    _state.SequenceId = await _writer.WriteColumnDefinitionAsync(_state.SequenceId, paramDef, ct).ConfigureAwait(false);
                }

                if (!_state.ClientCapabilities.HasFlag(MySqlCapabilities.CLIENT_DEPRECATE_EOF))
                {
                    await _writer.WriteEofPacketAsync(_state.SequenceId++, serverStatus: _state.ServerStatus, ct: ct).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            await _writer.WriteErrorPacketAsync(
                _state.SequenceId++,
                MySqlErrorCode.ER_INTERNAL_ERROR,
                MySqlSqlState.ServerError,
                ex.Message,
                ct).ConfigureAwait(false);
        }
    }

    private async Task HandleExecuteAsync(MySqlPacket packet, CancellationToken ct)
    {
        try
        {
            // Parse statement ID from packet
            if (packet.Payload.Length < 5)
            {
                await _writer.WriteErrorPacketAsync(
                    _state.SequenceId++,
                    MySqlErrorCode.ER_UNKNOWN_STMT_HANDLER,
                    MySqlSqlState.ServerError,
                    "Invalid execute packet",
                    ct).ConfigureAwait(false);
                return;
            }

            var statementId = BitConverter.ToUInt32(packet.Payload, 1);

            if (!_state.PreparedStatements.TryGetValue(statementId, out var statement))
            {
                await _writer.WriteErrorPacketAsync(
                    _state.SequenceId++,
                    MySqlErrorCode.ER_UNKNOWN_STMT_HANDLER,
                    MySqlSqlState.ServerError,
                    $"Unknown prepared statement: {statementId}",
                    ct).ConfigureAwait(false);
                return;
            }

            var request = _reader.ParseExecuteCommand(packet, statement);
            var sql = _queryProcessor.SubstituteParameters(statement.Query, request.ParameterValues);

            var result = await _queryProcessor.ExecuteQueryAsync(sql, ct).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(result.ErrorMessage))
            {
                await _writer.WriteErrorPacketAsync(
                    _state.SequenceId++,
                    result.ErrorCode ?? MySqlErrorCode.ER_INTERNAL_ERROR,
                    result.SqlState ?? MySqlSqlState.ServerError,
                    result.ErrorMessage,
                    ct).ConfigureAwait(false);
                return;
            }

            if (result.Columns.Count > 0)
            {
                await SendResultSetAsync(result, ct).ConfigureAwait(false);
            }
            else
            {
                await _writer.WriteOkPacketAsync(
                    _state.SequenceId++,
                    result.AffectedRows,
                    result.LastInsertId,
                    _state.ServerStatus,
                    result.Warnings,
                    ct: ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            await _writer.WriteErrorPacketAsync(
                _state.SequenceId++,
                MySqlErrorCode.ER_INTERNAL_ERROR,
                MySqlSqlState.ServerError,
                ex.Message,
                ct).ConfigureAwait(false);
        }
    }

    private void HandleStmtClose(MySqlPacket packet)
    {
        if (packet.Payload.Length >= 5)
        {
            var statementId = _reader.ParseCloseCommand(packet);
            _state.PreparedStatements.Remove(statementId);
        }
        // No response for COM_STMT_CLOSE
    }

    private async Task HandleStmtResetAsync(MySqlPacket packet, CancellationToken ct)
    {
        if (packet.Payload.Length >= 5)
        {
            var statementId = BitConverter.ToUInt32(packet.Payload, 1);
            // Reset statement state if needed
        }
        await _writer.WriteOkPacketAsync(_state.SequenceId++, serverStatus: _state.ServerStatus, ct: ct).ConfigureAwait(false);
    }

    private async Task HandleResetConnectionAsync(CancellationToken ct)
    {
        // Reset connection state
        _state.PreparedStatements.Clear();
        _state.InTransaction = false;
        _state.AutoCommit = true;
        _state.ServerStatus = MySqlServerStatus.SERVER_STATUS_AUTOCOMMIT;

        await _writer.WriteOkPacketAsync(_state.SequenceId++, serverStatus: _state.ServerStatus, ct: ct).ConfigureAwait(false);
    }

    private async Task HandleSetOptionAsync(MySqlPacket packet, CancellationToken ct)
    {
        // Set option (e.g., multi-statements)
        // Option is in payload[1..2]
        await _writer.WriteEofPacketAsync(_state.SequenceId++, serverStatus: _state.ServerStatus, ct: ct).ConfigureAwait(false);
    }

    private async Task HandleStatisticsAsync(CancellationToken ct)
    {
        var stats = $"Uptime: 3600  Threads: 1  Questions: 1  Slow queries: 0  Opens: 0  " +
                   $"Flush tables: 0  Open tables: 0  Queries per second avg: 0.000";

        var payload = System.Text.Encoding.UTF8.GetBytes(stats);
        await _writer.WritePacketAsync(payload, _state.SequenceId++, ct).ConfigureAwait(false);
    }

    private async Task HandleFieldListAsync(MySqlPacket packet, CancellationToken ct)
    {
        // Field list returns column definitions for a table
        // For simplicity, return empty result
        if (!_state.ClientCapabilities.HasFlag(MySqlCapabilities.CLIENT_DEPRECATE_EOF))
        {
            await _writer.WriteEofPacketAsync(_state.SequenceId++, serverStatus: _state.ServerStatus, ct: ct).ConfigureAwait(false);
        }
        else
        {
            await _writer.WriteOkPacketAsync(_state.SequenceId++, serverStatus: _state.ServerStatus, ct: ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Releases resources used by the protocol handler.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_stream is SslStream sslStream)
            {
                sslStream.Dispose();
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }
}
