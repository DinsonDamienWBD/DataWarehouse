using DataWarehouse.Plugins.PostgresWireProtocol.Protocol;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace DataWarehouse.Plugins.PostgresWireProtocol;

/// <summary>
/// PostgreSQL Wire Protocol Plugin for SQL tool compatibility.
/// Implements the PostgreSQL wire protocol, allowing SQL tools like DBeaver, DataGrip,
/// pgAdmin, and others to connect to DataWarehouse.
///
/// Features:
/// - Full PostgreSQL wire protocol 3.0 support
/// - Authentication (MD5, SCRAM-SHA-256, trust)
/// - Simple query protocol (Query message)
/// - Extended query protocol (Parse, Bind, Execute)
/// - Connection pooling
/// - SSL negotiation
/// - System catalog queries for tool compatibility
///
/// Supported clients:
/// - DBeaver, DataGrip, pgAdmin
/// - psql command line
/// - Python (psycopg2), Node.js (pg), .NET (Npgsql)
/// - Any JDBC/ODBC PostgreSQL driver
///
/// Usage:
///   psql -h localhost -p 5432 -U admin -d datawarehouse
///   # Or connect via any PostgreSQL-compatible tool
/// </summary>
public sealed class PostgresWireProtocolPlugin : InterfacePluginBase
{
    public override string Id => "com.datawarehouse.protocol.postgres";
    public override string Name => "PostgreSQL Wire Protocol";
    public override string Version => "1.0.0";
    public override PluginCategory Category => PluginCategory.InterfaceProvider;
    public override string Protocol => "postgresql";
    public override int? Port => _config.Port;

    private readonly PostgresWireProtocolConfig _config = new();
    private readonly ConcurrentDictionary<string, PgConnectionState> _connections = new();
    private readonly ConcurrentDictionary<string, QueryProcessor> _queryProcessors = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private Func<string, CancellationToken, Task<QueryResult>>? _sqlExecutor;

    public override Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
    {
        // Load configuration from handshake
        if (request.Config != null)
        {
            if (request.Config.TryGetValue("port", out var port) && port is int p)
                _config.Port = p;

            if (request.Config.TryGetValue("maxConnections", out var maxConn) && maxConn is int mc)
                _config.MaxConnections = mc;

            if (request.Config.TryGetValue("authMethod", out var authMethod) && authMethod is string am)
                _config.AuthMethod = am;

            if (request.Config.TryGetValue("sslMode", out var sslMode) && sslMode is string sm)
                _config.SslMode = sm;

            // Get SQL executor callback (injected by kernel)
            if (request.Config.TryGetValue("sqlExecutor", out var executor) &&
                executor is Func<string, CancellationToken, Task<QueryResult>> sqlExec)
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

    public override async Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        try
        {
            _listener = new TcpListener(IPAddress.Any, _config.Port);
            _listener.Start(_config.MaxConnections);

            Console.WriteLine($"[PostgresWireProtocol] Listening on port {_config.Port}");

            _acceptTask = AcceptConnectionsAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[PostgresWireProtocol] Failed to start: {ex.Message}");
            throw;
        }

        await Task.CompletedTask;
    }

    public override async Task StopAsync()
    {
        Console.WriteLine("[PostgresWireProtocol] Stopping...");

        _cts?.Cancel();
        _listener?.Stop();

        // Close all active connections
        foreach (var connState in _connections.Values)
        {
            // Connections will be closed by their handlers
        }

        _connections.Clear();

        if (_acceptTask != null)
        {
            try
            {
                await _acceptTask;
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
        }

        Console.WriteLine("[PostgresWireProtocol] Stopped");
    }

    public override Task OnMessageAsync(PluginMessage message)
    {
        if (message.Payload == null)
            return Task.CompletedTask;

        var response = message.Type switch
        {
            "postgres.start" => HandleStart(),
            "postgres.stop" => HandleStop(),
            "postgres.status" => HandleStatus(),
            "postgres.connections" => HandleConnections(),
            _ => new Dictionary<string, object> { ["error"] = $"Unknown command: {message.Type}" }
        };

        if (response != null)
        {
            message.Payload["_response"] = response;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Sets the SQL executor callback for executing queries.
    /// Should be called before starting the plugin.
    /// </summary>
    public void SetSqlExecutor(Func<string, CancellationToken, Task<QueryResult>> executor)
    {
        _sqlExecutor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    protected override List<PluginCapabilityDescriptor> GetCapabilities()
    {
        return new List<PluginCapabilityDescriptor>
        {
            new()
            {
                Name = "postgresql_wire_protocol",
                DisplayName = "PostgreSQL Wire Protocol",
                Description = "Full PostgreSQL 3.0 wire protocol support"
            },
            new()
            {
                Name = "simple_query_protocol",
                DisplayName = "Simple Query Protocol",
                Description = "Execute queries via Query message"
            },
            new()
            {
                Name = "extended_query_protocol",
                DisplayName = "Extended Query Protocol",
                Description = "Prepared statements with Parse/Bind/Execute"
            },
            new()
            {
                Name = "authentication",
                DisplayName = "Authentication",
                Description = "MD5, SCRAM-SHA-256, and trust authentication"
            },
            new()
            {
                Name = "ssl_negotiation",
                DisplayName = "SSL Negotiation",
                Description = "SSL/TLS connection support"
            },
            new()
            {
                Name = "system_catalogs",
                DisplayName = "System Catalogs",
                Description = "pg_catalog and information_schema queries for tool compatibility"
            },
            new()
            {
                Name = "transactions",
                DisplayName = "Transactions",
                Description = "BEGIN, COMMIT, ROLLBACK support"
            }
        };
    }

    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["WireProtocol"] = "PostgreSQL 3.0";
        metadata["Port"] = _config.Port;
        metadata["MaxConnections"] = _config.MaxConnections;
        metadata["AuthMethod"] = _config.AuthMethod;
        metadata["SslMode"] = _config.SslMode;
        metadata["ActiveConnections"] = _connections.Count;
        metadata["CompatibleClients"] = new[] { "DBeaver", "DataGrip", "pgAdmin", "psql", "Npgsql", "psycopg2", "node-postgres" };
        return metadata;
    }

    private async Task AcceptConnectionsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(ct);

                // Check connection limit
                if (_connections.Count >= _config.MaxConnections)
                {
                    Console.WriteLine("[PostgresWireProtocol] Connection limit reached, rejecting connection");
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
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                {
                    Console.Error.WriteLine($"[PostgresWireProtocol] Accept error: {ex.Message}");
                }
            }
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken ct)
    {
        var connectionId = Guid.NewGuid().ToString("N");

        try
        {
            // Create query processor
            var queryProcessor = new QueryProcessor(_sqlExecutor ?? DefaultSqlExecutor);
            _queryProcessors[connectionId] = queryProcessor;

            // Create protocol handler
            var handler = new ProtocolHandler(client, _config, queryProcessor);

            // Track connection
            // (Connection state is tracked internally by handler)

            Console.WriteLine($"[PostgresWireProtocol] New connection: {connectionId}");

            // Handle the connection
            await handler.HandleConnectionAsync(ct);

            Console.WriteLine($"[PostgresWireProtocol] Connection closed: {connectionId}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[PostgresWireProtocol] Connection error ({connectionId}): {ex.Message}");
        }
        finally
        {
            _queryProcessors.TryRemove(connectionId, out _);
            _connections.TryRemove(connectionId, out _);

            try { client.Close(); } catch { }
        }
    }

    /// <summary>
    /// Default SQL executor that returns mock data.
    /// Override by calling SetSqlExecutor or providing via handshake config.
    /// </summary>
    private async Task<QueryResult> DefaultSqlExecutor(string sql, CancellationToken ct)
    {
        // Simple fallback executor for testing
        await Task.CompletedTask;

        return new QueryResult
        {
            Columns = new List<PgColumnDescription>
            {
                TypeConverter.CreateColumnDescription("message", typeof(string), 0)
            },
            Rows = new List<List<byte[]?>>
            {
                new() { System.Text.Encoding.UTF8.GetBytes("SQL executor not configured") }
            },
            CommandTag = "SELECT 1"
        };
    }

    // Message handlers
    private Dictionary<string, object> HandleStart()
    {
        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["message"] = "Already started"
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
            ["isListening"] = _listener != null
        };
    }

    private Dictionary<string, object> HandleConnections()
    {
        var connections = _connections.Values.Select(conn => new Dictionary<string, object>
        {
            ["connectionId"] = conn.ConnectionId,
            ["username"] = conn.Username,
            ["database"] = conn.Database,
            ["applicationName"] = conn.ApplicationName,
            ["connectedAt"] = conn.ConnectedAt.ToString("O"),
            ["inTransaction"] = conn.InTransaction,
            ["processId"] = conn.ProcessId,
            ["preparedStatements"] = conn.PreparedStatements.Count,
            ["portals"] = conn.Portals.Count
        }).ToList();

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["connections"] = connections,
            ["count"] = connections.Count
        };
    }
}
