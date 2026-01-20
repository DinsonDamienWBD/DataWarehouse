using DataWarehouse.SDK.Primitives;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts
{
    /// <summary>
    /// Interface for multi-region geo-replication capabilities.
    /// Provides methods for managing geographic regions, monitoring replication status,
    /// and controlling replication behavior.
    /// </summary>
    public interface IMultiRegionReplication : IPlugin
    {
        /// <summary>
        /// Adds a new region to the replication topology.
        /// </summary>
        /// <param name="regionId">Unique identifier for the region (e.g., "us-east-1", "eu-west-1")</param>
        /// <param name="endpoint">Network endpoint for the region (e.g., "https://us-east-1.example.com")</param>
        /// <param name="config">Region configuration settings</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>Result of the add operation</returns>
        Task<RegionOperationResult> AddRegionAsync(
            string regionId,
            string endpoint,
            RegionConfig config,
            CancellationToken ct = default);

        /// <summary>
        /// Removes a region from the replication topology.
        /// </summary>
        /// <param name="regionId">Region identifier to remove</param>
        /// <param name="drainFirst">If true, waits for pending replication to complete before removal</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>Result of the remove operation</returns>
        Task<RegionOperationResult> RemoveRegionAsync(
            string regionId,
            bool drainFirst = true,
            CancellationToken ct = default);

        /// <summary>
        /// Gets the current replication status across all regions.
        /// </summary>
        /// <param name="ct">Cancellation token</param>
        /// <returns>Comprehensive replication status</returns>
        Task<MultiRegionReplicationStatus> GetReplicationStatusAsync(CancellationToken ct = default);

        /// <summary>
        /// Forces immediate synchronization of a specific key across all regions.
        /// </summary>
        /// <param name="key">Data key to synchronize</param>
        /// <param name="targetRegions">Specific regions to sync to (null = all regions)</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>Sync operation result with per-region status</returns>
        Task<SyncOperationResult> ForceSyncAsync(
            string key,
            string[]? targetRegions = null,
            CancellationToken ct = default);

        /// <summary>
        /// Resolves a conflict for a specific key using the specified strategy.
        /// </summary>
        /// <param name="key">Key with conflicting values</param>
        /// <param name="strategy">Conflict resolution strategy to apply</param>
        /// <param name="winningRegion">For manual resolution, specifies which region's value wins</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>Conflict resolution result</returns>
        Task<ConflictResolutionResult> ResolveConflictAsync(
            string key,
            ConflictResolutionStrategy strategy,
            string? winningRegion = null,
            CancellationToken ct = default);

        /// <summary>
        /// Sets the consistency level for replication operations.
        /// </summary>
        /// <param name="level">Desired consistency level</param>
        /// <param name="ct">Cancellation token</param>
        Task SetConsistencyLevelAsync(ConsistencyLevel level, CancellationToken ct = default);

        /// <summary>
        /// Gets the current consistency level.
        /// </summary>
        ConsistencyLevel GetConsistencyLevel();

        /// <summary>
        /// Triggers a health check for all regions.
        /// </summary>
        /// <param name="ct">Cancellation token</param>
        /// <returns>Per-region health status</returns>
        Task<Dictionary<string, RegionHealth>> CheckRegionHealthAsync(CancellationToken ct = default);

        /// <summary>
        /// Sets bandwidth throttling for cross-region replication.
        /// </summary>
        /// <param name="maxBytesPerSecond">Maximum bytes per second (null = unlimited)</param>
        /// <param name="ct">Cancellation token</param>
        Task SetBandwidthThrottleAsync(long? maxBytesPerSecond, CancellationToken ct = default);

        /// <summary>
        /// Gets the list of all configured regions.
        /// </summary>
        /// <param name="ct">Cancellation token</param>
        /// <returns>List of regions with their configurations</returns>
        Task<List<RegionInfo>> ListRegionsAsync(CancellationToken ct = default);

        /// <summary>
        /// Event triggered when a conflict is detected and resolved.
        /// </summary>
        event EventHandler<ConflictResolvedEventArgs>? ConflictResolved;

        /// <summary>
        /// Event triggered when a region becomes unhealthy or recovers.
        /// </summary>
        event EventHandler<RegionHealthChangedEventArgs>? RegionHealthChanged;
    }

    /// <summary>
    /// Configuration for a geographic region.
    /// </summary>
    public class RegionConfig
    {
        /// <summary>
        /// Region priority (higher priority regions are preferred during leader election).
        /// </summary>
        public int Priority { get; init; } = 100;

        /// <summary>
        /// Whether this region is a witness region (read-only, participates in quorum but not data storage).
        /// </summary>
        public bool IsWitness { get; init; } = false;

        /// <summary>
        /// Expected round-trip latency to this region in milliseconds (for latency-aware routing).
        /// </summary>
        public int ExpectedLatencyMs { get; init; } = 100;

        /// <summary>
        /// Whether to enable automatic failover to this region.
        /// </summary>
        public bool AutoFailover { get; init; } = true;

        /// <summary>
        /// Custom metadata for this region.
        /// </summary>
        public Dictionary<string, string> Metadata { get; init; } = new();
    }

    /// <summary>
    /// Result of a region operation (add/remove).
    /// </summary>
    public class RegionOperationResult
    {
        /// <summary>
        /// Whether the operation succeeded.
        /// </summary>
        public bool Success { get; init; }

        /// <summary>
        /// Error message if operation failed.
        /// </summary>
        public string? ErrorMessage { get; init; }

        /// <summary>
        /// The region ID that was operated on.
        /// </summary>
        public string RegionId { get; init; } = string.Empty;

        /// <summary>
        /// Time taken for the operation.
        /// </summary>
        public TimeSpan Duration { get; init; }
    }

    /// <summary>
    /// Multi-region replication status.
    /// </summary>
    public class MultiRegionReplicationStatus
    {
        /// <summary>
        /// Total number of configured regions.
        /// </summary>
        public int TotalRegions { get; init; }

        /// <summary>
        /// Number of healthy regions.
        /// </summary>
        public int HealthyRegions { get; init; }

        /// <summary>
        /// Current consistency level.
        /// </summary>
        public ConsistencyLevel ConsistencyLevel { get; init; }

        /// <summary>
        /// Per-region status details.
        /// </summary>
        public Dictionary<string, RegionStatus> Regions { get; init; } = new();

        /// <summary>
        /// Number of pending replication operations.
        /// </summary>
        public long PendingOperations { get; init; }

        /// <summary>
        /// Number of conflicts detected.
        /// </summary>
        public long ConflictsDetected { get; init; }

        /// <summary>
        /// Number of conflicts resolved.
        /// </summary>
        public long ConflictsResolved { get; init; }

        /// <summary>
        /// Maximum replication lag across all regions.
        /// </summary>
        public TimeSpan MaxReplicationLag { get; init; }

        /// <summary>
        /// Current bandwidth throttle (bytes per second, null = unlimited).
        /// </summary>
        public long? BandwidthThrottle { get; init; }
    }

    /// <summary>
    /// Status of an individual region.
    /// </summary>
    public class RegionStatus
    {
        /// <summary>
        /// Region identifier.
        /// </summary>
        public string RegionId { get; init; } = string.Empty;

        /// <summary>
        /// Region endpoint.
        /// </summary>
        public string Endpoint { get; init; } = string.Empty;

        /// <summary>
        /// Health status of the region.
        /// </summary>
        public RegionHealth Health { get; init; }

        /// <summary>
        /// Replication lag for this region.
        /// </summary>
        public TimeSpan ReplicationLag { get; init; }

        /// <summary>
        /// Number of pending operations for this region.
        /// </summary>
        public long PendingOperations { get; init; }

        /// <summary>
        /// Last successful sync timestamp.
        /// </summary>
        public DateTime LastSuccessfulSync { get; init; }

        /// <summary>
        /// Number of bytes transferred to this region in the last minute.
        /// </summary>
        public long BytesTransferredLastMinute { get; init; }

        /// <summary>
        /// Whether this region is a witness.
        /// </summary>
        public bool IsWitness { get; init; }

        /// <summary>
        /// Region priority.
        /// </summary>
        public int Priority { get; init; }
    }

    /// <summary>
    /// Information about a configured region.
    /// </summary>
    public class RegionInfo
    {
        /// <summary>
        /// Region identifier.
        /// </summary>
        public string RegionId { get; init; } = string.Empty;

        /// <summary>
        /// Region endpoint.
        /// </summary>
        public string Endpoint { get; init; } = string.Empty;

        /// <summary>
        /// Region configuration.
        /// </summary>
        public RegionConfig Config { get; init; } = new();

        /// <summary>
        /// When the region was added.
        /// </summary>
        public DateTime AddedAt { get; init; }

        /// <summary>
        /// Current health status.
        /// </summary>
        public RegionHealth Health { get; init; }
    }

    /// <summary>
    /// Result of a force sync operation.
    /// </summary>
    public class SyncOperationResult
    {
        /// <summary>
        /// Overall success status.
        /// </summary>
        public bool Success { get; init; }

        /// <summary>
        /// The key that was synchronized.
        /// </summary>
        public string Key { get; init; } = string.Empty;

        /// <summary>
        /// Per-region sync results.
        /// </summary>
        public Dictionary<string, SyncResult> RegionResults { get; init; } = new();

        /// <summary>
        /// Total time taken.
        /// </summary>
        public TimeSpan Duration { get; init; }
    }

    /// <summary>
    /// Sync result for a single region.
    /// </summary>
    public class SyncResult
    {
        /// <summary>
        /// Whether sync succeeded for this region.
        /// </summary>
        public bool Success { get; init; }

        /// <summary>
        /// Error message if sync failed.
        /// </summary>
        public string? ErrorMessage { get; init; }

        /// <summary>
        /// Time taken for this region's sync.
        /// </summary>
        public TimeSpan Duration { get; init; }
    }

    /// <summary>
    /// Result of a conflict resolution operation.
    /// </summary>
    public class ConflictResolutionResult
    {
        /// <summary>
        /// Whether the conflict was successfully resolved.
        /// </summary>
        public bool Success { get; init; }

        /// <summary>
        /// The key that had the conflict.
        /// </summary>
        public string Key { get; init; } = string.Empty;

        /// <summary>
        /// Strategy used for resolution.
        /// </summary>
        public ConflictResolutionStrategy StrategyUsed { get; init; }

        /// <summary>
        /// The region whose value was chosen (if applicable).
        /// </summary>
        public string? WinningRegion { get; init; }

        /// <summary>
        /// Error message if resolution failed.
        /// </summary>
        public string? ErrorMessage { get; init; }
    }

    /// <summary>
    /// Conflict resolution strategies.
    /// </summary>
    public enum ConflictResolutionStrategy
    {
        /// <summary>
        /// Last Write Wins - most recent timestamp wins (default).
        /// </summary>
        LastWriteWins,

        /// <summary>
        /// Highest priority region wins.
        /// </summary>
        HighestPriorityWins,

        /// <summary>
        /// Manual resolution - user specifies winning region.
        /// </summary>
        Manual,

        /// <summary>
        /// Merge values if possible (e.g., for CRDTs).
        /// </summary>
        Merge,

        /// <summary>
        /// Keep all conflicting values (multi-value).
        /// </summary>
        KeepAll
    }

    /// <summary>
    /// Consistency levels for geo-replication.
    /// </summary>
    public enum ConsistencyLevel
    {
        /// <summary>
        /// Eventual consistency - writes return immediately, async replication.
        /// Fastest but may read stale data.
        /// </summary>
        Eventual,

        /// <summary>
        /// Local consistency - waits for local region quorum.
        /// Balances performance and consistency.
        /// </summary>
        Local,

        /// <summary>
        /// Quorum consistency - waits for majority of all regions.
        /// Strong consistency across regions.
        /// </summary>
        Quorum,

        /// <summary>
        /// Strong consistency - waits for all healthy regions.
        /// Slowest but strongest guarantees.
        /// </summary>
        Strong
    }

    /// <summary>
    /// Health status of a region.
    /// </summary>
    public enum RegionHealth
    {
        /// <summary>
        /// Region is healthy and accepting traffic.
        /// </summary>
        Healthy,

        /// <summary>
        /// Region is degraded but still functional.
        /// </summary>
        Degraded,

        /// <summary>
        /// Region is unhealthy and not accepting traffic.
        /// </summary>
        Unhealthy,

        /// <summary>
        /// Region is unknown (not yet checked or unreachable).
        /// </summary>
        Unknown
    }

    /// <summary>
    /// Event args for conflict resolution events.
    /// </summary>
    public class ConflictResolvedEventArgs : EventArgs
    {
        /// <summary>
        /// The key that had the conflict.
        /// </summary>
        public string Key { get; init; } = string.Empty;

        /// <summary>
        /// Regions involved in the conflict.
        /// </summary>
        public string[] ConflictingRegions { get; init; } = Array.Empty<string>();

        /// <summary>
        /// Strategy used for resolution.
        /// </summary>
        public ConflictResolutionStrategy Strategy { get; init; }

        /// <summary>
        /// The region whose value was chosen.
        /// </summary>
        public string WinningRegion { get; init; } = string.Empty;

        /// <summary>
        /// When the conflict was resolved.
        /// </summary>
        public DateTime ResolvedAt { get; init; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Event args for region health changes.
    /// </summary>
    public class RegionHealthChangedEventArgs : EventArgs
    {
        /// <summary>
        /// Region that changed health status.
        /// </summary>
        public string RegionId { get; init; } = string.Empty;

        /// <summary>
        /// Previous health status.
        /// </summary>
        public RegionHealth OldHealth { get; init; }

        /// <summary>
        /// New health status.
        /// </summary>
        public RegionHealth NewHealth { get; init; }

        /// <summary>
        /// When the change occurred.
        /// </summary>
        public DateTime ChangedAt { get; init; } = DateTime.UtcNow;

        /// <summary>
        /// Optional reason for the health change.
        /// </summary>
        public string? Reason { get; init; }
    }
}
