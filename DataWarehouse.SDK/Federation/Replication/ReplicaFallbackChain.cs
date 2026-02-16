using DataWarehouse.SDK.Federation.Topology;
using System.Collections.Generic;
using System.Linq;

namespace DataWarehouse.SDK.Federation.Replication;

/// <summary>
/// Builds ordered fallback chains for replica selection.
/// </summary>
/// <remarks>
/// <para>
/// ReplicaFallbackChain constructs an ordered list of fallback replicas based on topology
/// proximity. When a replica fails to serve a read, the router uses this chain to select
/// the next-best replica for retry.
/// </para>
/// <para>
/// <strong>Ordering Strategy:</strong> Replicas are scored by proximity to the local node
/// using the same algorithm as initial replica selection. The fallback chain is ordered
/// by descending proximity score (nearest first, most distant last).
/// </para>
/// </remarks>
internal static class ReplicaFallbackChain
{
    /// <summary>
    /// Builds a fallback chain for a failed replica.
    /// </summary>
    /// <param name="failedNodeId">The node ID that failed to serve the read.</param>
    /// <param name="allReplicas">All replica node IDs for the object.</param>
    /// <param name="self">The local node topology.</param>
    /// <param name="topologyProvider">The topology provider for retrieving node metadata.</param>
    /// <returns>
    /// A list of node IDs ordered by preference (highest proximity score first). The failed
    /// node is excluded. Returns an empty list if no other replicas are available.
    /// </returns>
    /// <remarks>
    /// <para>
    /// This method filters out the failed node, queries topology metadata for each remaining
    /// replica, scores them by proximity, and returns a sorted list.
    /// </para>
    /// <para>
    /// <strong>Synchronous Topology Lookup:</strong> This method uses blocking synchronous
    /// calls to <see cref="ITopologyProvider.GetNodeTopologyAsync"/>. This is acceptable
    /// because fallback chain construction happens on the fallback path (after a failure),
    /// not on the hot path for initial selection.
    /// </para>
    /// </remarks>
    public static List<string> Build(
        string failedNodeId,
        IReadOnlyList<string> allReplicas,
        NodeTopology self,
        ITopologyProvider topologyProvider)
    {
        var replicas = allReplicas.Where(id => id != failedNodeId).ToList();

        // Score replicas by proximity
        var scored = new List<(string NodeId, double Score)>();
        foreach (var replicaId in replicas)
        {
            // Blocking call acceptable on fallback path
            var topology = topologyProvider.GetNodeTopologyAsync(replicaId).Result;
            if (topology == null) continue;

            var score = ProximityCalculator.CalculateProximityScore(self, topology, RoutingPolicy.LatencyOptimized);
            scored.Add((replicaId, score));
        }

        // Sort by score descending (highest score = nearest replica)
        return scored.OrderByDescending(x => x.Score).Select(x => x.NodeId).ToList();
    }
}
