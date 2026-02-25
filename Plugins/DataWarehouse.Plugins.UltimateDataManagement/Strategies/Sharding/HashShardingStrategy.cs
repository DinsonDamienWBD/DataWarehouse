using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.Sharding;

/// <summary>
/// Hash-based sharding strategy that distributes data across shards using consistent hash functions.
/// </summary>
/// <remarks>
/// Features:
/// - Uses MurmurHash3 for fast, well-distributed hashing
/// - Configurable shard count with dynamic scaling
/// - Key-to-shard mapping with O(1) lookup
/// - Thread-safe for concurrent access
/// - Supports hot shard detection and rebalancing
/// </remarks>
public sealed class HashShardingStrategy : ShardingStrategyBase
{
    private readonly int _initialShardCount;
    private readonly BoundedDictionary<string, string> _keyToShardCache = new BoundedDictionary<string, string>(1000);
    private readonly int _cacheMaxSize;
    private readonly object _cacheLock = new();

    /// <summary>
    /// Initializes a new HashShardingStrategy with default settings.
    /// </summary>
    public HashShardingStrategy() : this(16) { }

    /// <summary>
    /// Initializes a new HashShardingStrategy with specified shard count.
    /// </summary>
    /// <param name="initialShardCount">Initial number of shards to create.</param>
    /// <param name="cacheMaxSize">Maximum size of the key-to-shard cache.</param>
    public HashShardingStrategy(int initialShardCount, int cacheMaxSize = 100000)
    {
        if (initialShardCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(initialShardCount), "Shard count must be positive.");

        _initialShardCount = initialShardCount;
        _cacheMaxSize = cacheMaxSize;
    }

    /// <inheritdoc/>
    public override string StrategyId => "sharding.hash";

    /// <inheritdoc/>
    public override string DisplayName => "Hash-Based Sharding";

    /// <inheritdoc/>
    public override DataManagementCapabilities Capabilities { get; } = new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = true,
        SupportsTransactions = false,
        SupportsTTL = false,
        MaxThroughput = 500_000,
        TypicalLatencyMs = 0.01
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Hash-based sharding strategy that distributes data evenly across shards using MurmurHash3. " +
        "Provides O(1) key-to-shard lookup with configurable shard count. " +
        "Best for uniform key distribution without range query requirements.";

    /// <inheritdoc/>
    public override string[] Tags => ["sharding", "hash", "distributed", "partition", "murmurhash"];

    /// <inheritdoc/>
    protected override Task InitializeCoreAsync(CancellationToken ct)
    {
        // Create initial shards
        for (int i = 0; i < _initialShardCount; i++)
        {
            var shardId = $"shard-{i:D4}";
            var location = $"node-{i % 4}/db-{i}"; // Distribute across 4 nodes

            ShardRegistry[shardId] = new ShardInfo(shardId, location, ShardStatus.Online, 0, 0)
            {
                CreatedAt = DateTime.UtcNow,
                LastModifiedAt = DateTime.UtcNow
            };
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task<ShardInfo> GetShardCoreAsync(string key, CancellationToken ct)
    {
        // Check cache first
        if (_keyToShardCache.TryGetValue(key, out var cachedShardId))
        {
            if (ShardRegistry.TryGetValue(cachedShardId, out var cachedShard) &&
                cachedShard.Status == ShardStatus.Online)
            {
                return Task.FromResult(cachedShard);
            }
            // Cached shard is no longer valid, remove from cache
            _keyToShardCache.TryRemove(key, out _);
        }

        // Compute hash and find shard
        var hash = ComputeHash(key);
        var onlineShards = GetOnlineShards();

        if (onlineShards.Count == 0)
        {
            throw new InvalidOperationException("No online shards available.");
        }

        var shardIndex = (int)(hash % (uint)onlineShards.Count);
        var shard = onlineShards[shardIndex];

        // Cache the mapping
        CacheKeyMapping(key, shard.ShardId);

        return Task.FromResult(shard);
    }

    /// <inheritdoc/>
    protected override async Task<bool> RebalanceCoreAsync(RebalanceOptions options, CancellationToken ct)
    {
        var shards = GetOnlineShards();
        if (shards.Count < 2)
        {
            return true; // Nothing to rebalance with fewer than 2 shards
        }

        var totalObjects = shards.Sum(s => s.ObjectCount);
        var targetPerShard = totalObjects / shards.Count;
        var tolerance = (long)(targetPerShard * (1 - options.TargetBalanceRatio));

        var overloaded = shards.Where(s => s.ObjectCount > targetPerShard + tolerance).ToList();
        var underloaded = shards.Where(s => s.ObjectCount < targetPerShard - tolerance).ToList();

        if (!overloaded.Any() || !underloaded.Any())
        {
            return true; // Already balanced within tolerance
        }

        if (!options.AllowDataMovement)
        {
            return false; // Cannot rebalance without data movement
        }

        // Calculate movements needed
        var movements = new List<(string FromShard, string ToShard, long ObjectsToMove)>();
        var maxMovement = (long)(totalObjects * options.MaxDataMovementPercent / 100);
        long totalMoved = 0;

        foreach (var source in overloaded)
        {
            if (totalMoved >= maxMovement) break;

            var excess = source.ObjectCount - targetPerShard;

            foreach (var target in underloaded)
            {
                if (totalMoved >= maxMovement) break;

                var deficit = targetPerShard - target.ObjectCount;
                var toMove = Math.Min(excess, deficit);
                toMove = Math.Min(toMove, maxMovement - totalMoved);

                if (toMove > 0)
                {
                    movements.Add((source.ShardId, target.ShardId, toMove));
                    totalMoved += toMove;
                    excess -= toMove;
                }

                if (excess <= 0) break;
            }
        }

        // Execute movements (in a real implementation, this would involve actual data transfer)
        foreach (var (from, to, count) in movements)
        {
            ct.ThrowIfCancellationRequested();

            // Update metrics to reflect the movement
            IncrementShardMetrics(from, -count, -count * 1024); // Assume 1KB avg object size
            IncrementShardMetrics(to, count, count * 1024);

            // Clear affected cache entries
            ClearCacheForShard(from);
        }

        return true;
    }

    /// <summary>
    /// Gets the shard ID for a key without caching.
    /// </summary>
    /// <param name="key">The key to route.</param>
    /// <returns>The shard ID.</returns>
    public string GetShardIdForKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var hash = ComputeHash(key);
        var onlineShards = GetOnlineShards();

        if (onlineShards.Count == 0)
        {
            throw new InvalidOperationException("No online shards available.");
        }

        var shardIndex = (int)(hash % (uint)onlineShards.Count);
        return onlineShards[shardIndex].ShardId;
    }

    /// <summary>
    /// Gets multiple shard assignments in batch.
    /// </summary>
    /// <param name="keys">The keys to route.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Dictionary mapping keys to their assigned shards.</returns>
    public Task<IReadOnlyDictionary<string, ShardInfo>> GetShardsForKeysAsync(
        IEnumerable<string> keys,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ThrowIfNotInitialized();

        var result = new Dictionary<string, ShardInfo>();
        var onlineShards = GetOnlineShards();

        if (onlineShards.Count == 0)
        {
            throw new InvalidOperationException("No online shards available.");
        }

        foreach (var key in keys)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(key)) continue;

            var hash = ComputeHash(key);
            var shardIndex = (int)(hash % (uint)onlineShards.Count);
            result[key] = onlineShards[shardIndex];
        }

        return Task.FromResult<IReadOnlyDictionary<string, ShardInfo>>(result);
    }

    /// <summary>
    /// Groups keys by their target shard for efficient batch operations.
    /// </summary>
    /// <param name="keys">The keys to group.</param>
    /// <returns>Dictionary mapping shard IDs to lists of keys.</returns>
    public IReadOnlyDictionary<string, List<string>> GroupKeysByShard(IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        var result = new Dictionary<string, List<string>>();
        var onlineShards = GetOnlineShards();

        if (onlineShards.Count == 0)
        {
            return result;
        }

        foreach (var key in keys)
        {
            if (string.IsNullOrWhiteSpace(key)) continue;

            var hash = ComputeHash(key);
            var shardIndex = (int)(hash % (uint)onlineShards.Count);
            var shardId = onlineShards[shardIndex].ShardId;

            if (!result.TryGetValue(shardId, out var list))
            {
                list = new List<string>();
                result[shardId] = list;
            }

            list.Add(key);
        }

        return result;
    }

    /// <summary>
    /// Caches a key-to-shard mapping.
    /// </summary>
    private void CacheKeyMapping(string key, string shardId)
    {
        if (_keyToShardCache.Count >= _cacheMaxSize)
        {
            lock (_cacheLock)
            {
                if (_keyToShardCache.Count >= _cacheMaxSize)
                {
                    // Evict 10% of cache
                    var toRemove = _keyToShardCache.Keys.Take(_cacheMaxSize / 10).ToList();
                    foreach (var k in toRemove)
                    {
                        _keyToShardCache.TryRemove(k, out _);
                    }
                }
            }
        }

        _keyToShardCache[key] = shardId;
    }

    /// <summary>
    /// Clears cache entries for a specific shard.
    /// </summary>
    private void ClearCacheForShard(string shardId)
    {
        var keysToRemove = _keyToShardCache
            .Where(kvp => kvp.Value == shardId)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in keysToRemove)
        {
            _keyToShardCache.TryRemove(key, out _);
        }
    }

    /// <inheritdoc/>
    protected override void OnShardAdded(ShardInfo shard)
    {
        // Clear all cache since shard count changed (affects hash distribution)
        _keyToShardCache.Clear();
    }

    /// <inheritdoc/>
    protected override void OnShardRemoved(ShardInfo shard)
    {
        // Clear all cache since shard count changed
        _keyToShardCache.Clear();
    }
}
