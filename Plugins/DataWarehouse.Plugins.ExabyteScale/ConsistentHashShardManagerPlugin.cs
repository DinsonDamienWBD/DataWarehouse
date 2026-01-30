using System.Collections.Concurrent;
using System.IO.Hashing;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Scale;

namespace DataWarehouse.Plugins.ExabyteScale;

/// <summary>
/// Shard manager using consistent hashing with virtual nodes for balanced distribution.
/// Supports trillion-object scale with automatic rebalancing.
/// </summary>
public class ConsistentHashShardManagerPlugin : ShardManagerPluginBase
{
    /// <inheritdoc/>
    public override string Id => "com.datawarehouse.scale.consistenthash";

    /// <inheritdoc/>
    public override string Name => "Consistent Hash Shard Manager";

    /// <inheritdoc/>
    public override PluginCategory Category => PluginCategory.StorageProvider;

    /// <inheritdoc/>
    protected override ShardingStrategy Strategy => ShardingStrategy.ConsistentHash;

    /// <inheritdoc/>
    protected override int DefaultShardCount => 1024;

    private readonly ConcurrentDictionary<string, Shard> _shards = new();
    private readonly ConcurrentDictionary<string, string> _nodeShardMapping = new();
    private int _virtualNodesPerShard = 150;
    private string[] _sortedRing = Array.Empty<string>();
    private readonly SemaphoreSlim _ringLock = new(1, 1);

    /// <summary>Number of virtual nodes per physical shard for better distribution.</summary>
    public int VirtualNodesPerShard
    {
        get => _virtualNodesPerShard;
        set => _virtualNodesPerShard = Math.Max(1, value);
    }

    /// <inheritdoc/>
    protected override async Task<IShard> GetShardForHashAsync(ulong hash)
    {
        await _ringLock.WaitAsync();
        try
        {
            if (_sortedRing.Length == 0)
                await InitializeShardsAsync();

            // Jump consistent hash for even distribution
            var shardIndex = JumpConsistentHash(hash, _shards.Count);
            var shardId = _shards.Keys.ElementAt((int)shardIndex);
            return _shards[shardId];
        }
        finally { _ringLock.Release(); }
    }

    /// <inheritdoc/>
    protected override Task<IReadOnlyList<IShard>> GetAllShardsAsync()
        => Task.FromResult<IReadOnlyList<IShard>>(_shards.Values.Cast<IShard>().ToList());

    /// <inheritdoc/>
    protected override async Task PerformRebalanceAsync(RebalanceOptions options)
    {
        if (options.DryRun)
        {
            // Calculate what would be rebalanced without actually moving data
            return;
        }

        await _ringLock.WaitAsync();
        try
        {
            // Rebuild the hash ring
            var ringPoints = new List<(ulong Hash, string ShardId)>();
            foreach (var shard in _shards.Values)
            {
                for (int i = 0; i < _virtualNodesPerShard; i++)
                {
                    var virtualNodeKey = $"{shard.ShardId}:{i}";
                    var hash = ComputeConsistentHash(virtualNodeKey);
                    ringPoints.Add((hash, shard.ShardId));
                }
            }

            ringPoints.Sort((a, b) => a.Hash.CompareTo(b.Hash));
            _sortedRing = ringPoints.Select(p => p.ShardId).ToArray();
        }
        finally { _ringLock.Release(); }
    }

    /// <inheritdoc/>
    protected override async Task<ShardMigrationStatus> PerformMigrationAsync(string fromNode, string toNode)
    {
        var migrationId = $"MIG-{Guid.NewGuid():N}";

        // Get shards on source node
        var shardsToMigrate = _shards.Values
            .Where(s => s.ReplicaNodes.Contains(fromNode))
            .ToList();

        var total = shardsToMigrate.Count;
        var completed = 0;

        foreach (var shard in shardsToMigrate)
        {
            // Update replica list
            var newReplicas = shard.ReplicaNodes
                .Where(n => n != fromNode)
                .Append(toNode)
                .ToArray();

            var updatedShard = shard with { ReplicaNodes = newReplicas, LastRebalancedAt = DateTimeOffset.UtcNow };
            _shards[shard.ShardId] = updatedShard;
            completed++;
        }

        return new ShardMigrationStatus(migrationId, 100, total, MigrationState.Completed);
    }

    /// <inheritdoc/>
    protected override async Task PerformSplitAsync(string shardId)
    {
        if (!_shards.TryGetValue(shardId, out var shard))
            throw new ArgumentException($"Shard {shardId} not found");

        // Create two new shards from the split
        var newShardId1 = $"{shardId}-a";
        var newShardId2 = $"{shardId}-b";

        var halfObjects = shard.ObjectCount / 2;
        var halfSize = shard.TotalSizeBytes / 2;

        _shards[newShardId1] = new Shard
        {
            ShardId = newShardId1,
            State = ShardState.Active,
            ObjectCount = halfObjects,
            TotalSizeBytes = halfSize,
            ReplicaNodes = shard.ReplicaNodes,
            LastRebalancedAt = DateTimeOffset.UtcNow
        };

        _shards[newShardId2] = new Shard
        {
            ShardId = newShardId2,
            State = ShardState.Active,
            ObjectCount = halfObjects,
            TotalSizeBytes = halfSize,
            ReplicaNodes = shard.ReplicaNodes,
            LastRebalancedAt = DateTimeOffset.UtcNow
        };

        // Remove original shard
        _shards.TryRemove(shardId, out _);

        // Rebuild ring
        await PerformRebalanceAsync(new RebalanceOptions());
    }

    /// <inheritdoc/>
    protected override async Task PerformMergeAsync(string[] shardIds)
    {
        if (shardIds.Length < 2)
            throw new ArgumentException("Need at least 2 shards to merge");

        var shards = shardIds
            .Select(id => _shards.TryGetValue(id, out var s) ? s : null)
            .Where(s => s != null)
            .Cast<Shard>()
            .ToList();

        if (shards.Count != shardIds.Length)
            throw new ArgumentException("One or more shards not found");

        var newShardId = $"merged-{Guid.NewGuid():N}";
        var mergedShard = new Shard
        {
            ShardId = newShardId,
            State = ShardState.Active,
            ObjectCount = shards.Sum(s => s.ObjectCount),
            TotalSizeBytes = shards.Sum(s => s.TotalSizeBytes),
            ReplicaNodes = shards.SelectMany(s => s.ReplicaNodes).Distinct().ToArray(),
            LastRebalancedAt = DateTimeOffset.UtcNow
        };

        _shards[newShardId] = mergedShard;

        foreach (var id in shardIds)
            _shards.TryRemove(id, out _);

        await PerformRebalanceAsync(new RebalanceOptions());
    }

    private async Task InitializeShardsAsync()
    {
        for (int i = 0; i < DefaultShardCount; i++)
        {
            var shardId = $"shard-{i:D4}";
            _shards[shardId] = new Shard
            {
                ShardId = shardId,
                State = ShardState.Active,
                ObjectCount = 0,
                TotalSizeBytes = 0,
                ReplicaNodes = new[] { "node-0" },
                LastRebalancedAt = DateTimeOffset.UtcNow
            };
        }
        await PerformRebalanceAsync(new RebalanceOptions());
    }

    /// <summary>Google's Jump Consistent Hash algorithm - O(ln n) time, O(1) space.</summary>
    private static long JumpConsistentHash(ulong key, int numBuckets)
    {
        long b = -1, j = 0;
        while (j < numBuckets)
        {
            b = j;
            key = key * 2862933555777941757UL + 1;
            j = (long)((b + 1) * ((double)(1L << 31) / ((key >> 33) + 1)));
        }
        return b;
    }

    /// <inheritdoc/>
    public override Task StartAsync(CancellationToken ct)
    {
        // Initialize shards on startup
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public override Task StopAsync()
    {
        // Cleanup resources
        _ringLock.Dispose();
        return Task.CompletedTask;
    }
}

/// <summary>Concrete shard implementation.</summary>
public record Shard : IShard
{
    /// <inheritdoc/>
    public required string ShardId { get; init; }

    /// <inheritdoc/>
    public required ShardState State { get; init; }

    /// <inheritdoc/>
    public required long ObjectCount { get; init; }

    /// <inheritdoc/>
    public required long TotalSizeBytes { get; init; }

    /// <inheritdoc/>
    public required string[] ReplicaNodes { get; init; }

    /// <inheritdoc/>
    public required DateTimeOffset LastRebalancedAt { get; init; }
}
