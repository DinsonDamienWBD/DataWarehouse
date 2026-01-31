using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Scale;

/// <summary>
/// Sharding strategies for exabyte-scale deployments.
/// Determines how data is distributed across shards in trillion-object systems.
/// </summary>
public enum ShardingStrategy
{
    /// <summary>
    /// Consistent hashing strategy for even distribution.
    /// Uses Jump Consistent Hash for minimal key reassignment during rebalancing.
    /// </summary>
    ConsistentHash,

    /// <summary>
    /// Range-based partitioning by key ranges.
    /// Efficient for sequential access patterns and range queries.
    /// </summary>
    RangePartition,

    /// <summary>
    /// Directory-based partitioning with explicit mapping.
    /// Allows manual control over data placement for optimization.
    /// </summary>
    DirectoryPartition,

    /// <summary>
    /// Composite strategy combining multiple approaches.
    /// Enables hybrid sharding for complex workload requirements.
    /// </summary>
    Composite
}

/// <summary>
/// Represents the operational state of a shard.
/// Used to track shard lifecycle during rebalancing and maintenance.
/// </summary>
public enum ShardState
{
    /// <summary>
    /// Shard is active and serving read/write requests.
    /// </summary>
    Active,

    /// <summary>
    /// Shard is being migrated to another node.
    /// Continues serving requests during migration.
    /// </summary>
    Migrating,

    /// <summary>
    /// Shard is being drained before decommissioning.
    /// New writes are redirected to other shards.
    /// </summary>
    Draining,

    /// <summary>
    /// Shard is offline and not serving requests.
    /// Used during maintenance or after failure.
    /// </summary>
    Offline,

    /// <summary>
    /// Shard is serving read requests only.
    /// Used during split/merge operations.
    /// </summary>
    ReadOnly
}

/// <summary>
/// Represents a shard in distributed storage.
/// A shard is a horizontal partition of data for trillion-object scale.
/// </summary>
public interface IShard
{
    /// <summary>
    /// Gets the unique identifier for this shard.
    /// Format: {node-id}/{partition-id} for distributed tracking.
    /// </summary>
    string ShardId { get; }

    /// <summary>
    /// Gets the current operational state of the shard.
    /// Determines what operations are allowed on this shard.
    /// </summary>
    ShardState State { get; }

    /// <summary>
    /// Gets the total number of objects stored in this shard.
    /// Updated asynchronously for trillion-object scale efficiency.
    /// </summary>
    long ObjectCount { get; }

    /// <summary>
    /// Gets the total size in bytes of all objects in this shard.
    /// Includes metadata overhead and internal structures.
    /// </summary>
    long TotalSizeBytes { get; }

    /// <summary>
    /// Gets the array of node identifiers hosting replicas of this shard.
    /// Ensures fault tolerance through geographic distribution.
    /// </summary>
    string[] ReplicaNodes { get; }

    /// <summary>
    /// Gets the timestamp of the last rebalancing operation for this shard.
    /// Used to schedule maintenance and prevent rebalance thrashing.
    /// </summary>
    DateTimeOffset LastRebalancedAt { get; }
}

/// <summary>
/// Manages sharding for trillion-object scale deployments.
/// Provides consistent hashing, rebalancing, and shard lifecycle operations.
/// Implements Jump Consistent Hash (Google) for minimal key reassignment.
/// </summary>
public interface IShardManager
{
    /// <summary>
    /// Gets the shard responsible for storing the specified key.
    /// Uses consistent hashing to ensure stable mapping during rebalancing.
    /// </summary>
    /// <param name="key">The object key to locate.</param>
    /// <returns>The shard that should store this key.</returns>
    /// <remarks>
    /// Time complexity: O(1) for consistent hash, O(log n) for range partition.
    /// Result is cached for performance at trillion-object scale.
    /// </remarks>
    Task<IShard> GetShardForKeyAsync(string key);

    /// <summary>
    /// Lists all shards in the distributed system.
    /// Returns a snapshot of current shard topology.
    /// </summary>
    /// <returns>Read-only list of all shards with current state.</returns>
    /// <remarks>
    /// This operation is eventually consistent.
    /// Use for monitoring and rebalancing planning.
    /// </remarks>
    Task<IReadOnlyList<IShard>> ListShardsAsync();

    /// <summary>
    /// Rebalances shards across the cluster to optimize distribution.
    /// Moves data between nodes to equalize load and storage.
    /// </summary>
    /// <param name="options">Configuration for rebalancing operation.</param>
    /// <returns>A task representing the asynchronous rebalancing operation.</returns>
    /// <remarks>
    /// Rebalancing runs in background with minimal service disruption.
    /// Use dry-run mode to preview changes before execution.
    /// Respects MaxConcurrentMigrations to control resource usage.
    /// </remarks>
    Task RebalanceShardsAsync(RebalanceOptions options);

    /// <summary>
    /// Migrates shards from one node to another.
    /// Used for node decommissioning or capacity rebalancing.
    /// </summary>
    /// <param name="fromNode">Source node identifier.</param>
    /// <param name="toNode">Destination node identifier.</param>
    /// <returns>Status object for tracking migration progress.</returns>
    /// <remarks>
    /// Migration is performed with zero downtime using live copying.
    /// Shards remain accessible during migration via dual-read strategy.
    /// </remarks>
    Task<ShardMigrationStatus> MigrateShardsAsync(string fromNode, string toNode);

    /// <summary>
    /// Splits a shard into two smaller shards.
    /// Used when a shard grows beyond optimal size threshold.
    /// </summary>
    /// <param name="shardId">The shard to split.</param>
    /// <returns>A task representing the asynchronous split operation.</returns>
    /// <remarks>
    /// Split operation runs online with read-only mode during final cutover.
    /// New shards inherit replica configuration from parent shard.
    /// </remarks>
    Task SplitShardAsync(string shardId);

    /// <summary>
    /// Merges multiple shards into a single shard.
    /// Used when shards become too small after data deletion.
    /// </summary>
    /// <param name="shardIds">Array of shard identifiers to merge.</param>
    /// <returns>A task representing the asynchronous merge operation.</returns>
    /// <remarks>
    /// All source shards must be on same node for atomic merge.
    /// Merged shard inherits highest replica count from sources.
    /// </remarks>
    Task MergeShardsAsync(string[] shardIds);
}

/// <summary>
/// Distributed metadata index using LSM-tree architecture.
/// Provides trillion-object scale metadata search with Bloom filters and compaction.
/// Optimized for write-heavy workloads with eventual consistency.
/// </summary>
public interface IDistributedMetadataIndex
{
    /// <summary>
    /// Indexes metadata for an object key.
    /// Writes to in-memory MemTable first, then flushes to persistent storage.
    /// </summary>
    /// <param name="key">The object key to index.</param>
    /// <param name="metadata">Dictionary of metadata attributes to index.</param>
    /// <returns>True if indexing succeeded, false otherwise.</returns>
    /// <remarks>
    /// Time complexity: O(log n) for MemTable insertion.
    /// Automatic flush when MemTable reaches configured size threshold.
    /// </remarks>
    Task<bool> IndexAsync(string key, Dictionary<string, object> metadata);

    /// <summary>
    /// Searches the metadata index for objects matching the query.
    /// Returns results as async enumerable for streaming trillion-object results.
    /// </summary>
    /// <param name="query">Search criteria including pattern, filters, and pagination.</param>
    /// <returns>Async enumerable of search results with relevance scores.</returns>
    /// <remarks>
    /// Searches across all LSM levels using Bloom filters for efficiency.
    /// Results are merged and sorted by score across distributed nodes.
    /// Use Limit and Offset for pagination in large result sets.
    /// </remarks>
    Task<IAsyncEnumerable<MetadataSearchResult>> SearchAsync(MetadataQuery query);

    /// <summary>
    /// Compacts the LSM-tree to merge levels and reclaim space.
    /// Merges SSTables across levels to optimize read performance.
    /// </summary>
    /// <returns>A task representing the asynchronous compaction operation.</returns>
    /// <remarks>
    /// Compaction runs in background without blocking reads/writes.
    /// Automatically triggered when level thresholds are exceeded.
    /// Uses size-tiered compaction strategy for trillion-object scale.
    /// </remarks>
    Task CompactAsync();

    /// <summary>
    /// Gets current statistics about the metadata index.
    /// Includes document count, index size, level distribution, and Bloom filter overhead.
    /// </summary>
    /// <returns>Statistics object with current index metrics.</returns>
    /// <remarks>
    /// Statistics are eventually consistent across distributed nodes.
    /// Use for monitoring index health and planning compaction.
    /// </remarks>
    Task<DistributedIndexStatistics> GetStatisticsAsync();
}

/// <summary>
/// Distributed caching layer compatible with Redis cluster.
/// Provides high-throughput caching for metadata and hot data paths.
/// Supports TTL, sliding expiration, and tag-based invalidation.
/// </summary>
public interface IDistributedCache
{
    /// <summary>
    /// Gets a cached value by key with automatic deserialization.
    /// Returns default value if key not found or expired.
    /// </summary>
    /// <typeparam name="T">Type of cached value.</typeparam>
    /// <param name="key">Cache key to retrieve.</param>
    /// <returns>Cached value or default if not found.</returns>
    /// <remarks>
    /// Uses JSON deserialization for complex types.
    /// Cache misses are recorded in statistics for hit ratio tracking.
    /// </remarks>
    Task<T?> GetAsync<T>(string key);

    /// <summary>
    /// Sets a cached value with optional TTL and tags.
    /// Automatically serializes value to JSON for storage.
    /// </summary>
    /// <typeparam name="T">Type of value to cache.</typeparam>
    /// <param name="key">Cache key to store value under.</param>
    /// <param name="value">Value to cache.</param>
    /// <param name="options">Optional caching options including TTL and tags.</param>
    /// <returns>A task representing the asynchronous cache set operation.</returns>
    /// <remarks>
    /// Uses JSON serialization for complex types.
    /// TTL defaults to infinite if not specified.
    /// Tags enable bulk invalidation of related cache entries.
    /// </remarks>
    Task SetAsync<T>(string key, T value, CacheOptions? options = null);

    /// <summary>
    /// Deletes a cached value by key.
    /// </summary>
    /// <param name="key">Cache key to delete.</param>
    /// <returns>True if key existed and was deleted, false otherwise.</returns>
    /// <remarks>
    /// Deletion is synchronous across cache cluster for consistency.
    /// </remarks>
    Task<bool> DeleteAsync(string key);

    /// <summary>
    /// Invalidates all cached entries matching a pattern.
    /// Supports glob-style wildcards for bulk invalidation.
    /// </summary>
    /// <param name="pattern">Pattern to match keys (e.g., "user:*:profile").</param>
    /// <returns>A task representing the asynchronous invalidation operation.</returns>
    /// <remarks>
    /// Pattern matching uses glob syntax: * matches any string, ? matches single char.
    /// Invalidation is eventually consistent across distributed cache nodes.
    /// Use sparingly as pattern scan can be expensive at trillion-object scale.
    /// </remarks>
    Task InvalidatePatternAsync(string pattern);

    /// <summary>
    /// Gets cache statistics including hits, misses, and hit ratio.
    /// </summary>
    /// <returns>Statistics object with current cache metrics.</returns>
    /// <remarks>
    /// Statistics are aggregated across all cache nodes.
    /// Use for monitoring cache effectiveness and tuning.
    /// </remarks>
    Task<DistributedCacheStatistics> GetStatisticsAsync();
}

/// <summary>
/// Configuration options for cached values.
/// </summary>
/// <param name="Ttl">Time-to-live duration. Null means no expiration.</param>
/// <param name="Sliding">Whether TTL resets on each access. Default is false (absolute expiration).</param>
/// <param name="Tags">Optional tags for grouped invalidation. Null means no tags.</param>
public record CacheOptions(
    TimeSpan? Ttl = null,
    bool Sliding = false,
    string[]? Tags = null
);

/// <summary>
/// Cache performance statistics.
/// </summary>
/// <param name="HitCount">Number of cache hits.</param>
/// <param name="MissCount">Number of cache misses.</param>
/// <param name="HitRatio">Hit rate as percentage (0.0 to 1.0).</param>
/// <param name="EvictionCount">Number of entries evicted due to TTL or memory pressure.</param>
public record DistributedCacheStatistics(
    long HitCount,
    long MissCount,
    double HitRatio,
    long EvictionCount
);

/// <summary>
/// LSM-tree index statistics.
/// </summary>
/// <param name="DocumentCount">Total number of indexed documents.</param>
/// <param name="IndexSizeBytes">Total size of all index levels in bytes.</param>
/// <param name="LevelCount">Number of LSM tree levels.</param>
/// <param name="BloomFilterBytes">Memory used by Bloom filters for all levels.</param>
public record DistributedIndexStatistics(
    long DocumentCount,
    long IndexSizeBytes,
    int LevelCount,
    long BloomFilterBytes
);

/// <summary>
/// Search result from metadata index.
/// </summary>
/// <param name="Key">Object key that matched the query.</param>
/// <param name="Metadata">Metadata attributes for the matched object.</param>
/// <param name="Score">Relevance score (0.0 to 1.0, higher is better).</param>
public record MetadataSearchResult(
    string Key,
    Dictionary<string, object> Metadata,
    double Score
);

/// <summary>
/// Query for searching metadata index.
/// </summary>
/// <param name="Pattern">Key pattern to match (glob syntax). Null matches all keys.</param>
/// <param name="Filters">Metadata filters as key-value pairs. All filters must match (AND logic).</param>
/// <param name="Limit">Maximum number of results to return. Default 100.</param>
/// <param name="Offset">Number of results to skip for pagination. Default 0.</param>
public record MetadataQuery(
    string? Pattern,
    Dictionary<string, object>? Filters,
    int Limit = 100,
    int Offset = 0
);

/// <summary>
/// Options for shard rebalancing operations.
/// </summary>
/// <param name="MaxConcurrentMigrations">Maximum number of concurrent shard migrations. Default 5.</param>
/// <param name="DryRun">If true, only plan rebalancing without executing. Default false.</param>
public record RebalanceOptions(
    int MaxConcurrentMigrations = 5,
    bool DryRun = false
);

/// <summary>
/// Status of shard migration operation.
/// </summary>
/// <param name="MigrationId">Unique identifier for this migration operation.</param>
/// <param name="Progress">Number of shards migrated so far.</param>
/// <param name="TotalShards">Total number of shards to migrate.</param>
/// <param name="State">Current state of the migration.</param>
public record ShardMigrationStatus(
    string MigrationId,
    int Progress,
    int TotalShards,
    MigrationState State
);

/// <summary>
/// State of a migration operation.
/// </summary>
public enum MigrationState
{
    /// <summary>
    /// Migration is queued but not yet started.
    /// </summary>
    Pending,

    /// <summary>
    /// Migration is actively transferring data.
    /// </summary>
    InProgress,

    /// <summary>
    /// Migration completed successfully.
    /// </summary>
    Completed,

    /// <summary>
    /// Migration failed with errors.
    /// </summary>
    Failed,

    /// <summary>
    /// Migration was cancelled by user.
    /// </summary>
    Cancelled
}
