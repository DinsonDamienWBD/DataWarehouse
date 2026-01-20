namespace DataWarehouse.SDK.Federation;

using System.Collections.Concurrent;
using System.Security.Cryptography;

/// <summary>
/// Topology/layout of a storage pool.
/// </summary>
public enum PoolTopology
{
    /// <summary>Objects can reside on any member node (combined namespace).</summary>
    Union = 0,
    /// <summary>Objects striped across members for performance.</summary>
    Striped = 1,
    /// <summary>Objects replicated to all members for redundancy.</summary>
    Mirrored = 2,
    /// <summary>Objects tiered across members by access pattern.</summary>
    Tiered = 3,
    /// <summary>Objects placed via consistent hashing.</summary>
    Distributed = 4
}

/// <summary>
/// Role of a node within a storage pool.
/// </summary>
public enum PoolMemberRole
{
    /// <summary>Regular storage member.</summary>
    Member = 0,
    /// <summary>Primary/coordinator for the pool.</summary>
    Primary = 1,
    /// <summary>Read-only replica.</summary>
    Replica = 2,
    /// <summary>Hot tier (fast storage).</summary>
    HotTier = 3,
    /// <summary>Warm tier (balanced).</summary>
    WarmTier = 4,
    /// <summary>Cold tier (archive).</summary>
    ColdTier = 5,
    /// <summary>Gateway/proxy (no local storage).</summary>
    Gateway = 6
}

/// <summary>
/// State of a pool member.
/// </summary>
public enum PoolMemberState
{
    /// <summary>Active and healthy.</summary>
    Active = 0,
    /// <summary>Joining the pool.</summary>
    Joining = 1,
    /// <summary>Leaving the pool gracefully.</summary>
    Leaving = 2,
    /// <summary>Temporarily unavailable.</summary>
    Unavailable = 3,
    /// <summary>Dormant (offline, e.g., USB unplugged).</summary>
    Dormant = 4,
    /// <summary>Failed/unreachable.</summary>
    Failed = 5
}

/// <summary>
/// A member node within a storage pool.
/// </summary>
public sealed class StoragePoolMember
{
    /// <summary>Node identifier.</summary>
    public NodeId NodeId { get; init; }

    /// <summary>Role in the pool.</summary>
    public PoolMemberRole Role { get; set; } = PoolMemberRole.Member;

    /// <summary>Current state.</summary>
    public PoolMemberState State { get; set; } = PoolMemberState.Active;

    /// <summary>Weight for placement (higher = more objects).</summary>
    public int Weight { get; set; } = 100;

    /// <summary>Storage capacity in bytes.</summary>
    public long CapacityBytes { get; set; }

    /// <summary>Used storage in bytes.</summary>
    public long UsedBytes { get; set; }

    /// <summary>When this member joined the pool.</summary>
    public DateTimeOffset JoinedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Last time this member was seen active.</summary>
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Endpoint for direct communication.</summary>
    public NodeEndpoint? Endpoint { get; set; }

    /// <summary>Custom metadata.</summary>
    public Dictionary<string, string> Metadata { get; set; } = new();

    /// <summary>Available capacity.</summary>
    public long AvailableBytes => CapacityBytes - UsedBytes;

    /// <summary>Usage percentage (0-100).</summary>
    public float UsagePercent => CapacityBytes > 0 ? (float)UsedBytes / CapacityBytes * 100 : 0;
}

/// <summary>
/// Policy for placing objects in a pool.
/// </summary>
public sealed class PoolPlacementPolicy
{
    /// <summary>Minimum replicas required.</summary>
    public int MinReplicas { get; set; } = 1;

    /// <summary>Maximum replicas allowed.</summary>
    public int MaxReplicas { get; set; } = 3;

    /// <summary>Prefer members in these regions.</summary>
    public HashSet<string> PreferredRegions { get; set; } = new();

    /// <summary>Avoid members in these regions.</summary>
    public HashSet<string> ExcludedRegions { get; set; } = new();

    /// <summary>Required member roles.</summary>
    public HashSet<PoolMemberRole> RequiredRoles { get; set; } = new();

    /// <summary>Maximum usage percent for new placements.</summary>
    public float MaxUsagePercent { get; set; } = 90f;

    /// <summary>Whether to prefer local node.</summary>
    public bool PreferLocal { get; set; } = true;

    /// <summary>
    /// Selects members for object placement.
    /// </summary>
    public IReadOnlyList<StoragePoolMember> SelectMembers(
        IEnumerable<StoragePoolMember> candidates,
        ObjectId objectId,
        NodeId? localNodeId = null)
    {
        var eligible = candidates
            .Where(m => m.State == PoolMemberState.Active)
            .Where(m => m.UsagePercent < MaxUsagePercent)
            .Where(m => RequiredRoles.Count == 0 || RequiredRoles.Contains(m.Role))
            .ToList();

        if (eligible.Count == 0)
            return Array.Empty<StoragePoolMember>();

        // Sort by preference
        var sorted = eligible
            .OrderByDescending(m => localNodeId.HasValue && m.NodeId == localNodeId.Value && PreferLocal ? 1 : 0)
            .ThenByDescending(m => m.Role == PoolMemberRole.Primary ? 1 : 0)
            .ThenBy(m => m.UsagePercent)
            .ThenByDescending(m => m.Weight)
            .ToList();

        // Use consistent hashing for deterministic selection
        var hash = objectId.GetHashCode();
        var selected = new List<StoragePoolMember>();

        for (int i = 0; i < sorted.Count && selected.Count < MaxReplicas; i++)
        {
            var idx = (hash + i) % sorted.Count;
            if (idx < 0) idx += sorted.Count;
            var member = sorted[idx];
            if (!selected.Contains(member))
                selected.Add(member);
        }

        return selected.Take(Math.Max(MinReplicas, selected.Count)).ToList();
    }
}

/// <summary>
/// Interface for storage pools.
/// </summary>
public interface IStoragePool
{
    /// <summary>Unique pool identifier.</summary>
    string PoolId { get; }

    /// <summary>Human-readable pool name.</summary>
    string Name { get; }

    /// <summary>Pool topology.</summary>
    PoolTopology Topology { get; }

    /// <summary>Pool members.</summary>
    IReadOnlyList<StoragePoolMember> Members { get; }

    /// <summary>Placement policy.</summary>
    PoolPlacementPolicy PlacementPolicy { get; }

    /// <summary>Adds a member to the pool.</summary>
    Task<bool> AddMemberAsync(StoragePoolMember member, CancellationToken ct = default);

    /// <summary>Removes a member from the pool.</summary>
    Task<bool> RemoveMemberAsync(NodeId nodeId, CancellationToken ct = default);

    /// <summary>Updates a member's state.</summary>
    Task<bool> UpdateMemberStateAsync(NodeId nodeId, PoolMemberState state, CancellationToken ct = default);

    /// <summary>Gets members that have a specific object.</summary>
    Task<IReadOnlyList<StoragePoolMember>> GetObjectLocationsAsync(ObjectId objectId, CancellationToken ct = default);

    /// <summary>Selects members for storing a new object.</summary>
    Task<IReadOnlyList<StoragePoolMember>> SelectMembersForStoreAsync(ObjectId objectId, CancellationToken ct = default);

    /// <summary>Gets aggregate pool statistics.</summary>
    PoolStatistics GetStatistics();
}

/// <summary>
/// Aggregate statistics for a pool.
/// </summary>
public sealed class PoolStatistics
{
    public string PoolId { get; init; } = string.Empty;
    public int TotalMembers { get; init; }
    public int ActiveMembers { get; init; }
    public int DormantMembers { get; init; }
    public long TotalCapacityBytes { get; init; }
    public long UsedBytes { get; init; }
    public long AvailableBytes => TotalCapacityBytes - UsedBytes;
    public float UsagePercent => TotalCapacityBytes > 0 ? (float)UsedBytes / TotalCapacityBytes * 100 : 0;
}

/// <summary>
/// Base storage pool implementation.
/// </summary>
public class StoragePool : IStoragePool
{
    private readonly ConcurrentDictionary<NodeId, StoragePoolMember> _members = new();
    private readonly ConcurrentDictionary<ObjectId, HashSet<NodeId>> _objectLocations = new();
    private readonly ReaderWriterLockSlim _lock = new();

    /// <inheritdoc />
    public string PoolId { get; }

    /// <inheritdoc />
    public string Name { get; set; }

    /// <inheritdoc />
    public PoolTopology Topology { get; }

    /// <inheritdoc />
    public IReadOnlyList<StoragePoolMember> Members => _members.Values.ToList();

    /// <inheritdoc />
    public PoolPlacementPolicy PlacementPolicy { get; set; } = new();

    /// <summary>Owner node that created this pool.</summary>
    public NodeId OwnerNodeId { get; init; }

    /// <summary>When this pool was created.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Custom metadata.</summary>
    public Dictionary<string, string> Metadata { get; set; } = new();

    public StoragePool(string poolId, string name, PoolTopology topology)
    {
        PoolId = poolId;
        Name = name;
        Topology = topology;
    }

    /// <inheritdoc />
    public virtual Task<bool> AddMemberAsync(StoragePoolMember member, CancellationToken ct = default)
    {
        member.State = PoolMemberState.Joining;
        var added = _members.TryAdd(member.NodeId, member);

        if (added)
        {
            member.State = PoolMemberState.Active;
            member.JoinedAt = DateTimeOffset.UtcNow;
        }

        return Task.FromResult(added);
    }

    /// <inheritdoc />
    public virtual Task<bool> RemoveMemberAsync(NodeId nodeId, CancellationToken ct = default)
    {
        if (_members.TryGetValue(nodeId, out var member))
        {
            member.State = PoolMemberState.Leaving;
        }

        var removed = _members.TryRemove(nodeId, out _);

        // Remove from object locations
        foreach (var locations in _objectLocations.Values)
        {
            locations.Remove(nodeId);
        }

        return Task.FromResult(removed);
    }

    /// <inheritdoc />
    public virtual Task<bool> UpdateMemberStateAsync(NodeId nodeId, PoolMemberState state, CancellationToken ct = default)
    {
        if (_members.TryGetValue(nodeId, out var member))
        {
            member.State = state;
            member.LastSeenAt = DateTimeOffset.UtcNow;
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    /// <inheritdoc />
    public virtual Task<IReadOnlyList<StoragePoolMember>> GetObjectLocationsAsync(ObjectId objectId, CancellationToken ct = default)
    {
        if (_objectLocations.TryGetValue(objectId, out var nodeIds))
        {
            var members = nodeIds
                .Select(id => _members.GetValueOrDefault(id))
                .Where(m => m != null)
                .Cast<StoragePoolMember>()
                .ToList();
            return Task.FromResult<IReadOnlyList<StoragePoolMember>>(members);
        }
        return Task.FromResult<IReadOnlyList<StoragePoolMember>>(Array.Empty<StoragePoolMember>());
    }

    /// <inheritdoc />
    public virtual Task<IReadOnlyList<StoragePoolMember>> SelectMembersForStoreAsync(ObjectId objectId, CancellationToken ct = default)
    {
        var selected = PlacementPolicy.SelectMembers(_members.Values, objectId);
        return Task.FromResult(selected);
    }

    /// <summary>
    /// Registers an object location.
    /// </summary>
    public void RegisterObjectLocation(ObjectId objectId, NodeId nodeId)
    {
        _objectLocations.AddOrUpdate(
            objectId,
            _ => new HashSet<NodeId> { nodeId },
            (_, set) => { set.Add(nodeId); return set; });
    }

    /// <summary>
    /// Unregisters an object location.
    /// </summary>
    public void UnregisterObjectLocation(ObjectId objectId, NodeId nodeId)
    {
        if (_objectLocations.TryGetValue(objectId, out var set))
        {
            set.Remove(nodeId);
            if (set.Count == 0)
                _objectLocations.TryRemove(objectId, out _);
        }
    }

    /// <inheritdoc />
    public PoolStatistics GetStatistics()
    {
        var members = _members.Values.ToList();
        return new PoolStatistics
        {
            PoolId = PoolId,
            TotalMembers = members.Count,
            ActiveMembers = members.Count(m => m.State == PoolMemberState.Active),
            DormantMembers = members.Count(m => m.State == PoolMemberState.Dormant),
            TotalCapacityBytes = members.Sum(m => m.CapacityBytes),
            UsedBytes = members.Sum(m => m.UsedBytes)
        };
    }
}

/// <summary>
/// Union pool - combines multiple nodes into a single logical namespace.
/// Objects can exist on any member; queries search all members.
/// Implements scenarios 3/4: DWH+DW1 combined pool.
/// </summary>
public sealed class UnionPool : StoragePool
{
    private readonly IObjectResolver? _resolver;
    private readonly ITransportBus? _transportBus;

    public UnionPool(
        string poolId,
        string name,
        IObjectResolver? resolver = null,
        ITransportBus? transportBus = null)
        : base(poolId, name, PoolTopology.Union)
    {
        _resolver = resolver;
        _transportBus = transportBus;
    }

    /// <summary>
    /// Resolves an object across all pool members.
    /// </summary>
    public async Task<UnionResolveResult> ResolveAsync(ObjectId objectId, CancellationToken ct = default)
    {
        // First check local tracking
        var knownLocations = await GetObjectLocationsAsync(objectId, ct);
        if (knownLocations.Count > 0)
        {
            return new UnionResolveResult
            {
                Found = true,
                ObjectId = objectId,
                Locations = knownLocations.ToList()
            };
        }

        // Query all active members
        if (_resolver != null)
        {
            var resolution = await _resolver.ResolveAsync(objectId, ct);
            if (resolution.Found)
            {
                // Filter to pool members only
                var poolMemberIds = Members.Select(m => m.NodeId).ToHashSet();
                var poolLocations = resolution.Locations
                    .Where(loc => poolMemberIds.Contains(loc.NodeId))
                    .ToList();

                if (poolLocations.Count > 0)
                {
                    // Update local tracking
                    foreach (var loc in poolLocations)
                    {
                        RegisterObjectLocation(objectId, loc.NodeId);
                    }

                    var members = poolLocations
                        .Select(loc => Members.FirstOrDefault(m => m.NodeId == loc.NodeId))
                        .Where(m => m != null)
                        .Cast<StoragePoolMember>()
                        .ToList();

                    return new UnionResolveResult
                    {
                        Found = true,
                        ObjectId = objectId,
                        Locations = members
                    };
                }
            }
        }

        return new UnionResolveResult { Found = false, ObjectId = objectId };
    }

    /// <summary>
    /// Lists all objects visible in the union pool.
    /// </summary>
    public async IAsyncEnumerable<ObjectId> EnumerateObjectsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var seen = new HashSet<ObjectId>();

        // Return locally tracked objects
        foreach (var objectId in _objectLocations.Keys)
        {
            if (seen.Add(objectId))
                yield return objectId;
        }

        // Query each member for their objects (would need member-specific enumeration)
        await Task.CompletedTask;
    }

    /// <summary>
    /// Gets the best member to fetch an object from.
    /// </summary>
    public StoragePoolMember? GetBestSourceMember(ObjectId objectId)
    {
        if (!_objectLocations.TryGetValue(objectId, out var nodeIds))
            return null;

        // Prefer active members, then by latency/usage
        return Members
            .Where(m => nodeIds.Contains(m.NodeId))
            .OrderByDescending(m => m.State == PoolMemberState.Active ? 1 : 0)
            .ThenBy(m => m.UsagePercent)
            .FirstOrDefault();
    }

    // Expose protected dictionary for enumeration
    private ConcurrentDictionary<ObjectId, HashSet<NodeId>> _objectLocations =>
        (ConcurrentDictionary<ObjectId, HashSet<NodeId>>)typeof(StoragePool)
            .GetField("_objectLocations", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(this)!;
}

/// <summary>
/// Result of union pool resolution.
/// </summary>
public sealed class UnionResolveResult
{
    public bool Found { get; init; }
    public ObjectId ObjectId { get; init; }
    public List<StoragePoolMember> Locations { get; init; } = new();
}

/// <summary>
/// Registry of storage pools.
/// </summary>
public sealed class StoragePoolRegistry
{
    private readonly ConcurrentDictionary<string, IStoragePool> _pools = new();

    /// <summary>
    /// Registers a pool.
    /// </summary>
    public void Register(IStoragePool pool)
    {
        _pools[pool.PoolId] = pool;
    }

    /// <summary>
    /// Gets a pool by ID.
    /// </summary>
    public IStoragePool? GetPool(string poolId)
    {
        _pools.TryGetValue(poolId, out var pool);
        return pool;
    }

    /// <summary>
    /// Removes a pool.
    /// </summary>
    public bool Remove(string poolId)
    {
        return _pools.TryRemove(poolId, out _);
    }

    /// <summary>
    /// Gets all pools.
    /// </summary>
    public IReadOnlyList<IStoragePool> GetAllPools()
    {
        return _pools.Values.ToList();
    }

    /// <summary>
    /// Finds pools containing a specific node.
    /// </summary>
    public IReadOnlyList<IStoragePool> GetPoolsForNode(NodeId nodeId)
    {
        return _pools.Values
            .Where(p => p.Members.Any(m => m.NodeId == nodeId))
            .ToList();
    }

    /// <summary>
    /// Creates a temporary union pool.
    /// </summary>
    public UnionPool CreateUnionPool(string name, IEnumerable<NodeId> memberNodeIds, NodeRegistry nodeRegistry)
    {
        var poolId = Guid.NewGuid().ToString("N");
        var pool = new UnionPool(poolId, name);

        foreach (var nodeId in memberNodeIds)
        {
            var node = nodeRegistry.GetNode(nodeId);
            if (node != null)
            {
                pool.AddMemberAsync(new StoragePoolMember
                {
                    NodeId = nodeId,
                    Role = PoolMemberRole.Member,
                    CapacityBytes = node.StorageCapacityBytes,
                    UsedBytes = node.StorageCapacityBytes - node.StorageAvailableBytes,
                    Endpoint = node.GetPreferredEndpoint()
                }).Wait();
            }
        }

        Register(pool);
        return pool;
    }
}
