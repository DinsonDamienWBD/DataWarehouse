using System;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Federation;

/// <summary>
/// Role of a catalog replica in the Raft consensus protocol.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 92: VDE 2.0B Catalog Replication (VFED-07)")]
public enum CatalogReplicaRole : byte
{
    /// <summary>The elected leader that processes write requests and replicates to followers.</summary>
    Leader = 0,

    /// <summary>A follower that replicates log entries from the leader.</summary>
    Follower = 1,

    /// <summary>A candidate requesting votes during a leader election.</summary>
    Candidate = 2,

    /// <summary>A non-voting observer that receives replication but does not participate in elections.</summary>
    Observer = 3
}

/// <summary>
/// Raft replication parameters for catalog VDEs. Defines quorum size, election timing,
/// heartbeat interval, and replica topology for the shard catalog hierarchy.
/// </summary>
/// <remarks>
/// <para>
/// The default configuration uses a 5-replica quorum which tolerates 2 simultaneous failures
/// (quorum = ceil((5+1)/2) = 3). Election timeout is set to 300ms with 100ms heartbeats,
/// following the Raft recommendation that heartbeat &lt;&lt; election timeout &lt;&lt; MTBF.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 92: VDE 2.0B Catalog Replication (VFED-07)")]
public sealed record CatalogReplicationConfig
{
    /// <summary>
    /// Number of replicas in the Raft quorum. Must be an odd number >= 3.
    /// Default is 5 for 2-fault tolerance.
    /// </summary>
    public int ReplicaCount { get; init; } = 5;

    /// <summary>
    /// Raft election timeout. If a follower receives no heartbeat within this period,
    /// it transitions to candidate and starts an election.
    /// </summary>
    public TimeSpan ElectionTimeout { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// Interval at which the leader sends heartbeat messages to followers.
    /// Must be significantly less than <see cref="ElectionTimeout"/>.
    /// </summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Maximum number of log entries to include in a single AppendEntries RPC batch.
    /// </summary>
    public int MaxLogEntriesPerBatch { get; init; } = 1000;

    /// <summary>
    /// The role of the local replica in the Raft cluster.
    /// </summary>
    public CatalogReplicaRole LocalRole { get; init; } = CatalogReplicaRole.Follower;

    /// <summary>
    /// VDE IDs of all replicas in the Raft cluster, including the local replica.
    /// </summary>
    public Guid[] ReplicaVdeIds { get; init; } = Array.Empty<Guid>();

    /// <summary>
    /// Validates the replication configuration, ensuring all invariants hold.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <see cref="ReplicaCount"/> is less than 3, is even,
    /// or when timing constraints are violated.
    /// </exception>
    public void Validate()
    {
        if (ReplicaCount < 3)
            throw new InvalidOperationException(
                $"ReplicaCount must be at least 3 for Raft consensus, but was {ReplicaCount}.");

        if (ReplicaCount % 2 == 0)
            throw new InvalidOperationException(
                $"ReplicaCount must be odd for optimal Raft quorum, but was {ReplicaCount}.");

        if (ElectionTimeout <= TimeSpan.Zero)
            throw new InvalidOperationException("ElectionTimeout must be positive.");

        if (HeartbeatInterval <= TimeSpan.Zero)
            throw new InvalidOperationException("HeartbeatInterval must be positive.");

        if (HeartbeatInterval >= ElectionTimeout)
            throw new InvalidOperationException(
                "HeartbeatInterval must be less than ElectionTimeout for Raft correctness.");

        if (MaxLogEntriesPerBatch <= 0)
            throw new InvalidOperationException("MaxLogEntriesPerBatch must be positive.");
    }
}
