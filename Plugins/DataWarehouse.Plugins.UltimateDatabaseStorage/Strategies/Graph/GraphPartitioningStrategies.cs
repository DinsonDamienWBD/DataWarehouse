using System.Security.Cryptography;
using System.Text;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDatabaseStorage.Strategies.Graph;

/// <summary>
/// Graph partitioning strategy base class.
/// Provides common functionality for partitioning large graphs across multiple shards.
/// </summary>
public abstract class GraphPartitioningStrategyBase
{
    /// <summary>Gets the strategy identifier.</summary>
    public abstract string StrategyId { get; }

    /// <summary>Gets the strategy name.</summary>
    public abstract string Name { get; }

    /// <summary>Gets or sets the number of partitions.</summary>
    public int PartitionCount { get; set; } = 4;

    /// <summary>
    /// Assigns a vertex to a partition.
    /// </summary>
    /// <param name="vertexId">Vertex identifier.</param>
    /// <param name="properties">Vertex properties.</param>
    /// <returns>Partition index (0 to PartitionCount-1).</returns>
    public abstract int AssignVertexPartition(string vertexId, IDictionary<string, object>? properties = null);

    /// <summary>
    /// Assigns an edge to partitions.
    /// </summary>
    /// <param name="edgeId">Edge identifier.</param>
    /// <param name="sourceVertexId">Source vertex ID.</param>
    /// <param name="targetVertexId">Target vertex ID.</param>
    /// <param name="properties">Edge properties.</param>
    /// <returns>Partition assignments (may include multiple for replicated edges).</returns>
    public abstract IEnumerable<int> AssignEdgePartitions(
        string edgeId,
        string sourceVertexId,
        string targetVertexId,
        IDictionary<string, object>? properties = null);

    /// <summary>
    /// Gets the partitions that need to be queried for a given vertex.
    /// </summary>
    /// <param name="vertexId">Vertex identifier.</param>
    /// <returns>Partition indices.</returns>
    public virtual IEnumerable<int> GetQueryPartitions(string vertexId)
    {
        yield return AssignVertexPartition(vertexId);
    }

    /// <summary>
    /// Gets all partition indices.
    /// </summary>
    public IEnumerable<int> GetAllPartitions()
    {
        for (int i = 0; i < PartitionCount; i++)
        {
            yield return i;
        }
    }
}

/// <summary>
/// Hash-based graph partitioning strategy.
/// Distributes vertices uniformly based on hash of vertex ID.
/// Simple and effective for random access patterns.
/// </summary>
/// <remarks>
/// Properties:
/// - Uniform distribution of vertices
/// - O(1) partition lookup
/// - No locality preservation
/// - May create many edge cuts
/// </remarks>
public sealed class HashPartitioningStrategy : GraphPartitioningStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "graph.partition.hash";

    /// <inheritdoc/>
    public override string Name => "Hash-Based Graph Partitioning";

    /// <summary>
    /// Gets or sets the hash algorithm to use.
    /// </summary>
    public HashPartitionAlgorithm Algorithm { get; set; } = HashPartitionAlgorithm.Murmur3;

    /// <inheritdoc/>
    public override int AssignVertexPartition(string vertexId, IDictionary<string, object>? properties = null)
    {
        var hash = Algorithm switch
        {
            HashPartitionAlgorithm.Murmur3 => MurmurHash3(vertexId),
            HashPartitionAlgorithm.Xxhash => XxHash32(vertexId),
            HashPartitionAlgorithm.Fnv1a => Fnv1aHash(vertexId),
            HashPartitionAlgorithm.Sha256 => Sha256Hash(vertexId),
            _ => MurmurHash3(vertexId)
        };

        return (int)(hash % (uint)PartitionCount);
    }

    /// <inheritdoc/>
    public override IEnumerable<int> AssignEdgePartitions(
        string edgeId,
        string sourceVertexId,
        string targetVertexId,
        IDictionary<string, object>? properties = null)
    {
        // Edge goes to source vertex partition (edge-cut strategy)
        yield return AssignVertexPartition(sourceVertexId);
    }

    private static uint MurmurHash3(string key)
    {
        var data = Encoding.UTF8.GetBytes(key);
        const uint c1 = 0xcc9e2d51;
        const uint c2 = 0x1b873593;
        const uint seed = 0x9747b28c;

        uint h1 = seed;
        int length = data.Length;
        int blocks = length / 4;

        for (int i = 0; i < blocks; i++)
        {
            uint k1 = BitConverter.ToUInt32(data, i * 4);
            k1 *= c1;
            k1 = RotateLeft(k1, 15);
            k1 *= c2;

            h1 ^= k1;
            h1 = RotateLeft(h1, 13);
            h1 = h1 * 5 + 0xe6546b64;
        }

        uint tail = 0;
        int tailIndex = blocks * 4;
        switch (length & 3)
        {
            case 3:
                tail ^= (uint)data[tailIndex + 2] << 16;
                goto case 2;
            case 2:
                tail ^= (uint)data[tailIndex + 1] << 8;
                goto case 1;
            case 1:
                tail ^= data[tailIndex];
                tail *= c1;
                tail = RotateLeft(tail, 15);
                tail *= c2;
                h1 ^= tail;
                break;
        }

        h1 ^= (uint)length;
        h1 = FMix(h1);
        return h1;
    }

    private static uint RotateLeft(uint x, int r) => (x << r) | (x >> (32 - r));

    private static uint FMix(uint h)
    {
        h ^= h >> 16;
        h *= 0x85ebca6b;
        h ^= h >> 13;
        h *= 0xc2b2ae35;
        h ^= h >> 16;
        return h;
    }

    private static uint XxHash32(string key)
    {
        var data = Encoding.UTF8.GetBytes(key);
        const uint prime1 = 2654435761U;
        const uint prime2 = 2246822519U;
        const uint prime3 = 3266489917U;
        const uint prime4 = 668265263U;
        const uint prime5 = 374761393U;

        uint h32;
        int index = 0;
        int length = data.Length;

        if (length >= 16)
        {
            uint v1 = unchecked(prime1 + prime2);
            uint v2 = prime2;
            uint v3 = 0;
            uint v4 = unchecked(0U - prime1);

            while (index <= length - 16)
            {
                v1 = Round(v1, BitConverter.ToUInt32(data, index)); index += 4;
                v2 = Round(v2, BitConverter.ToUInt32(data, index)); index += 4;
                v3 = Round(v3, BitConverter.ToUInt32(data, index)); index += 4;
                v4 = Round(v4, BitConverter.ToUInt32(data, index)); index += 4;
            }

            h32 = RotateLeft(v1, 1) + RotateLeft(v2, 7) + RotateLeft(v3, 12) + RotateLeft(v4, 18);
        }
        else
        {
            h32 = prime5;
        }

        h32 += (uint)length;

        while (index <= length - 4)
        {
            h32 += BitConverter.ToUInt32(data, index) * prime3;
            h32 = RotateLeft(h32, 17) * prime4;
            index += 4;
        }

        while (index < length)
        {
            h32 += data[index] * prime5;
            h32 = RotateLeft(h32, 11) * prime1;
            index++;
        }

        h32 ^= h32 >> 15;
        h32 *= prime2;
        h32 ^= h32 >> 13;
        h32 *= prime3;
        h32 ^= h32 >> 16;

        return h32;
    }

    private static uint Round(uint acc, uint input)
    {
        acc += input * 2246822519U;
        acc = RotateLeft(acc, 13);
        acc *= 2654435761U;
        return acc;
    }

    private static uint Fnv1aHash(string key)
    {
        const uint fnvPrime = 16777619;
        const uint offsetBasis = 2166136261;

        uint hash = offsetBasis;
        foreach (char c in key)
        {
            hash ^= c;
            hash *= fnvPrime;
        }
        return hash;
    }

    private static uint Sha256Hash(string key)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return BitConverter.ToUInt32(bytes, 0);
    }
}

/// <summary>
/// Hash algorithm for graph partitioning.
/// </summary>
public enum HashPartitionAlgorithm
{
    /// <summary>MurmurHash3 - fast with good distribution.</summary>
    Murmur3,
    /// <summary>xxHash32 - extremely fast.</summary>
    Xxhash,
    /// <summary>FNV-1a - simple and fast.</summary>
    Fnv1a,
    /// <summary>SHA-256 truncated - cryptographic quality.</summary>
    Sha256
}

/// <summary>
/// Range-based graph partitioning strategy.
/// Partitions vertices based on ID ranges for locality-preserving access.
/// </summary>
/// <remarks>
/// Properties:
/// - Preserves ID ordering locality
/// - Good for range queries
/// - May create uneven distribution
/// - O(log n) partition lookup with binary search
/// </remarks>
public sealed class RangePartitioningStrategy : GraphPartitioningStrategyBase
{
    private readonly List<string> _rangeBoundaries = new();

    /// <inheritdoc/>
    public override string StrategyId => "graph.partition.range";

    /// <inheritdoc/>
    public override string Name => "Range-Based Graph Partitioning";

    /// <summary>
    /// Sets the range boundaries for partitioning.
    /// </summary>
    /// <param name="boundaries">Ordered list of boundary values.</param>
    public void SetRangeBoundaries(IEnumerable<string> boundaries)
    {
        _rangeBoundaries.Clear();
        _rangeBoundaries.AddRange(boundaries.OrderBy(b => b));
        PartitionCount = _rangeBoundaries.Count + 1;
    }

    /// <inheritdoc/>
    public override int AssignVertexPartition(string vertexId, IDictionary<string, object>? properties = null)
    {
        if (_rangeBoundaries.Count == 0)
        {
            // Fall back to simple modulo if no boundaries set
            return Math.Abs(StableHash.Compute(vertexId)) % PartitionCount;
        }

        // Binary search for the appropriate partition
        int left = 0;
        int right = _rangeBoundaries.Count - 1;

        while (left <= right)
        {
            int mid = left + (right - left) / 2;
            int comparison = string.Compare(vertexId, _rangeBoundaries[mid], StringComparison.Ordinal);

            if (comparison < 0)
            {
                right = mid - 1;
            }
            else
            {
                left = mid + 1;
            }
        }

        return left;
    }

    /// <inheritdoc/>
    public override IEnumerable<int> AssignEdgePartitions(
        string edgeId,
        string sourceVertexId,
        string targetVertexId,
        IDictionary<string, object>? properties = null)
    {
        yield return AssignVertexPartition(sourceVertexId);
    }
}

/// <summary>
/// Edge-cut partitioning strategy.
/// Minimizes the number of edges that cross partition boundaries.
/// Vertices are assigned to partitions, edges are cut.
/// </summary>
/// <remarks>
/// Properties:
/// - Each vertex exists in exactly one partition
/// - Edges crossing partitions require network communication
/// - Good for vertex-centric algorithms
/// - Uses random streaming assignment with refinement
/// </remarks>
public sealed class EdgeCutPartitioningStrategy : GraphPartitioningStrategyBase
{
    private readonly BoundedDictionary<string, int> _vertexAssignments = new BoundedDictionary<string, int>(1000);
    private readonly BoundedDictionary<int, long> _partitionSizes = new BoundedDictionary<int, long>(1000);
    private readonly object _balanceLock = new();

    /// <inheritdoc/>
    public override string StrategyId => "graph.partition.edge-cut";

    /// <inheritdoc/>
    public override string Name => "Edge-Cut Graph Partitioning";

    /// <summary>
    /// Gets or sets the balance factor (max partition size / avg partition size).
    /// </summary>
    public double BalanceFactor { get; set; } = 1.1;

    /// <inheritdoc/>
    public override int AssignVertexPartition(string vertexId, IDictionary<string, object>? properties = null)
    {
        if (_vertexAssignments.TryGetValue(vertexId, out var existing))
        {
            return existing;
        }

        // Assign to least loaded partition
        int partition = GetLeastLoadedPartition();

        _vertexAssignments[vertexId] = partition;
        _partitionSizes.AddOrUpdate(partition, 1, (_, count) => count + 1);

        return partition;
    }

    /// <inheritdoc/>
    public override IEnumerable<int> AssignEdgePartitions(
        string edgeId,
        string sourceVertexId,
        string targetVertexId,
        IDictionary<string, object>? properties = null)
    {
        // Edge is stored with source vertex
        yield return AssignVertexPartition(sourceVertexId);
    }

    private int GetLeastLoadedPartition()
    {
        lock (_balanceLock)
        {
            int minPartition = 0;
            long minSize = long.MaxValue;

            for (int i = 0; i < PartitionCount; i++)
            {
                var size = _partitionSizes.GetOrAdd(i, 0);
                if (size < minSize)
                {
                    minSize = size;
                    minPartition = i;
                }
            }

            return minPartition;
        }
    }

    /// <summary>
    /// Gets the current partition statistics.
    /// </summary>
    public Dictionary<int, long> GetPartitionSizes()
    {
        return new Dictionary<int, long>(_partitionSizes);
    }

    /// <summary>
    /// Gets the imbalance ratio (max/min partition sizes).
    /// </summary>
    public double GetImbalanceRatio()
    {
        if (_partitionSizes.IsEmpty) return 1.0;

        var sizes = _partitionSizes.Values.ToList();
        var max = sizes.Max();
        var min = sizes.Min();

        return min == 0 ? double.MaxValue : (double)max / min;
    }
}

/// <summary>
/// Vertex-cut partitioning strategy.
/// Edges are assigned to single partitions, vertices may be replicated.
/// </summary>
/// <remarks>
/// Properties:
/// - Each edge exists in exactly one partition
/// - High-degree vertices are replicated across partitions
/// - Good for edge-centric algorithms (PowerGraph style)
/// - Reduces communication for dense graphs
/// </remarks>
public sealed class VertexCutPartitioningStrategy : GraphPartitioningStrategyBase
{
    private readonly BoundedDictionary<string, HashSet<int>> _vertexReplicas = new BoundedDictionary<string, HashSet<int>>(1000);
    private readonly BoundedDictionary<int, long> _partitionEdgeCounts = new BoundedDictionary<int, long>(1000);
    private readonly object _balanceLock = new();

    /// <inheritdoc/>
    public override string StrategyId => "graph.partition.vertex-cut";

    /// <inheritdoc/>
    public override string Name => "Vertex-Cut Graph Partitioning";

    /// <summary>
    /// Gets or sets the replication factor threshold (vertices with degree > threshold may be replicated).
    /// </summary>
    public int ReplicationThreshold { get; set; } = 10;

    /// <inheritdoc/>
    public override int AssignVertexPartition(string vertexId, IDictionary<string, object>? properties = null)
    {
        if (_vertexReplicas.TryGetValue(vertexId, out var replicas) && replicas.Count > 0)
        {
            // Return the primary (first assigned) partition
            return replicas.First();
        }

        // New vertex - assign to least loaded partition
        int partition = GetLeastLoadedEdgePartition();

        _vertexReplicas.AddOrUpdate(
            vertexId,
            _ => new HashSet<int> { partition },
            (_, set) => { set.Add(partition); return set; });

        return partition;
    }

    /// <inheritdoc/>
    public override IEnumerable<int> AssignEdgePartitions(
        string edgeId,
        string sourceVertexId,
        string targetVertexId,
        IDictionary<string, object>? properties = null)
    {
        // Assign edge to least loaded partition
        int partition = GetLeastLoadedEdgePartition();

        // Ensure both vertices have replicas in this partition
        AddVertexReplica(sourceVertexId, partition);
        AddVertexReplica(targetVertexId, partition);

        _partitionEdgeCounts.AddOrUpdate(partition, 1, (_, count) => count + 1);

        yield return partition;
    }

    /// <inheritdoc/>
    public override IEnumerable<int> GetQueryPartitions(string vertexId)
    {
        if (_vertexReplicas.TryGetValue(vertexId, out var replicas))
        {
            return replicas;
        }
        return base.GetQueryPartitions(vertexId);
    }

    private void AddVertexReplica(string vertexId, int partition)
    {
        _vertexReplicas.AddOrUpdate(
            vertexId,
            _ => new HashSet<int> { partition },
            (_, set) => { set.Add(partition); return set; });
    }

    private int GetLeastLoadedEdgePartition()
    {
        lock (_balanceLock)
        {
            int minPartition = 0;
            long minCount = long.MaxValue;

            for (int i = 0; i < PartitionCount; i++)
            {
                var count = _partitionEdgeCounts.GetOrAdd(i, 0);
                if (count < minCount)
                {
                    minCount = count;
                    minPartition = i;
                }
            }

            return minPartition;
        }
    }

    /// <summary>
    /// Gets the replication factor for a vertex.
    /// </summary>
    /// <param name="vertexId">Vertex identifier.</param>
    /// <returns>Number of partitions containing this vertex.</returns>
    public int GetVertexReplicationFactor(string vertexId)
    {
        return _vertexReplicas.TryGetValue(vertexId, out var replicas) ? replicas.Count : 0;
    }

    /// <summary>
    /// Gets the average replication factor across all vertices.
    /// </summary>
    public double GetAverageReplicationFactor()
    {
        if (_vertexReplicas.IsEmpty) return 1.0;
        return _vertexReplicas.Values.Average(s => s.Count);
    }
}

/// <summary>
/// Community-based graph partitioning strategy.
/// Uses community detection to assign related vertices to the same partition.
/// </summary>
/// <remarks>
/// Properties:
/// - Vertices in the same community tend to be in the same partition
/// - Minimizes edge cuts within communities
/// - Requires pre-computed community assignments or streaming detection
/// - Good for social networks and clustered graphs
/// </remarks>
public sealed class CommunityPartitioningStrategy : GraphPartitioningStrategyBase
{
    private readonly BoundedDictionary<string, int> _communityAssignments = new BoundedDictionary<string, int>(1000);
    private readonly BoundedDictionary<int, int> _communityToPartition = new BoundedDictionary<int, int>(1000);
    private int _nextCommunityId = 0;

    /// <inheritdoc/>
    public override string StrategyId => "graph.partition.community";

    /// <inheritdoc/>
    public override string Name => "Community-Based Graph Partitioning";

    /// <summary>
    /// Gets or sets the algorithm used for community detection.
    /// </summary>
    public CommunityDetectionAlgorithm Algorithm { get; set; } = CommunityDetectionAlgorithm.LabelPropagation;

    /// <summary>
    /// Assigns a vertex to a community.
    /// </summary>
    /// <param name="vertexId">Vertex identifier.</param>
    /// <param name="communityId">Community identifier.</param>
    public void AssignCommunity(string vertexId, int communityId)
    {
        _communityAssignments[vertexId] = communityId;
    }

    /// <summary>
    /// Sets the community assignments in bulk.
    /// </summary>
    /// <param name="assignments">Dictionary of vertex ID to community ID.</param>
    public void SetCommunityAssignments(IDictionary<string, int> assignments)
    {
        _communityAssignments.Clear();
        foreach (var kvp in assignments)
        {
            _communityAssignments[kvp.Key] = kvp.Value;
        }
        RebalanceCommunityPartitions();
    }

    /// <inheritdoc/>
    public override int AssignVertexPartition(string vertexId, IDictionary<string, object>? properties = null)
    {
        // Check if vertex has a pre-assigned community
        if (_communityAssignments.TryGetValue(vertexId, out var community))
        {
            return MapCommunityToPartition(community);
        }

        // Use streaming label propagation if neighbors are available
        if (properties?.TryGetValue("_neighborCommunities", out var neighborsObj) == true &&
            neighborsObj is IEnumerable<int> neighborCommunities)
        {
            var mostFrequent = neighborCommunities
                .GroupBy(c => c)
                .OrderByDescending(g => g.Count())
                .FirstOrDefault()?.Key;

            if (mostFrequent.HasValue)
            {
                _communityAssignments[vertexId] = mostFrequent.Value;
                return MapCommunityToPartition(mostFrequent.Value);
            }
        }

        // Assign to new community
        var newCommunity = Interlocked.Increment(ref _nextCommunityId);
        _communityAssignments[vertexId] = newCommunity;
        return MapCommunityToPartition(newCommunity);
    }

    /// <inheritdoc/>
    public override IEnumerable<int> AssignEdgePartitions(
        string edgeId,
        string sourceVertexId,
        string targetVertexId,
        IDictionary<string, object>? properties = null)
    {
        // Edge goes to source vertex partition
        yield return AssignVertexPartition(sourceVertexId);
    }

    private int MapCommunityToPartition(int communityId)
    {
        return _communityToPartition.GetOrAdd(communityId, _ => communityId % PartitionCount);
    }

    private void RebalanceCommunityPartitions()
    {
        // Count vertices per community
        var communitySizes = _communityAssignments.Values
            .GroupBy(c => c)
            .OrderByDescending(g => g.Count())
            .ToList();

        _communityToPartition.Clear();

        // Assign communities to partitions using greedy bin packing
        var partitionLoads = new long[PartitionCount];

        foreach (var community in communitySizes)
        {
            int minPartition = 0;
            long minLoad = long.MaxValue;

            for (int i = 0; i < PartitionCount; i++)
            {
                if (partitionLoads[i] < minLoad)
                {
                    minLoad = partitionLoads[i];
                    minPartition = i;
                }
            }

            _communityToPartition[community.Key] = minPartition;
            partitionLoads[minPartition] += community.Count();
        }
    }

    /// <summary>
    /// Gets the community assignment for a vertex.
    /// </summary>
    /// <param name="vertexId">Vertex identifier.</param>
    /// <returns>Community ID, or -1 if not assigned.</returns>
    public int GetCommunity(string vertexId)
    {
        return _communityAssignments.TryGetValue(vertexId, out var community) ? community : -1;
    }
}

/// <summary>
/// Community detection algorithm type.
/// </summary>
public enum CommunityDetectionAlgorithm
{
    /// <summary>Label propagation - fast streaming algorithm.</summary>
    LabelPropagation,
    /// <summary>Louvain algorithm - modularity optimization.</summary>
    Louvain,
    /// <summary>Infomap - information-theoretic approach.</summary>
    Infomap,
    /// <summary>Spectral clustering.</summary>
    Spectral
}

/// <summary>
/// Two-dimensional grid partitioning for geo-spatial graphs.
/// Partitions based on spatial coordinates.
/// </summary>
public sealed class GridPartitioningStrategy : GraphPartitioningStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "graph.partition.grid";

    /// <inheritdoc/>
    public override string Name => "Grid-Based Spatial Graph Partitioning";

    /// <summary>Gets or sets the number of columns in the grid.</summary>
    public int GridColumns { get; set; } = 2;

    /// <summary>Gets or sets the number of rows in the grid.</summary>
    public int GridRows { get; set; } = 2;

    /// <summary>Gets or sets the minimum latitude.</summary>
    public double MinLatitude { get; set; } = -90.0;

    /// <summary>Gets or sets the maximum latitude.</summary>
    public double MaxLatitude { get; set; } = 90.0;

    /// <summary>Gets or sets the minimum longitude.</summary>
    public double MinLongitude { get; set; } = -180.0;

    /// <summary>Gets or sets the maximum longitude.</summary>
    public double MaxLongitude { get; set; } = 180.0;

    /// <summary>
    /// Initializes the strategy with grid dimensions.
    /// </summary>
    public GridPartitioningStrategy()
    {
        PartitionCount = GridColumns * GridRows;
    }

    /// <inheritdoc/>
    public override int AssignVertexPartition(string vertexId, IDictionary<string, object>? properties = null)
    {
        double lat = 0, lon = 0;

        if (properties != null)
        {
            if (properties.TryGetValue("latitude", out var latObj))
                lat = Convert.ToDouble(latObj);
            if (properties.TryGetValue("longitude", out var lonObj))
                lon = Convert.ToDouble(lonObj);
        }

        // Normalize coordinates to grid cell
        var latNorm = Math.Clamp((lat - MinLatitude) / (MaxLatitude - MinLatitude), 0, 0.9999);
        var lonNorm = Math.Clamp((lon - MinLongitude) / (MaxLongitude - MinLongitude), 0, 0.9999);

        int row = (int)(latNorm * GridRows);
        int col = (int)(lonNorm * GridColumns);

        return row * GridColumns + col;
    }

    /// <inheritdoc/>
    public override IEnumerable<int> AssignEdgePartitions(
        string edgeId,
        string sourceVertexId,
        string targetVertexId,
        IDictionary<string, object>? properties = null)
    {
        yield return AssignVertexPartition(sourceVertexId, properties);
    }

    /// <summary>
    /// Gets adjacent partition indices for spatial queries.
    /// </summary>
    /// <param name="partition">Center partition index.</param>
    /// <returns>Adjacent partition indices.</returns>
    public IEnumerable<int> GetAdjacentPartitions(int partition)
    {
        int row = partition / GridColumns;
        int col = partition % GridColumns;

        for (int dr = -1; dr <= 1; dr++)
        {
            for (int dc = -1; dc <= 1; dc++)
            {
                int newRow = row + dr;
                int newCol = col + dc;

                if (newRow >= 0 && newRow < GridRows && newCol >= 0 && newCol < GridColumns)
                {
                    yield return newRow * GridColumns + newCol;
                }
            }
        }
    }
}

/// <summary>
/// Registry for graph partitioning strategies.
/// </summary>
public sealed class GraphPartitioningStrategyRegistry
{
    private readonly BoundedDictionary<string, GraphPartitioningStrategyBase> _strategies = new BoundedDictionary<string, GraphPartitioningStrategyBase>(1000);

    /// <summary>
    /// Registers a partitioning strategy.
    /// </summary>
    /// <param name="strategy">Strategy to register.</param>
    public void Register(GraphPartitioningStrategyBase strategy)
    {
        _strategies[strategy.StrategyId] = strategy;
    }

    /// <summary>
    /// Gets a strategy by ID.
    /// </summary>
    /// <param name="strategyId">Strategy identifier.</param>
    /// <returns>The strategy, or null if not found.</returns>
    public GraphPartitioningStrategyBase? Get(string strategyId)
    {
        return _strategies.TryGetValue(strategyId, out var strategy) ? strategy : null;
    }

    /// <summary>
    /// Gets all registered strategies.
    /// </summary>
    public IEnumerable<GraphPartitioningStrategyBase> GetAll()
    {
        return _strategies.Values;
    }

    /// <summary>
    /// Creates a registry with all built-in strategies.
    /// </summary>
    public static GraphPartitioningStrategyRegistry CreateDefault()
    {
        var registry = new GraphPartitioningStrategyRegistry();
        registry.Register(new HashPartitioningStrategy());
        registry.Register(new RangePartitioningStrategy());
        registry.Register(new EdgeCutPartitioningStrategy());
        registry.Register(new VertexCutPartitioningStrategy());
        registry.Register(new CommunityPartitioningStrategy());
        registry.Register(new GridPartitioningStrategy());
        return registry;
    }
}
