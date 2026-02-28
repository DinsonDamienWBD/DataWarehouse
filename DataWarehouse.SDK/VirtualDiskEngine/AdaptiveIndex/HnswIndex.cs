using DataWarehouse.SDK.Contracts;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.VirtualDiskEngine.AdaptiveIndex;

/// <summary>
/// Distance metric for vector similarity computation.
/// </summary>
public enum DistanceMetric
{
    /// <summary>Cosine distance: 1 - cos(a, b). Range [0, 2].</summary>
    Cosine,

    /// <summary>Negative dot product for maximum inner product search.</summary>
    DotProduct,

    /// <summary>Euclidean (L2) distance.</summary>
    Euclidean
}

/// <summary>
/// Hierarchical Navigable Small World (HNSW) graph for approximate nearest neighbor search.
/// Provides logarithmic query time with SIMD-accelerated distance computation via Vector256/Avx2.
/// </summary>
/// <remarks>
/// <para>
/// HNSW builds a multi-layer proximity graph where higher layers act as express lanes for
/// navigation. Each node is assigned a random maximum layer using an exponential distribution.
/// During insertion, the algorithm connects each new node to its M closest neighbors at each
/// layer using a greedy search. Layer 0 uses 2*M connections for better recall.
/// </para>
/// <para>
/// Query proceeds top-down: greedy descent through upper layers to find a good entry point,
/// then beam search at layer 0 with efSearch candidates. Returns the k nearest neighbors
/// sorted by distance ascending.
/// </para>
/// <para>
/// Distance functions use Vector256/Avx2 SIMD intrinsics when available, processing 8 floats
/// per iteration. Falls back to scalar computation on unsupported hardware.
/// </para>
/// <para>
/// Thread safety: insertions are serialized via SemaphoreSlim. Concurrent reads are safe
/// after construction. The node dictionary uses a lock for concurrent insert/read access.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-13 HNSW+PQ")]
public sealed class HnswIndex : IAsyncDisposable
{
    private readonly int _maxLayers;
    private readonly int _m;
    private readonly int _mMax0;
    private readonly int _efConstruction;
    private readonly int _efSearch;
    private readonly double _mL;
    private readonly DistanceMetric _metric;
    private readonly Func<float[], float[], float> _distanceFunc;
    private readonly Dictionary<int, HnswNode> _nodes = new();
    private readonly ReaderWriterLockSlim _nodesLock = new();
    private readonly SemaphoreSlim _insertLock = new(1, 1);
    private readonly Random _random = new();

    private HnswNode? _entryPoint;
    private int _maxLevel;

    /// <summary>
    /// Gets the number of nodes in the index.
    /// </summary>
    public int Count
    {
        get
        {
            _nodesLock.EnterReadLock();
            try { return _nodes.Count; }
            finally { _nodesLock.ExitReadLock(); }
        }
    }

    /// <summary>
    /// Gets the distance metric used by this index.
    /// </summary>
    public DistanceMetric Metric => _metric;

    /// <summary>
    /// Initializes a new HNSW index.
    /// </summary>
    /// <param name="metric">Distance metric for similarity computation.</param>
    /// <param name="maxLayers">Maximum number of layers (default 6).</param>
    /// <param name="m">Maximum connections per node per layer (default 16).</param>
    /// <param name="efConstruction">Search width during index construction (default 200).</param>
    /// <param name="efSearch">Default search width during query (default 50).</param>
    public HnswIndex(
        DistanceMetric metric = DistanceMetric.Cosine,
        int maxLayers = 6,
        int m = 16,
        int efConstruction = 200,
        int efSearch = 50)
    {
        _metric = metric;
        _maxLayers = maxLayers;
        _m = m;
        _mMax0 = m * 2;
        _efConstruction = efConstruction;
        _efSearch = efSearch;
        _mL = 1.0 / Math.Log(m);

        _distanceFunc = metric switch
        {
            DistanceMetric.Cosine => CosineDistance,
            DistanceMetric.DotProduct => DotProductDistance,
            DistanceMetric.Euclidean => EuclideanDistance,
            _ => CosineDistance
        };
    }

    /// <summary>
    /// Inserts a vector into the HNSW graph with the given identifier.
    /// </summary>
    /// <param name="id">Unique identifier for the vector.</param>
    /// <param name="vector">The vector to insert.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task InsertAsync(int id, float[] vector, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(vector);

        var node = new HnswNode(id, vector, _maxLayers);
        int level = AssignLevel();

        await _insertLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _nodesLock.EnterWriteLock();
            try { _nodes[id] = node; }
            finally { _nodesLock.ExitWriteLock(); }

            if (_entryPoint == null)
            {
                _entryPoint = node;
                _maxLevel = level;
                return;
            }

            var current = _entryPoint;

            // Greedily descend from top layer to level+1
            for (int layer = _maxLevel; layer > level; layer--)
            {
                current = GreedyClosest(current, vector, layer);
            }

            // Insert at each layer from level down to 0
            for (int layer = Math.Min(level, _maxLevel); layer >= 0; layer--)
            {
                int maxConnections = layer == 0 ? _mMax0 : _m;
                var candidates = SearchLayer(current, vector, _efConstruction, layer);

                // Select M closest candidates
                var neighbors = candidates
                    .OrderBy(c => c.Distance)
                    .Take(maxConnections)
                    .ToList();

                foreach (var (neighborNode, _) in neighbors)
                {
                    node.AddNeighbor(layer, neighborNode.Id);
                    neighborNode.AddNeighbor(layer, node.Id);

                    // Prune neighbor if it has too many connections
                    PruneConnections(neighborNode, layer, maxConnections);
                }

                if (candidates.Count > 0)
                {
                    current = candidates.OrderBy(c => c.Distance).First().Node;
                }
            }

            if (level > _maxLevel)
            {
                _entryPoint = node;
                _maxLevel = level;
            }
        }
        finally
        {
            _insertLock.Release();
        }
    }

    /// <summary>
    /// Searches for the k nearest neighbors of the query vector.
    /// </summary>
    /// <param name="query">Query vector.</param>
    /// <param name="k">Number of nearest neighbors to return.</param>
    /// <param name="efSearch">Search width override (0 uses default).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of (Id, Distance) pairs sorted by distance ascending.</returns>
    public Task<IReadOnlyList<(int Id, float Distance)>> SearchAsync(
        float[] query,
        int k,
        int efSearch = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(k);

        if (_entryPoint == null)
            return Task.FromResult<IReadOnlyList<(int Id, float Distance)>>(Array.Empty<(int, float)>());

        int ef = efSearch > 0 ? efSearch : _efSearch;
        var current = _entryPoint;

        // Greedy descent through upper layers
        for (int layer = _maxLevel; layer > 0; layer--)
        {
            current = GreedyClosest(current, query, layer);
        }

        // Beam search at layer 0
        var candidates = SearchLayer(current, query, Math.Max(ef, k), 0);

        var results = candidates
            .OrderBy(c => c.Distance)
            .Take(k)
            .Select(c => (c.Node.Id, c.Distance))
            .ToList();

        return Task.FromResult<IReadOnlyList<(int Id, float Distance)>>(results);
    }

    /// <summary>
    /// Serializes the HNSW index to a stream for persistence.
    /// </summary>
    public void Serialize(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        _nodesLock.EnterReadLock();
        try
        {
            writer.Write(_nodes.Count);
            writer.Write(_maxLevel);
            writer.Write(_entryPoint?.Id ?? -1);
            writer.Write((int)_metric);
            writer.Write(_m);
            writer.Write(_maxLayers);

            foreach (var (id, node) in _nodes)
            {
                writer.Write(id);
                writer.Write(node.Vector.Length);
                foreach (float v in node.Vector)
                    writer.Write(v);

                // Write neighbor lists for each layer
                for (int layer = 0; layer < _maxLayers; layer++)
                {
                    var neighbors = node.GetNeighbors(layer);
                    writer.Write(neighbors.Count);
                    foreach (int n in neighbors)
                        writer.Write(n);
                }
            }
        }
        finally
        {
            _nodesLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Deserializes an HNSW index from a stream.
    /// </summary>
    public static HnswIndex Deserialize(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        int nodeCount = reader.ReadInt32();
        int maxLevel = reader.ReadInt32();
        int entryPointId = reader.ReadInt32();
        var metric = (DistanceMetric)reader.ReadInt32();
        int m = reader.ReadInt32();
        int maxLayers = reader.ReadInt32();

        var index = new HnswIndex(metric, maxLayers, m);

        for (int i = 0; i < nodeCount; i++)
        {
            int id = reader.ReadInt32();
            int dim = reader.ReadInt32();
            float[] vector = new float[dim];
            for (int d = 0; d < dim; d++)
                vector[d] = reader.ReadSingle();

            var node = new HnswNode(id, vector, maxLayers);

            for (int layer = 0; layer < maxLayers; layer++)
            {
                int neighborCount = reader.ReadInt32();
                for (int n = 0; n < neighborCount; n++)
                    node.AddNeighbor(layer, reader.ReadInt32());
            }

            index._nodes[id] = node;
        }

        index._maxLevel = maxLevel;
        if (entryPointId >= 0 && index._nodes.TryGetValue(entryPointId, out var ep))
            index._entryPoint = ep;

        return index;
    }

    #region Distance Functions

    /// <summary>
    /// Computes cosine distance between two vectors: 1 - cos(a, b).
    /// Uses AVX2 SIMD intrinsics when available, processing 8 floats per iteration.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float CosineDistance(float[] a, float[] b)
    {
        if (Avx.IsSupported)
            return CosineDistanceAvx(a, b);
        return CosineDistanceScalar(a, b);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float CosineDistanceAvx(float[] a, float[] b)
    {
        int len = Math.Min(a.Length, b.Length);
        int simdLen = len - (len % Vector256<float>.Count);

        var dotSum = Vector256<float>.Zero;
        var magASum = Vector256<float>.Zero;
        var magBSum = Vector256<float>.Zero;

        ref float refA = ref MemoryMarshal.GetArrayDataReference(a);
        ref float refB = ref MemoryMarshal.GetArrayDataReference(b);

        for (int i = 0; i < simdLen; i += Vector256<float>.Count)
        {
            var va = Vector256.LoadUnsafe(ref Unsafe.Add(ref refA, i));
            var vb = Vector256.LoadUnsafe(ref Unsafe.Add(ref refB, i));

            dotSum = Avx.IsSupported
                ? Fma.IsSupported
                    ? Fma.MultiplyAdd(va, vb, dotSum)
                    : Avx.Add(Avx.Multiply(va, vb), dotSum)
                : Vector256.Add(Vector256.Multiply(va, vb), dotSum);

            magASum = Fma.IsSupported
                ? Fma.MultiplyAdd(va, va, magASum)
                : Avx.Add(Avx.Multiply(va, va), magASum);

            magBSum = Fma.IsSupported
                ? Fma.MultiplyAdd(vb, vb, magBSum)
                : Avx.Add(Avx.Multiply(vb, vb), magBSum);
        }

        float dot = HorizontalSum256(dotSum);
        float magA = HorizontalSum256(magASum);
        float magB = HorizontalSum256(magBSum);

        // Handle remainder
        for (int i = simdLen; i < len; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }

        float denom = MathF.Sqrt(magA) * MathF.Sqrt(magB);
        return denom > 0f ? 1f - dot / denom : 1f;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float CosineDistanceScalar(float[] a, float[] b)
    {
        int len = Math.Min(a.Length, b.Length);
        float dot = 0f, magA = 0f, magB = 0f;

        for (int i = 0; i < len; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }

        float denom = MathF.Sqrt(magA) * MathF.Sqrt(magB);
        return denom > 0f ? 1f - dot / denom : 1f;
    }

    /// <summary>
    /// Computes negative dot product distance for maximum inner product search.
    /// Uses AVX2 SIMD intrinsics when available.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float DotProductDistance(float[] a, float[] b)
    {
        if (Avx.IsSupported)
            return DotProductDistanceAvx(a, b);
        return DotProductDistanceScalar(a, b);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float DotProductDistanceAvx(float[] a, float[] b)
    {
        int len = Math.Min(a.Length, b.Length);
        int simdLen = len - (len % Vector256<float>.Count);

        var dotSum = Vector256<float>.Zero;
        ref float refA = ref MemoryMarshal.GetArrayDataReference(a);
        ref float refB = ref MemoryMarshal.GetArrayDataReference(b);

        for (int i = 0; i < simdLen; i += Vector256<float>.Count)
        {
            var va = Vector256.LoadUnsafe(ref Unsafe.Add(ref refA, i));
            var vb = Vector256.LoadUnsafe(ref Unsafe.Add(ref refB, i));

            dotSum = Fma.IsSupported
                ? Fma.MultiplyAdd(va, vb, dotSum)
                : Avx.Add(Avx.Multiply(va, vb), dotSum);
        }

        float dot = HorizontalSum256(dotSum);
        for (int i = simdLen; i < len; i++)
            dot += a[i] * b[i];

        return -dot; // Negative for max inner product
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float DotProductDistanceScalar(float[] a, float[] b)
    {
        int len = Math.Min(a.Length, b.Length);
        float dot = 0f;
        for (int i = 0; i < len; i++)
            dot += a[i] * b[i];
        return -dot;
    }

    /// <summary>
    /// Computes Euclidean (L2) distance between two vectors.
    /// Uses AVX2 SIMD intrinsics when available.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float EuclideanDistance(float[] a, float[] b)
    {
        if (Avx.IsSupported)
            return EuclideanDistanceAvx(a, b);
        return EuclideanDistanceScalar(a, b);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float EuclideanDistanceAvx(float[] a, float[] b)
    {
        int len = Math.Min(a.Length, b.Length);
        int simdLen = len - (len % Vector256<float>.Count);

        var sumSq = Vector256<float>.Zero;
        ref float refA = ref MemoryMarshal.GetArrayDataReference(a);
        ref float refB = ref MemoryMarshal.GetArrayDataReference(b);

        for (int i = 0; i < simdLen; i += Vector256<float>.Count)
        {
            var va = Vector256.LoadUnsafe(ref Unsafe.Add(ref refA, i));
            var vb = Vector256.LoadUnsafe(ref Unsafe.Add(ref refB, i));
            var diff = Avx.Subtract(va, vb);

            sumSq = Fma.IsSupported
                ? Fma.MultiplyAdd(diff, diff, sumSq)
                : Avx.Add(Avx.Multiply(diff, diff), sumSq);
        }

        float sum = HorizontalSum256(sumSq);
        for (int i = simdLen; i < len; i++)
        {
            float d = a[i] - b[i];
            sum += d * d;
        }

        return MathF.Sqrt(sum);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float EuclideanDistanceScalar(float[] a, float[] b)
    {
        int len = Math.Min(a.Length, b.Length);
        float sum = 0f;
        for (int i = 0; i < len; i++)
        {
            float d = a[i] - b[i];
            sum += d * d;
        }
        return MathF.Sqrt(sum);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float HorizontalSum256(Vector256<float> v)
    {
        // Sum all 8 elements: split into two 128-bit halves, add, then horizontal sum
        var upper = v.GetUpper();
        var lower = v.GetLower();
        var sum128 = Vector128.Add(upper, lower);
        // Shuffle and add pairs
        var shuf = Vector128.Shuffle(sum128, Vector128.Create(2, 3, 0, 1));
        sum128 = Vector128.Add(sum128, shuf);
        shuf = Vector128.Shuffle(sum128, Vector128.Create(1, 0, 3, 2));
        sum128 = Vector128.Add(sum128, shuf);
        return sum128.ToScalar();
    }

    #endregion

    #region Graph Operations

    private int AssignLevel()
    {
        double r = _random.NextDouble();
        int level = (int)Math.Floor(-Math.Log(Math.Max(r, 1e-10)) * _mL);
        return Math.Min(level, _maxLayers - 1);
    }

    private HnswNode GreedyClosest(HnswNode entry, float[] query, int layer)
    {
        var current = entry;
        float currentDist = _distanceFunc(query, current.Vector);
        bool changed = true;

        while (changed)
        {
            changed = false;
            foreach (int neighborId in current.GetNeighbors(layer))
            {
                if (TryGetNode(neighborId, out var neighbor))
                {
                    float dist = _distanceFunc(query, neighbor.Vector);
                    if (dist < currentDist)
                    {
                        current = neighbor;
                        currentDist = dist;
                        changed = true;
                    }
                }
            }
        }

        return current;
    }

    private List<(HnswNode Node, float Distance)> SearchLayer(
        HnswNode entry, float[] query, int ef, int layer)
    {
        float entryDist = _distanceFunc(query, entry.Vector);
        var visited = new HashSet<int> { entry.Id };

        // candidates: min-heap by distance (closest first for expansion)
        var candidates = new SortedList<float, HnswNode>(new DuplicateKeyComparer());
        candidates.Add(entryDist, entry);

        // results: track best ef results
        var results = new List<(HnswNode Node, float Distance)> { (entry, entryDist) };

        // Track worst result distance to avoid O(n) scan per iteration
        float trackedWorstDist = float.MaxValue;

        while (candidates.Count > 0)
        {
            var (closestDist, closestNode) = (candidates.Keys[0], candidates.Values[0]);
            candidates.RemoveAt(0);

            // If closest candidate is farther than worst result, stop
            if (results.Count >= ef && closestDist > trackedWorstDist)
                break;

            foreach (int neighborId in closestNode.GetNeighbors(layer))
            {
                if (visited.Add(neighborId) && TryGetNode(neighborId, out var neighbor))
                {
                    float dist = _distanceFunc(query, neighbor.Vector);

                    if (dist < trackedWorstDist || results.Count < ef)
                    {
                        candidates.Add(dist, neighbor);
                        results.Add((neighbor, dist));

                        // Keep only ef best results and update tracked worst
                        if (results.Count > ef)
                        {
                            int worstIdx = 0;
                            float worstDist = results[0].Distance;
                            for (int i = 1; i < results.Count; i++)
                            {
                                if (results[i].Distance > worstDist)
                                {
                                    worstDist = results[i].Distance;
                                    worstIdx = i;
                                }
                            }
                            results.RemoveAt(worstIdx);
                        }

                        // Recompute tracked worst after modification
                        if (results.Count >= ef)
                        {
                            trackedWorstDist = float.MinValue;
                            for (int i = 0; i < results.Count; i++)
                            {
                                if (results[i].Distance > trackedWorstDist)
                                    trackedWorstDist = results[i].Distance;
                            }
                        }
                    }
                }
            }
        }

        return results;
    }

    private void PruneConnections(HnswNode node, int layer, int maxConnections)
    {
        var neighbors = node.GetNeighbors(layer);
        if (neighbors.Count <= maxConnections) return;

        // Keep the closest maxConnections neighbors
        var scored = new List<(int Id, float Distance)>();
        foreach (int nId in neighbors)
        {
            if (TryGetNode(nId, out var n))
                scored.Add((nId, _distanceFunc(node.Vector, n.Vector)));
        }

        scored.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        var keep = new HashSet<int>(scored.Take(maxConnections).Select(s => s.Id));
        node.SetNeighbors(layer, keep.ToList());
    }

    private bool TryGetNode(int id, out HnswNode node)
    {
        _nodesLock.EnterReadLock();
        try { return _nodes.TryGetValue(id, out node!); }
        finally { _nodesLock.ExitReadLock(); }
    }

    #endregion

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        _insertLock.Dispose();
        _nodesLock.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Comparer that allows duplicate keys in SortedList by adding tie-breaking.
    /// </summary>
    private sealed class DuplicateKeyComparer : IComparer<float>
    {
        public int Compare(float x, float y)
        {
            int result = x.CompareTo(y);
            return result == 0 ? 1 : result; // Never return 0 so duplicates are allowed
        }
    }
}

/// <summary>
/// A node in the HNSW graph containing a vector and per-layer neighbor lists.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 86: AIE-13 HNSW node")]
public sealed class HnswNode
{
    private readonly List<int>[] _neighbors;
    private readonly object _lock = new();

    /// <summary>Gets the node identifier.</summary>
    public int Id { get; }

    /// <summary>Gets the vector stored at this node.</summary>
    public float[] Vector { get; }

    /// <summary>
    /// Creates a new HNSW node.
    /// </summary>
    public HnswNode(int id, float[] vector, int maxLayers)
    {
        Id = id;
        Vector = vector;
        _neighbors = new List<int>[maxLayers];
        for (int i = 0; i < maxLayers; i++)
            _neighbors[i] = new List<int>();
    }

    /// <summary>
    /// Gets the neighbor IDs at the specified layer.
    /// </summary>
    public IReadOnlyList<int> GetNeighbors(int layer)
    {
        if (layer < 0 || layer >= _neighbors.Length)
            return Array.Empty<int>();

        lock (_lock)
        {
            return _neighbors[layer].ToList();
        }
    }

    /// <summary>
    /// Adds a neighbor at the specified layer.
    /// </summary>
    public void AddNeighbor(int layer, int neighborId)
    {
        if (layer < 0 || layer >= _neighbors.Length) return;

        lock (_lock)
        {
            // Use binary search on sorted list for O(log n) duplicate check
            int idx = _neighbors[layer].BinarySearch(neighborId);
            if (idx < 0)
                _neighbors[layer].Insert(~idx, neighborId);
        }
    }

    /// <summary>
    /// Replaces the neighbor list at the specified layer.
    /// </summary>
    public void SetNeighbors(int layer, List<int> neighbors)
    {
        if (layer < 0 || layer >= _neighbors.Length) return;

        lock (_lock)
        {
            _neighbors[layer].Clear();
            _neighbors[layer].AddRange(neighbors);
        }
    }
}
