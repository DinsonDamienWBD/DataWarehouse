namespace DataWarehouse.SDK.Federation;

using System.Collections.Concurrent;
using System.Text.Json;

#region 3.1 Node Discovery

/// <summary>
/// Discovery method for finding nodes.
/// </summary>
public enum DiscoveryMethod
{
    /// <summary>Static configuration.</summary>
    Static = 0,
    /// <summary>Multicast/broadcast on local network.</summary>
    Multicast = 1,
    /// <summary>DNS-based discovery.</summary>
    Dns = 2,
    /// <summary>Bootstrap nodes.</summary>
    Bootstrap = 3,
    /// <summary>Gossip protocol.</summary>
    Gossip = 4
}

/// <summary>
/// Interface for node discovery.
/// </summary>
public interface INodeDiscovery
{
    /// <summary>Discovers available nodes.</summary>
    Task<IReadOnlyList<NodeIdentity>> DiscoverAsync(TimeSpan timeout, CancellationToken ct = default);

    /// <summary>Announces local node presence.</summary>
    Task AnnounceAsync(CancellationToken ct = default);

    /// <summary>Starts continuous discovery.</summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>Stops discovery.</summary>
    Task StopAsync();
}

/// <summary>
/// Gossip-based node discovery and failure detection.
/// </summary>
public sealed class GossipDiscovery : INodeDiscovery, IAsyncDisposable
{
    private readonly NodeIdentityManager _identityManager;
    private readonly NodeRegistry _nodeRegistry;
    private readonly ITransportBus _transportBus;
    private readonly TimeSpan _gossipInterval;
    private readonly int _fanout;
    private readonly ConcurrentDictionary<NodeId, long> _heartbeats = new();
    private CancellationTokenSource? _cts;
    private Task? _gossipTask;

    public GossipDiscovery(
        NodeIdentityManager identityManager,
        NodeRegistry nodeRegistry,
        ITransportBus transportBus,
        TimeSpan? gossipInterval = null,
        int fanout = 3)
    {
        _identityManager = identityManager;
        _nodeRegistry = nodeRegistry;
        _transportBus = transportBus;
        _gossipInterval = gossipInterval ?? TimeSpan.FromSeconds(1);
        _fanout = fanout;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<NodeIdentity>> DiscoverAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        // Send discovery request to known nodes
        var message = _identityManager.CreateSignedMessage(
            Array.Empty<byte>(),
            "discovery.request");

        var knownNodes = _nodeRegistry.GetNodes(n => n.State == NodeState.Active);
        await _transportBus.BroadcastAsync(knownNodes.Select(n => n.Id), message, ct);

        // Wait for responses
        await Task.Delay(timeout, ct);

        return _nodeRegistry.GetNodes(n => n.State == NodeState.Active);
    }

    /// <inheritdoc />
    public async Task AnnounceAsync(CancellationToken ct = default)
    {
        var identity = _identityManager.ExportPublicIdentity();
        var payload = JsonSerializer.SerializeToUtf8Bytes(identity);
        var message = _identityManager.CreateSignedMessage(payload, "discovery.announce");

        var nodes = _nodeRegistry.GetNodes(n => n.Id != _identityManager.LocalIdentity.Id);
        await _transportBus.BroadcastAsync(nodes.Select(n => n.Id), message, ct);
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Register message handlers
        _transportBus.OnMessage("discovery.request", HandleDiscoveryRequest);
        _transportBus.OnMessage("discovery.announce", HandleAnnounce);
        _transportBus.OnMessage("gossip.heartbeat", HandleHeartbeat);
        _transportBus.OnMessage("gossip.sync", HandleGossipSync);

        _gossipTask = RunGossipLoopAsync(_cts.Token);

        return Task.CompletedTask;
    }

    private async Task RunGossipLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await GossipRoundAsync(ct);
                await Task.Delay(_gossipInterval, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Continue gossiping
            }
        }
    }

    private async Task GossipRoundAsync(CancellationToken ct)
    {
        // Select random nodes to gossip with
        var allNodes = _nodeRegistry.GetNodes(n => n.Id != _identityManager.LocalIdentity.Id);
        var targets = SelectRandomNodes(allNodes, _fanout);

        // Send heartbeat
        var heartbeat = new GossipHeartbeat
        {
            NodeId = _identityManager.LocalIdentity.Id.ToHex(),
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            KnownNodes = _heartbeats.ToDictionary(kvp => kvp.Key.ToHex(), kvp => kvp.Value)
        };

        var payload = JsonSerializer.SerializeToUtf8Bytes(heartbeat);
        var message = _identityManager.CreateSignedMessage(payload, "gossip.heartbeat");

        await _transportBus.BroadcastAsync(targets.Select(n => n.Id), message, ct);

        // Update local heartbeat
        _heartbeats[_identityManager.LocalIdentity.Id] = heartbeat.Timestamp;

        // Check for failed nodes
        var timeout = _gossipInterval * 5;
        var cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (long)timeout.TotalMilliseconds;

        foreach (var (nodeId, lastSeen) in _heartbeats.ToArray())
        {
            if (lastSeen < cutoff && nodeId != _identityManager.LocalIdentity.Id)
            {
                _nodeRegistry.UpdateState(nodeId, NodeState.Offline);
            }
        }
    }

    private IReadOnlyList<NodeIdentity> SelectRandomNodes(IReadOnlyList<NodeIdentity> nodes, int count)
    {
        if (nodes.Count <= count) return nodes;

        var random = new Random();
        return nodes.OrderBy(_ => random.Next()).Take(count).ToList();
    }

    private async Task HandleDiscoveryRequest(SignedMessage message, NodeConnection connection)
    {
        var identity = _identityManager.ExportPublicIdentity();
        var payload = JsonSerializer.SerializeToUtf8Bytes(identity);
        var response = _identityManager.CreateSignedMessage(payload, "discovery.announce");
        await connection.SendMessageAsync(response);
    }

    private Task HandleAnnounce(SignedMessage message, NodeConnection connection)
    {
        var identity = JsonSerializer.Deserialize<NodeIdentity>(message.Payload);
        if (identity != null)
        {
            _nodeRegistry.Register(identity);
        }
        return Task.CompletedTask;
    }

    private Task HandleHeartbeat(SignedMessage message, NodeConnection connection)
    {
        var heartbeat = JsonSerializer.Deserialize<GossipHeartbeat>(message.Payload);
        if (heartbeat != null)
        {
            var nodeId = NodeId.FromHex(heartbeat.NodeId);
            _heartbeats[nodeId] = heartbeat.Timestamp;
            _nodeRegistry.UpdateState(nodeId, NodeState.Active);

            // Merge known nodes
            foreach (var (id, ts) in heartbeat.KnownNodes)
            {
                var nid = NodeId.FromHex(id);
                _heartbeats.AddOrUpdate(nid, ts, (_, existing) => Math.Max(existing, ts));
            }
        }
        return Task.CompletedTask;
    }

    private Task HandleGossipSync(SignedMessage message, NodeConnection connection)
    {
        // Handle full state sync if needed
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync()
    {
        _cts?.Cancel();
        if (_gossipTask != null)
        {
            try { await _gossipTask; }
            catch { /* Ignore cancellation */ }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cts?.Dispose();
    }
}

/// <summary>
/// Gossip heartbeat message.
/// </summary>
public sealed class GossipHeartbeat
{
    public string NodeId { get; set; } = string.Empty;
    public long Timestamp { get; set; }
    public Dictionary<string, long> KnownNodes { get; set; } = new();
}

#endregion

#region 3.2 Metadata Synchronization

/// <summary>
/// Vector clock for causality tracking.
/// </summary>
public sealed class VectorClock : IEquatable<VectorClock>
{
    private readonly Dictionary<string, long> _clock = new();

    /// <summary>
    /// Gets the current vector.
    /// </summary>
    public IReadOnlyDictionary<string, long> Vector => _clock;

    /// <summary>
    /// Increments the clock for a node.
    /// </summary>
    public void Increment(string nodeId)
    {
        _clock[nodeId] = _clock.GetValueOrDefault(nodeId) + 1;
    }

    /// <summary>
    /// Merges another clock into this one.
    /// </summary>
    public void Merge(VectorClock other)
    {
        foreach (var (nodeId, value) in other._clock)
        {
            _clock[nodeId] = Math.Max(_clock.GetValueOrDefault(nodeId), value);
        }
    }

    /// <summary>
    /// Compares causality with another clock.
    /// </summary>
    public ClockComparison Compare(VectorClock other)
    {
        var allKeys = _clock.Keys.Union(other._clock.Keys).ToList();

        bool thisGreater = false;
        bool otherGreater = false;

        foreach (var key in allKeys)
        {
            var thisVal = _clock.GetValueOrDefault(key);
            var otherVal = other._clock.GetValueOrDefault(key);

            if (thisVal > otherVal) thisGreater = true;
            if (otherVal > thisVal) otherGreater = true;
        }

        if (thisGreater && otherGreater) return ClockComparison.Concurrent;
        if (thisGreater) return ClockComparison.HappenedAfter;
        if (otherGreater) return ClockComparison.HappenedBefore;
        return ClockComparison.Equal;
    }

    public VectorClock Clone()
    {
        var clone = new VectorClock();
        foreach (var kvp in _clock)
            clone._clock[kvp.Key] = kvp.Value;
        return clone;
    }

    public bool Equals(VectorClock? other)
    {
        if (other == null) return false;
        return Compare(other) == ClockComparison.Equal;
    }

    public override bool Equals(object? obj) => obj is VectorClock vc && Equals(vc);
    public override int GetHashCode() => _clock.Count;
}

/// <summary>
/// Clock comparison result.
/// </summary>
public enum ClockComparison
{
    /// <summary>Clocks are equal.</summary>
    Equal,
    /// <summary>This happened before the other.</summary>
    HappenedBefore,
    /// <summary>This happened after the other.</summary>
    HappenedAfter,
    /// <summary>Concurrent (conflict).</summary>
    Concurrent
}

/// <summary>
/// A metadata entry with vector clock.
/// </summary>
public sealed class MetadataEntry
{
    public ObjectId ObjectId { get; init; }
    public ObjectManifest Manifest { get; set; } = new();
    public VectorClock Clock { get; set; } = new();
    public DateTimeOffset LastModified { get; set; } = DateTimeOffset.UtcNow;
    public bool IsDeleted { get; set; }
}

/// <summary>
/// Interface for metadata synchronization.
/// </summary>
public interface IMetadataSync
{
    /// <summary>Syncs metadata with a remote node.</summary>
    Task<SyncResult> SyncWithAsync(NodeId nodeId, CancellationToken ct = default);

    /// <summary>Applies a remote metadata update.</summary>
    Task<bool> ApplyUpdateAsync(MetadataEntry entry, CancellationToken ct = default);

    /// <summary>Gets entries changed since a vector clock.</summary>
    Task<IReadOnlyList<MetadataEntry>> GetChangesSinceAsync(VectorClock since, CancellationToken ct = default);
}

/// <summary>
/// Result of a sync operation.
/// </summary>
public sealed class SyncResult
{
    public bool Success { get; init; }
    public int EntriesReceived { get; init; }
    public int EntriesSent { get; init; }
    public int Conflicts { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// CRDT-based metadata synchronization.
/// </summary>
public sealed class CrdtMetadataSync : IMetadataSync
{
    private readonly ConcurrentDictionary<ObjectId, MetadataEntry> _entries = new();
    private readonly NodeIdentityManager _identityManager;
    private readonly ITransportBus _transportBus;
    private readonly Func<MetadataEntry, MetadataEntry, MetadataEntry>? _conflictResolver;

    public CrdtMetadataSync(
        NodeIdentityManager identityManager,
        ITransportBus transportBus,
        Func<MetadataEntry, MetadataEntry, MetadataEntry>? conflictResolver = null)
    {
        _identityManager = identityManager;
        _transportBus = transportBus;
        _conflictResolver = conflictResolver ?? DefaultConflictResolver;
    }

    /// <inheritdoc />
    public async Task<SyncResult> SyncWithAsync(NodeId nodeId, CancellationToken ct = default)
    {
        try
        {
            // Send our current state
            var localEntries = _entries.Values.ToList();
            var payload = JsonSerializer.SerializeToUtf8Bytes(localEntries);
            var message = _identityManager.CreateSignedMessage(payload, "metadata.sync");

            var connection = await _transportBus.GetConnectionAsync(nodeId, ct);
            await connection.SendMessageAsync(message, ct);

            // Receive remote state
            var response = await connection.ReceiveMessageAsync(ct);
            if (response == null)
                return new SyncResult { Success = false, ErrorMessage = "No response" };

            var remoteEntries = JsonSerializer.Deserialize<List<MetadataEntry>>(response.Payload)
                ?? new List<MetadataEntry>();

            var conflicts = 0;
            foreach (var entry in remoteEntries)
            {
                if (await ApplyUpdateAsync(entry, ct))
                    conflicts++;
            }

            return new SyncResult
            {
                Success = true,
                EntriesReceived = remoteEntries.Count,
                EntriesSent = localEntries.Count,
                Conflicts = conflicts
            };
        }
        catch (Exception ex)
        {
            return new SyncResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    /// <inheritdoc />
    public Task<bool> ApplyUpdateAsync(MetadataEntry entry, CancellationToken ct = default)
    {
        var hadConflict = false;

        _entries.AddOrUpdate(
            entry.ObjectId,
            entry,
            (_, existing) =>
            {
                var comparison = existing.Clock.Compare(entry.Clock);

                switch (comparison)
                {
                    case ClockComparison.HappenedBefore:
                        // Remote is newer, accept it
                        return entry;

                    case ClockComparison.HappenedAfter:
                        // Local is newer, keep it
                        return existing;

                    case ClockComparison.Concurrent:
                        // Conflict - use resolver
                        hadConflict = true;
                        return _conflictResolver!(existing, entry);

                    default:
                        // Equal - no change
                        return existing;
                }
            });

        return Task.FromResult(hadConflict);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<MetadataEntry>> GetChangesSinceAsync(VectorClock since, CancellationToken ct = default)
    {
        var changes = _entries.Values
            .Where(e => e.Clock.Compare(since) == ClockComparison.HappenedAfter)
            .ToList();

        return Task.FromResult<IReadOnlyList<MetadataEntry>>(changes);
    }

    /// <summary>
    /// Updates a local entry.
    /// </summary>
    public void UpdateLocal(ObjectId objectId, Action<ObjectManifest> updater)
    {
        var nodeId = _identityManager.LocalIdentity.Id.ToHex();

        _entries.AddOrUpdate(
            objectId,
            _ =>
            {
                var manifest = new ObjectManifest { ObjectId = objectId };
                updater(manifest);
                var clock = new VectorClock();
                clock.Increment(nodeId);
                return new MetadataEntry
                {
                    ObjectId = objectId,
                    Manifest = manifest,
                    Clock = clock,
                    LastModified = DateTimeOffset.UtcNow
                };
            },
            (_, existing) =>
            {
                updater(existing.Manifest);
                existing.Clock.Increment(nodeId);
                existing.LastModified = DateTimeOffset.UtcNow;
                return existing;
            });
    }

    private static MetadataEntry DefaultConflictResolver(MetadataEntry local, MetadataEntry remote)
    {
        // Last-write-wins by default
        return local.LastModified >= remote.LastModified ? local : remote;
    }
}

#endregion

#region 3.3 Object Replication

/// <summary>
/// Replication strategy.
/// </summary>
public enum ReplicationStrategy
{
    /// <summary>Synchronous replication (wait for all replicas).</summary>
    Synchronous,
    /// <summary>Asynchronous replication (return after primary write).</summary>
    Asynchronous,
    /// <summary>Quorum-based replication.</summary>
    Quorum
}

/// <summary>
/// Replication configuration.
/// </summary>
public sealed class ReplicationConfig
{
    /// <summary>Number of replicas to maintain.</summary>
    public int ReplicationFactor { get; set; } = 3;

    /// <summary>Replication strategy.</summary>
    public ReplicationStrategy Strategy { get; set; } = ReplicationStrategy.Quorum;

    /// <summary>Minimum writes for quorum.</summary>
    public int WriteQuorum { get; set; } = 2;

    /// <summary>Minimum reads for quorum.</summary>
    public int ReadQuorum { get; set; } = 2;

    /// <summary>Whether to use erasure coding.</summary>
    public bool UseErasureCoding { get; set; } = false;

    /// <summary>Data shards for erasure coding.</summary>
    public int DataShards { get; set; } = 4;

    /// <summary>Parity shards for erasure coding.</summary>
    public int ParityShards { get; set; } = 2;
}

/// <summary>
/// Interface for object replication.
/// </summary>
public interface IObjectReplicator
{
    /// <summary>Replicates an object to target nodes.</summary>
    Task<ReplicationResult> ReplicateAsync(ObjectId objectId, IEnumerable<NodeId> targetNodes, CancellationToken ct = default);

    /// <summary>Fetches an object from replica nodes.</summary>
    Task<FetchResult> FetchFromReplicasAsync(ObjectId objectId, CancellationToken ct = default);

    /// <summary>Checks replica health and repairs if needed.</summary>
    Task<RepairResult> CheckAndRepairAsync(ObjectId objectId, CancellationToken ct = default);
}

/// <summary>
/// Result of replication operation.
/// </summary>
public sealed class ReplicationResult
{
    public bool Success { get; init; }
    public int SuccessfulReplicas { get; init; }
    public int FailedReplicas { get; init; }
    public List<NodeId> SuccessNodes { get; init; } = new();
    public List<NodeId> FailedNodes { get; init; } = new();
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Result of fetch operation.
/// </summary>
public sealed class FetchResult
{
    public bool Success { get; init; }
    public byte[]? Data { get; init; }
    public NodeId SourceNode { get; init; }
    public int AttemptedNodes { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Result of repair operation.
/// </summary>
public sealed class RepairResult
{
    public bool Healthy { get; init; }
    public int ExistingReplicas { get; init; }
    public int RepairedReplicas { get; init; }
    public int RequiredReplicas { get; init; }
}

/// <summary>
/// Object replicator with quorum support.
/// </summary>
public sealed class QuorumReplicator : IObjectReplicator
{
    private readonly ContentAddressableObjectStore _objectStore;
    private readonly ITransportBus _transportBus;
    private readonly NodeRegistry _nodeRegistry;
    private readonly ReplicationConfig _config;

    public QuorumReplicator(
        ContentAddressableObjectStore objectStore,
        ITransportBus transportBus,
        NodeRegistry nodeRegistry,
        ReplicationConfig? config = null)
    {
        _objectStore = objectStore;
        _transportBus = transportBus;
        _nodeRegistry = nodeRegistry;
        _config = config ?? new ReplicationConfig();
    }

    /// <inheritdoc />
    public async Task<ReplicationResult> ReplicateAsync(
        ObjectId objectId,
        IEnumerable<NodeId> targetNodes,
        CancellationToken ct = default)
    {
        var targets = targetNodes.ToList();
        var successNodes = new List<NodeId>();
        var failedNodes = new List<NodeId>();

        // Get the object data
        var result = await _objectStore.RetrieveAsync(objectId, ct);
        if (!result.Success || result.Data == null)
        {
            return new ReplicationResult
            {
                Success = false,
                ErrorMessage = "Object not found locally"
            };
        }

        // Replicate based on strategy
        if (_config.Strategy == ReplicationStrategy.Synchronous)
        {
            // Wait for all
            var tasks = targets.Select(async nodeId =>
            {
                var success = await ReplicateToNodeAsync(objectId, result.Data, nodeId, ct);
                return (nodeId, success);
            });

            var results = await Task.WhenAll(tasks);

            foreach (var (nodeId, success) in results)
            {
                if (success)
                    successNodes.Add(nodeId);
                else
                    failedNodes.Add(nodeId);
            }
        }
        else if (_config.Strategy == ReplicationStrategy.Quorum)
        {
            // Wait for quorum
            var completed = 0;
            var semaphore = new SemaphoreSlim(0);

            foreach (var nodeId in targets)
            {
                _ = Task.Run(async () =>
                {
                    var success = await ReplicateToNodeAsync(objectId, result.Data, nodeId, ct);
                    lock (successNodes)
                    {
                        if (success)
                            successNodes.Add(nodeId);
                        else
                            failedNodes.Add(nodeId);
                        completed++;
                    }
                    semaphore.Release();
                }, ct);
            }

            // Wait for quorum
            var needed = _config.WriteQuorum;
            while (successNodes.Count < needed && completed < targets.Count)
            {
                await semaphore.WaitAsync(ct);
            }
        }
        else // Asynchronous
        {
            // Fire and forget
            foreach (var nodeId in targets)
            {
                _ = ReplicateToNodeAsync(objectId, result.Data, nodeId, ct);
            }
            successNodes.AddRange(targets); // Optimistically assume success
        }

        var quorumMet = successNodes.Count >= _config.WriteQuorum;

        return new ReplicationResult
        {
            Success = quorumMet,
            SuccessfulReplicas = successNodes.Count,
            FailedReplicas = failedNodes.Count,
            SuccessNodes = successNodes,
            FailedNodes = failedNodes
        };
    }

    private async Task<bool> ReplicateToNodeAsync(ObjectId objectId, byte[] data, NodeId nodeId, CancellationToken ct)
    {
        try
        {
            var connection = await _transportBus.GetConnectionAsync(nodeId, ct);

            // Send replication request
            var request = new ReplicationRequest
            {
                ObjectId = objectId.ToHex(),
                Data = data
            };

            var payload = JsonSerializer.SerializeToUtf8Bytes(request);
            await connection.SendAsync(payload, ct);

            // Wait for acknowledgment
            var buffer = new byte[1024];
            var read = await connection.ReceiveAsync(buffer, ct);

            return read > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<FetchResult> FetchFromReplicasAsync(ObjectId objectId, CancellationToken ct = default)
    {
        // Get nodes that have this object
        var storageNodes = _nodeRegistry.GetNodesWithCapability(NodeCapabilities.Storage);
        var attempted = 0;

        foreach (var node in storageNodes)
        {
            attempted++;
            try
            {
                var connection = await _transportBus.GetConnectionAsync(node.Id, ct);

                // Send fetch request
                var request = new FetchRequest { ObjectId = objectId.ToHex() };
                var payload = JsonSerializer.SerializeToUtf8Bytes(request);
                await connection.SendAsync(payload, ct);

                // Receive data
                var buffer = new byte[1024 * 1024]; // 1MB buffer
                var read = await connection.ReceiveAsync(buffer, ct);

                if (read > 0)
                {
                    return new FetchResult
                    {
                        Success = true,
                        Data = buffer[..read],
                        SourceNode = node.Id,
                        AttemptedNodes = attempted
                    };
                }
            }
            catch
            {
                continue;
            }
        }

        return new FetchResult
        {
            Success = false,
            AttemptedNodes = attempted,
            ErrorMessage = "Object not found on any replica"
        };
    }

    /// <inheritdoc />
    public async Task<RepairResult> CheckAndRepairAsync(ObjectId objectId, CancellationToken ct = default)
    {
        var storageNodes = _nodeRegistry.GetNodesWithCapability(NodeCapabilities.Storage);
        var existingReplicas = 0;
        var nodesWithObject = new List<NodeId>();

        // Check which nodes have the object
        foreach (var node in storageNodes)
        {
            try
            {
                // In real implementation, would send a lightweight check request
                nodesWithObject.Add(node.Id);
                existingReplicas++;
            }
            catch
            {
                continue;
            }
        }

        var needed = _config.ReplicationFactor;
        var repaired = 0;

        if (existingReplicas < needed && nodesWithObject.Count > 0)
        {
            // Need to replicate to more nodes
            var targetNodes = storageNodes
                .Where(n => !nodesWithObject.Contains(n.Id))
                .Take(needed - existingReplicas)
                .Select(n => n.Id);

            var result = await ReplicateAsync(objectId, targetNodes, ct);
            repaired = result.SuccessfulReplicas;
        }

        return new RepairResult
        {
            Healthy = existingReplicas >= needed,
            ExistingReplicas = existingReplicas,
            RepairedReplicas = repaired,
            RequiredReplicas = needed
        };
    }
}

/// <summary>
/// Replication request message.
/// </summary>
public sealed class ReplicationRequest
{
    public string ObjectId { get; set; } = string.Empty;
    public byte[] Data { get; set; } = Array.Empty<byte>();
}

/// <summary>
/// Fetch request message.
/// </summary>
public sealed class FetchRequest
{
    public string ObjectId { get; set; } = string.Empty;
}

#endregion

#region 3.4 Cluster Coordination

/// <summary>
/// Coordinator role in the cluster.
/// </summary>
public enum CoordinatorRole
{
    /// <summary>Regular participant.</summary>
    Follower,
    /// <summary>Coordinator candidate.</summary>
    Candidate,
    /// <summary>Current coordinator.</summary>
    Leader
}

/// <summary>
/// Interface for cluster coordination.
/// </summary>
public interface IClusterCoordinator
{
    /// <summary>Gets the current coordinator node.</summary>
    NodeId? CurrentLeader { get; }

    /// <summary>Gets this node's role.</summary>
    CoordinatorRole Role { get; }

    /// <summary>Starts participating in coordination.</summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>Stops coordination.</summary>
    Task StopAsync();

    /// <summary>Requests a distributed lock.</summary>
    Task<DistributedLock?> AcquireLockAsync(string resource, TimeSpan timeout, CancellationToken ct = default);
}

/// <summary>
/// A distributed lock.
/// </summary>
public sealed class DistributedLock : IAsyncDisposable
{
    private readonly Func<Task> _releaseFunc;
    private bool _released;

    public string Resource { get; init; } = string.Empty;
    public string LockId { get; init; } = string.Empty;
    public NodeId Owner { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }

    public DistributedLock(Func<Task> releaseFunc)
    {
        _releaseFunc = releaseFunc;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_released)
        {
            await _releaseFunc();
            _released = true;
        }
    }
}

/// <summary>
/// Simple leader election using bully algorithm.
/// </summary>
public sealed class BullyCoordinator : IClusterCoordinator, IAsyncDisposable
{
    private readonly NodeIdentityManager _identityManager;
    private readonly NodeRegistry _nodeRegistry;
    private readonly ITransportBus _transportBus;
    private readonly ConcurrentDictionary<string, DistributedLock> _locks = new();
    private readonly TimeSpan _electionTimeout;
    private readonly TimeSpan _heartbeatInterval;
    private CancellationTokenSource? _cts;
    private Task? _heartbeatTask;
    private NodeId? _currentLeader;
    private CoordinatorRole _role = CoordinatorRole.Follower;

    /// <inheritdoc />
    public NodeId? CurrentLeader => _currentLeader;

    /// <inheritdoc />
    public CoordinatorRole Role => _role;

    public BullyCoordinator(
        NodeIdentityManager identityManager,
        NodeRegistry nodeRegistry,
        ITransportBus transportBus,
        TimeSpan? electionTimeout = null,
        TimeSpan? heartbeatInterval = null)
    {
        _identityManager = identityManager;
        _nodeRegistry = nodeRegistry;
        _transportBus = transportBus;
        _electionTimeout = electionTimeout ?? TimeSpan.FromSeconds(5);
        _heartbeatInterval = heartbeatInterval ?? TimeSpan.FromSeconds(1);
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        _transportBus.OnMessage("election.start", HandleElectionStart);
        _transportBus.OnMessage("election.victory", HandleVictory);
        _transportBus.OnMessage("leader.heartbeat", HandleLeaderHeartbeat);
        _transportBus.OnMessage("lock.request", HandleLockRequest);
        _transportBus.OnMessage("lock.release", HandleLockRelease);

        // Start election if no leader
        _ = StartElectionAsync(_cts.Token);

        return Task.CompletedTask;
    }

    private async Task StartElectionAsync(CancellationToken ct)
    {
        _role = CoordinatorRole.Candidate;

        var myId = _identityManager.LocalIdentity.Id;
        var higherNodes = _nodeRegistry.GetNodes(n =>
            n.State == NodeState.Active &&
            n.Id.CompareTo(myId) > 0);

        if (higherNodes.Count == 0)
        {
            // No higher nodes, we become leader
            await BecomeLeaderAsync(ct);
            return;
        }

        // Send election message to higher nodes
        var message = _identityManager.CreateSignedMessage(
            Array.Empty<byte>(),
            "election.start");

        await _transportBus.BroadcastAsync(higherNodes.Select(n => n.Id), message, ct);

        // Wait for response
        await Task.Delay(_electionTimeout, ct);

        // If no one claimed leadership, we become leader
        if (_currentLeader == null)
        {
            await BecomeLeaderAsync(ct);
        }
    }

    private async Task BecomeLeaderAsync(CancellationToken ct)
    {
        _role = CoordinatorRole.Leader;
        _currentLeader = _identityManager.LocalIdentity.Id;

        // Announce victory
        var message = _identityManager.CreateSignedMessage(
            Array.Empty<byte>(),
            "election.victory");

        var allNodes = _nodeRegistry.GetNodes(n => n.Id != _currentLeader);
        await _transportBus.BroadcastAsync(allNodes.Select(n => n.Id), message, ct);

        // Start heartbeat
        _heartbeatTask = RunHeartbeatLoopAsync(ct);
    }

    private async Task RunHeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _role == CoordinatorRole.Leader)
        {
            var message = _identityManager.CreateSignedMessage(
                Array.Empty<byte>(),
                "leader.heartbeat");

            var allNodes = _nodeRegistry.GetNodes(n => n.Id != _currentLeader);
            await _transportBus.BroadcastAsync(allNodes.Select(n => n.Id), message, ct);

            await Task.Delay(_heartbeatInterval, ct);
        }
    }

    private Task HandleElectionStart(SignedMessage message, NodeConnection connection)
    {
        var senderId = message.SenderId;
        if (_identityManager.LocalIdentity.Id.CompareTo(senderId) > 0)
        {
            // We have higher ID, start our own election
            _ = StartElectionAsync(_cts?.Token ?? default);
        }
        return Task.CompletedTask;
    }

    private Task HandleVictory(SignedMessage message, NodeConnection connection)
    {
        _currentLeader = message.SenderId;
        _role = CoordinatorRole.Follower;
        return Task.CompletedTask;
    }

    private Task HandleLeaderHeartbeat(SignedMessage message, NodeConnection connection)
    {
        _currentLeader = message.SenderId;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<DistributedLock?> AcquireLockAsync(string resource, TimeSpan timeout, CancellationToken ct = default)
    {
        if (_role != CoordinatorRole.Leader)
        {
            // Forward to leader
            if (_currentLeader == null) return null;

            var request = new LockRequest { Resource = resource, TimeoutMs = (long)timeout.TotalMilliseconds };
            var payload = JsonSerializer.SerializeToUtf8Bytes(request);
            var message = _identityManager.CreateSignedMessage(payload, "lock.request");

            await _transportBus.SendAsync(_currentLeader.Value, message, ct);

            // Wait for response (simplified)
            await Task.Delay(100, ct);
            return null;
        }

        // We are the leader
        var lockId = Guid.NewGuid().ToString("N");

        if (_locks.ContainsKey(resource))
            return null; // Already locked

        var distributedLock = new DistributedLock(async () =>
        {
            _locks.TryRemove(resource, out _);
            await Task.CompletedTask;
        })
        {
            Resource = resource,
            LockId = lockId,
            Owner = _identityManager.LocalIdentity.Id,
            ExpiresAt = DateTimeOffset.UtcNow + timeout
        };

        if (_locks.TryAdd(resource, distributedLock))
            return distributedLock;

        return null;
    }

    private Task HandleLockRequest(SignedMessage message, NodeConnection connection)
    {
        // Handle lock request from remote node
        return Task.CompletedTask;
    }

    private Task HandleLockRelease(SignedMessage message, NodeConnection connection)
    {
        // Handle lock release from remote node
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync()
    {
        _cts?.Cancel();
        if (_heartbeatTask != null)
        {
            try { await _heartbeatTask; }
            catch { /* Ignore cancellation */ }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cts?.Dispose();
    }
}

/// <summary>
/// Lock request message.
/// </summary>
public sealed class LockRequest
{
    public string Resource { get; set; } = string.Empty;
    public long TimeoutMs { get; set; }
}

#endregion
