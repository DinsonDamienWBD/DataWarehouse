using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Federation.Routing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Federation.Topology;

/// <summary>
/// Decorator for <see cref="IStorageRouter"/> that performs location-aware node selection.
/// </summary>
/// <remarks>
/// <para>
/// LocationAwareRouter enhances routing with topology awareness. It queries the <see cref="ITopologyProvider"/>
/// for all available nodes, scores them based on proximity to the local node, and selects the best candidate
/// according to the configured <see cref="RoutingPolicy"/>.
/// </para>
/// <para>
/// <strong>Routing Flow:</strong>
/// </para>
/// <list type="number">
///   <item><description>Retrieve local node topology from <see cref="ITopologyProvider.GetSelfTopologyAsync"/></description></item>
///   <item><description>Retrieve all candidate nodes from <see cref="ITopologyProvider.GetAllNodesAsync"/></description></item>
///   <item><description>Filter out unhealthy nodes (health score &lt; 0.1)</description></item>
///   <item><description>Score each candidate using <see cref="ProximityCalculator.CalculateProximityScore"/></description></item>
///   <item><description>Select the node with the highest score</description></item>
///   <item><description>Attach target node hint to request metadata</description></item>
///   <item><description>Delegate to inner router for actual request execution</description></item>
/// </list>
/// <para>
/// <strong>Fallback Behavior:</strong> If topology data is unavailable (no self topology, no candidate nodes),
/// the router falls back to the inner router without modification. This ensures graceful degradation when
/// topology services are unavailable.
/// </para>
/// <para>
/// <strong>Node Hint Metadata:</strong> The selected node ID is attached to <see cref="StorageRequest.Metadata"/>
/// under the key "target-node-id". Downstream routing layers or storage handlers can use this hint to
/// direct the request to the specified node.
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 34: Location-aware routing with topology-based node selection (FOS-04)")]
public sealed class LocationAwareRouter : IStorageRouter
{
    private readonly IStorageRouter _innerRouter;
    private readonly ITopologyProvider _topologyProvider;
    private readonly RoutingPolicy _policy;

    /// <summary>
    /// Initializes a new instance of the <see cref="LocationAwareRouter"/> class.
    /// </summary>
    /// <param name="innerRouter">The inner router to delegate to after node selection.</param>
    /// <param name="topologyProvider">The topology provider for retrieving node metadata.</param>
    /// <param name="policy">
    /// The routing policy to use for node scoring. Default: <see cref="RoutingPolicy.LatencyOptimized"/>.
    /// </param>
    public LocationAwareRouter(
        IStorageRouter innerRouter,
        ITopologyProvider topologyProvider,
        RoutingPolicy policy = RoutingPolicy.LatencyOptimized)
    {
        _innerRouter = innerRouter;
        _topologyProvider = topologyProvider;
        _policy = policy;
    }

    /// <inheritdoc/>
    public async Task<StorageResponse> RouteRequestAsync(StorageRequest request, CancellationToken ct = default)
    {
        // Get self topology and all candidate nodes
        var self = await _topologyProvider.GetSelfTopologyAsync(ct).ConfigureAwait(false);
        var allNodes = await _topologyProvider.GetAllNodesAsync(ct).ConfigureAwait(false);

        if (self == null || allNodes.Count == 0)
        {
            // Topology data unavailable -- fallback to inner router
            return await _innerRouter.RouteRequestAsync(request, ct).ConfigureAwait(false);
        }

        // Filter out unhealthy nodes (health score < 0.1)
        var healthyNodes = allNodes
            .Where(n => n.HealthScore >= 0.1)
            .ToList();

        if (healthyNodes.Count == 0)
        {
            // No healthy nodes available
            return new StorageResponse
            {
                Success = false,
                NodeId = "router",
                ErrorMessage = "No healthy nodes available for routing"
            };
        }

        // Score and sort nodes by proximity (highest score first)
        var scoredNodes = healthyNodes
            .Select(n => new
            {
                Node = n,
                Score = ProximityCalculator.CalculateProximityScore(self, n, _policy)
            })
            .OrderByDescending(x => x.Score)
            .ToList();

        // Select the best node (highest score)
        var bestNode = scoredNodes.First().Node;

        // Attach target node hint to request metadata
        var metadata = request.Metadata != null
            ? new Dictionary<string, string>(request.Metadata)
            : new Dictionary<string, string>();

        metadata["target-node-id"] = bestNode.NodeId;

        var routedRequest = new StorageRequest
        {
            RequestId = request.RequestId,
            Address = request.Address,
            Operation = request.Operation,
            Metadata = metadata,
            LanguageHint = request.LanguageHint,
            UserId = request.UserId,
            TimestampUtc = request.TimestampUtc
        };

        // Delegate to inner router with modified request
        return await _innerRouter.RouteRequestAsync(routedRequest, ct).ConfigureAwait(false);
    }
}
