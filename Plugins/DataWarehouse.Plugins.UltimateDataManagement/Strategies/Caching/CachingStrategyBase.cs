using System.Diagnostics;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.Caching;

/// <summary>
/// Options for cache operations.
/// </summary>
public sealed class CacheOptions
{
    /// <summary>
    /// Time-to-live for cached items.
    /// </summary>
    public TimeSpan? TTL { get; init; }

    /// <summary>
    /// Sliding expiration window (resets on access).
    /// </summary>
    public TimeSpan? SlidingExpiration { get; init; }

    /// <summary>
    /// Cache priority for eviction decisions.
    /// </summary>
    public CachePriority Priority { get; init; } = CachePriority.Normal;

    /// <summary>
    /// Custom tags for cache grouping and invalidation.
    /// </summary>
    public string[]? Tags { get; init; }
}

/// <summary>
/// Cache entry priority for eviction decisions.
/// </summary>
public enum CachePriority
{
    /// <summary>
    /// Low priority - evicted first.
    /// </summary>
    Low = 0,

    /// <summary>
    /// Normal priority.
    /// </summary>
    Normal = 1,

    /// <summary>
    /// High priority - evicted last.
    /// </summary>
    High = 2,

    /// <summary>
    /// Never evict this entry.
    /// </summary>
    NeverRemove = 3
}

/// <summary>
/// Result of a cache get operation.
/// </summary>
/// <typeparam name="T">Type of cached value.</typeparam>
public sealed class CacheResult<T>
{
    /// <summary>
    /// Whether the value was found in the cache.
    /// </summary>
    public bool Found { get; init; }

    /// <summary>
    /// The cached value (default if not found).
    /// </summary>
    public T? Value { get; init; }

    /// <summary>
    /// Time until expiration (null if not found or no expiration).
    /// </summary>
    public TimeSpan? ExpiresIn { get; init; }

    /// <summary>
    /// Creates a hit result.
    /// </summary>
    public static CacheResult<T> Hit(T value, TimeSpan? expiresIn = null) =>
        new() { Found = true, Value = value, ExpiresIn = expiresIn };

    /// <summary>
    /// Creates a miss result.
    /// </summary>
    public static CacheResult<T> Miss() =>
        new() { Found = false };
}

/// <summary>
/// Interface for caching strategies.
/// </summary>
public interface ICachingStrategy : IDataManagementStrategy
{
    /// <summary>
    /// Gets a value from the cache.
    /// </summary>
    Task<CacheResult<byte[]>> GetAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// Sets a value in the cache.
    /// </summary>
    Task SetAsync(string key, byte[] value, CacheOptions? options = null, CancellationToken ct = default);

    /// <summary>
    /// Removes a value from the cache.
    /// </summary>
    Task<bool> RemoveAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// Checks if a key exists in the cache.
    /// </summary>
    Task<bool> ExistsAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// Gets or sets a value using a factory function if not found.
    /// </summary>
    Task<byte[]> GetOrSetAsync(string key, Func<CancellationToken, Task<byte[]>> factory, CacheOptions? options = null, CancellationToken ct = default);

    /// <summary>
    /// Removes all entries with the specified tags.
    /// </summary>
    Task InvalidateByTagsAsync(string[] tags, CancellationToken ct = default);

    /// <summary>
    /// Clears all entries from the cache.
    /// </summary>
    Task ClearAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets the current size of the cache in bytes.
    /// </summary>
    long GetCurrentSize();

    /// <summary>
    /// Gets the number of entries in the cache.
    /// </summary>
    long GetEntryCount();
}

/// <summary>
/// Abstract base class for caching strategies.
/// Provides common functionality for cache implementations.
/// </summary>
public abstract class CachingStrategyBase : DataManagementStrategyBase, ICachingStrategy
{
    /// <inheritdoc/>
    public override DataManagementCategory Category => DataManagementCategory.Caching;

    /// <inheritdoc/>
    public async Task<CacheResult<byte[]>> GetAsync(string key, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await GetCoreAsync(key, ct);
            sw.Stop();

            RecordRead(result.Found ? (result.Value?.Length ?? 0) : 0, sw.Elapsed.TotalMilliseconds,
                hit: result.Found, miss: !result.Found);

            return result;
        }
        catch
        {
            sw.Stop();
            RecordFailure();
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task SetAsync(string key, byte[] value, CacheOptions? options = null, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        var sw = Stopwatch.StartNew();
        try
        {
            await SetCoreAsync(key, value, options ?? new CacheOptions(), ct);
            sw.Stop();

            RecordWrite(value.Length, sw.Elapsed.TotalMilliseconds);
        }
        catch
        {
            sw.Stop();
            RecordFailure();
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<bool> RemoveAsync(string key, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await RemoveCoreAsync(key, ct);
            sw.Stop();

            RecordDelete(sw.Elapsed.TotalMilliseconds);
            return result;
        }
        catch
        {
            sw.Stop();
            RecordFailure();
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<bool> ExistsAsync(string key, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        return await ExistsCoreAsync(key, ct);
    }

    /// <inheritdoc/>
    public async Task<byte[]> GetOrSetAsync(string key, Func<CancellationToken, Task<byte[]>> factory,
        CacheOptions? options = null, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(factory);

        var result = await GetAsync(key, ct);
        if (result.Found && result.Value != null)
            return result.Value;

        var value = await factory(ct);
        await SetAsync(key, value, options, ct);
        return value;
    }

    /// <inheritdoc/>
    public async Task InvalidateByTagsAsync(string[] tags, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentNullException.ThrowIfNull(tags);

        await InvalidateByTagsCoreAsync(tags, ct);
    }

    /// <inheritdoc/>
    public async Task ClearAsync(CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        await ClearCoreAsync(ct);
    }

    /// <inheritdoc/>
    public abstract long GetCurrentSize();

    /// <inheritdoc/>
    public abstract long GetEntryCount();

    /// <summary>
    /// Core get implementation.
    /// </summary>
    protected abstract Task<CacheResult<byte[]>> GetCoreAsync(string key, CancellationToken ct);

    /// <summary>
    /// Core set implementation.
    /// </summary>
    protected abstract Task SetCoreAsync(string key, byte[] value, CacheOptions options, CancellationToken ct);

    /// <summary>
    /// Core remove implementation.
    /// </summary>
    protected abstract Task<bool> RemoveCoreAsync(string key, CancellationToken ct);

    /// <summary>
    /// Core exists implementation.
    /// </summary>
    protected abstract Task<bool> ExistsCoreAsync(string key, CancellationToken ct);

    /// <summary>
    /// Core tag invalidation implementation.
    /// </summary>
    protected virtual Task InvalidateByTagsCoreAsync(string[] tags, CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Core clear implementation.
    /// </summary>
    protected abstract Task ClearCoreAsync(CancellationToken ct);
}
