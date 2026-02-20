using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts.Replication;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateReplication.Strategies.Topology
{
    #region Topology Node Types

    /// <summary>
    /// Represents a node in a replication topology.
    /// </summary>
    public sealed class TopologyNode
    {
        /// <summary>Node identifier.</summary>
        public required string NodeId { get; init; }

        /// <summary>Display name.</summary>
        public required string Name { get; init; }

        /// <summary>Node endpoint.</summary>
        public required string Endpoint { get; init; }

        /// <summary>Node role in topology.</summary>
        public TopologyRole Role { get; set; } = TopologyRole.Replica;

        /// <summary>Parent node (for hierarchical topologies).</summary>
        public string? ParentNodeId { get; set; }

        /// <summary>Child nodes (for hierarchical topologies).</summary>
        public List<string> ChildNodeIds { get; } = new();

        /// <summary>Neighbor nodes (for mesh/ring topologies).</summary>
        public List<string> NeighborNodeIds { get; } = new();

        /// <summary>Current health status.</summary>
        public TopologyNodeHealth Health { get; set; } = TopologyNodeHealth.Healthy;

        /// <summary>Estimated latency to this node.</summary>
        public int LatencyMs { get; set; } = 20;

        /// <summary>Node capacity weight (for load distribution).</summary>
        public double CapacityWeight { get; set; } = 1.0;
    }

    /// <summary>
    /// Node role in the topology.
    /// </summary>
    public enum TopologyRole
    {
        Hub,
        Spoke,
        Primary,
        Replica,
        Relay,
        Leaf
    }

    /// <summary>
    /// Node health status.
    /// </summary>
    public enum TopologyNodeHealth
    {
        Healthy,
        Degraded,
        Unhealthy,
        Offline
    }

    #endregion

    /// <summary>
    /// Star topology replication strategy with central hub and spoke nodes.
    /// All replication flows through the hub.
    /// </summary>
    public sealed class StarTopologyStrategy : EnhancedReplicationStrategyBase
    {
        private readonly BoundedDictionary<string, TopologyNode> _nodes = new BoundedDictionary<string, TopologyNode>(1000);
        private readonly BoundedDictionary<string, (byte[] Data, DateTimeOffset Timestamp)> _dataStore = new BoundedDictionary<string, (byte[] Data, DateTimeOffset Timestamp)>(1000);
        private string? _hubNodeId;

        /// <inheritdoc/>
        public override ReplicationCharacteristics Characteristics { get; } = new()
        {
            StrategyName = "StarTopology",
            Description = "Star topology replication with central hub coordinating all spoke nodes",
            ConsistencyModel = ConsistencyModel.SessionConsistent,
            Capabilities = new ReplicationCapabilities(
                SupportsMultiMaster: false,
                ConflictResolutionMethods: new[] { ConflictResolutionMethod.FirstWriteWins },
                SupportsAsyncReplication: true,
                SupportsSyncReplication: true,
                IsGeoAware: false,
                MaxReplicationLag: TimeSpan.FromSeconds(30),
                MinReplicaCount: 2,
                MaxReplicaCount: 50),
            SupportsAutoConflictResolution = true,
            SupportsVectorClocks = true,
            SupportsDeltaSync = false,
            SupportsStreaming = true,
            TypicalLagMs = 40,
            ConsistencySlaMs = 30000
        };

        /// <inheritdoc/>
        public override ReplicationCapabilities Capabilities => Characteristics.Capabilities;

        /// <inheritdoc/>
        public override ConsistencyModel ConsistencyModel => Characteristics.ConsistencyModel;

        /// <summary>
        /// Sets the hub node.
        /// </summary>
        public void SetHub(TopologyNode node)
        {
            node.Role = TopologyRole.Hub;
            _nodes[node.NodeId] = node;
            _hubNodeId = node.NodeId;
        }

        /// <summary>
        /// Adds a spoke node.
        /// </summary>
        public void AddSpoke(TopologyNode node)
        {
            if (_hubNodeId == null)
                throw new InvalidOperationException("Hub must be set before adding spokes");

            node.Role = TopologyRole.Spoke;
            node.ParentNodeId = _hubNodeId;
            _nodes[node.NodeId] = node;

            if (_nodes.TryGetValue(_hubNodeId, out var hub))
            {
                hub.ChildNodeIds.Add(node.NodeId);
            }
        }

        /// <summary>
        /// Gets the hub node.
        /// </summary>
        public TopologyNode? GetHub()
        {
            return _hubNodeId != null && _nodes.TryGetValue(_hubNodeId, out var hub) ? hub : null;
        }

        /// <summary>
        /// Gets all spoke nodes.
        /// </summary>
        public IEnumerable<TopologyNode> GetSpokes()
        {
            return _nodes.Values.Where(n => n.Role == TopologyRole.Spoke);
        }

        /// <inheritdoc/>
        public override async Task ReplicateAsync(
            string sourceNodeId,
            IEnumerable<string> targetNodeIds,
            ReadOnlyMemory<byte> data,
            IDictionary<string, string>? metadata = null,
            CancellationToken cancellationToken = default)
        {
            IncrementLocalClock();
            var key = metadata?.GetValueOrDefault("key") ?? Guid.NewGuid().ToString();

            if (_hubNodeId == null)
                throw new InvalidOperationException("Star topology has no hub configured");

            _dataStore[key] = (data.ToArray(), DateTimeOffset.UtcNow);

            // All replication goes through hub
            var startTime = DateTime.UtcNow;

            // First replicate to hub
            if (sourceNodeId != _hubNodeId)
            {
                await Task.Delay(20, cancellationToken);
            }

            // Then from hub to all spokes
            var spokeTasks = targetNodeIds
                .Where(id => id != _hubNodeId && _nodes.TryGetValue(id, out var n) && n.Health == TopologyNodeHealth.Healthy)
                .Select(async spokeId =>
                {
                    var latency = _nodes.TryGetValue(spokeId, out var spoke) ? spoke.LatencyMs : 20;
                    await Task.Delay(latency, cancellationToken);
                    RecordReplicationLag(spokeId, DateTime.UtcNow - startTime);
                });

            await Task.WhenAll(spokeTasks);
        }

        /// <inheritdoc/>
        public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(
            ReplicationConflict conflict,
            CancellationToken cancellationToken = default)
        {
            // Star topology: hub always wins
            var mergedClock = conflict.LocalVersion.Clone();
            mergedClock.Merge(conflict.RemoteVersion);

            var hubIsLocal = conflict.LocalNodeId == _hubNodeId;
            var winner = hubIsLocal ? conflict.LocalData : conflict.RemoteData;

            return Task.FromResult((winner, mergedClock));
        }

        /// <inheritdoc/>
        public override Task<bool> VerifyConsistencyAsync(
            IEnumerable<string> nodeIds,
            string dataId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_dataStore.ContainsKey(dataId));
        }

        /// <inheritdoc/>
        public override Task<TimeSpan> GetReplicationLagAsync(
            string sourceNodeId,
            string targetNodeId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(LagTracker.GetCurrentLag(targetNodeId));
        }
    }

    /// <summary>
    /// Mesh topology replication strategy where all nodes are interconnected
    /// for maximum redundancy and parallel replication.
    /// </summary>
    public sealed class MeshTopologyStrategy : EnhancedReplicationStrategyBase
    {
        private readonly BoundedDictionary<string, TopologyNode> _nodes = new BoundedDictionary<string, TopologyNode>(1000);
        private readonly BoundedDictionary<string, (byte[] Data, HashSet<string> ReplicatedTo)> _dataStore = new BoundedDictionary<string, (byte[] Data, HashSet<string> ReplicatedTo)>(1000);
        private int _minConnections = 2;

        /// <inheritdoc/>
        public override ReplicationCharacteristics Characteristics { get; } = new()
        {
            StrategyName = "MeshTopology",
            Description = "Full mesh topology with all nodes interconnected for maximum redundancy",
            ConsistencyModel = ConsistencyModel.Eventual,
            Capabilities = new ReplicationCapabilities(
                SupportsMultiMaster: true,
                ConflictResolutionMethods: new[] { ConflictResolutionMethod.LastWriteWins, ConflictResolutionMethod.Crdt },
                SupportsAsyncReplication: true,
                SupportsSyncReplication: false,
                IsGeoAware: false,
                MaxReplicationLag: TimeSpan.FromSeconds(30),
                MinReplicaCount: 3,
                MaxReplicaCount: 20),
            SupportsAutoConflictResolution = true,
            SupportsVectorClocks = true,
            SupportsDeltaSync = true,
            SupportsStreaming = true,
            TypicalLagMs = 30,
            ConsistencySlaMs = 30000
        };

        /// <inheritdoc/>
        public override ReplicationCapabilities Capabilities => Characteristics.Capabilities;

        /// <inheritdoc/>
        public override ConsistencyModel ConsistencyModel => Characteristics.ConsistencyModel;

        /// <summary>
        /// Sets minimum connections per node.
        /// </summary>
        public void SetMinConnections(int minConnections)
        {
            _minConnections = minConnections;
        }

        /// <summary>
        /// Adds a node to the mesh.
        /// </summary>
        public void AddNode(TopologyNode node)
        {
            node.Role = TopologyRole.Replica;
            _nodes[node.NodeId] = node;

            // Connect to all existing nodes (full mesh)
            foreach (var existing in _nodes.Values.Where(n => n.NodeId != node.NodeId))
            {
                node.NeighborNodeIds.Add(existing.NodeId);
                existing.NeighborNodeIds.Add(node.NodeId);
            }
        }

        /// <summary>
        /// Gets connected neighbors for a node.
        /// </summary>
        public IEnumerable<TopologyNode> GetNeighbors(string nodeId)
        {
            if (_nodes.TryGetValue(nodeId, out var node))
            {
                return node.NeighborNodeIds
                    .Select(id => _nodes.GetValueOrDefault(id))
                    .Where(n => n != null)!;
            }
            return Array.Empty<TopologyNode>();
        }

        /// <summary>
        /// Verifies mesh connectivity.
        /// </summary>
        public bool VerifyMeshConnectivity()
        {
            return _nodes.Values.All(n => n.NeighborNodeIds.Count >= _minConnections);
        }

        /// <inheritdoc/>
        public override async Task ReplicateAsync(
            string sourceNodeId,
            IEnumerable<string> targetNodeIds,
            ReadOnlyMemory<byte> data,
            IDictionary<string, string>? metadata = null,
            CancellationToken cancellationToken = default)
        {
            IncrementLocalClock();
            var key = metadata?.GetValueOrDefault("key") ?? Guid.NewGuid().ToString();

            var replicatedTo = new HashSet<string> { sourceNodeId };
            _dataStore[key] = (data.ToArray(), replicatedTo);

            // Parallel replication to all mesh nodes
            var tasks = targetNodeIds
                .Where(id => _nodes.TryGetValue(id, out var n) && n.Health == TopologyNodeHealth.Healthy)
                .Select(async targetId =>
                {
                    var startTime = DateTime.UtcNow;
                    var latency = _nodes.TryGetValue(targetId, out var node) ? node.LatencyMs : 20;
                    await Task.Delay(latency, cancellationToken);

                    lock (replicatedTo) replicatedTo.Add(targetId);
                    RecordReplicationLag(targetId, DateTime.UtcNow - startTime);
                });

            await Task.WhenAll(tasks);
        }

        /// <inheritdoc/>
        public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(
            ReplicationConflict conflict,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(ResolveLastWriteWins(conflict));
        }

        /// <inheritdoc/>
        public override Task<bool> VerifyConsistencyAsync(
            IEnumerable<string> nodeIds,
            string dataId,
            CancellationToken cancellationToken = default)
        {
            if (_dataStore.TryGetValue(dataId, out var item))
            {
                var targetCount = nodeIds.Count();
                var replicatedCount = nodeIds.Count(id => item.ReplicatedTo.Contains(id));
                return Task.FromResult(replicatedCount >= targetCount * 0.5); // At least 50% replicated
            }
            return Task.FromResult(false);
        }

        /// <inheritdoc/>
        public override Task<TimeSpan> GetReplicationLagAsync(
            string sourceNodeId,
            string targetNodeId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(LagTracker.GetCurrentLag(targetNodeId));
        }
    }

    /// <summary>
    /// Chain topology replication strategy where data flows sequentially
    /// from node to node, useful for geographic distribution.
    /// </summary>
    public sealed class ChainTopologyStrategy : EnhancedReplicationStrategyBase
    {
        private readonly List<TopologyNode> _chain = new();
        private readonly BoundedDictionary<string, (byte[] Data, int ChainPosition)> _dataStore = new BoundedDictionary<string, (byte[] Data, int ChainPosition)>(1000);

        /// <inheritdoc/>
        public override ReplicationCharacteristics Characteristics { get; } = new()
        {
            StrategyName = "ChainTopology",
            Description = "Chain topology with sequential replication along the chain for geographic distribution",
            ConsistencyModel = ConsistencyModel.Eventual,
            Capabilities = new ReplicationCapabilities(
                SupportsMultiMaster: false,
                ConflictResolutionMethods: new[] { ConflictResolutionMethod.LastWriteWins },
                SupportsAsyncReplication: true,
                SupportsSyncReplication: false,
                IsGeoAware: true,
                MaxReplicationLag: TimeSpan.FromMinutes(1),
                MinReplicaCount: 2,
                MaxReplicaCount: 10),
            SupportsAutoConflictResolution = true,
            SupportsVectorClocks = true,
            SupportsDeltaSync = true,
            SupportsStreaming = false,
            TypicalLagMs = 100,
            ConsistencySlaMs = 60000
        };

        /// <inheritdoc/>
        public override ReplicationCapabilities Capabilities => Characteristics.Capabilities;

        /// <inheritdoc/>
        public override ConsistencyModel ConsistencyModel => Characteristics.ConsistencyModel;

        /// <summary>
        /// Adds a node to the end of the chain.
        /// </summary>
        public void AddToChain(TopologyNode node)
        {
            node.Role = _chain.Count == 0 ? TopologyRole.Primary : TopologyRole.Replica;

            if (_chain.Count > 0)
            {
                var lastNode = _chain[^1];
                lastNode.ChildNodeIds.Add(node.NodeId);
                node.ParentNodeId = lastNode.NodeId;
            }

            _chain.Add(node);
        }

        /// <summary>
        /// Gets the chain head (primary).
        /// </summary>
        public TopologyNode? GetChainHead()
        {
            return _chain.FirstOrDefault();
        }

        /// <summary>
        /// Gets the chain tail.
        /// </summary>
        public TopologyNode? GetChainTail()
        {
            return _chain.LastOrDefault();
        }

        /// <summary>
        /// Gets position in chain.
        /// </summary>
        public int GetChainPosition(string nodeId)
        {
            return _chain.FindIndex(n => n.NodeId == nodeId);
        }

        /// <inheritdoc/>
        public override async Task ReplicateAsync(
            string sourceNodeId,
            IEnumerable<string> targetNodeIds,
            ReadOnlyMemory<byte> data,
            IDictionary<string, string>? metadata = null,
            CancellationToken cancellationToken = default)
        {
            IncrementLocalClock();
            var key = metadata?.GetValueOrDefault("key") ?? Guid.NewGuid().ToString();

            var sourcePosition = GetChainPosition(sourceNodeId);
            _dataStore[key] = (data.ToArray(), sourcePosition);

            // Sequential replication along the chain
            var startTime = DateTime.UtcNow;
            var cumulativeLag = TimeSpan.Zero;

            for (int i = sourcePosition + 1; i < _chain.Count; i++)
            {
                var node = _chain[i];
                if (node.Health != TopologyNodeHealth.Healthy) continue;
                if (!targetNodeIds.Contains(node.NodeId)) continue;

                await Task.Delay(node.LatencyMs, cancellationToken);
                cumulativeLag += TimeSpan.FromMilliseconds(node.LatencyMs);
                RecordReplicationLag(node.NodeId, cumulativeLag);
            }
        }

        /// <inheritdoc/>
        public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(
            ReplicationConflict conflict,
            CancellationToken cancellationToken = default)
        {
            // Chain: earlier position wins
            var localPos = GetChainPosition(conflict.LocalNodeId);
            var remotePos = GetChainPosition(conflict.RemoteNodeId);

            var mergedClock = conflict.LocalVersion.Clone();
            mergedClock.Merge(conflict.RemoteVersion);

            var winner = localPos <= remotePos ? conflict.LocalData : conflict.RemoteData;
            return Task.FromResult((winner, mergedClock));
        }

        /// <inheritdoc/>
        public override Task<bool> VerifyConsistencyAsync(
            IEnumerable<string> nodeIds,
            string dataId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_dataStore.ContainsKey(dataId));
        }

        /// <inheritdoc/>
        public override Task<TimeSpan> GetReplicationLagAsync(
            string sourceNodeId,
            string targetNodeId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(LagTracker.GetCurrentLag(targetNodeId));
        }
    }

    /// <summary>
    /// Tree topology replication strategy with hierarchical parent-child relationships
    /// for scalable fan-out replication.
    /// </summary>
    public sealed class TreeTopologyStrategy : EnhancedReplicationStrategyBase
    {
        private readonly BoundedDictionary<string, TopologyNode> _nodes = new BoundedDictionary<string, TopologyNode>(1000);
        private readonly BoundedDictionary<string, (byte[] Data, int Depth)> _dataStore = new BoundedDictionary<string, (byte[] Data, int Depth)>(1000);
        private string? _rootNodeId;
        private int _maxFanOut = 5;

        /// <inheritdoc/>
        public override ReplicationCharacteristics Characteristics { get; } = new()
        {
            StrategyName = "TreeTopology",
            Description = "Tree topology with hierarchical parent-child relationships for scalable fan-out",
            ConsistencyModel = ConsistencyModel.Eventual,
            Capabilities = new ReplicationCapabilities(
                SupportsMultiMaster: false,
                ConflictResolutionMethods: new[] { ConflictResolutionMethod.PriorityBased },
                SupportsAsyncReplication: true,
                SupportsSyncReplication: false,
                IsGeoAware: true,
                MaxReplicationLag: TimeSpan.FromSeconds(60),
                MinReplicaCount: 2,
                MaxReplicaCount: 100),
            SupportsAutoConflictResolution = true,
            SupportsVectorClocks = true,
            SupportsDeltaSync = true,
            SupportsStreaming = false,
            TypicalLagMs = 80,
            ConsistencySlaMs = 60000
        };

        /// <inheritdoc/>
        public override ReplicationCapabilities Capabilities => Characteristics.Capabilities;

        /// <inheritdoc/>
        public override ConsistencyModel ConsistencyModel => Characteristics.ConsistencyModel;

        /// <summary>
        /// Sets the root node.
        /// </summary>
        public void SetRoot(TopologyNode node)
        {
            node.Role = TopologyRole.Primary;
            _nodes[node.NodeId] = node;
            _rootNodeId = node.NodeId;
        }

        /// <summary>
        /// Adds a child node to a parent.
        /// </summary>
        public void AddChild(string parentNodeId, TopologyNode child)
        {
            if (!_nodes.ContainsKey(parentNodeId))
                throw new ArgumentException($"Parent node {parentNodeId} not found");

            var parent = _nodes[parentNodeId];
            if (parent.ChildNodeIds.Count >= _maxFanOut)
                throw new InvalidOperationException($"Parent node {parentNodeId} has reached max fan-out of {_maxFanOut}");

            child.Role = TopologyRole.Replica;
            child.ParentNodeId = parentNodeId;
            parent.ChildNodeIds.Add(child.NodeId);
            _nodes[child.NodeId] = child;
        }

        /// <summary>
        /// Sets maximum fan-out per node.
        /// </summary>
        public void SetMaxFanOut(int maxFanOut)
        {
            _maxFanOut = maxFanOut;
        }

        /// <summary>
        /// Gets the depth of a node in the tree.
        /// </summary>
        public int GetDepth(string nodeId)
        {
            int depth = 0;
            var currentId = nodeId;

            while (currentId != null && _nodes.TryGetValue(currentId, out var node) && node.ParentNodeId != null)
            {
                depth++;
                currentId = node.ParentNodeId;
            }

            return depth;
        }

        /// <summary>
        /// Gets all descendants of a node.
        /// </summary>
        public IEnumerable<string> GetDescendants(string nodeId)
        {
            if (!_nodes.TryGetValue(nodeId, out var node))
                yield break;

            foreach (var childId in node.ChildNodeIds)
            {
                yield return childId;
                foreach (var descendant in GetDescendants(childId))
                {
                    yield return descendant;
                }
            }
        }

        /// <inheritdoc/>
        public override async Task ReplicateAsync(
            string sourceNodeId,
            IEnumerable<string> targetNodeIds,
            ReadOnlyMemory<byte> data,
            IDictionary<string, string>? metadata = null,
            CancellationToken cancellationToken = default)
        {
            IncrementLocalClock();
            var key = metadata?.GetValueOrDefault("key") ?? Guid.NewGuid().ToString();
            var sourceDepth = GetDepth(sourceNodeId);

            _dataStore[key] = (data.ToArray(), sourceDepth);

            // Replicate down the tree level by level
            await ReplicateDownTree(sourceNodeId, targetNodeIds.ToHashSet(), data, DateTime.UtcNow, cancellationToken);
        }

        private async Task ReplicateDownTree(string nodeId, HashSet<string> targets, ReadOnlyMemory<byte> data,
            DateTime startTime, CancellationToken ct)
        {
            if (!_nodes.TryGetValue(nodeId, out var node))
                return;

            var childTasks = node.ChildNodeIds
                .Where(childId => _nodes.TryGetValue(childId, out var c) && c.Health == TopologyNodeHealth.Healthy)
                .Select(async childId =>
                {
                    var child = _nodes[childId];
                    await Task.Delay(child.LatencyMs, ct);

                    if (targets.Contains(childId))
                    {
                        RecordReplicationLag(childId, DateTime.UtcNow - startTime);
                    }

                    // Continue down the tree
                    await ReplicateDownTree(childId, targets, data, startTime, ct);
                });

            await Task.WhenAll(childTasks);
        }

        /// <inheritdoc/>
        public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(
            ReplicationConflict conflict,
            CancellationToken cancellationToken = default)
        {
            // Tree: shallower node (closer to root) wins
            var localDepth = GetDepth(conflict.LocalNodeId);
            var remoteDepth = GetDepth(conflict.RemoteNodeId);

            var mergedClock = conflict.LocalVersion.Clone();
            mergedClock.Merge(conflict.RemoteVersion);

            var winner = localDepth <= remoteDepth ? conflict.LocalData : conflict.RemoteData;
            return Task.FromResult((winner, mergedClock));
        }

        /// <inheritdoc/>
        public override Task<bool> VerifyConsistencyAsync(
            IEnumerable<string> nodeIds,
            string dataId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_dataStore.ContainsKey(dataId));
        }

        /// <inheritdoc/>
        public override Task<TimeSpan> GetReplicationLagAsync(
            string sourceNodeId,
            string targetNodeId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(LagTracker.GetCurrentLag(targetNodeId));
        }
    }

    /// <summary>
    /// Ring topology replication strategy where data flows around the ring
    /// with each node forwarding to the next.
    /// </summary>
    public sealed class RingTopologyStrategy : EnhancedReplicationStrategyBase
    {
        private readonly List<TopologyNode> _ring = new();
        private readonly BoundedDictionary<string, (byte[] Data, int RingPosition)> _dataStore = new BoundedDictionary<string, (byte[] Data, int RingPosition)>(1000);
        private bool _bidirectional = false;

        /// <inheritdoc/>
        public override ReplicationCharacteristics Characteristics { get; } = new()
        {
            StrategyName = "RingTopology",
            Description = "Ring topology with circular replication flow and optional bidirectional propagation",
            ConsistencyModel = ConsistencyModel.Eventual,
            Capabilities = new ReplicationCapabilities(
                SupportsMultiMaster: true,
                ConflictResolutionMethods: new[] { ConflictResolutionMethod.LastWriteWins },
                SupportsAsyncReplication: true,
                SupportsSyncReplication: false,
                IsGeoAware: false,
                MaxReplicationLag: TimeSpan.FromSeconds(60),
                MinReplicaCount: 3,
                MaxReplicaCount: 20),
            SupportsAutoConflictResolution = true,
            SupportsVectorClocks = true,
            SupportsDeltaSync = false,
            SupportsStreaming = true,
            TypicalLagMs = 70,
            ConsistencySlaMs = 60000
        };

        /// <inheritdoc/>
        public override ReplicationCapabilities Capabilities => Characteristics.Capabilities;

        /// <inheritdoc/>
        public override ConsistencyModel ConsistencyModel => Characteristics.ConsistencyModel;

        /// <summary>
        /// Adds a node to the ring.
        /// </summary>
        public void AddToRing(TopologyNode node)
        {
            node.Role = TopologyRole.Replica;

            if (_ring.Count > 0)
            {
                var lastNode = _ring[^1];
                lastNode.NeighborNodeIds.Add(node.NodeId);
                node.NeighborNodeIds.Add(lastNode.NodeId);

                // Close the ring
                var firstNode = _ring[0];
                firstNode.NeighborNodeIds.Remove(_ring[^1].NodeId);
                firstNode.NeighborNodeIds.Add(node.NodeId);
                node.NeighborNodeIds.Add(firstNode.NodeId);
            }

            _ring.Add(node);
        }

        /// <summary>
        /// Enables bidirectional replication.
        /// </summary>
        public void SetBidirectional(bool enabled)
        {
            _bidirectional = enabled;
        }

        /// <summary>
        /// Gets the next node in the ring.
        /// </summary>
        public TopologyNode? GetNextNode(string nodeId)
        {
            var index = _ring.FindIndex(n => n.NodeId == nodeId);
            if (index < 0) return null;
            var nextIndex = (index + 1) % _ring.Count;
            return _ring[nextIndex];
        }

        /// <summary>
        /// Gets the previous node in the ring.
        /// </summary>
        public TopologyNode? GetPreviousNode(string nodeId)
        {
            var index = _ring.FindIndex(n => n.NodeId == nodeId);
            if (index < 0) return null;
            var prevIndex = (index - 1 + _ring.Count) % _ring.Count;
            return _ring[prevIndex];
        }

        /// <inheritdoc/>
        public override async Task ReplicateAsync(
            string sourceNodeId,
            IEnumerable<string> targetNodeIds,
            ReadOnlyMemory<byte> data,
            IDictionary<string, string>? metadata = null,
            CancellationToken cancellationToken = default)
        {
            IncrementLocalClock();
            var key = metadata?.GetValueOrDefault("key") ?? Guid.NewGuid().ToString();

            var sourceIndex = _ring.FindIndex(n => n.NodeId == sourceNodeId);
            _dataStore[key] = (data.ToArray(), sourceIndex);

            var targetSet = targetNodeIds.ToHashSet();
            var startTime = DateTime.UtcNow;

            // Propagate around the ring (clockwise)
            await PropagateRing(sourceIndex, 1, targetSet, startTime, cancellationToken);

            // Optionally propagate counter-clockwise
            if (_bidirectional)
            {
                await PropagateRing(sourceIndex, -1, targetSet, startTime, cancellationToken);
            }
        }

        private async Task PropagateRing(int startIndex, int direction, HashSet<string> targets,
            DateTime startTime, CancellationToken ct)
        {
            var visited = new HashSet<int> { startIndex };

            var currentIndex = (startIndex + direction + _ring.Count) % _ring.Count;

            while (!visited.Contains(currentIndex))
            {
                var node = _ring[currentIndex];
                visited.Add(currentIndex);

                if (node.Health == TopologyNodeHealth.Healthy)
                {
                    await Task.Delay(node.LatencyMs, ct);

                    if (targets.Contains(node.NodeId))
                    {
                        RecordReplicationLag(node.NodeId, DateTime.UtcNow - startTime);
                    }
                }

                currentIndex = (currentIndex + direction + _ring.Count) % _ring.Count;
            }
        }

        /// <inheritdoc/>
        public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(
            ReplicationConflict conflict,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(ResolveLastWriteWins(conflict));
        }

        /// <inheritdoc/>
        public override Task<bool> VerifyConsistencyAsync(
            IEnumerable<string> nodeIds,
            string dataId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_dataStore.ContainsKey(dataId));
        }

        /// <inheritdoc/>
        public override Task<TimeSpan> GetReplicationLagAsync(
            string sourceNodeId,
            string targetNodeId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(LagTracker.GetCurrentLag(targetNodeId));
        }
    }

    /// <summary>
    /// Hierarchical topology replication strategy combining multiple levels
    /// with different replication strategies at each level.
    /// </summary>
    public sealed class HierarchicalTopologyStrategy : EnhancedReplicationStrategyBase
    {
        private readonly BoundedDictionary<string, TopologyNode> _nodes = new BoundedDictionary<string, TopologyNode>(1000);
        private readonly BoundedDictionary<int, List<string>> _levels = new BoundedDictionary<int, List<string>>(1000);
        private readonly BoundedDictionary<string, (byte[] Data, int Level)> _dataStore = new BoundedDictionary<string, (byte[] Data, int Level)>(1000);
        private int _maxLevels = 4;

        /// <inheritdoc/>
        public override ReplicationCharacteristics Characteristics { get; } = new()
        {
            StrategyName = "HierarchicalTopology",
            Description = "Hierarchical topology with multiple levels for enterprise-scale geographic distribution",
            ConsistencyModel = ConsistencyModel.Eventual,
            Capabilities = new ReplicationCapabilities(
                SupportsMultiMaster: false,
                ConflictResolutionMethods: new[] { ConflictResolutionMethod.PriorityBased, ConflictResolutionMethod.LastWriteWins },
                SupportsAsyncReplication: true,
                SupportsSyncReplication: true,
                IsGeoAware: true,
                MaxReplicationLag: TimeSpan.FromMinutes(5),
                MinReplicaCount: 2,
                MaxReplicaCount: 500),
            SupportsAutoConflictResolution = true,
            SupportsVectorClocks = true,
            SupportsDeltaSync = true,
            SupportsStreaming = false,
            TypicalLagMs = 200,
            ConsistencySlaMs = 300000
        };

        /// <inheritdoc/>
        public override ReplicationCapabilities Capabilities => Characteristics.Capabilities;

        /// <inheritdoc/>
        public override ConsistencyModel ConsistencyModel => Characteristics.ConsistencyModel;

        /// <summary>
        /// Sets maximum hierarchy levels.
        /// </summary>
        public void SetMaxLevels(int maxLevels)
        {
            _maxLevels = maxLevels;
        }

        /// <summary>
        /// Adds a node at a specific level.
        /// </summary>
        public void AddNodeAtLevel(TopologyNode node, int level, string? parentNodeId = null)
        {
            if (level >= _maxLevels)
                throw new ArgumentException($"Level {level} exceeds max levels {_maxLevels}");

            node.Role = level == 0 ? TopologyRole.Primary : TopologyRole.Replica;

            if (parentNodeId != null && _nodes.TryGetValue(parentNodeId, out var parent))
            {
                node.ParentNodeId = parentNodeId;
                parent.ChildNodeIds.Add(node.NodeId);
            }

            _nodes[node.NodeId] = node;

            var levelNodes = _levels.GetOrAdd(level, _ => new List<string>());
            lock (levelNodes) levelNodes.Add(node.NodeId);
        }

        /// <summary>
        /// Gets all nodes at a level.
        /// </summary>
        public IEnumerable<TopologyNode> GetNodesAtLevel(int level)
        {
            if (_levels.TryGetValue(level, out var nodeIds))
            {
                return nodeIds.Select(id => _nodes.GetValueOrDefault(id)).Where(n => n != null)!;
            }
            return Array.Empty<TopologyNode>();
        }

        /// <summary>
        /// Gets the level of a node.
        /// </summary>
        public int GetNodeLevel(string nodeId)
        {
            foreach (var (level, nodes) in _levels)
            {
                if (nodes.Contains(nodeId))
                    return level;
            }
            return -1;
        }

        /// <inheritdoc/>
        public override async Task ReplicateAsync(
            string sourceNodeId,
            IEnumerable<string> targetNodeIds,
            ReadOnlyMemory<byte> data,
            IDictionary<string, string>? metadata = null,
            CancellationToken cancellationToken = default)
        {
            IncrementLocalClock();
            var key = metadata?.GetValueOrDefault("key") ?? Guid.NewGuid().ToString();

            var sourceLevel = GetNodeLevel(sourceNodeId);
            _dataStore[key] = (data.ToArray(), sourceLevel);

            var targetSet = targetNodeIds.ToHashSet();
            var startTime = DateTime.UtcNow;

            // Replicate level by level
            for (int level = sourceLevel + 1; level < _maxLevels; level++)
            {
                var levelTasks = GetNodesAtLevel(level)
                    .Where(n => targetSet.Contains(n.NodeId) && n.Health == TopologyNodeHealth.Healthy)
                    .Select(async node =>
                    {
                        // Add level-based latency
                        var latency = node.LatencyMs + (level * 20);
                        await Task.Delay(latency, cancellationToken);
                        RecordReplicationLag(node.NodeId, DateTime.UtcNow - startTime);
                    });

                await Task.WhenAll(levelTasks);
            }
        }

        /// <inheritdoc/>
        public override Task<(ReadOnlyMemory<byte> ResolvedData, VectorClock ResolvedVersion)> ResolveConflictAsync(
            ReplicationConflict conflict,
            CancellationToken cancellationToken = default)
        {
            // Higher level (lower number) wins
            var localLevel = GetNodeLevel(conflict.LocalNodeId);
            var remoteLevel = GetNodeLevel(conflict.RemoteNodeId);

            var mergedClock = conflict.LocalVersion.Clone();
            mergedClock.Merge(conflict.RemoteVersion);

            var winner = localLevel <= remoteLevel ? conflict.LocalData : conflict.RemoteData;
            return Task.FromResult((winner, mergedClock));
        }

        /// <inheritdoc/>
        public override Task<bool> VerifyConsistencyAsync(
            IEnumerable<string> nodeIds,
            string dataId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_dataStore.ContainsKey(dataId));
        }

        /// <inheritdoc/>
        public override Task<TimeSpan> GetReplicationLagAsync(
            string sourceNodeId,
            string targetNodeId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(LagTracker.GetCurrentLag(targetNodeId));
        }
    }
}
