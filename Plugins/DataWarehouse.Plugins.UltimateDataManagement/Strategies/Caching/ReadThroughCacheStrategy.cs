using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.Caching;

/// <summary>
/// Data loader delegate for read-through cache.
/// </summary>
/// <param name="key">Cache key.</param>
/// <param name="ct">Cancellation token.</param>
/// <returns>Loaded data or null if not found.</returns>
public delegate Task<byte[]?> DataLoaderDelegate(string key, CancellationToken ct);

/// <summary>
/// Write-through callback delegate.
/// </summary>
/// <param name="key">Cache key.</param>
/// <param name="value">Value to write.</param>
/// <param name="ct">Cancellation token.</param>
public delegate Task WriteBackDelegate(string key, byte[] value, CancellationToken ct);

/// <summary>
/// Configuration for read-through cache.
/// </summary>
public sealed class ReadThroughCacheConfig
{
    /// <summary>
    /// Default TTL for cached entries.
    /// </summary>
    public TimeSpan DefaultTTL { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Maximum cache size in bytes.
    /// </summary>
    public long MaxCacheSize { get; init; } = 512 * 1024 * 1024; // 512MB

    /// <summary>
    /// Whether to enable write-through.
    /// </summary>
    public bool EnableWriteThrough { get; init; }

    /// <summary>
    /// Whether to refresh entries on access.
    /// </summary>
    public bool RefreshOnAccess { get; init; } = true;

    /// <summary>
    /// Threshold for background refresh (percentage of TTL remaining).
    /// </summary>
    public double BackgroundRefreshThreshold { get; init; } = 0.2;

    /// <summary>
    /// Maximum concurrent loader calls.
    /// </summary>
    public int MaxConcurrentLoads { get; init; } = 10;

    /// <summary>
    /// Loader timeout.
    /// </summary>
    public TimeSpan LoaderTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Cache entry with loading state.
/// </summary>
internal sealed class ReadThroughEntry
{
    public byte[]? Value { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime LastAccess { get; set; }
    public DateTime LoadedAt { get; set; }
    public bool IsLoading { get; set; }
    public TaskCompletionSource<byte[]?>? LoadingTask { get; set; }
    public CachePriority Priority { get; set; }
    public string[]? Tags { get; set; }
}

/// <summary>
/// Read-through cache strategy with auto-populate on cache miss.
/// Supports write-through option and configurable TTL.
/// </summary>
/// <remarks>
/// Features:
/// - Automatic cache population on miss
/// - Configurable TTL per entry
/// - Write-through support
/// - Background refresh for hot keys
/// - Concurrent load protection (single-flight)
/// - LRU eviction
/// </remarks>
public sealed class ReadThroughCacheStrategy : CachingStrategyBase
{
    private readonly BoundedDictionary<string, ReadThroughEntry> _cache = new BoundedDictionary<string, ReadThroughEntry>(1000);
    private readonly BoundedDictionary<string, HashSet<string>> _tagIndex = new BoundedDictionary<string, HashSet<string>>(1000);
    private readonly object _tagLock = new();
    private readonly SemaphoreSlim _loadSemaphore;
    private readonly ReadThroughCacheConfig _config;
    private DataLoaderDelegate? _dataLoader;
    private WriteBackDelegate? _writeBack;
    private long _currentSize;
    private readonly Timer _cleanupTimer;

    /// <summary>
    /// Initializes a new ReadThroughCacheStrategy.
    /// </summary>
    public ReadThroughCacheStrategy() : this(new ReadThroughCacheConfig()) { }

    /// <summary>
    /// Initializes a new ReadThroughCacheStrategy with configuration.
    /// </summary>
    /// <param name="config">Cache configuration.</param>
    public ReadThroughCacheStrategy(ReadThroughCacheConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _loadSemaphore = new SemaphoreSlim(_config.MaxConcurrentLoads);
        _cleanupTimer = new Timer(CleanupExpired, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <inheritdoc/>
    public override string StrategyId => "cache.read-through";

    /// <inheritdoc/>
    public override string DisplayName => "Read-Through Cache";

    /// <inheritdoc/>
    public override DataManagementCapabilities Capabilities { get; } = new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = false,
        SupportsTransactions = false,
        SupportsTTL = true,
        MaxThroughput = 500_000,
        TypicalLatencyMs = 0.1
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Read-through cache strategy that automatically populates cache on miss. " +
        "Supports write-through, background refresh, and LRU eviction.";

    /// <inheritdoc/>
    public override string[] Tags => ["cache", "read-through", "cache-aside", "auto-populate"];

    /// <inheritdoc/>
    public override long GetCurrentSize() => Interlocked.Read(ref _currentSize);

    /// <inheritdoc/>
    public override long GetEntryCount() => _cache.Count;

    /// <summary>
    /// Sets the data loader for cache misses.
    /// </summary>
    /// <param name="loader">Data loader delegate.</param>
    public void SetDataLoader(DataLoaderDelegate loader)
    {
        _dataLoader = loader ?? throw new ArgumentNullException(nameof(loader));
    }

    /// <summary>
    /// Sets the write-back handler for write-through mode.
    /// </summary>
    /// <param name="writeBack">Write-back delegate.</param>
    public void SetWriteBackHandler(WriteBackDelegate writeBack)
    {
        _writeBack = writeBack ?? throw new ArgumentNullException(nameof(writeBack));
    }

    /// <inheritdoc/>
    protected override async Task<CacheResult<byte[]>> GetCoreAsync(string key, CancellationToken ct)
    {
        // Check if entry exists and is valid
        if (_cache.TryGetValue(key, out var entry))
        {
            if (entry.IsLoading && entry.LoadingTask != null)
            {
                // Wait for ongoing load
                var value = await entry.LoadingTask.Task;
                if (value != null)
                {
                    return CacheResult<byte[]>.Hit(value, GetTimeToExpiration(entry));
                }
                return CacheResult<byte[]>.Miss();
            }

            if (entry.Value != null && !IsExpired(entry))
            {
                entry.LastAccess = DateTime.UtcNow;

                // Check if we should background refresh
                if (ShouldBackgroundRefresh(entry))
                {
                    _ = BackgroundRefreshAsync(key, entry.Tags, ct);
                }

                return CacheResult<byte[]>.Hit(entry.Value, GetTimeToExpiration(entry));
            }
        }

        // Cache miss - load from source
        if (_dataLoader != null)
        {
            var value = await LoadWithDeduplicationAsync(key, ct);
            if (value != null)
            {
                return CacheResult<byte[]>.Hit(value);
            }
        }

        return CacheResult<byte[]>.Miss();
    }

    /// <inheritdoc/>
    protected override async Task SetCoreAsync(string key, byte[] value, CacheOptions options, CancellationToken ct)
    {
        EnsureCapacity(value.Length);

        var ttl = options.TTL ?? _config.DefaultTTL;
        var entry = new ReadThroughEntry
        {
            Value = value,
            ExpiresAt = ttl > TimeSpan.Zero ? DateTime.UtcNow.Add(ttl) : null,
            LastAccess = DateTime.UtcNow,
            LoadedAt = DateTime.UtcNow,
            Priority = options.Priority,
            Tags = options.Tags
        };

        if (_cache.TryGetValue(key, out var existing))
        {
            Interlocked.Add(ref _currentSize, -(existing.Value?.Length ?? 0));
        }

        _cache[key] = entry;
        Interlocked.Add(ref _currentSize, value.Length);

        // Update tag index
        if (options.Tags != null)
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

        // Write-through if enabled
        if (_config.EnableWriteThrough && _writeBack != null)
        {
            await _writeBack(key, value, ct);
        }
    }

    /// <inheritdoc/>
    protected override Task<bool> RemoveCoreAsync(string key, CancellationToken ct)
    {
        if (_cache.TryRemove(key, out var entry))
        {
            Interlocked.Add(ref _currentSize, -(entry.Value?.Length ?? 0));
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
            if (!IsExpired(entry))
            {
                return Task.FromResult(true);
            }
            // Remove expired entry
            _cache.TryRemove(key, out _);
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
                if (_tagIndex.TryRemove(tag, out var keys))
                {
                    foreach (var key in keys)
                    {
                        if (_cache.TryRemove(key, out var entry))
                        {
                            Interlocked.Add(ref _currentSize, -(entry.Value?.Length ?? 0));
                        }
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
        _loadSemaphore.Dispose();
        _cache.Clear();
        _tagIndex.Clear();
        return Task.CompletedTask;
    }

    private async Task<byte[]?> LoadWithDeduplicationAsync(string key, CancellationToken ct)
    {
        var newEntry = new ReadThroughEntry
        {
            IsLoading = true,
            LoadingTask = new TaskCompletionSource<byte[]?>(),
            LastAccess = DateTime.UtcNow
        };

        // Try to add entry with loading state
        var existingEntry = _cache.GetOrAdd(key, newEntry);

        if (existingEntry != newEntry)
        {
            // Another thread is already loading or has loaded
            if (existingEntry.IsLoading && existingEntry.LoadingTask != null)
            {
                return await existingEntry.LoadingTask.Task;
            }
            if (existingEntry.Value != null && !IsExpired(existingEntry))
            {
                return existingEntry.Value;
            }
        }

        // We're responsible for loading
        try
        {
            await _loadSemaphore.WaitAsync(ct);
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(_config.LoaderTimeout);

                var value = await _dataLoader!(key, cts.Token);

                if (value != null)
                {
                    EnsureCapacity(value.Length);

                    newEntry.Value = value;
                    newEntry.ExpiresAt = DateTime.UtcNow.Add(_config.DefaultTTL);
                    newEntry.LoadedAt = DateTime.UtcNow;
                    newEntry.IsLoading = false;

                    Interlocked.Add(ref _currentSize, value.Length);
                }
                else
                {
                    _cache.TryRemove(key, out _);
                }

                newEntry.LoadingTask?.TrySetResult(value);
                return value;
            }
            finally
            {
                _loadSemaphore.Release();
            }
        }
        catch (Exception ex)
        {
            newEntry.LoadingTask?.TrySetException(ex);
            _cache.TryRemove(key, out _);
            throw;
        }
    }

    private async Task BackgroundRefreshAsync(string key, string[]? tags, CancellationToken ct)
    {
        if (_dataLoader == null) return;

        try
        {
            await _loadSemaphore.WaitAsync(ct);
            try
            {
                var value = await _dataLoader(key, ct);
                if (value != null)
                {
                    await SetCoreAsync(key, value, new CacheOptions
                    {
                        TTL = _config.DefaultTTL,
                        Tags = tags
                    }, ct);
                }
            }
            finally
            {
                _loadSemaphore.Release();
            }
        }
        catch
        {

            // Background refresh failures are non-fatal
            System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
        }
    }

    private bool ShouldBackgroundRefresh(ReadThroughEntry entry)
    {
        if (!_config.RefreshOnAccess || entry.ExpiresAt == null)
            return false;

        var remaining = entry.ExpiresAt.Value - DateTime.UtcNow;
        var totalTtl = entry.ExpiresAt.Value - entry.LoadedAt;

        return remaining.TotalSeconds > 0 &&
               remaining.TotalSeconds / totalTtl.TotalSeconds < _config.BackgroundRefreshThreshold;
    }

    private static bool IsExpired(ReadThroughEntry entry)
    {
        return entry.ExpiresAt.HasValue && DateTime.UtcNow > entry.ExpiresAt.Value;
    }

    private static TimeSpan? GetTimeToExpiration(ReadThroughEntry entry)
    {
        if (!entry.ExpiresAt.HasValue) return null;
        var remaining = entry.ExpiresAt.Value - DateTime.UtcNow;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private void EnsureCapacity(long requiredSize)
    {
        var currentSize = Interlocked.Read(ref _currentSize);
        if (currentSize + requiredSize <= _config.MaxCacheSize)
            return;

        // Evict entries using LRU
        var toEvict = _cache
            .Where(kvp => kvp.Value.Priority != CachePriority.NeverRemove && !kvp.Value.IsLoading)
            .OrderBy(kvp => kvp.Value.Priority)
            .ThenBy(kvp => kvp.Value.LastAccess)
            .Take(Math.Max(1, _cache.Count / 10))
            .ToList();

        foreach (var kvp in toEvict)
        {
            if (_cache.TryRemove(kvp.Key, out var entry))
            {
                Interlocked.Add(ref _currentSize, -(entry.Value?.Length ?? 0));
                RemoveFromTagIndex(kvp.Key, entry.Tags);
            }

            if (Interlocked.Read(ref _currentSize) + requiredSize <= _config.MaxCacheSize)
                break;
        }
    }

    private void RemoveFromTagIndex(string key, string[]? tags)
    {
        if (tags == null) return;

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

    private void CleanupExpired(object? state)
    {
        var expired = _cache
            .Where(kvp => IsExpired(kvp.Value))
            .Take(1000)
            .ToList();

        foreach (var kvp in expired)
        {
            if (_cache.TryRemove(kvp.Key, out var entry))
            {
                Interlocked.Add(ref _currentSize, -(entry.Value?.Length ?? 0));
                RemoveFromTagIndex(kvp.Key, entry.Tags);
            }
        }
    }
}
