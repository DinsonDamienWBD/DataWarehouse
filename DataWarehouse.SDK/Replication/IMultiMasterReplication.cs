using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Replication
{
    /// <summary>
    /// Consistency models for multi-master replication.
    /// Defines the trade-off between consistency guarantees and performance.
    /// </summary>
    public enum ConsistencyLevel
    {
        /// <summary>
        /// Eventual consistency - fastest, may read stale data.
        /// Writes return immediately, replication happens asynchronously.
        /// Use for highest performance with acceptable eventual consistency.
        /// </summary>
        Eventual,

        /// <summary>
        /// Read-your-writes consistency - session consistency.
        /// A client reading its own writes will always see them.
        /// Use for user-facing applications where users expect to see their own changes.
        /// </summary>
        ReadYourWrites,

        /// <summary>
        /// Causal consistency - respects causality relationships.
        /// If operation B causally depends on operation A, all nodes observe A before B.
        /// Use when operation ordering matters for correctness.
        /// </summary>
        CausalConsistency,

        /// <summary>
        /// Bounded staleness - maximum lag configured.
        /// Guarantees reads are no more than a specified time or version count behind.
        /// Use when you can tolerate bounded lag but need predictable freshness.
        /// </summary>
        BoundedStaleness,

        /// <summary>
        /// Strong consistency - linearizable, slowest but strongest guarantee.
        /// All reads see the most recent committed write, globally ordered.
        /// Use for critical operations requiring strict consistency (e.g., financial transactions).
        /// </summary>
        Strong
    }

    /// <summary>
    /// Conflict resolution strategies for multi-master replication.
    /// Determines how conflicts are detected and resolved when concurrent writes occur.
    /// </summary>
    public enum ConflictResolution
    {
        /// <summary>
        /// Last Writer Wins - timestamp-based resolution.
        /// The write with the latest timestamp wins, others are discarded.
        /// Simple but may lose concurrent updates. Use for commutative operations.
        /// </summary>
        LastWriterWins,

        /// <summary>
        /// Vector Clock - detect and flag conflicts for manual resolution.
        /// Tracks causality using vector clocks, detects true conflicts.
        /// Use when conflicts are rare and require human intervention.
        /// </summary>
        VectorClock,

        /// <summary>
        /// CRDT - Conflict-free Replicated Data Type, automatic merge.
        /// Uses mathematical properties to merge concurrent updates deterministically.
        /// Use for collaborative editing, counters, sets where merges are well-defined.
        /// </summary>
        CRDT,

        /// <summary>
        /// Custom Resolver - application-provided callback.
        /// Application logic determines how to merge conflicting values.
        /// Use when domain-specific logic is required for conflict resolution.
        /// </summary>
        CustomResolver,

        /// <summary>
        /// Manual Resolution - queue conflicts for human review.
        /// Conflicts are stored and require explicit human resolution.
        /// Use for critical data where automated resolution is too risky.
        /// </summary>
        ManualResolution
    }

    /// <summary>
    /// Represents a write operation for replication.
    /// Immutable record containing all information needed to replicate a write across regions.
    /// </summary>
    /// <param name="EventId">Unique identifier for this replication event (globally unique).</param>
    /// <param name="Key">Data key being replicated.</param>
    /// <param name="Data">Binary data payload.</param>
    /// <param name="Clock">Vector clock for causality tracking.</param>
    /// <param name="OriginRegion">Region where the write originated.</param>
    /// <param name="Timestamp">UTC timestamp when the write occurred.</param>
    /// <param name="Metadata">Optional metadata for the write (e.g., content-type, encoding).</param>
    public record ReplicationEvent(
        string EventId,
        string Key,
        byte[] Data,
        VectorClock Clock,
        string OriginRegion,
        DateTimeOffset Timestamp,
        Dictionary<string, string>? Metadata = null
    );

    /// <summary>
    /// Vector clock for causality tracking in distributed systems.
    /// Implements Lamport's vector clock algorithm for detecting concurrent and causally-ordered events.
    /// Immutable record - operations return new instances.
    /// </summary>
    /// <param name="Clocks">Dictionary mapping node IDs to logical clock values.</param>
    public record VectorClock(Dictionary<string, long> Clocks)
    {
        /// <summary>
        /// Increments the clock for a specific node.
        /// Creates a new VectorClock with the incremented value.
        /// </summary>
        /// <param name="nodeId">Node identifier to increment.</param>
        /// <returns>New VectorClock with incremented value.</returns>
        public VectorClock Increment(string nodeId)
        {
            var newClocks = new Dictionary<string, long>(Clocks);
            newClocks[nodeId] = newClocks.GetValueOrDefault(nodeId, 0) + 1;
            return new VectorClock(newClocks);
        }

        /// <summary>
        /// Determines if this vector clock happens-before another.
        /// Returns true if all entries in this clock are less than or equal to the other,
        /// with at least one strictly less.
        /// </summary>
        /// <param name="other">Vector clock to compare against.</param>
        /// <returns>True if this clock happens-before the other.</returns>
        public bool HappensBefore(VectorClock other)
        {
            foreach (var (node, time) in Clocks)
                if (time > other.Clocks.GetValueOrDefault(node, 0)) return false;
            return Clocks.Count > 0;
        }

        /// <summary>
        /// Merges two vector clocks by taking the maximum value for each node.
        /// Used when receiving updates from other nodes to advance local knowledge.
        /// </summary>
        /// <param name="a">First vector clock.</param>
        /// <param name="b">Second vector clock.</param>
        /// <returns>New VectorClock with merged values.</returns>
        public static VectorClock Merge(VectorClock a, VectorClock b)
        {
            var merged = new Dictionary<string, long>(a.Clocks);
            foreach (var (node, time) in b.Clocks)
                merged[node] = Math.Max(merged.GetValueOrDefault(node, 0), time);
            return new VectorClock(merged);
        }
    }

    /// <summary>
    /// Conflict detected during replication.
    /// Represents concurrent writes to the same key from different regions.
    /// </summary>
    /// <param name="Key">Key with conflicting writes.</param>
    /// <param name="LocalVersion">Local region's version of the data.</param>
    /// <param name="RemoteVersion">Remote region's conflicting version.</param>
    /// <param name="SuggestedResolution">Recommended resolution strategy based on configuration.</param>
    /// <param name="DetectedAt">UTC timestamp when conflict was detected.</param>
    public record ReplicationConflict(
        string Key,
        ReplicationEvent LocalVersion,
        ReplicationEvent RemoteVersion,
        ConflictResolution SuggestedResolution,
        DateTimeOffset DetectedAt
    );

    /// <summary>
    /// Result of conflict resolution.
    /// Contains the resolved data or indicates need for human review.
    /// </summary>
    /// <param name="Key">Key that was resolved.</param>
    /// <param name="ResolvedData">Resolved data (null if requires human review).</param>
    /// <param name="ResolvedClock">Merged vector clock after resolution.</param>
    /// <param name="RequiresHumanReview">True if conflict could not be automatically resolved.</param>
    public record ConflictResolutionResult(
        string Key,
        byte[]? ResolvedData,
        VectorClock ResolvedClock,
        bool RequiresHumanReview
    );

    /// <summary>
    /// Multi-master replication manager.
    /// Provides distributed multi-master replication with configurable consistency and conflict resolution.
    /// Supports global distribution with multiple writable regions.
    /// </summary>
    public interface IMultiMasterReplication
    {
        /// <summary>
        /// Gets the local region identifier.
        /// </summary>
        string LocalRegion { get; }

        /// <summary>
        /// Gets the list of connected peer regions.
        /// These are regions actively participating in replication.
        /// </summary>
        IReadOnlyList<string> ConnectedRegions { get; }

        /// <summary>
        /// Gets the default consistency level for operations.
        /// Can be overridden per-operation via WriteOptions/ReadOptions.
        /// </summary>
        ConsistencyLevel DefaultConsistency { get; }

        /// <summary>
        /// Gets the default conflict resolution strategy.
        /// Can be overridden per-key via RegisterConflictResolver.
        /// </summary>
        ConflictResolution DefaultConflictResolution { get; }

        /// <summary>
        /// Writes data with replication to all regions.
        /// Returns after consistency requirements are met (based on options.Consistency).
        /// </summary>
        /// <param name="key">Data key to write.</param>
        /// <param name="data">Binary data payload.</param>
        /// <param name="options">Write options (consistency, conflict resolution, timeout).</param>
        /// <returns>ReplicationEvent representing the successful write.</returns>
        Task<ReplicationEvent> WriteAsync(string key, byte[] data, WriteOptions? options = null);

        /// <summary>
        /// Reads data with specified consistency level.
        /// May return stale data if consistency level permits.
        /// </summary>
        /// <param name="key">Data key to read.</param>
        /// <param name="options">Read options (consistency, max staleness, preferred region).</param>
        /// <returns>ReadResult with data, vector clock, and staleness information.</returns>
        Task<ReadResult> ReadAsync(string key, ReadOptions? options = null);

        /// <summary>
        /// Registers a custom conflict resolver for a specific key.
        /// Resolver is called when conflicts are detected for this key.
        /// </summary>
        /// <param name="key">Key to apply custom resolution to (supports wildcards).</param>
        /// <param name="resolver">Async function that resolves conflicts.</param>
        void RegisterConflictResolver(string key, Func<ReplicationConflict, Task<ConflictResolutionResult>> resolver);

        /// <summary>
        /// Gets pending conflicts requiring human resolution.
        /// Returns conflicts that could not be automatically resolved.
        /// </summary>
        /// <returns>List of conflicts awaiting manual resolution.</returns>
        Task<IReadOnlyList<ReplicationConflict>> GetPendingConflictsAsync();

        /// <summary>
        /// Resolves a conflict manually by providing resolved data.
        /// Updates all regions with the resolved value.
        /// </summary>
        /// <param name="key">Key to resolve.</param>
        /// <param name="resolvedData">Manually resolved data.</param>
        Task ResolveConflictAsync(string key, byte[] resolvedData);

        /// <summary>
        /// Gets the current replication lag to a target region.
        /// Measures how far behind the target region is from the local region.
        /// </summary>
        /// <param name="targetRegion">Region to measure lag for.</param>
        /// <returns>Estimated replication lag (time difference).</returns>
        Task<TimeSpan> GetReplicationLagAsync(string targetRegion);

        /// <summary>
        /// Subscribes to replication events as they occur.
        /// Returns an async stream of ReplicationEvents matching the optional key pattern.
        /// </summary>
        /// <param name="keyPattern">Optional key pattern to filter events (null = all keys).</param>
        /// <param name="ct">Cancellation token to stop subscription.</param>
        /// <returns>Async enumerable stream of replication events.</returns>
        IAsyncEnumerable<ReplicationEvent> SubscribeAsync(string? keyPattern = null, CancellationToken ct = default);
    }

    /// <summary>
    /// Options for write operations.
    /// Allows per-operation override of consistency and conflict resolution.
    /// </summary>
    /// <param name="Consistency">Consistency level for this write (null = use default).</param>
    /// <param name="OnConflict">Conflict resolution strategy (null = use default).</param>
    /// <param name="Timeout">Maximum time to wait for write to complete (null = no timeout).</param>
    /// <param name="TargetRegions">Specific regions to replicate to (null = all regions).</param>
    public record WriteOptions(
        ConsistencyLevel? Consistency = null,
        ConflictResolution? OnConflict = null,
        TimeSpan? Timeout = null,
        string[]? TargetRegions = null
    );

    /// <summary>
    /// Options for read operations.
    /// Allows per-operation override of consistency and staleness tolerance.
    /// </summary>
    /// <param name="Consistency">Consistency level for this read (null = use default).</param>
    /// <param name="MaxStaleness">Maximum acceptable staleness (null = no limit).</param>
    /// <param name="PreferredRegion">Preferred region to read from (null = local region).</param>
    public record ReadOptions(
        ConsistencyLevel? Consistency = null,
        TimeSpan? MaxStaleness = null,
        string? PreferredRegion = null
    );

    /// <summary>
    /// Result of a read operation.
    /// Contains data, metadata, and freshness information.
    /// </summary>
    /// <param name="Key">Key that was read.</param>
    /// <param name="Data">Data payload (null if not found).</param>
    /// <param name="Clock">Vector clock of the returned data.</param>
    /// <param name="ServedByRegion">Region that served this read.</param>
    /// <param name="IsStale">True if data is known to be stale based on consistency requirements.</param>
    public record ReadResult(
        string Key,
        byte[]? Data,
        VectorClock Clock,
        string ServedByRegion,
        bool IsStale
    );
}
