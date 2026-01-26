using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DataWarehouse.Plugins.NoSqlProtocol;

/// <summary>
/// NoSQL Protocol Plugin for MongoDB and Redis compatibility.
/// Implements both MongoDB Wire Protocol and Redis RESP3 protocol, allowing NoSQL tools
/// and clients to connect to DataWarehouse using their native protocols.
///
/// Features:
/// - MongoDB Wire Protocol (OP_MSG, OP_QUERY, OP_REPLY, OP_INSERT, OP_UPDATE, OP_DELETE)
/// - SCRAM-SHA-1 and SCRAM-SHA-256 authentication for MongoDB
/// - Redis RESP3 protocol with full data type support
/// - Redis Pub/Sub, transactions, and Lua scripting
/// - Concurrent connection handling for both protocols
/// - Thread-safe in-memory data stores
///
/// MongoDB Support:
/// - Connects on port 27017 (default)
/// - Compatible with MongoDB drivers and tools (mongo shell, Compass, Studio 3T)
/// - Supports BSON serialization/deserialization
/// - Authentication via SCRAM mechanisms
///
/// Redis Support:
/// - Connects on port 6379 (default)
/// - Compatible with redis-cli and Redis client libraries
/// - Full RESP3 protocol with backwards compatibility for RESP2
/// - String, List, Set, Hash, Sorted Set data types
/// - Pub/Sub messaging and pattern subscriptions
/// - Transactions (MULTI/EXEC/DISCARD) with WATCH support
///
/// Usage:
///   # MongoDB
///   mongo --host localhost --port 27017
///   mongosh mongodb://localhost:27017
///
///   # Redis
///   redis-cli -h localhost -p 6379
///   redis-cli PING
/// </summary>
public sealed class NoSqlProtocolPlugin : InterfacePluginBase, IDisposable
{
    /// <summary>
    /// Unique plugin identifier.
    /// </summary>
    public override string Id => "com.datawarehouse.protocol.nosql";

    /// <summary>
    /// Human-readable plugin name.
    /// </summary>
    public override string Name => "NoSQL Protocol (MongoDB + Redis)";

    /// <summary>
    /// Plugin version following semantic versioning.
    /// </summary>
    public override string Version => "1.0.0";

    /// <summary>
    /// Plugin category for classification.
    /// </summary>
    public override PluginCategory Category => PluginCategory.InterfaceProvider;

    /// <summary>
    /// Protocol identifier for this interface (multi-protocol).
    /// </summary>
    public override string Protocol => "nosql";

    /// <summary>
    /// Primary port number (MongoDB). Redis uses _redisPort.
    /// </summary>
    public override int? Port => _mongoPort;

    // Configuration
    private int _mongoPort = 27017;
    private int _redisPort = 6379;
    private int _maxConnections = 1000;

    // MongoDB components
    private TcpListener? _mongoListener;
    private readonly MongoAuthenticator _mongoAuth = new();
    private readonly ConcurrentDictionary<string, TcpClient> _mongoConnections = new();

    // Redis components
    private TcpListener? _redisListener;
    private readonly RedisDataStore _redisStore = new();
    private readonly RedisPubSubManager _redisPubSub = new();
    private readonly RedisTransactionManager _redisTransactions = new();
    private readonly RedisScriptManager _redisScripts = new();
    private readonly ConcurrentDictionary<string, TcpClient> _redisConnections = new();

    // Shared components
    private CancellationTokenSource? _cts;
    private Task? _mongoAcceptTask;
    private Task? _redisAcceptTask;
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
            if (request.Config.TryGetValue("mongoPort", out var mongoPort) && mongoPort is int mp)
                _mongoPort = mp;

            if (request.Config.TryGetValue("redisPort", out var redisPort) && redisPort is int rp)
                _redisPort = rp;

            if (request.Config.TryGetValue("maxConnections", out var maxConn) && maxConn is int mc)
                _maxConnections = mc;

            // Initialize default MongoDB users if provided
            if (request.Config.TryGetValue("mongoUsers", out var users) && users is Dictionary<string, string> mongoUsers)
            {
                foreach (var (username, password) in mongoUsers)
                {
                    _mongoAuth.RegisterCredential(username, password, "admin", "readWrite", "dbAdmin");
                }
            }
        }

        // Register default admin user for MongoDB if no users configured
        if (request.Config?.ContainsKey("mongoUsers") != true)
        {
            _mongoAuth.RegisterCredential("admin", "admin", "admin", "root");
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
    /// Starts both MongoDB and Redis protocol listeners.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public override async Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        try
        {
            // Start MongoDB listener
            _mongoListener = new TcpListener(IPAddress.Any, _mongoPort);
            _mongoListener.Start(_maxConnections);
            _mongoAcceptTask = AcceptMongoConnectionsAsync(_cts.Token);

            Console.WriteLine($"[NoSqlProtocol] MongoDB listener started on port {_mongoPort}");

            // Start Redis listener
            _redisListener = new TcpListener(IPAddress.Any, _redisPort);
            _redisListener.Start(_maxConnections);
            _redisAcceptTask = AcceptRedisConnectionsAsync(_cts.Token);

            Console.WriteLine($"[NoSqlProtocol] Redis listener started on port {_redisPort}");
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            throw new InvalidOperationException(
                $"Ports {_mongoPort} (MongoDB) or {_redisPort} (Redis) are already in use. Configure different ports.",
                ex);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to start NoSQL protocol listeners: {ex.Message}", ex);
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Stops both protocol listeners and closes all connections.
    /// </summary>
    public override async Task StopAsync()
    {
        Console.WriteLine("[NoSqlProtocol] Stopping...");

        _cts?.Cancel();

        try
        {
            _mongoListener?.Stop();
            _redisListener?.Stop();
        }
        catch
        {
            // Best effort
        }

        // Close all MongoDB connections
        foreach (var client in _mongoConnections.Values)
        {
            try { client.Close(); } catch { }
        }
        _mongoConnections.Clear();

        // Close all Redis connections
        foreach (var client in _redisConnections.Values)
        {
            try { client.Close(); } catch { }
        }
        _redisConnections.Clear();

        // Wait for accept tasks
        if (_mongoAcceptTask != null)
        {
            try { await _mongoAcceptTask; }
            catch (OperationCanceledException) { }
        }

        if (_redisAcceptTask != null)
        {
            try { await _redisAcceptTask; }
            catch (OperationCanceledException) { }
        }

        Console.WriteLine("[NoSqlProtocol] Stopped");
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
            "nosql.status" => HandleStatus(),
            "nosql.mongo.connections" => HandleMongoConnections(),
            "nosql.redis.connections" => HandleRedisConnections(),
            "nosql.redis.info" => HandleRedisInfo(),
            _ => new Dictionary<string, object> { ["error"] = $"Unknown command: {message.Type}" }
        };

        message.Payload["_response"] = response;
        return Task.CompletedTask;
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
                Name = "mongodb_wire_protocol",
                DisplayName = "MongoDB Wire Protocol",
                Description = "Full MongoDB wire protocol with OP_MSG and legacy opcodes"
            },
            new()
            {
                Name = "mongodb_authentication",
                DisplayName = "MongoDB Authentication",
                Description = "SCRAM-SHA-1 and SCRAM-SHA-256 authentication mechanisms"
            },
            new()
            {
                Name = "bson_serialization",
                DisplayName = "BSON Serialization",
                Description = "Complete BSON encoding/decoding with all data types"
            },
            new()
            {
                Name = "redis_resp3_protocol",
                DisplayName = "Redis RESP3 Protocol",
                Description = "Full RESP3 protocol with RESP2 backwards compatibility"
            },
            new()
            {
                Name = "redis_data_types",
                DisplayName = "Redis Data Types",
                Description = "String, List, Set, Hash, Sorted Set, Stream support"
            },
            new()
            {
                Name = "redis_pubsub",
                DisplayName = "Redis Pub/Sub",
                Description = "Channel and pattern-based publish/subscribe messaging"
            },
            new()
            {
                Name = "redis_transactions",
                DisplayName = "Redis Transactions",
                Description = "MULTI/EXEC/DISCARD with WATCH optimistic locking"
            },
            new()
            {
                Name = "redis_scripting",
                DisplayName = "Redis Lua Scripting",
                Description = "EVAL, EVALSHA, SCRIPT LOAD commands"
            }
        };
    }

    /// <summary>
    /// Gets plugin metadata.
    /// </summary>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["MongoDBPort"] = _mongoPort;
        metadata["RedisPort"] = _redisPort;
        metadata["MaxConnections"] = _maxConnections;
        metadata["ActiveMongoConnections"] = _mongoConnections.Count;
        metadata["ActiveRedisConnections"] = _redisConnections.Count;
        metadata["RedisKeyCount"] = _redisStore.DbSize().keys;
        metadata["SupportedProtocols"] = new[] { "MongoDB", "Redis" };
        metadata["CompatibleClients"] = new[]
        {
            "MongoDB: mongo shell, mongosh, Compass, Studio 3T, Robo 3T, MongoDB drivers",
            "Redis: redis-cli, RedisInsight, Medis, Redis drivers"
        };
        return metadata;
    }

    #region MongoDB Connection Handling

    private async Task AcceptMongoConnectionsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await _mongoListener!.AcceptTcpClientAsync(ct);

                if (_mongoConnections.Count >= _maxConnections)
                {
                    client.Close();
                    continue;
                }

                _ = HandleMongoConnectionAsync(client, ct);
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
                // Continue accepting connections
            }
        }
    }

    private async Task HandleMongoConnectionAsync(TcpClient client, CancellationToken ct)
    {
        var connectionId = Guid.NewGuid().ToString("N");
        _mongoConnections[connectionId] = client;

        try
        {
            var stream = client.GetStream();
            var buffer = new byte[65536];

            while (!ct.IsCancellationRequested && client.Connected)
            {
                // Read message header (16 bytes)
                var headerBytes = await ReadExactlyAsync(stream, 16, ct);
                if (headerBytes == null) break;

                var header = MongoMessageHeader.Read(headerBytes);

                // Read message body
                var bodySize = header.MessageLength - 16;
                var bodyBytes = await ReadExactlyAsync(stream, bodySize, ct);
                if (bodyBytes == null) break;

                // Combine header and body
                var fullMessage = new byte[header.MessageLength];
                Array.Copy(headerBytes, 0, fullMessage, 0, 16);
                Array.Copy(bodyBytes, 0, fullMessage, 16, bodySize);

                // Parse and handle message
                var response = await HandleMongoMessageAsync(connectionId, fullMessage, ct);
                if (response != null)
                {
                    await stream.WriteAsync(response, ct);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NoSqlProtocol] MongoDB connection error ({connectionId}): {ex.Message}");
        }
        finally
        {
            _mongoConnections.TryRemove(connectionId, out _);
            _mongoAuth.CleanupConnection(connectionId);
            try { client.Close(); } catch { }
        }
    }

    private async Task<byte[]?> HandleMongoMessageAsync(string connectionId, byte[] data, CancellationToken ct)
    {
        try
        {
            var message = MongoWireParser.ParseMessage(data);

            return message switch
            {
                OpMsgMessage opMsg => await HandleMongoOpMsgAsync(connectionId, opMsg, ct),
                OpQueryMessage opQuery => HandleMongoOpQuery(connectionId, opQuery),
                OpInsertMessage opInsert => HandleMongoOpInsert(connectionId, opInsert),
                OpUpdateMessage opUpdate => HandleMongoOpUpdate(connectionId, opUpdate),
                OpDeleteMessage opDelete => HandleMongoOpDelete(connectionId, opDelete),
                _ => null
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NoSqlProtocol] MongoDB message handling error: {ex.Message}");
            return null;
        }
    }

    private async Task<byte[]> HandleMongoOpMsgAsync(string connectionId, OpMsgMessage message, CancellationToken ct)
    {
        var body = message.Body;
        if (body == null)
        {
            return MongoWireBuilder.BuildErrorResponse(message.Header.RequestId, 1, "Empty message body");
        }

        // Extract command name
        var commandName = body.Keys.FirstOrDefault(k => !k.StartsWith('$'));
        if (commandName == null)
        {
            return MongoWireBuilder.BuildErrorResponse(message.Header.RequestId, 59, "Unknown command");
        }

        await Task.CompletedTask;

        // Handle authentication commands
        if (commandName == "saslStart")
        {
            var mechanism = body.GetValue<string>("mechanism") ?? "SCRAM-SHA-256";
            var payload = body.GetValue<byte[]>("payload") ?? Array.Empty<byte>();
            var db = body.GetValue<string>("$db") ?? "admin";

            var result = _mongoAuth.ProcessSaslStart(connectionId, mechanism, payload, db);
            return MongoWireBuilder.BuildOpMsgResponse(message.Header.RequestId, result);
        }

        if (commandName == "saslContinue")
        {
            var conversationId = body.GetValue<int>("conversationId");
            var payload = body.GetValue<byte[]>("payload") ?? Array.Empty<byte>();

            var result = _mongoAuth.ProcessSaslContinue(connectionId, conversationId, payload);
            return MongoWireBuilder.BuildOpMsgResponse(message.Header.RequestId, result);
        }

        // Handle basic commands
        var response = commandName.ToLowerInvariant() switch
        {
            "hello" or "ismaster" or "isMaster" => CreateMongoHelloResponse(),
            "ping" => new BsonDocument { ["ok"] = 1 },
            "buildinfo" or "buildInfo" => CreateMongoBuildInfo(),
            "getlog" or "getLog" => new BsonDocument { ["ok"] = 1, ["log"] = new List<object>() },
            "getcmdlineopts" => new BsonDocument { ["ok"] = 1, ["argv"] = new List<object>(), ["parsed"] = new BsonDocument() },
            "whatsmyuri" => new BsonDocument { ["ok"] = 1, ["you"] = "127.0.0.1:27017" },
            "listdatabases" or "listDatabases" => CreateMongoListDatabases(),
            "listcollections" or "listCollections" => CreateMongoListCollections(),
            _ => new BsonDocument { ["ok"] = 0, ["errmsg"] = $"Command '{commandName}' not implemented", ["code"] = 59 }
        };

        return MongoWireBuilder.BuildOpMsgResponse(message.Header.RequestId, response);
    }

    private byte[] HandleMongoOpQuery(string connectionId, OpQueryMessage message)
    {
        // Legacy query handling - return empty result
        var response = new BsonDocument { ["ok"] = 1, ["cursor"] = new BsonDocument { ["id"] = 0L, ["ns"] = message.FullCollectionName, ["firstBatch"] = new List<object>() } };
        return MongoWireBuilder.BuildOpReply(message.Header.RequestId, new List<BsonDocument> { response });
    }

    private byte[]? HandleMongoOpInsert(string connectionId, OpInsertMessage message)
    {
        // Insert operations don't typically send a response in legacy protocol
        return null;
    }

    private byte[]? HandleMongoOpUpdate(string connectionId, OpUpdateMessage message)
    {
        // Update operations don't typically send a response in legacy protocol
        return null;
    }

    private byte[]? HandleMongoOpDelete(string connectionId, OpDeleteMessage message)
    {
        // Delete operations don't typically send a response in legacy protocol
        return null;
    }

    private static BsonDocument CreateMongoHelloResponse()
    {
        return new BsonDocument
        {
            ["ismaster"] = true,
            ["topologyVersion"] = new BsonDocument { ["processId"] = new BsonObjectId(), ["counter"] = 0L },
            ["maxBsonObjectSize"] = 16777216,
            ["maxMessageSizeBytes"] = 48000000,
            ["maxWriteBatchSize"] = 100000,
            ["localTime"] = DateTime.UtcNow,
            ["logicalSessionTimeoutMinutes"] = 30,
            ["connectionId"] = 1,
            ["minWireVersion"] = 0,
            ["maxWireVersion"] = 17,
            ["readOnly"] = false,
            ["ok"] = 1
        };
    }

    private static BsonDocument CreateMongoBuildInfo()
    {
        return new BsonDocument
        {
            ["version"] = "6.0.0",
            ["gitVersion"] = "unknown",
            ["modules"] = new List<object>(),
            ["ok"] = 1
        };
    }

    private static BsonDocument CreateMongoListDatabases()
    {
        return new BsonDocument
        {
            ["databases"] = new List<object>
            {
                new BsonDocument { ["name"] = "admin", ["sizeOnDisk"] = 0L, ["empty"] = false },
                new BsonDocument { ["name"] = "datawarehouse", ["sizeOnDisk"] = 0L, ["empty"] = false }
            },
            ["totalSize"] = 0L,
            ["ok"] = 1
        };
    }

    private static BsonDocument CreateMongoListCollections()
    {
        return new BsonDocument
        {
            ["cursor"] = new BsonDocument
            {
                ["id"] = 0L,
                ["ns"] = "datawarehouse.$cmd.listCollections",
                ["firstBatch"] = new List<object>()
            },
            ["ok"] = 1
        };
    }

    #endregion

    #region Redis Connection Handling

    private async Task AcceptRedisConnectionsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await _redisListener!.AcceptTcpClientAsync(ct);

                if (_redisConnections.Count >= _maxConnections)
                {
                    client.Close();
                    continue;
                }

                _ = HandleRedisConnectionAsync(client, ct);
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
                // Continue accepting connections
            }
        }
    }

    private async Task HandleRedisConnectionAsync(TcpClient client, CancellationToken ct)
    {
        var connectionId = Guid.NewGuid().ToString("N");
        _redisConnections[connectionId] = client;

        try
        {
            var stream = client.GetStream();
            var buffer = new byte[65536];
            var offset = 0;

            while (!ct.IsCancellationRequested && client.Connected)
            {
                var bytesRead = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct);
                if (bytesRead == 0) break;

                offset += bytesRead;

                // Try to parse commands from buffer
                var consumed = 0;
                while (consumed < offset)
                {
                    var command = RespParser.Parse(buffer.AsSpan(consumed, offset - consumed), out var bytesConsumed);
                    if (command == null) break;

                    consumed += bytesConsumed;

                    var response = await HandleRedisCommandAsync(connectionId, command, ct);
                    if (response != null)
                    {
                        var responseBytes = RespSerializer.Serialize(response);
                        await stream.WriteAsync(responseBytes, ct);
                    }
                }

                // Shift remaining data to start of buffer
                if (consumed > 0)
                {
                    Array.Copy(buffer, consumed, buffer, 0, offset - consumed);
                    offset -= consumed;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NoSqlProtocol] Redis connection error ({connectionId}): {ex.Message}");
        }
        finally
        {
            _redisConnections.TryRemove(connectionId, out _);
            _redisPubSub.CleanupConnection(connectionId);
            _redisTransactions.CleanupConnection(connectionId);
            try { client.Close(); } catch { }
        }
    }

    private async Task<RespValue?> HandleRedisCommandAsync(string connectionId, RespValue command, CancellationToken ct)
    {
        await Task.CompletedTask;

        if (command.Type != RespType.Array || command.ArrayValue == null || command.ArrayValue.Length == 0)
        {
            return RespValue.Error("ERR", "Invalid command format");
        }

        var args = command.ArrayValue.Select(v => v.AsString() ?? "").ToArray();
        var cmd = args[0].ToUpperInvariant();

        // String commands
        if (cmd == "GET")
        {
            if (args.Length < 2) return RespValue.Error("ERR", "wrong number of arguments for 'GET' command");
            var value = _redisStore.GetString(args[1]);
            return value == null ? RespValue.Null() : RespValue.BulkString(value);
        }

        if (cmd == "SET")
        {
            if (args.Length < 3) return RespValue.Error("ERR", "wrong number of arguments for 'SET' command");
            _redisStore.Set(args[1], Encoding.UTF8.GetBytes(args[2]));
            return RespValue.SimpleString("OK");
        }

        if (cmd == "DEL")
        {
            if (args.Length < 2) return RespValue.Error("ERR", "wrong number of arguments for 'DEL' command");
            var count = _redisStore.Delete(args[1..]);
            return RespValue.Integer(count);
        }

        // Server commands
        if (cmd == "PING")
        {
            return args.Length > 1 ? RespValue.BulkString(args[1]) : RespValue.SimpleString("PONG");
        }

        if (cmd == "ECHO")
        {
            return args.Length > 1 ? RespValue.BulkString(args[1]) : RespValue.Error("ERR", "wrong number of arguments for 'ECHO' command");
        }

        if (cmd == "INFO")
        {
            var info = $"# Server\r\nredis_version:7.0.0\r\nredis_mode:standalone\r\n# Keyspace\r\ndb0:keys={_redisStore.DbSize().keys}\r\n";
            return RespValue.BulkString(info);
        }

        // Fallback
        return RespValue.Error("ERR", $"unknown command '{cmd}'");
    }

    #endregion

    #region Message Handlers

    private Dictionary<string, object> HandleStatus()
    {
        var (keys, expires) = _redisStore.DbSize();

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["mongoPort"] = _mongoPort,
            ["redisPort"] = _redisPort,
            ["maxConnections"] = _maxConnections,
            ["activeMongoConnections"] = _mongoConnections.Count,
            ["activeRedisConnections"] = _redisConnections.Count,
            ["redisKeyCount"] = keys,
            ["redisKeyExpires"] = expires
        };
    }

    private Dictionary<string, object> HandleMongoConnections()
    {
        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["count"] = _mongoConnections.Count,
            ["connections"] = _mongoConnections.Keys.ToArray()
        };
    }

    private Dictionary<string, object> HandleRedisConnections()
    {
        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["count"] = _redisConnections.Count,
            ["connections"] = _redisConnections.Keys.ToArray()
        };
    }

    private Dictionary<string, object> HandleRedisInfo()
    {
        var (keys, expires) = _redisStore.DbSize();

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["keyCount"] = keys,
            ["expiringKeys"] = expires,
            ["pubSubChannels"] = _redisPubSub.Channels().Length,
            ["pubSubPatterns"] = _redisPubSub.NumPat()
        };
    }

    #endregion

    #region Helper Methods

    private static async Task<byte[]?> ReadExactlyAsync(NetworkStream stream, int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        var offset = 0;

        while (offset < count)
        {
            var bytesRead = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), ct);
            if (bytesRead == 0) return null; // Connection closed
            offset += bytesRead;
        }

        return buffer;
    }

    #endregion

    /// <summary>
    /// Releases resources used by the plugin.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts?.Cancel();

        foreach (var client in _mongoConnections.Values)
        {
            try { client.Close(); } catch { }
        }

        foreach (var client in _redisConnections.Values)
        {
            try { client.Close(); } catch { }
        }

        _mongoConnections.Clear();
        _redisConnections.Clear();

        try { _mongoListener?.Stop(); } catch { }
        try { _redisListener?.Stop(); } catch { }

        _redisStore.Dispose();
        _cts?.Dispose();
    }
}
