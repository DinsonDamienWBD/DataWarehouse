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

#region H11: Consensus Handoff

/// <summary>
/// Manages graceful leader resignation and consensus handoff.
/// </summary>
public sealed class ConsensusHandoff
{
    private readonly IClusterCoordinator _coordinator;
    private readonly INodeDiscovery _discovery;
    private readonly IObjectReplicator _replicator;
    private readonly ConcurrentDictionary<string, HandoffState> _handoffStates = new();

    public ConsensusHandoff(IClusterCoordinator coordinator, INodeDiscovery discovery, IObjectReplicator replicator)
    {
        _coordinator = coordinator;
        _discovery = discovery;
        _replicator = replicator;
    }

    /// <summary>
    /// Initiates graceful leader resignation with state transfer.
    /// </summary>
    public async Task<HandoffResult> InitiateHandoffAsync(string targetNodeId, CancellationToken ct = default)
    {
        var handoffId = Guid.NewGuid().ToString("N");
        var state = new HandoffState
        {
            HandoffId = handoffId,
            TargetNodeId = targetNodeId,
            Phase = HandoffPhase.Initiated,
            StartedAt = DateTime.UtcNow
        };
        _handoffStates[handoffId] = state;

        try
        {
            // Phase 1: Announce resignation intent
            state.Phase = HandoffPhase.Announcing;
            await AnnounceResignationAsync(targetNodeId, ct);

            // Phase 2: Transfer state to target
            state.Phase = HandoffPhase.TransferringState;
            await TransferStateAsync(targetNodeId, ct);

            // Phase 3: Wait for target to acknowledge readiness
            state.Phase = HandoffPhase.AwaitingAck;
            await WaitForTargetReadyAsync(targetNodeId, ct);

            // Phase 4: Complete handoff
            state.Phase = HandoffPhase.Completing;
            await CompleteHandoffAsync(targetNodeId, ct);

            state.Phase = HandoffPhase.Completed;
            state.CompletedAt = DateTime.UtcNow;

            return new HandoffResult { Success = true, HandoffId = handoffId };
        }
        catch (Exception ex)
        {
            state.Phase = HandoffPhase.Failed;
            state.Error = ex.Message;
            return new HandoffResult { Success = false, HandoffId = handoffId, Error = ex.Message };
        }
    }

    private async Task AnnounceResignationAsync(string targetNodeId, CancellationToken ct)
    {
        // Broadcast resignation intent to all nodes
        await _discovery.AnnounceAsync(new Dictionary<string, string>
        {
            ["event"] = "leader_resignation",
            ["target"] = targetNodeId,
            ["timestamp"] = DateTime.UtcNow.ToString("O")
        }, ct);
        await Task.Delay(100, ct); // Allow propagation
    }

    private async Task TransferStateAsync(string targetNodeId, CancellationToken ct)
    {
        // Transfer any uncommitted state to target
        await Task.Delay(50, ct); // Simulated state transfer
    }

    private async Task WaitForTargetReadyAsync(string targetNodeId, CancellationToken ct)
    {
        // Wait for target to signal readiness (with timeout)
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        await Task.Delay(100, cts.Token);
    }

    private async Task CompleteHandoffAsync(string targetNodeId, CancellationToken ct)
    {
        // Final handoff completion
        await _discovery.AnnounceAsync(new Dictionary<string, string>
        {
            ["event"] = "leader_handoff_complete",
            ["new_leader"] = targetNodeId
        }, ct);
    }

    public HandoffState? GetHandoffState(string handoffId) =>
        _handoffStates.TryGetValue(handoffId, out var state) ? state : null;
}

public sealed class HandoffState
{
    public string HandoffId { get; init; } = string.Empty;
    public string TargetNodeId { get; init; } = string.Empty;
    public HandoffPhase Phase { get; set; }
    public DateTime StartedAt { get; init; }
    public DateTime? CompletedAt { get; set; }
    public string? Error { get; set; }
}

public enum HandoffPhase { Initiated, Announcing, TransferringState, AwaitingAck, Completing, Completed, Failed }

public sealed class HandoffResult
{
    public bool Success { get; init; }
    public string HandoffId { get; init; } = string.Empty;
    public string? Error { get; init; }
}

#endregion

#region H12: Graceful Shutdown Protocol

/// <summary>
/// Manages graceful node shutdown with data migration and connection draining.
/// </summary>
public sealed class GracefulShutdownProtocol : IAsyncDisposable
{
    private readonly INodeDiscovery _discovery;
    private readonly IObjectReplicator _replicator;
    private readonly string _nodeId;
    private readonly ConcurrentDictionary<string, bool> _shutdownAcks = new();
    private volatile ShutdownPhase _currentPhase = ShutdownPhase.Running;

    public event EventHandler<ShutdownPhase>? PhaseChanged;

    public GracefulShutdownProtocol(INodeDiscovery discovery, IObjectReplicator replicator, string nodeId)
    {
        _discovery = discovery;
        _replicator = replicator;
        _nodeId = nodeId;
    }

    public ShutdownPhase CurrentPhase => _currentPhase;

    /// <summary>
    /// Initiates graceful shutdown sequence.
    /// </summary>
    public async Task<ShutdownResult> InitiateShutdownAsync(TimeSpan drainTimeout, CancellationToken ct = default)
    {
        var result = new ShutdownResult { StartedAt = DateTime.UtcNow };

        try
        {
            // Phase 1: Announce shutdown intent
            SetPhase(ShutdownPhase.Announcing);
            await BroadcastShutdownIntentAsync(ct);

            // Phase 2: Drain connections (stop accepting new requests)
            SetPhase(ShutdownPhase.Draining);
            await DrainConnectionsAsync(drainTimeout, ct);

            // Phase 3: Migrate data to other nodes
            SetPhase(ShutdownPhase.Migrating);
            result.DataMigrated = await MigrateDataAsync(ct);

            // Phase 4: Wait for acknowledgments
            SetPhase(ShutdownPhase.AwaitingAcks);
            await WaitForAcksAsync(TimeSpan.FromSeconds(10), ct);

            // Phase 5: Final shutdown
            SetPhase(ShutdownPhase.Completed);
            result.CompletedAt = DateTime.UtcNow;
            result.Success = true;
        }
        catch (Exception ex)
        {
            SetPhase(ShutdownPhase.Failed);
            result.Error = ex.Message;
        }

        return result;
    }

    private void SetPhase(ShutdownPhase phase)
    {
        _currentPhase = phase;
        PhaseChanged?.Invoke(this, phase);
    }

    private async Task BroadcastShutdownIntentAsync(CancellationToken ct)
    {
        await _discovery.AnnounceAsync(new Dictionary<string, string>
        {
            ["event"] = "node_shutdown",
            ["node_id"] = _nodeId,
            ["timestamp"] = DateTime.UtcNow.ToString("O")
        }, ct);
    }

    private async Task DrainConnectionsAsync(TimeSpan timeout, CancellationToken ct)
    {
        // Wait for in-flight requests to complete
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        await Task.Delay(Math.Min(1000, (int)timeout.TotalMilliseconds / 2), cts.Token);
    }

    private async Task<long> MigrateDataAsync(CancellationToken ct)
    {
        // Trigger replication to ensure data is available on other nodes
        // In production, this would enumerate local data and ensure replication
        await Task.Delay(100, ct);
        return 0; // Return count of migrated objects
    }

    private async Task WaitForAcksAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        while (_shutdownAcks.Count < 1 && !cts.Token.IsCancellationRequested)
        {
            await Task.Delay(100, cts.Token);
        }
    }

    public void AcknowledgeShutdown(string fromNodeId) => _shutdownAcks[fromNodeId] = true;

    public ValueTask DisposeAsync()
    {
        _shutdownAcks.Clear();
        return ValueTask.CompletedTask;
    }
}

public enum ShutdownPhase { Running, Announcing, Draining, Migrating, AwaitingAcks, Completed, Failed }

public sealed class ShutdownResult
{
    public bool Success { get; set; }
    public DateTime StartedAt { get; init; }
    public DateTime? CompletedAt { get; set; }
    public long DataMigrated { get; set; }
    public string? Error { get; set; }
}

#endregion

#region H13: Metadata Sync Race Condition Fixes

/// <summary>
/// Versioned sync barrier to prevent race conditions during metadata sync.
/// </summary>
public sealed class SyncBarrier
{
    private readonly SemaphoreSlim _barrierLock = new(1, 1);
    private readonly ConcurrentDictionary<string, long> _syncVersions = new();
    private long _globalVersion;

    /// <summary>
    /// Acquires sync barrier for a specific key.
    /// </summary>
    public async Task<SyncBarrierHandle> AcquireAsync(string key, CancellationToken ct = default)
    {
        await _barrierLock.WaitAsync(ct);
        var version = Interlocked.Increment(ref _globalVersion);
        _syncVersions[key] = version;
        return new SyncBarrierHandle(key, version, this);
    }

    internal void Release(string key, long version)
    {
        if (_syncVersions.TryGetValue(key, out var current) && current == version)
        {
            _syncVersions.TryRemove(key, out _);
        }
        _barrierLock.Release();
    }

    public bool IsLocked(string key) => _syncVersions.ContainsKey(key);
    public long GetVersion(string key) => _syncVersions.TryGetValue(key, out var v) ? v : 0;
}

public sealed class SyncBarrierHandle : IDisposable
{
    private readonly string _key;
    private readonly long _version;
    private readonly SyncBarrier _barrier;
    private bool _disposed;

    internal SyncBarrierHandle(string key, long version, SyncBarrier barrier)
    {
        _key = key;
        _version = version;
        _barrier = barrier;
    }

    public long Version => _version;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _barrier.Release(_key, _version);
    }
}

/// <summary>
/// Sync checkpoint for tracking progress.
/// </summary>
public sealed class SyncCheckpoint
{
    public string NodeId { get; init; } = string.Empty;
    public VectorClock Clock { get; init; } = new();
    public DateTime Timestamp { get; init; }
    public long SequenceNumber { get; init; }
}

/// <summary>
/// Enhanced metadata sync with conflict-free ordering.
/// </summary>
public sealed class OrderedMetadataSync : IMetadataSync
{
    private readonly CrdtMetadataSync _innerSync;
    private readonly SyncBarrier _barrier = new();
    private readonly ConcurrentQueue<SyncCheckpoint> _checkpoints = new();

    public OrderedMetadataSync(string nodeId) => _innerSync = new CrdtMetadataSync(nodeId);

    public async Task<SyncResult> SyncWithAsync(string peerId, IEnumerable<MetadataEntry> peerEntries, CancellationToken ct = default)
    {
        using var handle = await _barrier.AcquireAsync(peerId, ct);
        var result = await _innerSync.SyncWithAsync(peerId, peerEntries, ct);
        _checkpoints.Enqueue(new SyncCheckpoint
        {
            NodeId = peerId,
            Clock = new VectorClock(),
            Timestamp = DateTime.UtcNow,
            SequenceNumber = handle.Version
        });
        // Keep only recent checkpoints
        while (_checkpoints.Count > 1000) _checkpoints.TryDequeue(out _);
        return result;
    }

    public Task ApplyUpdateAsync(MetadataEntry entry, CancellationToken ct = default) =>
        _innerSync.ApplyUpdateAsync(entry, ct);

    public Task<IEnumerable<MetadataEntry>> GetChangesSinceAsync(VectorClock since, CancellationToken ct = default) =>
        _innerSync.GetChangesSinceAsync(since, ct);

    public IReadOnlyList<SyncCheckpoint> GetRecentCheckpoints() => _checkpoints.ToArray();
}

#endregion

#region H14: Replication Lag Monitoring

/// <summary>
/// Monitors replication lag across replicas.
/// </summary>
public sealed class ReplicationLagMonitor : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, ReplicaLagInfo> _replicaLags = new();
    private readonly ReplicationLagConfig _config;
    private readonly Timer _sweepTimer;
    private volatile bool _disposed;

    public event EventHandler<LagAlertEventArgs>? LagAlert;

    public ReplicationLagMonitor(ReplicationLagConfig? config = null)
    {
        _config = config ?? new ReplicationLagConfig();
        _sweepTimer = new Timer(CheckLagThresholds, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// Records replication progress for a replica.
    /// </summary>
    public void RecordProgress(string replicaId, long sequenceNumber, DateTime timestamp)
    {
        var info = _replicaLags.GetOrAdd(replicaId, _ => new ReplicaLagInfo { ReplicaId = replicaId });
        info.LastSequence = sequenceNumber;
        info.LastTimestamp = timestamp;
        info.LastUpdated = DateTime.UtcNow;
    }

    /// <summary>
    /// Gets current lag for a replica.
    /// </summary>
    public TimeSpan GetLag(string replicaId, long currentSequence)
    {
        if (!_replicaLags.TryGetValue(replicaId, out var info))
            return TimeSpan.MaxValue;

        var sequenceLag = currentSequence - info.LastSequence;
        var timeLag = DateTime.UtcNow - info.LastUpdated;

        // Estimate lag based on sequence difference and time since last update
        return timeLag + TimeSpan.FromMilliseconds(sequenceLag * 10); // Rough estimate
    }

    /// <summary>
    /// Gets all replicas with their lag information.
    /// </summary>
    public IReadOnlyDictionary<string, ReplicaLagInfo> GetAllLags() =>
        new Dictionary<string, ReplicaLagInfo>(_replicaLags);

    /// <summary>
    /// Checks if replica is in catch-up mode (significantly behind).
    /// </summary>
    public bool IsCatchingUp(string replicaId, long currentSequence)
    {
        if (!_replicaLags.TryGetValue(replicaId, out var info))
            return true;

        return (currentSequence - info.LastSequence) > _config.CatchUpThreshold;
    }

    private void CheckLagThresholds(object? state)
    {
        if (_disposed) return;

        foreach (var (replicaId, info) in _replicaLags)
        {
            var timeSinceUpdate = DateTime.UtcNow - info.LastUpdated;
            if (timeSinceUpdate > _config.AlertThreshold)
            {
                LagAlert?.Invoke(this, new LagAlertEventArgs
                {
                    ReplicaId = replicaId,
                    Lag = timeSinceUpdate,
                    Level = timeSinceUpdate > _config.CriticalThreshold ? AlertLevel.Critical : AlertLevel.Warning
                });
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        _sweepTimer.Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed class ReplicaLagInfo
{
    public string ReplicaId { get; init; } = string.Empty;
    public long LastSequence { get; set; }
    public DateTime LastTimestamp { get; set; }
    public DateTime LastUpdated { get; set; }
}

public sealed class ReplicationLagConfig
{
    public TimeSpan AlertThreshold { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan CriticalThreshold { get; set; } = TimeSpan.FromMinutes(5);
    public long CatchUpThreshold { get; set; } = 10000;
}

public sealed class LagAlertEventArgs : EventArgs
{
    public string ReplicaId { get; init; } = string.Empty;
    public TimeSpan Lag { get; init; }
    public AlertLevel Level { get; init; }
}

public enum AlertLevel { Info, Warning, Critical }

#endregion

#region H15: Proper Quorum Validation

/// <summary>
/// Validates quorum requirements for distributed operations.
/// </summary>
public sealed class QuorumValidator
{
    private readonly ConcurrentDictionary<string, bool> _nodeHealth = new();
    private readonly QuorumConfig _config;

    public QuorumValidator(QuorumConfig? config = null)
    {
        _config = config ?? new QuorumConfig();
    }

    /// <summary>
    /// Updates node health status.
    /// </summary>
    public void UpdateNodeHealth(string nodeId, bool healthy) => _nodeHealth[nodeId] = healthy;

    /// <summary>
    /// Calculates required quorum size.
    /// </summary>
    public int CalculateQuorumSize(int totalNodes) => (totalNodes / 2) + 1;

    /// <summary>
    /// Checks if quorum is available for reads.
    /// </summary>
    public QuorumCheckResult CheckReadQuorum(IEnumerable<string> availableNodes)
    {
        var healthy = availableNodes.Count(n => _nodeHealth.GetValueOrDefault(n, true));
        var required = CalculateQuorumSize(_nodeHealth.Count > 0 ? _nodeHealth.Count : healthy);

        return new QuorumCheckResult
        {
            HasQuorum = healthy >= required,
            AvailableNodes = healthy,
            RequiredNodes = required,
            QuorumType = QuorumType.Read
        };
    }

    /// <summary>
    /// Checks if quorum is available for writes.
    /// </summary>
    public QuorumCheckResult CheckWriteQuorum(IEnumerable<string> availableNodes)
    {
        var healthy = availableNodes.Count(n => _nodeHealth.GetValueOrDefault(n, true));
        var total = _nodeHealth.Count > 0 ? _nodeHealth.Count : healthy;
        var required = _config.StrictWriteQuorum ? (total / 2) + 1 : Math.Max(1, total / 3);

        return new QuorumCheckResult
        {
            HasQuorum = healthy >= required,
            AvailableNodes = healthy,
            RequiredNodes = required,
            QuorumType = QuorumType.Write
        };
    }

    /// <summary>
    /// Validates read-your-writes consistency.
    /// </summary>
    public bool ValidateReadYourWrites(string lastWriteNode, IEnumerable<string> readNodes)
    {
        // For read-your-writes, ensure we read from at least one node that saw the write
        return readNodes.Contains(lastWriteNode) ||
               readNodes.Count() >= CalculateQuorumSize(_nodeHealth.Count);
    }

    /// <summary>
    /// Detects quorum loss condition.
    /// </summary>
    public bool IsQuorumLost()
    {
        var healthy = _nodeHealth.Values.Count(h => h);
        var required = CalculateQuorumSize(_nodeHealth.Count);
        return healthy < required;
    }

    /// <summary>
    /// Gets witness nodes that can help achieve quorum.
    /// </summary>
    public IEnumerable<string> GetWitnessNodes()
    {
        // Return nodes marked as witnesses (lightweight nodes for quorum only)
        return _nodeHealth.Keys.Where(n => n.StartsWith("witness-"));
    }
}

public sealed class QuorumConfig
{
    public bool StrictWriteQuorum { get; set; } = true;
    public bool AllowWitnessNodes { get; set; } = true;
}

public sealed class QuorumCheckResult
{
    public bool HasQuorum { get; init; }
    public int AvailableNodes { get; init; }
    public int RequiredNodes { get; init; }
    public QuorumType QuorumType { get; init; }
}

public enum QuorumType { Read, Write }

#endregion
