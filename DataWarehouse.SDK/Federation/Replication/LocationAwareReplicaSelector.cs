using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Federation.Addressing;
using DataWarehouse.SDK.Federation.Catalog;
using DataWarehouse.SDK.Federation.Topology;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Federation.Replication;

/// <summary>
/// Location-aware replica selector with topology-based preference.
/// </summary>
/// <remarks>
/// <para>
/// LocationAwareReplicaSelector implements <see cref="IReplicaSelector"/> using topology
/// awareness to prefer nearby replicas and consistency-level-aware filtering to enforce
/// staleness bounds or leader-only reads.
/// </para>
/// <para>
/// <strong>Selection Strategy:</strong>
/// </para>
/// <list type="number">
///   <item><description>Query manifest for object location (all replica node IDs)</description></item>
///   <item><description>For strong consistency: filter to Raft leader only</description></item>
///   <item><description>For bounded staleness: filter out replicas with stale heartbeats</description></item>
///   <item><description>Score remaining replicas by proximity (topology distance from local node)</description></item>
///   <item><description>Return the highest-scoring replica</description></item>
/// </list>
/// <para>
/// <strong>Fallback Chain:</strong> When a replica fails, the selector builds a fallback chain
/// ordered by proximity score. The router retries with progressively more distant replicas
/// until success or max attempts is reached.
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 34: Location-aware replica selector (FOS-07)")]
public sealed class LocationAwareReplicaSelector : IReplicaSelector
{
    private readonly IManifestService _manifest;
    private readonly ITopologyProvider _topology;
    private readonly IConsensusEngine? _raft;
    private readonly ConsistencyConfiguration _config;

    /// <summary>
    /// Initializes a new instance of the <see cref="LocationAwareReplicaSelector"/> class.
    /// </summary>
    /// <param name="manifest">The manifest service for object location lookups.</param>
    /// <param name="topology">The topology provider for node metadata.</param>
    /// <param name="raft">
    /// Optional consensus engine for leader detection. If null, strong consistency reads
    /// will fail with an error.
    /// </param>
    /// <param name="config">
    /// Optional consistency configuration. If null, uses default configuration (Eventual
    /// consistency, 5s staleness bound, 3 fallback attempts, 2s timeout).
    /// </param>
    public LocationAwareReplicaSelector(
        IManifestService manifest,
        ITopologyProvider topology,
        IConsensusEngine? raft = null,
        ConsistencyConfiguration? config = null)
    {
        _manifest = manifest;
        _topology = topology;
        _raft = raft;
        _config = config ?? new ConsistencyConfiguration();
    }

    /// <inheritdoc/>
    public async Task<ReplicaSelectionResult> SelectReplicaAsync(
        ObjectIdentity objectId,
        ConsistencyLevel consistency,
        CancellationToken ct = default)
    {
        // Get object location from manifest
        var location = await _manifest.GetLocationAsync(objectId, ct).ConfigureAwait(false);
        if (location == null || location.NodeIds.Count == 0)
        {
            throw new InvalidOperationException($"Object {objectId} not found in manifest");
        }

        // Strong consistency: always read from leader
        if (consistency == ConsistencyLevel.Strong)
        {
            var leaderId = await GetLeaderNodeIdAsync(ct).ConfigureAwait(false);
            if (leaderId == null || !location.NodeIds.Contains(leaderId))
            {
                throw new InvalidOperationException("Leader not available or does not have replica");
            }

            return new ReplicaSelectionResult
            {
                NodeId = leaderId,
                Reason = "Strong consistency requires leader read",
                ProximityScore = 1.0,
                IsLeader = true
            };
        }

        // Eventual or bounded staleness: prefer nearest replica
        var self = await _topology.GetSelfTopologyAsync(ct).ConfigureAwait(false);
        if (self == null)
        {
            // Fallback to first available replica
            return new ReplicaSelectionResult
            {
                NodeId = location.NodeIds.First(),
                Reason = "Self topology unavailable, using first replica",
                ProximityScore = 0.5
            };
        }

        // Score replicas by proximity
        var scoredReplicas = new List<(string NodeId, double Score, NodeTopology Topology)>();
        foreach (var replicaId in location.NodeIds)
        {
            var replicaTopology = await _topology.GetNodeTopologyAsync(replicaId, ct).ConfigureAwait(false);
            if (replicaTopology == null) continue;

            // Check staleness for bounded consistency
            if (consistency == ConsistencyLevel.BoundedStaleness)
            {
                var staleness = DateTimeOffset.UtcNow - replicaTopology.LastHeartbeat;
                if (staleness > _config.StalenessBound)
                    continue;  // Skip stale replica
            }

            var score = ProximityCalculator.CalculateProximityScore(self, replicaTopology, RoutingPolicy.LatencyOptimized);
            scoredReplicas.Add((replicaId, score, replicaTopology));
        }

        if (scoredReplicas.Count == 0)
        {
            throw new InvalidOperationException("No suitable replicas available for consistency level");
        }

        // Select best (highest score)
        var best = scoredReplicas.OrderByDescending(x => x.Score).First();

        return new ReplicaSelectionResult
        {
            NodeId = best.NodeId,
            Reason = $"Selected nearest replica (topology level: {best.Topology.GetLevelRelativeTo(self)})",
            ProximityScore = best.Score
        };
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<string>> GetFallbackChainAsync(
        ObjectIdentity objectId,
        string failedNodeId,
        CancellationToken ct = default)
    {
        var location = await _manifest.GetLocationAsync(objectId, ct).ConfigureAwait(false);
        if (location == null) return Array.Empty<string>();

        var self = await _topology.GetSelfTopologyAsync(ct).ConfigureAwait(false);
        if (self == null) return location.NodeIds.Where(id => id != failedNodeId).ToList();

        return await ReplicaFallbackChain.BuildAsync(failedNodeId, location.NodeIds, self, _topology);
    }

    /// <summary>
    /// Gets the current Raft leader node ID.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The node ID of the current Raft leader, or null if the consensus engine is not available
    /// or no leader is elected.
    /// </returns>
    /// <remarks>
    /// <para>
    /// This method queries the consensus engine to identify the current Raft leader. The leader
    /// is the only node that can serve strong consistency reads (linearizable reads).
    /// </para>
    /// <para>
    /// <strong>Placeholder Implementation:</strong> The current implementation returns null
    /// because <see cref="IConsensusEngine"/> does not expose a GetLeaderNodeId method.
    /// Future work: extend IConsensusEngine with leader discovery API.
    /// </para>
    /// </remarks>
    private async Task<string?> GetLeaderNodeIdAsync(CancellationToken ct)
    {
        if (_raft == null) return null;

        // Check if this node is the Raft leader
        if (_raft.IsLeader)
        {
            // Return self node ID from topology
            var self = await _topology.GetSelfTopologyAsync(ct).ConfigureAwait(false);
            return self?.NodeId;
        }

        // Current node is not leader; IConsensusEngine does not expose leader discovery.
        // For strong consistency, callers should retry on the leader node.
        return null;
    }
}
