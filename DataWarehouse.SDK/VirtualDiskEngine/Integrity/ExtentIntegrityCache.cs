using System.Collections.Concurrent;
using System.Threading;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Integrity;

/// <summary>
/// Statistics snapshot for the <see cref="ExtentIntegrityCache"/>.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: Extent integrity cache statistics (VOPT-52)")]
public readonly struct IntegrityCacheStats
{
    /// <summary>Number of entries currently stored in the cache.</summary>
    public int TotalEntries { get; }

    /// <summary>Cumulative number of successful cache lookups (generation + TTL valid).</summary>
    public long Hits { get; }

    /// <summary>Cumulative number of cache misses (entry absent, stale generation, or expired TTL).</summary>
    public long Misses { get; }

    /// <summary>Cumulative number of entries evicted by the LRU eviction sweep.</summary>
    public long Evictions { get; }

    /// <summary>
    /// Ratio of <see cref="Hits"/> to total lookups.
    /// Returns 0 when no lookups have been performed.
    /// </summary>
    public double HitRate => (Hits + Misses) == 0 ? 0.0 : (double)Hits / (Hits + Misses);

    /// <summary>Creates a new statistics snapshot.</summary>
    public IntegrityCacheStats(int totalEntries, long hits, long misses, long evictions)
    {
        TotalEntries = totalEntries;
        Hits = hits;
        Misses = misses;
        Evictions = evictions;
    }
}

/// <summary>
/// In-memory cache that stores verified integrity results for extents.
/// Generation tracking (via <c>UniversalBlockTrailer.GenerationNumber</c>) invalidates
/// cache entries when extents are modified, so unchanged extents skip re-verification
/// on subsequent reads and dramatically reduce I/O on the integrity hot path.
///
/// Thread-safety: all public members are safe for concurrent use.
/// </summary>
/// <remarks>
/// The cache uses an approximate LRU policy: a <see cref="ConcurrentQueue{T}"/> tracks
/// insertion order; when the entry count exceeds <see cref="IntegrityCacheConfig.MaxEntries"/>
/// an eviction sweep removes a configurable batch of the oldest candidates.
/// Because items may be re-verified between eviction sweeps the policy is approximate,
/// but it is lock-free and low-overhead.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 87: Extent integrity cache with generation tracking (VOPT-52)")]
public sealed class ExtentIntegrityCache
{
    // ── Inner types ───────────────────────────────────────────────────────

    /// <summary>
    /// A single cached verification result for one extent, keyed by
    /// <see cref="StartBlock"/>.
    /// </summary>
    public readonly struct CacheEntry
    {
        /// <summary>Physical start block of the extent (cache key).</summary>
        public long StartBlock { get; }

        /// <summary>Number of contiguous blocks in the extent.</summary>
        public int BlockCount { get; }

        /// <summary>
        /// Value of <c>UniversalBlockTrailer.GenerationNumber</c> at the time
        /// the extent was verified. A different current generation means the
        /// extent has been written and this entry is stale.
        /// </summary>
        public uint VerifiedGeneration { get; }

        /// <summary>
        /// <see langword="true"/> if the integrity check passed when this entry
        /// was recorded; <see langword="false"/> if corruption was detected.
        /// </summary>
        public bool IsVerified { get; }

        /// <summary>Wall-clock timestamp when verification was performed.</summary>
        public DateTimeOffset VerifiedAt { get; }

        /// <summary>
        /// Cached BLAKE3 / XxHash64 hash of the extent (up to 16 bytes), used
        /// for quick hash-level comparison before a full re-read.
        /// <see langword="null"/> when the caller did not supply a hash.
        /// </summary>
        public byte[]? ExpectedHash { get; }

        /// <summary>Creates a new cache entry.</summary>
        public CacheEntry(
            long startBlock,
            int blockCount,
            uint verifiedGeneration,
            bool isVerified,
            DateTimeOffset verifiedAt,
            byte[]? expectedHash)
        {
            StartBlock = startBlock;
            BlockCount = blockCount;
            VerifiedGeneration = verifiedGeneration;
            IsVerified = isVerified;
            VerifiedAt = verifiedAt;
            ExpectedHash = expectedHash;
        }
    }

    // ── Fields ────────────────────────────────────────────────────────────

    private readonly IntegrityCacheConfig _config;

    /// <summary>Primary store: StartBlock -> CacheEntry.</summary>
    private readonly ConcurrentDictionary<long, CacheEntry> _cache;

    /// <summary>Approximate LRU ordering for eviction candidates.</summary>
    private readonly ConcurrentQueue<long> _lruQueue;

    // Thread-safe counters
    private long _hits;
    private long _misses;
    private long _evictions;

    // ── Constructor ───────────────────────────────────────────────────────

    /// <summary>
    /// Creates a new <see cref="ExtentIntegrityCache"/> with the supplied configuration.
    /// </summary>
    /// <param name="config">Cache configuration. Must not be <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="config"/> is <see langword="null"/>.
    /// </exception>
    public ExtentIntegrityCache(IntegrityCacheConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _cache = new ConcurrentDictionary<long, CacheEntry>();
        _lruQueue = new ConcurrentQueue<long>();
    }

    // ── Core operations ───────────────────────────────────────────────────

    /// <summary>
    /// Attempts to retrieve a valid (non-stale, non-expired) cache entry for the
    /// given extent start block.
    /// </summary>
    /// <param name="startBlock">Physical start block of the extent.</param>
    /// <param name="currentGeneration">
    /// The current <c>GenerationNumber</c> read from the block's
    /// <c>UniversalBlockTrailer</c>. Must match the cached generation for a hit.
    /// </param>
    /// <param name="entry">
    /// The cached entry when the method returns <see langword="true"/>; default otherwise.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if a valid, unexpired, generation-matching entry was found;
    /// <see langword="false"/> on any miss (absent, stale generation, or TTL expired).
    /// </returns>
    public bool TryGetVerified(long startBlock, uint currentGeneration, out CacheEntry entry)
    {
        if (_cache.TryGetValue(startBlock, out entry))
        {
            // Generation mismatch -> extent was written since last verification
            if (entry.VerifiedGeneration != currentGeneration)
            {
                _cache.TryRemove(startBlock, out _);
                Interlocked.Increment(ref _misses);
                entry = default;
                return false;
            }

            // TTL expiry -> evict and treat as miss
            if (DateTimeOffset.UtcNow - entry.VerifiedAt > _config.EntryTtl)
            {
                _cache.TryRemove(startBlock, out _);
                Interlocked.Increment(ref _misses);
                entry = default;
                return false;
            }

            Interlocked.Increment(ref _hits);
            return true;
        }

        Interlocked.Increment(ref _misses);
        entry = default;
        return false;
    }

    /// <summary>
    /// Records a new (or updated) verification result for the given extent.
    /// Triggers LRU eviction when the cache is full.
    /// </summary>
    /// <param name="startBlock">Physical start block of the extent.</param>
    /// <param name="blockCount">Number of contiguous blocks in the extent.</param>
    /// <param name="generation">
    /// Current <c>GenerationNumber</c> from the block's <c>UniversalBlockTrailer</c>.
    /// </param>
    /// <param name="isVerified">
    /// <see langword="true"/> if the integrity check passed; <see langword="false"/> if
    /// corruption was detected.
    /// </param>
    /// <param name="expectedHash">
    /// Optional cached hash (BLAKE3/XxHash64, up to 16 bytes) for quick comparison.
    /// Pass <see langword="null"/> when unavailable or when
    /// <see cref="IntegrityCacheConfig.TrackBlockLevelResults"/> is disabled.
    /// </param>
    public void RecordVerification(
        long startBlock,
        int blockCount,
        uint generation,
        bool isVerified,
        byte[]? expectedHash)
    {
        var newEntry = new CacheEntry(
            startBlock,
            blockCount,
            generation,
            isVerified,
            DateTimeOffset.UtcNow,
            expectedHash);

        _cache[startBlock] = newEntry;
        _lruQueue.Enqueue(startBlock);

        if (_cache.Count > _config.MaxEntries)
            EvictOldest();
    }

    /// <summary>
    /// Removes a specific extent entry from the cache.
    /// Should be called on every write to the extent so the next read forces
    /// a fresh verification.
    /// </summary>
    /// <param name="startBlock">Physical start block of the extent to invalidate.</param>
    public void InvalidateExtent(long startBlock)
        => _cache.TryRemove(startBlock, out _);

    /// <summary>
    /// Bulk-invalidates all entries whose <c>VerifiedGeneration</c> is strictly
    /// less than <paramref name="olderThanGeneration"/>.
    /// Useful after a compaction or mount-time replay that advances the global
    /// generation counter by a large step.
    /// </summary>
    /// <param name="olderThanGeneration">
    /// Generation threshold (exclusive). Entries with
    /// <c>VerifiedGeneration &lt; olderThanGeneration</c> are removed.
    /// </param>
    public void InvalidateByGeneration(uint olderThanGeneration)
    {
        foreach (var kvp in _cache)
        {
            if (kvp.Value.VerifiedGeneration < olderThanGeneration)
                _cache.TryRemove(kvp.Key, out _);
        }
    }

    /// <summary>
    /// Removes all entries from the cache. Thread-safe.
    /// </summary>
    public void Clear()
    {
        _cache.Clear();
        // Drain the LRU queue so it doesn't hold stale keys.
        while (_lruQueue.TryDequeue(out _)) { }
    }

    /// <summary>
    /// Returns a point-in-time statistics snapshot.
    /// </summary>
    public IntegrityCacheStats GetStats()
        => new IntegrityCacheStats(
            _cache.Count,
            Interlocked.Read(ref _hits),
            Interlocked.Read(ref _misses),
            Interlocked.Read(ref _evictions));

    // ── LRU eviction ──────────────────────────────────────────────────────

    /// <summary>
    /// Dequeues up to <see cref="IntegrityCacheConfig.EvictionBatchSize"/> candidates
    /// from the LRU queue and removes them from the dictionary.
    /// Entries that were re-verified after being queued may still be present in the
    /// dictionary; removing them is safe because a re-verification will repopulate them.
    /// </summary>
    private void EvictOldest()
    {
        int evicted = 0;
        int batchSize = _config.EvictionBatchSize;

        while (evicted < batchSize && _lruQueue.TryDequeue(out long startBlock))
        {
            if (_cache.TryRemove(startBlock, out _))
            {
                Interlocked.Increment(ref _evictions);
                evicted++;
            }
            // Key not found: entry was already evicted or invalidated — skip and continue.
        }
    }
}
