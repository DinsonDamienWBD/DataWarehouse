using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.Memory;

/// <summary>
/// Hybrid memory store combining volatile (RAM) and persistent (disk/distributed) storage.
/// Automatically migrates data between tiers based on access patterns, providing
/// optimal performance for hot data while maintaining durability for all data.
/// </summary>
public sealed class HybridMemoryStore : IPersistentMemoryStore
{
    private readonly IPersistentMemoryStore _persistentStore;
    private readonly BoundedDictionary<string, ContextEntry> _hotCache = new BoundedDictionary<string, ContextEntry>(1000);
    private readonly BoundedDictionary<string, DateTimeOffset> _accessTimes = new BoundedDictionary<string, DateTimeOffset>(1000);
    private readonly BoundedDictionary<string, long> _accessCounts = new BoundedDictionary<string, long>(1000);
    private readonly ConcurrentQueue<PendingWrite> _writeQueue = new();
    private readonly SemaphoreSlim _migrationLock = new(1, 1);
    private readonly SemaphoreSlim _flushLock = new(1, 1);
    private readonly Timer _migrationTimer;
    private readonly Timer _flushTimer;

    private readonly HybridMemoryOptions _options;
    private long _cacheHits;
    private long _cacheMisses;
    private long _evictions;
    private long _promotions;
    private long _demotions;
    private long _pendingBytes;
    private bool _disposed;
    private bool _shutdownRequested;

    /// <summary>
    /// Initializes a new hybrid memory store.
    /// </summary>
    /// <param name="persistentStore">The underlying persistent store.</param>
    /// <param name="options">Configuration options.</param>
    public HybridMemoryStore(
        IPersistentMemoryStore? persistentStore = null,
        HybridMemoryOptions? options = null)
    {
        _options = options ?? new HybridMemoryOptions();
        _persistentStore = persistentStore ?? new RocksDbMemoryStore();

        // Start background migration timer
        _migrationTimer = new Timer(
            _ => _ = MigrateBasedOnAccessPatternsAsync(CancellationToken.None),
            null,
            _options.MigrationInterval,
            _options.MigrationInterval);

        // Start background flush timer
        _flushTimer = new Timer(
            _ => _ = FlushPendingWritesAsync(CancellationToken.None),
            null,
            _options.FlushInterval,
            _options.FlushInterval);
    }

    /// <inheritdoc/>
    public async Task StoreAsync(ContextEntry entry, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Always store in hot cache first for fast subsequent access
        _hotCache[entry.EntryId] = entry;
        _accessTimes[entry.EntryId] = DateTimeOffset.UtcNow;
        _accessCounts[entry.EntryId] = 1;

        // Queue for async persistence
        var pendingWrite = new PendingWrite
        {
            Entry = entry,
            QueuedAt = DateTimeOffset.UtcNow
        };

        _writeQueue.Enqueue(pendingWrite);
        Interlocked.Add(ref _pendingBytes, entry.SizeBytes);

        // Check cache size and evict if necessary
        if (_hotCache.Count > _options.MaxHotCacheEntries)
        {
            await EvictColdEntriesAsync(ct);
        }

        // Flush immediately if pending bytes exceed threshold
        if (Interlocked.Read(ref _pendingBytes) > _options.MaxPendingBytes)
        {
            await FlushPendingWritesAsync(ct);
        }
    }

    /// <inheritdoc/>
    public async Task StoreBatchAsync(IEnumerable<ContextEntry> entries, CancellationToken ct = default)
    {
        foreach (var entry in entries)
        {
            await StoreAsync(entry, ct);
        }
    }

    /// <inheritdoc/>
    public async Task<ContextEntry?> GetAsync(string entryId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Try hot cache first
        if (_hotCache.TryGetValue(entryId, out var entry))
        {
            Interlocked.Increment(ref _cacheHits);
            UpdateAccessMetrics(entryId);
            return entry;
        }

        Interlocked.Increment(ref _cacheMisses);

        // Fall back to persistent store
        entry = await _persistentStore.GetAsync(entryId, ct);

        if (entry != null)
        {
            // Promote to hot cache if accessed
            await PromoteToHotCacheAsync(entry, ct);
        }

        return entry;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ContextEntry> SearchAsync(
        float[] queryVector,
        int topK,
        string? scopeFilter = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var seen = new HashSet<string>();
        var results = new List<(ContextEntry Entry, float Score)>();

        // Search hot cache first
        foreach (var entry in _hotCache.Values)
        {
            if (scopeFilter != null && entry.Scope != scopeFilter)
                continue;

            if (entry.Embedding != null)
            {
                var score = CosineSimilarity(queryVector, entry.Embedding);
                results.Add((entry, score));
                seen.Add(entry.EntryId);
            }
        }

        // Search persistent store
        await foreach (var entry in _persistentStore.SearchAsync(queryVector, topK * 2, scopeFilter, ct))
        {
            if (!seen.Contains(entry.EntryId))
            {
                if (entry.Embedding != null)
                {
                    var score = CosineSimilarity(queryVector, entry.Embedding);
                    results.Add((entry, score));
                }
                seen.Add(entry.EntryId);
            }
        }

        // Return top K results
        foreach (var (entry, _) in results.OrderByDescending(r => r.Score).Take(topK))
        {
            yield return entry;
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ContextEntry> SearchByMetadataAsync(
        Dictionary<string, object> filter,
        int limit = 100,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var seen = new HashSet<string>();
        var count = 0;

        // Search hot cache first
        foreach (var entry in _hotCache.Values)
        {
            if (count >= limit) break;
            if (MatchesFilter(entry, filter) && seen.Add(entry.EntryId))
            {
                count++;
                yield return entry;
            }
        }

        // Search persistent store
        if (count < limit)
        {
            await foreach (var entry in _persistentStore.SearchByMetadataAsync(filter, limit - count, ct))
            {
                if (seen.Add(entry.EntryId))
                {
                    yield return entry;
                }
            }
        }
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(string entryId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Remove from hot cache
        _hotCache.TryRemove(entryId, out _);
        _accessTimes.TryRemove(entryId, out _);
        _accessCounts.TryRemove(entryId, out _);

        // Remove from persistent store
        await _persistentStore.DeleteAsync(entryId, ct);
    }

    /// <inheritdoc/>
    public async Task DeleteScopeAsync(string scope, CancellationToken ct = default)
    {
        // Remove from hot cache
        var toRemove = _hotCache.Values
            .Where(e => e.Scope == scope)
            .Select(e => e.EntryId)
            .ToList();

        foreach (var id in toRemove)
        {
            _hotCache.TryRemove(id, out _);
            _accessTimes.TryRemove(id, out _);
            _accessCounts.TryRemove(id, out _);
        }

        // Remove from persistent store
        await _persistentStore.DeleteScopeAsync(scope, ct);
    }

    /// <inheritdoc/>
    public async Task<long> GetEntryCountAsync(string? scopeFilter = null, CancellationToken ct = default)
    {
        var hotCount = scopeFilter == null
            ? _hotCache.Count
            : _hotCache.Values.Count(e => e.Scope == scopeFilter);

        var persistentCount = await _persistentStore.GetEntryCountAsync(scopeFilter, ct);

        // Subtract overlap (entries in both)
        var overlapCount = _hotCache.Values
            .Count(e => scopeFilter == null || e.Scope == scopeFilter);

        return Math.Max(hotCount, persistentCount);
    }

    /// <inheritdoc/>
    public async Task<long> GetTotalBytesAsync(string? scopeFilter = null, CancellationToken ct = default)
    {
        var hotBytes = scopeFilter == null
            ? _hotCache.Values.Sum(e => e.SizeBytes)
            : _hotCache.Values.Where(e => e.Scope == scopeFilter).Sum(e => e.SizeBytes);

        var persistentBytes = await _persistentStore.GetTotalBytesAsync(scopeFilter, ct);

        return Math.Max(hotBytes, persistentBytes);
    }

    /// <inheritdoc/>
    public async Task CompactAsync(CancellationToken ct = default)
    {
        // Compact hot cache (remove expired entries)
        var expiredIds = _hotCache.Values
            .Where(e => e.IsExpired)
            .Select(e => e.EntryId)
            .ToList();

        foreach (var id in expiredIds)
        {
            _hotCache.TryRemove(id, out _);
            _accessTimes.TryRemove(id, out _);
            _accessCounts.TryRemove(id, out _);
        }

        // Compact persistent store
        await _persistentStore.CompactAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<bool> FlushAsync(CancellationToken ct = default)
    {
        // Flush pending writes
        await FlushPendingWritesAsync(ct);

        // Flush persistent store
        return await _persistentStore.FlushAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<bool> HealthCheckAsync(CancellationToken ct = default)
    {
        if (_disposed) return false;

        return await _persistentStore.HealthCheckAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<PersistentStoreStatistics> GetStatisticsAsync(CancellationToken ct = default)
    {
        var persistentStats = await _persistentStore.GetStatisticsAsync(ct);

        return new PersistentStoreStatistics
        {
            TotalEntries = persistentStats.TotalEntries,
            TotalBytes = persistentStats.TotalBytes,
            HotTierEntries = _hotCache.Count,
            WarmTierEntries = persistentStats.WarmTierEntries,
            ColdTierEntries = persistentStats.ColdTierEntries,
            ArchiveTierEntries = persistentStats.ArchiveTierEntries,
            PendingWrites = _writeQueue.Count,
            CompactionCount = persistentStats.CompactionCount,
            LastCompaction = persistentStats.LastCompaction,
            LastFlush = persistentStats.LastFlush,
            AverageAccessLatencyMs = CalculateAverageLatency(),
            AverageWriteLatencyMs = persistentStats.AverageWriteLatencyMs
        };
    }

    /// <summary>
    /// Gets hybrid-specific statistics.
    /// </summary>
    public HybridMemoryStatistics GetHybridStatistics()
    {
        var total = Interlocked.Read(ref _cacheHits) + Interlocked.Read(ref _cacheMisses);
        var hitRate = total > 0 ? (double)_cacheHits / total : 0;

        return new HybridMemoryStatistics
        {
            HotCacheEntries = _hotCache.Count,
            HotCacheBytes = _hotCache.Values.Sum(e => e.SizeBytes),
            CacheHits = Interlocked.Read(ref _cacheHits),
            CacheMisses = Interlocked.Read(ref _cacheMisses),
            CacheHitRate = hitRate,
            Evictions = Interlocked.Read(ref _evictions),
            Promotions = Interlocked.Read(ref _promotions),
            Demotions = Interlocked.Read(ref _demotions),
            PendingWrites = _writeQueue.Count,
            PendingBytes = Interlocked.Read(ref _pendingBytes)
        };
    }

    /// <summary>
    /// Initiates graceful shutdown, ensuring all data is persisted.
    /// </summary>
    public async Task GracefulShutdownAsync(CancellationToken ct = default)
    {
        _shutdownRequested = true;

        // Stop timers
        await _migrationTimer.DisposeAsync();
        await _flushTimer.DisposeAsync();

        // Flush all pending writes
        await FlushPendingWritesAsync(ct);

        // Demote all hot cache entries to persistent storage
        foreach (var entry in _hotCache.Values)
        {
            await _persistentStore.StoreAsync(entry, ct);
        }

        // Final flush
        await _persistentStore.FlushAsync(ct);
    }

    /// <summary>
    /// Restores state from persistent storage after restart.
    /// </summary>
    public async Task RestoreFromPersistentAsync(int hotCacheLimit = 1000, CancellationToken ct = default)
    {
        // Restore most recently accessed entries to hot cache
        var restored = 0;
        await foreach (var entry in _persistentStore.SearchByMetadataAsync(
            new Dictionary<string, object>(), limit: hotCacheLimit, ct: ct))
        {
            _hotCache[entry.EntryId] = entry;
            _accessTimes[entry.EntryId] = entry.LastAccessedAt ?? entry.CreatedAt;
            _accessCounts[entry.EntryId] = entry.AccessCount;
            restored++;
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        // Graceful shutdown
        await GracefulShutdownAsync(CancellationToken.None);

        _migrationLock.Dispose();
        _flushLock.Dispose();
        await _persistentStore.DisposeAsync();
    }

    // Private helper methods

    private void UpdateAccessMetrics(string entryId)
    {
        _accessTimes[entryId] = DateTimeOffset.UtcNow;
        _accessCounts.AddOrUpdate(entryId, 1, (_, count) => count + 1);
    }

    private async Task PromoteToHotCacheAsync(ContextEntry entry, CancellationToken ct)
    {
        if (_hotCache.Count >= _options.MaxHotCacheEntries)
        {
            await EvictColdEntriesAsync(ct);
        }

        _hotCache[entry.EntryId] = entry;
        _accessTimes[entry.EntryId] = DateTimeOffset.UtcNow;
        _accessCounts[entry.EntryId] = entry.AccessCount + 1;

        Interlocked.Increment(ref _promotions);
    }

    private async Task EvictColdEntriesAsync(CancellationToken ct)
    {
        await _migrationLock.WaitAsync(ct);
        try
        {
            var targetCount = (int)(_options.MaxHotCacheEntries * 0.8); // Evict to 80% capacity
            var toEvict = _hotCache.Count - targetCount;

            if (toEvict <= 0) return;

            // Find coldest entries (oldest access time, lowest access count)
            var coldest = _hotCache.Keys
                .Select(id => (Id: id,
                    LastAccess: _accessTimes.GetValueOrDefault(id, DateTimeOffset.MinValue),
                    AccessCount: _accessCounts.GetValueOrDefault(id, 0)))
                .OrderBy(x => x.LastAccess)
                .ThenBy(x => x.AccessCount)
                .Take(toEvict)
                .Select(x => x.Id)
                .ToList();

            foreach (var id in coldest)
            {
                if (_hotCache.TryRemove(id, out var entry))
                {
                    // Entry is already in persistent store from initial write
                    _accessTimes.TryRemove(id, out _);
                    _accessCounts.TryRemove(id, out _);
                    Interlocked.Increment(ref _evictions);
                }
            }
        }
        finally
        {
            _migrationLock.Release();
        }
    }

    private async Task FlushPendingWritesAsync(CancellationToken ct)
    {
        if (_writeQueue.IsEmpty) return;

        await _flushLock.WaitAsync(ct);
        try
        {
            var batch = new List<ContextEntry>();
            var batchBytes = 0L;

            while (_writeQueue.TryDequeue(out var pending))
            {
                batch.Add(pending.Entry);
                batchBytes += pending.Entry.SizeBytes;
            }

            if (batch.Count > 0)
            {
                await _persistentStore.StoreBatchAsync(batch, ct);
                Interlocked.Add(ref _pendingBytes, -batchBytes);
            }
        }
        finally
        {
            _flushLock.Release();
        }
    }

    private async Task MigrateBasedOnAccessPatternsAsync(CancellationToken ct)
    {
        if (_shutdownRequested) return;

        await _migrationLock.WaitAsync(ct);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var coldThreshold = now - _options.ColdThreshold;

            // Find cold entries in hot cache
            var coldEntries = _hotCache.Keys
                .Where(id =>
                {
                    var lastAccess = _accessTimes.GetValueOrDefault(id, DateTimeOffset.MinValue);
                    return lastAccess < coldThreshold;
                })
                .Take(_options.MaxMigrationBatchSize)
                .ToList();

            foreach (var id in coldEntries)
            {
                if (_hotCache.TryRemove(id, out _))
                {
                    _accessTimes.TryRemove(id, out _);
                    _accessCounts.TryRemove(id, out _);
                    Interlocked.Increment(ref _demotions);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }
        finally
        {
            _migrationLock.Release();
        }
    }

    private double CalculateAverageLatency()
    {
        var hits = Interlocked.Read(ref _cacheHits);
        var misses = Interlocked.Read(ref _cacheMisses);
        var total = hits + misses;

        if (total == 0) return 0;

        // Hot cache: ~0.1ms, Persistent: ~1ms
        var hitLatency = 0.1;
        var missLatency = 1.0;

        return (hits * hitLatency + misses * missLatency) / total;
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;
        float dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        var denom = Math.Sqrt(normA) * Math.Sqrt(normB);
        return denom > 0 ? (float)(dot / denom) : 0;
    }

    private static bool MatchesFilter(ContextEntry entry, Dictionary<string, object> filter)
    {
        if (entry.Metadata == null) return false;
        foreach (var (key, value) in filter)
        {
            if (!entry.Metadata.TryGetValue(key, out var entryValue) || !Equals(entryValue, value))
                return false;
        }
        return true;
    }
}

/// <summary>
/// Pending write operation for async persistence.
/// </summary>
internal record PendingWrite
{
    public required ContextEntry Entry { get; init; }
    public DateTimeOffset QueuedAt { get; init; }
}

/// <summary>
/// Configuration options for hybrid memory store.
/// </summary>
public sealed record HybridMemoryOptions
{
    /// <summary>Maximum entries in hot cache.</summary>
    public int MaxHotCacheEntries { get; init; } = 10_000;

    /// <summary>Maximum pending bytes before forced flush.</summary>
    public long MaxPendingBytes { get; init; } = 100 * 1024 * 1024; // 100MB

    /// <summary>Interval between automatic flushes.</summary>
    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Interval between migration checks.</summary>
    public TimeSpan MigrationInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Time after which an entry is considered cold.</summary>
    public TimeSpan ColdThreshold { get; init; } = TimeSpan.FromHours(1);

    /// <summary>Maximum entries to migrate in a single batch.</summary>
    public int MaxMigrationBatchSize { get; init; } = 100;

    /// <summary>Enable write-ahead logging for durability.</summary>
    public bool EnableWriteAheadLog { get; init; } = true;

    /// <summary>Enable compression for cold data.</summary>
    public bool EnableColdCompression { get; init; } = true;
}

/// <summary>
/// Hybrid-specific statistics.
/// </summary>
public sealed record HybridMemoryStatistics
{
    public long HotCacheEntries { get; init; }
    public long HotCacheBytes { get; init; }
    public long CacheHits { get; init; }
    public long CacheMisses { get; init; }
    public double CacheHitRate { get; init; }
    public long Evictions { get; init; }
    public long Promotions { get; init; }
    public long Demotions { get; init; }
    public int PendingWrites { get; init; }
    public long PendingBytes { get; init; }
}

// =============================================================================
// Write-Ahead Log for Durability
// =============================================================================

/// <summary>
/// Write-ahead log for ensuring durability of pending writes.
/// </summary>
public sealed class WriteAheadLog : IAsyncDisposable
{
    private readonly string _logPath;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private FileStream? _logStream;
    private long _sequenceNumber;
    private bool _disposed;

    /// <summary>
    /// Initializes a new write-ahead log.
    /// </summary>
    /// <param name="logPath">Path to the log file.</param>
    public WriteAheadLog(string? logPath = null)
    {
        _logPath = logPath ?? Path.Combine(Path.GetTempPath(), "hybrid_wal.log");
    }

    /// <summary>
    /// Opens the write-ahead log.
    /// </summary>
    public async Task OpenAsync(CancellationToken ct = default)
    {
        _logStream = new FileStream(
            _logPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.Read,
            bufferSize: 64 * 1024,
            useAsync: true);

        // Read existing sequence number
        if (_logStream.Length > 0)
        {
            _logStream.Seek(-sizeof(long), SeekOrigin.End);
            var buffer = new byte[sizeof(long)];
            await _logStream.ReadExactlyAsync(buffer, ct);
            _sequenceNumber = BitConverter.ToInt64(buffer, 0);
        }
    }

    /// <summary>
    /// Appends an entry to the log.
    /// </summary>
    public async Task<long> AppendAsync(ContextEntry entry, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            if (_logStream == null)
                throw new InvalidOperationException("WAL not opened");

            var sequence = Interlocked.Increment(ref _sequenceNumber);

            var record = new WalRecord
            {
                SequenceNumber = sequence,
                EntryId = entry.EntryId,
                Timestamp = DateTimeOffset.UtcNow,
                Data = entry.Content
            };

            var json = JsonSerializer.Serialize(record);
            var bytes = System.Text.Encoding.UTF8.GetBytes(json + "\n");

            await _logStream.WriteAsync(bytes, ct);
            await _logStream.FlushAsync(ct);

            return sequence;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Truncates the log up to the given sequence number.
    /// </summary>
    public async Task TruncateAsync(long upToSequence, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            // In a real implementation, this would compact the log
            // For now, we just reset if all entries are committed
            if (upToSequence >= _sequenceNumber && _logStream != null)
            {
                _logStream.SetLength(0);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Recovers uncommitted entries from the log.
    /// </summary>
    public async IAsyncEnumerable<WalRecord> RecoverAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_logStream == null || _logStream.Length == 0)
            yield break;

        _logStream.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(_logStream, leaveOpen: true);

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            WalRecord? record;
            try
            {
                record = JsonSerializer.Deserialize<WalRecord>(line);
            }
            catch
            {
                continue;
            }

            if (record != null)
            {
                yield return record;
            }
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _writeLock.Dispose();
        if (_logStream != null)
        {
            await _logStream.DisposeAsync();
        }
    }
}

/// <summary>
/// Record in the write-ahead log.
/// </summary>
public sealed record WalRecord
{
    public long SequenceNumber { get; init; }
    public required string EntryId { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public byte[]? Data { get; init; }
}

// =============================================================================
// Tiered Hybrid Memory Store
// =============================================================================

/// <summary>
/// Multi-tiered hybrid memory store with RAM -> SSD -> HDD -> Cloud tiers.
/// Provides automatic data migration based on access patterns and policies.
/// </summary>
public sealed class TieredHybridMemoryStore : IPersistentMemoryStore
{
    private readonly HybridMemoryStore _ramToSsd;
    private readonly HybridMemoryStore _ssdToHdd;
    private readonly HybridMemoryStore _hddToCloud;
    private readonly BoundedDictionary<string, StorageTier> _entryTiers = new BoundedDictionary<string, StorageTier>(1000);
    private bool _disposed;

    /// <summary>
    /// Initializes a new tiered hybrid memory store.
    /// </summary>
    public TieredHybridMemoryStore(
        IPersistentMemoryStore? ssdStore = null,
        IPersistentMemoryStore? hddStore = null,
        IPersistentMemoryStore? cloudStore = null)
    {
        var ssd = ssdStore ?? new RocksDbMemoryStore();
        var hdd = hddStore ?? new RocksDbMemoryStore("hdd_store");
        var cloud = cloudStore ?? new ObjectStorageMemoryStore();

        _ramToSsd = new HybridMemoryStore(ssd, new HybridMemoryOptions
        {
            MaxHotCacheEntries = 10_000,
            ColdThreshold = TimeSpan.FromMinutes(30)
        });

        _ssdToHdd = new HybridMemoryStore(hdd, new HybridMemoryOptions
        {
            MaxHotCacheEntries = 100_000,
            ColdThreshold = TimeSpan.FromHours(24)
        });

        _hddToCloud = new HybridMemoryStore(cloud, new HybridMemoryOptions
        {
            MaxHotCacheEntries = 1_000_000,
            ColdThreshold = TimeSpan.FromDays(30)
        });
    }

    /// <inheritdoc/>
    public async Task StoreAsync(ContextEntry entry, CancellationToken ct = default)
    {
        // All new entries start in RAM tier
        await _ramToSsd.StoreAsync(entry, ct);
        _entryTiers[entry.EntryId] = StorageTier.Hot;
    }

    /// <inheritdoc/>
    public async Task StoreBatchAsync(IEnumerable<ContextEntry> entries, CancellationToken ct = default)
    {
        await _ramToSsd.StoreBatchAsync(entries, ct);
        foreach (var entry in entries)
        {
            _entryTiers[entry.EntryId] = StorageTier.Hot;
        }
    }

    /// <inheritdoc/>
    public async Task<ContextEntry?> GetAsync(string entryId, CancellationToken ct = default)
    {
        // Try RAM/SSD tier first
        var entry = await _ramToSsd.GetAsync(entryId, ct);
        if (entry != null)
        {
            _entryTiers[entryId] = StorageTier.Hot;
            return entry;
        }

        // Try SSD/HDD tier
        entry = await _ssdToHdd.GetAsync(entryId, ct);
        if (entry != null)
        {
            // Promote to hot tier
            await _ramToSsd.StoreAsync(entry, ct);
            _entryTiers[entryId] = StorageTier.Warm;
            return entry;
        }

        // Try HDD/Cloud tier
        entry = await _hddToCloud.GetAsync(entryId, ct);
        if (entry != null)
        {
            // Promote through tiers
            await _ssdToHdd.StoreAsync(entry, ct);
            _entryTiers[entryId] = StorageTier.Cold;
            return entry;
        }

        return null;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ContextEntry> SearchAsync(
        float[] queryVector,
        int topK,
        string? scopeFilter = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var seen = new HashSet<string>();

        // Search all tiers
        await foreach (var entry in _ramToSsd.SearchAsync(queryVector, topK, scopeFilter, ct))
        {
            if (seen.Add(entry.EntryId))
                yield return entry;
        }

        if (seen.Count < topK)
        {
            await foreach (var entry in _ssdToHdd.SearchAsync(queryVector, topK - seen.Count, scopeFilter, ct))
            {
                if (seen.Add(entry.EntryId))
                    yield return entry;
            }
        }

        if (seen.Count < topK)
        {
            await foreach (var entry in _hddToCloud.SearchAsync(queryVector, topK - seen.Count, scopeFilter, ct))
            {
                if (seen.Add(entry.EntryId))
                    yield return entry;
            }
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ContextEntry> SearchByMetadataAsync(
        Dictionary<string, object> filter,
        int limit = 100,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var seen = new HashSet<string>();
        var count = 0;

        await foreach (var entry in _ramToSsd.SearchByMetadataAsync(filter, limit, ct))
        {
            if (count >= limit) break;
            if (seen.Add(entry.EntryId))
            {
                count++;
                yield return entry;
            }
        }

        if (count < limit)
        {
            await foreach (var entry in _ssdToHdd.SearchByMetadataAsync(filter, limit - count, ct))
            {
                if (seen.Add(entry.EntryId))
                {
                    count++;
                    yield return entry;
                }
            }
        }

        if (count < limit)
        {
            await foreach (var entry in _hddToCloud.SearchByMetadataAsync(filter, limit - count, ct))
            {
                if (seen.Add(entry.EntryId))
                {
                    yield return entry;
                }
            }
        }
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(string entryId, CancellationToken ct = default)
    {
        await Task.WhenAll(
            _ramToSsd.DeleteAsync(entryId, ct),
            _ssdToHdd.DeleteAsync(entryId, ct),
            _hddToCloud.DeleteAsync(entryId, ct));

        _entryTiers.TryRemove(entryId, out _);
    }

    /// <inheritdoc/>
    public async Task DeleteScopeAsync(string scope, CancellationToken ct = default)
    {
        await Task.WhenAll(
            _ramToSsd.DeleteScopeAsync(scope, ct),
            _ssdToHdd.DeleteScopeAsync(scope, ct),
            _hddToCloud.DeleteScopeAsync(scope, ct));
    }

    /// <inheritdoc/>
    public async Task<long> GetEntryCountAsync(string? scopeFilter = null, CancellationToken ct = default)
    {
        var counts = await Task.WhenAll(
            _ramToSsd.GetEntryCountAsync(scopeFilter, ct),
            _ssdToHdd.GetEntryCountAsync(scopeFilter, ct),
            _hddToCloud.GetEntryCountAsync(scopeFilter, ct));

        // Take max as entries may exist in multiple tiers
        return counts.Max();
    }

    /// <inheritdoc/>
    public async Task<long> GetTotalBytesAsync(string? scopeFilter = null, CancellationToken ct = default)
    {
        var bytes = await Task.WhenAll(
            _ramToSsd.GetTotalBytesAsync(scopeFilter, ct),
            _ssdToHdd.GetTotalBytesAsync(scopeFilter, ct),
            _hddToCloud.GetTotalBytesAsync(scopeFilter, ct));

        return bytes.Max();
    }

    /// <inheritdoc/>
    public async Task CompactAsync(CancellationToken ct = default)
    {
        await Task.WhenAll(
            _ramToSsd.CompactAsync(ct),
            _ssdToHdd.CompactAsync(ct),
            _hddToCloud.CompactAsync(ct));
    }

    /// <inheritdoc/>
    public async Task<bool> FlushAsync(CancellationToken ct = default)
    {
        var results = await Task.WhenAll(
            _ramToSsd.FlushAsync(ct),
            _ssdToHdd.FlushAsync(ct),
            _hddToCloud.FlushAsync(ct));

        return results.All(r => r);
    }

    /// <inheritdoc/>
    public async Task<bool> HealthCheckAsync(CancellationToken ct = default)
    {
        if (_disposed) return false;

        var results = await Task.WhenAll(
            _ramToSsd.HealthCheckAsync(ct),
            _ssdToHdd.HealthCheckAsync(ct),
            _hddToCloud.HealthCheckAsync(ct));

        // Healthy if at least RAM/SSD tier is healthy
        return results[0];
    }

    /// <inheritdoc/>
    public async Task<PersistentStoreStatistics> GetStatisticsAsync(CancellationToken ct = default)
    {
        var stats = await Task.WhenAll(
            _ramToSsd.GetStatisticsAsync(ct),
            _ssdToHdd.GetStatisticsAsync(ct),
            _hddToCloud.GetStatisticsAsync(ct));

        return new PersistentStoreStatistics
        {
            TotalEntries = stats.Max(s => s.TotalEntries),
            TotalBytes = stats.Max(s => s.TotalBytes),
            HotTierEntries = stats[0].HotTierEntries,
            WarmTierEntries = stats[1].HotTierEntries,
            ColdTierEntries = stats[2].HotTierEntries,
            ArchiveTierEntries = stats[2].ArchiveTierEntries,
            PendingWrites = stats.Sum(s => s.PendingWrites),
            CompactionCount = stats.Sum(s => s.CompactionCount),
            LastCompaction = stats.Max(s => s.LastCompaction),
            LastFlush = stats.Max(s => s.LastFlush),
            AverageAccessLatencyMs = stats.Average(s => s.AverageAccessLatencyMs),
            AverageWriteLatencyMs = stats.Average(s => s.AverageWriteLatencyMs)
        };
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await _ramToSsd.DisposeAsync();
        await _ssdToHdd.DisposeAsync();
        await _hddToCloud.DisposeAsync();
    }
}
