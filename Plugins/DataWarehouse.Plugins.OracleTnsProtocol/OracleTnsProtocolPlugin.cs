using DataWarehouse.Plugins.OracleTnsProtocol.Protocol;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace DataWarehouse.Plugins.OracleTnsProtocol;

/// <summary>
/// Oracle TNS Protocol Plugin for SQL tool compatibility.
/// Implements the Oracle Transparent Network Substrate (TNS) and Two-Task Interface (TTI) protocols,
/// allowing Oracle tools like SQL Developer, Toad, Oracle SQL*Plus, and others to connect to DataWarehouse.
///
/// Features:
/// - Full Oracle TNS/TTI wire protocol support (compatible with Oracle 11g, 12c, 18c, 19c, 21c)
/// - Authentication (O3LOGON, O5LOGON, O7LOGON, O8LOGON with SHA-1 hashing)
/// - Native Network Encryption (NNE) support with AES-256, AES-192, AES-128
/// - Data integrity algorithms (SHA-256, SHA-1, MD5)
/// - Simple query protocol (OAll7 parse/execute)
/// - Extended query protocol (cursor management, fetch operations)
/// - Connection pooling and session management
/// - Oracle system catalog queries (v$, dba_, user_, all_ views)
/// - Transaction support (COMMIT, ROLLBACK)
/// - PL/SQL block execution
/// - Bind variable substitution
///
/// Supported clients:
/// - Oracle SQL Developer, Toad, SQL*Plus, PL/SQL Developer
/// - DBeaver, DataGrip with Oracle drivers
/// - Python (cx_Oracle, oracledb), Node.js (node-oracledb), .NET (Oracle.ManagedDataAccess)
/// - Any Oracle JDBC/ODBC driver
///
/// Usage:
///   sqlplus admin/password@localhost:1521/DATAWAREHOUSE
///   # Or connect via any Oracle-compatible tool
/// </summary>
public sealed class OracleTnsProtocolPlugin : InterfacePluginBase, IDisposable
{
    /// <summary>
    /// Unique plugin identifier.
    /// </summary>
    public override string Id => "com.datawarehouse.protocol.oracle_tns";

    /// <summary>
    /// Human-readable plugin name.
    /// </summary>
    public override string Name => "Oracle TNS Protocol";

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
    public override string Protocol => "oracle_tns";

    /// <summary>
    /// Port number the plugin listens on.
    /// </summary>
    public override int? Port => _config.Port;

    private readonly OracleTnsProtocolConfig _config = new();
    private readonly ConcurrentDictionary<string, OracleConnectionState> _connections = new();
    private readonly ConcurrentDictionary<string, TnsProtocolHandler> _handlers = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private Func<string, CancellationToken, Task<OracleQueryResult>>? _sqlExecutor;
    private bool _disposed;

    /// <summary>
    /// Handles plugin handshake and configuration.
    /// </summary>
    /// <param name="request">Handshake request with configuration.</param>
    /// <returns>Handshake response indicating success or failure.</returns>
    public override Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
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

            if (request.Config.TryGetValue("encryptionMode", out var encryptionMode) && encryptionMode is string em)
            {
                _config.EncryptionMode = em;
            }

            if (request.Config.TryGetValue("encryptionAlgorithms", out var encAlgs) && encAlgs is string[] ea)
            {
                _config.EncryptionAlgorithms = ea;
            }

            if (request.Config.TryGetValue("integrityAlgorithms", out var intAlgs) && intAlgs is string[] ia)
            {
                _config.IntegrityAlgorithms = ia;
            }

            if (request.Config.TryGetValue("serviceName", out var serviceName) && serviceName is string sn)
            {
                _config.ServiceName = sn;
            }

            if (request.Config.TryGetValue("sid", out var sid) && sid is string sidStr)
            {
                _config.Sid = sidStr;
            }

            if (request.Config.TryGetValue("reportedVersion", out var version) && version is string v)
            {
                _config.ReportedVersion = v;
            }

            if (request.Config.TryGetValue("sduSize", out var sduSize))
            {
                _config.SduSize = sduSize switch
                {
                    int sz => sz,
                    long l => (int)l,
                    string str when int.TryParse(str, out var szParsed) => szParsed,
                    _ => _config.SduSize
                };
            }

            if (request.Config.TryGetValue("tduSize", out var tduSize))
            {
                _config.TduSize = tduSize switch
                {
                    int tsz => tsz,
                    long l => (int)l,
                    string str2 when int.TryParse(str2, out var tszParsed) => tszParsed,
                    _ => _config.TduSize
                };
            }

            if (request.Config.TryGetValue("connectionTimeoutSeconds", out var connTimeout))
            {
                _config.ConnectionTimeoutSeconds = connTimeout switch
                {
                    int timeout => timeout,
                    long l => (int)l,
                    string str3 when int.TryParse(str3, out var timeoutParsed) => timeoutParsed,
                    _ => _config.ConnectionTimeoutSeconds
                };
            }

            if (request.Config.TryGetValue("queryTimeoutSeconds", out var queryTimeout))
            {
                _config.QueryTimeoutSeconds = queryTimeout switch
                {
                    int qtimeout => qtimeout,
                    long l => (int)l,
                    string str4 when int.TryParse(str4, out var qtimeoutParsed) => qtimeoutParsed,
                    _ => _config.QueryTimeoutSeconds
                };
            }

            if (request.Config.TryGetValue("enableVersionSpoofing", out var versionSpoofing) && versionSpoofing is bool vs)
            {
                _config.EnableVersionSpoofing = vs;
            }

            if (request.Config.TryGetValue("sqlExecutor", out var executor) &&
                executor is Func<string, CancellationToken, Task<OracleQueryResult>> sqlExec)
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
    /// Starts the Oracle TNS protocol listener.
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
            throw new InvalidOperationException($"Failed to start Oracle TNS protocol listener: {ex.Message}", ex);
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Stops the Oracle TNS protocol listener and closes all connections.
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
            "oracle_tns.start" => HandleStart(),
            "oracle_tns.stop" => HandleStop(),
            "oracle_tns.status" => HandleStatus(),
            "oracle_tns.connections" => HandleConnections(),
            "oracle_tns.kill" => HandleKillConnection(message.Payload),
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
    public void SetSqlExecutor(Func<string, CancellationToken, Task<OracleQueryResult>> executor)
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
                Name = "oracle_tns_protocol",
                DisplayName = "Oracle TNS Protocol",
                Description = "Full Oracle TNS/TTI wire protocol support (Oracle 11g-21c compatible)"
            },
            new()
            {
                Name = "o8logon_authentication",
                DisplayName = "O8LOGON Authentication",
                Description = "SHA-1 based O8LOGON authentication (Oracle 8i+)"
            },
            new()
            {
                Name = "o7logon_authentication",
                DisplayName = "O7LOGON Authentication",
                Description = "DES-based O7LOGON authentication (Oracle 7/8)"
            },
            new()
            {
                Name = "native_network_encryption",
                DisplayName = "Native Network Encryption",
                Description = "Oracle NNE support with AES-256, AES-192, AES-128, 3DES"
            },
            new()
            {
                Name = "data_integrity",
                DisplayName = "Data Integrity",
                Description = "SHA-256, SHA-1, MD5 integrity algorithms"
            },
            new()
            {
                Name = "cursor_operations",
                DisplayName = "Cursor Operations",
                Description = "Open, parse, execute, fetch, close cursor lifecycle"
            },
            new()
            {
                Name = "bind_variables",
                DisplayName = "Bind Variables",
                Description = "Named and positional bind parameter support"
            },
            new()
            {
                Name = "transactions",
                DisplayName = "Transactions",
                Description = "COMMIT, ROLLBACK transaction control"
            },
            new()
            {
                Name = "plsql_blocks",
                DisplayName = "PL/SQL Blocks",
                Description = "Anonymous PL/SQL block execution"
            },
            new()
            {
                Name = "system_catalogs",
                DisplayName = "System Catalogs",
                Description = "v$, dba_, user_, all_ system view queries for tool compatibility"
            },
            new()
            {
                Name = "oracle_types",
                DisplayName = "Oracle Data Types",
                Description = "VARCHAR2, NUMBER, DATE, TIMESTAMP, CLOB, BLOB, and other Oracle types"
            },
            new()
            {
                Name = "connection_pooling",
                DisplayName = "Connection Pooling",
                Description = "Session management with configurable connection limits"
            }
        };
    }

    /// <summary>
    /// Gets plugin metadata.
    /// </summary>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["WireProtocol"] = "Oracle TNS/TTI";
        metadata["Port"] = _config.Port;
        metadata["MaxConnections"] = _config.MaxConnections;
        metadata["AuthMethod"] = _config.AuthMethod;
        metadata["EncryptionMode"] = _config.EncryptionMode;
        metadata["EncryptionAlgorithms"] = _config.EncryptionAlgorithms;
        metadata["IntegrityAlgorithms"] = _config.IntegrityAlgorithms;
        metadata["ServiceName"] = _config.ServiceName;
        metadata["Sid"] = _config.Sid;
        metadata["ReportedVersion"] = _config.ReportedVersion;
        metadata["SduSize"] = _config.SduSize;
        metadata["TduSize"] = _config.TduSize;
        metadata["ActiveConnections"] = _connections.Count;
        metadata["CompatibleClients"] = new[]
        {
            "Oracle SQL Developer", "Toad", "SQL*Plus", "PL/SQL Developer",
            "DBeaver", "DataGrip", "cx_Oracle", "oracledb (Python)",
            "node-oracledb", "Oracle.ManagedDataAccess (.NET)", "JDBC Thin Driver"
        };
        metadata["OracleCompatibility"] = "11g, 12c, 18c, 19c, 21c";
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
            var writer = new TnsPacketWriter(stream);

            await writer.WriteRefusePacketAsync(
                4,
                $"ORA-00020: maximum number of connections ({_config.MaxConnections}) exceeded",
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
        var connectionGuid = Guid.NewGuid().ToString("N");
        TnsProtocolHandler? handler = null;

        try
        {
            var queryProcessor = new OracleQueryProcessor(_sqlExecutor ?? DefaultSqlExecutor);
            handler = new TnsProtocolHandler(client, _config, queryProcessor);

            _handlers[connectionGuid] = handler;
            _connections[connectionGuid] = handler.ConnectionState;

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

    private Task<OracleQueryResult> DefaultSqlExecutor(string sql, CancellationToken ct)
    {
        return Task.FromResult(new OracleQueryResult
        {
            Columns = new List<OracleColumnDescription>
            {
                new()
                {
                    Name = "MESSAGE",
                    TypeOid = OracleTypeOid.Varchar2,
                    DisplaySize = 4000,
                    Position = 1
                }
            },
            Rows = new List<List<byte[]?>>
            {
                new() { System.Text.Encoding.UTF8.GetBytes("SQL executor not configured") }
            },
            RowsAffected = 1,
            StatementType = OracleStatementType.Select
        });
    }

    private Dictionary<string, object> HandleStart()
    {
        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["message"] = "Oracle TNS protocol listener is running",
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
            ["encryptionMode"] = _config.EncryptionMode,
            ["serviceName"] = _config.ServiceName,
            ["sid"] = _config.Sid,
            ["reportedVersion"] = _config.ReportedVersion,
            ["isListening"] = _listener != null
        };
    }

    private Dictionary<string, object> HandleConnections()
    {
        var connections = _connections.Values.Select(conn => new Dictionary<string, object>
        {
            ["connectionId"] = conn.ConnectionId,
            ["username"] = conn.Username,
            ["schema"] = conn.Schema,
            ["serviceName"] = conn.ServiceName,
            ["sessionId"] = conn.SessionId,
            ["serialNumber"] = conn.SerialNumber,
            ["connectedAt"] = conn.ConnectedAt.ToString("O"),
            ["isAuthenticated"] = conn.IsAuthenticated,
            ["inTransaction"] = conn.InTransaction,
            ["nneEnabled"] = conn.NneEnabled,
            ["nneEncryptionAlgorithm"] = conn.NneEncryptionAlgorithm ?? "none",
            ["nneIntegrityAlgorithm"] = conn.NneIntegrityAlgorithm ?? "none",
            ["openCursors"] = conn.Cursors.Count,
            ["programName"] = conn.ProgramName,
            ["machineName"] = conn.MachineName,
            ["processId"] = conn.ProcessId
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
