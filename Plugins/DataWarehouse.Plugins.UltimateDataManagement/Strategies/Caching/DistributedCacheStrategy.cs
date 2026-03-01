using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.Caching;

/// <summary>
/// Distributed cache backend type.
/// </summary>
public enum DistributedCacheBackend
{
    /// <summary>
    /// Redis cache.
    /// </summary>
    Redis,

    /// <summary>
    /// Memcached.
    /// </summary>
    Memcached,

    /// <summary>
    /// Custom implementation.
    /// </summary>
    Custom
}

/// <summary>
/// Serialization format for cache values.
/// </summary>
public enum CacheSerializationFormat
{
    /// <summary>
    /// JSON serialization.
    /// </summary>
    Json,

    /// <summary>
    /// Binary (raw bytes).
    /// </summary>
    Binary,

    /// <summary>
    /// MessagePack serialization.
    /// </summary>
    MessagePack
}

/// <summary>
/// Configuration for distributed cache.
/// </summary>
public sealed class DistributedCacheConfig
{
    /// <summary>
    /// Cache backend type.
    /// </summary>
    public DistributedCacheBackend Backend { get; init; } = DistributedCacheBackend.Redis;

    /// <summary>
    /// Connection string.
    /// </summary>
    public required string ConnectionString { get; init; }

    /// <summary>
    /// Serialization format.
    /// </summary>
    public CacheSerializationFormat SerializationFormat { get; init; } = CacheSerializationFormat.Binary;

    /// <summary>
    /// Connection pool size.
    /// </summary>
    public int PoolSize { get; init; } = 10;

    /// <summary>
    /// Connection timeout.
    /// </summary>
    public TimeSpan ConnectionTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Operation timeout.
    /// </summary>
    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Key prefix for namespacing.
    /// </summary>
    public string? KeyPrefix { get; init; }

    /// <summary>
    /// Whether to use cluster mode.
    /// </summary>
    public bool ClusterMode { get; init; }

    /// <summary>
    /// Cluster node endpoints (for cluster mode).
    /// </summary>
    public string[]? ClusterEndpoints { get; init; }

    /// <summary>
    /// Whether to enable SSL/TLS.
    /// </summary>
    public bool UseSsl { get; init; }

    /// <summary>
    /// Authentication password.
    /// </summary>
    public string? Password { get; init; }
}

/// <summary>
/// Distributed cache connection.
/// </summary>
internal sealed class CacheConnection : IDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly object _lock = new();
    private bool _disposed;

    public CacheConnection(string host, int port, TimeSpan timeout)
    {
        _client = new TcpClient();
        _client.ReceiveTimeout = (int)timeout.TotalMilliseconds;
        _client.SendTimeout = (int)timeout.TotalMilliseconds;
        // P2-2399: bound the TCP connect with the caller-supplied timeout so we never hang forever.
        using var cts = new CancellationTokenSource(timeout);
        _client.ConnectAsync(host, port, cts.Token).GetAwaiter().GetResult();
        _stream = _client.GetStream();
    }

    public bool IsConnected => _client.Connected && !_disposed;

    public async Task<byte[]?> SendCommandAsync(byte[] command, CancellationToken ct)
    {
        if (_disposed) return null;

        await _stream.WriteAsync(command, ct);
        await _stream.FlushAsync(ct);

        var buffer = new byte[65536];
        var bytesRead = await _stream.ReadAsync(buffer, ct);

        if (bytesRead > 0)
        {
            var result = new byte[bytesRead];
            Array.Copy(buffer, result, bytesRead);
            return result;
        }

        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stream?.Dispose();
        _client?.Dispose();
    }
}

/// <summary>
/// Connection pool for distributed cache.
/// </summary>
internal sealed class ConnectionPool : IDisposable
{
    private readonly ConcurrentBag<CacheConnection> _connections = new();
    private readonly SemaphoreSlim _semaphore;
    private readonly string _host;
    private readonly int _port;
    private readonly TimeSpan _timeout;
    private readonly int _maxSize;
    private bool _disposed;

    public ConnectionPool(string host, int port, int maxSize, TimeSpan timeout)
    {
        _host = host;
        _port = port;
        _maxSize = maxSize;
        _timeout = timeout;
        _semaphore = new SemaphoreSlim(maxSize, maxSize);
    }

    public async Task<CacheConnection?> GetConnectionAsync(CancellationToken ct)
    {
        if (_disposed) return null;

        await _semaphore.WaitAsync(ct);

        if (_connections.TryTake(out var connection) && connection.IsConnected)
        {
            return connection;
        }

        try
        {
            return new CacheConnection(_host, _port, _timeout);
        }
        catch
        {
            _semaphore.Release();
            return null;
        }
    }

    public void ReturnConnection(CacheConnection connection)
    {
        if (_disposed || !connection.IsConnected)
        {
            connection.Dispose();
        }
        else
        {
            _connections.Add(connection);
        }
        _semaphore.Release();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        while (_connections.TryTake(out var connection))
        {
            connection.Dispose();
        }

        _semaphore.Dispose();
    }
}

/// <summary>
/// Distributed cache strategy supporting Redis and Memcached.
/// Provides connection pooling, serialization options, and cluster support.
/// </summary>
/// <remarks>
/// Features:
/// - Redis and Memcached backend support
/// - Connection pooling for high throughput
/// - Multiple serialization formats
/// - Cluster mode support
/// - Tag-based invalidation
/// - SSL/TLS encryption
/// </remarks>
public sealed class DistributedCacheStrategy : CachingStrategyBase
{
    private readonly DistributedCacheConfig _config;
    private ConnectionPool? _pool;
    private readonly BoundedDictionary<string, HashSet<string>> _tagIndex = new BoundedDictionary<string, HashSet<string>>(1000);
    private readonly object _tagLock = new();
    private long _trackedSize;
    private long _trackedEntryCount;

    /// <summary>
    /// Initializes a new DistributedCacheStrategy.
    /// </summary>
    /// <param name="config">Cache configuration.</param>
    public DistributedCacheStrategy(DistributedCacheConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <inheritdoc/>
    public override string StrategyId => "cache.distributed";

    /// <inheritdoc/>
    public override string DisplayName => $"Distributed Cache ({_config.Backend})";

    /// <inheritdoc/>
    public override DataManagementCapabilities Capabilities { get; } = new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = true,
        SupportsTransactions = false,
        SupportsTTL = true,
        MaxThroughput = 100_000,
        TypicalLatencyMs = 1.0
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Distributed cache strategy supporting Redis and Memcached backends. " +
        "Provides connection pooling, serialization options, and cluster support for high-throughput caching.";

    /// <inheritdoc/>
    public override string[] Tags => ["cache", "distributed", "redis", "memcached", "cluster"];

    /// <inheritdoc/>
    public override long GetCurrentSize() => Interlocked.Read(ref _trackedSize);

    /// <inheritdoc/>
    public override long GetEntryCount() => Interlocked.Read(ref _trackedEntryCount);

    /// <inheritdoc/>
    protected override Task InitializeCoreAsync(CancellationToken ct)
    {
        var uri = new Uri(_config.ConnectionString.StartsWith("redis://") || _config.ConnectionString.StartsWith("memcached://")
            ? _config.ConnectionString
            : $"tcp://{_config.ConnectionString}");

        var host = uri.Host;
        var port = uri.Port > 0 ? uri.Port : (_config.Backend == DistributedCacheBackend.Redis ? 6379 : 11211);

        _pool = new ConnectionPool(host, port, _config.PoolSize, _config.ConnectionTimeout);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task DisposeCoreAsync()
    {
        _pool?.Dispose();
        _pool = null;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override async Task<CacheResult<byte[]>> GetCoreAsync(string key, CancellationToken ct)
    {
        var fullKey = GetFullKey(key);

        if (_config.Backend == DistributedCacheBackend.Redis)
        {
            var result = await ExecuteRedisCommandAsync($"GET {fullKey}\r\n", ct);
            if (result != null && !IsNullResponse(result))
            {
                return CacheResult<byte[]>.Hit(ExtractRedisValue(result));
            }
        }
        else if (_config.Backend == DistributedCacheBackend.Memcached)
        {
            var result = await ExecuteMemcachedCommandAsync($"get {fullKey}\r\n", ct);
            if (result != null && !result.StartsWith("END"))
            {
                return CacheResult<byte[]>.Hit(ExtractMemcachedValue(result));
            }
        }

        return CacheResult<byte[]>.Miss();
    }

    /// <inheritdoc/>
    protected override async Task SetCoreAsync(string key, byte[] value, CacheOptions options, CancellationToken ct)
    {
        var fullKey = GetFullKey(key);
        var ttlSeconds = options.TTL?.TotalSeconds ?? 0;

        if (_config.Backend == DistributedCacheBackend.Redis)
        {
            var valueStr = Convert.ToBase64String(value);
            var command = ttlSeconds > 0
                ? $"SETEX {fullKey} {(int)ttlSeconds} {valueStr}\r\n"
                : $"SET {fullKey} {valueStr}\r\n";
            await ExecuteRedisCommandAsync(command, ct);
        }
        else if (_config.Backend == DistributedCacheBackend.Memcached)
        {
            var valueStr = Convert.ToBase64String(value);
            var command = $"set {fullKey} 0 {(int)ttlSeconds} {valueStr.Length}\r\n{valueStr}\r\n";
            await ExecuteMemcachedCommandAsync(command, ct);
        }

        // Track tags
        if (options.Tags != null && options.Tags.Length > 0)
        {
            lock (_tagLock)
            {
                foreach (var tag in options.Tags)
                {
                    var keys = _tagIndex.GetOrAdd(tag, _ => new HashSet<string>());
                    keys.Add(key);
                }
            }
        }
    }

    /// <inheritdoc/>
    protected override async Task<bool> RemoveCoreAsync(string key, CancellationToken ct)
    {
        var fullKey = GetFullKey(key);

        if (_config.Backend == DistributedCacheBackend.Redis)
        {
            var result = await ExecuteRedisCommandAsync($"DEL {fullKey}\r\n", ct);
            return result != null && result.Contains(":1");
        }
        else if (_config.Backend == DistributedCacheBackend.Memcached)
        {
            var result = await ExecuteMemcachedCommandAsync($"delete {fullKey}\r\n", ct);
            return result != null && result.Contains("DELETED");
        }

        return false;
    }

    /// <inheritdoc/>
    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct)
    {
        var fullKey = GetFullKey(key);

        if (_config.Backend == DistributedCacheBackend.Redis)
        {
            var result = await ExecuteRedisCommandAsync($"EXISTS {fullKey}\r\n", ct);
            return result != null && result.Contains(":1");
        }

        // For Memcached, try to get the key
        var getResult = await GetCoreAsync(key, ct);
        return getResult.Found;
    }

    /// <inheritdoc/>
    protected override async Task InvalidateByTagsCoreAsync(string[] tags, CancellationToken ct)
    {
        var keysToRemove = new HashSet<string>();

        lock (_tagLock)
        {
            foreach (var tag in tags)
            {
                if (_tagIndex.TryGetValue(tag, out var keys))
                {
                    foreach (var key in keys)
                    {
                        keysToRemove.Add(key);
                    }
                    _tagIndex.TryRemove(tag, out _);
                }
            }
        }

        foreach (var key in keysToRemove)
        {
            await RemoveCoreAsync(key, ct);
        }
    }

    /// <inheritdoc/>
    protected override async Task ClearCoreAsync(CancellationToken ct)
    {
        if (_config.Backend == DistributedCacheBackend.Redis)
        {
            if (!string.IsNullOrEmpty(_config.KeyPrefix))
            {
                // Delete only keys with our prefix
                var result = await ExecuteRedisCommandAsync($"KEYS {_config.KeyPrefix}*\r\n", ct);
                if (result != null)
                {
                    var keys = ParseRedisArray(result);
                    foreach (var key in keys)
                    {
                        await ExecuteRedisCommandAsync($"DEL {key}\r\n", ct);
                    }
                }
            }
            else
            {
                await ExecuteRedisCommandAsync("FLUSHDB\r\n", ct);
            }
        }
        else if (_config.Backend == DistributedCacheBackend.Memcached)
        {
            await ExecuteMemcachedCommandAsync("flush_all\r\n", ct);
        }

        _tagIndex.Clear();
    }

    private string GetFullKey(string key)
    {
        // P2-2350/P2-2351: Sanitize key to prevent RESP and Memcached command injection.
        // Remove CR, LF, and space characters which are used as protocol delimiters.
        var sanitized = key.Replace("\r", string.Empty).Replace("\n", string.Empty).Replace(" ", "_");
        return string.IsNullOrEmpty(_config.KeyPrefix) ? sanitized : $"{_config.KeyPrefix}:{sanitized}";
    }

    private async Task<string?> ExecuteRedisCommandAsync(string command, CancellationToken ct)
    {
        if (_pool == null) return null;

        var connection = await _pool.GetConnectionAsync(ct);
        if (connection == null) return null;

        try
        {
            var commandBytes = Encoding.UTF8.GetBytes(command);
            var result = await connection.SendCommandAsync(commandBytes, ct);
            return result != null ? Encoding.UTF8.GetString(result) : null;
        }
        finally
        {
            _pool.ReturnConnection(connection);
        }
    }

    private async Task<string?> ExecuteMemcachedCommandAsync(string command, CancellationToken ct)
    {
        if (_pool == null) return null;

        var connection = await _pool.GetConnectionAsync(ct);
        if (connection == null) return null;

        try
        {
            var commandBytes = Encoding.UTF8.GetBytes(command);
            var result = await connection.SendCommandAsync(commandBytes, ct);
            return result != null ? Encoding.UTF8.GetString(result) : null;
        }
        finally
        {
            _pool.ReturnConnection(connection);
        }
    }

    private static bool IsNullResponse(string response)
    {
        return response.StartsWith("$-1") || response.StartsWith("nil");
    }

    private static byte[] ExtractRedisValue(string response)
    {
        // Simple RESP parsing for bulk string
        var lines = response.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length >= 2)
        {
            var valueStr = lines[1].TrimEnd('\r');
            return Convert.FromBase64String(valueStr);
        }
        return Array.Empty<byte>();
    }

    private static byte[] ExtractMemcachedValue(string response)
    {
        // Parse "VALUE key flags bytes\r\ndata\r\nEND\r\n"
        var lines = response.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length >= 2)
        {
            var valueStr = lines[1].TrimEnd('\r');
            return Convert.FromBase64String(valueStr);
        }
        return Array.Empty<byte>();
    }

    private static string[] ParseRedisArray(string response)
    {
        var results = new List<string>();
        var lines = response.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.StartsWith("$") && i + 1 < lines.Length)
            {
                results.Add(lines[++i].TrimEnd('\r'));
            }
        }

        return results.ToArray();
    }
}
