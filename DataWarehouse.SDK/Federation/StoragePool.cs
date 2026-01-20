namespace DataWarehouse.SDK.Federation;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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

// ============================================================================
// SCENARIO 3: UNIFIED POOL (DWH + DW1)
// Pool discovery, auto-join, federated routing, deduplication, and monitoring.
// ============================================================================

#region Pool Discovery Protocol

/// <summary>
/// Discovery message for pool advertisement.
/// </summary>
public sealed class PoolDiscoveryMessage
{
    public string MessageType { get; set; } = "PoolDiscovery";
    public string PoolId { get; set; } = string.Empty;
    public string PoolName { get; set; } = string.Empty;
    public string NodeId { get; set; } = string.Empty;
    public PoolTopology Topology { get; set; }
    public int MemberCount { get; set; }
    public long TotalCapacityBytes { get; set; }
    public long AvailableBytes { get; set; }
    public string EndpointAddress { get; set; } = string.Empty;
    public int EndpointPort { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public int Ttl { get; set; } = 30; // seconds
}

/// <summary>
/// Implements pool discovery using mDNS-style multicast and gossip protocols.
/// </summary>
public sealed class PoolDiscoveryProtocol : IDisposable
{
    private readonly NodeIdentityManager _identityManager;
    private readonly StoragePoolRegistry _poolRegistry;
    private readonly ConcurrentDictionary<string, DiscoveredPool> _discoveredPools = new();
    private UdpClient? _multicastClient;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private Task? _announceTask;
    private bool _disposed;

    private const int MulticastPort = 5357; // DataWarehouse discovery port
    private static readonly IPAddress MulticastAddress = IPAddress.Parse("239.255.77.88");
    private const string ServiceName = "_datawarehouse._udp.local";

    public event EventHandler<PoolDiscoveredEventArgs>? PoolDiscovered;
    public event EventHandler<PoolDiscoveredEventArgs>? PoolLost;

    public PoolDiscoveryProtocol(
        NodeIdentityManager identityManager,
        StoragePoolRegistry poolRegistry)
    {
        _identityManager = identityManager;
        _poolRegistry = poolRegistry;
    }

    /// <summary>
    /// Starts discovery - listens for and announces pools.
    /// </summary>
    public void Start()
    {
        _cts = new CancellationTokenSource();

        try
        {
            _multicastClient = new UdpClient();
            _multicastClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _multicastClient.Client.Bind(new IPEndPoint(IPAddress.Any, MulticastPort));
            _multicastClient.JoinMulticastGroup(MulticastAddress);

            _listenTask = ListenLoopAsync(_cts.Token);
            _announceTask = AnnounceLoopAsync(_cts.Token);
        }
        catch
        {
            // Multicast may not be available - fall back to gossip only
        }
    }

    /// <summary>
    /// Actively probes for pools on the network.
    /// </summary>
    public async Task ProbeAsync(CancellationToken ct = default)
    {
        var probe = new PoolDiscoveryMessage
        {
            MessageType = "PoolProbe",
            NodeId = _identityManager.LocalIdentity.Id.ToHex()
        };

        await BroadcastMessageAsync(probe, ct);
    }

    /// <summary>
    /// Gets all discovered pools.
    /// </summary>
    public IReadOnlyList<DiscoveredPool> GetDiscoveredPools()
    {
        // Prune expired
        var now = DateTimeOffset.UtcNow;
        var expired = _discoveredPools.Where(kv => kv.Value.ExpiresAt < now).Select(kv => kv.Key).ToList();
        foreach (var id in expired)
        {
            if (_discoveredPools.TryRemove(id, out var pool))
            {
                PoolLost?.Invoke(this, new PoolDiscoveredEventArgs { Pool = pool });
            }
        }

        return _discoveredPools.Values.ToList();
    }

    /// <summary>
    /// Announces a pool to the network.
    /// </summary>
    public async Task AnnouncePoolAsync(IStoragePool pool, IPEndPoint endpoint, CancellationToken ct = default)
    {
        var stats = pool.GetStatistics();
        var message = new PoolDiscoveryMessage
        {
            PoolId = pool.PoolId,
            PoolName = pool.Name,
            NodeId = _identityManager.LocalIdentity.Id.ToHex(),
            Topology = pool.Topology,
            MemberCount = stats.TotalMembers,
            TotalCapacityBytes = stats.TotalCapacityBytes,
            AvailableBytes = stats.AvailableBytes,
            EndpointAddress = endpoint.Address.ToString(),
            EndpointPort = endpoint.Port
        };

        await BroadcastMessageAsync(message, ct);
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _multicastClient != null)
        {
            try
            {
                var result = await _multicastClient.ReceiveAsync(ct);
                var json = Encoding.UTF8.GetString(result.Buffer);
                var message = JsonSerializer.Deserialize<PoolDiscoveryMessage>(json);

                if (message != null)
                {
                    await HandleDiscoveryMessageAsync(message, result.RemoteEndPoint, ct);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Continue listening
            }
        }
    }

    private async Task AnnounceLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                foreach (var pool in _poolRegistry.GetAllPools())
                {
                    var localMember = pool.Members.FirstOrDefault(m =>
                        m.NodeId == _identityManager.LocalIdentity.Id);

                    if (localMember?.Endpoint != null)
                    {
                        var endpoint = new IPEndPoint(
                            IPAddress.Parse(localMember.Endpoint.Address ?? "127.0.0.1"),
                            localMember.Endpoint.Port);
                        await AnnouncePoolAsync(pool, endpoint, ct);
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(10), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    private async Task HandleDiscoveryMessageAsync(
        PoolDiscoveryMessage message,
        IPEndPoint sender,
        CancellationToken ct)
    {
        if (message.MessageType == "PoolProbe")
        {
            // Respond with our pools
            foreach (var pool in _poolRegistry.GetAllPools())
            {
                var localMember = pool.Members.FirstOrDefault(m =>
                    m.NodeId == _identityManager.LocalIdentity.Id);
                if (localMember?.Endpoint != null)
                {
                    var endpoint = new IPEndPoint(
                        IPAddress.Parse(localMember.Endpoint.Address ?? sender.Address.ToString()),
                        localMember.Endpoint.Port);
                    await AnnouncePoolAsync(pool, endpoint, ct);
                }
            }
        }
        else if (message.MessageType == "PoolDiscovery")
        {
            // Skip our own announcements
            if (message.NodeId == _identityManager.LocalIdentity.Id.ToHex())
                return;

            var discovered = new DiscoveredPool
            {
                PoolId = message.PoolId,
                PoolName = message.PoolName,
                Topology = message.Topology,
                MemberCount = message.MemberCount,
                TotalCapacityBytes = message.TotalCapacityBytes,
                AvailableBytes = message.AvailableBytes,
                DiscoveredAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(message.Ttl * 2),
                AnnouncerNodeId = message.NodeId,
                AnnouncerEndpoint = new IPEndPoint(
                    IPAddress.Parse(message.EndpointAddress),
                    message.EndpointPort)
            };

            var isNew = !_discoveredPools.ContainsKey(message.PoolId);
            _discoveredPools[message.PoolId] = discovered;

            if (isNew)
            {
                PoolDiscovered?.Invoke(this, new PoolDiscoveredEventArgs { Pool = discovered });
            }
        }
    }

    private async Task BroadcastMessageAsync(PoolDiscoveryMessage message, CancellationToken ct)
    {
        if (_multicastClient == null) return;

        var json = JsonSerializer.Serialize(message);
        var data = Encoding.UTF8.GetBytes(json);
        var endpoint = new IPEndPoint(MulticastAddress, MulticastPort);

        await _multicastClient.SendAsync(data, data.Length, endpoint);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _cts?.Cancel();
            _multicastClient?.Dispose();
            _disposed = true;
        }
    }
}

/// <summary>
/// Discovered pool information.
/// </summary>
public sealed class DiscoveredPool
{
    public string PoolId { get; set; } = string.Empty;
    public string PoolName { get; set; } = string.Empty;
    public PoolTopology Topology { get; set; }
    public int MemberCount { get; set; }
    public long TotalCapacityBytes { get; set; }
    public long AvailableBytes { get; set; }
    public DateTimeOffset DiscoveredAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public string AnnouncerNodeId { get; set; } = string.Empty;
    public IPEndPoint? AnnouncerEndpoint { get; set; }
}

public sealed class PoolDiscoveredEventArgs : EventArgs
{
    public DiscoveredPool? Pool { get; set; }
}

#endregion

#region Pool Auto-Join Manager

/// <summary>
/// Manages automatic joining of discovered pools based on policies.
/// </summary>
public sealed class PoolAutoJoinManager
{
    private readonly NodeIdentityManager _identityManager;
    private readonly StoragePoolRegistry _poolRegistry;
    private readonly PoolDiscoveryProtocol _discovery;
    private readonly PoolAutoJoinPolicy _policy;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _joinAttempts = new();

    public event EventHandler<PoolJoinedEventArgs>? PoolJoined;
    public event EventHandler<PoolJoinedEventArgs>? JoinFailed;

    public PoolAutoJoinManager(
        NodeIdentityManager identityManager,
        StoragePoolRegistry poolRegistry,
        PoolDiscoveryProtocol discovery,
        PoolAutoJoinPolicy? policy = null)
    {
        _identityManager = identityManager;
        _poolRegistry = poolRegistry;
        _discovery = discovery;
        _policy = policy ?? new PoolAutoJoinPolicy();

        _discovery.PoolDiscovered += OnPoolDiscovered;
    }

    private async void OnPoolDiscovered(object? sender, PoolDiscoveredEventArgs e)
    {
        if (e.Pool == null || !_policy.AutoJoinEnabled) return;

        // Check if we should auto-join
        if (!ShouldAutoJoin(e.Pool)) return;

        await TryJoinPoolAsync(e.Pool);
    }

    private bool ShouldAutoJoin(DiscoveredPool pool)
    {
        // Check if already in this pool
        var existingPool = _poolRegistry.GetPool(pool.PoolId);
        if (existingPool != null)
        {
            return existingPool.Members.All(m => m.NodeId != _identityManager.LocalIdentity.Id);
        }

        // Check topology whitelist
        if (_policy.AllowedTopologies.Count > 0 &&
            !_policy.AllowedTopologies.Contains(pool.Topology))
        {
            return false;
        }

        // Check pool name pattern
        if (!string.IsNullOrEmpty(_policy.PoolNamePattern) &&
            !pool.PoolName.Contains(_policy.PoolNamePattern, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Check maximum pools
        var myPools = _poolRegistry.GetPoolsForNode(_identityManager.LocalIdentity.Id);
        if (myPools.Count >= _policy.MaxPoolMemberships)
        {
            return false;
        }

        // Rate limit join attempts
        if (_joinAttempts.TryGetValue(pool.PoolId, out var lastAttempt) &&
            DateTimeOffset.UtcNow - lastAttempt < _policy.JoinRetryInterval)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Attempts to join a discovered pool.
    /// </summary>
    public async Task<bool> TryJoinPoolAsync(DiscoveredPool discoveredPool, CancellationToken ct = default)
    {
        _joinAttempts[discoveredPool.PoolId] = DateTimeOffset.UtcNow;

        try
        {
            // Create or get pool
            var pool = _poolRegistry.GetPool(discoveredPool.PoolId);
            if (pool == null)
            {
                // Create local representation of remote pool
                pool = new UnionPool(discoveredPool.PoolId, discoveredPool.PoolName);
                _poolRegistry.Register(pool);
            }

            // Create member entry for ourselves
            var localIdentity = _identityManager.LocalIdentity;
            var member = new StoragePoolMember
            {
                NodeId = localIdentity.Id,
                Role = PoolMemberRole.Member,
                State = PoolMemberState.Joining,
                CapacityBytes = localIdentity.StorageCapacityBytes,
                UsedBytes = localIdentity.StorageCapacityBytes - localIdentity.StorageAvailableBytes,
                Endpoint = localIdentity.GetPreferredEndpoint()
            };

            var added = await pool.AddMemberAsync(member, ct);

            if (added)
            {
                PoolJoined?.Invoke(this, new PoolJoinedEventArgs
                {
                    PoolId = pool.PoolId,
                    PoolName = pool.Name,
                    Success = true
                });
                return true;
            }
        }
        catch (Exception ex)
        {
            JoinFailed?.Invoke(this, new PoolJoinedEventArgs
            {
                PoolId = discoveredPool.PoolId,
                PoolName = discoveredPool.PoolName,
                Success = false,
                Error = ex.Message
            });
        }

        return false;
    }

    /// <summary>
    /// Manually requests to join a specific pool.
    /// </summary>
    public async Task<bool> RequestJoinAsync(string poolId, IPEndPoint coordinatorEndpoint, CancellationToken ct = default)
    {
        // In a full implementation, this would send a join request to the coordinator
        // For now, create local pool entry
        var pool = _poolRegistry.GetPool(poolId);
        if (pool == null)
        {
            pool = new UnionPool(poolId, $"Pool-{poolId[..8]}");
            _poolRegistry.Register(pool);
        }

        var localIdentity = _identityManager.LocalIdentity;
        var member = new StoragePoolMember
        {
            NodeId = localIdentity.Id,
            Role = PoolMemberRole.Member,
            CapacityBytes = localIdentity.StorageCapacityBytes,
            Endpoint = localIdentity.GetPreferredEndpoint()
        };

        return await pool.AddMemberAsync(member, ct);
    }
}

/// <summary>
/// Policy for automatic pool joining.
/// </summary>
public sealed class PoolAutoJoinPolicy
{
    public bool AutoJoinEnabled { get; set; } = false;
    public HashSet<PoolTopology> AllowedTopologies { get; set; } = new() { PoolTopology.Union };
    public string? PoolNamePattern { get; set; }
    public int MaxPoolMemberships { get; set; } = 5;
    public TimeSpan JoinRetryInterval { get; set; } = TimeSpan.FromMinutes(5);
    public bool RequireAuthentication { get; set; } = true;
}

public sealed class PoolJoinedEventArgs : EventArgs
{
    public string PoolId { get; set; } = string.Empty;
    public string PoolName { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? Error { get; set; }
}

#endregion

#region Federated Pool Router

/// <summary>
/// Routes object requests transparently across federated pools.
/// </summary>
public sealed class FederatedPoolRouter
{
    private readonly StoragePoolRegistry _poolRegistry;
    private readonly IObjectResolver _objectResolver;
    private readonly ITransportBus _transportBus;
    private readonly ConcurrentDictionary<ObjectId, RoutingCacheEntry> _routingCache = new();
    private readonly TimeSpan _cacheTtl;

    public FederatedPoolRouter(
        StoragePoolRegistry poolRegistry,
        IObjectResolver objectResolver,
        ITransportBus transportBus,
        TimeSpan? cacheTtl = null)
    {
        _poolRegistry = poolRegistry;
        _objectResolver = objectResolver;
        _transportBus = transportBus;
        _cacheTtl = cacheTtl ?? TimeSpan.FromMinutes(5);
    }

    /// <summary>
    /// Routes a store request to the optimal pool member.
    /// </summary>
    public async Task<RoutingDecision> RouteStoreAsync(
        ObjectId objectId,
        long sizeBytes,
        CancellationToken ct = default)
    {
        var allPools = _poolRegistry.GetAllPools();
        var candidates = new List<(IStoragePool Pool, IReadOnlyList<StoragePoolMember> Members, float Score)>();

        foreach (var pool in allPools)
        {
            var members = await pool.SelectMembersForStoreAsync(objectId, ct);
            if (members.Count > 0)
            {
                var score = CalculatePoolScore(pool, sizeBytes);
                candidates.Add((pool, members, score));
            }
        }

        if (candidates.Count == 0)
        {
            return new RoutingDecision { Success = false, Reason = "No pool can accept the object" };
        }

        // Select best pool
        var best = candidates.OrderByDescending(c => c.Score).First();

        return new RoutingDecision
        {
            Success = true,
            Pool = best.Pool,
            TargetMembers = best.Members.ToList(),
            Reason = $"Selected pool '{best.Pool.Name}' with score {best.Score:F2}"
        };
    }

    /// <summary>
    /// Routes a retrieve request to find the object across pools.
    /// </summary>
    public async Task<RoutingDecision> RouteRetrieveAsync(
        ObjectId objectId,
        CancellationToken ct = default)
    {
        // Check cache first
        if (_routingCache.TryGetValue(objectId, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
        {
            return cached.Decision;
        }

        // Try object resolver
        var resolution = await _objectResolver.ResolveAsync(objectId, ct);
        if (resolution.Found)
        {
            var pool = FindPoolForLocations(resolution.Locations);
            if (pool != null)
            {
                var decision = new RoutingDecision
                {
                    Success = true,
                    Pool = pool,
                    TargetMembers = resolution.Locations
                        .Select(loc => pool.Members.FirstOrDefault(m => m.NodeId == loc.NodeId))
                        .Where(m => m != null)
                        .Cast<StoragePoolMember>()
                        .ToList()
                };

                CacheDecision(objectId, decision);
                return decision;
            }
        }

        // Search all pools
        foreach (var pool in _poolRegistry.GetAllPools())
        {
            var locations = await pool.GetObjectLocationsAsync(objectId, ct);
            if (locations.Count > 0)
            {
                var decision = new RoutingDecision
                {
                    Success = true,
                    Pool = pool,
                    TargetMembers = locations.ToList()
                };

                CacheDecision(objectId, decision);
                return decision;
            }
        }

        return new RoutingDecision { Success = false, Reason = "Object not found in any pool" };
    }

    private float CalculatePoolScore(IStoragePool pool, long sizeBytes)
    {
        var stats = pool.GetStatistics();

        // Score based on: availability, capacity, member count
        var availabilityScore = stats.AvailableBytes > sizeBytes * 10 ? 1.0f : 0.5f;
        var capacityScore = Math.Max(0, 1.0f - stats.UsagePercent / 100);
        var redundancyScore = Math.Min(1.0f, stats.ActiveMembers / 3.0f);

        return (availabilityScore + capacityScore + redundancyScore) / 3;
    }

    private IStoragePool? FindPoolForLocations(IEnumerable<ObjectLocation> locations)
    {
        var nodeIds = locations.Select(l => l.NodeId).ToHashSet();

        foreach (var pool in _poolRegistry.GetAllPools())
        {
            if (pool.Members.Any(m => nodeIds.Contains(m.NodeId)))
            {
                return pool;
            }
        }

        return null;
    }

    private void CacheDecision(ObjectId objectId, RoutingDecision decision)
    {
        _routingCache[objectId] = new RoutingCacheEntry
        {
            Decision = decision,
            ExpiresAt = DateTimeOffset.UtcNow + _cacheTtl
        };
    }

    /// <summary>
    /// Invalidates routing cache for an object.
    /// </summary>
    public void InvalidateCache(ObjectId objectId)
    {
        _routingCache.TryRemove(objectId, out _);
    }

    private sealed class RoutingCacheEntry
    {
        public RoutingDecision Decision { get; set; } = new();
        public DateTimeOffset ExpiresAt { get; set; }
    }
}

/// <summary>
/// Result of a routing decision.
/// </summary>
public sealed class RoutingDecision
{
    public bool Success { get; set; }
    public IStoragePool? Pool { get; set; }
    public List<StoragePoolMember> TargetMembers { get; set; } = new();
    public string? Reason { get; set; }
}

#endregion

#region Pool Deduplication Service

/// <summary>
/// Provides cross-pool content-addressable deduplication.
/// </summary>
public sealed class PoolDeduplicationService
{
    private readonly StoragePoolRegistry _poolRegistry;
    private readonly ConcurrentDictionary<string, DeduplicationEntry> _contentIndex = new();
    private long _bytesDeduped;
    private long _objectsDeduped;

    public long BytesDeduped => _bytesDeduped;
    public long ObjectsDeduped => _objectsDeduped;

    public PoolDeduplicationService(StoragePoolRegistry poolRegistry)
    {
        _poolRegistry = poolRegistry;
    }

    /// <summary>
    /// Checks if content already exists in any pool.
    /// </summary>
    public async Task<DeduplicationResult> CheckForDuplicateAsync(
        byte[] content,
        CancellationToken ct = default)
    {
        var hash = ComputeContentHash(content);

        // Check local index
        if (_contentIndex.TryGetValue(hash, out var entry))
        {
            return new DeduplicationResult
            {
                IsDuplicate = true,
                ContentHash = hash,
                ExistingObjectId = entry.ObjectId,
                ExistingPoolId = entry.PoolId,
                SizeBytes = content.Length
            };
        }

        // Check across pools
        foreach (var pool in _poolRegistry.GetAllPools())
        {
            if (pool is UnionPool unionPool)
            {
                // Would need object store access to check content
                // For now, rely on local index
            }
        }

        return new DeduplicationResult
        {
            IsDuplicate = false,
            ContentHash = hash,
            SizeBytes = content.Length
        };
    }

    /// <summary>
    /// Registers content in the deduplication index.
    /// </summary>
    public void RegisterContent(string contentHash, ObjectId objectId, string poolId)
    {
        _contentIndex[contentHash] = new DeduplicationEntry
        {
            ContentHash = contentHash,
            ObjectId = objectId,
            PoolId = poolId,
            RegisteredAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>
    /// Registers a deduplicated reference (saves storage).
    /// </summary>
    public void RecordDeduplication(long sizeBytes)
    {
        Interlocked.Add(ref _bytesDeduped, sizeBytes);
        Interlocked.Increment(ref _objectsDeduped);
    }

    /// <summary>
    /// Unregisters content when deleted.
    /// </summary>
    public void UnregisterContent(string contentHash)
    {
        _contentIndex.TryRemove(contentHash, out _);
    }

    /// <summary>
    /// Gets deduplication statistics.
    /// </summary>
    public DeduplicationStats GetStats()
    {
        return new DeduplicationStats
        {
            TotalIndexedObjects = _contentIndex.Count,
            BytesDeduped = _bytesDeduped,
            ObjectsDeduped = _objectsDeduped
        };
    }

    private static string ComputeContentHash(byte[] content)
    {
        return Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
    }
}

public sealed class DeduplicationEntry
{
    public string ContentHash { get; set; } = string.Empty;
    public ObjectId ObjectId { get; set; }
    public string PoolId { get; set; } = string.Empty;
    public DateTimeOffset RegisteredAt { get; set; }
}

public sealed class DeduplicationResult
{
    public bool IsDuplicate { get; set; }
    public string ContentHash { get; set; } = string.Empty;
    public ObjectId? ExistingObjectId { get; set; }
    public string? ExistingPoolId { get; set; }
    public long SizeBytes { get; set; }
}

public sealed class DeduplicationStats
{
    public int TotalIndexedObjects { get; set; }
    public long BytesDeduped { get; set; }
    public long ObjectsDeduped { get; set; }
}

#endregion

#region Pool Capacity Monitor

/// <summary>
/// Monitors pool capacity and generates alerts.
/// </summary>
public sealed class PoolCapacityMonitor : IDisposable
{
    private readonly StoragePoolRegistry _poolRegistry;
    private readonly PoolCapacityThresholds _thresholds;
    private readonly ConcurrentDictionary<string, PoolCapacitySnapshot> _snapshots = new();
    private Timer? _monitorTimer;
    private bool _disposed;

    public event EventHandler<PoolCapacityAlertEventArgs>? CapacityAlert;
    public event EventHandler<PoolCapacityAlertEventArgs>? MemberAlert;

    public PoolCapacityMonitor(
        StoragePoolRegistry poolRegistry,
        PoolCapacityThresholds? thresholds = null)
    {
        _poolRegistry = poolRegistry;
        _thresholds = thresholds ?? new PoolCapacityThresholds();
    }

    /// <summary>
    /// Starts monitoring with specified interval.
    /// </summary>
    public void Start(TimeSpan? interval = null)
    {
        var checkInterval = interval ?? TimeSpan.FromMinutes(1);
        _monitorTimer = new Timer(_ => CheckCapacity(), null, TimeSpan.Zero, checkInterval);
    }

    /// <summary>
    /// Gets aggregated statistics for all pools.
    /// </summary>
    public AggregatedPoolStats GetAggregatedStats()
    {
        var pools = _poolRegistry.GetAllPools();
        var stats = pools.Select(p => p.GetStatistics()).ToList();

        return new AggregatedPoolStats
        {
            TotalPools = pools.Count,
            TotalMembers = stats.Sum(s => s.TotalMembers),
            ActiveMembers = stats.Sum(s => s.ActiveMembers),
            TotalCapacityBytes = stats.Sum(s => s.TotalCapacityBytes),
            UsedBytes = stats.Sum(s => s.UsedBytes),
            AvailableBytes = stats.Sum(s => s.AvailableBytes),
            PoolStatistics = stats
        };
    }

    private void CheckCapacity()
    {
        foreach (var pool in _poolRegistry.GetAllPools())
        {
            var stats = pool.GetStatistics();
            var previousSnapshot = _snapshots.GetValueOrDefault(pool.PoolId);

            // Check pool-level thresholds
            if (stats.UsagePercent >= _thresholds.CriticalUsagePercent)
            {
                RaiseAlert(pool, AlertLevel.Critical, $"Pool usage critical: {stats.UsagePercent:F1}%");
            }
            else if (stats.UsagePercent >= _thresholds.WarningUsagePercent)
            {
                RaiseAlert(pool, AlertLevel.Warning, $"Pool usage high: {stats.UsagePercent:F1}%");
            }

            // Check member-level
            foreach (var member in pool.Members)
            {
                if (member.UsagePercent >= _thresholds.MemberCriticalPercent)
                {
                    RaiseMemberAlert(pool, member, AlertLevel.Critical,
                        $"Member {member.NodeId.ToShortString()} usage critical: {member.UsagePercent:F1}%");
                }

                if (member.State == PoolMemberState.Failed || member.State == PoolMemberState.Unavailable)
                {
                    RaiseMemberAlert(pool, member, AlertLevel.Warning,
                        $"Member {member.NodeId.ToShortString()} is {member.State}");
                }
            }

            // Check growth rate
            if (previousSnapshot != null)
            {
                var elapsed = (DateTimeOffset.UtcNow - previousSnapshot.Timestamp).TotalHours;
                if (elapsed > 0)
                {
                    var growthRate = (stats.UsedBytes - previousSnapshot.UsedBytes) / elapsed;
                    var hoursToFull = growthRate > 0
                        ? stats.AvailableBytes / growthRate
                        : double.MaxValue;

                    if (hoursToFull < _thresholds.HoursToFullWarning)
                    {
                        RaiseAlert(pool, AlertLevel.Warning,
                            $"Pool projected full in {hoursToFull:F1} hours at current growth rate");
                    }
                }
            }

            // Update snapshot
            _snapshots[pool.PoolId] = new PoolCapacitySnapshot
            {
                PoolId = pool.PoolId,
                UsedBytes = stats.UsedBytes,
                Timestamp = DateTimeOffset.UtcNow
            };
        }
    }

    private void RaiseAlert(IStoragePool pool, AlertLevel level, string message)
    {
        CapacityAlert?.Invoke(this, new PoolCapacityAlertEventArgs
        {
            PoolId = pool.PoolId,
            PoolName = pool.Name,
            Level = level,
            Message = message,
            Timestamp = DateTimeOffset.UtcNow
        });
    }

    private void RaiseMemberAlert(IStoragePool pool, StoragePoolMember member, AlertLevel level, string message)
    {
        MemberAlert?.Invoke(this, new PoolCapacityAlertEventArgs
        {
            PoolId = pool.PoolId,
            PoolName = pool.Name,
            MemberId = member.NodeId.ToHex(),
            Level = level,
            Message = message,
            Timestamp = DateTimeOffset.UtcNow
        });
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _monitorTimer?.Dispose();
            _disposed = true;
        }
    }

    private sealed class PoolCapacitySnapshot
    {
        public string PoolId { get; set; } = string.Empty;
        public long UsedBytes { get; set; }
        public DateTimeOffset Timestamp { get; set; }
    }
}

/// <summary>
/// Thresholds for capacity alerts.
/// </summary>
public sealed class PoolCapacityThresholds
{
    public float WarningUsagePercent { get; set; } = 75f;
    public float CriticalUsagePercent { get; set; } = 90f;
    public float MemberCriticalPercent { get; set; } = 95f;
    public double HoursToFullWarning { get; set; } = 24;
}

public enum AlertLevel
{
    Info,
    Warning,
    Critical
}

public sealed class PoolCapacityAlertEventArgs : EventArgs
{
    public string PoolId { get; set; } = string.Empty;
    public string PoolName { get; set; } = string.Empty;
    public string? MemberId { get; set; }
    public AlertLevel Level { get; set; }
    public string Message { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; }
}

/// <summary>
/// Aggregated statistics across all pools.
/// </summary>
public sealed class AggregatedPoolStats
{
    public int TotalPools { get; set; }
    public int TotalMembers { get; set; }
    public int ActiveMembers { get; set; }
    public long TotalCapacityBytes { get; set; }
    public long UsedBytes { get; set; }
    public long AvailableBytes { get; set; }
    public float UsagePercent => TotalCapacityBytes > 0 ? (float)UsedBytes / TotalCapacityBytes * 100 : 0;
    public List<PoolStatistics> PoolStatistics { get; set; } = new();
}

#endregion
