using System;
using System.Collections.Generic;
using System.IO.Hashing;
using System.Runtime.CompilerServices;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.AdaptiveIndex;

/// <summary>
/// Controlled Replication Under Scalable Hashing (CRUSH) — deterministic pseudorandom
/// shard placement without a centralized lookup table.
/// </summary>
/// <remarks>
/// <para>
/// CRUSH maps objects to storage nodes using a hierarchical cluster topology and weighted
/// pseudorandom selection. Given the same key and the same cluster map, CRUSH always produces
/// the same placement — no coordinator needed. This eliminates the single point of failure
/// inherent in centralized placement tables.
/// </para>
/// <para>
/// The algorithm walks the hierarchy from root to leaf: at each level, it applies the Straw2
/// algorithm (hash * weight, pick max draw) to select a child. For replicas, it uses
/// (key + replicaIndex) as the hash input. Failed nodes are handled by re-selection with
/// an incremented tiebreaker.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-05 CRUSH deterministic shard placement")]
public sealed class CrushPlacement
{
    /// <summary>
    /// Selects target node IDs for the given key using the CRUSH algorithm.
    /// Deterministic: same key + same map = same placement.
    /// </summary>
    /// <param name="key">The key to place.</param>
    /// <param name="numReplicas">Number of replicas to place. Default: 1.</param>
    /// <param name="map">The cluster topology map.</param>
    /// <returns>Array of node IDs for the key's placement.</returns>
    public int[] Select(byte[] key, int numReplicas, CrushMap map)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentOutOfRangeException.ThrowIfLessThan(numReplicas, 1, nameof(numReplicas));

        if (map.Root == null || map.Root.Children.Count == 0)
            return Array.Empty<int>();

        var result = new List<int>(numReplicas);
        var usedNodes = new HashSet<int>();

        for (int r = 0; r < numReplicas; r++)
        {
            int attempts = 0;
            int maxAttempts = numReplicas * 3 + 10; // Prevent infinite loops

            while (attempts < maxAttempts)
            {
                int nodeId = SelectSingleNode(key, r + attempts * numReplicas, map.Root, map);
                if (nodeId >= 0 && !usedNodes.Contains(nodeId))
                {
                    result.Add(nodeId);
                    usedNodes.Add(nodeId);
                    break;
                }
                attempts++;
            }
        }

        return result.ToArray();
    }

    /// <summary>
    /// Returns the target node IDs for a key's placement. Convenience wrapper.
    /// </summary>
    /// <param name="key">The key to place.</param>
    /// <param name="replicas">Number of replicas. Default: 1.</param>
    /// <param name="map">The cluster topology map.</param>
    /// <returns>Array of node IDs.</returns>
    public int[] GetTargetNodes(byte[] key, CrushMap map, int replicas = 1)
    {
        return Select(key, replicas, map);
    }

    /// <summary>
    /// Selects a single node by walking the hierarchy using Straw2 at each level.
    /// </summary>
    private int SelectSingleNode(byte[] key, int replicaIndex, CrushBucket current, CrushMap map)
    {
        while (true)
        {
            if (current.Children.Count == 0)
                return current.BucketId;

            // Straw2: for each child, compute draw = hash(key, child.id, replica) * weight
            CrushBucket? bestChild = null;
            ulong bestDraw = 0;

            foreach (var child in current.Children)
            {
                if (child.IsDown && !child.IsLeafNode)
                    continue;

                if (child.IsDown && child.IsLeafNode)
                    continue;

                ulong draw = ComputeStraw2Draw(key, child.BucketId, replicaIndex, child.Weight);
                if (bestChild == null || draw > bestDraw)
                {
                    bestDraw = draw;
                    bestChild = child;
                }
            }

            if (bestChild == null)
                return -1; // All children are down

            if (bestChild.IsLeafNode)
                return bestChild.BucketId;

            current = bestChild;
        }
    }

    /// <summary>
    /// Computes the Straw2 draw value for a child: hash(key, childId, replicaIndex) * weight.
    /// Higher draw wins the selection at each hierarchy level.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong ComputeStraw2Draw(byte[] key, int childId, int replicaIndex, float weight)
    {
        // Build hash input: key + childId + replicaIndex
        Span<byte> input = stackalloc byte[key.Length + 8];
        key.AsSpan().CopyTo(input);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(input.Slice(key.Length), childId);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(input.Slice(key.Length + 4), replicaIndex);

        ulong hash = XxHash64.HashToUInt64(input, 0xC805EED42);

        // Multiply by weight (scaled to avoid floating-point issues)
        // Use the upper 48 bits of hash multiplied by weight
        double draw = (double)(hash >> 16) * Math.Max(weight, 0.001f);
        return (ulong)draw;
    }

}

/// <summary>
/// Hierarchical cluster topology map used by the CRUSH placement algorithm.
/// </summary>
/// <remarks>
/// The map describes a tree of buckets: the root represents the entire cluster,
/// with levels for datacenters, racks, hosts, and individual storage nodes (leaves).
/// Each bucket has a weight proportional to its storage capacity.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-05 CRUSH cluster topology map")]
public sealed class CrushMap
{
    /// <summary>
    /// Gets the root bucket of the cluster hierarchy.
    /// </summary>
    public CrushBucket Root { get; }

    /// <summary>
    /// Index of all buckets by ID for quick lookup.
    /// </summary>
    private readonly Dictionary<int, CrushBucket> _bucketIndex = new();

    /// <summary>
    /// Initializes a new <see cref="CrushMap"/> with a root bucket.
    /// </summary>
    /// <param name="rootId">The root bucket ID.</param>
    /// <param name="rootWeight">The root bucket weight.</param>
    public CrushMap(int rootId = 0, float rootWeight = 1.0f)
    {
        Root = new CrushBucket(rootId, CrushBucketType.Root, rootWeight);
        _bucketIndex[rootId] = Root;
    }

    /// <summary>
    /// Adds a node (bucket) to the cluster map under the specified parent.
    /// </summary>
    /// <param name="nodeId">The unique ID for the new node.</param>
    /// <param name="weight">The capacity weight of the node.</param>
    /// <param name="parentBucketId">The parent bucket's ID.</param>
    /// <param name="type">The bucket type (datacenter, rack, host, node). Default: Node.</param>
    /// <exception cref="ArgumentException">Thrown if the parent doesn't exist or nodeId is duplicate.</exception>
    public void AddNode(int nodeId, float weight, int parentBucketId, CrushBucketType type = CrushBucketType.Node)
    {
        if (_bucketIndex.ContainsKey(nodeId))
            throw new ArgumentException($"Node {nodeId} already exists in the map.", nameof(nodeId));

        if (!_bucketIndex.TryGetValue(parentBucketId, out var parent))
            throw new ArgumentException($"Parent bucket {parentBucketId} not found.", nameof(parentBucketId));

        var bucket = new CrushBucket(nodeId, type, weight);
        parent.Children.Add(bucket);
        _bucketIndex[nodeId] = bucket;
    }

    /// <summary>
    /// Marks a node as down (failed). CRUSH will skip this node during selection,
    /// causing minimal data movement (only keys on this node are reassigned).
    /// </summary>
    /// <param name="nodeId">The node ID to mark as down.</param>
    /// <returns>True if the node was found and marked down.</returns>
    public bool RemoveNode(int nodeId)
    {
        if (!_bucketIndex.TryGetValue(nodeId, out var bucket))
            return false;

        bucket.IsDown = true;
        return true;
    }

    /// <summary>
    /// Marks a previously-down node as back up.
    /// </summary>
    /// <param name="nodeId">The node ID to restore.</param>
    /// <returns>True if the node was found and restored.</returns>
    public bool RestoreNode(int nodeId)
    {
        if (!_bucketIndex.TryGetValue(nodeId, out var bucket))
            return false;

        bucket.IsDown = false;
        return true;
    }

    /// <summary>
    /// Looks up a bucket by ID.
    /// </summary>
    /// <param name="bucketId">The bucket ID.</param>
    /// <returns>The bucket, or null if not found.</returns>
    public CrushBucket? GetBucket(int bucketId)
    {
        return _bucketIndex.TryGetValue(bucketId, out var bucket) ? bucket : null;
    }

    /// <summary>
    /// Gets the total number of buckets (including root) in the map.
    /// </summary>
    public int BucketCount => _bucketIndex.Count;
}

/// <summary>
/// A bucket in the CRUSH cluster hierarchy (root, datacenter, rack, host, or storage node).
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-05 CRUSH bucket")]
public sealed class CrushBucket
{
    /// <summary>
    /// Gets the unique identifier for this bucket.
    /// </summary>
    public int BucketId { get; }

    /// <summary>
    /// Gets the type of this bucket in the hierarchy.
    /// </summary>
    public CrushBucketType BucketType { get; }

    /// <summary>
    /// Gets the capacity weight. Higher weight = more data assigned. Proportional to storage capacity.
    /// </summary>
    public float Weight { get; }

    /// <summary>
    /// Gets or sets whether this node is marked as down (failed).
    /// Thread-safe via volatile backing field — routing threads must see marking immediately.
    /// </summary>
    private volatile bool _isDown;
    public bool IsDown
    {
        get => _isDown;
        set => _isDown = value;
    }

    /// <summary>
    /// Gets the child buckets.
    /// </summary>
    public List<CrushBucket> Children { get; } = new();

    /// <summary>
    /// Gets whether this is a leaf node (no children, represents a storage device).
    /// </summary>
    public bool IsLeafNode => Children.Count == 0;

    /// <summary>
    /// Initializes a new <see cref="CrushBucket"/>.
    /// </summary>
    public CrushBucket(int bucketId, CrushBucketType bucketType, float weight)
    {
        BucketId = bucketId;
        BucketType = bucketType;
        Weight = weight;
    }
}

/// <summary>
/// Types of buckets in the CRUSH hierarchy.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-05 CRUSH bucket types")]
public enum CrushBucketType
{
    /// <summary>Cluster root.</summary>
    Root = 0,

    /// <summary>Datacenter or availability zone.</summary>
    Datacenter = 1,

    /// <summary>Rack within a datacenter.</summary>
    Rack = 2,

    /// <summary>Host (physical or virtual machine).</summary>
    Host = 3,

    /// <summary>Storage node (leaf — individual disk/device).</summary>
    Node = 4
}
