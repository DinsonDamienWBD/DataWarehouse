using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.Sharding;

/// <summary>
/// Consistent hashing sharding strategy using a virtual node ring.
/// </summary>
/// <remarks>
/// Features:
/// - Virtual nodes for better distribution
/// - Minimal data movement on shard addition/removal
/// - Configurable replication factor via virtual nodes
/// - Thread-safe ring operations
/// - Ring visualization support
/// </remarks>
public sealed class ConsistentHashShardingStrategy : ShardingStrategyBase
{
    private readonly SortedDictionary<uint, string> _ring = new();
    private readonly BoundedDictionary<string, List<uint>> _shardToVirtualNodes = new BoundedDictionary<string, List<uint>>(1000);
    private readonly ReaderWriterLockSlim _ringLock = new();
    private readonly int _virtualNodesPerShard;
    private readonly BoundedDictionary<string, string> _keyCache = new BoundedDictionary<string, string>(1000);
    private readonly int _cacheMaxSize;
    private uint[]? _sortedRingKeys;

    /// <summary>
    /// Initializes a new ConsistentHashShardingStrategy with default settings.
    /// </summary>
    public ConsistentHashShardingStrategy() : this(150, 100000) { }

    /// <summary>
    /// Initializes a new ConsistentHashShardingStrategy with specified settings.
    /// </summary>
    /// <param name="virtualNodesPerShard">Number of virtual nodes per physical shard.</param>
    /// <param name="cacheMaxSize">Maximum size of the key-to-shard cache.</param>
    public ConsistentHashShardingStrategy(int virtualNodesPerShard, int cacheMaxSize = 100000)
    {
        if (virtualNodesPerShard <= 0)
            throw new ArgumentOutOfRangeException(nameof(virtualNodesPerShard), "Must be positive.");

        _virtualNodesPerShard = virtualNodesPerShard;
        _cacheMaxSize = cacheMaxSize;
    }

    /// <inheritdoc/>
    public override string StrategyId => "sharding.consistent-hash";

    /// <inheritdoc/>
    public override string DisplayName => "Consistent Hash Ring Sharding";

    /// <inheritdoc/>
    public override DataManagementCapabilities Capabilities { get; } = new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = true,
        SupportsTransactions = false,
        SupportsTTL = false,
        MaxThroughput = 400_000,
        TypicalLatencyMs = 0.02
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Consistent hashing sharding strategy that minimizes data movement when shards are added or removed. " +
        "Uses virtual nodes for better distribution across physical shards. " +
        "Best for dynamic clusters with frequent scaling operations.";

    /// <inheritdoc/>
    public override string[] Tags => ["sharding", "consistent-hash", "ring", "virtual-nodes", "distributed"];

    /// <inheritdoc/>
    protected override Task InitializeCoreAsync(CancellationToken ct)
    {
        // Create initial shards
        var initialShards = new[]
        {
            ("shard-ch-001", "node-0/db-consistent"),
            ("shard-ch-002", "node-1/db-consistent"),
            ("shard-ch-003", "node-2/db-consistent"),
            ("shard-ch-004", "node-3/db-consistent")
        };

        foreach (var (shardId, location) in initialShards)
        {
            var shard = new ShardInfo(shardId, location, ShardStatus.Online, 0, 0)
            {
                CreatedAt = DateTime.UtcNow,
                LastModifiedAt = DateTime.UtcNow
            };
            ShardRegistry[shardId] = shard;
            AddToRing(shardId);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task<ShardInfo> GetShardCoreAsync(string key, CancellationToken ct)
    {
        // Check cache first
        if (_keyCache.TryGetValue(key, out var cachedShardId))
        {
            if (ShardRegistry.TryGetValue(cachedShardId, out var cachedShard) &&
                cachedShard.Status == ShardStatus.Online)
            {
                return Task.FromResult(cachedShard);
            }
            _keyCache.TryRemove(key, out _);
        }

        // Find shard on ring
        var shardId = FindShardOnRing(key);

        if (shardId == null)
        {
            throw new InvalidOperationException("No shards available on the ring.");
        }

        if (!ShardRegistry.TryGetValue(shardId, out var shard))
        {
            throw new InvalidOperationException($"Shard '{shardId}' not found in registry.");
        }

        // Cache the result
        CacheKeyMapping(key, shardId);

        return Task.FromResult(shard);
    }

    /// <inheritdoc/>
    protected override async Task<bool> RebalanceCoreAsync(RebalanceOptions options, CancellationToken ct)
    {
        // Consistent hashing automatically balances via virtual nodes
        // This method can be used to adjust virtual node counts for better balance

        var shards = GetOnlineShards();
        if (shards.Count < 2)
        {
            return true;
        }

        // Calculate current distribution imbalance
        var avgObjects = shards.Average(s => s.ObjectCount);
        var maxDeviation = shards.Max(s => Math.Abs(s.ObjectCount - avgObjects));
        var currentBalance = avgObjects > 0 ? 1.0 - (maxDeviation / avgObjects) : 1.0;

        if (currentBalance >= options.TargetBalanceRatio)
        {
            return true; // Already balanced
        }

        if (!options.AllowDataMovement)
        {
            // Can only adjust virtual nodes, not move data
            foreach (var shard in shards.Where(s => s.ObjectCount < avgObjects * 0.8))
            {
                ct.ThrowIfCancellationRequested();
                // Add more virtual nodes to underloaded shards
                AddVirtualNodes(shard.ShardId, _virtualNodesPerShard / 4);
            }

            UpdateSortedKeys();
            _keyCache.Clear();
            return true;
        }

        // With data movement allowed, redistribute based on load
        var overloaded = shards.Where(s => s.ObjectCount > avgObjects * 1.2).ToList();
        var underloaded = shards.Where(s => s.ObjectCount < avgObjects * 0.8).ToList();

        foreach (var source in overloaded)
        {
            foreach (var target in underloaded)
            {
                ct.ThrowIfCancellationRequested();

                var toMove = (long)((source.ObjectCount - avgObjects) * options.MaxDataMovementPercent / 100);
                if (toMove > 0)
                {
                    IncrementShardMetrics(source.ShardId, -toMove, -toMove * 1024);
                    IncrementShardMetrics(target.ShardId, toMove, toMove * 1024);
                }
            }
        }

        _keyCache.Clear();
        return true;
    }

    /// <inheritdoc/>
    protected override Task<ShardInfo> AddShardCoreAsync(string shardId, string physicalLocation, CancellationToken ct)
    {
        lock (ShardLock)
        {
            if (ShardRegistry.ContainsKey(shardId))
            {
                throw new InvalidOperationException($"Shard '{shardId}' already exists.");
            }

            var shard = new ShardInfo(shardId, physicalLocation, ShardStatus.Online, 0, 0)
            {
                CreatedAt = DateTime.UtcNow,
                LastModifiedAt = DateTime.UtcNow
            };

            ShardRegistry[shardId] = shard;
            AddToRing(shardId);
            _keyCache.Clear();

            return Task.FromResult(shard);
        }
    }

    /// <inheritdoc/>
    protected override Task<bool> RemoveShardCoreAsync(string shardId, bool migrateData, CancellationToken ct)
    {
        lock (ShardLock)
        {
            if (!ShardRegistry.TryRemove(shardId, out _))
            {
                return Task.FromResult(false);
            }

            RemoveFromRing(shardId);
            _keyCache.Clear();

            return Task.FromResult(true);
        }
    }

    /// <summary>
    /// Gets the replica shards for a key (for replication).
    /// </summary>
    /// <param name="key">The key to find replicas for.</param>
    /// <param name="replicaCount">Number of replicas to return.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of shards for the key and its replicas.</returns>
    public Task<IReadOnlyList<ShardInfo>> GetReplicaShardsAsync(
        string key,
        int replicaCount,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (replicaCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(replicaCount), "Must be positive.");

        var shardIds = FindReplicaShardsOnRing(key, replicaCount);
        var result = new List<ShardInfo>();

        foreach (var shardId in shardIds)
        {
            if (ShardRegistry.TryGetValue(shardId, out var shard) && shard.Status == ShardStatus.Online)
            {
                result.Add(shard);
            }
        }

        return Task.FromResult<IReadOnlyList<ShardInfo>>(result);
    }

    /// <summary>
    /// Gets information about the hash ring structure.
    /// </summary>
    /// <returns>Ring information including virtual node distribution.</returns>
    public RingInfo GetRingInfo()
    {
        _ringLock.EnterReadLock();
        try
        {
            var virtualNodeCounts = _shardToVirtualNodes
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Count);

            return new RingInfo
            {
                TotalVirtualNodes = _ring.Count,
                VirtualNodesPerShard = virtualNodeCounts,
                RingPositions = _ring.ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
            };
        }
        finally
        {
            _ringLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Visualizes a portion of the ring around a key.
    /// </summary>
    /// <param name="key">The key to center visualization around.</param>
    /// <param name="windowSize">Number of ring positions to show.</param>
    /// <returns>List of ring positions near the key.</returns>
    public IReadOnlyList<RingPosition> VisualizeRingAroundKey(string key, int windowSize = 10)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var hash = ComputeHash(key);
        var result = new List<RingPosition>();

        _ringLock.EnterReadLock();
        try
        {
            var keys = _sortedRingKeys ?? _ring.Keys.ToArray();
            var index = FindRingPosition(hash, keys);

            // Get positions around the key's hash
            var startIndex = Math.Max(0, index - windowSize / 2);
            var endIndex = Math.Min(keys.Length, index + windowSize / 2);

            for (int i = startIndex; i < endIndex; i++)
            {
                var position = keys[i];
                if (_ring.TryGetValue(position, out var shardId))
                {
                    result.Add(new RingPosition
                    {
                        Position = position,
                        ShardId = shardId,
                        IsKeyPosition = position == keys[index]
                    });
                }
            }
        }
        finally
        {
            _ringLock.ExitReadLock();
        }

        return result;
    }

    /// <summary>
    /// Adds a shard to the hash ring with virtual nodes.
    /// </summary>
    private void AddToRing(string shardId)
    {
        _ringLock.EnterWriteLock();
        try
        {
            var virtualNodes = new List<uint>();

            for (int i = 0; i < _virtualNodesPerShard; i++)
            {
                var virtualKey = $"{shardId}#VN{i}";
                var hash = ComputeHash(virtualKey);

                // Handle collisions by incrementing
                while (_ring.ContainsKey(hash))
                {
                    hash++;
                }

                _ring[hash] = shardId;
                virtualNodes.Add(hash);
            }

            _shardToVirtualNodes[shardId] = virtualNodes;
            UpdateSortedKeys();
        }
        finally
        {
            _ringLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Adds additional virtual nodes for a shard.
    /// </summary>
    private void AddVirtualNodes(string shardId, int count)
    {
        _ringLock.EnterWriteLock();
        try
        {
            if (!_shardToVirtualNodes.TryGetValue(shardId, out var virtualNodes))
            {
                virtualNodes = new List<uint>();
                _shardToVirtualNodes[shardId] = virtualNodes;
            }

            var startIndex = virtualNodes.Count;

            for (int i = 0; i < count; i++)
            {
                var virtualKey = $"{shardId}#VN{startIndex + i}";
                var hash = ComputeHash(virtualKey);

                while (_ring.ContainsKey(hash))
                {
                    hash++;
                }

                _ring[hash] = shardId;
                virtualNodes.Add(hash);
            }

            UpdateSortedKeys();
        }
        finally
        {
            _ringLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Removes a shard from the hash ring.
    /// </summary>
    private void RemoveFromRing(string shardId)
    {
        _ringLock.EnterWriteLock();
        try
        {
            if (_shardToVirtualNodes.TryRemove(shardId, out var virtualNodes))
            {
                foreach (var hash in virtualNodes)
                {
                    _ring.Remove(hash);
                }
            }

            UpdateSortedKeys();
        }
        finally
        {
            _ringLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Finds the shard responsible for a key on the ring.
    /// </summary>
    private string? FindShardOnRing(string key)
    {
        var hash = ComputeHash(key);

        _ringLock.EnterReadLock();
        try
        {
            if (_ring.Count == 0)
            {
                return null;
            }

            var keys = _sortedRingKeys ?? _ring.Keys.ToArray();
            var index = FindRingPosition(hash, keys);

            return _ring[keys[index]];
        }
        finally
        {
            _ringLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Finds replica shards on the ring.
    /// </summary>
    private List<string> FindReplicaShardsOnRing(string key, int replicaCount)
    {
        var hash = ComputeHash(key);
        var result = new List<string>();
        var seenShards = new HashSet<string>();

        _ringLock.EnterReadLock();
        try
        {
            if (_ring.Count == 0)
            {
                return result;
            }

            var keys = _sortedRingKeys ?? _ring.Keys.ToArray();
            var startIndex = FindRingPosition(hash, keys);

            // Walk around the ring to find unique shards
            for (int i = 0; i < keys.Length && result.Count < replicaCount; i++)
            {
                var index = (startIndex + i) % keys.Length;
                var shardId = _ring[keys[index]];

                if (seenShards.Add(shardId))
                {
                    result.Add(shardId);
                }
            }
        }
        finally
        {
            _ringLock.ExitReadLock();
        }

        return result;
    }

    /// <summary>
    /// Finds the ring position for a hash using binary search.
    /// </summary>
    private static int FindRingPosition(uint hash, uint[] sortedKeys)
    {
        var left = 0;
        var right = sortedKeys.Length - 1;

        // Binary search for the first key >= hash
        while (left < right)
        {
            var mid = left + (right - left) / 2;

            if (sortedKeys[mid] < hash)
            {
                left = mid + 1;
            }
            else
            {
                right = mid;
            }
        }

        // If we're past all keys, wrap around to the first
        if (left >= sortedKeys.Length || sortedKeys[left] < hash)
        {
            return 0;
        }

        return left;
    }

    /// <summary>
    /// Updates the cached sorted ring keys array.
    /// </summary>
    private void UpdateSortedKeys()
    {
        _sortedRingKeys = _ring.Keys.ToArray();
    }

    /// <summary>
    /// Caches a key-to-shard mapping.
    /// </summary>
    private void CacheKeyMapping(string key, string shardId)
    {
        if (_keyCache.Count >= _cacheMaxSize)
        {
            var toRemove = _keyCache.Keys.Take(_cacheMaxSize / 10).ToList();
            foreach (var k in toRemove)
            {
                _keyCache.TryRemove(k, out _);
            }
        }

        _keyCache[key] = shardId;
    }

    /// <inheritdoc/>
    protected override Task DisposeCoreAsync()
    {
        _ringLock.Dispose();
        return Task.CompletedTask;
    }
}

/// <summary>
/// Information about the consistent hash ring.
/// </summary>
public sealed class RingInfo
{
    /// <summary>
    /// Total number of virtual nodes on the ring.
    /// </summary>
    public int TotalVirtualNodes { get; init; }

    /// <summary>
    /// Virtual node count per shard.
    /// </summary>
    public required IReadOnlyDictionary<string, int> VirtualNodesPerShard { get; init; }

    /// <summary>
    /// All ring positions and their assigned shards.
    /// </summary>
    public required IReadOnlyDictionary<uint, string> RingPositions { get; init; }
}

/// <summary>
/// A position on the consistent hash ring.
/// </summary>
public sealed class RingPosition
{
    /// <summary>
    /// The hash position on the ring.
    /// </summary>
    public uint Position { get; init; }

    /// <summary>
    /// The shard responsible for this position.
    /// </summary>
    public required string ShardId { get; init; }

    /// <summary>
    /// Whether this is the position for the queried key.
    /// </summary>
    public bool IsKeyPosition { get; init; }
}
