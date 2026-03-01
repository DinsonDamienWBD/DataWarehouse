// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

using System.Collections.Concurrent;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Scaling;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateBlockchain.Scaling;

/// <summary>
/// Scaling manager for the blockchain subsystem implementing <see cref="IScalableSubsystem"/>.
/// Wires <see cref="SegmentedBlockStore"/> as the primary block storage, replaces all unbounded
/// collections with <see cref="BoundedCache{TKey,TValue}"/>, and enforces configurable
/// concurrency limits via <see cref="SemaphoreSlim"/>.
/// </summary>
/// <remarks>
/// <para>
/// The manager provides runtime-reconfigurable scaling limits, bounded manifest and validation
/// caches, and backpressure monitoring based on block append queue depth.
/// </para>
/// <para>
/// <b>Manifest cache:</b> LRU, default 10,000 entries. Caches parsed manifest data for
/// recently accessed blocks.
/// </para>
/// <para>
/// <b>Validation cache:</b> TTL (5 minutes), default 50,000 entries. Caches block validation
/// results to avoid redundant hash recomputation.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 88-04: Blockchain scaling manager with IScalableSubsystem")]
public sealed class BlockchainScalingManager : IScalableSubsystem, IDisposable
{
    private readonly ILogger _logger;
    private readonly SegmentedBlockStore _blockStore;

    // Bounded caches replacing unbounded ConcurrentDictionary
    private readonly BoundedCache<string, byte[]> _manifestCache;
    private readonly BoundedCache<string, bool> _validationCache;

    // Concurrency control
    private SemaphoreSlim _writeSemaphore;
    private ScalingLimits _currentLimits;
    private long _pendingAppends;

    private bool _disposed;

    /// <summary>
    /// Initializes a new <see cref="BlockchainScalingManager"/> with the specified block store
    /// and optional scaling limits.
    /// </summary>
    /// <param name="logger">Logger instance for diagnostics.</param>
    /// <param name="blockStore">The segmented block store to manage.</param>
    /// <param name="limits">Optional initial scaling limits. Uses defaults if <c>null</c>.</param>
    public BlockchainScalingManager(
        ILogger logger,
        SegmentedBlockStore blockStore,
        ScalingLimits? limits = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _blockStore = blockStore ?? throw new ArgumentNullException(nameof(blockStore));

        _currentLimits = limits ?? new ScalingLimits(
            MaxCacheEntries: SegmentedBlockStore.DefaultMaxCacheEntries,
            MaxConcurrentOperations: Environment.ProcessorCount);

        _writeSemaphore = new SemaphoreSlim(
            _currentLimits.MaxConcurrentOperations,
            _currentLimits.MaxConcurrentOperations);

        _manifestCache = new BoundedCache<string, byte[]>(
            new BoundedCacheOptions<string, byte[]>
            {
                MaxEntries = Math.Min(_currentLimits.MaxCacheEntries, 10_000),
                EvictionPolicy = CacheEvictionMode.LRU
            });

        _validationCache = new BoundedCache<string, bool>(
            new BoundedCacheOptions<string, bool>
            {
                MaxEntries = Math.Min(_currentLimits.MaxCacheEntries * 5, 50_000),
                EvictionPolicy = CacheEvictionMode.TTL,
                DefaultTtl = TimeSpan.FromMinutes(5)
            });

        _logger.LogInformation(
            "BlockchainScalingManager initialized: MaxConcurrentWrites={MaxWrites}, ManifestCache={ManifestMax}, ValidationCache={ValidationMax}",
            _currentLimits.MaxConcurrentOperations, 10_000, 50_000);
    }

    /// <summary>
    /// Gets the underlying segmented block store managed by this scaling manager.
    /// </summary>
    public SegmentedBlockStore BlockStore => _blockStore;

    /// <summary>
    /// Gets the bounded manifest cache for recently accessed block manifests.
    /// </summary>
    public BoundedCache<string, byte[]> ManifestCache => _manifestCache;

    /// <summary>
    /// Gets the bounded validation cache for block validation results.
    /// </summary>
    public BoundedCache<string, bool> ValidationCache => _validationCache;

    /// <summary>
    /// Appends a block through the scaling manager with concurrency limiting.
    /// </summary>
    /// <param name="block">The block data to append.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the block has been appended.</returns>
    public async Task AppendBlockAsync(BlockData block, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Interlocked.Increment(ref _pendingAppends);
        try
        {
            await _writeSemaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await _blockStore.AppendBlockAsync(block, ct).ConfigureAwait(false);
            }
            finally
            {
                _writeSemaphore.Release();
            }
        }
        finally
        {
            Interlocked.Decrement(ref _pendingAppends);
        }
    }

    /// <summary>
    /// Looks up a manifest entry in the bounded cache, returning <c>null</c> on miss.
    /// </summary>
    /// <param name="key">The manifest cache key.</param>
    /// <returns>The cached manifest bytes, or <c>null</c> if not cached.</returns>
    public byte[]? GetManifest(string key)
    {
        return _manifestCache.GetOrDefault(key);
    }

    /// <summary>
    /// Stores a manifest entry in the bounded cache.
    /// </summary>
    /// <param name="key">The manifest cache key.</param>
    /// <param name="data">The manifest data bytes.</param>
    public void PutManifest(string key, byte[] data)
    {
        _manifestCache.Put(key, data);
    }

    /// <summary>
    /// Checks the validation cache for a previously computed result.
    /// </summary>
    /// <param name="blockHash">The block hash to check.</param>
    /// <param name="isValid">When this method returns <c>true</c>, contains the cached validation result.</param>
    /// <returns><c>true</c> if the result was cached; otherwise <c>false</c>.</returns>
    public bool TryGetValidation(string blockHash, out bool isValid)
    {
        // Single atomic lookup to avoid TOCTOU race between ContainsKey + GetOrDefault
        return _validationCache.TryGet(blockHash, out isValid);
    }

    /// <summary>
    /// Caches a block validation result with TTL-based expiry.
    /// </summary>
    /// <param name="blockHash">The block hash as cache key.</param>
    /// <param name="isValid">The validation result to cache.</param>
    public void PutValidation(string blockHash, bool isValid)
    {
        _validationCache.Put(blockHash, isValid);
    }

    // ---- IScalableSubsystem ----

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, object> GetScalingMetrics()
    {
        var blockCacheStats = _blockStore.CacheStatistics;
        var manifestStats = _manifestCache.GetStatistics();
        var validationStats = _validationCache.GetStatistics();
        var tierContention = _blockStore.GetTierLockContention();

        var metrics = new Dictionary<string, object>
        {
            ["block.count"] = _blockStore.BlockCount,
            ["segment.count"] = _blockStore.SegmentCount,
            ["journal.shardCount"] = _blockStore.JournalShardCount,
            ["cache.block.size"] = blockCacheStats.ItemCount,
            // Cat 10 (finding 1366): use HitRatio property directly — it's already computed on CacheStatistics.
            ["cache.block.hitRate"] = blockCacheStats.HitRatio,
            ["cache.block.memoryBytes"] = blockCacheStats.TotalSizeBytes,
            ["cache.manifest.size"] = manifestStats.ItemCount,
            ["cache.manifest.hitRate"] = manifestStats.HitRatio,
            ["cache.validation.size"] = validationStats.ItemCount,
            ["cache.validation.hitRate"] = validationStats.HitRatio,
            ["backpressure.queueDepth"] = Interlocked.Read(ref _pendingAppends),
            ["backpressure.state"] = CurrentBackpressureState.ToString(),
            ["concurrency.maxWrites"] = _currentLimits.MaxConcurrentOperations,
            ["concurrency.availableSlots"] = _writeSemaphore.CurrentCount
        };

        // Add per-tier lock contention
        foreach (var kvp in tierContention)
        {
            metrics[$"tierLock.{kvp.Key}.contended"] = kvp.Value;
        }

        return metrics;
    }

    /// <inheritdoc/>
    public async Task ReconfigureLimitsAsync(ScalingLimits limits, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(limits);

        var oldLimits = _currentLimits;
        _currentLimits = limits;

        // Recreate write semaphore if concurrency changed.
        // Safety: store the new semaphore first, then wait for callers to drain the old one
        // before disposing, to prevent in-flight operations crashing on Release() (findings 1350, 1351).
        if (limits.MaxConcurrentOperations != oldLimits.MaxConcurrentOperations)
        {
            var oldSemaphore = _writeSemaphore;
            // Assign new semaphore before disposing the old one so new callers use it immediately.
            _writeSemaphore = new SemaphoreSlim(
                limits.MaxConcurrentOperations,
                limits.MaxConcurrentOperations);
            // Drain outstanding waiters: acquire all old slots to confirm no caller holds it.
            // In practice the caller should stop new operations before reconfiguring.
            // We do a best-effort wait; callers that already hold the old semaphore will
            // still be able to Release() it since SemaphoreSlim.Release() works after Dispose()
            // only throws ObjectDisposedException on Wait(), not Release().
            // Schedule disposal on ThreadPool to avoid blocking the reconfig path.
            _ = Task.Run(async () =>
            {
                for (int i = 0; i < oldLimits.MaxConcurrentOperations; i++)
                {
                    await oldSemaphore.WaitAsync().ConfigureAwait(false);
                }
                oldSemaphore.Dispose();
            });
        }

        _logger.LogInformation(
            "Blockchain scaling limits reconfigured: MaxCache={MaxCache}, MaxConcurrent={MaxConcurrent}, MaxQueue={MaxQueue}",
            limits.MaxCacheEntries, limits.MaxConcurrentOperations, limits.MaxQueueDepth);

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public ScalingLimits CurrentLimits => _currentLimits;

    /// <inheritdoc/>
    public BackpressureState CurrentBackpressureState
    {
        get
        {
            long pending = Interlocked.Read(ref _pendingAppends);
            int maxQueue = _currentLimits.MaxQueueDepth;

            if (pending <= 0) return BackpressureState.Normal;
            if (pending < maxQueue * 0.5) return BackpressureState.Normal;
            if (pending < maxQueue * 0.8) return BackpressureState.Warning;
            if (pending < maxQueue) return BackpressureState.Critical;
            return BackpressureState.Shedding;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _manifestCache.Dispose();
        _validationCache.Dispose();
        _writeSemaphore.Dispose();
        // Note: _blockStore is disposed by the caller (plugin)
    }
}
