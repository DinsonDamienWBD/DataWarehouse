using System.Runtime.CompilerServices;
using DataWarehouse.SDK.Connectors;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using StackExchange.Redis;

namespace DataWarehouse.Plugins.DataConnectors;

/// <summary>
/// Production-ready Redis connector plugin.
/// Provides comprehensive Redis operations including key/value, hash, list, set, sorted set, and pub/sub.
/// Supports connection multiplexer pooling, cluster mode, pipeline operations, and async/await throughout.
/// </summary>
public class RedisConnectorPlugin : DatabaseConnectorPluginBase
{
    private ConnectionMultiplexer? _multiplexer;
    private IDatabase? _database;
    private ISubscriber? _subscriber;
    private string? _connectionString;
    private int _databaseIndex;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private RedisConnectorConfig _config = new();

    /// <inheritdoc />
    public override string Id => "datawarehouse.connector.redis";

    /// <inheritdoc />
    public override string Name => "Redis Connector";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    public override string ConnectorId => "redis";

    /// <inheritdoc />
    public override PluginCategory Category => PluginCategory.InfrastructureProvider;

    /// <inheritdoc />
    public override ConnectorCapabilities Capabilities =>
        ConnectorCapabilities.Read |
        ConnectorCapabilities.Write |
        ConnectorCapabilities.Schema |
        ConnectorCapabilities.BulkOperations |
        ConnectorCapabilities.Streaming;

    /// <summary>
    /// Configures the connector with additional options.
    /// </summary>
    /// <param name="config">Redis-specific configuration.</param>
    public void Configure(RedisConnectorConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <inheritdoc />
    protected override async Task<ConnectionResult> EstablishConnectionAsync(ConnectorConfig config, CancellationToken ct)
    {
        await _connectionLock.WaitAsync(ct);
        try
        {
            _connectionString = config.ConnectionString;

            if (string.IsNullOrWhiteSpace(_connectionString))
            {
                return new ConnectionResult(false, "Connection string is required", null);
            }

            // Parse database index from properties or use default
            _databaseIndex = int.TryParse(props.GetValueOrDefault("Database", "0"), out var dbIdx)
                ? dbIdx
                : 0;

            // Configure connection options
            var options = ConfigurationOptions.Parse(_connectionString);
            options.ConnectTimeout = _config.ConnectTimeoutMs;
            options.SyncTimeout = _config.SyncTimeoutMs;
            options.AsyncTimeout = _config.AsyncTimeoutMs;
            options.ConnectRetry = _config.ConnectRetry;
            options.AbortOnConnectFail = _config.AbortOnConnectFail;
            options.AllowAdmin = _config.AllowAdmin;
            options.KeepAlive = _config.KeepAliveSeconds;

            // Create connection multiplexer
            _multiplexer = await ConnectionMultiplexer.ConnectAsync(options);
            _database = _multiplexer.GetDatabase(_databaseIndex);
            _subscriber = _multiplexer.GetSubscriber();

            // Test the connection with PING
            var pingTime = await _database.PingAsync();

            // Get server info
            var endpoints = _multiplexer.GetEndPoints();
            var server = _multiplexer.GetServer(endpoints[0]);
            var serverInfo = server.Info("Server");
            var clusterInfo = server.Info("Cluster");

            var serverData = new Dictionary<string, object>
            {
                ["PingTimeMs"] = pingTime.TotalMilliseconds,
                ["DatabaseIndex"] = _databaseIndex,
                ["IsConnected"] = _multiplexer.IsConnected,
                ["EndpointCount"] = endpoints.Length,
                ["Endpoints"] = string.Join(", ", endpoints.Select(e => e.ToString())),
                ["AllowAdmin"] = options.AllowAdmin,
                ["ConnectionType"] = _multiplexer.IsConnected ? "Connected" : "Disconnected"
            };

            // Extract server version
            var versionInfo = serverInfo.FirstOrDefault(g => g.Key == "Server");
            if (versionInfo != null)
            {
                var redisVersion = versionInfo.FirstOrDefault(kv => kv.Key == "redis_version");
                if (redisVersion.Key != null)
                {
                    serverData["RedisVersion"] = redisVersion.Value;
                }

                var mode = versionInfo.FirstOrDefault(kv => kv.Key == "redis_mode");
                if (mode.Key != null)
                {
                    serverData["RedisMode"] = mode.Value;
                }
            }

            // Check cluster status
            var clusterEnabled = clusterInfo.FirstOrDefault(g => g.Key == "Cluster")
                ?.FirstOrDefault(kv => kv.Key == "cluster_enabled").Value;
            serverData["ClusterEnabled"] = clusterEnabled == "1";

            return new ConnectionResult(true, null, serverData);
        }
        catch (RedisException ex)
        {
            return new ConnectionResult(false, $"Redis connection failed: {ex.Message}", null);
        }
        catch (Exception ex)
        {
            return new ConnectionResult(false, $"Connection failed: {ex.Message}", null);
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <inheritdoc />
    protected override async Task CloseConnectionAsync()
    {
        await _connectionLock.WaitAsync();
        try
        {
            if (_multiplexer != null)
            {
                await _multiplexer.CloseAsync();
                _multiplexer.Dispose();
                _multiplexer = null;
            }

            _database = null;
            _subscriber = null;
            _connectionString = null;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <inheritdoc />
    protected override async Task<bool> PingAsync()
    {
        if (_database == null) return false;

        try
        {
            var pingTime = await _database.PingAsync();
            return pingTime != TimeSpan.MaxValue;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
    protected override async Task<DataSchema> FetchSchemaAsync()
    {
        if (_multiplexer == null || _database == null)
            throw new InvalidOperationException("Not connected to Redis");

        var endpoints = _multiplexer.GetEndPoints();
        var server = _multiplexer.GetServer(endpoints[0]);

        // Get database info
        var info = server.Info("Keyspace");
        var keyspaceInfo = info.FirstOrDefault(g => g.Key == "Keyspace");
        var dbStats = keyspaceInfo?.FirstOrDefault(kv => kv.Key == $"db{_databaseIndex}").Value;

        long keyCount = 0;
        if (!string.IsNullOrEmpty(dbStats))
        {
            // Parse "keys=123,expires=45,avg_ttl=678" format
            var parts = dbStats.Split(',');
            var keysInfo = parts.FirstOrDefault(p => p.StartsWith("keys="));
            if (keysInfo != null && long.TryParse(keysInfo.Substring(5), out var count))
            {
                keyCount = count;
            }
        }

        return new DataSchema(
            Name: $"redis-db{_databaseIndex}",
            Fields: new[]
            {
                new DataSchemaField("key", "string", false, null, null),
                new DataSchemaField("value", "string", true, null, null),
                new DataSchemaField("type", "string", false, null, null),
                new DataSchemaField("ttl", "long", true, null, null),
                new DataSchemaField("size", "long", true, null, null)
            },
            PrimaryKeys: new[] { "key" },
            Metadata: new Dictionary<string, object>
            {
                ["DatabaseIndex"] = _databaseIndex,
                ["KeyCount"] = keyCount,
                ["ServerType"] = "Redis",
                ["SupportedTypes"] = new[] { "string", "hash", "list", "set", "zset" }
            }
        );
    }

    #region Key/Value Operations

    /// <summary>
    /// Gets a string value by key.
    /// </summary>
    /// <param name="key">Redis key.</param>
    /// <returns>String value or null if key doesn't exist.</returns>
    public async Task<string?> GetAsync(string key)
    {
        if (_database == null)
            throw new InvalidOperationException("Not connected to Redis");

        return await _database.StringGetAsync(key);
    }

    /// <summary>
    /// Sets a string value for a key.
    /// </summary>
    /// <param name="key">Redis key.</param>
    /// <param name="value">Value to set.</param>
    /// <param name="expiry">Optional expiration time.</param>
    /// <returns>True if the operation succeeded.</returns>
    public async Task<bool> SetAsync(string key, string value, TimeSpan? expiry = null)
    {
        if (_database == null)
            throw new InvalidOperationException("Not connected to Redis");

        return await _database.StringSetAsync(key, value, expiry);
    }

    /// <summary>
    /// Gets multiple string values by keys.
    /// </summary>
    /// <param name="keys">Array of Redis keys.</param>
    /// <returns>Array of values corresponding to the keys.</returns>
    public async Task<RedisValue[]> GetMultipleAsync(string[] keys)
    {
        if (_database == null)
            throw new InvalidOperationException("Not connected to Redis");

        var redisKeys = keys.Select(k => (RedisKey)k).ToArray();
        return await _database.StringGetAsync(redisKeys);
    }

    /// <summary>
    /// Sets multiple key-value pairs.
    /// </summary>
    /// <param name="keyValuePairs">Dictionary of key-value pairs to set.</param>
    /// <returns>True if all operations succeeded.</returns>
    public async Task<bool> SetMultipleAsync(Dictionary<string, string> keyValuePairs)
    {
        if (_database == null)
            throw new InvalidOperationException("Not connected to Redis");

        var redisValues = keyValuePairs.Select(kv => new KeyValuePair<RedisKey, RedisValue>(kv.Key, kv.Value)).ToArray();
        return await _database.StringSetAsync(redisValues);
    }

    #endregion

    #region Hash Operations

    /// <summary>
    /// Gets a hash field value.
    /// </summary>
    /// <param name="key">Hash key.</param>
    /// <param name="field">Field name.</param>
    /// <returns>Field value or null if not found.</returns>
    public async Task<string?> HashGetAsync(string key, string field)
    {
        if (_database == null)
            throw new InvalidOperationException("Not connected to Redis");

        return await _database.HashGetAsync(key, field);
    }

    /// <summary>
    /// Sets a hash field value.
    /// </summary>
    /// <param name="key">Hash key.</param>
    /// <param name="field">Field name.</param>
    /// <param name="value">Field value.</param>
    /// <returns>True if the field was created, false if updated.</returns>
    public async Task<bool> HashSetAsync(string key, string field, string value)
    {
        if (_database == null)
            throw new InvalidOperationException("Not connected to Redis");

        return await _database.HashSetAsync(key, field, value);
    }

    /// <summary>
    /// Gets all fields and values in a hash.
    /// </summary>
    /// <param name="key">Hash key.</param>
    /// <returns>Dictionary of all field-value pairs.</returns>
    public async Task<Dictionary<string, string>> HashGetAllAsync(string key)
    {
        if (_database == null)
            throw new InvalidOperationException("Not connected to Redis");

        var entries = await _database.HashGetAllAsync(key);
        return entries.ToDictionary(e => e.Name.ToString(), e => e.Value.ToString());
    }

    /// <summary>
    /// Sets multiple hash fields.
    /// </summary>
    /// <param name="key">Hash key.</param>
    /// <param name="fields">Dictionary of field-value pairs.</param>
    /// <returns>Task representing the operation.</returns>
    public async Task HashSetMultipleAsync(string key, Dictionary<string, string> fields)
    {
        if (_database == null)
            throw new InvalidOperationException("Not connected to Redis");

        var entries = fields.Select(kv => new HashEntry(kv.Key, kv.Value)).ToArray();
        await _database.HashSetAsync(key, entries);
    }

    #endregion

    #region List Operations

    /// <summary>
    /// Pushes a value to the left (head) of a list.
    /// </summary>
    /// <param name="key">List key.</param>
    /// <param name="value">Value to push.</param>
    /// <returns>Length of the list after the operation.</returns>
    public async Task<long> ListLeftPushAsync(string key, string value)
    {
        if (_database == null)
            throw new InvalidOperationException("Not connected to Redis");

        return await _database.ListLeftPushAsync(key, value);
    }

    /// <summary>
    /// Pushes a value to the right (tail) of a list.
    /// </summary>
    /// <param name="key">List key.</param>
    /// <param name="value">Value to push.</param>
    /// <returns>Length of the list after the operation.</returns>
    public async Task<long> ListRightPushAsync(string key, string value)
    {
        if (_database == null)
            throw new InvalidOperationException("Not connected to Redis");

        return await _database.ListRightPushAsync(key, value);
    }

    /// <summary>
    /// Gets a range of elements from a list.
    /// </summary>
    /// <param name="key">List key.</param>
    /// <param name="start">Start index (0-based, inclusive).</param>
    /// <param name="stop">Stop index (0-based, inclusive, -1 for end).</param>
    /// <returns>Array of values in the range.</returns>
    public async Task<string[]> ListRangeAsync(string key, long start = 0, long stop = -1)
    {
        if (_database == null)
            throw new InvalidOperationException("Not connected to Redis");

        var values = await _database.ListRangeAsync(key, start, stop);
        return values.Select(v => v.ToString()).ToArray();
    }

    #endregion

    #region Set Operations

    /// <summary>
    /// Adds a member to a set.
    /// </summary>
    /// <param name="key">Set key.</param>
    /// <param name="value">Member value.</param>
    /// <returns>True if the member was added, false if it already existed.</returns>
    public async Task<bool> SetAddAsync(string key, string value)
    {
        if (_database == null)
            throw new InvalidOperationException("Not connected to Redis");

        return await _database.SetAddAsync(key, value);
    }

    /// <summary>
    /// Adds multiple members to a set.
    /// </summary>
    /// <param name="key">Set key.</param>
    /// <param name="values">Array of member values.</param>
    /// <returns>Number of members added.</returns>
    public async Task<long> SetAddMultipleAsync(string key, string[] values)
    {
        if (_database == null)
            throw new InvalidOperationException("Not connected to Redis");

        var redisValues = values.Select(v => (RedisValue)v).ToArray();
        return await _database.SetAddAsync(key, redisValues);
    }

    /// <summary>
    /// Gets all members of a set.
    /// </summary>
    /// <param name="key">Set key.</param>
    /// <returns>Array of all members.</returns>
    public async Task<string[]> SetMembersAsync(string key)
    {
        if (_database == null)
            throw new InvalidOperationException("Not connected to Redis");

        var values = await _database.SetMembersAsync(key);
        return values.Select(v => v.ToString()).ToArray();
    }

    #endregion

    #region Sorted Set Operations

    /// <summary>
    /// Adds a member with a score to a sorted set.
    /// </summary>
    /// <param name="key">Sorted set key.</param>
    /// <param name="member">Member value.</param>
    /// <param name="score">Score for ordering.</param>
    /// <returns>True if the member was added, false if the score was updated.</returns>
    public async Task<bool> SortedSetAddAsync(string key, string member, double score)
    {
        if (_database == null)
            throw new InvalidOperationException("Not connected to Redis");

        return await _database.SortedSetAddAsync(key, member, score);
    }

    /// <summary>
    /// Adds multiple members with scores to a sorted set.
    /// </summary>
    /// <param name="key">Sorted set key.</param>
    /// <param name="members">Dictionary of member-score pairs.</param>
    /// <returns>Number of members added.</returns>
    public async Task<long> SortedSetAddMultipleAsync(string key, Dictionary<string, double> members)
    {
        if (_database == null)
            throw new InvalidOperationException("Not connected to Redis");

        var entries = members.Select(kv => new SortedSetEntry(kv.Key, kv.Value)).ToArray();
        return await _database.SortedSetAddAsync(key, entries);
    }

    /// <summary>
    /// Gets a range of members from a sorted set by rank.
    /// </summary>
    /// <param name="key">Sorted set key.</param>
    /// <param name="start">Start rank (0-based, inclusive).</param>
    /// <param name="stop">Stop rank (0-based, inclusive, -1 for end).</param>
    /// <param name="order">Sort order (ascending or descending).</param>
    /// <returns>Array of members in the range.</returns>
    public async Task<string[]> SortedSetRangeByRankAsync(
        string key,
        long start = 0,
        long stop = -1,
        Order order = Order.Ascending)
    {
        if (_database == null)
            throw new InvalidOperationException("Not connected to Redis");

        var values = await _database.SortedSetRangeByRankAsync(key, start, stop, order);
        return values.Select(v => v.ToString()).ToArray();
    }

    /// <summary>
    /// Gets a range of members with scores from a sorted set by rank.
    /// </summary>
    /// <param name="key">Sorted set key.</param>
    /// <param name="start">Start rank (0-based, inclusive).</param>
    /// <param name="stop">Stop rank (0-based, inclusive, -1 for end).</param>
    /// <param name="order">Sort order (ascending or descending).</param>
    /// <returns>Dictionary of member-score pairs.</returns>
    public async Task<Dictionary<string, double>> SortedSetRangeByRankWithScoresAsync(
        string key,
        long start = 0,
        long stop = -1,
        Order order = Order.Ascending)
    {
        if (_database == null)
            throw new InvalidOperationException("Not connected to Redis");

        var entries = await _database.SortedSetRangeByRankWithScoresAsync(key, start, stop, order);
        return entries.ToDictionary(e => e.Element.ToString(), e => e.Score);
    }

    #endregion

    #region Pub/Sub Operations

    /// <summary>
    /// Publishes a message to a channel.
    /// </summary>
    /// <param name="channel">Channel name.</param>
    /// <param name="message">Message to publish.</param>
    /// <returns>Number of subscribers that received the message.</returns>
    public async Task<long> PublishAsync(string channel, string message)
    {
        if (_subscriber == null)
            throw new InvalidOperationException("Not connected to Redis");

        return await _subscriber.PublishAsync(RedisChannel.Literal(channel), message);
    }

    /// <summary>
    /// Subscribes to a channel and returns messages as an async enumerable.
    /// </summary>
    /// <param name="channel">Channel name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Async enumerable of messages.</returns>
    public async IAsyncEnumerable<string> SubscribeAsync(
        string channel,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_subscriber == null)
            throw new InvalidOperationException("Not connected to Redis");

        var messageQueue = System.Threading.Channels.Channel.CreateUnbounded<string>();

        await _subscriber.SubscribeAsync(RedisChannel.Literal(channel), async (ch, msg) =>
        {
            await messageQueue.Writer.WriteAsync(msg.ToString(), ct);
        });

        await foreach (var message in messageQueue.Reader.ReadAllAsync(ct))
        {
            yield return message;
        }
    }

    /// <summary>
    /// Unsubscribes from a channel.
    /// </summary>
    /// <param name="channel">Channel name.</param>
    /// <returns>Task representing the operation.</returns>
    public async Task UnsubscribeAsync(string channel)
    {
        if (_subscriber == null)
            throw new InvalidOperationException("Not connected to Redis");

        await _subscriber.UnsubscribeAsync(RedisChannel.Literal(channel));
    }

    #endregion

    #region Pipeline/Batch Operations

    /// <summary>
    /// Executes multiple commands in a pipeline for better performance.
    /// </summary>
    /// <param name="operations">Function that performs batch operations.</param>
    /// <returns>Task representing the batch operation.</returns>
    public async Task ExecuteBatchAsync(Func<IBatch, Task> operations)
    {
        if (_database == null)
            throw new InvalidOperationException("Not connected to Redis");

        var batch = _database.CreateBatch();
        await operations(batch);
        batch.Execute();
    }

    /// <summary>
    /// Executes multiple operations in a transaction.
    /// </summary>
    /// <param name="operations">Function that performs transactional operations.</param>
    /// <returns>True if the transaction succeeded.</returns>
    public async Task<bool> ExecuteTransactionAsync(Func<ITransaction, Task> operations)
    {
        if (_database == null)
            throw new InvalidOperationException("Not connected to Redis");

        var transaction = _database.CreateTransaction();
        await operations(transaction);
        return await transaction.ExecuteAsync();
    }

    #endregion

    #region Cluster Operations

    /// <summary>
    /// Gets cluster nodes information.
    /// </summary>
    /// <returns>Dictionary with cluster node details.</returns>
    public async Task<Dictionary<string, object>?> GetClusterInfoAsync()
    {
        if (_multiplexer == null)
            throw new InvalidOperationException("Not connected to Redis");

        try
        {
            var endpoints = _multiplexer.GetEndPoints();
            var server = _multiplexer.GetServer(endpoints[0]);

            // Check if cluster mode is enabled
            var clusterInfo = server.Info("Cluster");
            var clusterEnabled = clusterInfo.FirstOrDefault(g => g.Key == "Cluster")
                ?.FirstOrDefault(kv => kv.Key == "cluster_enabled").Value;

            if (clusterEnabled != "1")
                return null;

            var nodes = server.ClusterNodes();

            return new Dictionary<string, object>
            {
                ["NodeCount"] = nodes?.Nodes.Count ?? 0,
                ["Nodes"] = nodes?.Nodes.Select(n => new
                {
                    EndPoint = n.EndPoint?.ToString() ?? "unknown",
                    NodeId = n.NodeId,
                    IsReplica = n.IsReplica,
                    IsMaster = !n.IsReplica,
                    ServerId = n.NodeId
                }).ToArray() ?? Array.Empty<object>()
            };
        }
        catch
        {
            return null;
        }
    }

    #endregion

    /// <inheritdoc />
    protected override async IAsyncEnumerable<DataRecord> ExecuteReadAsync(
        DataQuery query,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (_multiplexer == null || _database == null)
            throw new InvalidOperationException("Not connected to Redis");

        // Support key pattern scanning
        var pattern = query.Filter ?? "*";
        var endpoints = _multiplexer.GetEndPoints();
        var server = _multiplexer.GetServer(endpoints[0]);

        long position = 0;
        var limit = query.Limit ?? long.MaxValue;
        var offset = query.Offset ?? 0;

        await foreach (var key in server.KeysAsync(_databaseIndex, pattern).WithCancellation(ct))
        {
            if (ct.IsCancellationRequested) yield break;

            if (position < offset)
            {
                position++;
                continue;
            }

            if (position >= offset + limit)
                break;

            var keyStr = key.ToString();
            var type = await _database.KeyTypeAsync(key);
            var ttl = await _database.KeyTimeToLiveAsync(key);

            var values = new Dictionary<string, object?>
            {
                ["key"] = keyStr,
                ["type"] = type.ToString().ToLower(),
                ["ttl"] = ttl?.TotalSeconds ?? -1
            };

            // Get value based on type
            switch (type)
            {
                case RedisType.String:
                    values["value"] = await _database.StringGetAsync(key);
                    break;
                case RedisType.List:
                    var listLength = await _database.ListLengthAsync(key);
                    values["value"] = $"<list:{listLength}>";
                    values["size"] = listLength;
                    break;
                case RedisType.Set:
                    var setLength = await _database.SetLengthAsync(key);
                    values["value"] = $"<set:{setLength}>";
                    values["size"] = setLength;
                    break;
                case RedisType.SortedSet:
                    var zsetLength = await _database.SortedSetLengthAsync(key);
                    values["value"] = $"<zset:{zsetLength}>";
                    values["size"] = zsetLength;
                    break;
                case RedisType.Hash:
                    var hashLength = await _database.HashLengthAsync(key);
                    values["value"] = $"<hash:{hashLength}>";
                    values["size"] = hashLength;
                    break;
            }

            yield return new DataRecord(
                Values: values,
                Position: position++,
                Timestamp: DateTimeOffset.UtcNow
            );
        }
    }

    /// <inheritdoc />
    protected override async Task<WriteResult> ExecuteWriteAsync(
        IAsyncEnumerable<DataRecord> records,
        WriteOptions options,
        CancellationToken ct)
    {
        if (_database == null)
            throw new InvalidOperationException("Not connected to Redis");

        long written = 0;
        long failed = 0;
        var errors = new List<string>();

        await foreach (var record in records.WithCancellation(ct))
        {
            try
            {
                var key = record.Values.GetValueOrDefault("key")?.ToString()
                    ?? throw new ArgumentException("Key is required");
                var value = record.Values.GetValueOrDefault("value")?.ToString()
                    ?? throw new ArgumentException("Value is required");

                TimeSpan? expiry = null;
                if (record.Values.TryGetValue("ttl", out var ttlObj) && ttlObj != null)
                {
                    if (long.TryParse(ttlObj.ToString(), out var ttlSeconds) && ttlSeconds > 0)
                    {
                        expiry = TimeSpan.FromSeconds(ttlSeconds);
                    }
                }

                var success = await _database.StringSetAsync(key, value, expiry);
                if (success)
                    written++;
                else
                    failed++;
            }
            catch (Exception ex)
            {
                failed++;
                errors.Add($"Record at position {record.Position}: {ex.Message}");
            }
        }

        return new WriteResult(written, failed, errors.Count > 0 ? errors.ToArray() : null);
    }

    /// <inheritdoc />
    protected override string BuildSelectQuery(DataQuery query)
    {
        // Redis uses key patterns instead of SQL
        return query.Filter ?? "*";
    }

    /// <inheritdoc />
    protected override string BuildInsertStatement(string table, string[] columns)
    {
        // Not applicable for Redis
        return string.Empty;
    }

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc />
    public override Task StopAsync() => DisconnectAsync();
}

/// <summary>
/// Configuration options for the Redis connector.
/// </summary>
public class RedisConnectorConfig
{
    /// <summary>
    /// Connection timeout in milliseconds.
    /// </summary>
    public int ConnectTimeoutMs { get; set; } = 5000;

    /// <summary>
    /// Synchronous operation timeout in milliseconds.
    /// </summary>
    public int SyncTimeoutMs { get; set; } = 5000;

    /// <summary>
    /// Asynchronous operation timeout in milliseconds.
    /// </summary>
    public int AsyncTimeoutMs { get; set; } = 5000;

    /// <summary>
    /// Number of connection retry attempts.
    /// </summary>
    public int ConnectRetry { get; set; } = 3;

    /// <summary>
    /// Whether to abort on connection failure.
    /// </summary>
    public bool AbortOnConnectFail { get; set; } = false;

    /// <summary>
    /// Whether to allow admin commands.
    /// </summary>
    public bool AllowAdmin { get; set; } = false;

    /// <summary>
    /// Keep-alive interval in seconds.
    /// </summary>
    public int KeepAliveSeconds { get; set; } = 60;
}
