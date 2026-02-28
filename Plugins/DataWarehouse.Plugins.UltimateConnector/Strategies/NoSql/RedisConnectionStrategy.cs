using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Connectors;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.NoSql;

/// <summary>
/// Redis connection strategy using StackExchange.Redis driver.
/// Provides production-ready connectivity to Redis 6.0+ with support for
/// String, Hash, List, Set, SortedSet, Pub/Sub, Streams, Lua scripting, and cluster.
/// </summary>
public sealed class RedisConnectionStrategy : DatabaseConnectionStrategyBase
{
    public override string StrategyId => "redis";
    public override string DisplayName => "Redis";
    public override string SemanticDescription =>
        "Redis in-memory data store using StackExchange.Redis driver. Supports strings, hashes, lists, " +
        "sets, sorted sets, pub/sub, streams, Lua scripting, pipelining, and cluster mode.";
    public override string[] Tags => ["nosql", "cache", "key-value", "redis", "in-memory", "pub-sub"];

    public override ConnectionStrategyCapabilities Capabilities => new(
        SupportsPooling: true,
        SupportsStreaming: true,
        SupportsTransactions: true,
        SupportsBulkOperations: true,
        SupportsSchemaDiscovery: false,
        SupportsSsl: true,
        SupportsCompression: false,
        SupportsAuthentication: true,
        MaxConcurrentConnections: 1000,
        SupportedAuthMethods: ["password", "acl"]
    );

    public RedisConnectionStrategy(ILogger? logger = null) : base(logger) { }

    protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
    {
        var connectionString = config.ConnectionString;

        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("Connection string is required for Redis connection.");

        var options = ConfigurationOptions.Parse(connectionString);
        options.ConnectTimeout = (int)config.Timeout.TotalMilliseconds;
        options.SyncTimeout = (int)config.Timeout.TotalMilliseconds;
        options.AsyncTimeout = (int)config.Timeout.TotalMilliseconds;
        options.AbortOnConnectFail = false;

        var multiplexer = await ConnectionMultiplexer.ConnectAsync(options);

        var server = multiplexer.GetServers().FirstOrDefault();
        var connectionInfo = new Dictionary<string, object>
        {
            ["Provider"] = "StackExchange.Redis",
            ["Endpoint"] = server?.EndPoint?.ToString() ?? "unknown",
            ["ServerVersion"] = server?.Version?.ToString() ?? "unknown",
            ["IsConnected"] = multiplexer.IsConnected,
            ["State"] = "Connected"
        };

        return new DefaultConnectionHandle(multiplexer, connectionInfo);
    }

    protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
    {
        try
        {
            var multiplexer = handle.GetConnection<IConnectionMultiplexer>();
            if (!multiplexer.IsConnected) return false;

            // Finding 2051: Use async PingAsync instead of synchronous db.Ping() to avoid
            // blocking the calling thread.
            var db = multiplexer.GetDatabase();
            var result = await db.PingAsync();
            return result.TotalMilliseconds >= 0;
        }
        catch
        {
            return false;
        }
    }

    protected override async Task DisconnectCoreAsync(IConnectionHandle handle, CancellationToken ct)
    {
        var multiplexer = handle.GetConnection<IConnectionMultiplexer>();
        await multiplexer.CloseAsync();
        await multiplexer.DisposeAsync();

        if (handle is DefaultConnectionHandle defaultHandle)
        {
            defaultHandle.MarkDisconnected();
        }
    }

    protected override async Task<ConnectionHealth> GetHealthCoreAsync(IConnectionHandle handle, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var multiplexer = handle.GetConnection<IConnectionMultiplexer>();
            var db = multiplexer.GetDatabase();
            // Finding 2052: Use async PingAsync instead of synchronous db.Ping().
            var latency = await db.PingAsync();
            sw.Stop();

            var server = multiplexer.GetServers().FirstOrDefault();
            var version = server?.Version?.ToString() ?? "unknown";

            return new ConnectionHealth(
                IsHealthy: multiplexer.IsConnected,
                StatusMessage: $"Redis {version} - Ping: {latency.TotalMilliseconds:F1}ms",
                Latency: sw.Elapsed,
                CheckedAt: DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ConnectionHealth(
                IsHealthy: false,
                StatusMessage: $"Health check failed: {ex.Message}",
                Latency: sw.Elapsed,
                CheckedAt: DateTimeOffset.UtcNow);
        }
    }

    /// <summary>
    /// Executes a Redis command. The query string is parsed as a Redis command (e.g., "GET mykey", "HGETALL myhash").
    /// </summary>
    public override async Task<IReadOnlyList<Dictionary<string, object?>>> ExecuteQueryAsync(
        IConnectionHandle handle,
        string query,
        Dictionary<string, object?>? parameters = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        var multiplexer = handle.GetConnection<IConnectionMultiplexer>();
        var db = multiplexer.GetDatabase();

        var parts = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            throw new ArgumentException("Redis command is empty.");

        var command = parts[0].ToUpperInvariant();
        var args = parts.Skip(1).Select(p => (RedisValue)p).ToArray();

        var result = await db.ExecuteAsync(command, args.Cast<object>().ToArray());
        var results = new List<Dictionary<string, object?>>();

        if (result.IsNull)
        {
            results.Add(new Dictionary<string, object?> { ["result"] = null, ["type"] = "null" });
        }
        else if (result.Resp2Type == ResultType.SimpleString || result.Resp2Type == ResultType.BulkString)
        {
            results.Add(new Dictionary<string, object?> { ["result"] = (string?)result, ["type"] = "string" });
        }
        else if (result.Resp2Type == ResultType.Integer)
        {
            results.Add(new Dictionary<string, object?> { ["result"] = (long)result, ["type"] = "integer" });
        }
        else if (result.Resp2Type == ResultType.Array)
        {
            var arr = (RedisResult[])result!;
            for (int i = 0; i < arr.Length; i++)
            {
                results.Add(new Dictionary<string, object?>
                {
                    ["index"] = i,
                    ["value"] = (string?)arr[i],
                    ["type"] = "array_element"
                });
            }
        }
        else
        {
            results.Add(new Dictionary<string, object?> { ["result"] = result.ToString(), ["type"] = "other" });
        }

        return results;
    }

    /// <summary>
    /// Executes a Redis write command (SET, DEL, HSET, etc.).
    /// </summary>
    public override async Task<int> ExecuteNonQueryAsync(
        IConnectionHandle handle,
        string command,
        Dictionary<string, object?>? parameters = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        var multiplexer = handle.GetConnection<IConnectionMultiplexer>();
        var db = multiplexer.GetDatabase();

        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var cmd = parts[0].ToUpperInvariant();
        var args = parts.Skip(1).Select(p => (object)(RedisValue)p).ToArray();

        var result = await db.ExecuteAsync(cmd, args);

        if (result.Resp2Type == ResultType.Integer)
            return (int)(long)result;

        return result.IsNull ? 0 : 1;
    }

    public override async Task<IReadOnlyList<DataSchema>> GetSchemaAsync(
        IConnectionHandle handle,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);

        var multiplexer = handle.GetConnection<IConnectionMultiplexer>();
        var server = multiplexer.GetServers().FirstOrDefault();

        var schemas = new List<DataSchema>();

        if (server != null)
        {
            // Get database info via INFO keyspace
            var info = await server.InfoAsync("keyspace");
            var keyspaceSection = info.FirstOrDefault(s => s.Key == "Keyspace");

            if (keyspaceSection is { Key: not null })
            {
                foreach (var entry in keyspaceSection!)
                {
                    schemas.Add(new DataSchema(
                        Name: entry.Key,
                        Fields:
                        [
                            new DataSchemaField("key", "String", false, null, null),
                            new DataSchemaField("value", "Any", true, null, null),
                            new DataSchemaField("type", "String", true, null, null),
                            new DataSchemaField("ttl", "Int64", true, null, null)
                        ],
                        PrimaryKeys: ["key"],
                        Metadata: new Dictionary<string, object> { ["info"] = entry.Value }
                    ));
                }
            }
        }

        if (schemas.Count == 0)
        {
            schemas.Add(new DataSchema(
                "db0",
                [
                    new DataSchemaField("key", "String", false, null, null),
                    new DataSchemaField("value", "Any", true, null, null),
                    new DataSchemaField("type", "String", true, null, null),
                    new DataSchemaField("ttl", "Int64", true, null, null)
                ],
                ["key"],
                new Dictionary<string, object> { ["type"] = "keyspace" }
            ));
        }

        return schemas;
    }

    public override Task<(bool IsValid, string[] Errors)> ValidateConfigAsync(
        ConnectionConfig config, CancellationToken ct = default)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(config.ConnectionString))
        {
            errors.Add("ConnectionString is required for Redis connection.");
        }
        else
        {
            try
            {
                ConfigurationOptions.Parse(config.ConnectionString);
            }
            catch (Exception ex)
            {
                errors.Add($"Invalid Redis connection string format: {ex.Message}");
            }
        }

        if (config.Timeout <= TimeSpan.Zero)
            errors.Add("Timeout must be a positive duration.");

        return Task.FromResult((errors.Count == 0, errors.ToArray()));
    }
}
