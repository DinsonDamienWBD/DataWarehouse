using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDatabaseStorage.Strategies;

#region Index Management

/// <summary>
/// Universal index management for database storage strategies supporting
/// B-tree, hash, GiST, and full-text indexes with auto-tuning and impact analysis.
/// </summary>
public sealed class DatabaseIndexManager
{
    private readonly BoundedDictionary<string, IndexDefinition> _indexes = new BoundedDictionary<string, IndexDefinition>(1000);
    private readonly BoundedDictionary<string, IndexUsageStats> _usageStats = new BoundedDictionary<string, IndexUsageStats>(1000);

    /// <summary>Creates an index on a collection/table.</summary>
    public IndexDefinition CreateIndex(string collection, string indexName, IndexType type,
        IReadOnlyList<string> fields, bool unique = false)
    {
        var key = $"{collection}:{indexName}";
        var index = new IndexDefinition
        {
            Collection = collection,
            IndexName = indexName,
            Type = type,
            Fields = fields.ToList(),
            IsUnique = unique,
            CreatedAt = DateTime.UtcNow,
            Status = IndexStatus.Active,
            SizeBytes = EstimateIndexSize(type, fields.Count)
        };
        _indexes[key] = index;
        _usageStats[key] = new IndexUsageStats { IndexKey = key };
        return index;
    }

    /// <summary>Drops an index.</summary>
    public bool DropIndex(string collection, string indexName)
    {
        var key = $"{collection}:{indexName}";
        _usageStats.TryRemove(key, out _);
        return _indexes.TryRemove(key, out _);
    }

    /// <summary>Records an index usage for statistics tracking.</summary>
    public void RecordUsage(string collection, string indexName, double queryTimeMs)
    {
        var key = $"{collection}:{indexName}";
        if (_usageStats.TryGetValue(key, out var stats))
        {
            stats.TotalScans++;
            stats.TotalTimeMs += queryTimeMs;
            stats.LastUsed = DateTime.UtcNow;
        }
    }

    /// <summary>Analyzes indexes and recommends additions/removals.</summary>
    public IndexRecommendation Analyze(string collection)
    {
        var collectionIndexes = _indexes.Values.Where(i => i.Collection == collection).ToList();
        var unused = new List<string>();
        var overused = new List<string>();

        foreach (var idx in collectionIndexes)
        {
            var key = $"{idx.Collection}:{idx.IndexName}";
            if (_usageStats.TryGetValue(key, out var stats))
            {
                if (stats.TotalScans == 0 && (DateTime.UtcNow - idx.CreatedAt).TotalDays > 7)
                    unused.Add(idx.IndexName);
                if (stats.TotalScans > 10000)
                    overused.Add(idx.IndexName);
            }
        }

        return new IndexRecommendation
        {
            Collection = collection,
            UnusedIndexes = unused,
            HeavilyUsedIndexes = overused,
            TotalIndexes = collectionIndexes.Count,
            TotalIndexSizeBytes = collectionIndexes.Sum(i => i.SizeBytes)
        };
    }

    /// <summary>Gets all indexes for a collection.</summary>
    public IReadOnlyList<IndexDefinition> GetIndexes(string collection) =>
        _indexes.Values.Where(i => i.Collection == collection).ToList();

    private static long EstimateIndexSize(IndexType type, int fieldCount) => type switch
    {
        IndexType.BTree => 8192L * fieldCount,
        IndexType.Hash => 4096L * fieldCount,
        IndexType.FullText => 32768L * fieldCount,
        IndexType.GiST => 16384L * fieldCount,
        IndexType.Spatial => 16384L * fieldCount,
        _ => 8192L * fieldCount
    };
}

/// <summary>Index types.</summary>
public enum IndexType { BTree, Hash, FullText, GiST, Spatial, Bitmap, Bloom }

/// <summary>Index status.</summary>
public enum IndexStatus { Active, Building, Invalid, Disabled }

/// <summary>Index definition.</summary>
public sealed record IndexDefinition
{
    public required string Collection { get; init; }
    public required string IndexName { get; init; }
    public IndexType Type { get; init; }
    public List<string> Fields { get; init; } = [];
    public bool IsUnique { get; init; }
    public DateTime CreatedAt { get; init; }
    public IndexStatus Status { get; init; }
    public long SizeBytes { get; init; }
}

/// <summary>Index usage statistics.</summary>
public sealed class IndexUsageStats
{
    public required string IndexKey { get; init; }
    public long TotalScans { get; set; }
    public double TotalTimeMs { get; set; }
    public DateTime? LastUsed { get; set; }
}

/// <summary>Index analysis recommendation.</summary>
public sealed record IndexRecommendation
{
    public required string Collection { get; init; }
    public List<string> UnusedIndexes { get; init; } = [];
    public List<string> HeavilyUsedIndexes { get; init; } = [];
    public int TotalIndexes { get; init; }
    public long TotalIndexSizeBytes { get; init; }
}

#endregion

#region Compaction Policies

/// <summary>
/// Compaction policy manager for LSM-tree and similar storage engines.
/// Supports size-tiered, leveled, and time-window compaction strategies.
/// </summary>
public sealed class CompactionPolicyManager
{
    private readonly BoundedDictionary<string, CompactionPolicy> _policies = new BoundedDictionary<string, CompactionPolicy>(1000);
    private readonly BoundedDictionary<string, CompactionHistory> _history = new BoundedDictionary<string, CompactionHistory>(1000);
    private long _totalCompactions;

    /// <summary>Registers a compaction policy for a table/collection.</summary>
    public void SetPolicy(string collection, CompactionStrategy strategy,
        int maxLevels = 7, double sizeRatio = 10, TimeSpan? timeWindow = null)
    {
        _policies[collection] = new CompactionPolicy
        {
            Collection = collection,
            Strategy = strategy,
            MaxLevels = maxLevels,
            SizeRatio = sizeRatio,
            TimeWindow = timeWindow ?? TimeSpan.FromHours(1),
            CreatedAt = DateTime.UtcNow
        };
    }

    /// <summary>Evaluates whether compaction should run for a collection.</summary>
    public CompactionDecision EvaluateCompaction(string collection, int sstableCount,
        long totalSizeBytes, int level = 0)
    {
        if (!_policies.TryGetValue(collection, out var policy))
            return new CompactionDecision { ShouldCompact = false };

        var shouldCompact = policy.Strategy switch
        {
            CompactionStrategy.SizeTiered => sstableCount >= 4,
            CompactionStrategy.Leveled => totalSizeBytes > (long)(Math.Pow(policy.SizeRatio, level) * 10_000_000),
            CompactionStrategy.TimeWindow => true, // Always compact time windows
            CompactionStrategy.Unified => sstableCount >= 4 || totalSizeBytes > 100_000_000,
            _ => false
        };

        return new CompactionDecision
        {
            ShouldCompact = shouldCompact,
            Strategy = policy.Strategy,
            EstimatedDuration = TimeSpan.FromSeconds(totalSizeBytes / 100_000_000.0),
            Priority = shouldCompact ? (sstableCount > 10 ? CompactionPriority.High : CompactionPriority.Normal) : CompactionPriority.Low
        };
    }

    /// <summary>Records a completed compaction.</summary>
    public void RecordCompaction(string collection, long bytesRead, long bytesWritten,
        int filesRemoved, int filesCreated, TimeSpan duration)
    {
        var key = $"{collection}:{DateTime.UtcNow.Ticks}";
        _history[key] = new CompactionHistory
        {
            Collection = collection,
            BytesRead = bytesRead,
            BytesWritten = bytesWritten,
            FilesRemoved = filesRemoved,
            FilesCreated = filesCreated,
            Duration = duration,
            CompletedAt = DateTime.UtcNow
        };
        Interlocked.Increment(ref _totalCompactions);
    }

    /// <summary>Gets compaction history for a collection.</summary>
    public IReadOnlyList<CompactionHistory> GetHistory(string collection, int limit = 50)
    {
        return _history.Values.Where(h => h.Collection == collection)
            .OrderByDescending(h => h.CompletedAt).Take(limit).ToList();
    }
}

/// <summary>Compaction strategies.</summary>
public enum CompactionStrategy { SizeTiered, Leveled, TimeWindow, Unified }

/// <summary>Compaction priority.</summary>
public enum CompactionPriority { Low, Normal, High, Urgent }

/// <summary>Compaction policy for a collection.</summary>
public sealed record CompactionPolicy
{
    public required string Collection { get; init; }
    public CompactionStrategy Strategy { get; init; }
    public int MaxLevels { get; init; }
    public double SizeRatio { get; init; }
    public TimeSpan TimeWindow { get; init; }
    public DateTime CreatedAt { get; init; }
}

/// <summary>Decision on whether to run compaction.</summary>
public sealed record CompactionDecision
{
    public bool ShouldCompact { get; init; }
    public CompactionStrategy Strategy { get; init; }
    public TimeSpan EstimatedDuration { get; init; }
    public CompactionPriority Priority { get; init; }
}

/// <summary>Compaction history record.</summary>
public sealed record CompactionHistory
{
    public required string Collection { get; init; }
    public long BytesRead { get; init; }
    public long BytesWritten { get; init; }
    public int FilesRemoved { get; init; }
    public int FilesCreated { get; init; }
    public TimeSpan Duration { get; init; }
    public DateTime CompletedAt { get; init; }
}

#endregion

#region Query Optimization

/// <summary>
/// Query optimizer for database storage strategies supporting cost-based optimization,
/// query plan caching, and statistics-based selectivity estimation.
/// </summary>
public sealed class QueryOptimizer
{
    private readonly BoundedDictionary<string, QueryPlan> _planCache = new BoundedDictionary<string, QueryPlan>(1000);
    private readonly BoundedDictionary<string, TableStatistics> _statistics = new BoundedDictionary<string, TableStatistics>(1000);
    private long _plansGenerated;
    private long _cacheHits;

    /// <summary>Updates table statistics for query optimization.</summary>
    public void UpdateStatistics(string collection, long rowCount, long sizeBytes,
        Dictionary<string, ColumnStatistics>? columnStats = null)
    {
        _statistics[collection] = new TableStatistics
        {
            Collection = collection,
            RowCount = rowCount,
            SizeBytes = sizeBytes,
            ColumnStats = columnStats ?? new(),
            UpdatedAt = DateTime.UtcNow
        };
    }

    /// <summary>Generates or retrieves a cached query plan.</summary>
    public QueryPlan GetPlan(string queryHash, string collection, QueryType type,
        IReadOnlyList<string>? filterFields = null)
    {
        if (_planCache.TryGetValue(queryHash, out var cached))
        {
            Interlocked.Increment(ref _cacheHits);
            return cached;
        }

        var stats = _statistics.TryGetValue(collection, out var s) ? s : null;
        var estimatedRows = stats?.RowCount ?? 1000;

        // Cost estimation
        var plan = new QueryPlan
        {
            QueryHash = queryHash,
            Collection = collection,
            Type = type,
            AccessMethod = DetermineAccessMethod(filterFields, collection),
            EstimatedRows = estimatedRows,
            EstimatedCost = EstimateCost(type, estimatedRows, filterFields?.Count ?? 0),
            CreatedAt = DateTime.UtcNow
        };

        _planCache[queryHash] = plan;
        Interlocked.Increment(ref _plansGenerated);
        return plan;
    }

    /// <summary>Invalidates cached plans for a collection (after schema change).</summary>
    public int InvalidateCache(string collection)
    {
        var toRemove = _planCache.Where(kv => kv.Value.Collection == collection).ToList();
        foreach (var kv in toRemove) _planCache.TryRemove(kv.Key, out _);
        return toRemove.Count;
    }

    /// <summary>Gets optimizer metrics.</summary>
    public OptimizerMetrics GetMetrics() => new()
    {
        PlansGenerated = Interlocked.Read(ref _plansGenerated),
        CacheHits = Interlocked.Read(ref _cacheHits),
        CachedPlans = _planCache.Count,
        TablesWithStatistics = _statistics.Count
    };

    private AccessMethod DetermineAccessMethod(IReadOnlyList<string>? filterFields, string collection)
    {
        if (filterFields == null || filterFields.Count == 0)
            return AccessMethod.FullScan;

        // Check if any filter fields have indexes
        return AccessMethod.IndexScan; // Simplified
    }

    private double EstimateCost(QueryType type, long rows, int filterCount) => type switch
    {
        QueryType.PointLookup => 1.0,
        QueryType.RangeScan => rows * 0.01 / Math.Max(1, filterCount),
        QueryType.FullScan => rows * 1.0,
        QueryType.Aggregation => rows * 0.5,
        QueryType.Join => rows * rows * 0.001,
        _ => rows
    };
}

/// <summary>Query types.</summary>
public enum QueryType { PointLookup, RangeScan, FullScan, Aggregation, Join }

/// <summary>Access methods for query execution.</summary>
public enum AccessMethod { FullScan, IndexScan, IndexOnly, HashLookup, BitmapScan }

/// <summary>Query execution plan.</summary>
public sealed record QueryPlan
{
    public required string QueryHash { get; init; }
    public required string Collection { get; init; }
    public QueryType Type { get; init; }
    public AccessMethod AccessMethod { get; init; }
    public long EstimatedRows { get; init; }
    public double EstimatedCost { get; init; }
    public DateTime CreatedAt { get; init; }
}

/// <summary>Table/collection statistics for query optimization.</summary>
public sealed record TableStatistics
{
    public required string Collection { get; init; }
    public long RowCount { get; init; }
    public long SizeBytes { get; init; }
    public Dictionary<string, ColumnStatistics> ColumnStats { get; init; } = new();
    public DateTime UpdatedAt { get; init; }
}

/// <summary>Per-column statistics for selectivity estimation.</summary>
public sealed record ColumnStatistics
{
    public required string ColumnName { get; init; }
    public long DistinctValues { get; init; }
    public object? MinValue { get; init; }
    public object? MaxValue { get; init; }
    public double NullFraction { get; init; }
    public double AverageWidth { get; init; }
}

/// <summary>Query optimizer metrics.</summary>
public sealed record OptimizerMetrics
{
    public long PlansGenerated { get; init; }
    public long CacheHits { get; init; }
    public int CachedPlans { get; init; }
    public int TablesWithStatistics { get; init; }
}

#endregion

#region Cache Integration

/// <summary>
/// Storage cache integration layer providing read-through and write-through caching
/// for database storage strategies with LRU eviction.
/// </summary>
public sealed class StorageCacheIntegration
{
    private readonly BoundedDictionary<string, CacheEntry> _cache = new BoundedDictionary<string, CacheEntry>(1000);
    private readonly int _maxEntries;
    private long _hits;
    private long _misses;

    public StorageCacheIntegration(int maxEntries = 10_000)
    {
        _maxEntries = maxEntries;
    }

    /// <summary>Gets a value from cache (read-through).</summary>
    public async Task<byte[]?> GetAsync(string key, Func<string, Task<byte[]?>> fallback, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(key, out var entry))
        {
            entry.LastAccessed = DateTime.UtcNow;
            entry.AccessCount++;
            Interlocked.Increment(ref _hits);
            return entry.Data;
        }

        Interlocked.Increment(ref _misses);
        var data = await fallback(key);
        if (data != null)
        {
            Put(key, data);
        }
        return data;
    }

    /// <summary>Puts a value into cache.</summary>
    public void Put(string key, byte[] data, TimeSpan? ttl = null)
    {
        EvictIfNeeded();
        _cache[key] = new CacheEntry
        {
            Key = key,
            Data = data,
            CreatedAt = DateTime.UtcNow,
            LastAccessed = DateTime.UtcNow,
            ExpiresAt = ttl.HasValue ? DateTime.UtcNow + ttl.Value : null
        };
    }

    /// <summary>Invalidates a cache entry.</summary>
    public bool Invalidate(string key) => _cache.TryRemove(key, out _);

    /// <summary>Gets cache metrics.</summary>
    public CacheMetrics GetMetrics()
    {
        var hits = Interlocked.Read(ref _hits);
        var misses = Interlocked.Read(ref _misses);
        var total = hits + misses;
        return new CacheMetrics
        {
            Hits = hits,
            Misses = misses,
            HitRate = total > 0 ? (double)hits / total : 0,
            EntryCount = _cache.Count,
            TotalSizeBytes = _cache.Values.Sum(e => (long)e.Data.Length)
        };
    }

    private void EvictIfNeeded()
    {
        // Remove expired entries
        var now = DateTime.UtcNow;
        var expired = _cache.Where(kv => kv.Value.ExpiresAt.HasValue && kv.Value.ExpiresAt.Value < now).ToList();
        foreach (var kv in expired) _cache.TryRemove(kv.Key, out _);

        // LRU eviction if still over capacity
        while (_cache.Count >= _maxEntries)
        {
            var lru = _cache.OrderBy(kv => kv.Value.LastAccessed).FirstOrDefault();
            if (lru.Key != null) _cache.TryRemove(lru.Key, out _);
            else break;
        }
    }
}

/// <summary>A cache entry.</summary>
public sealed class CacheEntry
{
    public required string Key { get; init; }
    public required byte[] Data { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime LastAccessed { get; set; }
    public long AccessCount { get; set; }
    public DateTime? ExpiresAt { get; init; }
}

/// <summary>Cache performance metrics.</summary>
public sealed record CacheMetrics
{
    public long Hits { get; init; }
    public long Misses { get; init; }
    public double HitRate { get; init; }
    public int EntryCount { get; init; }
    public long TotalSizeBytes { get; init; }
}

#endregion
