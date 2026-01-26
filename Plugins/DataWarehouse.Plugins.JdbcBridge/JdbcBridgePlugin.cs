using DataWarehouse.Plugins.JdbcBridge.Protocol;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace DataWarehouse.Plugins.JdbcBridge;

/// <summary>
/// JDBC Bridge Plugin for DataWarehouse.
/// Implements a JDBC Type 4 driver bridge server, enabling Java applications to connect to DataWarehouse
/// using standard JDBC interfaces (java.sql.Connection, Statement, PreparedStatement, ResultSet).
///
/// Features:
/// - Full JDBC 4.2 wire protocol support
/// - Connection pooling and management
/// - Prepared statements with parameter binding
/// - Batch operations for bulk inserts/updates
/// - Transaction support with savepoints
/// - DatabaseMetaData and ResultSetMetaData retrieval
/// - SSL/TLS encryption (optional)
/// - Thread-safe connection handling
///
/// Wire Protocol:
/// - Length-prefixed binary protocol for efficient network transport
/// - Big-endian byte order for cross-platform compatibility
/// - Supports all JDBC SQL types
///
/// Usage:
///   Java: Connection conn = DriverManager.getConnection("jdbc:datawarehouse://localhost:9527/datawarehouse", user, password);
/// </summary>
public sealed class JdbcBridgePlugin : InterfacePluginBase
{
    /// <summary>
    /// Gets the unique plugin identifier.
    /// </summary>
    public override string Id => "com.datawarehouse.bridge.jdbc";

    /// <summary>
    /// Gets the human-readable plugin name.
    /// </summary>
    public override string Name => "JDBC Bridge Server";

    /// <summary>
    /// Gets the plugin version.
    /// </summary>
    public override string Version => "1.0.0";

    /// <summary>
    /// Gets the plugin category.
    /// </summary>
    public override PluginCategory Category => PluginCategory.InterfaceProvider;

    /// <summary>
    /// Gets the protocol name.
    /// </summary>
    public override string Protocol => "jdbc";

    /// <summary>
    /// Gets the configured port number.
    /// </summary>
    public override int? Port => _config.Port;

    private readonly JdbcBridgeConfig _config = new();
    private readonly ConcurrentDictionary<string, ConnectionHandler> _connections = new();
    private readonly object _listenerLock = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private Func<string, CancellationToken, Task<JdbcQueryResult>>? _sqlExecutor;
    private volatile bool _isRunning;

    /// <summary>
    /// Handles the initial handshake with the kernel.
    /// </summary>
    /// <param name="request">The handshake request containing configuration.</param>
    /// <returns>The handshake response with plugin capabilities.</returns>
    public override Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Load configuration from handshake
        if (request.Config != null)
        {
            if (request.Config.TryGetValue("port", out var port))
            {
                _config.Port = port switch
                {
                    int p => p,
                    long l => (int)l,
                    string s when int.TryParse(s, out var parsed) => parsed,
                    _ => _config.Port
                };
            }

            if (request.Config.TryGetValue("maxConnections", out var maxConn))
            {
                _config.MaxConnections = maxConn switch
                {
                    int mc => mc,
                    long l => (int)l,
                    string s when int.TryParse(s, out var parsed) => parsed,
                    _ => _config.MaxConnections
                };
            }

            if (request.Config.TryGetValue("authenticationEnabled", out var authEnabled))
            {
                _config.AuthenticationEnabled = authEnabled switch
                {
                    bool b => b,
                    string s => bool.TryParse(s, out var parsed) && parsed,
                    _ => _config.AuthenticationEnabled
                };
            }

            if (request.Config.TryGetValue("defaultDatabase", out var defaultDb) && defaultDb is string db)
            {
                _config.DefaultDatabase = db;
            }

            if (request.Config.TryGetValue("defaultSchema", out var defaultSchema) && defaultSchema is string schema)
            {
                _config.DefaultSchema = schema;
            }

            if (request.Config.TryGetValue("connectionTimeoutSeconds", out var connTimeout))
            {
                _config.ConnectionTimeoutSeconds = connTimeout switch
                {
                    int ct => ct,
                    long l => (int)l,
                    string s when int.TryParse(s, out var parsed) => parsed,
                    _ => _config.ConnectionTimeoutSeconds
                };
            }

            if (request.Config.TryGetValue("queryTimeoutSeconds", out var queryTimeout))
            {
                _config.QueryTimeoutSeconds = queryTimeout switch
                {
                    int qt => qt,
                    long l => (int)l,
                    string s when int.TryParse(s, out var parsed) => parsed,
                    _ => _config.QueryTimeoutSeconds
                };
            }

            if (request.Config.TryGetValue("maxBatchSize", out var maxBatch))
            {
                _config.MaxBatchSize = maxBatch switch
                {
                    int mb => mb,
                    long l => (int)l,
                    string s when int.TryParse(s, out var parsed) => parsed,
                    _ => _config.MaxBatchSize
                };
            }

            if (request.Config.TryGetValue("sslEnabled", out var sslEnabled))
            {
                _config.SslEnabled = sslEnabled switch
                {
                    bool b => b,
                    string s => bool.TryParse(s, out var parsed) && parsed,
                    _ => _config.SslEnabled
                };
            }

            if (request.Config.TryGetValue("serverVersion", out var serverVersion) && serverVersion is string sv)
            {
                _config.ServerVersion = sv;
            }

            // Get SQL executor callback (injected by kernel)
            if (request.Config.TryGetValue("sqlExecutor", out var executor) &&
                executor is Func<string, CancellationToken, Task<JdbcQueryResult>> sqlExec)
            {
                _sqlExecutor = sqlExec;
            }
        }

        return Task.FromResult(new HandshakeResponse
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
    /// Starts the JDBC bridge server.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">Thrown when the server is already running.</exception>
    public override async Task StartAsync(CancellationToken ct)
    {
        lock (_listenerLock)
        {
            if (_isRunning)
            {
                throw new InvalidOperationException("JDBC Bridge server is already running");
            }

            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        }

        try
        {
            _listener = new TcpListener(IPAddress.Any, _config.Port);
            _listener.Start(_config.MaxConnections);

            _isRunning = true;

            Console.WriteLine($"[JdbcBridge] Listening on port {_config.Port}");
            Console.WriteLine($"[JdbcBridge] JDBC URL: jdbc:datawarehouse://localhost:{_config.Port}/{_config.DefaultDatabase}");

            _acceptTask = AcceptConnectionsAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[JdbcBridge] Failed to start: {ex.Message}");
            _isRunning = false;
            throw;
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Stops the JDBC bridge server.
    /// </summary>
    public override async Task StopAsync()
    {
        Console.WriteLine("[JdbcBridge] Stopping...");

        lock (_listenerLock)
        {
            _isRunning = false;
            _cts?.Cancel();
            _listener?.Stop();
        }

        // Close all active connections
        foreach (var handler in _connections.Values)
        {
            try
            {
                handler.Dispose();
            }
            catch
            {
                // Best effort cleanup
            }
        }

        _connections.Clear();

        if (_acceptTask != null)
        {
            try
            {
                await _acceptTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[JdbcBridge] Error during shutdown: {ex.Message}");
            }
        }

        Console.WriteLine("[JdbcBridge] Stopped");
    }

    /// <summary>
    /// Handles plugin messages.
    /// </summary>
    /// <param name="message">The message to handle.</param>
    /// <returns>A task representing the operation.</returns>
    public override Task OnMessageAsync(PluginMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (message.Payload == null)
        {
            return Task.CompletedTask;
        }

        var response = message.Type switch
        {
            "jdbc.start" => HandleStart(),
            "jdbc.stop" => HandleStop(),
            "jdbc.status" => HandleStatus(),
            "jdbc.connections" => HandleConnections(),
            "jdbc.disconnect" => HandleDisconnect(message.Payload),
            _ => new Dictionary<string, object> { ["error"] = $"Unknown command: {message.Type}" }
        };

        if (response != null)
        {
            message.Payload["_response"] = response;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Sets the SQL executor callback.
    /// </summary>
    /// <param name="executor">The SQL execution delegate.</param>
    /// <exception cref="ArgumentNullException">Thrown when executor is null.</exception>
    public void SetSqlExecutor(Func<string, CancellationToken, Task<JdbcQueryResult>> executor)
    {
        _sqlExecutor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    /// <summary>
    /// Gets the plugin capabilities.
    /// </summary>
    /// <returns>List of capability descriptors.</returns>
    protected override List<PluginCapabilityDescriptor> GetCapabilities()
    {
        return new List<PluginCapabilityDescriptor>
        {
            new()
            {
                Name = "jdbc_wire_protocol",
                DisplayName = "JDBC Wire Protocol",
                Description = "Binary wire protocol for JDBC Type 4 driver connectivity"
            },
            new()
            {
                Name = "connection_management",
                DisplayName = "Connection Management",
                Description = "Connection pooling and lifecycle management"
            },
            new()
            {
                Name = "prepared_statements",
                DisplayName = "Prepared Statements",
                Description = "PreparedStatement support with parameter binding"
            },
            new()
            {
                Name = "batch_operations",
                DisplayName = "Batch Operations",
                Description = "Batch inserts, updates, and deletes"
            },
            new()
            {
                Name = "transactions",
                DisplayName = "Transactions",
                Description = "Transaction support with commit, rollback, and savepoints"
            },
            new()
            {
                Name = "database_metadata",
                DisplayName = "Database Metadata",
                Description = "DatabaseMetaData retrieval for schema introspection"
            },
            new()
            {
                Name = "result_set_metadata",
                DisplayName = "ResultSet Metadata",
                Description = "ResultSetMetaData for column information"
            },
            new()
            {
                Name = "auto_commit",
                DisplayName = "Auto-Commit Control",
                Description = "Auto-commit mode configuration"
            },
            new()
            {
                Name = "savepoints",
                DisplayName = "Savepoints",
                Description = "Named and unnamed savepoint support"
            },
            new()
            {
                Name = "sql_type_mapping",
                DisplayName = "SQL Type Mapping",
                Description = "Full java.sql.Types mapping support"
            }
        };
    }

    /// <summary>
    /// Gets the plugin metadata.
    /// </summary>
    /// <returns>Metadata dictionary.</returns>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["WireProtocol"] = "JDBC Binary v1.0";
        metadata["Port"] = _config.Port;
        metadata["MaxConnections"] = _config.MaxConnections;
        metadata["DefaultDatabase"] = _config.DefaultDatabase;
        metadata["DefaultSchema"] = _config.DefaultSchema;
        metadata["ConnectionTimeout"] = _config.ConnectionTimeoutSeconds;
        metadata["QueryTimeout"] = _config.QueryTimeoutSeconds;
        metadata["MaxBatchSize"] = _config.MaxBatchSize;
        metadata["SslEnabled"] = _config.SslEnabled;
        metadata["AuthenticationEnabled"] = _config.AuthenticationEnabled;
        metadata["ActiveConnections"] = _connections.Count;
        metadata["IsRunning"] = _isRunning;
        metadata["JdbcUrl"] = $"jdbc:datawarehouse://localhost:{_config.Port}/{_config.DefaultDatabase}";
        metadata["SupportedJdbcVersion"] = "4.2";
        return metadata;
    }

    /// <summary>
    /// Accepts incoming connections.
    /// </summary>
    private async Task AcceptConnectionsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _isRunning)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false);

                // Check connection limit
                if (_connections.Count >= _config.MaxConnections)
                {
                    Console.WriteLine("[JdbcBridge] Connection limit reached, rejecting connection");
                    client.Close();
                    continue;
                }

                // Handle connection in background
                _ = HandleConnectionAsync(client, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException) when (!_isRunning)
            {
                // Listener stopped
                break;
            }
            catch (Exception ex)
            {
                if (_isRunning && !ct.IsCancellationRequested)
                {
                    Console.Error.WriteLine($"[JdbcBridge] Accept error: {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// Handles a single client connection.
    /// </summary>
    private async Task HandleConnectionAsync(TcpClient client, CancellationToken ct)
    {
        var connectionId = Guid.NewGuid().ToString("N");
        ConnectionHandler? handler = null;

        try
        {
            var queryProcessor = new QueryProcessor(_sqlExecutor ?? DefaultSqlExecutor);
            var metadataProvider = new MetadataProvider(_config, _sqlExecutor);

            handler = new ConnectionHandler(client, _config, queryProcessor, metadataProvider);
            _connections[connectionId] = handler;

            Console.WriteLine($"[JdbcBridge] New connection: {connectionId}");

            await handler.HandleAsync(ct).ConfigureAwait(false);

            Console.WriteLine($"[JdbcBridge] Connection closed: {connectionId}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[JdbcBridge] Connection error ({connectionId}): {ex.Message}");
        }
        finally
        {
            _connections.TryRemove(connectionId, out _);
            handler?.Dispose();
        }
    }

    /// <summary>
    /// Default SQL executor when none is configured.
    /// </summary>
    private static Task<JdbcQueryResult> DefaultSqlExecutor(string sql, CancellationToken ct)
    {
        return Task.FromResult(new JdbcQueryResult
        {
            Columns = new List<JdbcColumnMetadata>
            {
                TypeConverter.CreateColumnMetadata("message", JdbcSqlTypes.VARCHAR, 0)
            },
            Rows = new List<object?[]>
            {
                new object?[] { "SQL executor not configured - connect DataWarehouse kernel" }
            }
        });
    }

    // Message handlers

    private Dictionary<string, object> HandleStart()
    {
        return new Dictionary<string, object>
        {
            ["success"] = _isRunning,
            ["message"] = _isRunning ? "Already running" : "Not started"
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
            ["defaultDatabase"] = _config.DefaultDatabase,
            ["defaultSchema"] = _config.DefaultSchema,
            ["authenticationEnabled"] = _config.AuthenticationEnabled,
            ["sslEnabled"] = _config.SslEnabled,
            ["isRunning"] = _isRunning,
            ["jdbcUrl"] = $"jdbc:datawarehouse://localhost:{_config.Port}/{_config.DefaultDatabase}"
        };
    }

    private Dictionary<string, object> HandleConnections()
    {
        var connections = _connections.Values.Select(handler => new Dictionary<string, object>
        {
            ["connectionId"] = handler.State.ConnectionId,
            ["user"] = handler.State.User,
            ["catalog"] = handler.State.Catalog,
            ["schema"] = handler.State.Schema,
            ["applicationName"] = handler.State.ApplicationName,
            ["connectedAt"] = handler.State.ConnectedAt.ToString("O"),
            ["autoCommit"] = handler.State.AutoCommit,
            ["inTransaction"] = handler.State.InTransaction,
            ["preparedStatements"] = handler.State.PreparedStatements.Count,
            ["savepoints"] = handler.State.Savepoints.Count
        }).ToList();

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["connections"] = connections,
            ["count"] = connections.Count
        };
    }

    private Dictionary<string, object> HandleDisconnect(Dictionary<string, object> payload)
    {
        if (payload.TryGetValue("connectionId", out var connIdObj) && connIdObj is string connectionId)
        {
            if (_connections.TryRemove(connectionId, out var handler))
            {
                handler.Dispose();
                return new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["message"] = $"Connection {connectionId} disconnected"
                };
            }

            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = $"Connection {connectionId} not found"
            };
        }

        return new Dictionary<string, object>
        {
            ["success"] = false,
            ["error"] = "connectionId required"
        };
    }
}
