using System.Collections.Concurrent;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.Sharding;

/// <summary>
/// Represents a key range for range-based sharding.
/// </summary>
public sealed class KeyRange : IComparable<KeyRange>
{
    /// <summary>
    /// Start of the range (inclusive).
    /// </summary>
    public string Start { get; }

    /// <summary>
    /// End of the range (exclusive). Null means unbounded.
    /// </summary>
    public string? End { get; }

    /// <summary>
    /// The shard ID that owns this range.
    /// </summary>
    public string ShardId { get; }

    /// <summary>
    /// Initializes a new KeyRange.
    /// </summary>
    /// <param name="start">Start of the range (inclusive).</param>
    /// <param name="end">End of the range (exclusive).</param>
    /// <param name="shardId">The shard that owns this range.</param>
    public KeyRange(string start, string? end, string shardId)
    {
        Start = start ?? throw new ArgumentNullException(nameof(start));
        End = end;
        ShardId = shardId ?? throw new ArgumentNullException(nameof(shardId));
    }

    /// <summary>
    /// Checks if a key falls within this range.
    /// </summary>
    /// <param name="key">The key to check.</param>
    /// <returns>True if the key is within the range.</returns>
    public bool Contains(string key)
    {
        var startComparison = string.Compare(key, Start, StringComparison.Ordinal);
        if (startComparison < 0) return false;

        if (End == null) return true;

        var endComparison = string.Compare(key, End, StringComparison.Ordinal);
        return endComparison < 0;
    }

    /// <inheritdoc/>
    public int CompareTo(KeyRange? other)
    {
        if (other == null) return 1;
        return string.Compare(Start, other.Start, StringComparison.Ordinal);
    }
}

/// <summary>
/// Range-based sharding strategy that partitions data by key ranges.
/// </summary>
/// <remarks>
/// Features:
/// - Define custom key ranges per shard
/// - Supports range queries efficiently
/// - Dynamic range splitting and merging
/// - Ordered key support for sequential access
/// - Thread-safe range lookups using binary search
/// </remarks>
public sealed class RangeShardingStrategy : ShardingStrategyBase
{
    private readonly List<KeyRange> _ranges = new();
    private readonly ReaderWriterLockSlim _rangeLock = new();
    private readonly ConcurrentDictionary<string, string> _keyCache = new();
    private readonly int _cacheMaxSize;

    /// <summary>
    /// Initializes a new RangeShardingStrategy with default settings.
    /// </summary>
    public RangeShardingStrategy() : this(100000) { }

    /// <summary>
    /// Initializes a new RangeShardingStrategy with specified cache size.
    /// </summary>
    /// <param name="cacheMaxSize">Maximum size of the key-to-shard cache.</param>
    public RangeShardingStrategy(int cacheMaxSize)
    {
        _cacheMaxSize = cacheMaxSize;
    }

    /// <inheritdoc/>
    public override string StrategyId => "sharding.range";

    /// <inheritdoc/>
    public override string DisplayName => "Range-Based Sharding";

    /// <inheritdoc/>
    public override DataManagementCapabilities Capabilities { get; } = new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = true,
        SupportsTransactions = false,
        SupportsTTL = false,
        MaxThroughput = 200_000,
        TypicalLatencyMs = 0.05
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Range-based sharding strategy that partitions data by key ranges. " +
        "Supports efficient range queries and ordered access patterns. " +
        "Best for time-series data, alphabetical ranges, or sequential keys.";

    /// <inheritdoc/>
    public override string[] Tags => ["sharding", "range", "partition", "ordered", "sequential"];

    /// <inheritdoc/>
    protected override Task InitializeCoreAsync(CancellationToken ct)
    {
        // Create default ranges (A-Z split into 4 shards)
        var ranges = new[]
        {
            ("shard-0001", "", "G"),
            ("shard-0002", "G", "N"),
            ("shard-0003", "N", "T"),
            ("shard-0004", "T", null as string)
        };

        foreach (var (shardId, start, end) in ranges)
        {
            var location = $"node-{shardId.GetHashCode() % 4}/db-range";

            ShardRegistry[shardId] = new ShardInfo(shardId, location, ShardStatus.Online, 0, 0)
            {
                CreatedAt = DateTime.UtcNow,
                LastModifiedAt = DateTime.UtcNow
            };

            _ranges.Add(new KeyRange(start, end, shardId));
        }

        _ranges.Sort();

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

        // Find range using binary search
        var shardId = FindShardForKey(key);

        if (shardId == null)
        {
            throw new InvalidOperationException($"No shard found for key '{key}'.");
        }

        if (!ShardRegistry.TryGetValue(shardId, out var shard))
        {
            throw new InvalidOperationException($"Shard '{shardId}' not found in registry.");
        }

        if (shard.Status != ShardStatus.Online)
        {
            throw new InvalidOperationException($"Shard '{shardId}' is not online (status: {shard.Status}).");
        }

        // Cache the result
        CacheKeyMapping(key, shardId);

        return Task.FromResult(shard);
    }

    /// <inheritdoc/>
    protected override async Task<bool> RebalanceCoreAsync(RebalanceOptions options, CancellationToken ct)
    {
        if (!options.AllowDataMovement)
        {
            return true; // Range rebalance requires data movement
        }

        var shards = GetOnlineShards();
        if (shards.Count < 2)
        {
            return true;
        }

        // Identify hot shards (significantly above average)
        var avgObjects = shards.Average(s => s.ObjectCount);
        var hotThreshold = avgObjects * (1 + (1 - options.TargetBalanceRatio));

        var hotShards = shards.Where(s => s.ObjectCount > hotThreshold).ToList();

        foreach (var hotShard in hotShards)
        {
            ct.ThrowIfCancellationRequested();

            if (options.SkipBusyShards && hotShard.Status != ShardStatus.Online)
                continue;

            // Split the hot shard's range
            await SplitRangeAsync(hotShard.ShardId, ct);
        }

        return true;
    }

    /// <summary>
    /// Finds the shard for a key using binary search.
    /// </summary>
    private string? FindShardForKey(string key)
    {
        _rangeLock.EnterReadLock();
        try
        {
            // Binary search for the range containing the key
            int left = 0;
            int right = _ranges.Count - 1;

            while (left <= right)
            {
                int mid = left + (right - left) / 2;
                var range = _ranges[mid];

                if (range.Contains(key))
                {
                    return range.ShardId;
                }

                var comparison = string.Compare(key, range.Start, StringComparison.Ordinal);
                if (comparison < 0)
                {
                    right = mid - 1;
                }
                else
                {
                    left = mid + 1;
                }
            }

            // Check if key falls before first range
            if (_ranges.Count > 0 && string.Compare(key, _ranges[0].Start, StringComparison.Ordinal) < 0)
            {
                return _ranges[0].ShardId;
            }

            return null;
        }
        finally
        {
            _rangeLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Adds a new range to the sharding configuration.
    /// </summary>
    /// <param name="start">Start of the range (inclusive).</param>
    /// <param name="end">End of the range (exclusive).</param>
    /// <param name="shardId">The shard ID for this range.</param>
    /// <param name="physicalLocation">Physical location of the shard.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created shard info.</returns>
    public Task<ShardInfo> AddRangeAsync(
        string start,
        string? end,
        string shardId,
        string physicalLocation,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentNullException.ThrowIfNull(start);
        ArgumentException.ThrowIfNullOrWhiteSpace(shardId);
        ArgumentException.ThrowIfNullOrWhiteSpace(physicalLocation);

        _rangeLock.EnterWriteLock();
        try
        {
            // Check for overlapping ranges
            foreach (var existingRange in _ranges)
            {
                if (RangesOverlap(start, end, existingRange.Start, existingRange.End))
                {
                    throw new InvalidOperationException(
                        $"Range [{start}, {end ?? "inf"}) overlaps with existing range [{existingRange.Start}, {existingRange.End ?? "inf"}).");
                }
            }

            // Add the shard
            var shard = new ShardInfo(shardId, physicalLocation, ShardStatus.Online, 0, 0)
            {
                CreatedAt = DateTime.UtcNow,
                LastModifiedAt = DateTime.UtcNow
            };
            ShardRegistry[shardId] = shard;

            // Add the range
            var range = new KeyRange(start, end, shardId);
            _ranges.Add(range);
            _ranges.Sort();

            // Clear cache since ranges changed
            _keyCache.Clear();

            return Task.FromResult(shard);
        }
        finally
        {
            _rangeLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Splits a range at a specified key.
    /// </summary>
    /// <param name="shardId">The shard whose range to split.</param>
    /// <param name="splitKey">The key at which to split (becomes start of new range).</param>
    /// <param name="newShardId">ID for the new shard.</param>
    /// <param name="newPhysicalLocation">Physical location for the new shard.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The new shard info.</returns>
    public Task<ShardInfo> SplitRangeAtKeyAsync(
        string shardId,
        string splitKey,
        string newShardId,
        string newPhysicalLocation,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(shardId);
        ArgumentException.ThrowIfNullOrWhiteSpace(splitKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(newShardId);
        ArgumentException.ThrowIfNullOrWhiteSpace(newPhysicalLocation);

        _rangeLock.EnterWriteLock();
        try
        {
            var rangeIndex = _ranges.FindIndex(r => r.ShardId == shardId);
            if (rangeIndex < 0)
            {
                throw new InvalidOperationException($"No range found for shard '{shardId}'.");
            }

            var originalRange = _ranges[rangeIndex];

            // Validate split key is within range
            if (!originalRange.Contains(splitKey))
            {
                throw new InvalidOperationException(
                    $"Split key '{splitKey}' is not within range [{originalRange.Start}, {originalRange.End ?? "inf"}).");
            }

            // Create new shard
            var newShard = new ShardInfo(newShardId, newPhysicalLocation, ShardStatus.Online, 0, 0)
            {
                CreatedAt = DateTime.UtcNow,
                LastModifiedAt = DateTime.UtcNow
            };
            ShardRegistry[newShardId] = newShard;

            // Update ranges
            _ranges[rangeIndex] = new KeyRange(originalRange.Start, splitKey, shardId);
            _ranges.Insert(rangeIndex + 1, new KeyRange(splitKey, originalRange.End, newShardId));

            // Clear cache
            _keyCache.Clear();

            return Task.FromResult(newShard);
        }
        finally
        {
            _rangeLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Merges two adjacent ranges.
    /// </summary>
    /// <param name="shardId1">First shard (will be kept).</param>
    /// <param name="shardId2">Second shard (will be removed).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if merge was successful.</returns>
    public Task<bool> MergeRangesAsync(string shardId1, string shardId2, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(shardId1);
        ArgumentException.ThrowIfNullOrWhiteSpace(shardId2);

        _rangeLock.EnterWriteLock();
        try
        {
            var index1 = _ranges.FindIndex(r => r.ShardId == shardId1);
            var index2 = _ranges.FindIndex(r => r.ShardId == shardId2);

            if (index1 < 0 || index2 < 0)
            {
                return Task.FromResult(false);
            }

            // Ensure ranges are adjacent
            if (Math.Abs(index1 - index2) != 1)
            {
                throw new InvalidOperationException("Can only merge adjacent ranges.");
            }

            var firstIndex = Math.Min(index1, index2);
            var secondIndex = Math.Max(index1, index2);

            var firstRange = _ranges[firstIndex];
            var secondRange = _ranges[secondIndex];

            // Verify adjacency
            if (firstRange.End != secondRange.Start)
            {
                throw new InvalidOperationException("Ranges are not truly adjacent.");
            }

            // Merge into first range
            var mergedRange = new KeyRange(firstRange.Start, secondRange.End, shardId1);
            _ranges[firstIndex] = mergedRange;
            _ranges.RemoveAt(secondIndex);

            // Remove second shard
            ShardRegistry.TryRemove(shardId2, out _);

            // Clear cache
            _keyCache.Clear();

            return Task.FromResult(true);
        }
        finally
        {
            _rangeLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Gets all shards that cover a key range (for range queries).
    /// </summary>
    /// <param name="startKey">Start of the query range (inclusive).</param>
    /// <param name="endKey">End of the query range (exclusive).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Shards that cover the query range.</returns>
    public Task<IReadOnlyList<ShardInfo>> GetShardsForRangeAsync(
        string startKey,
        string endKey,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentNullException.ThrowIfNull(startKey);
        ArgumentNullException.ThrowIfNull(endKey);

        var result = new List<ShardInfo>();

        _rangeLock.EnterReadLock();
        try
        {
            foreach (var range in _ranges)
            {
                ct.ThrowIfCancellationRequested();

                // Check if query range overlaps with this range
                if (RangesOverlap(startKey, endKey, range.Start, range.End))
                {
                    if (ShardRegistry.TryGetValue(range.ShardId, out var shard) &&
                        shard.Status == ShardStatus.Online)
                    {
                        result.Add(shard);
                    }
                }
            }
        }
        finally
        {
            _rangeLock.ExitReadLock();
        }

        return Task.FromResult<IReadOnlyList<ShardInfo>>(result);
    }

    /// <summary>
    /// Gets all configured ranges.
    /// </summary>
    /// <returns>List of all key ranges.</returns>
    public IReadOnlyList<KeyRange> GetAllRanges()
    {
        _rangeLock.EnterReadLock();
        try
        {
            return _ranges.ToList();
        }
        finally
        {
            _rangeLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Automatically splits a hot shard's range.
    /// </summary>
    private Task SplitRangeAsync(string shardId, CancellationToken ct)
    {
        _rangeLock.EnterWriteLock();
        try
        {
            var rangeIndex = _ranges.FindIndex(r => r.ShardId == shardId);
            if (rangeIndex < 0) return Task.CompletedTask;

            var range = _ranges[rangeIndex];

            // Calculate midpoint for split
            var start = range.Start;
            var end = range.End ?? "~"; // Use high character if unbounded

            // Simple midpoint calculation (in practice, would use data distribution)
            var midpoint = GetMidpoint(start, end);
            if (midpoint == start || midpoint == end)
            {
                return Task.CompletedTask; // Cannot split further
            }

            // Create new shard
            var newShardId = $"shard-{Guid.NewGuid():N}".Substring(0, 15);
            var newShard = new ShardInfo(
                newShardId,
                $"node-{newShardId.GetHashCode() % 4}/db-range",
                ShardStatus.Online,
                0, 0)
            {
                CreatedAt = DateTime.UtcNow,
                LastModifiedAt = DateTime.UtcNow
            };
            ShardRegistry[newShardId] = newShard;

            // Split the range
            _ranges[rangeIndex] = new KeyRange(range.Start, midpoint, shardId);
            _ranges.Insert(rangeIndex + 1, new KeyRange(midpoint, range.End, newShardId));

            // Update metrics (split objects approximately)
            if (ShardRegistry.TryGetValue(shardId, out var originalShard))
            {
                var halfObjects = originalShard.ObjectCount / 2;
                var halfSize = originalShard.SizeBytes / 2;

                UpdateShardMetrics(shardId, halfObjects, halfSize);
                UpdateShardMetrics(newShardId, halfObjects, halfSize);
            }

            // Clear cache
            _keyCache.Clear();
        }
        finally
        {
            _rangeLock.ExitWriteLock();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Checks if two ranges overlap.
    /// </summary>
    private static bool RangesOverlap(string start1, string? end1, string start2, string? end2)
    {
        // Range 1 ends before Range 2 starts
        if (end1 != null && string.Compare(end1, start2, StringComparison.Ordinal) <= 0)
            return false;

        // Range 2 ends before Range 1 starts
        if (end2 != null && string.Compare(end2, start1, StringComparison.Ordinal) <= 0)
            return false;

        return true;
    }

    /// <summary>
    /// Calculates the midpoint between two strings.
    /// </summary>
    private static string GetMidpoint(string start, string end)
    {
        var minLength = Math.Min(start.Length, end.Length);
        var maxLength = Math.Max(start.Length, end.Length);

        for (int i = 0; i < minLength; i++)
        {
            if (start[i] != end[i])
            {
                var midChar = (char)((start[i] + end[i]) / 2);
                if (midChar != start[i])
                {
                    return start.Substring(0, i) + midChar;
                }
            }
        }

        // Strings are equal up to minLength
        if (maxLength > minLength)
        {
            var longer = start.Length > end.Length ? start : end;
            return longer.Substring(0, minLength + 1);
        }

        return start; // Cannot split
    }

    /// <summary>
    /// Caches a key-to-shard mapping.
    /// </summary>
    private void CacheKeyMapping(string key, string shardId)
    {
        if (_keyCache.Count >= _cacheMaxSize)
        {
            // Simple eviction: clear half the cache
            var toRemove = _keyCache.Keys.Take(_cacheMaxSize / 2).ToList();
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
        _rangeLock.Dispose();
        return Task.CompletedTask;
    }
}
