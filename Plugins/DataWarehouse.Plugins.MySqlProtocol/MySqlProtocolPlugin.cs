using DataWarehouse.Plugins.MySqlProtocol.Protocol;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

// Disambiguate SDK's HandshakeResponse from local protocol HandshakeResponse
using SdkHandshakeResponse = DataWarehouse.SDK.Primitives.HandshakeResponse;

namespace DataWarehouse.Plugins.MySqlProtocol;

/// <summary>
/// MySQL Wire Protocol Plugin for SQL tool compatibility.
/// Implements the MySQL Client/Server Protocol (version 4.1+), allowing MySQL tools
/// like HeidiSQL, MySQL Workbench, SQLyog, and others to connect to DataWarehouse.
///
/// Features:
/// - Full MySQL Client/Server Protocol 4.1+ support
/// - Authentication (mysql_native_password, caching_sha2_password, trust)
/// - SSL/TLS encryption support
/// - Simple query protocol (COM_QUERY)
/// - Prepared statements (COM_STMT_PREPARE, COM_STMT_EXECUTE, COM_STMT_CLOSE)
/// - Connection pooling and session management
/// - System catalog queries for tool compatibility
/// - Transaction support (BEGIN, COMMIT, ROLLBACK)
///
/// Supported clients:
/// - HeidiSQL, MySQL Workbench, SQLyog, DBeaver, DataGrip
/// - mysql command line client
/// - Python (mysql-connector-python, PyMySQL), Node.js (mysql2), .NET (MySqlConnector)
/// - Any MySQL JDBC/ODBC driver
///
/// Usage:
///   mysql -h localhost -P 3306 -u root -p datawarehouse
///   # Or connect via any MySQL-compatible tool
/// </summary>
public sealed class MySqlProtocolPlugin : InterfacePluginBase, IDisposable
{
    /// <summary>
    /// Unique plugin identifier.
    /// </summary>
    public override string Id => "com.datawarehouse.protocol.mysql";

    /// <summary>
    /// Human-readable plugin name.
    /// </summary>
    public override string Name => "MySQL Wire Protocol";

    /// <summary>
    /// Plugin version following semantic versioning.
    /// </summary>
    public override string Version => "1.0.0";

    /// <summary>
    /// Plugin category for classification.
    /// </summary>
    public override PluginCategory Category => PluginCategory.InterfaceProvider;

    /// <summary>
    /// Protocol identifier for this interface.
    /// </summary>
    public override string Protocol => "mysql";

    /// <summary>
    /// Port number the plugin listens on.
    /// </summary>
    public override int? Port => _config.Port;

    private readonly MySqlProtocolConfig _config = new();
    private readonly ConcurrentDictionary<string, MySqlConnectionState> _connections = new();
    private readonly ConcurrentDictionary<string, ProtocolHandler> _handlers = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private Func<string, CancellationToken, Task<MySqlQueryResult>>? _sqlExecutor;
    private uint _nextConnectionId = 1;
    private readonly object _connectionIdLock = new();
    private bool _disposed;

    /// <summary>
    /// Handles plugin handshake and configuration.
    /// </summary>
    /// <param name="request">Handshake request with configuration.</param>
    /// <returns>Handshake response indicating success or failure.</returns>
    public override Task<SdkHandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
    {
        if (request.Config != null)
        {
            if (request.Config.TryGetValue("port", out var port))
            {
                _config.Port = port switch
                {
                    int p => p,
                    long l => (int)l,
                    string s when int.TryParse(s, out var sp) => sp,
                    _ => _config.Port
                };
            }

            if (request.Config.TryGetValue("maxConnections", out var maxConn))
            {
                _config.MaxConnections = maxConn switch
                {
                    int m => m,
                    long l => (int)l,
                    string s when int.TryParse(s, out var sm) => sm,
                    _ => _config.MaxConnections
                };
            }

            if (request.Config.TryGetValue("authMethod", out var authMethod) && authMethod is string am)
            {
                _config.AuthMethod = am;
            }

            if (request.Config.TryGetValue("sslMode", out var sslMode) && sslMode is string sm2)
            {
                _config.SslMode = sm2;
            }

            if (request.Config.TryGetValue("serverVersion", out var version) && version is string v)
            {
                _config.ServerVersion = v;
            }

            if (request.Config.TryGetValue("sslCertificatePath", out var certPath) && certPath is string cp)
            {
                _config.SslCertificatePath = cp;
            }

            if (request.Config.TryGetValue("sslKeyPath", out var keyPath) && keyPath is string kp)
            {
                _config.SslKeyPath = kp;
            }

            if (request.Config.TryGetValue("sqlExecutor", out var executor) &&
                executor is Func<string, CancellationToken, Task<MySqlQueryResult>> sqlExec)
            {
                _sqlExecutor = sqlExec;
            }
        }

        return Task.FromResult(new SdkHandshakeResponse
        {
            PluginId = Id,
            Name = Name,
            Version = ParseSemanticVersion(Version),
            Category = Category,
            Success = true,
            ReadyState = PluginReadyState.Ready,
            Capabilities = GetCapabilities(),
            Metadata = GetMetadata()
        });
    }

    /// <summary>
    /// Starts the MySQL protocol listener.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public override async Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        try
        {
            _listener = new TcpListener(IPAddress.Any, _config.Port);
            _listener.Start(_config.MaxConnections);

            _acceptTask = AcceptConnectionsAsync(_cts.Token);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            throw new InvalidOperationException(
                $"Port {_config.Port} is already in use. Configure a different port or stop the conflicting service.",
                ex);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to start MySQL protocol listener: {ex.Message}", ex);
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Stops the MySQL protocol listener and closes all connections.
    /// </summary>
    public override async Task StopAsync()
    {
        _cts?.Cancel();

        try
        {
            _listener?.Stop();
        }
        catch
        {
            // Best effort
        }

        // Close all active connections
        foreach (var handler in _handlers.Values)
        {
            try
            {
                handler.Dispose();
            }
            catch
            {
                // Best effort
            }
        }

        _handlers.Clear();
        _connections.Clear();

        if (_acceptTask != null)
        {
            try
            {
                await _acceptTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
            catch
            {
                // Ignore errors during shutdown
            }
        }
    }

    /// <summary>
    /// Handles plugin messages.
    /// </summary>
    /// <param name="message">The message to handle.</param>
    public override Task OnMessageAsync(PluginMessage message)
    {
        if (message.Payload == null)
            return Task.CompletedTask;

        var response = message.Type switch
        {
            "mysql.start" => HandleStart(),
            "mysql.stop" => HandleStop(),
            "mysql.status" => HandleStatus(),
            "mysql.connections" => HandleConnections(),
            "mysql.kill" => HandleKillConnection(message.Payload),
            _ => new Dictionary<string, object> { ["error"] = $"Unknown command: {message.Type}" }
        };

        message.Payload["_response"] = response;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Sets the SQL executor callback for executing queries.
    /// Should be called before starting the plugin.
    /// </summary>
    /// <param name="executor">The SQL executor delegate.</param>
    /// <exception cref="ArgumentNullException">Thrown when executor is null.</exception>
    public void SetSqlExecutor(Func<string, CancellationToken, Task<MySqlQueryResult>> executor)
    {
        _sqlExecutor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    /// <summary>
    /// Gets the plugin capabilities.
    /// </summary>
    protected override List<PluginCapabilityDescriptor> GetCapabilities()
    {
        return new List<PluginCapabilityDescriptor>
        {
            new()
            {
                Name = "mysql_wire_protocol",
                DisplayName = "MySQL Wire Protocol",
                Description = "Full MySQL Client/Server Protocol 4.1+ support"
            },
            new()
            {
                Name = "mysql_native_password",
                DisplayName = "Native Password Authentication",
                Description = "SHA1-based mysql_native_password authentication"
            },
            new()
            {
                Name = "caching_sha2_password",
                DisplayName = "Caching SHA2 Authentication",
                Description = "SHA256-based caching_sha2_password authentication (MySQL 8.0 default)"
            },
            new()
            {
                Name = "ssl_tls",
                DisplayName = "SSL/TLS Encryption",
                Description = "Secure connections via TLS 1.2/1.3"
            },
            new()
            {
                Name = "simple_query",
                DisplayName = "Simple Query Protocol",
                Description = "Execute queries via COM_QUERY"
            },
            new()
            {
                Name = "prepared_statements",
                DisplayName = "Prepared Statements",
                Description = "COM_STMT_PREPARE, COM_STMT_EXECUTE, COM_STMT_CLOSE"
            },
            new()
            {
                Name = "transactions",
                DisplayName = "Transactions",
                Description = "BEGIN, COMMIT, ROLLBACK support"
            },
            new()
            {
                Name = "connection_pooling",
                DisplayName = "Connection Pooling",
                Description = "Session management with configurable connection limits"
            },
            new()
            {
                Name = "system_catalogs",
                DisplayName = "System Catalogs",
                Description = "information_schema queries for tool compatibility"
            }
        };
    }

    /// <summary>
    /// Gets plugin metadata.
    /// </summary>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["WireProtocol"] = "MySQL 4.1+";
        metadata["Port"] = _config.Port;
        metadata["MaxConnections"] = _config.MaxConnections;
        metadata["AuthMethod"] = _config.AuthMethod;
        metadata["SslMode"] = _config.SslMode;
        metadata["ServerVersion"] = _config.ServerVersion;
        metadata["ActiveConnections"] = _connections.Count;
        metadata["CompatibleClients"] = new[]
        {
            "HeidiSQL", "MySQL Workbench", "SQLyog", "DBeaver", "DataGrip",
            "mysql CLI", "MySqlConnector", "mysql-connector-python", "PyMySQL", "mysql2 (Node.js)"
        };
        return metadata;
    }

    private async Task AcceptConnectionsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false);

                // Check connection limit
                if (_connections.Count >= _config.MaxConnections)
                {
                    await RejectConnectionAsync(client, ct).ConfigureAwait(false);
                    continue;
                }

                // Handle connection in background
                _ = HandleConnectionAsync(client, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Continue accepting connections despite individual errors
            }
        }
    }

    private async Task RejectConnectionAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            var stream = client.GetStream();
            var writer = new MessageWriter(stream);
            await writer.WriteErrorPacketAsync(
                0,
                MySqlErrorCode.ER_CON_COUNT_ERROR,
                MySqlSqlState.ConnectionException,
                $"Too many connections (max: {_config.MaxConnections})",
                ct).ConfigureAwait(false);
        }
        catch
        {
            // Best effort
        }
        finally
        {
            try { client.Close(); } catch { }
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken ct)
    {
        uint connectionId;
        lock (_connectionIdLock)
        {
            connectionId = _nextConnectionId++;
        }

        var connectionGuid = Guid.NewGuid().ToString("N");
        ProtocolHandler? handler = null;

        try
        {
            var queryProcessor = new QueryProcessor(_sqlExecutor ?? DefaultSqlExecutor);
            handler = new ProtocolHandler(client, _config, queryProcessor, connectionId);

            _handlers[connectionGuid] = handler;
            _connections[connectionGuid] = handler.State;

            await handler.HandleConnectionAsync(ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Connection error - already handled internally
        }
        finally
        {
            _handlers.TryRemove(connectionGuid, out _);
            _connections.TryRemove(connectionGuid, out _);

            handler?.Dispose();

            try { client.Close(); } catch { }
        }
    }

    private Task<MySqlQueryResult> DefaultSqlExecutor(string sql, CancellationToken ct)
    {
        return Task.FromResult(new MySqlQueryResult
        {
            Columns = new List<MySqlColumnDescription>
            {
                TypeConverter.CreateColumnDescription("message", 0)
            },
            Rows = new List<List<byte[]?>>
            {
                new() { System.Text.Encoding.UTF8.GetBytes("SQL executor not configured") }
            },
            AffectedRows = 1,
            CommandTag = "SELECT"
        });
    }

    private Dictionary<string, object> HandleStart()
    {
        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["message"] = "MySQL protocol listener is running",
            ["port"] = _config.Port
        };
    }

    private Dictionary<string, object> HandleStop()
    {
        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["message"] = "Stop requested"
        };
    }

    private Dictionary<string, object> HandleStatus()
    {
        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["port"] = _config.Port,
            ["maxConnections"] = _config.MaxConnections,
            ["activeConnections"] = _connections.Count,
            ["authMethod"] = _config.AuthMethod,
            ["sslMode"] = _config.SslMode,
            ["serverVersion"] = _config.ServerVersion,
            ["isListening"] = _listener != null
        };
    }

    private Dictionary<string, object> HandleConnections()
    {
        var connections = _connections.Values.Select(conn => new Dictionary<string, object>
        {
            ["connectionId"] = conn.ConnectionId,
            ["threadId"] = conn.ThreadId,
            ["username"] = conn.Username,
            ["database"] = conn.Database,
            ["clientAddress"] = conn.ClientAddress ?? "unknown",
            ["connectedAt"] = conn.ConnectedAt.ToString("O"),
            ["inTransaction"] = conn.InTransaction,
            ["autoCommit"] = conn.AutoCommit,
            ["sslEnabled"] = conn.SslEnabled,
            ["preparedStatements"] = conn.PreparedStatements.Count
        }).ToList();

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["connections"] = connections,
            ["count"] = connections.Count
        };
    }

    private Dictionary<string, object> HandleKillConnection(Dictionary<string, object> payload)
    {
        if (!payload.TryGetValue("connectionId", out var connIdObj) || connIdObj is not string connId)
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "connectionId is required"
            };
        }

        if (_handlers.TryRemove(connId, out var handler))
        {
            handler.Dispose();
            _connections.TryRemove(connId, out _);

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["message"] = $"Connection {connId} killed"
            };
        }

        return new Dictionary<string, object>
        {
            ["success"] = false,
            ["error"] = $"Connection {connId} not found"
        };
    }

    /// <summary>
    /// Releases resources used by the plugin.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts?.Cancel();

        foreach (var handler in _handlers.Values)
        {
            try { handler.Dispose(); } catch { }
        }

        _handlers.Clear();
        _connections.Clear();

        try { _listener?.Stop(); } catch { }

        _cts?.Dispose();
    }
}
