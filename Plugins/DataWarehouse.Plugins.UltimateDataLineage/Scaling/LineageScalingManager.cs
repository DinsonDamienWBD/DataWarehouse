using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Persistence;
using DataWarehouse.SDK.Contracts.Scaling;

namespace DataWarehouse.Plugins.UltimateDataLineage.Scaling;

/// <summary>
/// Paginated result set returned by lineage traversal operations.
/// </summary>
/// <typeparam name="T">The type of items in the result set.</typeparam>
[SdkCompatibility("6.0.0", Notes = "Phase 88-08: Paginated result for lineage queries")]
public sealed class LineagePagedResult<T>
{
    /// <summary>Gets the items in this page.</summary>
    public IReadOnlyList<T> Items { get; }

    /// <summary>Gets the total number of items across all pages.</summary>
    public int TotalCount { get; }

    /// <summary>Gets whether more items exist beyond this page.</summary>
    public bool HasMore { get; }

    /// <summary>
    /// Initializes a new <see cref="LineagePagedResult{T}"/>.
    /// </summary>
    /// <param name="items">Items in this page.</param>
    /// <param name="totalCount">Total count across all pages.</param>
    /// <param name="hasMore">Whether additional pages exist.</param>
    public LineagePagedResult(IReadOnlyList<T> items, int totalCount, bool hasMore)
    {
        Items = items;
        TotalCount = totalCount;
        HasMore = hasMore;
    }
}

/// <summary>
/// Direction of lineage traversal for <see cref="LineageScalingManager.TraceLineage"/>.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 88-08: Lineage traversal direction")]
public enum LineageDirection
{
    /// <summary>Traverse upstream (ancestors/inputs).</summary>
    Upstream,

    /// <summary>Traverse downstream (descendants/outputs).</summary>
    Downstream,

    /// <summary>Traverse both upstream and downstream.</summary>
    Both
}

/// <summary>
/// A node in a lineage graph with its adjacency edges.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 88-08: Lineage graph node")]
public sealed class LineageNode
{
    /// <summary>Gets the unique node identifier.</summary>
    public string NodeId { get; }

    /// <summary>Gets the depth at which this node was discovered during traversal.</summary>
    public int Depth { get; }

    /// <summary>Gets the serialized node metadata.</summary>
    public byte[]? Metadata { get; }

    /// <summary>
    /// Initializes a new <see cref="LineageNode"/>.
    /// </summary>
    /// <param name="nodeId">Unique node identifier.</param>
    /// <param name="depth">Discovery depth during traversal.</param>
    /// <param name="metadata">Optional serialized node metadata.</param>
    public LineageNode(string nodeId, int depth, byte[]? metadata = null)
    {
        NodeId = nodeId;
        Depth = depth;
        Metadata = metadata;
    }
}

/// <summary>
/// Manages scaling for the DataLineage plugin with persistent graph store backed by
/// <see cref="IPersistentBackingStore"/>, consistent-hash graph partitioning with per-partition
/// <see cref="BoundedCache{TKey,TValue}"/> (LRU), and paginated BFS lineage traversal with
/// configurable maximum depth.
/// </summary>
/// <remarks>
/// <para>
/// Addresses DSCL-15: DataLineage previously stored the entire graph in unbounded in-memory dictionaries.
/// On restart, all lineage data was lost. Under load, memory grew without bound. This manager provides:
/// <list type="bullet">
///   <item><description>Persistent adjacency list storage: each node's outgoing edges serialized to <c>dw://internal/lineage/{nodeId}</c></description></item>
///   <item><description>Consistent-hash partitioning into N partitions (default 16), each with its own LRU BoundedCache (50K nodes)</description></item>
///   <item><description>Paginated BFS traversal via <see cref="TraceLineage"/> with configurable <see cref="MaxTraversalDepth"/> (default 50)</description></item>
///   <item><description>Hot partition auto-expansion (doubles capacity when >90% full)</description></item>
/// </list>
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 88-08: Lineage scaling with persistent graph, partitioned caches, paginated BFS")]
public sealed class LineageScalingManager : IScalableSubsystem, IDisposable
{
    /// <summary>Default number of graph partitions.</summary>
    public const int DefaultPartitionCount = 16;

    /// <summary>Default maximum nodes per partition.</summary>
    public const int DefaultMaxNodesPerPartition = 50_000;

    /// <summary>Default maximum traversal depth to prevent runaway graph walks.</summary>
    public const int DefaultMaxTraversalDepth = 50;

    /// <summary>Utilization threshold above which a hot partition auto-expands.</summary>
    public const double HotPartitionThreshold = 0.90;

    private const string BackingStorePrefix = "dw://internal/lineage";

    // ---- Partitioned node caches (nodeId -> serialized adjacency list) ----
    private readonly BoundedCache<string, byte[]>[] _partitions;
    private readonly int _partitionCount;
    private readonly int[] _partitionCapacities;

    // ---- Backing store ----
    private readonly IPersistentBackingStore? _backingStore;

    // ---- Configuration ----
    private int _maxTraversalDepth;

    // ---- Scaling ----
    private readonly object _configLock = new();
    private ScalingLimits _currentLimits;

    // ---- Metrics ----
    private long _totalNodeCount;
    private long _backingStoreReads;
    private long _backingStoreWrites;
    private long _traversalsExecuted;
    private long _traversalNodesVisited;

    /// <summary>
    /// Initializes a new instance of the <see cref="LineageScalingManager"/> class.
    /// </summary>
    /// <param name="backingStore">
    /// Optional persistent backing store for graph adjacency lists.
    /// When <c>null</c>, operates in-memory only (state lost on restart).
    /// </param>
    /// <param name="initialLimits">Initial scaling limits. Uses defaults if <c>null</c>.</param>
    /// <param name="partitionCount">Number of graph partitions. Uses <see cref="DefaultPartitionCount"/> if <c>null</c>.</param>
    /// <param name="maxTraversalDepth">Maximum BFS traversal depth. Uses <see cref="DefaultMaxTraversalDepth"/> if <c>null</c>.</param>
    public LineageScalingManager(
        IPersistentBackingStore? backingStore = null,
        ScalingLimits? initialLimits = null,
        int? partitionCount = null,
        int? maxTraversalDepth = null)
    {
        _backingStore = backingStore;
        _currentLimits = initialLimits ?? new ScalingLimits(MaxCacheEntries: DefaultMaxNodesPerPartition);
        _partitionCount = partitionCount ?? DefaultPartitionCount;
        _maxTraversalDepth = maxTraversalDepth ?? DefaultMaxTraversalDepth;

        if (_partitionCount < 1) throw new ArgumentOutOfRangeException(nameof(partitionCount), "Must be at least 1.");

        _partitions = new BoundedCache<string, byte[]>[_partitionCount];
        _partitionCapacities = new int[_partitionCount];

        for (int i = 0; i < _partitionCount; i++)
        {
            _partitionCapacities[i] = DefaultMaxNodesPerPartition;
            _partitions[i] = CreatePartitionCache(i, DefaultMaxNodesPerPartition);
        }
    }

    // -------------------------------------------------------------------
    // Graph operations
    // -------------------------------------------------------------------

    /// <summary>
    /// Stores a node's adjacency list (outgoing edges) with write-through to the backing store.
    /// The node is routed to a partition via consistent hash of the node ID.
    /// </summary>
    /// <param name="nodeId">Unique node identifier.</param>
    /// <param name="adjacencyData">Serialized adjacency list (outgoing edges).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task PutNodeAsync(string nodeId, byte[] adjacencyData, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(nodeId);
        ArgumentNullException.ThrowIfNull(adjacencyData);

        int partition = GetPartition(nodeId);
        await _partitions[partition].PutAsync(nodeId, adjacencyData, ct).ConfigureAwait(false);
        Interlocked.Increment(ref _totalNodeCount);
        Interlocked.Increment(ref _backingStoreWrites);

        // Check for hot partition auto-expansion
        AutoExpandPartition(partition);
    }

    /// <summary>
    /// Retrieves a node's adjacency list, falling back to the backing store on cache miss.
    /// </summary>
    /// <param name="nodeId">Unique node identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Serialized adjacency list, or <c>null</c> if not found.</returns>
    public async Task<byte[]?> GetNodeAsync(string nodeId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(nodeId);

        int partition = GetPartition(nodeId);
        var result = await _partitions[partition].GetAsync(nodeId, ct).ConfigureAwait(false);
        if (result != null)
            Interlocked.Increment(ref _backingStoreReads);
        return result;
    }

    /// <summary>
    /// Removes a node from the graph.
    /// </summary>
    /// <param name="nodeId">Unique node identifier.</param>
    /// <returns><c>true</c> if the node was found and removed; otherwise <c>false</c>.</returns>
    public bool RemoveNode(string nodeId)
    {
        ArgumentNullException.ThrowIfNull(nodeId);

        int partition = GetPartition(nodeId);
        if (_partitions[partition].TryRemove(nodeId, out _))
        {
            Interlocked.Decrement(ref _totalNodeCount);
            return true;
        }
        return false;
    }

    // -------------------------------------------------------------------
    // Paginated lineage traversal (BFS)
    // -------------------------------------------------------------------

    /// <summary>
    /// Traces lineage from a starting node using BFS with configurable direction, maximum depth,
    /// and pagination. Uses a visited set to prevent cycles and infinite loops.
    /// </summary>
    /// <param name="nodeId">Starting node identifier.</param>
    /// <param name="direction">Direction to traverse (upstream, downstream, or both).</param>
    /// <param name="maxDepth">Maximum traversal depth. Clamped to <see cref="MaxTraversalDepth"/>.</param>
    /// <param name="offset">Number of discovered nodes to skip (for pagination).</param>
    /// <param name="limit">Maximum number of nodes to return.</param>
    /// <returns>A <see cref="LineagePagedResult{T}"/> of <see cref="LineageNode"/> entries.</returns>
    public LineagePagedResult<LineageNode> TraceLineage(
        string nodeId,
        LineageDirection direction,
        int maxDepth,
        int offset,
        int limit)
    {
        ArgumentNullException.ThrowIfNull(nodeId);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        maxDepth = Math.Min(maxDepth, _maxTraversalDepth);

        Interlocked.Increment(ref _traversalsExecuted);

        // BFS traversal with visited set
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var allNodes = new List<LineageNode>();
        var queue = new Queue<(string Id, int Depth)>();

        queue.Enqueue((nodeId, 0));
        visited.Add(nodeId);

        while (queue.Count > 0)
        {
            var (currentId, currentDepth) = queue.Dequeue();

            int partition = GetPartition(currentId);
            var adjacencyData = _partitions[partition].GetOrDefault(currentId);

            allNodes.Add(new LineageNode(currentId, currentDepth, adjacencyData));
            Interlocked.Increment(ref _traversalNodesVisited);

            if (currentDepth >= maxDepth || adjacencyData == null)
                continue;

            // Deserialize adjacency list to get connected nodes
            var edges = DeserializeEdges(adjacencyData);
            var relevantEdges = FilterEdgesByDirection(edges, direction);

            foreach (var edgeTarget in relevantEdges)
            {
                if (visited.Add(edgeTarget))
                {
                    queue.Enqueue((edgeTarget, currentDepth + 1));
                }
            }
        }

        int totalCount = allNodes.Count;
        var page = allNodes.Skip(offset).Take(limit).ToList();
        bool hasMore = offset + limit < totalCount;

        return new LineagePagedResult<LineageNode>(page, totalCount, hasMore);
    }

    /// <summary>
    /// Gets or sets the maximum traversal depth. Prevents runaway graph walks.
    /// </summary>
    public int MaxTraversalDepth
    {
        get => _maxTraversalDepth;
        set => _maxTraversalDepth = Math.Max(1, value);
    }

    // -------------------------------------------------------------------
    // Partitioning
    // -------------------------------------------------------------------

    /// <summary>
    /// Gets the number of graph partitions.
    /// </summary>
    public int PartitionCount => _partitionCount;

    /// <summary>
    /// Gets the total number of nodes across all partitions.
    /// </summary>
    public long TotalNodeCount => Interlocked.Read(ref _totalNodeCount);

    /// <summary>
    /// Gets the current capacity for a specific partition.
    /// </summary>
    /// <param name="partitionIndex">Zero-based partition index.</param>
    /// <returns>The maximum number of entries for the specified partition.</returns>
    public int GetPartitionCapacity(int partitionIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(partitionIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(partitionIndex, _partitionCount);
        return _partitionCapacities[partitionIndex];
    }

    private int GetPartition(string nodeId)
    {
        // Consistent hash: FNV-1a for stable distribution
        uint hash = 2166136261u;
        foreach (char c in nodeId)
        {
            hash ^= (uint)c;
            hash *= 16777619u;
        }
        return (int)(hash % (uint)_partitionCount);
    }

    private BoundedCache<string, byte[]> CreatePartitionCache(int partitionId, int maxEntries)
    {
        return new BoundedCache<string, byte[]>(new BoundedCacheOptions<string, byte[]>
        {
            MaxEntries = maxEntries,
            EvictionPolicy = CacheEvictionMode.LRU,
            BackingStore = _backingStore,
            BackingStorePath = $"{BackingStorePrefix}/partition-{partitionId}",
            Serializer = static v => v,
            Deserializer = static v => v,
            KeyToString = static k => k,
            WriteThrough = _backingStore != null
        });
    }

    private void AutoExpandPartition(int partitionIndex)
    {
        var partition = _partitions[partitionIndex];
        int capacity = _partitionCapacities[partitionIndex];
        int count = partition.Count;

        if (capacity > 0 && (double)count / capacity >= HotPartitionThreshold)
        {
            int newCapacity = Math.Min(capacity * 2, int.MaxValue / 2);
            if (newCapacity > capacity)
            {
                // P2-2353: Actually replace the cache with one of doubled capacity.
                // BoundedCache does not support in-place resize, so we create a new instance
                // and copy all live entries from the old cache before swapping.
                var newPartition = CreatePartitionCache(partitionIndex, newCapacity);
                foreach (var kvp in partition)
                    newPartition.Put(kvp.Key, kvp.Value);
                _partitions[partitionIndex] = newPartition;
                _partitionCapacities[partitionIndex] = newCapacity;
            }
        }
    }

    // -------------------------------------------------------------------
    // Edge helpers
    // -------------------------------------------------------------------

    private static IReadOnlyList<string> DeserializeEdges(byte[] adjacencyData)
    {
        try
        {
            var edges = JsonSerializer.Deserialize<EdgeList>(adjacencyData)?.Targets;
            return edges != null ? edges : Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static IEnumerable<string> FilterEdgesByDirection(IReadOnlyList<string> edges, LineageDirection direction)
    {
        // P2-2351: All stored edges are outgoing (downstream, i.e. from source to target).
        // Downstream and Both traversals follow these edges.
        // Upstream traversal requires a reverse index which is not built in this implementation;
        // returning an empty set for Upstream prevents silently returning wrong results.
        return direction switch
        {
            LineageDirection.Downstream => edges,
            LineageDirection.Both => edges,
            LineageDirection.Upstream => Array.Empty<string>(), // reverse index not available
            _ => edges
        };
    }

    /// <summary>
    /// Internal DTO for edge serialization.
    /// </summary>
    private sealed class EdgeList
    {
        /// <summary>Gets or sets the target node IDs.</summary>
        public List<string>? Targets { get; set; }
    }

    // -------------------------------------------------------------------
    // IScalableSubsystem
    // -------------------------------------------------------------------

    /// <inheritdoc />
    public IReadOnlyDictionary<string, object> GetScalingMetrics()
    {
        var metrics = new Dictionary<string, object>
        {
            ["lineage.totalNodeCount"] = Interlocked.Read(ref _totalNodeCount),
            ["lineage.partitionCount"] = _partitionCount,
            ["lineage.maxTraversalDepth"] = _maxTraversalDepth,
            ["lineage.traversalsExecuted"] = Interlocked.Read(ref _traversalsExecuted),
            ["lineage.traversalNodesVisited"] = Interlocked.Read(ref _traversalNodesVisited),
            ["lineage.backingStoreReads"] = Interlocked.Read(ref _backingStoreReads),
            ["lineage.backingStoreWrites"] = Interlocked.Read(ref _backingStoreWrites)
        };

        // Per-partition sizes and hit rates
        for (int i = 0; i < _partitionCount; i++)
        {
            var stats = _partitions[i].GetStatistics();
            metrics[$"lineage.partition.{i}.size"] = stats.ItemCount;
            metrics[$"lineage.partition.{i}.hitRate"] = stats.HitRatio;
            metrics[$"lineage.partition.{i}.capacity"] = _partitionCapacities[i];
        }

        return metrics;
    }

    /// <inheritdoc />
    public Task ReconfigureLimitsAsync(ScalingLimits limits, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        lock (_configLock)
        {
            _currentLimits = limits;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public ScalingLimits CurrentLimits
    {
        get
        {
            lock (_configLock)
            {
                return _currentLimits;
            }
        }
    }

    /// <inheritdoc />
    public BackpressureState CurrentBackpressureState
    {
        get
        {
            long totalNodes = Interlocked.Read(ref _totalNodeCount);
            long maxCapacity = 0;
            for (int i = 0; i < _partitionCount; i++)
                maxCapacity += _partitionCapacities[i];

            if (maxCapacity == 0) return BackpressureState.Normal;

            double utilization = (double)totalNodes / maxCapacity;
            return utilization switch
            {
                >= 0.85 => BackpressureState.Critical,
                >= 0.50 => BackpressureState.Warning,
                _ => BackpressureState.Normal
            };
        }
    }

    // -------------------------------------------------------------------
    // IDisposable
    // -------------------------------------------------------------------

    /// <inheritdoc />
    public void Dispose()
    {
        for (int i = 0; i < _partitions.Length; i++)
            _partitions[i].Dispose();
    }
}
