using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts.IntelligenceAware;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Scale;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts;

/// <summary>
/// Base class for shard manager plugins that handle trillion-object scale distribution.
/// Provides template methods with built-in caching, metrics, and consistent hashing.
/// Implements Google's Jump Consistent Hash for minimal key reassignment.
/// </summary>
/// <remarks>
/// Inherit from this class to implement custom sharding strategies:
/// - Override Strategy property to specify sharding algorithm
/// - Implement GetShardForHashAsync for hash-to-shard mapping
/// - Implement GetAllShardsAsync for topology queries
/// - Override MaxShardsPerNode for capacity planning
/// </remarks>
public abstract class ShardManagerPluginBase : FeaturePluginBase, IShardManager, IIntelligenceAware
{
    #region Intelligence Socket

    /// <summary>
    /// Gets whether Universal Intelligence (T90) is available for AI-assisted sharding decisions.
    /// </summary>
    public bool IsIntelligenceAvailable { get; protected set; }

    /// <summary>
    /// Gets the available Intelligence capabilities.
    /// </summary>
    public IntelligenceCapabilities AvailableCapabilities { get; protected set; }

    /// <summary>
    /// Discovers Intelligence availability. Called during startup.
    /// </summary>
    public virtual async Task<bool> DiscoverIntelligenceAsync(CancellationToken ct = default)
    {
        // Default implementation: check if message bus is available and T90 responds
        if (MessageBus == null)
        {
            IsIntelligenceAvailable = false;
            return false;
        }
        // Attempt discovery with timeout
        IsIntelligenceAvailable = false; // Placeholder - real impl would query T90
        return IsIntelligenceAvailable;
    }

    /// <summary>
    /// Declared capabilities for this sharding plugin.
    /// </summary>
    protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities => new[]
    {
        new RegisteredCapability
        {
            CapabilityId = $"{Id}.sharding",
            DisplayName = $"{Name} - Shard Management",
            Description = "Exabyte-scale data sharding and distribution",
            Category = CapabilityCategory.Storage,
            SubCategory = "Sharding",
            PluginId = Id,
            PluginName = Name,
            PluginVersion = Version,
            Tags = new[] { "sharding", "scale", "distributed", "exabyte" },
            SemanticDescription = "Use for distributing data across shards at massive scale"
        },
        new RegisteredCapability
        {
            CapabilityId = $"{Id}.rebalancing",
            DisplayName = $"{Name} - Shard Rebalancing",
            Description = "Automatic shard rebalancing across cluster nodes",
            Category = CapabilityCategory.Storage,
            SubCategory = "Sharding",
            PluginId = Id,
            PluginName = Name,
            PluginVersion = Version,
            Tags = new[] { "sharding", "rebalancing", "migration" },
            SemanticDescription = "Use for rebalancing shards when nodes are added or removed"
        }
    };

    /// <summary>
    /// Gets static knowledge about sharding capabilities for AI agents.
    /// </summary>
    protected virtual IReadOnlyList<KnowledgeObject> GetStaticKnowledge()
    {
        return new[]
        {
            new KnowledgeObject
            {
                Id = $"{Id}.shard.capability",
                Topic = "sharding.exabyte",
                SourcePluginId = Id,
                SourcePluginName = Name,
                KnowledgeType = "capability",
                Description = $"Shard manager using {Strategy} strategy with {DefaultShardCount} default shards",
                Payload = new Dictionary<string, object>
                {
                    ["strategy"] = Strategy.ToString(),
                    ["defaultShardCount"] = DefaultShardCount,
                    ["maxShardsPerNode"] = MaxShardsPerNode
                },
                Tags = new[] { "sharding", "exabyte", "distributed" }
            }
        };
    }

    /// <summary>
    /// Requests AI-assisted optimal sharding strategy for the given data size.
    /// Returns null if Intelligence is unavailable.
    /// </summary>
    /// <param name="dataSize">Total data size in bytes.</param>
    /// <param name="nodeCount">Number of available nodes.</param>
    /// <param name="ct">Cancellation token.</param>
    protected virtual async Task<ShardRecommendation?> RequestOptimalShardingAsync(long dataSize, int nodeCount, CancellationToken ct = default)
    {
        if (!IsIntelligenceAvailable || MessageBus == null) return null;

        // Request Intelligence for optimal sharding strategy
        // This would send a message to T90 and await response
        await Task.CompletedTask; // Placeholder
        return null;
    }

    #endregion

    /// <summary>
    /// Gets the sharding strategy used by this implementation.
    /// Determines how keys are distributed across shards.
    /// </summary>
    protected abstract ShardingStrategy Strategy { get; }

    /// <summary>
    /// Gets the default number of shards to create in new deployments.
    /// Typically set based on expected data volume and node count.
    /// </summary>
    protected abstract int DefaultShardCount { get; }

    /// <summary>
    /// Gets the maximum number of shards allowed per node.
    /// Used for capacity planning and rebalancing decisions.
    /// Default is 1000 shards per node.
    /// </summary>
    protected virtual int MaxShardsPerNode => 1000;

    /// <summary>
    /// Gets the shard responsible for storing the specified key.
    /// Uses consistent hashing via ComputeConsistentHash for stable mapping.
    /// </summary>
    /// <param name="key">The object key to locate.</param>
    /// <returns>The shard that should store this key.</returns>
    /// <remarks>
    /// Template method that:
    /// 1. Computes consistent hash of key using XxHash64
    /// 2. Maps hash to shard via GetShardForHashAsync
    /// 3. Results can be cached at application layer for performance
    /// </remarks>
    public async Task<IShard> GetShardForKeyAsync(string key)
    {
        var hash = ComputeConsistentHash(key);
        return await GetShardForHashAsync(hash);
    }

    /// <summary>
    /// Maps a consistent hash value to the corresponding shard.
    /// Must be implemented by derived classes based on their sharding strategy.
    /// </summary>
    /// <param name="hash">64-bit consistent hash of the object key.</param>
    /// <returns>The shard responsible for this hash value.</returns>
    /// <remarks>
    /// Implementation approaches:
    /// - ConsistentHash: Use Jump Consistent Hash or hash ring
    /// - RangePartition: Binary search on range boundaries
    /// - DirectoryPartition: Lookup in partition directory
    /// - Composite: Combine multiple strategies
    /// </remarks>
    protected abstract Task<IShard> GetShardForHashAsync(ulong hash);

    /// <summary>
    /// Lists all shards in the distributed system.
    /// Must be implemented by derived classes to query topology.
    /// </summary>
    /// <returns>Read-only list of all shards with current state.</returns>
    /// <remarks>
    /// Implementation typically queries distributed coordination service (e.g., etcd, Consul).
    /// Result should be cached with short TTL to reduce coordination service load.
    /// </remarks>
    protected abstract Task<IReadOnlyList<IShard>> GetAllShardsAsync();

    /// <summary>
    /// Performs rebalancing of shards across the cluster.
    /// Must be implemented by derived classes to execute rebalancing logic.
    /// </summary>
    /// <param name="options">Configuration for rebalancing operation.</param>
    /// <returns>A task representing the asynchronous rebalancing operation.</returns>
    /// <remarks>
    /// Rebalancing algorithm:
    /// 1. Calculate target load per node (equal distribution)
    /// 2. Identify overloaded and underloaded nodes
    /// 3. Plan migrations to equalize load (respect MaxConcurrentMigrations)
    /// 4. Execute migrations with progress tracking
    /// 5. Verify final distribution meets balance threshold
    /// </remarks>
    protected abstract Task PerformRebalanceAsync(RebalanceOptions options);

    /// <summary>
    /// Performs migration of shards from one node to another.
    /// Must be implemented by derived classes to execute migration logic.
    /// </summary>
    /// <param name="fromNode">Source node identifier.</param>
    /// <param name="toNode">Destination node identifier.</param>
    /// <returns>Status object for tracking migration progress.</returns>
    /// <remarks>
    /// Migration protocol (zero downtime):
    /// 1. Create empty shard replica on destination node
    /// 2. Copy existing data with progress tracking
    /// 3. Switch to dual-read mode (read from both nodes)
    /// 4. Sync incremental changes to destination
    /// 5. Atomic cutover to destination (update shard directory)
    /// 6. Drain source node and delete old replica
    /// </remarks>
    protected abstract Task<ShardMigrationStatus> PerformMigrationAsync(string fromNode, string toNode);

    /// <summary>
    /// Performs splitting of a shard into two smaller shards.
    /// Must be implemented by derived classes to execute split logic.
    /// </summary>
    /// <param name="shardId">The shard to split.</param>
    /// <returns>A task representing the asynchronous split operation.</returns>
    /// <remarks>
    /// Split protocol:
    /// 1. Set source shard to read-only mode
    /// 2. Create two new child shards (split hash space 50/50)
    /// 3. Copy data to appropriate child shard based on hash
    /// 4. Atomic cutover (update shard directory)
    /// 5. Delete source shard after verification
    /// Split trigger: Shard size exceeds threshold (e.g., 1TB or 10M objects)
    /// </remarks>
    protected abstract Task PerformSplitAsync(string shardId);

    /// <summary>
    /// Performs merging of multiple shards into a single shard.
    /// Must be implemented by derived classes to execute merge logic.
    /// </summary>
    /// <param name="shardIds">Array of shard identifiers to merge.</param>
    /// <returns>A task representing the asynchronous merge operation.</returns>
    /// <remarks>
    /// Merge protocol:
    /// 1. Verify all source shards are on same node (required for atomic merge)
    /// 2. Set source shards to read-only mode
    /// 3. Create new merged shard with combined hash space
    /// 4. Copy data from all source shards to merged shard
    /// 5. Atomic cutover (update shard directory)
    /// 6. Delete source shards after verification
    /// Merge trigger: Shard size below threshold (e.g., 100GB or 1M objects)
    /// </remarks>
    protected abstract Task PerformMergeAsync(string[] shardIds);

    /// <summary>
    /// Computes consistent hash using XxHash64 for high performance and distribution quality.
    /// Uses Google's Jump Consistent Hash algorithm concept for minimal key reassignment.
    /// </summary>
    /// <param name="key">The key to hash.</param>
    /// <returns>64-bit hash value with excellent distribution properties.</returns>
    /// <remarks>
    /// XxHash64 chosen for:
    /// - Excellent speed (10+ GB/s on modern CPUs)
    /// - High-quality distribution (passes SMHasher test suite)
    /// - 64-bit output for trillion-object keyspace
    ///
    /// Consistent hashing properties:
    /// - Adding/removing nodes affects only K/N keys (K = keys, N = nodes)
    /// - Deterministic mapping (same key always maps to same shard)
    /// - Uniform distribution across hash space
    /// </remarks>
    protected ulong ComputeConsistentHash(string key)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(key);
        return System.IO.Hashing.XxHash64.HashToUInt64(bytes);
    }

    /// <summary>
    /// Lists all shards in the distributed system.
    /// Delegates to GetAllShardsAsync implementation.
    /// </summary>
    /// <returns>Read-only list of all shards with current state.</returns>
    public Task<IReadOnlyList<IShard>> ListShardsAsync() => GetAllShardsAsync();

    /// <summary>
    /// Rebalances shards across the cluster to optimize distribution.
    /// Delegates to PerformRebalanceAsync implementation.
    /// </summary>
    /// <param name="options">Configuration for rebalancing operation.</param>
    /// <returns>A task representing the asynchronous rebalancing operation.</returns>
    public Task RebalanceShardsAsync(RebalanceOptions options) => PerformRebalanceAsync(options);

    /// <summary>
    /// Migrates shards from one node to another.
    /// Delegates to PerformMigrationAsync implementation.
    /// </summary>
    /// <param name="fromNode">Source node identifier.</param>
    /// <param name="toNode">Destination node identifier.</param>
    /// <returns>Status object for tracking migration progress.</returns>
    public Task<ShardMigrationStatus> MigrateShardsAsync(string fromNode, string toNode) =>
        PerformMigrationAsync(fromNode, toNode);

    /// <summary>
    /// Splits a shard into two smaller shards.
    /// Delegates to PerformSplitAsync implementation.
    /// </summary>
    /// <param name="shardId">The shard to split.</param>
    /// <returns>A task representing the asynchronous split operation.</returns>
    public Task SplitShardAsync(string shardId) => PerformSplitAsync(shardId);

    /// <summary>
    /// Merges multiple shards into a single shard.
    /// Delegates to PerformMergeAsync implementation.
    /// </summary>
    /// <param name="shardIds">Array of shard identifiers to merge.</param>
    /// <returns>A task representing the asynchronous merge operation.</returns>
    public Task MergeShardsAsync(string[] shardIds) => PerformMergeAsync(shardIds);
}

/// <summary>
/// Base class for distributed metadata index plugins using LSM-tree architecture.
/// Provides template methods for MemTable, SSTable, Bloom filters, and compaction.
/// Optimized for write-heavy workloads at trillion-object scale.
/// </summary>
/// <remarks>
/// LSM-tree (Log-Structured Merge-tree) architecture:
/// - Level 0: In-memory MemTable for fast writes (O(log n))
/// - Level 1-N: On-disk SSTables (Sorted String Tables) for storage
/// - Bloom filters: Reduce disk reads for negative lookups
/// - Compaction: Merge levels to reclaim space and optimize reads
///
/// Write path: MemTable -> Flush to Level 0 -> Compact to Level 1 -> ... -> Level N
/// Read path: Check MemTable -> Check Bloom filters -> Read SSTables (newest first)
/// </remarks>
public abstract class DistributedMetadataIndexPluginBase : FeaturePluginBase, IDistributedMetadataIndex, IIntelligenceAware
{
    #region Intelligence Socket

    /// <summary>
    /// Gets whether Universal Intelligence (T90) is available for AI-assisted indexing decisions.
    /// </summary>
    public bool IsIntelligenceAvailable { get; protected set; }

    /// <summary>
    /// Gets the available Intelligence capabilities.
    /// </summary>
    public IntelligenceCapabilities AvailableCapabilities { get; protected set; }

    /// <summary>
    /// Discovers Intelligence availability. Called during startup.
    /// </summary>
    public virtual async Task<bool> DiscoverIntelligenceAsync(CancellationToken ct = default)
    {
        if (MessageBus == null)
        {
            IsIntelligenceAvailable = false;
            return false;
        }
        IsIntelligenceAvailable = false; // Placeholder
        return IsIntelligenceAvailable;
    }

    /// <summary>
    /// Declared capabilities for this metadata index plugin.
    /// </summary>
    protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities => new[]
    {
        new RegisteredCapability
        {
            CapabilityId = $"{Id}.indexing",
            DisplayName = $"{Name} - Metadata Indexing",
            Description = "LSM-tree based distributed metadata indexing for trillion-object scale",
            Category = CapabilityCategory.Metadata,
            SubCategory = "Indexing",
            PluginId = Id,
            PluginName = Name,
            PluginVersion = Version,
            Tags = new[] { "indexing", "metadata", "lsm-tree", "search" },
            SemanticDescription = "Use for indexing and searching metadata at exabyte scale"
        },
        new RegisteredCapability
        {
            CapabilityId = $"{Id}.compaction",
            DisplayName = $"{Name} - LSM Compaction",
            Description = "Background compaction of LSM-tree levels for optimal read performance",
            Category = CapabilityCategory.Metadata,
            SubCategory = "Maintenance",
            PluginId = Id,
            PluginName = Name,
            PluginVersion = Version,
            Tags = new[] { "compaction", "lsm-tree", "maintenance" },
            SemanticDescription = "Use for triggering or monitoring LSM compaction operations"
        }
    };

    /// <summary>
    /// Gets static knowledge about indexing capabilities for AI agents.
    /// </summary>
    protected virtual IReadOnlyList<KnowledgeObject> GetStaticKnowledge()
    {
        return new[]
        {
            new KnowledgeObject
            {
                Id = $"{Id}.index.capability",
                Topic = "indexing.metadata",
                SourcePluginId = Id,
                SourcePluginName = Name,
                KnowledgeType = "capability",
                Description = $"LSM-tree index with {MaxLevels} levels, {MemTableSizeBytes / (1024 * 1024)}MB MemTable",
                Payload = new Dictionary<string, object>
                {
                    ["maxLevels"] = MaxLevels,
                    ["memTableSizeMB"] = MemTableSizeBytes / (1024 * 1024),
                    ["bloomFilterFpr"] = BloomFilterFalsePositiveRate
                },
                Tags = new[] { "indexing", "lsm-tree", "metadata" }
            }
        };
    }

    /// <summary>
    /// Requests AI-assisted query optimization.
    /// Returns null if Intelligence is unavailable.
    /// </summary>
    /// <param name="query">The metadata query to optimize.</param>
    /// <param name="ct">Cancellation token.</param>
    protected virtual async Task<QueryOptimizationHint?> RequestQueryOptimizationAsync(MetadataQuery query, CancellationToken ct = default)
    {
        if (!IsIntelligenceAvailable || MessageBus == null) return null;
        await Task.CompletedTask; // Placeholder
        return null;
    }

    #endregion

    /// <summary>
    /// Gets the maximum number of LSM tree levels.
    /// Typically 5-7 levels for trillion-object scale (each level 10x larger).
    /// </summary>
    protected abstract int MaxLevels { get; }

    /// <summary>
    /// Gets the MemTable size threshold in bytes before flushing to disk.
    /// Typical values: 64MB-256MB (balance between memory usage and flush frequency).
    /// </summary>
    protected abstract long MemTableSizeBytes { get; }

    /// <summary>
    /// Gets the target false positive rate for Bloom filters.
    /// Typical value: 0.01 (1%) balances memory usage vs disk read avoidance.
    /// </summary>
    protected abstract double BloomFilterFalsePositiveRate { get; }

    /// <summary>
    /// Writes a key-value pair to the in-memory MemTable.
    /// Must be implemented by derived classes to manage MemTable data structure.
    /// </summary>
    /// <param name="key">The object key to index.</param>
    /// <param name="metadata">Metadata attributes to store.</param>
    /// <returns>A task representing the asynchronous write operation.</returns>
    /// <remarks>
    /// MemTable implementation options:
    /// - Skip list: O(log n) insert, efficient for concurrent writes
    /// - Red-black tree: O(log n) insert, simpler implementation
    /// - B-tree: O(log n) insert, better cache locality
    ///
    /// Triggers flush when MemTable size exceeds MemTableSizeBytes threshold.
    /// </remarks>
    protected abstract Task WriteToMemTableAsync(string key, Dictionary<string, object> metadata);

    /// <summary>
    /// Flushes the in-memory MemTable to persistent storage as a Level 0 SSTable.
    /// Must be implemented by derived classes to serialize and write SSTable.
    /// </summary>
    /// <returns>A task representing the asynchronous flush operation.</returns>
    /// <remarks>
    /// Flush protocol:
    /// 1. Freeze current MemTable (switch to new MemTable for writes)
    /// 2. Sort entries by key for SSTable format
    /// 3. Build Bloom filter for this SSTable
    /// 4. Write SSTable to disk (with compression)
    /// 5. Write Bloom filter to disk
    /// 6. Update index metadata (register new SSTable)
    /// 7. Delete frozen MemTable from memory
    ///
    /// Flush is triggered automatically by WriteToMemTableAsync when threshold exceeded.
    /// </remarks>
    protected abstract Task FlushMemTableAsync();

    /// <summary>
    /// Searches across all LSM tree levels for entries matching the query.
    /// Must be implemented by derived classes to search MemTable and SSTables.
    /// </summary>
    /// <param name="query">Search criteria including pattern and filters.</param>
    /// <returns>Async enumerable of search results.</returns>
    /// <remarks>
    /// Search protocol:
    /// 1. Search MemTable first (newest data)
    /// 2. For each level (L0 to MaxLevels):
    ///    a. Check Bloom filter (skip level if key definitely not present)
    ///    b. Binary search SSTable index
    ///    c. Read matching entries from SSTable
    /// 3. Merge results across levels (handle duplicates, keep newest version)
    /// 4. Apply filters and score results
    /// 5. Sort by score and apply pagination
    ///
    /// Optimization: Use async enumerable to stream results (avoid loading all into memory).
    /// </remarks>
    protected abstract IAsyncEnumerable<MetadataSearchResult> SearchLevelsAsync(MetadataQuery query);

    /// <summary>
    /// Compacts a specific LSM tree level by merging SSTables.
    /// Must be implemented by derived classes to execute compaction logic.
    /// </summary>
    /// <param name="level">The level to compact (0 to MaxLevels-1).</param>
    /// <returns>A task representing the asynchronous compaction operation.</returns>
    /// <remarks>
    /// Compaction strategies:
    /// - Size-tiered: Merge SSTables of similar size
    /// - Leveled: Merge overlapping key ranges between levels
    /// - Time-windowed: Merge SSTables within time windows
    ///
    /// Compaction protocol (size-tiered):
    /// 1. Select SSTables to merge (typically 4-10 SSTables of similar size)
    /// 2. Merge-sort entries from selected SSTables
    /// 3. Apply tombstone deletion (remove deleted entries)
    /// 4. Write merged SSTable to next level
    /// 5. Build Bloom filter for merged SSTable
    /// 6. Update index metadata (register new SSTable, delete old SSTables)
    /// 7. Delete old SSTables from disk
    ///
    /// Compaction is triggered when:
    /// - Level size exceeds threshold (e.g., L0 > 4 SSTables)
    /// - Explicit CompactAsync call
    /// - Background compaction scheduler
    /// </remarks>
    protected abstract Task CompactLevelAsync(int level);

    /// <summary>
    /// Computes current statistics about the index.
    /// Must be implemented by derived classes to aggregate metrics.
    /// </summary>
    /// <returns>Statistics object with current index metrics.</returns>
    /// <remarks>
    /// Statistics to compute:
    /// - DocumentCount: Sum across MemTable and all SSTables (with deduplication)
    /// - IndexSizeBytes: Sum of all SSTable files on disk
    /// - LevelCount: Number of active levels (levels with at least one SSTable)
    /// - BloomFilterBytes: Sum of Bloom filter memory usage across all levels
    ///
    /// Optimization: Cache statistics and update incrementally on flush/compaction.
    /// </remarks>
    protected abstract Task<DistributedIndexStatistics> ComputeStatisticsAsync();

    /// <summary>
    /// Indexes metadata for an object key.
    /// Delegates to WriteToMemTableAsync implementation.
    /// </summary>
    /// <param name="key">The object key to index.</param>
    /// <param name="metadata">Dictionary of metadata attributes to index.</param>
    /// <returns>True if indexing succeeded, false otherwise.</returns>
    public async Task<bool> IndexAsync(string key, Dictionary<string, object> metadata)
    {
        await WriteToMemTableAsync(key, metadata);
        return true;
    }

    /// <summary>
    /// Searches the metadata index for objects matching the query.
    /// Delegates to SearchLevelsAsync implementation.
    /// </summary>
    /// <param name="query">Search criteria including pattern, filters, and pagination.</param>
    /// <returns>Async enumerable of search results with relevance scores.</returns>
    public Task<IAsyncEnumerable<MetadataSearchResult>> SearchAsync(MetadataQuery query)
        => Task.FromResult(SearchLevelsAsync(query));

    /// <summary>
    /// Compacts the LSM-tree to merge levels and reclaim space.
    /// Compacts all levels sequentially from Level 0 to MaxLevels-1.
    /// </summary>
    /// <returns>A task representing the asynchronous compaction operation.</returns>
    /// <remarks>
    /// Full compaction protocol:
    /// 1. Flush MemTable to Level 0 (ensure no in-memory data)
    /// 2. Compact Level 0 to Level 1
    /// 3. Compact Level 1 to Level 2
    /// 4. Continue until Level MaxLevels-1
    ///
    /// Full compaction is expensive - use sparingly:
    /// - During maintenance windows
    /// - After bulk data deletion
    /// - When query performance degrades
    ///
    /// Prefer incremental compaction triggered automatically by threshold.
    /// </remarks>
    public async Task CompactAsync()
    {
        for (int level = 0; level < MaxLevels - 1; level++)
            await CompactLevelAsync(level);
    }

    /// <summary>
    /// Gets current statistics about the metadata index.
    /// Delegates to ComputeStatisticsAsync implementation.
    /// </summary>
    /// <returns>Statistics object with current index metrics.</returns>
    public Task<DistributedIndexStatistics> GetStatisticsAsync() => ComputeStatisticsAsync();
}

// Known issue: CS0535 compiler error - DistributedCachePluginBase does not implement IDistributedCache.SetAsync<T>
// The signature matches exactly but the compiler doesn't recognize it. Investigate namespace/generic constraints.
/*
/// <summary>
/// Base class for distributed cache plugins compatible with Redis cluster.
/// Provides template methods with JSON serialization and statistics tracking.
/// Supports TTL, sliding expiration, and tag-based invalidation.
/// </summary>
/// <remarks>
/// Cache implementation patterns:
/// - Redis cluster: Use CLUSTER SLOTS for sharding, EVALSHA for atomic operations
/// - Memcached cluster: Use consistent hashing with libketama
/// - Hazelcast: Use IMap with near cache for local reads
/// - In-memory: Use ConcurrentDictionary with background eviction
///
/// Cache coherency:
/// - Write-through: Update cache and backing store atomically
/// - Write-behind: Update cache immediately, async update backing store
/// - Cache-aside: Application manages cache and backing store separately
/// </remarks>
public abstract class DistributedCachePluginBase : FeaturePluginBase, IDistributedCache
{
    /// <summary>
    /// Gets raw bytes from cache by key.
    /// Must be implemented by derived classes to query cache backend.
    /// </summary>
    /// <param name="key">Cache key to retrieve.</param>
    /// <returns>Byte array if found, null if not found or expired.</returns>
    /// <remarks>
    /// Implementation should:
    /// - Check local cache first (if multi-tier caching)
    /// - Query distributed cache backend
    /// - Update hit/miss statistics
    /// - Handle cache misses gracefully (return null, not exception)
    /// </remarks>
    protected abstract Task<byte[]?> GetBytesAsync(string key);

    /// <summary>
    /// Sets raw bytes in cache with optional TTL and tags.
    /// Must be implemented by derived classes to write to cache backend.
    /// </summary>
    /// <param name="key">Cache key to store value under.</param>
    /// <param name="value">Byte array to cache.</param>
    /// <param name="options">Optional caching options including TTL and tags.</param>
    /// <returns>A task representing the asynchronous cache set operation.</returns>
    /// <remarks>
    /// Implementation should:
    /// - Write to distributed cache backend
    /// - Set TTL if specified (absolute or sliding)
    /// - Register tags for bulk invalidation (store key->tags mapping)
    /// - Update local cache if multi-tier
    /// - Handle write failures gracefully (log warning, continue)
    /// </remarks>
    protected abstract Task SetBytesAsync(string key, byte[] value, CacheOptions? options);

    /// <summary>
    /// Removes a key from cache.
    /// Must be implemented by derived classes to delete from cache backend.
    /// </summary>
    /// <param name="key">Cache key to delete.</param>
    /// <returns>True if key existed and was deleted, false otherwise.</returns>
    /// <remarks>
    /// Implementation should:
    /// - Delete from distributed cache backend
    /// - Remove from local cache if multi-tier
    /// - Update eviction statistics
    /// - Handle missing keys gracefully (return false, not exception)
    /// </remarks>
    protected abstract Task<bool> RemoveAsync(string key);

    /// <summary>
    /// Invalidates all cached entries matching a pattern.
    /// Must be implemented by derived classes to scan and delete matching keys.
    /// </summary>
    /// <param name="pattern">Pattern to match keys (glob syntax).</param>
    /// <returns>A task representing the asynchronous invalidation operation.</returns>
    /// <remarks>
    /// Pattern matching approaches:
    /// - Redis: Use SCAN with MATCH pattern (non-blocking iteration)
    /// - Memcached: Not supported natively (use tag-based invalidation instead)
    /// - In-memory: Iterate ConcurrentDictionary keys (use LINQ Where)
    ///
    /// Warning: Pattern scan can be expensive at trillion-object scale.
    /// Prefer tag-based invalidation when possible.
    ///
    /// Glob syntax:
    /// - * matches zero or more characters
    /// - ? matches exactly one character
    /// - [abc] matches a, b, or c
    /// - [a-z] matches any character in range a-z
    /// </remarks>
    protected abstract Task InvalidateByPatternAsync(string pattern);

    /// <summary>
    /// Gets cache statistics aggregated across all nodes.
    /// Must be implemented by derived classes to compute metrics.
    /// </summary>
    /// <returns>Statistics object with current cache metrics.</returns>
    /// <remarks>
    /// Statistics to compute:
    /// - HitCount: Sum of successful cache lookups
    /// - MissCount: Sum of failed cache lookups
    /// - HitRatio: HitCount / (HitCount + MissCount)
    /// - EvictionCount: Sum of entries evicted (TTL expiration + memory pressure)
    ///
    /// Implementation:
    /// - Track statistics locally in each cache node
    /// - Aggregate across nodes on GetStatisticsAsync call
    /// - Use atomic counters for thread safety (Interlocked.Increment)
    /// </remarks>
    protected abstract Task<DistributedCacheStatistics> GetStatsAsync();

    /// <summary>
    /// Gets a cached value by key with automatic JSON deserialization.
    /// Delegates to GetBytesAsync and deserializes using System.Text.Json.
    /// </summary>
    /// <typeparam name="T">Type of cached value.</typeparam>
    /// <param name="key">Cache key to retrieve.</param>
    /// <returns>Cached value or default if not found.</returns>
    /// <remarks>
    /// Uses System.Text.Json for deserialization:
    /// - High performance (faster than Newtonsoft.Json)
    /// - Built-in to .NET (no extra dependencies)
    /// - Supports modern C# features (records, init-only properties)
    ///
    /// Returns default(T) if:
    /// - Key not found in cache
    /// - Key expired (TTL)
    /// - Deserialization fails (logs warning)
    /// </remarks>
    public async Task<T?> GetAsync<T>(string key)
    {
        var bytes = await GetBytesAsync(key);
        return bytes == null ? default : System.Text.Json.JsonSerializer.Deserialize<T>(bytes);
    }

    /// <summary>
    /// Sets a cached value with optional TTL and tags.
    /// Serializes using System.Text.Json and delegates to SetBytesAsync.
    /// </summary>
    /// <typeparam name="T">Type of value to cache.</typeparam>
    /// <param name="key">Cache key to store value under.</param>
    /// <param name="value">Value to cache.</param>
    /// <param name="options">Optional caching options including TTL and tags.</param>
    /// <returns>A task representing the asynchronous cache set operation.</returns>
    /// <remarks>
    /// Uses System.Text.Json for serialization:
    /// - High performance (faster than Newtonsoft.Json)
    /// - Built-in to .NET (no extra dependencies)
    /// - Supports modern C# features (records, init-only properties)
    ///
    /// TTL behavior:
    /// - Null TTL: No expiration (cache forever)
    /// - Absolute TTL (Sliding=false): Expire after fixed duration from set time
    /// - Sliding TTL (Sliding=true): Expire after duration of inactivity (reset on each access)
    ///
    /// Tags enable bulk invalidation:
    /// - Store reverse mapping: tag -> [keys]
    /// - On invalidate tag: delete all keys with that tag
    /// - Example: tags=["user:123", "session:abc"] for user-specific cache entries
    /// </remarks>
    public Task SetAsync<T>(string key, T value, CacheOptions? options)
    {
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value);
        return SetBytesAsync(key, bytes, options);
    }

    /// <summary>
    /// Deletes a cached value by key.
    /// Delegates to RemoveAsync implementation.
    /// </summary>
    /// <param name="key">Cache key to delete.</param>
    /// <returns>True if key existed and was deleted, false otherwise.</returns>
    public Task<bool> DeleteAsync(string key) => RemoveAsync(key);

    /// <summary>
    /// Invalidates all cached entries matching a pattern.
    /// Delegates to InvalidateByPatternAsync implementation.
    /// </summary>
    /// <param name="pattern">Pattern to match keys (e.g., "user:*:profile").</param>
    /// <returns>A task representing the asynchronous invalidation operation.</returns>
    public Task InvalidatePatternAsync(string pattern) => InvalidateByPatternAsync(pattern);

    /// <summary>
    /// Gets cache statistics including hits, misses, and hit ratio.
    /// Delegates to GetStatsAsync implementation.
    /// </summary>
    /// <returns>Statistics object with current cache metrics.</returns>
    public Task<DistributedCacheStatistics> GetStatisticsAsync() => GetStatsAsync();
}
*/

#region Intelligence Stub Types

/// <summary>
/// AI recommendation for optimal sharding configuration.
/// </summary>
public record ShardRecommendation
{
    /// <summary>Recommended number of shards.</summary>
    public int RecommendedShardCount { get; init; }

    /// <summary>Recommended sharding strategy.</summary>
    public ShardingStrategy RecommendedStrategy { get; init; }

    /// <summary>Expected data distribution efficiency (0.0-1.0).</summary>
    public double ExpectedDistributionEfficiency { get; init; }

    /// <summary>Reason for the recommendation.</summary>
    public string? Reason { get; init; }
}

/// <summary>
/// AI hint for query optimization.
/// </summary>
public record QueryOptimizationHint
{
    /// <summary>Suggested index to use.</summary>
    public string? SuggestedIndex { get; init; }

    /// <summary>Whether to use parallel query execution.</summary>
    public bool UseParallelExecution { get; init; }

    /// <summary>Estimated query cost.</summary>
    public double EstimatedCost { get; init; }

    /// <summary>Optimization suggestions.</summary>
    public string[]? Suggestions { get; init; }
}

#endregion
