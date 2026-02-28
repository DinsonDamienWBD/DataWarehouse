using System.Globalization;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.Sharding;

/// <summary>
/// Time partition granularity.
/// </summary>
public enum TimePartitionGranularity
{
    /// <summary>Partition by hour.</summary>
    Hour,
    /// <summary>Partition by day.</summary>
    Day,
    /// <summary>Partition by week.</summary>
    Week,
    /// <summary>Partition by month.</summary>
    Month,
    /// <summary>Partition by quarter.</summary>
    Quarter,
    /// <summary>Partition by year.</summary>
    Year
}

/// <summary>
/// Time partition information.
/// </summary>
public sealed class TimePartition
{
    /// <summary>
    /// Partition identifier (e.g., "2024-01", "2024-W05").
    /// </summary>
    public required string PartitionId { get; init; }

    /// <summary>
    /// Start time of the partition (inclusive).
    /// </summary>
    public DateTime StartTime { get; init; }

    /// <summary>
    /// End time of the partition (exclusive).
    /// </summary>
    public DateTime EndTime { get; init; }

    /// <summary>
    /// The shard ID storing this partition.
    /// </summary>
    public required string ShardId { get; init; }

    /// <summary>
    /// Whether this partition is archived.
    /// </summary>
    public bool IsArchived { get; set; }

    /// <summary>
    /// When the partition was created.
    /// </summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Object count in this partition.
    /// </summary>
    public long ObjectCount { get; set; }

    /// <summary>
    /// Size in bytes.
    /// </summary>
    public long SizeBytes { get; set; }
}

/// <summary>
/// Time-based sharding strategy that partitions data by time periods.
/// </summary>
/// <remarks>
/// Features:
/// - Partition by day, week, month, or year
/// - Rolling partitions with automatic creation
/// - Partition archival support
/// - Efficient time-range queries
/// - Thread-safe partition management
/// </remarks>
public sealed class TimeShardingStrategy : ShardingStrategyBase
{
    private readonly BoundedDictionary<string, TimePartition> _partitions = new BoundedDictionary<string, TimePartition>(1000);
    private readonly BoundedDictionary<string, string> _cache = new BoundedDictionary<string, string>(1000);
    private readonly ReaderWriterLockSlim _partitionLock = new();
    private readonly TimePartitionGranularity _granularity;
    private readonly int _rollingPartitionCount;
    private readonly TimeSpan _archiveAfter;
    private readonly int _cacheMaxSize;
    private readonly Timer? _partitionMaintenanceTimer;
    private string _timestampKeyPattern = @"^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})";

    /// <summary>
    /// Initializes a new TimeShardingStrategy with default settings.
    /// </summary>
    public TimeShardingStrategy() : this(TimePartitionGranularity.Day) { }

    /// <summary>
    /// Initializes a new TimeShardingStrategy with specified settings.
    /// </summary>
    /// <param name="granularity">Partition granularity.</param>
    /// <param name="rollingPartitionCount">Number of rolling partitions to maintain.</param>
    /// <param name="archiveAfterDays">Days after which to archive partitions (0 = never).</param>
    /// <param name="cacheMaxSize">Maximum cache size.</param>
    public TimeShardingStrategy(
        TimePartitionGranularity granularity,
        int rollingPartitionCount = 30,
        int archiveAfterDays = 90,
        int cacheMaxSize = 100000)
    {
        _granularity = granularity;
        _rollingPartitionCount = rollingPartitionCount;
        _archiveAfter = archiveAfterDays > 0 ? TimeSpan.FromDays(archiveAfterDays) : TimeSpan.MaxValue;
        _cacheMaxSize = cacheMaxSize;

        // Start partition maintenance timer
        _partitionMaintenanceTimer = new Timer(
            _ => MaintenancePartitions(),
            null,
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(5));
    }

    /// <inheritdoc/>
    public override string StrategyId => "sharding.time";

    /// <inheritdoc/>
    public override string DisplayName => "Time-Based Sharding";

    /// <inheritdoc/>
    public override DataManagementCapabilities Capabilities { get; } = new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = true,
        SupportsTransactions = false,
        SupportsTTL = true,
        MaxThroughput = 300_000,
        TypicalLatencyMs = 0.03
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Time-based sharding strategy that partitions data by time periods (day/week/month/year). " +
        "Supports rolling partitions and automatic archival. " +
        "Best for time-series data, logs, events, and historical records.";

    /// <inheritdoc/>
    public override string[] Tags => ["sharding", "time", "temporal", "partition", "timeseries", "archive"];

    /// <summary>
    /// Gets the current partition granularity.
    /// </summary>
    public TimePartitionGranularity Granularity => _granularity;

    /// <inheritdoc/>
    protected override Task InitializeCoreAsync(CancellationToken ct)
    {
        // Create partitions for the rolling window
        var now = DateTime.UtcNow;

        for (int i = -_rollingPartitionCount + 1; i <= 1; i++)
        {
            var time = AddGranularityUnits(now, i);
            EnsurePartitionExists(time);
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

        // Extract timestamp from key
        var timestamp = ExtractTimestamp(key);
        var partitionId = GetPartitionId(timestamp);

        // Ensure partition exists
        var partition = EnsurePartitionExists(timestamp);

        if (!ShardRegistry.TryGetValue(partition.ShardId, out var shard))
        {
            throw new InvalidOperationException($"Shard '{partition.ShardId}' not found for partition '{partitionId}'.");
        }

        // Cache the result
        CacheKeyMapping(key, partition.ShardId);

        return Task.FromResult(shard);
    }

    /// <inheritdoc/>
    protected override Task<bool> RebalanceCoreAsync(RebalanceOptions options, CancellationToken ct)
    {
        // Time-based sharding doesn't typically rebalance
        // Data placement is determined by time
        // However, we can consolidate old partitions

        if (!options.AllowDataMovement)
        {
            return Task.FromResult(true);
        }

        // Archive old partitions
        var archiveThreshold = DateTime.UtcNow - _archiveAfter;

        foreach (var partition in _partitions.Values.Where(p => !p.IsArchived && p.EndTime < archiveThreshold))
        {
            ct.ThrowIfCancellationRequested();
            partition.IsArchived = true;
        }

        return Task.FromResult(true);
    }

    /// <summary>
    /// Gets the shard for a specific timestamp.
    /// </summary>
    /// <param name="timestamp">The timestamp.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The shard for the timestamp.</returns>
    public Task<ShardInfo> GetShardForTimestampAsync(DateTime timestamp, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();

        var partition = EnsurePartitionExists(timestamp);

        if (!ShardRegistry.TryGetValue(partition.ShardId, out var shard))
        {
            throw new InvalidOperationException($"Shard not found for timestamp {timestamp:O}.");
        }

        return Task.FromResult(shard);
    }

    /// <summary>
    /// Gets shards that cover a time range.
    /// </summary>
    /// <param name="startTime">Start of the range (inclusive).</param>
    /// <param name="endTime">End of the range (exclusive).</param>
    /// <param name="includeArchived">Whether to include archived partitions.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Shards covering the time range.</returns>
    public Task<IReadOnlyList<ShardInfo>> GetShardsForTimeRangeAsync(
        DateTime startTime,
        DateTime endTime,
        bool includeArchived = false,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();

        var result = new List<ShardInfo>();
        var seenShards = new HashSet<string>();

        _partitionLock.EnterReadLock();
        try
        {
            foreach (var partition in _partitions.Values)
            {
                ct.ThrowIfCancellationRequested();

                // Skip archived if not requested
                if (partition.IsArchived && !includeArchived)
                    continue;

                // Check for overlap
                if (partition.StartTime < endTime && partition.EndTime > startTime)
                {
                    if (seenShards.Add(partition.ShardId) &&
                        ShardRegistry.TryGetValue(partition.ShardId, out var shard) &&
                        shard.Status == ShardStatus.Online)
                    {
                        result.Add(shard);
                    }
                }
            }
        }
        finally
        {
            _partitionLock.ExitReadLock();
        }

        return Task.FromResult<IReadOnlyList<ShardInfo>>(result);
    }

    /// <summary>
    /// Gets all partitions.
    /// </summary>
    /// <param name="includeArchived">Whether to include archived partitions.</param>
    /// <returns>All partitions.</returns>
    public IReadOnlyList<TimePartition> GetAllPartitions(bool includeArchived = false)
    {
        return _partitions.Values
            .Where(p => includeArchived || !p.IsArchived)
            .OrderBy(p => p.StartTime)
            .ToList();
    }

    /// <summary>
    /// Gets the current (active) partition.
    /// </summary>
    /// <returns>The current partition.</returns>
    public TimePartition GetCurrentPartition()
    {
        return EnsurePartitionExists(DateTime.UtcNow);
    }

    /// <summary>
    /// Archives a partition.
    /// </summary>
    /// <param name="partitionId">The partition to archive.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the partition was archived.</returns>
    public Task<bool> ArchivePartitionAsync(string partitionId, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(partitionId);

        if (_partitions.TryGetValue(partitionId, out var partition))
        {
            partition.IsArchived = true;

            // Optionally update shard status
            if (ShardRegistry.TryGetValue(partition.ShardId, out var shard))
            {
                // In a real implementation, might move data to cold storage
                UpdateShardMetrics(partition.ShardId, 0, 0);
            }

            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }

    /// <summary>
    /// Sets the pattern for extracting timestamps from keys.
    /// </summary>
    /// <param name="regexPattern">Regex pattern with capture group for timestamp.</param>
    public void SetTimestampKeyPattern(string regexPattern)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(regexPattern);
        _timestampKeyPattern = regexPattern;
        _cache.Clear();
    }

    /// <summary>
    /// Creates a time-prefixed key.
    /// </summary>
    /// <param name="timestamp">The timestamp.</param>
    /// <param name="key">The original key.</param>
    /// <returns>Time-prefixed key.</returns>
    public static string CreateTimeKey(DateTime timestamp, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return $"{timestamp:yyyy-MM-ddTHH:mm:ss}:{key}";
    }

    /// <summary>
    /// Creates a time-prefixed key using current time.
    /// </summary>
    /// <param name="key">The original key.</param>
    /// <returns>Time-prefixed key.</returns>
    public static string CreateTimeKey(string key)
    {
        return CreateTimeKey(DateTime.UtcNow, key);
    }

    /// <summary>
    /// Updates partition metrics.
    /// </summary>
    /// <param name="partitionId">The partition ID.</param>
    /// <param name="objectDelta">Change in object count.</param>
    /// <param name="sizeDelta">Change in size.</param>
    public void UpdatePartitionMetrics(string partitionId, long objectDelta, long sizeDelta)
    {
        if (_partitions.TryGetValue(partitionId, out var partition))
        {
            // Use EnterWriteLock instead of monitor lock to correctly use the ReaderWriterLockSlim.
            _partitionLock.EnterWriteLock();
            try
            {
                partition.ObjectCount += objectDelta;
                partition.SizeBytes += sizeDelta;
            }
            finally
            {
                _partitionLock.ExitWriteLock();
            }

            // Also update shard metrics
            IncrementShardMetrics(partition.ShardId, objectDelta, sizeDelta);
        }
    }

    /// <summary>
    /// Gets the partition ID for a timestamp.
    /// </summary>
    private string GetPartitionId(DateTime timestamp)
    {
        return _granularity switch
        {
            TimePartitionGranularity.Hour => timestamp.ToString("yyyy-MM-ddTHH", CultureInfo.InvariantCulture),
            TimePartitionGranularity.Day => timestamp.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            TimePartitionGranularity.Week => $"{timestamp.Year}-W{ISOWeek.GetWeekOfYear(timestamp):D2}",
            TimePartitionGranularity.Month => timestamp.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            TimePartitionGranularity.Quarter => $"{timestamp.Year}-Q{(timestamp.Month - 1) / 3 + 1}",
            TimePartitionGranularity.Year => timestamp.ToString("yyyy", CultureInfo.InvariantCulture),
            _ => timestamp.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        };
    }

    /// <summary>
    /// Gets the start time for a partition.
    /// </summary>
    private DateTime GetPartitionStartTime(DateTime timestamp)
    {
        return _granularity switch
        {
            TimePartitionGranularity.Hour => new DateTime(timestamp.Year, timestamp.Month, timestamp.Day, timestamp.Hour, 0, 0, DateTimeKind.Utc),
            TimePartitionGranularity.Day => timestamp.Date,
            TimePartitionGranularity.Week => ISOWeek.ToDateTime(timestamp.Year, ISOWeek.GetWeekOfYear(timestamp), DayOfWeek.Monday),
            TimePartitionGranularity.Month => new DateTime(timestamp.Year, timestamp.Month, 1, 0, 0, 0, DateTimeKind.Utc),
            TimePartitionGranularity.Quarter => new DateTime(timestamp.Year, ((timestamp.Month - 1) / 3) * 3 + 1, 1, 0, 0, 0, DateTimeKind.Utc),
            TimePartitionGranularity.Year => new DateTime(timestamp.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            _ => timestamp.Date
        };
    }

    /// <summary>
    /// Gets the end time for a partition.
    /// </summary>
    private DateTime GetPartitionEndTime(DateTime startTime)
    {
        return _granularity switch
        {
            TimePartitionGranularity.Hour => startTime.AddHours(1),
            TimePartitionGranularity.Day => startTime.AddDays(1),
            TimePartitionGranularity.Week => startTime.AddDays(7),
            TimePartitionGranularity.Month => startTime.AddMonths(1),
            TimePartitionGranularity.Quarter => startTime.AddMonths(3),
            TimePartitionGranularity.Year => startTime.AddYears(1),
            _ => startTime.AddDays(1)
        };
    }

    /// <summary>
    /// Adds granularity units to a timestamp.
    /// </summary>
    private DateTime AddGranularityUnits(DateTime timestamp, int units)
    {
        return _granularity switch
        {
            TimePartitionGranularity.Hour => timestamp.AddHours(units),
            TimePartitionGranularity.Day => timestamp.AddDays(units),
            TimePartitionGranularity.Week => timestamp.AddDays(units * 7),
            TimePartitionGranularity.Month => timestamp.AddMonths(units),
            TimePartitionGranularity.Quarter => timestamp.AddMonths(units * 3),
            TimePartitionGranularity.Year => timestamp.AddYears(units),
            _ => timestamp.AddDays(units)
        };
    }

    /// <summary>
    /// Ensures a partition exists for the given timestamp.
    /// </summary>
    private TimePartition EnsurePartitionExists(DateTime timestamp)
    {
        var partitionId = GetPartitionId(timestamp);

        if (_partitions.TryGetValue(partitionId, out var existing))
        {
            return existing;
        }

        _partitionLock.EnterWriteLock();
        try
        {
            // Double-check after acquiring lock
            if (_partitions.TryGetValue(partitionId, out existing))
            {
                return existing;
            }

            var startTime = GetPartitionStartTime(timestamp);
            var endTime = GetPartitionEndTime(startTime);
            var shardId = $"shard-time-{partitionId}";

            // Create shard if not exists
            if (!ShardRegistry.ContainsKey(shardId))
            {
                ShardRegistry[shardId] = new ShardInfo(
                    shardId,
                    $"node-time/{partitionId}",
                    ShardStatus.Online,
                    0, 0)
                {
                    CreatedAt = DateTime.UtcNow,
                    LastModifiedAt = DateTime.UtcNow
                };
            }

            var partition = new TimePartition
            {
                PartitionId = partitionId,
                StartTime = startTime,
                EndTime = endTime,
                ShardId = shardId,
                CreatedAt = DateTime.UtcNow
            };

            _partitions[partitionId] = partition;
            return partition;
        }
        finally
        {
            _partitionLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Extracts timestamp from a key.
    /// </summary>
    private DateTime ExtractTimestamp(string key)
    {
        // Try ISO 8601 format at the start of the key
        if (key.Length >= 19)
        {
            var potentialTimestamp = key.Substring(0, 19);
            if (DateTime.TryParseExact(
                potentialTimestamp,
                "yyyy-MM-ddTHH:mm:ss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var timestamp))
            {
                return timestamp;
            }
        }

        // Try date only format
        if (key.Length >= 10)
        {
            var potentialDate = key.Substring(0, 10);
            if (DateTime.TryParseExact(
                potentialDate,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var date))
            {
                return date;
            }
        }

        // Try regex pattern
        var match = System.Text.RegularExpressions.Regex.Match(key, _timestampKeyPattern);
        if (match.Success && match.Groups.Count > 1)
        {
            if (DateTime.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedTime))
            {
                return parsedTime;
            }
        }

        // Default to current time
        return DateTime.UtcNow;
    }

    /// <summary>
    /// Performs partition maintenance.
    /// </summary>
    private void MaintenancePartitions()
    {
        try
        {
            // Create future partitions
            var now = DateTime.UtcNow;
            EnsurePartitionExists(AddGranularityUnits(now, 1));

            // Archive old partitions
            var archiveThreshold = now - _archiveAfter;
            foreach (var partition in _partitions.Values.Where(p => !p.IsArchived && p.EndTime < archiveThreshold))
            {
                partition.IsArchived = true;
            }
        }
        catch
        {

            // Swallow exceptions in maintenance
            System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
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
        _partitionMaintenanceTimer?.Dispose();
        _partitionLock.Dispose();
        return Task.CompletedTask;
    }
}
