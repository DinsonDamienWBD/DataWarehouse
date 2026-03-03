using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.Federation;

/// <summary>
/// Node type within the federation hierarchy.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 92: VDE 2.0B+ Super-Federation Router (VFED-07)")]
public enum FederationNodeType : byte
{
    /// <summary>VDE 2.0A data shard (terminal node).</summary>
    LeafVde = 0,

    /// <summary>VDE 2.0B federation (contains sub-nodes, routes via VdeFederationRouter).</summary>
    Federation = 1,

    /// <summary>VDE 2.0B+ super-federation (contains federations, enables recursive nesting).</summary>
    SuperFederation = 2
}

/// <summary>
/// Result of resolving a namespace path through the federation hierarchy.
/// Exposes only the leaf shard VDE ID and inode number, hiding intermediate routing layers.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 92: VDE 2.0B+ Super-Federation Router (VFED-07)")]
public readonly record struct FederationResolveResult(
    Guid LeafShardVdeId,
    long InodeNumber,
    int HopsUsed,
    bool WasCacheHit);

/// <summary>
/// Aggregate statistics for a single node in the federation hierarchy.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 92: VDE 2.0B+ Super-Federation Router (VFED-07)")]
public readonly record struct FederationNodeStats(
    long TotalShardCount,
    long TotalObjectCount,
    long TotalCapacityBytes,
    long UsedCapacityBytes,
    int MaxDepth);

/// <summary>
/// Abstraction for a node in the recursive federation tree.
/// Each node can be a leaf VDE (terminal), a federation (VDE 2.0B), or a super-federation (VDE 2.0B+).
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 92: VDE 2.0B+ Super-Federation Router (VFED-07)")]
public interface IFederationNode
{
    /// <summary>Unique identifier for this node.</summary>
    Guid NodeId { get; }

    /// <summary>The type of this federation node.</summary>
    FederationNodeType NodeType { get; }

    /// <summary>Depth in the federation tree (0 = root).</summary>
    int Depth { get; }

    /// <summary>
    /// Resolves a namespace path to a leaf shard address.
    /// </summary>
    /// <param name="namespacePath">The full namespace path to resolve.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The resolve result targeting the leaf shard.</returns>
    Task<FederationResolveResult> ResolveAsync(string namespacePath, CancellationToken ct = default);

    /// <summary>
    /// Returns the immediate children of this node.
    /// </summary>
    Task<IReadOnlyList<IFederationNode>> GetChildrenAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns aggregate statistics for this node and all descendants.
    /// </summary>
    Task<FederationNodeStats> GetStatsAsync(CancellationToken ct = default);
}

/// <summary>
/// Leaf node wrapping a single VDE 2.0A instance. Terminal in the federation tree.
/// Resolution returns the VDE ID directly; inode resolution is performed by the target VDE itself.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 92: VDE 2.0B+ Super-Federation Router (VFED-07)")]
internal sealed class LeafVdeNode : IFederationNode
{
    private readonly Guid _vdeId;
    private readonly int _depth;
    private readonly Func<CancellationToken, Task<FederationNodeStats>>? _statsProvider;

    /// <summary>
    /// Creates a leaf node for a VDE 2.0A instance.
    /// </summary>
    /// <param name="vdeId">The VDE instance identifier.</param>
    /// <param name="depth">Depth in the federation tree.</param>
    /// <param name="statsProvider">
    /// Optional callback to retrieve stats for this VDE instance.
    /// When null, returns zeroed stats.
    /// </param>
    public LeafVdeNode(Guid vdeId, int depth, Func<CancellationToken, Task<FederationNodeStats>>? statsProvider = null)
    {
        if (vdeId == Guid.Empty)
            throw new ArgumentException("VDE ID must not be empty.", nameof(vdeId));
        if (depth < 0)
            throw new ArgumentOutOfRangeException(nameof(depth), "Depth must be non-negative.");

        _vdeId = vdeId;
        _depth = depth;
        _statsProvider = statsProvider;
    }

    /// <inheritdoc />
    public Guid NodeId => _vdeId;

    /// <inheritdoc />
    public FederationNodeType NodeType => FederationNodeType.LeafVde;

    /// <inheritdoc />
    public int Depth => _depth;

    /// <inheritdoc />
    public Task<FederationResolveResult> ResolveAsync(string namespacePath, CancellationToken ct = default)
    {
        // Terminal: return the VDE ID. InodeNumber=0 because the target VDE resolves inodes internally.
        return Task.FromResult(new FederationResolveResult(
            LeafShardVdeId: _vdeId,
            InodeNumber: 0,
            HopsUsed: 0,
            WasCacheHit: false));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<IFederationNode>> GetChildrenAsync(CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<IFederationNode>>(Array.Empty<IFederationNode>());
    }

    /// <inheritdoc />
    public async Task<FederationNodeStats> GetStatsAsync(CancellationToken ct = default)
    {
        if (_statsProvider is not null)
            return await _statsProvider(ct).ConfigureAwait(false);

        return new FederationNodeStats(
            TotalShardCount: 1,
            TotalObjectCount: 0,
            TotalCapacityBytes: 0,
            UsedCapacityBytes: 0,
            MaxDepth: 0);
    }
}

/// <summary>
/// Federation node wrapping a VDE 2.0B instance (VdeFederationRouter).
/// Routes namespace paths through the contained federation router and exposes
/// the router's shards as leaf children.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 92: VDE 2.0B+ Super-Federation Router (VFED-07)")]
internal sealed class FederationNode : IFederationNode
{
    private readonly Guid _federationId;
    private readonly VdeFederationRouter _router;
    private readonly int _depth;
    private readonly IReadOnlyList<LeafVdeNode> _leafChildren;

    /// <summary>
    /// Creates a federation node wrapping a VdeFederationRouter.
    /// </summary>
    /// <param name="federationId">The federation instance identifier.</param>
    /// <param name="router">The VDE 2.0B router for this federation.</param>
    /// <param name="depth">Depth in the federation tree.</param>
    /// <param name="leafChildren">
    /// The leaf VDE shards managed by this federation.
    /// Provided externally because VdeFederationRouter does not expose its shard list directly.
    /// </param>
    public FederationNode(
        Guid federationId,
        VdeFederationRouter router,
        int depth,
        IReadOnlyList<LeafVdeNode>? leafChildren = null)
    {
        if (federationId == Guid.Empty)
            throw new ArgumentException("Federation ID must not be empty.", nameof(federationId));
        ArgumentNullException.ThrowIfNull(router);
        if (depth < 0)
            throw new ArgumentOutOfRangeException(nameof(depth), "Depth must be non-negative.");

        _federationId = federationId;
        _router = router;
        _depth = depth;
        _leafChildren = leafChildren ?? Array.Empty<LeafVdeNode>();
    }

    /// <inheritdoc />
    public Guid NodeId => _federationId;

    /// <inheritdoc />
    public FederationNodeType NodeType => FederationNodeType.Federation;

    /// <inheritdoc />
    public int Depth => _depth;

    /// <inheritdoc />
    public async Task<FederationResolveResult> ResolveAsync(string namespacePath, CancellationToken ct = default)
    {
        // Delegate to the VdeFederationRouter which resolves to a ShardAddress
        ShardAddress address = await _router.ResolveAsync(namespacePath, ct).ConfigureAwait(false);

        return new FederationResolveResult(
            LeafShardVdeId: address.VdeId,
            InodeNumber: address.InodeNumber,
            HopsUsed: 1, // One hop for this federation level
            WasCacheHit: false);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<IFederationNode>> GetChildrenAsync(CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<IFederationNode>>(_leafChildren);
    }

    /// <inheritdoc />
    public Task<FederationNodeStats> GetStatsAsync(CancellationToken ct = default)
    {
        FederationRouterStats routerStats = _router.GetStats();

        return Task.FromResult(new FederationNodeStats(
            TotalShardCount: _leafChildren.Count,
            TotalObjectCount: routerStats.CacheHits + routerStats.CacheMisses,
            TotalCapacityBytes: 0,
            UsedCapacityBytes: 0,
            MaxDepth: 1));
    }
}

/// <summary>
/// Top-level recursive router for VDE 2.0B+ super-federation composition.
/// Routes namespace paths through nested federation layers using longest-prefix matching.
/// </summary>
/// <remarks>
/// <para>
/// A single VDE 2.0B federation has a practical shard limit. To scale beyond that,
/// VDE 2.0B+ nests federations recursively: each entry in the super-federation's routing
/// table can be a leaf VDE 2.0A, a VDE 2.0B federation, or another super-federation.
/// This enables unbounded horizontal scaling from yottabyte to brontobyte capacity.
/// </para>
/// <para>
/// Routing uses longest-prefix match on namespace paths. The routing table is a sorted list
/// iterated in reverse (longest first). At super-federation scale, the number of entries
/// is expected to be small (tens to hundreds), making O(n) iteration acceptable.
/// A trie-based implementation can replace this if needed.
/// </para>
/// <para>
/// Single-VDE and single-federation deployments never instantiate SuperFederationRouter.
/// It is only created when admin explicitly configures multi-federation topology.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 92: VDE 2.0B+ Super-Federation Router (VFED-07)")]
public sealed class SuperFederationRouter : IFederationNode
{
    /// <summary>
    /// Maximum number of hops allowed during recursive resolution.
    /// Prevents infinite recursion from misconfigured circular references.
    /// </summary>
    public const int DefaultMaxHops = 10;

    private readonly Guid _superFederationId;
    private readonly SortedList<string, IFederationNode> _routingTable;
    private readonly Dictionary<Guid, IFederationNode> _nodesByIdCache;
    private readonly int _maxHops;
    private int _depth;

    /// <summary>
    /// Creates a super-federation router.
    /// </summary>
    /// <param name="superFederationId">Unique identifier for this super-federation.</param>
    /// <param name="maxHops">Maximum allowed hops during resolution (default 10).</param>
    public SuperFederationRouter(Guid superFederationId, int maxHops = DefaultMaxHops)
    {
        if (superFederationId == Guid.Empty)
            throw new ArgumentException("Super-federation ID must not be empty.", nameof(superFederationId));
        if (maxHops < 1)
            throw new ArgumentOutOfRangeException(nameof(maxHops), "Max hops must be at least 1.");

        _superFederationId = superFederationId;
        _maxHops = maxHops;
        _routingTable = new SortedList<string, IFederationNode>(StringComparer.Ordinal);
        _nodesByIdCache = new Dictionary<Guid, IFederationNode>();
    }

    /// <inheritdoc />
    public Guid NodeId => _superFederationId;

    /// <inheritdoc />
    public FederationNodeType NodeType => FederationNodeType.SuperFederation;

    /// <inheritdoc />
    public int Depth => _depth;

    /// <summary>
    /// Number of direct child nodes in the routing table.
    /// </summary>
    public int NodeCount => _routingTable.Count;

    /// <summary>
    /// The configured maximum hop count for recursive resolution.
    /// </summary>
    public int MaxHops => _maxHops;

    /// <summary>
    /// Adds a child node (federation or leaf) to the routing table under the given namespace prefix.
    /// </summary>
    /// <param name="namespacePrefix">
    /// The namespace prefix this node handles. Must be non-empty.
    /// Longest-prefix match is used during resolution.
    /// </param>
    /// <param name="node">The federation node to route to.</param>
    /// <exception cref="ArgumentException">Thrown when prefix is empty or already exists.</exception>
    /// <exception cref="ArgumentNullException">Thrown when node is null.</exception>
    public void AddNode(string namespacePrefix, IFederationNode node)
    {
        if (string.IsNullOrEmpty(namespacePrefix))
            throw new ArgumentException("Namespace prefix must not be empty.", nameof(namespacePrefix));
        ArgumentNullException.ThrowIfNull(node);

        if (_routingTable.ContainsKey(namespacePrefix))
            throw new ArgumentException($"Namespace prefix '{namespacePrefix}' is already registered.", nameof(namespacePrefix));

        _routingTable.Add(namespacePrefix, node);
        _nodesByIdCache[node.NodeId] = node;
        _depth = Math.Max(_depth, node.Depth + 1);
    }

    /// <summary>
    /// Removes a routing entry. Used during shard decommission or federation merge.
    /// </summary>
    /// <param name="namespacePrefix">The namespace prefix to remove.</param>
    /// <returns>True if the entry was removed; false if not found.</returns>
    public bool RemoveNode(string namespacePrefix)
    {
        if (string.IsNullOrEmpty(namespacePrefix))
            return false;

        if (_routingTable.TryGetValue(namespacePrefix, out IFederationNode? node))
        {
            _routingTable.Remove(namespacePrefix);
            _nodesByIdCache.Remove(node.NodeId);

            // Recalculate depth from remaining nodes
            _depth = _routingTable.Count > 0
                ? _routingTable.Values.Max(n => n.Depth) + 1
                : 0;

            return true;
        }

        return false;
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">
    /// Thrown when no node handles the given path, or when resolution exceeds the maximum hop count.
    /// </exception>
    public async Task<FederationResolveResult> ResolveAsync(string namespacePath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(namespacePath);

        return await ResolveInternalAsync(namespacePath, 0, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<IFederationNode>> GetChildrenAsync(CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<IFederationNode>>(_routingTable.Values.ToList().AsReadOnly());
    }

    /// <inheritdoc />
    public async Task<FederationNodeStats> GetStatsAsync(CancellationToken ct = default)
    {
        if (_routingTable.Count == 0)
        {
            return new FederationNodeStats(
                TotalShardCount: 0,
                TotalObjectCount: 0,
                TotalCapacityBytes: 0,
                UsedCapacityBytes: 0,
                MaxDepth: 0);
        }

        // Fan out stats collection to all children in parallel
        Task<FederationNodeStats>[] tasks = new Task<FederationNodeStats>[_routingTable.Count];
        int i = 0;
        foreach (IFederationNode child in _routingTable.Values)
        {
            tasks[i++] = child.GetStatsAsync(ct);
        }

        FederationNodeStats[] childStats = await Task.WhenAll(tasks).ConfigureAwait(false);

        long totalShards = 0;
        long totalObjects = 0;
        long totalCapacity = 0;
        long usedCapacity = 0;
        int maxChildDepth = 0;

        for (int j = 0; j < childStats.Length; j++)
        {
            totalShards += childStats[j].TotalShardCount;
            totalObjects += childStats[j].TotalObjectCount;
            totalCapacity += childStats[j].TotalCapacityBytes;
            usedCapacity += childStats[j].UsedCapacityBytes;
            if (childStats[j].MaxDepth > maxChildDepth)
                maxChildDepth = childStats[j].MaxDepth;
        }

        return new FederationNodeStats(
            TotalShardCount: totalShards,
            TotalObjectCount: totalObjects,
            TotalCapacityBytes: totalCapacity,
            UsedCapacityBytes: usedCapacity,
            MaxDepth: maxChildDepth + 1);
    }

    /// <summary>
    /// Finds a node by its unique identifier, searching recursively through the tree.
    /// </summary>
    /// <param name="nodeId">The node identifier to search for.</param>
    /// <returns>The matching node, or null if not found.</returns>
    public IFederationNode? FindNode(Guid nodeId)
    {
        if (nodeId == _superFederationId)
            return this;

        if (_nodesByIdCache.TryGetValue(nodeId, out IFederationNode? cached))
            return cached;

        // Recursively search children (for nested super-federations)
        foreach (IFederationNode child in _routingTable.Values)
        {
            if (child is SuperFederationRouter childSuper)
            {
                IFederationNode? found = childSuper.FindNode(nodeId);
                if (found is not null)
                    return found;
            }
        }

        return null;
    }

    /// <summary>
    /// Creates a two-level super-federation with FederationNode children.
    /// Convenience factory for the most common topology: one super-federation containing
    /// multiple VDE 2.0B federations, each identified by a namespace prefix.
    /// </summary>
    /// <param name="superFedId">Unique identifier for the super-federation.</param>
    /// <param name="federations">
    /// List of (namespace prefix, federation ID, router) tuples defining the children.
    /// </param>
    /// <returns>A configured super-federation router.</returns>
    public static SuperFederationRouter CreateTwoLevel(
        Guid superFedId,
        IReadOnlyList<(string prefix, Guid fedId, VdeFederationRouter router)> federations)
    {
        ArgumentNullException.ThrowIfNull(federations);

        var router = new SuperFederationRouter(superFedId);

        for (int i = 0; i < federations.Count; i++)
        {
            (string prefix, Guid fedId, VdeFederationRouter fedRouter) = federations[i];
            var node = new FederationNode(fedId, fedRouter, depth: 1);
            router.AddNode(prefix, node);
        }

        return router;
    }

    /// <summary>
    /// Internal recursive resolution with hop counting.
    /// </summary>
    private async Task<FederationResolveResult> ResolveInternalAsync(
        string namespacePath, int currentHops, CancellationToken ct)
    {
        if (currentHops >= _maxHops)
        {
            throw new InvalidOperationException(
                $"Federation resolution exceeded maximum hop count ({_maxHops}). " +
                "This may indicate circular federation references.");
        }

        ct.ThrowIfCancellationRequested();

        // Longest-prefix match: iterate routing table in reverse (longest keys first)
        // SortedList sorts by key ascending; we iterate backwards for longest-prefix-first
        IFederationNode? matched = null;
        for (int i = _routingTable.Count - 1; i >= 0; i--)
        {
            string prefix = _routingTable.Keys[i];
            if (namespacePath.StartsWith(prefix, StringComparison.Ordinal))
            {
                matched = _routingTable.Values[i];
                break;
            }
        }

        if (matched is null)
        {
            throw new InvalidOperationException(
                $"No federation node handles namespace path '{namespacePath}'.");
        }

        FederationResolveResult childResult = await matched.ResolveAsync(namespacePath, ct).ConfigureAwait(false);

        int totalHops = currentHops + 1 + childResult.HopsUsed;

        if (totalHops > _maxHops)
        {
            throw new InvalidOperationException(
                $"Federation resolution exceeded maximum hop count ({_maxHops}). " +
                "This may indicate circular federation references.");
        }

        return new FederationResolveResult(
            LeafShardVdeId: childResult.LeafShardVdeId,
            InodeNumber: childResult.InodeNumber,
            HopsUsed: currentHops + 1 + childResult.HopsUsed,
            WasCacheHit: childResult.WasCacheHit);
    }
}
