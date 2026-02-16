using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Federation.Topology;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace DataWarehouse.SDK.Federation.Orchestration;

/// <summary>
/// Represents the cluster-wide topology state with thread-safe operations and serialization.
/// </summary>
/// <remarks>
/// <para>
/// ClusterTopology maintains the set of all nodes in the federation along with their
/// topology metadata, health scores, and capacity. It provides thread-safe read/write
/// operations and serialization for Raft-backed persistence.
/// </para>
/// <para>
/// <strong>Concurrency:</strong> Uses a combination of ConcurrentDictionary for lock-free
/// reads and ReaderWriterLockSlim for bulk operations (GetAllNodes, Serialize). This
/// provides high read throughput while maintaining consistency during topology updates.
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 34: Cluster-wide topology state")]
public sealed class ClusterTopology
{
    private readonly ConcurrentDictionary<string, NodeTopology> _nodes;
    private readonly ReaderWriterLockSlim _lock;

    /// <summary>
    /// Initializes a new instance of the <see cref="ClusterTopology"/> class.
    /// </summary>
    public ClusterTopology()
    {
        _nodes = new ConcurrentDictionary<string, NodeTopology>();
        _lock = new ReaderWriterLockSlim();
    }

    /// <summary>
    /// Adds or updates a node in the topology.
    /// </summary>
    /// <param name="node">The node topology to add or update.</param>
    public void AddOrUpdateNode(NodeTopology node)
    {
        _lock.EnterWriteLock();
        try
        {
            _nodes[node.NodeId] = node;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Removes a node from the topology.
    /// </summary>
    /// <param name="nodeId">The unique identifier of the node to remove.</param>
    public void RemoveNode(string nodeId)
    {
        _lock.EnterWriteLock();
        try
        {
            _nodes.TryRemove(nodeId, out _);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Gets the topology metadata for a specific node.
    /// </summary>
    /// <param name="nodeId">The unique identifier of the node.</param>
    /// <returns>The node topology, or null if the node is not found.</returns>
    public NodeTopology? GetNode(string nodeId)
    {
        _lock.EnterReadLock();
        try
        {
            _nodes.TryGetValue(nodeId, out var node);
            return node;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Gets all nodes in the topology.
    /// </summary>
    /// <returns>A read-only list of all node topologies.</returns>
    public IReadOnlyList<NodeTopology> GetAllNodes()
    {
        _lock.EnterReadLock();
        try
        {
            return _nodes.Values.ToList();
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Serializes the topology to a byte array for Raft persistence.
    /// </summary>
    /// <returns>A JSON-serialized byte array representation of the topology.</returns>
    public byte[] Serialize()
    {
        _lock.EnterReadLock();
        try
        {
            return JsonSerializer.SerializeToUtf8Bytes(_nodes.Values.ToList());
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Deserializes a topology from a byte array.
    /// </summary>
    /// <param name="data">The JSON-serialized byte array.</param>
    /// <returns>A new <see cref="ClusterTopology"/> instance.</returns>
    public static ClusterTopology Deserialize(byte[] data)
    {
        var nodes = JsonSerializer.Deserialize<List<NodeTopology>>(data) ?? new List<NodeTopology>();
        var topology = new ClusterTopology();
        foreach (var node in nodes)
        {
            topology.AddOrUpdateNode(node);
        }
        return topology;
    }
}
