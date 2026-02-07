using System.Collections.Concurrent;
using System.Diagnostics;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.Sharding;

/// <summary>
/// Status of a shard in the cluster.
/// </summary>
public enum ShardStatus
{
    /// <summary>
    /// Shard is online and accepting requests.
    /// </summary>
    Online,

    /// <summary>
    /// Shard is offline and not accepting requests.
    /// </summary>
    Offline,

    /// <summary>
    /// Shard is migrating data to another shard.
    /// </summary>
    Migrating,

    /// <summary>
    /// Shard is being split into multiple shards.
    /// </summary>
    Splitting,

    /// <summary>
    /// Shard is being merged with another shard.
    /// </summary>
    Merging
}

/// <summary>
/// Information about a shard.
/// </summary>
/// <param name="ShardId">Unique identifier for the shard.</param>
/// <param name="PhysicalLocation">Physical location or connection string for the shard.</param>
/// <param name="Status">Current status of the shard.</param>
/// <param name="ObjectCount">Number of objects stored in the shard.</param>
/// <param name="SizeBytes">Size of data stored in the shard in bytes.</param>
public record ShardInfo(
    string ShardId,
    string PhysicalLocation,
    ShardStatus Status,
    long ObjectCount,
    long SizeBytes)
{
    /// <summary>
    /// When the shard was created.
    /// </summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// When the shard was last modified.
    /// </summary>
    public DateTime LastModifiedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Additional metadata about the shard.
    /// </summary>
    public IReadOnlyDictionary<string, object>? Metadata { get; init; }
}

/// <summary>
/// Options for rebalancing shards.
/// </summary>
public sealed class RebalanceOptions
{
    /// <summary>
    /// Whether to allow data movement during rebalance.
    /// </summary>
    public bool AllowDataMovement { get; init; } = true;

    /// <summary>
    /// Maximum percentage of data to move in a single rebalance operation.
    /// </summary>
    public double MaxDataMovementPercent { get; init; } = 10.0;

    /// <summary>
    /// Target balance ratio (0-1, where 1 is perfectly balanced).
    /// </summary>
    public double TargetBalanceRatio { get; init; } = 0.9;

    /// <summary>
    /// Whether to perform rebalance asynchronously.
    /// </summary>
    public bool Async { get; init; } = true;

    /// <summary>
    /// Maximum duration for the rebalance operation.
    /// </summary>
    public TimeSpan? MaxDuration { get; init; }

    /// <summary>
    /// Whether to skip shards that are currently migrating/splitting/merging.
    /// </summary>
    public bool SkipBusyShards { get; init; } = true;
}

/// <summary>
/// Statistics about sharding operations.
/// </summary>
public sealed class ShardingStats
{
    /// <summary>
    /// Total number of shards.
    /// </summary>
    public int TotalShards { get; init; }

    /// <summary>
    /// Number of online shards.
    /// </summary>
    public int OnlineShards { get; init; }

    /// <summary>
    /// Number of offline shards.
    /// </summary>
    public int OfflineShards { get; init; }

    /// <summary>
    /// Total objects across all shards.
    /// </summary>
    public long TotalObjects { get; init; }

    /// <summary>
    /// Total size across all shards in bytes.
    /// </summary>
    public long TotalSizeBytes { get; init; }

    /// <summary>
    /// Average objects per shard.
    /// </summary>
    public double AverageObjectsPerShard => TotalShards > 0 ? (double)TotalObjects / TotalShards : 0;

    /// <summary>
    /// Average size per shard in bytes.
    /// </summary>
    public double AverageSizePerShard => TotalShards > 0 ? (double)TotalSizeBytes / TotalShards : 0;

    /// <summary>
    /// Balance ratio (0-1, where 1 is perfectly balanced).
    /// </summary>
    public double BalanceRatio { get; init; }

    /// <summary>
    /// Total routing operations performed.
    /// </summary>
    public long TotalRoutingOperations { get; init; }

    /// <summary>
    /// Total rebalance operations performed.
    /// </summary>
    public long TotalRebalanceOperations { get; init; }

    /// <summary>
    /// Average routing latency in milliseconds.
    /// </summary>
    public double AverageRoutingLatencyMs { get; init; }
}

/// <summary>
/// Result of a shard routing operation.
/// </summary>
public sealed class ShardRoutingResult
{
    /// <summary>
    /// Whether the routing was successful.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// The target shard.
    /// </summary>
    public ShardInfo? Shard { get; init; }

    /// <summary>
    /// Error message if routing failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Time taken for routing in milliseconds.
    /// </summary>
    public double RoutingTimeMs { get; init; }

    /// <summary>
    /// Creates a successful routing result.
    /// </summary>
    public static ShardRoutingResult Ok(ShardInfo shard, double routingTimeMs) =>
        new() { Success = true, Shard = shard, RoutingTimeMs = routingTimeMs };

    /// <summary>
    /// Creates a failed routing result.
    /// </summary>
    public static ShardRoutingResult Failed(string error) =>
        new() { Success = false, ErrorMessage = error };
}

/// <summary>
/// Interface for sharding strategies.
/// </summary>
public interface IShardingStrategy : IDataManagementStrategy
{
    /// <summary>
    /// Gets the shard for a given key.
    /// </summary>
    /// <param name="key">The key to route.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Information about the target shard.</returns>
    Task<ShardInfo> GetShardAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// Gets all shards in the cluster.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Collection of all shards.</returns>
    Task<IEnumerable<ShardInfo>> GetAllShardsAsync(CancellationToken ct = default);

    /// <summary>
    /// Rebalances data across shards.
    /// </summary>
    /// <param name="options">Rebalance options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if rebalance was successful.</returns>
    Task<bool> RebalanceAsync(RebalanceOptions options, CancellationToken ct = default);

    /// <summary>
    /// Gets statistics about sharding operations.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Sharding statistics.</returns>
    Task<ShardingStats> GetStatsAsync(CancellationToken ct = default);

    /// <summary>
    /// Adds a new shard to the cluster.
    /// </summary>
    /// <param name="shardId">Unique identifier for the new shard.</param>
    /// <param name="physicalLocation">Physical location or connection string.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Information about the added shard.</returns>
    Task<ShardInfo> AddShardAsync(string shardId, string physicalLocation, CancellationToken ct = default);

    /// <summary>
    /// Removes a shard from the cluster.
    /// </summary>
    /// <param name="shardId">The shard to remove.</param>
    /// <param name="migrateData">Whether to migrate data to other shards.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if removal was successful.</returns>
    Task<bool> RemoveShardAsync(string shardId, bool migrateData = true, CancellationToken ct = default);

    /// <summary>
    /// Updates the status of a shard.
    /// </summary>
    /// <param name="shardId">The shard to update.</param>
    /// <param name="status">The new status.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if update was successful.</returns>
    Task<bool> UpdateShardStatusAsync(string shardId, ShardStatus status, CancellationToken ct = default);
}

/// <summary>
/// Abstract base class for sharding strategies.
/// Provides common functionality for shard management, routing, and rebalancing.
/// </summary>
public abstract class ShardingStrategyBase : DataManagementStrategyBase, IShardingStrategy
{
    /// <summary>
    /// Thread-safe registry of shards.
    /// </summary>
    protected readonly ConcurrentDictionary<string, ShardInfo> ShardRegistry = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Lock for shard registry modifications.
    /// </summary>
    protected readonly object ShardLock = new();

    /// <summary>
    /// Counter for routing operations.
    /// </summary>
    private long _routingOperations;

    /// <summary>
    /// Counter for rebalance operations.
    /// </summary>
    private long _rebalanceOperations;

    /// <summary>
    /// Total routing time in milliseconds.
    /// </summary>
    private double _totalRoutingTimeMs;

    /// <summary>
    /// Lock for statistics.
    /// </summary>
    private readonly object _statsLock = new();

    /// <inheritdoc/>
    public override DataManagementCategory Category => DataManagementCategory.Lifecycle;

    /// <inheritdoc/>
    public async Task<ShardInfo> GetShardAsync(string key, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var sw = Stopwatch.StartNew();
        try
        {
            var shard = await GetShardCoreAsync(key, ct);
            sw.Stop();

            RecordRoutingOperation(sw.Elapsed.TotalMilliseconds);
            RecordRead(0, sw.Elapsed.TotalMilliseconds);

            return shard;
        }
        catch
        {
            sw.Stop();
            RecordFailure();
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<ShardInfo>> GetAllShardsAsync(CancellationToken ct = default)
    {
        ThrowIfNotInitialized();

        return await GetAllShardsCoreAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<bool> RebalanceAsync(RebalanceOptions options, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentNullException.ThrowIfNull(options);

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await RebalanceCoreAsync(options, ct);
            sw.Stop();

            RecordRebalanceOperation();
            RecordWrite(0, sw.Elapsed.TotalMilliseconds);

            return result;
        }
        catch
        {
            sw.Stop();
            RecordFailure();
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<ShardingStats> GetStatsAsync(CancellationToken ct = default)
    {
        ThrowIfNotInitialized();

        var shards = ShardRegistry.Values.ToList();
        var onlineShards = shards.Count(s => s.Status == ShardStatus.Online);
        var offlineShards = shards.Count(s => s.Status == ShardStatus.Offline);
        var totalObjects = shards.Sum(s => s.ObjectCount);
        var totalSize = shards.Sum(s => s.SizeBytes);

        double balanceRatio = 1.0;
        if (shards.Count > 1 && totalObjects > 0)
        {
            var avgObjects = (double)totalObjects / shards.Count;
            var maxDeviation = shards.Max(s => Math.Abs(s.ObjectCount - avgObjects));
            balanceRatio = Math.Max(0, 1.0 - (maxDeviation / avgObjects));
        }

        double avgRoutingLatency;
        long routingOps;
        lock (_statsLock)
        {
            routingOps = _routingOperations;
            avgRoutingLatency = routingOps > 0 ? _totalRoutingTimeMs / routingOps : 0;
        }

        return new ShardingStats
        {
            TotalShards = shards.Count,
            OnlineShards = onlineShards,
            OfflineShards = offlineShards,
            TotalObjects = totalObjects,
            TotalSizeBytes = totalSize,
            BalanceRatio = balanceRatio,
            TotalRoutingOperations = routingOps,
            TotalRebalanceOperations = Interlocked.Read(ref _rebalanceOperations),
            AverageRoutingLatencyMs = avgRoutingLatency
        };
    }

    /// <inheritdoc/>
    public async Task<ShardInfo> AddShardAsync(string shardId, string physicalLocation, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(shardId);
        ArgumentException.ThrowIfNullOrWhiteSpace(physicalLocation);

        return await AddShardCoreAsync(shardId, physicalLocation, ct);
    }

    /// <inheritdoc/>
    public async Task<bool> RemoveShardAsync(string shardId, bool migrateData = true, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(shardId);

        return await RemoveShardCoreAsync(shardId, migrateData, ct);
    }

    /// <inheritdoc/>
    public async Task<bool> UpdateShardStatusAsync(string shardId, ShardStatus status, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(shardId);

        return await UpdateShardStatusCoreAsync(shardId, status, ct);
    }

    /// <summary>
    /// Core implementation for getting a shard for a key.
    /// </summary>
    protected abstract Task<ShardInfo> GetShardCoreAsync(string key, CancellationToken ct);

    /// <summary>
    /// Core implementation for getting all shards.
    /// </summary>
    protected virtual Task<IEnumerable<ShardInfo>> GetAllShardsCoreAsync(CancellationToken ct)
    {
        return Task.FromResult<IEnumerable<ShardInfo>>(ShardRegistry.Values.ToList());
    }

    /// <summary>
    /// Core implementation for rebalancing shards.
    /// </summary>
    protected abstract Task<bool> RebalanceCoreAsync(RebalanceOptions options, CancellationToken ct);

    /// <summary>
    /// Core implementation for adding a shard.
    /// </summary>
    protected virtual Task<ShardInfo> AddShardCoreAsync(string shardId, string physicalLocation, CancellationToken ct)
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
            OnShardAdded(shard);

            return Task.FromResult(shard);
        }
    }

    /// <summary>
    /// Core implementation for removing a shard.
    /// </summary>
    protected virtual async Task<bool> RemoveShardCoreAsync(string shardId, bool migrateData, CancellationToken ct)
    {
        if (!ShardRegistry.TryGetValue(shardId, out var shard))
        {
            return false;
        }

        if (migrateData && shard.ObjectCount > 0)
        {
            // Mark as migrating
            await UpdateShardStatusCoreAsync(shardId, ShardStatus.Migrating, ct);

            // Migrate data to other shards
            await MigrateDataFromShardAsync(shardId, ct);
        }

        lock (ShardLock)
        {
            if (ShardRegistry.TryRemove(shardId, out var removed))
            {
                OnShardRemoved(removed);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Core implementation for updating shard status.
    /// </summary>
    protected virtual Task<bool> UpdateShardStatusCoreAsync(string shardId, ShardStatus status, CancellationToken ct)
    {
        lock (ShardLock)
        {
            if (ShardRegistry.TryGetValue(shardId, out var shard))
            {
                var updated = shard with
                {
                    Status = status,
                    LastModifiedAt = DateTime.UtcNow
                };
                ShardRegistry[shardId] = updated;
                return Task.FromResult(true);
            }
        }

        return Task.FromResult(false);
    }

    /// <summary>
    /// Migrates data from a shard to other shards.
    /// Override in derived classes for actual data migration logic.
    /// </summary>
    protected virtual Task MigrateDataFromShardAsync(string shardId, CancellationToken ct)
    {
        // Default implementation: mark migration complete
        // Derived classes should implement actual data migration
        return Task.CompletedTask;
    }

    /// <summary>
    /// Called when a shard is added. Override for custom behavior.
    /// </summary>
    protected virtual void OnShardAdded(ShardInfo shard) { }

    /// <summary>
    /// Called when a shard is removed. Override for custom behavior.
    /// </summary>
    protected virtual void OnShardRemoved(ShardInfo shard) { }

    /// <summary>
    /// Updates shard metrics (object count, size).
    /// </summary>
    protected void UpdateShardMetrics(string shardId, long objectCount, long sizeBytes)
    {
        lock (ShardLock)
        {
            if (ShardRegistry.TryGetValue(shardId, out var shard))
            {
                var updated = new ShardInfo(
                    shard.ShardId,
                    shard.PhysicalLocation,
                    shard.Status,
                    objectCount,
                    sizeBytes)
                {
                    CreatedAt = shard.CreatedAt,
                    LastModifiedAt = DateTime.UtcNow,
                    Metadata = shard.Metadata
                };
                ShardRegistry[shardId] = updated;
            }
        }
    }

    /// <summary>
    /// Increments shard metrics by specified amounts.
    /// </summary>
    protected void IncrementShardMetrics(string shardId, long objectDelta, long sizeDelta)
    {
        lock (ShardLock)
        {
            if (ShardRegistry.TryGetValue(shardId, out var shard))
            {
                var updated = new ShardInfo(
                    shard.ShardId,
                    shard.PhysicalLocation,
                    shard.Status,
                    Math.Max(0, shard.ObjectCount + objectDelta),
                    Math.Max(0, shard.SizeBytes + sizeDelta))
                {
                    CreatedAt = shard.CreatedAt,
                    LastModifiedAt = DateTime.UtcNow,
                    Metadata = shard.Metadata
                };
                ShardRegistry[shardId] = updated;
            }
        }
    }

    /// <summary>
    /// Gets an online shard by ID.
    /// </summary>
    protected ShardInfo? GetOnlineShard(string shardId)
    {
        if (ShardRegistry.TryGetValue(shardId, out var shard) && shard.Status == ShardStatus.Online)
        {
            return shard;
        }
        return null;
    }

    /// <summary>
    /// Gets all online shards.
    /// </summary>
    protected IReadOnlyList<ShardInfo> GetOnlineShards()
    {
        return ShardRegistry.Values
            .Where(s => s.Status == ShardStatus.Online)
            .ToList();
    }

    /// <summary>
    /// Records a routing operation for statistics.
    /// </summary>
    private void RecordRoutingOperation(double routingTimeMs)
    {
        lock (_statsLock)
        {
            _routingOperations++;
            _totalRoutingTimeMs += routingTimeMs;
        }
    }

    /// <summary>
    /// Records a rebalance operation for statistics.
    /// </summary>
    private void RecordRebalanceOperation()
    {
        Interlocked.Increment(ref _rebalanceOperations);
    }

    /// <summary>
    /// Computes a hash code for a string key using a fast, well-distributed algorithm.
    /// </summary>
    protected static uint ComputeHash(string key)
    {
        // MurmurHash3 32-bit implementation
        const uint c1 = 0xcc9e2d51;
        const uint c2 = 0x1b873593;
        const uint seed = 0x9747b28c;

        var data = System.Text.Encoding.UTF8.GetBytes(key);
        var length = data.Length;
        var nblocks = length / 4;
        uint h1 = seed;

        // Body
        for (int i = 0; i < nblocks; i++)
        {
            uint k1 = BitConverter.ToUInt32(data, i * 4);
            k1 *= c1;
            k1 = RotateLeft(k1, 15);
            k1 *= c2;

            h1 ^= k1;
            h1 = RotateLeft(h1, 13);
            h1 = h1 * 5 + 0xe6546b64;
        }

        // Tail
        uint k = 0;
        var tailIndex = nblocks * 4;
        switch (length & 3)
        {
            case 3:
                k ^= (uint)data[tailIndex + 2] << 16;
                goto case 2;
            case 2:
                k ^= (uint)data[tailIndex + 1] << 8;
                goto case 1;
            case 1:
                k ^= data[tailIndex];
                k *= c1;
                k = RotateLeft(k, 15);
                k *= c2;
                h1 ^= k;
                break;
        }

        // Finalization
        h1 ^= (uint)length;
        h1 = FMix(h1);

        return h1;
    }

    /// <summary>
    /// Rotates bits left.
    /// </summary>
    private static uint RotateLeft(uint x, int r) => (x << r) | (x >> (32 - r));

    /// <summary>
    /// Final mix for MurmurHash3.
    /// </summary>
    private static uint FMix(uint h)
    {
        h ^= h >> 16;
        h *= 0x85ebca6b;
        h ^= h >> 13;
        h *= 0xc2b2ae35;
        h ^= h >> 16;
        return h;
    }
}
