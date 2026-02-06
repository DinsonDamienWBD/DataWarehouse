namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.Caching;

/// <summary>
/// Interface for the backing store used by write-through cache.
/// </summary>
public interface ICacheBackingStore
{
    /// <summary>
    /// Reads data from the backing store.
    /// </summary>
    Task<byte[]?> ReadAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// Writes data to the backing store.
    /// </summary>
    Task WriteAsync(string key, byte[] value, CancellationToken ct = default);

    /// <summary>
    /// Deletes data from the backing store.
    /// </summary>
    Task<bool> DeleteAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// Checks if data exists in the backing store.
    /// </summary>
    Task<bool> ExistsAsync(string key, CancellationToken ct = default);
}

/// <summary>
/// In-memory implementation of backing store for testing/demo purposes.
/// </summary>
public sealed class InMemoryBackingStore : ICacheBackingStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> _store = new();

    /// <inheritdoc/>
    public Task<byte[]?> ReadAsync(string key, CancellationToken ct = default)
    {
        _store.TryGetValue(key, out var value);
        return Task.FromResult(value);
    }

    /// <inheritdoc/>
    public Task WriteAsync(string key, byte[] value, CancellationToken ct = default)
    {
        _store[key] = value;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<bool> DeleteAsync(string key, CancellationToken ct = default)
    {
        return Task.FromResult(_store.TryRemove(key, out _));
    }

    /// <inheritdoc/>
    public Task<bool> ExistsAsync(string key, CancellationToken ct = default)
    {
        return Task.FromResult(_store.ContainsKey(key));
    }
}

/// <summary>
/// Synchronous write-through cache strategy.
/// All writes are immediately written to both cache and backing store.
/// Ensures strong consistency between cache and persistent storage.
/// </summary>
/// <remarks>
/// Features:
/// - Strong consistency guarantees
/// - Synchronous writes to backing store
/// - Automatic cache population on read
/// - Transaction-like semantics (both succeed or both fail)
/// - Suitable for write-heavy workloads requiring consistency
/// </remarks>
public sealed class WriteThruCacheStrategy : CachingStrategyBase
{
    private readonly InMemoryCacheStrategy _cache;
    private readonly ICacheBackingStore _backingStore;
    private readonly TimeSpan _defaultTTL;

    /// <summary>
    /// Initializes a new WriteThruCacheStrategy with default backing store.
    /// </summary>
    public WriteThruCacheStrategy() : this(new InMemoryBackingStore()) { }

    /// <summary>
    /// Initializes a new WriteThruCacheStrategy with specified backing store.
    /// </summary>
    /// <param name="backingStore">The backing store for persistent data.</param>
    /// <param name="cacheMaxSize">Maximum cache size in bytes.</param>
    /// <param name="defaultTTL">Default time-to-live for cache entries.</param>
    public WriteThruCacheStrategy(
        ICacheBackingStore backingStore,
        long cacheMaxSize = 128 * 1024 * 1024,
        TimeSpan? defaultTTL = null)
    {
        _backingStore = backingStore ?? throw new ArgumentNullException(nameof(backingStore));
        _cache = new InMemoryCacheStrategy(cacheMaxSize);
        _defaultTTL = defaultTTL ?? TimeSpan.FromMinutes(15);
    }

    /// <inheritdoc/>
    public override string StrategyId => "cache.writethru";

    /// <inheritdoc/>
    public override string DisplayName => "Write-Through Cache";

    /// <inheritdoc/>
    public override DataManagementCapabilities Capabilities { get; } = new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = false,
        SupportsTransactions = true,
        SupportsTTL = true,
        MaxThroughput = 10_000,
        TypicalLatencyMs = 5.0
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Synchronous write-through cache that writes to both cache and backing store simultaneously. " +
        "Provides strong consistency guarantees with immediate persistence. " +
        "Best for scenarios requiring consistent reads after writes.";

    /// <inheritdoc/>
    public override string[] Tags => ["cache", "writethru", "consistent", "persistent", "synchronous"];

    /// <inheritdoc/>
    public override long GetCurrentSize() => _cache.GetCurrentSize();

    /// <inheritdoc/>
    public override long GetEntryCount() => _cache.GetEntryCount();

    /// <inheritdoc/>
    protected override async Task InitializeCoreAsync(CancellationToken ct)
    {
        await _cache.InitializeAsync(ct);
    }

    /// <inheritdoc/>
    protected override async Task DisposeCoreAsync()
    {
        await _cache.DisposeAsync();
    }

    /// <inheritdoc/>
    protected override async Task<CacheResult<byte[]>> GetCoreAsync(string key, CancellationToken ct)
    {
        // Try cache first
        var cacheResult = await _cache.GetAsync(key, ct);
        if (cacheResult.Found)
        {
            return cacheResult;
        }

        // Cache miss - read from backing store
        var data = await _backingStore.ReadAsync(key, ct);
        if (data != null)
        {
            // Populate cache
            var options = new CacheOptions { TTL = _defaultTTL };
            await _cache.SetAsync(key, data, options, ct);
            return CacheResult<byte[]>.Hit(data, _defaultTTL);
        }

        return CacheResult<byte[]>.Miss();
    }

    /// <inheritdoc/>
    protected override async Task SetCoreAsync(string key, byte[] value, CacheOptions options, CancellationToken ct)
    {
        // Write-through: write to backing store first
        await _backingStore.WriteAsync(key, value, ct);

        // Then write to cache (if backing store succeeds)
        var cacheOptions = new CacheOptions
        {
            TTL = options.TTL ?? _defaultTTL,
            SlidingExpiration = options.SlidingExpiration,
            Priority = options.Priority,
            Tags = options.Tags
        };

        await _cache.SetAsync(key, value, cacheOptions, ct);
    }

    /// <inheritdoc/>
    protected override async Task<bool> RemoveCoreAsync(string key, CancellationToken ct)
    {
        // Remove from backing store first
        var backingResult = await _backingStore.DeleteAsync(key, ct);

        // Then remove from cache
        var cacheResult = await _cache.RemoveAsync(key, ct);

        return backingResult || cacheResult;
    }

    /// <inheritdoc/>
    protected override async Task<bool> ExistsCoreAsync(string key, CancellationToken ct)
    {
        // Check cache first
        if (await _cache.ExistsAsync(key, ct))
            return true;

        // Check backing store
        return await _backingStore.ExistsAsync(key, ct);
    }

    /// <inheritdoc/>
    protected override async Task InvalidateByTagsCoreAsync(string[] tags, CancellationToken ct)
    {
        // Only invalidate cache - backing store doesn't support tags
        await _cache.InvalidateByTagsAsync(tags, ct);
    }

    /// <inheritdoc/>
    protected override async Task ClearCoreAsync(CancellationToken ct)
    {
        // Only clear cache - backing store persists
        await _cache.ClearAsync(ct);
    }
}
