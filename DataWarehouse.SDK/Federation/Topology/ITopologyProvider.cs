using DataWarehouse.SDK.Contracts;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Federation.Topology;

/// <summary>
/// Defines the contract for retrieving network topology and node metadata.
/// </summary>
/// <remarks>
/// <para>
/// ITopologyProvider is the data source for location-aware routing decisions. It provides
/// topology information for individual nodes, all nodes in the federation, and the local node.
/// </para>
/// <para>
/// <strong>Implementation Sources:</strong> Topology providers may retrieve data from:
/// </para>
/// <list type="bullet">
///   <item><description>Configuration files (static topology)</description></item>
///   <item><description>Discovery services (Consul, etcd, ZooKeeper)</description></item>
///   <item><description>Cloud provider APIs (AWS, Azure, GCP region/zone metadata)</description></item>
///   <item><description>Health monitoring systems (heartbeat aggregators)</description></item>
/// </list>
/// <para>
/// <strong>Caching:</strong> Implementations should cache topology data to avoid excessive
/// lookups on the hot path. Topology changes slowly (nodes added/removed, health updates),
/// so staleness tolerance is acceptable (e.g., 30-60 second cache TTL).
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 34: Topology provider interface for location-aware routing (FOS-04)")]
public interface ITopologyProvider
{
    /// <summary>
    /// Gets the topology metadata for a specific node.
    /// </summary>
    /// <param name="nodeId">The unique identifier of the node to retrieve.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The <see cref="NodeTopology"/> for the specified node, or null if the node is not found
    /// or is offline.
    /// </returns>
    /// <remarks>
    /// This method is used for targeted lookups when routing requires metadata for a specific node.
    /// </remarks>
    Task<NodeTopology?> GetNodeTopologyAsync(string nodeId, CancellationToken ct = default);

    /// <summary>
    /// Gets the topology metadata for all nodes in the federation.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A read-only list of <see cref="NodeTopology"/> entries for all known nodes. The list may
    /// be empty if no nodes are available or the topology provider is uninitialized.
    /// </returns>
    /// <remarks>
    /// <para>
    /// This method is the primary data source for location-aware routing. The router calls this
    /// method to get all candidate nodes, scores them by proximity/health/load, and selects the
    /// best target.
    /// </para>
    /// <para>
    /// Implementations should cache the result to avoid repeated expensive lookups on the hot path.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<NodeTopology>> GetAllNodesAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets the topology metadata for the current (local) node.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The <see cref="NodeTopology"/> for the local node, or null if the local node is not
    /// part of the federation or topology data is unavailable.
    /// </returns>
    /// <remarks>
    /// The local node topology is used as the source/reference point for proximity calculations.
    /// Location-aware routing compares all candidate nodes against the local node to compute
    /// relative proximity scores.
    /// </remarks>
    Task<NodeTopology?> GetSelfTopologyAsync(CancellationToken ct = default);
}
