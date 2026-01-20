namespace DataWarehouse.SDK.Federation;

using System.Collections.Concurrent;

/// <summary>
/// Metrics for a route to a node.
/// </summary>
public sealed class RoutingMetrics
{
    /// <summary>Round-trip latency in milliseconds.</summary>
    public int LatencyMs { get; set; }

    /// <summary>Estimated bandwidth in bytes/sec.</summary>
    public long BandwidthBps { get; set; }

    /// <summary>Cost factor (0.0-1.0, lower is better).</summary>
    public float Cost { get; set; } = 0.5f;

    /// <summary>Number of hops to reach the node.</summary>
    public int Hops { get; set; } = 1;

    /// <summary>Reliability score (0.0-1.0).</summary>
    public float Reliability { get; set; } = 1.0f;

    /// <summary>When metrics were last updated.</summary>
    public DateTimeOffset LastUpdated { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Number of successful transfers.</summary>
    public long SuccessCount { get; set; }

    /// <summary>Number of failed transfers.</summary>
    public long FailureCount { get; set; }

    /// <summary>
    /// Calculates a composite score (lower is better).
    /// </summary>
    public float CalculateScore()
    {
        // Weighted composite: latency most important, then reliability, then cost
        var latencyScore = LatencyMs / 1000f; // Normalize to seconds
        var reliabilityScore = 1f - Reliability;
        var costScore = Cost;
        var hopScore = Hops / 10f;

        return (latencyScore * 0.4f) + (reliabilityScore * 0.3f) + (costScore * 0.2f) + (hopScore * 0.1f);
    }

    /// <summary>
    /// Updates metrics after a transfer.
    /// </summary>
    public void RecordTransfer(bool success, int latencyMs, long bytesTransferred, TimeSpan duration)
    {
        if (success)
        {
            SuccessCount++;
            // Exponential moving average for latency
            LatencyMs = (int)((LatencyMs * 0.8) + (latencyMs * 0.2));
            // Update bandwidth estimate
            if (duration.TotalSeconds > 0)
            {
                var newBandwidth = (long)(bytesTransferred / duration.TotalSeconds);
                BandwidthBps = (long)((BandwidthBps * 0.8) + (newBandwidth * 0.2));
            }
        }
        else
        {
            FailureCount++;
        }

        // Update reliability
        var total = SuccessCount + FailureCount;
        if (total > 0)
        {
            Reliability = (float)SuccessCount / total;
        }

        LastUpdated = DateTimeOffset.UtcNow;
    }
}

/// <summary>
/// A route entry mapping object to nodes.
/// </summary>
public sealed class RouteEntry
{
    /// <summary>Object being routed.</summary>
    public ObjectId ObjectId { get; init; }

    /// <summary>Nodes that have this object.</summary>
    public Dictionary<NodeId, RoutingMetrics> Nodes { get; init; } = new();

    /// <summary>When this entry was created.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When this entry was last accessed.</summary>
    public DateTimeOffset LastAccessedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Gets the best node for this object.
    /// </summary>
    public (NodeId NodeId, RoutingMetrics Metrics)? GetBestNode()
    {
        if (Nodes.Count == 0) return null;

        var best = Nodes
            .OrderBy(kvp => kvp.Value.CalculateScore())
            .First();

        return (best.Key, best.Value);
    }

    /// <summary>
    /// Gets top N nodes for this object.
    /// </summary>
    public IReadOnlyList<(NodeId NodeId, RoutingMetrics Metrics)> GetTopNodes(int count = 3)
    {
        return Nodes
            .OrderBy(kvp => kvp.Value.CalculateScore())
            .Take(count)
            .Select(kvp => (kvp.Key, kvp.Value))
            .ToList();
    }
}

/// <summary>
/// Interface for routing table.
/// </summary>
public interface IRoutingTable
{
    /// <summary>Gets a route for an object.</summary>
    Task<RouteEntry?> GetRouteAsync(ObjectId objectId, CancellationToken ct = default);

    /// <summary>Adds or updates a route.</summary>
    Task UpdateRouteAsync(ObjectId objectId, NodeId nodeId, RoutingMetrics metrics, CancellationToken ct = default);

    /// <summary>Removes a node from a route.</summary>
    Task RemoveRouteAsync(ObjectId objectId, NodeId nodeId, CancellationToken ct = default);

    /// <summary>Gets the best path to an object.</summary>
    Task<RoutingPath?> FindBestPathAsync(ObjectId objectId, CancellationToken ct = default);
}

/// <summary>
/// A path to reach an object.
/// </summary>
public sealed class RoutingPath
{
    /// <summary>Target object.</summary>
    public ObjectId ObjectId { get; init; }

    /// <summary>Target node.</summary>
    public NodeId TargetNodeId { get; init; }

    /// <summary>Intermediate nodes (for multi-hop routing).</summary>
    public List<NodeId> Hops { get; init; } = new();

    /// <summary>Total estimated latency.</summary>
    public int TotalLatencyMs { get; set; }

    /// <summary>Minimum bandwidth along the path.</summary>
    public long MinBandwidthBps { get; set; }

    /// <summary>Total cost.</summary>
    public float TotalCost { get; set; }

    /// <summary>Path reliability (product of hop reliabilities).</summary>
    public float Reliability { get; set; } = 1.0f;
}

/// <summary>
/// Routing table implementation with best-path selection.
/// </summary>
public sealed class RoutingTable : IRoutingTable
{
    private readonly ConcurrentDictionary<ObjectId, RouteEntry> _routes = new();
    private readonly NodeRegistry _nodeRegistry;
    private readonly NodeId _localNodeId;
    private readonly int _maxEntries;

    public RoutingTable(NodeRegistry nodeRegistry, NodeId localNodeId, int maxEntries = 100000)
    {
        _nodeRegistry = nodeRegistry;
        _localNodeId = localNodeId;
        _maxEntries = maxEntries;
    }

    /// <inheritdoc />
    public Task<RouteEntry?> GetRouteAsync(ObjectId objectId, CancellationToken ct = default)
    {
        if (_routes.TryGetValue(objectId, out var entry))
        {
            entry.LastAccessedAt = DateTimeOffset.UtcNow;
            return Task.FromResult<RouteEntry?>(entry);
        }
        return Task.FromResult<RouteEntry?>(null);
    }

    /// <inheritdoc />
    public Task UpdateRouteAsync(ObjectId objectId, NodeId nodeId, RoutingMetrics metrics, CancellationToken ct = default)
    {
        _routes.AddOrUpdate(
            objectId,
            _ => new RouteEntry
            {
                ObjectId = objectId,
                Nodes = { [nodeId] = metrics }
            },
            (_, entry) =>
            {
                entry.Nodes[nodeId] = metrics;
                entry.LastAccessedAt = DateTimeOffset.UtcNow;
                return entry;
            });

        // Evict old entries if over capacity
        if (_routes.Count > _maxEntries)
        {
            EvictOldEntries();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RemoveRouteAsync(ObjectId objectId, NodeId nodeId, CancellationToken ct = default)
    {
        if (_routes.TryGetValue(objectId, out var entry))
        {
            entry.Nodes.Remove(nodeId);
            if (entry.Nodes.Count == 0)
            {
                _routes.TryRemove(objectId, out _);
            }
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<RoutingPath?> FindBestPathAsync(ObjectId objectId, CancellationToken ct = default)
    {
        if (!_routes.TryGetValue(objectId, out var entry))
            return Task.FromResult<RoutingPath?>(null);

        var bestNode = entry.GetBestNode();
        if (!bestNode.HasValue)
            return Task.FromResult<RoutingPath?>(null);

        var (nodeId, metrics) = bestNode.Value;

        // Check if target is directly reachable
        var targetNode = _nodeRegistry.GetNode(nodeId);
        if (targetNode == null || targetNode.State != NodeState.Active)
        {
            // Find indirect path through other nodes
            return Task.FromResult(FindIndirectPath(objectId, nodeId, metrics));
        }

        var path = new RoutingPath
        {
            ObjectId = objectId,
            TargetNodeId = nodeId,
            TotalLatencyMs = metrics.LatencyMs,
            MinBandwidthBps = metrics.BandwidthBps,
            TotalCost = metrics.Cost,
            Reliability = metrics.Reliability
        };

        return Task.FromResult<RoutingPath?>(path);
    }

    private RoutingPath? FindIndirectPath(ObjectId objectId, NodeId targetNodeId, RoutingMetrics targetMetrics)
    {
        // Find active gateway nodes that might reach the target
        var gateways = _nodeRegistry.GetNodesWithCapability(NodeCapabilities.Gateway);

        foreach (var gateway in gateways.Where(g => g.State == NodeState.Active))
        {
            // Simple 2-hop path through gateway
            var path = new RoutingPath
            {
                ObjectId = objectId,
                TargetNodeId = targetNodeId,
                Hops = new List<NodeId> { gateway.Id },
                TotalLatencyMs = targetMetrics.LatencyMs * 2, // Estimate
                MinBandwidthBps = targetMetrics.BandwidthBps / 2,
                TotalCost = targetMetrics.Cost * 1.5f,
                Reliability = targetMetrics.Reliability * 0.9f
            };

            return path;
        }

        return null;
    }

    private void EvictOldEntries()
    {
        var toRemove = _routes
            .OrderBy(kvp => kvp.Value.LastAccessedAt)
            .Take(_routes.Count - _maxEntries + (_maxEntries / 10))
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in toRemove)
        {
            _routes.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// Gets routes for multiple objects.
    /// </summary>
    public async Task<Dictionary<ObjectId, RouteEntry?>> GetRoutesAsync(
        IEnumerable<ObjectId> objectIds,
        CancellationToken ct = default)
    {
        var results = new Dictionary<ObjectId, RouteEntry?>();
        foreach (var objectId in objectIds)
        {
            results[objectId] = await GetRouteAsync(objectId, ct);
        }
        return results;
    }

    /// <summary>
    /// Updates metrics for a node after a transfer.
    /// </summary>
    public void RecordTransfer(ObjectId objectId, NodeId nodeId, bool success, int latencyMs, long bytes, TimeSpan duration)
    {
        if (_routes.TryGetValue(objectId, out var entry))
        {
            if (entry.Nodes.TryGetValue(nodeId, out var metrics))
            {
                metrics.RecordTransfer(success, latencyMs, bytes, duration);
            }
        }
    }
}

/// <summary>
/// Routing policy for selecting paths.
/// </summary>
public enum RoutingPolicy
{
    /// <summary>Prefer lowest latency.</summary>
    LowestLatency,
    /// <summary>Prefer highest bandwidth.</summary>
    HighestBandwidth,
    /// <summary>Prefer lowest cost.</summary>
    LowestCost,
    /// <summary>Prefer highest reliability.</summary>
    HighestReliability,
    /// <summary>Balanced (default composite scoring).</summary>
    Balanced
}

/// <summary>
/// Router combining resolution and routing.
/// </summary>
public sealed class FederationRouter
{
    private readonly IObjectResolver _resolver;
    private readonly IRoutingTable _routingTable;
    private readonly ITransportBus _transportBus;
    private readonly CapabilityVerifier _capabilityVerifier;
    private readonly NodeId _localNodeId;

    public FederationRouter(
        IObjectResolver resolver,
        IRoutingTable routingTable,
        ITransportBus transportBus,
        CapabilityVerifier capabilityVerifier,
        NodeId localNodeId)
    {
        _resolver = resolver;
        _routingTable = routingTable;
        _transportBus = transportBus;
        _capabilityVerifier = capabilityVerifier;
        _localNodeId = localNodeId;
    }

    /// <summary>
    /// Finds the best route to fetch an object.
    /// </summary>
    public async Task<FetchRoute?> FindFetchRouteAsync(
        ObjectId objectId,
        CapabilityToken capability,
        CapabilityContext context,
        RoutingPolicy policy = RoutingPolicy.Balanced,
        CancellationToken ct = default)
    {
        // Verify capability first
        var verification = await _capabilityVerifier.VerifyAsync(capability, context, ct);
        if (!verification.IsValid)
            return null;

        // Check routing table first
        var route = await _routingTable.GetRouteAsync(objectId, ct);
        if (route != null && route.Nodes.Count > 0)
        {
            var bestNode = SelectNodeByPolicy(route, policy);
            if (bestNode.HasValue)
            {
                return new FetchRoute
                {
                    ObjectId = objectId,
                    TargetNodeId = bestNode.Value.NodeId,
                    Metrics = bestNode.Value.Metrics,
                    FromCache = true
                };
            }
        }

        // Fall back to resolution
        var resolution = await _resolver.ResolveAsync(objectId, ct);
        if (!resolution.Found)
            return null;

        // Update routing table with resolution results
        foreach (var location in resolution.Locations)
        {
            await _routingTable.UpdateRouteAsync(objectId, location.NodeId, new RoutingMetrics
            {
                LatencyMs = location.LatencyMs,
                BandwidthBps = location.BandwidthBps,
                Reliability = location.IsPrimary ? 1.0f : 0.9f
            }, ct);
        }

        var bestLocation = resolution.BestLocation;
        if (bestLocation == null)
            return null;

        return new FetchRoute
        {
            ObjectId = objectId,
            TargetNodeId = bestLocation.NodeId,
            Metrics = new RoutingMetrics
            {
                LatencyMs = bestLocation.LatencyMs,
                BandwidthBps = bestLocation.BandwidthBps
            },
            FromCache = false
        };
    }

    private (NodeId NodeId, RoutingMetrics Metrics)? SelectNodeByPolicy(RouteEntry route, RoutingPolicy policy)
    {
        if (route.Nodes.Count == 0) return null;

        var ordered = policy switch
        {
            RoutingPolicy.LowestLatency => route.Nodes.OrderBy(kvp => kvp.Value.LatencyMs),
            RoutingPolicy.HighestBandwidth => route.Nodes.OrderByDescending(kvp => kvp.Value.BandwidthBps),
            RoutingPolicy.LowestCost => route.Nodes.OrderBy(kvp => kvp.Value.Cost),
            RoutingPolicy.HighestReliability => route.Nodes.OrderByDescending(kvp => kvp.Value.Reliability),
            _ => route.Nodes.OrderBy(kvp => kvp.Value.CalculateScore())
        };

        var best = ordered.First();
        return (best.Key, best.Value);
    }

    /// <summary>
    /// Finds routes for storing an object to multiple nodes.
    /// </summary>
    public async Task<IReadOnlyList<StoreRoute>> FindStoreRoutesAsync(
        ObjectId objectId,
        int replicationFactor,
        CapabilityToken capability,
        CapabilityContext context,
        CancellationToken ct = default)
    {
        // Verify capability
        var verification = await _capabilityVerifier.VerifyAsync(capability, context, ct);
        if (!verification.IsValid)
            return Array.Empty<StoreRoute>();

        // Get storage nodes
        var route = await _routingTable.GetRouteAsync(objectId, ct);
        var existingNodes = route?.Nodes.Keys.ToHashSet() ?? new HashSet<NodeId>();

        // We need at least replicationFactor copies
        if (existingNodes.Count >= replicationFactor)
        {
            return existingNodes.Take(replicationFactor)
                .Select(n => new StoreRoute
                {
                    ObjectId = objectId,
                    TargetNodeId = n,
                    IsExisting = true
                })
                .ToList();
        }

        // Find additional storage nodes
        var storageNodes = (await Task.FromResult(new List<NodeIdentity>())).ToList();
        // In real implementation, would query for available storage nodes

        var routes = new List<StoreRoute>();

        // Add existing nodes
        foreach (var nodeId in existingNodes.Take(replicationFactor))
        {
            routes.Add(new StoreRoute
            {
                ObjectId = objectId,
                TargetNodeId = nodeId,
                IsExisting = true
            });
        }

        // Add new nodes as needed
        var needed = replicationFactor - routes.Count;
        foreach (var node in storageNodes.Take(needed))
        {
            routes.Add(new StoreRoute
            {
                ObjectId = objectId,
                TargetNodeId = node.Id,
                IsExisting = false
            });
        }

        return routes;
    }
}

/// <summary>
/// Route for fetching an object.
/// </summary>
public sealed class FetchRoute
{
    /// <summary>Object to fetch.</summary>
    public ObjectId ObjectId { get; init; }

    /// <summary>Node to fetch from.</summary>
    public NodeId TargetNodeId { get; init; }

    /// <summary>Routing metrics.</summary>
    public RoutingMetrics? Metrics { get; init; }

    /// <summary>Whether route came from cache.</summary>
    public bool FromCache { get; init; }
}

/// <summary>
/// Route for storing an object.
/// </summary>
public sealed class StoreRoute
{
    /// <summary>Object to store.</summary>
    public ObjectId ObjectId { get; init; }

    /// <summary>Node to store to.</summary>
    public NodeId TargetNodeId { get; init; }

    /// <summary>Whether object already exists at this node.</summary>
    public bool IsExisting { get; init; }
}
