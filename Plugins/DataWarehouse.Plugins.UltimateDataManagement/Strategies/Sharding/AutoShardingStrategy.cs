using System.Collections.Concurrent;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.Sharding;

/// <summary>
/// Auto-sharding operation type.
/// </summary>
public enum AutoShardingOperation
{
    /// <summary>Split a shard into multiple shards.</summary>
    Split,
    /// <summary>Merge multiple shards into one.</summary>
    Merge,
    /// <summary>Rebalance data across shards.</summary>
    Rebalance
}

/// <summary>
/// Configuration for automatic sharding thresholds.
/// </summary>
public sealed class AutoShardingConfig
{
    /// <summary>
    /// Minimum number of shards to maintain.
    /// </summary>
    public int MinShards { get; init; } = 2;

    /// <summary>
    /// Maximum number of shards allowed.
    /// </summary>
    public int MaxShards { get; init; } = 256;

    /// <summary>
    /// Object count threshold to trigger split.
    /// </summary>
    public long SplitThresholdObjects { get; init; } = 1_000_000;

    /// <summary>
    /// Size threshold in bytes to trigger split.
    /// </summary>
    public long SplitThresholdBytes { get; init; } = 10L * 1024 * 1024 * 1024; // 10 GB

    /// <summary>
    /// Object count threshold to trigger merge (below this).
    /// </summary>
    public long MergeThresholdObjects { get; init; } = 100_000;

    /// <summary>
    /// Size threshold in bytes to trigger merge (below this).
    /// </summary>
    public long MergeThresholdBytes { get; init; } = 1L * 1024 * 1024 * 1024; // 1 GB

    /// <summary>
    /// Load percentage threshold to trigger rebalance.
    /// </summary>
    public double RebalanceThresholdPercent { get; init; } = 30.0;

    /// <summary>
    /// Interval for checking thresholds.
    /// </summary>
    public TimeSpan CheckInterval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Cooldown period between operations.
    /// </summary>
    public TimeSpan OperationCooldown { get; init; } = TimeSpan.FromMinutes(30);
}

/// <summary>
/// Record of an auto-sharding operation.
/// </summary>
public sealed class AutoShardingOperationRecord
{
    /// <summary>
    /// Unique operation ID.
    /// </summary>
    public required string OperationId { get; init; }

    /// <summary>
    /// Type of operation.
    /// </summary>
    public AutoShardingOperation OperationType { get; init; }

    /// <summary>
    /// Shards involved in the operation.
    /// </summary>
    public required IReadOnlyList<string> InvolvedShards { get; init; }

    /// <summary>
    /// When the operation started.
    /// </summary>
    public DateTime StartedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// When the operation completed.
    /// </summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// Whether the operation succeeded.
    /// </summary>
    public bool? Success { get; set; }

    /// <summary>
    /// Error message if failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Objects moved during operation.
    /// </summary>
    public long ObjectsMoved { get; set; }

    /// <summary>
    /// Bytes moved during operation.
    /// </summary>
    public long BytesMoved { get; set; }
}

/// <summary>
/// Automatic shard splitting and merging strategy.
/// </summary>
/// <remarks>
/// Features:
/// - Monitor shard size and load
/// - Auto-split when threshold exceeded
/// - Auto-merge underutilized shards
/// - Zero-downtime rebalancing
/// - Configurable thresholds and policies
/// </remarks>
public sealed class AutoShardingStrategy : ShardingStrategyBase
{
    private readonly AutoShardingConfig _config;
    private readonly BoundedDictionary<string, string> _cache = new BoundedDictionary<string, string>(1000);
    private readonly ConcurrentQueue<AutoShardingOperationRecord> _operationHistory = new();
    private readonly BoundedDictionary<string, DateTime> _shardLastOperation = new BoundedDictionary<string, DateTime>(1000);
    private readonly Timer? _monitorTimer;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly int _cacheMaxSize;
    private bool _autoOperationsEnabled = true;
    private int _nextShardIndex;

    /// <summary>
    /// Initializes a new AutoShardingStrategy with default configuration.
    /// </summary>
    public AutoShardingStrategy() : this(new AutoShardingConfig()) { }

    /// <summary>
    /// Initializes a new AutoShardingStrategy with specified configuration.
    /// </summary>
    /// <param name="config">Auto-sharding configuration.</param>
    /// <param name="cacheMaxSize">Maximum cache size.</param>
    public AutoShardingStrategy(AutoShardingConfig config, int cacheMaxSize = 100000)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _cacheMaxSize = cacheMaxSize;

        // Start monitoring timer
        if (_config.CheckInterval > TimeSpan.Zero)
        {
            _monitorTimer = new Timer(
                _ => _ = MonitorAndAdjustAsync(CancellationToken.None),
                null,
                _config.CheckInterval,
                _config.CheckInterval);
        }
    }

    /// <inheritdoc/>
    public override string StrategyId => "sharding.auto";

    /// <inheritdoc/>
    public override string DisplayName => "Automatic Sharding";

    /// <inheritdoc/>
    public override DataManagementCapabilities Capabilities { get; } = new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = true,
        SupportsTransactions = false,
        SupportsTTL = false,
        MaxThroughput = 300_000,
        TypicalLatencyMs = 0.03
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Automatic sharding strategy that monitors shard metrics and performs split/merge operations. " +
        "Provides zero-downtime rebalancing with configurable thresholds. " +
        "Best for systems with unpredictable growth patterns.";

    /// <inheritdoc/>
    public override string[] Tags => ["sharding", "auto", "automatic", "split", "merge", "rebalance", "adaptive"];

    /// <summary>
    /// Gets the current configuration.
    /// </summary>
    public AutoShardingConfig Configuration => _config;

    /// <summary>
    /// Gets or sets whether automatic operations are enabled.
    /// </summary>
    public bool AutoOperationsEnabled
    {
        get => _autoOperationsEnabled;
        set => _autoOperationsEnabled = value;
    }

    /// <inheritdoc/>
    protected override Task InitializeCoreAsync(CancellationToken ct)
    {
        // Create initial shards
        for (int i = 0; i < _config.MinShards; i++)
        {
            var shardId = $"auto-shard-{i:D4}";
            ShardRegistry[shardId] = new ShardInfo(
                shardId,
                $"node-{i % 4}/db-auto",
                ShardStatus.Online,
                0, 0)
            {
                CreatedAt = DateTime.UtcNow,
                LastModifiedAt = DateTime.UtcNow
            };
            _nextShardIndex = i + 1;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task<ShardInfo> GetShardCoreAsync(string key, CancellationToken ct)
    {
        // Check cache first
        if (_cache.TryGetValue(key, out var cachedShardId))
        {
            if (ShardRegistry.TryGetValue(cachedShardId, out var cachedShard) &&
                cachedShard.Status == ShardStatus.Online)
            {
                return Task.FromResult(cachedShard);
            }
            _cache.TryRemove(key, out _);
        }

        // Hash-based routing
        var hash = ComputeHash(key);
        var onlineShards = GetOnlineShards();

        if (onlineShards.Count == 0)
        {
            throw new InvalidOperationException("No online shards available.");
        }

        var shardIndex = (int)(hash % (uint)onlineShards.Count);
        var shard = onlineShards[shardIndex];

        // Cache the result
        CacheKeyMapping(key, shard.ShardId);

        return Task.FromResult(shard);
    }

    /// <inheritdoc/>
    protected override async Task<bool> RebalanceCoreAsync(RebalanceOptions options, CancellationToken ct)
    {
        if (!options.AllowDataMovement)
        {
            return true;
        }

        await _operationLock.WaitAsync(ct);
        try
        {
            await MonitorAndAdjustAsync(ct);
            return true;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    /// <summary>
    /// Manually triggers a shard split.
    /// </summary>
    /// <param name="shardId">The shard to split.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>New shards created from the split.</returns>
    public async Task<IReadOnlyList<ShardInfo>> SplitShardAsync(string shardId, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(shardId);

        if (!ShardRegistry.TryGetValue(shardId, out var sourceShard))
        {
            throw new InvalidOperationException($"Shard '{shardId}' not found.");
        }

        if (ShardRegistry.Count >= _config.MaxShards)
        {
            throw new InvalidOperationException($"Cannot split: maximum shard count ({_config.MaxShards}) reached.");
        }

        await _operationLock.WaitAsync(ct);
        try
        {
            return await ExecuteSplitAsync(sourceShard, ct);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    /// <summary>
    /// Manually triggers a shard merge.
    /// </summary>
    /// <param name="shardId1">First shard to merge.</param>
    /// <param name="shardId2">Second shard to merge.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The merged shard.</returns>
    public async Task<ShardInfo> MergeShardsAsync(string shardId1, string shardId2, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(shardId1);
        ArgumentException.ThrowIfNullOrWhiteSpace(shardId2);

        if (!ShardRegistry.TryGetValue(shardId1, out var shard1))
        {
            throw new InvalidOperationException($"Shard '{shardId1}' not found.");
        }

        if (!ShardRegistry.TryGetValue(shardId2, out var shard2))
        {
            throw new InvalidOperationException($"Shard '{shardId2}' not found.");
        }

        if (ShardRegistry.Count <= _config.MinShards)
        {
            throw new InvalidOperationException($"Cannot merge: minimum shard count ({_config.MinShards}) reached.");
        }

        await _operationLock.WaitAsync(ct);
        try
        {
            return await ExecuteMergeAsync(shard1, shard2, ct);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    /// <summary>
    /// Gets the operation history.
    /// </summary>
    /// <param name="limit">Maximum number of records to return.</param>
    /// <returns>Recent operation records.</returns>
    public IReadOnlyList<AutoShardingOperationRecord> GetOperationHistory(int limit = 100)
    {
        return _operationHistory
            .OrderByDescending(r => r.StartedAt)
            .Take(limit)
            .ToList();
    }

    /// <summary>
    /// Gets shards that might need splitting.
    /// </summary>
    /// <returns>Shards above split thresholds.</returns>
    public IReadOnlyList<ShardInfo> GetShardsNeedingSplit()
    {
        return ShardRegistry.Values
            .Where(s => s.Status == ShardStatus.Online &&
                       (s.ObjectCount >= _config.SplitThresholdObjects ||
                        s.SizeBytes >= _config.SplitThresholdBytes))
            .ToList();
    }

    /// <summary>
    /// Gets shards that might need merging.
    /// </summary>
    /// <returns>Shards below merge thresholds.</returns>
    public IReadOnlyList<ShardInfo> GetShardsNeedingMerge()
    {
        return ShardRegistry.Values
            .Where(s => s.Status == ShardStatus.Online &&
                       s.ObjectCount < _config.MergeThresholdObjects &&
                       s.SizeBytes < _config.MergeThresholdBytes)
            .ToList();
    }

    /// <summary>
    /// Gets the current health status of the sharding system.
    /// </summary>
    /// <returns>Health status information.</returns>
    public AutoShardingHealth GetHealth()
    {
        var shards = ShardRegistry.Values.ToList();
        var avgObjects = shards.Count > 0 ? shards.Average(s => s.ObjectCount) : 0;
        var avgSize = shards.Count > 0 ? shards.Average(s => s.SizeBytes) : 0;

        var maxDeviation = shards.Count > 0 && avgObjects > 0
            ? shards.Max(s => Math.Abs(s.ObjectCount - avgObjects)) / avgObjects * 100
            : 0;

        return new AutoShardingHealth
        {
            TotalShards = shards.Count,
            OnlineShards = shards.Count(s => s.Status == ShardStatus.Online),
            TotalObjects = shards.Sum(s => s.ObjectCount),
            TotalSizeBytes = shards.Sum(s => s.SizeBytes),
            AverageObjectsPerShard = avgObjects,
            AverageSizePerShard = (long)avgSize,
            MaxDeviationPercent = maxDeviation,
            ShardsNeedingSplit = GetShardsNeedingSplit().Count,
            ShardsNeedingMerge = GetShardsNeedingMerge().Count,
            RecentOperations = _operationHistory.Count(r => r.StartedAt > DateTime.UtcNow.AddHours(-1)),
            IsHealthy = maxDeviation < _config.RebalanceThresholdPercent
        };
    }

    /// <summary>
    /// Updates metrics for a shard and checks for auto-operations.
    /// </summary>
    /// <param name="shardId">The shard to update.</param>
    /// <param name="objectCount">New object count.</param>
    /// <param name="sizeBytes">New size in bytes.</param>
    public void UpdateShardMetricsAndCheck(string shardId, long objectCount, long sizeBytes)
    {
        UpdateShardMetrics(shardId, objectCount, sizeBytes);

        // Trigger async check if thresholds might be exceeded
        if (_autoOperationsEnabled &&
            (objectCount >= _config.SplitThresholdObjects * 0.9 ||
             sizeBytes >= _config.SplitThresholdBytes * 0.9 ||
             objectCount <= _config.MergeThresholdObjects * 1.1 ||
             sizeBytes <= _config.MergeThresholdBytes * 1.1))
        {
            _ = Task.Run(() => MonitorAndAdjustAsync(CancellationToken.None));
        }
    }

    /// <summary>
    /// Monitors shard metrics and performs automatic adjustments.
    /// </summary>
    private async Task MonitorAndAdjustAsync(CancellationToken ct)
    {
        if (!_autoOperationsEnabled || !IsInitialized)
        {
            return;
        }

        try
        {
            await _operationLock.WaitAsync(ct);
            try
            {
                // Check for splits
                var needsSplit = GetShardsNeedingSplit();
                foreach (var shard in needsSplit)
                {
                    ct.ThrowIfCancellationRequested();

                    if (ShardRegistry.Count >= _config.MaxShards)
                        break;

                    if (!CanOperateOnShard(shard.ShardId))
                        continue;

                    await ExecuteSplitAsync(shard, ct);
                }

                // Check for merges
                var needsMerge = GetShardsNeedingMerge().ToList();
                while (needsMerge.Count >= 2 && ShardRegistry.Count > _config.MinShards)
                {
                    ct.ThrowIfCancellationRequested();

                    var shard1 = needsMerge[0];
                    var shard2 = needsMerge[1];

                    if (!CanOperateOnShard(shard1.ShardId) || !CanOperateOnShard(shard2.ShardId))
                    {
                        needsMerge.RemoveAt(0);
                        continue;
                    }

                    await ExecuteMergeAsync(shard1, shard2, ct);
                    needsMerge.RemoveAt(0);
                    needsMerge.RemoveAt(0);
                }
            }
            finally
            {
                _operationLock.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when cancellation requested
        }
        catch
        {
            // Log but don't throw from monitoring
        }
    }

    /// <summary>
    /// Checks if an operation can be performed on a shard (cooldown check).
    /// </summary>
    private bool CanOperateOnShard(string shardId)
    {
        if (_shardLastOperation.TryGetValue(shardId, out var lastOp))
        {
            return DateTime.UtcNow - lastOp >= _config.OperationCooldown;
        }
        return true;
    }

    /// <summary>
    /// Executes a shard split operation.
    /// </summary>
    private async Task<IReadOnlyList<ShardInfo>> ExecuteSplitAsync(ShardInfo sourceShard, CancellationToken ct)
    {
        var operationId = $"split-{Guid.NewGuid():N}".Substring(0, 20);
        var record = new AutoShardingOperationRecord
        {
            OperationId = operationId,
            OperationType = AutoShardingOperation.Split,
            InvolvedShards = new[] { sourceShard.ShardId },
            StartedAt = DateTime.UtcNow
        };

        _operationHistory.Enqueue(record);

        try
        {
            // Mark source as splitting
            await UpdateShardStatusAsync(sourceShard.ShardId, ShardStatus.Splitting, ct);

            // Create two new shards
            var newShard1Id = $"auto-shard-{_nextShardIndex++:D4}";
            var newShard2Id = $"auto-shard-{_nextShardIndex++:D4}";

            var halfObjects = sourceShard.ObjectCount / 2;
            var halfSize = sourceShard.SizeBytes / 2;

            var newShard1 = new ShardInfo(
                newShard1Id,
                $"node-{_nextShardIndex % 4}/db-auto",
                ShardStatus.Online,
                halfObjects,
                halfSize)
            {
                CreatedAt = DateTime.UtcNow,
                LastModifiedAt = DateTime.UtcNow
            };

            var newShard2 = new ShardInfo(
                newShard2Id,
                $"node-{(_nextShardIndex + 1) % 4}/db-auto",
                ShardStatus.Online,
                halfObjects,
                halfSize)
            {
                CreatedAt = DateTime.UtcNow,
                LastModifiedAt = DateTime.UtcNow
            };

            ShardRegistry[newShard1Id] = newShard1;
            ShardRegistry[newShard2Id] = newShard2;

            // Remove source shard
            ShardRegistry.TryRemove(sourceShard.ShardId, out _);

            // Record cooldowns
            _shardLastOperation[newShard1Id] = DateTime.UtcNow;
            _shardLastOperation[newShard2Id] = DateTime.UtcNow;

            // Clear cache
            _cache.Clear();

            // Update record
            record.CompletedAt = DateTime.UtcNow;
            record.Success = true;
            record.ObjectsMoved = sourceShard.ObjectCount;
            record.BytesMoved = sourceShard.SizeBytes;

            return new[] { newShard1, newShard2 };
        }
        catch (Exception ex)
        {
            record.CompletedAt = DateTime.UtcNow;
            record.Success = false;
            record.ErrorMessage = ex.Message;

            // Restore source shard status
            await UpdateShardStatusAsync(sourceShard.ShardId, ShardStatus.Online, ct);

            throw;
        }
    }

    /// <summary>
    /// Executes a shard merge operation.
    /// </summary>
    private async Task<ShardInfo> ExecuteMergeAsync(ShardInfo shard1, ShardInfo shard2, CancellationToken ct)
    {
        var operationId = $"merge-{Guid.NewGuid():N}".Substring(0, 20);
        var record = new AutoShardingOperationRecord
        {
            OperationId = operationId,
            OperationType = AutoShardingOperation.Merge,
            InvolvedShards = new[] { shard1.ShardId, shard2.ShardId },
            StartedAt = DateTime.UtcNow
        };

        _operationHistory.Enqueue(record);

        try
        {
            // Mark shards as merging
            await UpdateShardStatusAsync(shard1.ShardId, ShardStatus.Merging, ct);
            await UpdateShardStatusAsync(shard2.ShardId, ShardStatus.Merging, ct);

            // Create merged shard
            var mergedShardId = $"auto-shard-{_nextShardIndex++:D4}";
            var mergedShard = new ShardInfo(
                mergedShardId,
                $"node-{_nextShardIndex % 4}/db-auto",
                ShardStatus.Online,
                shard1.ObjectCount + shard2.ObjectCount,
                shard1.SizeBytes + shard2.SizeBytes)
            {
                CreatedAt = DateTime.UtcNow,
                LastModifiedAt = DateTime.UtcNow
            };

            ShardRegistry[mergedShardId] = mergedShard;

            // Remove source shards
            ShardRegistry.TryRemove(shard1.ShardId, out _);
            ShardRegistry.TryRemove(shard2.ShardId, out _);

            // Record cooldown
            _shardLastOperation[mergedShardId] = DateTime.UtcNow;

            // Clear cache
            _cache.Clear();

            // Update record
            record.CompletedAt = DateTime.UtcNow;
            record.Success = true;
            record.ObjectsMoved = shard1.ObjectCount + shard2.ObjectCount;
            record.BytesMoved = shard1.SizeBytes + shard2.SizeBytes;

            return mergedShard;
        }
        catch (Exception ex)
        {
            record.CompletedAt = DateTime.UtcNow;
            record.Success = false;
            record.ErrorMessage = ex.Message;

            // Restore shard statuses
            await UpdateShardStatusAsync(shard1.ShardId, ShardStatus.Online, ct);
            await UpdateShardStatusAsync(shard2.ShardId, ShardStatus.Online, ct);

            throw;
        }
    }

    /// <summary>
    /// Caches a key-to-shard mapping.
    /// </summary>
    private void CacheKeyMapping(string key, string shardId)
    {
        if (_cache.Count >= _cacheMaxSize)
        {
            var toRemove = _cache.Keys.Take(_cacheMaxSize / 10).ToList();
            foreach (var k in toRemove)
            {
                _cache.TryRemove(k, out _);
            }
        }

        _cache[key] = shardId;
    }

    /// <inheritdoc/>
    protected override Task DisposeCoreAsync()
    {
        _monitorTimer?.Dispose();
        _operationLock.Dispose();
        return Task.CompletedTask;
    }
}

/// <summary>
/// Health status of the auto-sharding system.
/// </summary>
public sealed class AutoShardingHealth
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
    /// Total objects across all shards.
    /// </summary>
    public long TotalObjects { get; init; }

    /// <summary>
    /// Total size across all shards.
    /// </summary>
    public long TotalSizeBytes { get; init; }

    /// <summary>
    /// Average objects per shard.
    /// </summary>
    public double AverageObjectsPerShard { get; init; }

    /// <summary>
    /// Average size per shard.
    /// </summary>
    public long AverageSizePerShard { get; init; }

    /// <summary>
    /// Maximum deviation from average (percent).
    /// </summary>
    public double MaxDeviationPercent { get; init; }

    /// <summary>
    /// Shards currently needing split.
    /// </summary>
    public int ShardsNeedingSplit { get; init; }

    /// <summary>
    /// Shards currently needing merge.
    /// </summary>
    public int ShardsNeedingMerge { get; init; }

    /// <summary>
    /// Operations in the last hour.
    /// </summary>
    public int RecentOperations { get; init; }

    /// <summary>
    /// Whether the system is healthy.
    /// </summary>
    public bool IsHealthy { get; init; }
}
