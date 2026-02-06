using System.Collections.Concurrent;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.Caching;

/// <summary>
/// L1 in-process cache using ConcurrentDictionary.
/// Provides the fastest possible cache access for single-process scenarios.
/// Thread-safe with automatic expiration support.
/// </summary>
/// <remarks>
/// Features:
/// - Zero network latency (in-process)
/// - Thread-safe via ConcurrentDictionary
/// - Automatic expiration with lazy cleanup
/// - Tag-based invalidation
/// - Memory pressure tracking
/// - LRU eviction when memory limit exceeded
/// </remarks>
public sealed class InMemoryCacheStrategy : CachingStrategyBase
{
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private readonly ConcurrentDictionary<string, HashSet<string>> _tagIndex = new();
    private readonly object _tagLock = new();
    private long _currentSize;
    private readonly long _maxSize;
    private readonly Timer _cleanupTimer;

    /// <summary>
    /// Cache entry with metadata.
    /// </summary>
    private sealed class CacheEntry
    {
        public byte[] Value { get; }
        public DateTime? AbsoluteExpiration { get; set; }
        public TimeSpan? SlidingExpiration { get; }
        public DateTime LastAccess { get; set; }
        public CachePriority Priority { get; }
        public string[]? Tags { get; }

        public CacheEntry(byte[] value, CacheOptions options)
        {
            Value = value;
            Priority = options.Priority;
            Tags = options.Tags;
            SlidingExpiration = options.SlidingExpiration;
            LastAccess = DateTime.UtcNow;

            if (options.TTL.HasValue)
                AbsoluteExpiration = DateTime.UtcNow.Add(options.TTL.Value);
        }

        public bool IsExpired =>
            AbsoluteExpiration.HasValue && DateTime.UtcNow > AbsoluteExpiration.Value;

        public TimeSpan? GetTimeToExpiration()
        {
            if (!AbsoluteExpiration.HasValue)
                return null;

            var remaining = AbsoluteExpiration.Value - DateTime.UtcNow;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }

        public void Touch()
        {
            LastAccess = DateTime.UtcNow;
            if (SlidingExpiration.HasValue)
                AbsoluteExpiration = DateTime.UtcNow.Add(SlidingExpiration.Value);
        }
    }

    /// <summary>
    /// Initializes a new InMemoryCacheStrategy with default 256MB limit.
    /// </summary>
    public InMemoryCacheStrategy() : this(256 * 1024 * 1024) { }

    /// <summary>
    /// Initializes a new InMemoryCacheStrategy with specified memory limit.
    /// </summary>
    /// <param name="maxSizeBytes">Maximum cache size in bytes.</param>
    public InMemoryCacheStrategy(long maxSizeBytes)
    {
        _maxSize = maxSizeBytes;
        _cleanupTimer = new Timer(CleanupExpired, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <inheritdoc/>
    public override string StrategyId => "cache.inmemory";

    /// <inheritdoc/>
    public override string DisplayName => "In-Memory Cache";

    /// <inheritdoc/>
    public override DataManagementCapabilities Capabilities { get; } = new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = false,
        SupportsTransactions = false,
        SupportsTTL = true,
        MaxThroughput = 1_000_000,
        TypicalLatencyMs = 0.001
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "L1 in-process cache using ConcurrentDictionary for ultra-low latency caching. " +
        "Provides thread-safe access with automatic expiration and LRU eviction. " +
        "Best for single-process scenarios requiring maximum performance.";

    /// <inheritdoc/>
    public override string[] Tags => ["cache", "inmemory", "l1", "fast", "local"];

    /// <inheritdoc/>
    public override long GetCurrentSize() => Interlocked.Read(ref _currentSize);

    /// <inheritdoc/>
    public override long GetEntryCount() => _cache.Count;

    /// <inheritdoc/>
    protected override Task<CacheResult<byte[]>> GetCoreAsync(string key, CancellationToken ct)
    {
        if (_cache.TryGetValue(key, out var entry))
        {
            if (entry.IsExpired)
            {
                RemoveEntry(key, entry);
                return Task.FromResult(CacheResult<byte[]>.Miss());
            }

            entry.Touch();
            return Task.FromResult(CacheResult<byte[]>.Hit(entry.Value, entry.GetTimeToExpiration()));
        }

        return Task.FromResult(CacheResult<byte[]>.Miss());
    }

    /// <inheritdoc/>
    protected override Task SetCoreAsync(string key, byte[] value, CacheOptions options, CancellationToken ct)
    {
        var entry = new CacheEntry(value, options);

        // Check if we need to evict entries
        EnsureCapacity(value.Length);

        if (_cache.TryGetValue(key, out var existing))
        {
            RemoveEntry(key, existing);
        }

        if (_cache.TryAdd(key, entry))
        {
            Interlocked.Add(ref _currentSize, value.Length);
            AddToTagIndex(key, options.Tags);
        }
        else
        {
            // Key was added concurrently, update it
            _cache[key] = entry;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task<bool> RemoveCoreAsync(string key, CancellationToken ct)
    {
        if (_cache.TryRemove(key, out var entry))
        {
            Interlocked.Add(ref _currentSize, -entry.Value.Length);
            RemoveFromTagIndex(key, entry.Tags);
            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    protected override Task<bool> ExistsCoreAsync(string key, CancellationToken ct)
    {
        if (_cache.TryGetValue(key, out var entry))
        {
            if (entry.IsExpired)
            {
                RemoveEntry(key, entry);
                return Task.FromResult(false);
            }
            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    protected override Task InvalidateByTagsCoreAsync(string[] tags, CancellationToken ct)
    {
        foreach (var tag in tags)
        {
            lock (_tagLock)
            {
                if (_tagIndex.TryGetValue(tag, out var keys))
                {
                    foreach (var key in keys.ToArray())
                    {
                        RemoveCoreAsync(key, ct).GetAwaiter().GetResult();
                    }
                }
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task ClearCoreAsync(CancellationToken ct)
    {
        _cache.Clear();
        _tagIndex.Clear();
        Interlocked.Exchange(ref _currentSize, 0);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task DisposeCoreAsync()
    {
        _cleanupTimer.Dispose();
        _cache.Clear();
        _tagIndex.Clear();
        return Task.CompletedTask;
    }

    private void RemoveEntry(string key, CacheEntry entry)
    {
        if (_cache.TryRemove(key, out _))
        {
            Interlocked.Add(ref _currentSize, -entry.Value.Length);
            RemoveFromTagIndex(key, entry.Tags);
        }
    }

    private void AddToTagIndex(string key, string[]? tags)
    {
        if (tags == null || tags.Length == 0) return;

        lock (_tagLock)
        {
            foreach (var tag in tags)
            {
                var keys = _tagIndex.GetOrAdd(tag, _ => new HashSet<string>());
                keys.Add(key);
            }
        }
    }

    private void RemoveFromTagIndex(string key, string[]? tags)
    {
        if (tags == null || tags.Length == 0) return;

        lock (_tagLock)
        {
            foreach (var tag in tags)
            {
                if (_tagIndex.TryGetValue(tag, out var keys))
                {
                    keys.Remove(key);
                    if (keys.Count == 0)
                        _tagIndex.TryRemove(tag, out _);
                }
            }
        }
    }

    private void EnsureCapacity(long requiredSize)
    {
        var currentSize = Interlocked.Read(ref _currentSize);
        if (currentSize + requiredSize <= _maxSize)
            return;

        // Evict entries using LRU until we have space
        var toEvict = _cache
            .Where(kvp => kvp.Value.Priority != CachePriority.NeverRemove)
            .OrderBy(kvp => kvp.Value.Priority)
            .ThenBy(kvp => kvp.Value.LastAccess)
            .Take(Math.Max(1, _cache.Count / 10)) // Evict 10% at a time
            .ToList();

        foreach (var kvp in toEvict)
        {
            RemoveEntry(kvp.Key, kvp.Value);

            if (Interlocked.Read(ref _currentSize) + requiredSize <= _maxSize)
                break;
        }
    }

    private void CleanupExpired(object? state)
    {
        var expired = _cache
            .Where(kvp => kvp.Value.IsExpired)
            .Take(1000) // Limit cleanup batch size
            .ToList();

        foreach (var kvp in expired)
        {
            RemoveEntry(kvp.Key, kvp.Value);
        }
    }
}
