using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace DataWarehouse.Plugins.TdsProtocol.Protocol;

/// <summary>
/// Handles TDS protocol communication for a single client connection.
/// Manages the connection lifecycle from PRELOGIN through query execution.
/// Thread-safe for single-connection scenarios.
/// </summary>
public sealed class TdsProtocolHandler : IDisposable
{
    private readonly TcpClient _client;
    private readonly TdsProtocolConfig _config;
    private readonly TdsQueryProcessor _queryProcessor;
    private readonly TdsConnectionState _state;
    private TdsPacketReader _reader;
    private TdsPacketWriter _writer;
    private Stream _stream;
    private bool _disposed;
    private bool _isEncrypted;

    /// <summary>
    /// Initializes a new instance of the <see cref="TdsProtocolHandler"/> class.
    /// </summary>
    /// <param name="client">The TCP client connection.</param>
    /// <param name="config">Protocol configuration.</param>
    /// <param name="queryProcessor">Query processor for SQL execution.</param>
    /// <exception cref="ArgumentNullException">Thrown when any parameter is null.</exception>
    public TdsProtocolHandler(
        TcpClient client,
        TdsProtocolConfig config,
        TdsQueryProcessor queryProcessor)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _queryProcessor = queryProcessor ?? throw new ArgumentNullException(nameof(queryProcessor));

        _stream = client.GetStream();
        _reader = new TdsPacketReader(_stream);
        _writer = new TdsPacketWriter(_stream, config.PacketSize);

        _state = new TdsConnectionState
        {
            ConnectionId = Guid.NewGuid().ToString("N"),
            ProcessId = Random.Shared.Next(51, 65535),
            Database = config.DefaultDatabase,
            PacketSize = config.PacketSize
        };
    }

    /// <summary>
    /// Gets the connection state.
    /// </summary>
    public TdsConnectionState ConnectionState => _state;

    /// <summary>
    /// Handles the entire connection lifecycle.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task HandleConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            // Phase 1: PRELOGIN exchange
            await HandlePreLoginAsync(ct);

            // Phase 2: TLS handshake (if required)
            if (_config.EncryptionMode != "off" && _isEncrypted)
            {
                await UpgradeToTlsAsync(ct);
            }

            // Phase 3: LOGIN7 authentication
            await HandleLoginAsync(ct);

            // Phase 4: Main command loop
            await HandleCommandLoopAsync(ct);
        }
        catch (EndOfStreamException)
        {
            // Client disconnected normally
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancellation requested
        }
        catch (Exception ex)
        {
            try
            {
                await _writer.WriteErrorAsync(
                    TdsErrorNumbers.GeneralError,
                    TdsSeverity.FatalError,
                    1,
                    $"Server error: {ex.Message}",
                    _config.ServerName,
                    ct);
            }
            catch
            {
                // Best effort error reporting
            }
            throw;
        }
    }

    /// <summary>
    /// Handles the PRELOGIN handshake.
    /// </summary>
    private async Task HandlePreLoginAsync(CancellationToken ct)
    {
        // Read PRELOGIN message
        var message = await _reader.ReadMessageAsync(ct);

        if (message.PacketType != TdsPacketType.PreLogin)
        {
            throw new InvalidOperationException($"Expected PRELOGIN message, got {message.PacketType}");
        }

        // Parse client PRELOGIN options
        var clientOptions = _reader.ParsePreLogin(message.Data);

        // Determine encryption negotiation
        var serverEncryption = _config.EncryptionMode.ToLowerInvariant() switch
        {
            "off" => TdsEncryption.Off,
            "on" => TdsEncryption.On,
            "required" => TdsEncryption.Required,
            _ => TdsEncryption.On
        };

        // Negotiate encryption
        var negotiatedEncryption = NegotiateEncryption(clientOptions.Encryption, serverEncryption);
        _isEncrypted = negotiatedEncryption != TdsEncryption.Off;

        // Build and send PRELOGIN response
        var response = new PreLoginResponse
        {
            ServerVersion = new Version(16, 0, 1000, 0),
            Encryption = negotiatedEncryption,
            InstanceName = _config.ServerName,
            ThreadId = (uint)Environment.CurrentManagedThreadId,
            MarsEnabled = clientOptions.Mars,
            TraceId = Guid.NewGuid()
        };

        await _writer.WritePreLoginResponseAsync(response, ct);
    }

    /// <summary>
    /// Negotiates encryption based on client and server preferences.
    /// </summary>
    private TdsEncryption NegotiateEncryption(TdsEncryption client, TdsEncryption server)
    {
        // If either side requires encryption, encrypt
        if (client == TdsEncryption.Required || server == TdsEncryption.Required)
        {
            return TdsEncryption.Required;
        }

        // If both sides support encryption and either prefers it, encrypt
        if (client != TdsEncryption.NotSupported && server != TdsEncryption.NotSupported)
        {
            if (client == TdsEncryption.On || server == TdsEncryption.On)
            {
                return TdsEncryption.On;
            }
        }

        // Otherwise, no encryption
        return TdsEncryption.Off;
    }

    /// <summary>
    /// Upgrades the connection to TLS.
    /// </summary>
    private async Task UpgradeToTlsAsync(CancellationToken ct)
    {
        X509Certificate2? serverCert = null;

        if (!string.IsNullOrEmpty(_config.SslCertificatePath) && File.Exists(_config.SslCertificatePath))
        {
            serverCert = new X509Certificate2(_config.SslCertificatePath, _config.SslCertificatePassword);
        }
        else
        {
            // Generate a self-signed certificate for development
            serverCert = GenerateSelfSignedCertificate();
        }

        var sslStream = new SslStream(_stream, false);

        try
        {
            await sslStream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = serverCert,
                ClientCertificateRequired = false,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, ct);

            _stream = sslStream;
            _reader = new TdsPacketReader(_stream);
            _writer = new TdsPacketWriter(_stream, _state.PacketSize);
            _state.EncryptionEnabled = true;
        }
        catch (Exception ex)
        {
            // TLS handshake failed - continue without encryption if allowed
            if (_config.EncryptionMode == "required")
            {
                throw new InvalidOperationException($"TLS handshake failed and encryption is required: {ex.Message}", ex);
            }

            // Fall back to unencrypted
            _isEncrypted = false;
            sslStream.Dispose();
        }
    }

    /// <summary>
    /// Generates a self-signed certificate for development/testing.
    /// </summary>
    private static X509Certificate2 GenerateSelfSignedCertificate()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);

        var request = new CertificateRequest(
            "CN=DataWarehouse TDS",
            rsa,
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));

        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false)); // Server authentication

        var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(1));

        // Export and re-import to get a certificate with a private key that works on all platforms
        return new X509Certificate2(cert.Export(X509ContentType.Pfx), (string?)null,
            X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.Exportable);
    }

    /// <summary>
    /// Handles the LOGIN7 authentication.
    /// </summary>
    private async Task HandleLoginAsync(CancellationToken ct)
    {
        // Read LOGIN7 message
        var message = await _reader.ReadMessageAsync(ct);

        if (message.PacketType != TdsPacketType.Tds7Login)
        {
            throw new InvalidOperationException($"Expected LOGIN7 message, got {message.PacketType}");
        }

        // Parse LOGIN7 data
        var login = _reader.ParseLogin7(message.Data);

        // Validate and authenticate
        if (!await AuthenticateAsync(login, ct))
        {
            await _writer.WriteErrorAsync(
                TdsErrorNumbers.LoginFailed,
                TdsSeverity.FatalError,
                1,
                $"Login failed for user '{login.Username}'.",
                _config.ServerName,
                ct);
            throw new InvalidOperationException("Authentication failed");
        }

        // Update connection state
        _state.Username = login.Username;
        _state.Database = string.IsNullOrEmpty(login.Database) ? _config.DefaultDatabase : login.Database;
        _state.ApplicationName = login.ApplicationName;
        _state.Hostname = login.Hostname;
        _state.TdsVersion = login.TdsVersion;
        _state.PacketSize = login.PacketSize > 0 ? login.PacketSize : _config.PacketSize;
        _state.ClientPid = (int)login.ClientPid;
        _state.ClientInterfaceName = login.LibraryName;
        _state.Language = string.IsNullOrEmpty(login.Language) ? "us_english" : login.Language;
        _state.MarsEnabled = (login.OptionFlags2 & 0x02) != 0;

        // Send login acknowledgment
        await _writer.WriteLoginAckAsync(_state, _config.ServerName, ct);
    }

    /// <summary>
    /// Authenticates the login request.
    /// </summary>
    private async Task<bool> AuthenticateAsync(Login7Data login, CancellationToken ct)
    {
        await Task.CompletedTask;

        // For SQL authentication
        if (_config.AuthMethod.Equals("sql", StringComparison.OrdinalIgnoreCase))
        {
            // In production, validate against user store
            // For now, accept any non-empty username
            return !string.IsNullOrEmpty(login.Username);
        }

        // For Windows/Integrated authentication
        if (_config.AuthMethod.Equals("windows", StringComparison.OrdinalIgnoreCase) ||
            _config.AuthMethod.Equals("integrated", StringComparison.OrdinalIgnoreCase))
        {
            // Would need SSPI/NTLM/Kerberos handling
            // For now, accept if SSPI data is present
            return login.SspiData != null && login.SspiData.Length > 0;
        }

        // Trust authentication - accept all
        if (_config.AuthMethod.Equals("trust", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Main command processing loop.
    /// </summary>
    private async Task HandleCommandLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _client.Connected)
        {
            TdsPacketMessage message;
            try
            {
                message = await _reader.ReadMessageAsync(ct);
            }
            catch (EndOfStreamException)
            {
                // Client disconnected
                break;
            }

            try
            {
                await ProcessMessageAsync(message, ct);
            }
            catch (Exception ex)
            {
                await _writer.WriteErrorAsync(
                    TdsErrorNumbers.GeneralError,
                    TdsSeverity.UserError,
                    1,
                    ex.Message,
                    _config.ServerName,
                    ct);
            }
        }
    }

    /// <summary>
    /// Processes a single TDS message.
    /// </summary>
    private async Task ProcessMessageAsync(TdsPacketMessage message, CancellationToken ct)
    {
        switch (message.PacketType)
        {
            case TdsPacketType.SqlBatch:
                await HandleSqlBatchAsync(message.Data, ct);
                break;

            case TdsPacketType.Rpc:
                await HandleRpcAsync(message.Data, ct);
                break;

            case TdsPacketType.Attention:
                await HandleAttentionAsync(ct);
                break;

            case TdsPacketType.TransactionManager:
                await HandleTransactionManagerAsync(message.Data, ct);
                break;

            default:
                await _writer.WriteErrorAsync(
                    TdsErrorNumbers.GeneralError,
                    TdsSeverity.UserError,
                    1,
                    $"Unsupported message type: {message.PacketType}",
                    _config.ServerName,
                    ct);
                break;
        }
    }

    /// <summary>
    /// Handles SQL batch execution.
    /// </summary>
    private async Task HandleSqlBatchAsync(byte[] data, CancellationToken ct)
    {
        var sql = _reader.ParseSqlBatch(data);

        // Handle multiple statements separated by GO or semicolon
        var statements = SplitStatements(sql);

        foreach (var statement in statements)
        {
            if (string.IsNullOrWhiteSpace(statement))
                continue;

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                if (_config.QueryTimeoutSeconds > 0)
                {
                    cts.CancelAfter(TimeSpan.FromSeconds(_config.QueryTimeoutSeconds));
                }

                var result = await _queryProcessor.ExecuteQueryAsync(statement, cts.Token);
                await _writer.WriteQueryResultAsync(result, ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                await _writer.WriteErrorAsync(
                    TdsErrorNumbers.TimeoutExpired,
                    TdsSeverity.UserError,
                    1,
                    "Query timeout expired",
                    _config.ServerName,
                    ct);
            }
        }
    }

    /// <summary>
    /// Handles RPC request execution.
    /// </summary>
    private async Task HandleRpcAsync(byte[] data, CancellationToken ct)
    {
        var request = _reader.ParseRpcRequest(data);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (_config.QueryTimeoutSeconds > 0)
        {
            cts.CancelAfter(TimeSpan.FromSeconds(_config.QueryTimeoutSeconds));
        }

        try
        {
            var result = await _queryProcessor.ExecuteRpcAsync(request, _state, cts.Token);

            // Handle sp_prepare/sp_prepexec which return a handle
            if (result.ReturnValue.HasValue)
            {
                await _writer.WritePrepareResponseAsync(result.ReturnValue.Value, ct);
            }
            else
            {
                await _writer.WriteQueryResultAsync(result, ct);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await _writer.WriteErrorAsync(
                TdsErrorNumbers.TimeoutExpired,
                TdsSeverity.UserError,
                1,
                "Query timeout expired",
                _config.ServerName,
                ct);
        }
    }

    /// <summary>
    /// Handles attention (cancel) signal.
    /// </summary>
    private async Task HandleAttentionAsync(CancellationToken ct)
    {
        // Send DONE with attention acknowledgment
        await _writer.WriteDoneAsync(0, ct);
    }

    /// <summary>
    /// Handles transaction manager requests.
    /// </summary>
    private async Task HandleTransactionManagerAsync(byte[] data, CancellationToken ct)
    {
        // Parse transaction manager request type
        if (data.Length < 2)
        {
            await _writer.WriteDoneAsync(0, ct);
            return;
        }

        var requestType = BitConverter.ToUInt16(data, 0);

        switch (requestType)
        {
            case 0: // TM_GET_DTC_ADDRESS
                // Not implemented - return empty
                await _writer.WriteDoneAsync(0, ct);
                break;

            case 5: // TM_BEGIN_XACT
                _state.InTransaction = true;
                _state.TransactionId = Random.Shared.NextInt64();
                await _writer.WriteDoneAsync(0, ct);
                break;

            case 6: // TM_COMMIT_XACT
                _state.InTransaction = false;
                _state.TransactionId = 0;
                await _writer.WriteDoneAsync(0, ct);
                break;

            case 7: // TM_ROLLBACK_XACT
                _state.InTransaction = false;
                _state.TransactionId = 0;
                await _writer.WriteDoneAsync(0, ct);
                break;

            default:
                await _writer.WriteErrorAsync(
                    TdsErrorNumbers.GeneralError,
                    TdsSeverity.UserError,
                    1,
                    $"Unsupported transaction manager request type: {requestType}",
                    _config.ServerName,
                    ct);
                break;
        }
    }

    /// <summary>
    /// Splits SQL batch into individual statements.
    /// </summary>
    private static List<string> SplitStatements(string sql)
    {
        var statements = new List<string>();
        var current = new System.Text.StringBuilder();
        var lines = sql.Split('\n');

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            // Check for GO separator
            if (trimmed.Equals("GO", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("GO ", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("GO\t", StringComparison.OrdinalIgnoreCase))
            {
                if (current.Length > 0)
                {
                    statements.Add(current.ToString().Trim());
                    current.Clear();
                }
                continue;
            }

            current.AppendLine(line);
        }

        if (current.Length > 0)
        {
            var remaining = current.ToString().Trim();
            if (!string.IsNullOrEmpty(remaining))
            {
                statements.Add(remaining);
            }
        }

        return statements;
    }

    /// <summary>
    /// Disposes resources.
    /// </summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _reader.Dispose();
            _writer.Dispose();

            try
            {
                _client.Close();
            }
            catch
            {
                // Best effort cleanup
            }
        }
    }
}
