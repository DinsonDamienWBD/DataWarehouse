using DataWarehouse.SDK.Contracts;
using System;

namespace DataWarehouse.SDK.Federation.Replication;

/// <summary>
/// Consistency levels for replication-aware reads.
/// </summary>
/// <remarks>
/// <para>
/// ConsistencyLevel controls the read consistency guarantees provided by the replication system.
/// Higher consistency levels provide stronger guarantees but may increase latency.
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///       <strong>Eventual:</strong> Read from any replica (lowest latency, no staleness guarantee).
///     </description>
///   </item>
///   <item>
///     <description>
///       <strong>BoundedStaleness:</strong> Read from replica within configured staleness bound
///       (e.g., replicas updated within last 5 seconds).
///     </description>
///   </item>
///   <item>
///     <description>
///       <strong>Strong:</strong> Read from Raft leader only (linearizable consistency, highest latency).
///     </description>
///   </item>
/// </list>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 34: Consistency level for replication reads (FOS-07)")]
public enum ConsistencyLevel
{
    /// <summary>Read from any replica (lowest latency, eventual consistency)</summary>
    Eventual = 0,

    /// <summary>Read from replica within staleness bound (bounded staleness)</summary>
    BoundedStaleness = 1,

    /// <summary>Read from Raft leader only (strong consistency, linearizable)</summary>
    Strong = 2
}

/// <summary>
/// Configuration for consistency and fallback behavior in replication-aware reads.
/// </summary>
/// <remarks>
/// <para>
/// ConsistencyConfiguration controls the default consistency level, staleness bounds for
/// bounded-staleness reads, and fallback behavior when replica reads fail.
/// </para>
/// <para>
/// <strong>Staleness Bound:</strong> For BoundedStaleness consistency, replicas are only
/// eligible if their last heartbeat was within this duration. Default: 5 seconds.
/// </para>
/// <para>
/// <strong>Fallback:</strong> When a replica read fails, the router automatically retries
/// with the next-best replica up to MaxFallbackAttempts. Each retry has a FallbackTimeout.
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 34: Consistency configuration (FOS-07)")]
public sealed record ConsistencyConfiguration
{
    /// <summary>
    /// Gets the default consistency level for reads when not explicitly specified.
    /// </summary>
    public ConsistencyLevel DefaultLevel { get; init; } = ConsistencyLevel.Eventual;

    /// <summary>
    /// Gets the staleness bound for BoundedStaleness consistency.
    /// </summary>
    /// <remarks>
    /// Replicas with heartbeats older than this duration are excluded from BoundedStaleness reads.
    /// Default: 5 seconds.
    /// </remarks>
    public TimeSpan StalenessBound { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets the maximum number of fallback attempts when a replica read fails.
    /// </summary>
    /// <remarks>
    /// After the initial replica selection fails, the router retries up to this many times
    /// using progressively more distant replicas from the fallback chain. Default: 3.
    /// </remarks>
    public int MaxFallbackAttempts { get; init; } = 3;

    /// <summary>
    /// Gets the timeout for each individual replica read attempt.
    /// </summary>
    /// <remarks>
    /// If a replica does not respond within this duration, the read is considered failed
    /// and the router falls back to the next replica. Default: 2 seconds.
    /// </remarks>
    public TimeSpan FallbackTimeout { get; init; } = TimeSpan.FromSeconds(2);
}
