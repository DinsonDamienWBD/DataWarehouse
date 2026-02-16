using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Federation.Addressing;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Federation.Replication;

/// <summary>
/// Defines the contract for replica selection and fallback chain generation.
/// </summary>
/// <remarks>
/// <para>
/// IReplicaSelector is responsible for choosing the best replica to serve a read request
/// based on consistency level, topology proximity, and node health. It also provides
/// fallback chains for transparent retry when replicas fail.
/// </para>
/// <para>
/// <strong>Selection Strategy:</strong>
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///       <strong>Strong Consistency:</strong> Always select the Raft leader (if available and
///       has a replica). Returns error if leader is unavailable or does not host the object.
///     </description>
///   </item>
///   <item>
///     <description>
///       <strong>Bounded Staleness:</strong> Select the nearest replica (highest proximity score)
///       that has a recent heartbeat (within staleness bound). Excludes stale replicas.
///     </description>
///   </item>
///   <item>
///     <description>
///       <strong>Eventual Consistency:</strong> Select the nearest replica regardless of staleness.
///       Optimizes for lowest latency.
///     </description>
///   </item>
/// </list>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 34: Replica selector interface (FOS-07)")]
public interface IReplicaSelector
{
    /// <summary>
    /// Selects the best replica for a read request based on consistency level and topology.
    /// </summary>
    /// <param name="objectId">The object identity to read.</param>
    /// <param name="consistency">The desired consistency level.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A <see cref="ReplicaSelectionResult"/> containing the selected node ID, reason for
    /// selection, proximity score, and whether the selected node is the Raft leader.
    /// </returns>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown if the object is not found in the manifest, or if strong consistency is requested
    /// but the leader is unavailable or does not have a replica.
    /// </exception>
    /// <remarks>
    /// <para>
    /// This method queries the manifest to get all replicas for the object, scores them based
    /// on proximity to the local node, filters by consistency requirements (staleness bound,
    /// leader-only), and returns the highest-scoring candidate.
    /// </para>
    /// <para>
    /// <strong>Leader Detection:</strong> For strong consistency, the selector queries the
    /// consensus engine (if available) to identify the current Raft leader.
    /// </para>
    /// </remarks>
    Task<ReplicaSelectionResult> SelectReplicaAsync(
        ObjectIdentity objectId,
        ConsistencyLevel consistency,
        CancellationToken ct = default);

    /// <summary>
    /// Gets fallback replicas in order of preference for retry logic.
    /// </summary>
    /// <param name="objectId">The object identity being read.</param>
    /// <param name="failedNodeId">The node ID that failed to serve the read.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A read-only list of node IDs ordered by preference (highest proximity score first).
    /// The failed node is excluded from the list. Returns an empty list if no fallback replicas
    /// are available.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The fallback chain is ordered by proximity score: nearest replicas first, most distant last.
    /// This allows the router to retry failed reads by progressively attempting more distant replicas.
    /// </para>
    /// <para>
    /// <strong>Topology-Aware Ordering:</strong> Replicas in the same rack are tried before replicas
    /// in the same datacenter, which are tried before cross-region replicas.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<string>> GetFallbackChainAsync(
        ObjectIdentity objectId,
        string failedNodeId,
        CancellationToken ct = default);
}

/// <summary>
/// Represents the result of replica selection.
/// </summary>
/// <remarks>
/// <para>
/// ReplicaSelectionResult contains the selected node ID, a human-readable reason explaining
/// why this replica was chosen, the proximity score used for selection, and metadata about
/// the replica (e.g., whether it is the Raft leader).
/// </para>
/// <para>
/// <strong>Reason Examples:</strong>
/// </para>
/// <list type="bullet">
///   <item><description>"Strong consistency requires leader read"</description></item>
///   <item><description>"Selected nearest replica (topology level: SameRack)"</description></item>
///   <item><description>"Self topology unavailable, using first replica"</description></item>
/// </list>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 34: Replica selection result (FOS-07)")]
public sealed record ReplicaSelectionResult
{
    /// <summary>
    /// Gets the node ID of the selected replica.
    /// </summary>
    public required string NodeId { get; init; }

    /// <summary>
    /// Gets a human-readable reason explaining why this replica was selected.
    /// </summary>
    /// <remarks>
    /// Used for observability and debugging. Examples: "Strong consistency requires leader read",
    /// "Selected nearest replica (topology level: SameRack)".
    /// </remarks>
    public required string Reason { get; init; }

    /// <summary>
    /// Gets the proximity score for the selected replica.
    /// </summary>
    /// <remarks>
    /// Proximity scores range from 0.0 (poor candidate) to 1.0 (best candidate). Higher scores
    /// indicate topologically closer or healthier replicas.
    /// </remarks>
    public required double ProximityScore { get; init; }

    /// <summary>
    /// Gets a value indicating whether the selected replica is the current Raft leader.
    /// </summary>
    /// <remarks>
    /// Always true for strong consistency reads. May be true for other consistency levels if
    /// the leader happens to be the nearest replica.
    /// </remarks>
    public bool IsLeader { get; init; }

    /// <summary>
    /// Gets the fallback attempt number (0 for initial selection, 1+ for retries).
    /// </summary>
    /// <remarks>
    /// Used by the router to track how many fallback attempts have been made. Incremented
    /// each time a replica fails and the router retries with the next-best candidate.
    /// </remarks>
    public int FallbackAttempt { get; init; }
}
