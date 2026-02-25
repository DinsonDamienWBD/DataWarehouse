using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.Retention;

/// <summary>
/// Inactivity-based retention strategy that deletes data if unused for a period.
/// Tracks access patterns to identify stale data for cleanup.
/// </summary>
/// <remarks>
/// Features:
/// - Access tracking for read/write operations
/// - Configurable inactivity thresholds
/// - Content-type specific thresholds
/// - Warning period before deletion
/// - Access pattern analytics
/// </remarks>
public sealed class InactivityBasedRetentionStrategy : RetentionStrategyBase
{
    private readonly BoundedDictionary<string, AccessRecord> _accessRecords = new BoundedDictionary<string, AccessRecord>(1000);
    private readonly TimeSpan _defaultInactivityThreshold;
    private readonly TimeSpan _warningPeriod;
    private readonly Dictionary<string, TimeSpan> _contentTypeThresholds = new();
    private readonly bool _trackWrites;
    private readonly bool _trackReads;

    /// <summary>
    /// Initializes with default 90-day inactivity threshold.
    /// </summary>
    public InactivityBasedRetentionStrategy() : this(TimeSpan.FromDays(90), TimeSpan.FromDays(7)) { }

    /// <summary>
    /// Initializes with specified inactivity threshold and warning period.
    /// </summary>
    /// <param name="inactivityThreshold">Time of inactivity before deletion.</param>
    /// <param name="warningPeriod">Warning period before deletion.</param>
    /// <param name="trackReads">Whether to consider reads as activity.</param>
    /// <param name="trackWrites">Whether to consider writes as activity.</param>
    public InactivityBasedRetentionStrategy(
        TimeSpan inactivityThreshold,
        TimeSpan warningPeriod,
        bool trackReads = true,
        bool trackWrites = true)
    {
        if (inactivityThreshold <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(inactivityThreshold), "Inactivity threshold must be positive");
        if (warningPeriod < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(warningPeriod), "Warning period cannot be negative");

        _defaultInactivityThreshold = inactivityThreshold;
        _warningPeriod = warningPeriod;
        _trackReads = trackReads;
        _trackWrites = trackWrites;
    }

    /// <inheritdoc/>
    public override string StrategyId => "retention.inactivity";

    /// <inheritdoc/>
    public override string DisplayName => "Inactivity-Based Retention";

    /// <inheritdoc/>
    public override DataManagementCapabilities Capabilities { get; } = new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = false,
        SupportsTransactions = false,
        SupportsTTL = false,
        MaxThroughput = 100_000,
        TypicalLatencyMs = 0.5
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        $"Inactivity-based retention that deletes data unused for {_defaultInactivityThreshold.TotalDays} days. " +
        $"Provides {_warningPeriod.TotalDays}-day warning before deletion. " +
        "Tracks read and write access patterns to identify stale data.";

    /// <inheritdoc/>
    public override string[] Tags => ["retention", "inactivity", "access-tracking", "stale-data", "cleanup"];

    /// <summary>
    /// Gets the default inactivity threshold.
    /// </summary>
    public TimeSpan InactivityThreshold => _defaultInactivityThreshold;

    /// <summary>
    /// Gets the warning period.
    /// </summary>
    public TimeSpan WarningPeriod => _warningPeriod;

    /// <summary>
    /// Sets an inactivity threshold for a specific content type.
    /// </summary>
    /// <param name="contentType">Content type (MIME type).</param>
    /// <param name="threshold">Inactivity threshold.</param>
    public void SetContentTypeThreshold(string contentType, TimeSpan threshold)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        _contentTypeThresholds[contentType.ToLowerInvariant()] = threshold;
    }

    /// <summary>
    /// Records a read access for an object.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    public void RecordRead(string objectId)
    {
        if (!_trackReads) return;

        var record = GetOrCreateRecord(objectId);
        record.LastReadAt = DateTime.UtcNow;
        Interlocked.Increment(ref record.ReadCount);
        record.LastAccessAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Records a write access for an object.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    public void RecordWrite(string objectId)
    {
        if (!_trackWrites) return;

        var record = GetOrCreateRecord(objectId);
        record.LastWriteAt = DateTime.UtcNow;
        Interlocked.Increment(ref record.WriteCount);
        record.LastAccessAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Gets the access record for an object.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <returns>Access record or null.</returns>
    public AccessRecord? GetAccessRecord(string objectId)
    {
        return _accessRecords.TryGetValue(objectId, out var record) ? record : null;
    }

    /// <summary>
    /// Gets objects that will expire within the specified time.
    /// </summary>
    /// <param name="within">Time window.</param>
    /// <returns>List of object IDs that will expire.</returns>
    public IReadOnlyList<string> GetExpiringObjects(TimeSpan within)
    {
        var expirationThreshold = DateTime.UtcNow + within;
        var result = new List<string>();

        foreach (var record in _accessRecords.Values)
        {
            var threshold = GetThreshold(record.ContentType);
            var expirationDate = record.LastAccessAt + threshold;

            if (expirationDate <= expirationThreshold)
            {
                result.Add(record.ObjectId);
            }
        }

        return result.AsReadOnly();
    }

    /// <summary>
    /// Gets access pattern statistics.
    /// </summary>
    /// <returns>Access pattern statistics.</returns>
    public AccessPatternStats GetAccessStats()
    {
        var records = _accessRecords.Values.ToList();

        if (records.Count == 0)
        {
            return new AccessPatternStats
            {
                TotalObjects = 0,
                ActiveObjects = 0,
                InactiveObjects = 0,
                StaleObjects = 0,
                AverageReadsPerObject = 0,
                AverageWritesPerObject = 0
            };
        }

        var now = DateTime.UtcNow;
        var activeThreshold = TimeSpan.FromDays(7);
        var staleThreshold = _defaultInactivityThreshold;

        return new AccessPatternStats
        {
            TotalObjects = records.Count,
            ActiveObjects = records.Count(r => now - r.LastAccessAt < activeThreshold),
            InactiveObjects = records.Count(r => now - r.LastAccessAt >= activeThreshold && now - r.LastAccessAt < staleThreshold),
            StaleObjects = records.Count(r => now - r.LastAccessAt >= staleThreshold),
            AverageReadsPerObject = records.Average(r => r.ReadCount),
            AverageWritesPerObject = records.Average(r => r.WriteCount),
            MostAccessedObjects = records.OrderByDescending(r => r.ReadCount + r.WriteCount).Take(10).Select(r => r.ObjectId).ToList(),
            LeastAccessedObjects = records.OrderBy(r => r.LastAccessAt).Take(10).Select(r => r.ObjectId).ToList()
        };
    }

    /// <inheritdoc/>
    protected override Task<RetentionDecision> EvaluateCoreAsync(DataObject data, CancellationToken ct)
    {
        var record = GetOrCreateRecord(data.ObjectId, data.ContentType);
        var threshold = GetThreshold(data.ContentType);
        var now = DateTime.UtcNow;

        // Use explicit access record or fall back to object metadata
        var lastAccess = record.LastAccessAt;
        if (lastAccess == default)
        {
            lastAccess = data.LastAccessedAt ?? data.LastModifiedAt ?? data.CreatedAt;
            record.LastAccessAt = lastAccess;
        }

        var inactivityDuration = now - lastAccess;
        var expirationDate = lastAccess + threshold;
        var warningDate = expirationDate - _warningPeriod;

        // Still active
        if (inactivityDuration < threshold - _warningPeriod)
        {
            return Task.FromResult(RetentionDecision.Retain(
                $"Last accessed {inactivityDuration.TotalDays:F0} days ago - active",
                warningDate));
        }

        // In warning period
        if (inactivityDuration < threshold)
        {
            var daysUntilDeletion = (expirationDate - now).TotalDays;
            return Task.FromResult(new RetentionDecision
            {
                Action = RetentionAction.Retain,
                Reason = $"WARNING: Inactive for {inactivityDuration.TotalDays:F0} days - deletion in {daysUntilDeletion:F0} days",
                NextEvaluationDate = expirationDate,
                Metadata = new Dictionary<string, object>
                {
                    ["InactivityDays"] = inactivityDuration.TotalDays,
                    ["DaysUntilDeletion"] = daysUntilDeletion,
                    ["LastAccess"] = lastAccess
                }
            });
        }

        // Inactive - eligible for deletion
        return Task.FromResult(new RetentionDecision
        {
            Action = RetentionAction.Delete,
            Reason = $"Inactive for {inactivityDuration.TotalDays:F0} days (threshold: {threshold.TotalDays} days)",
            Metadata = new Dictionary<string, object>
            {
                ["InactivityDays"] = inactivityDuration.TotalDays,
                ["LastAccess"] = lastAccess
            }
        });
    }

    private AccessRecord GetOrCreateRecord(string objectId, string? contentType = null)
    {
        return _accessRecords.GetOrAdd(objectId, _ => new AccessRecord
        {
            ObjectId = objectId,
            ContentType = contentType,
            CreatedAt = DateTime.UtcNow,
            LastAccessAt = DateTime.UtcNow
        });
    }

    private TimeSpan GetThreshold(string? contentType)
    {
        if (!string.IsNullOrEmpty(contentType))
        {
            if (_contentTypeThresholds.TryGetValue(contentType.ToLowerInvariant(), out var threshold))
            {
                return threshold;
            }

            // Try category match
            var category = contentType.Split('/')[0] + "/*";
            if (_contentTypeThresholds.TryGetValue(category, out threshold))
            {
                return threshold;
            }
        }

        return _defaultInactivityThreshold;
    }

    /// <inheritdoc/>
    protected override Task DisposeCoreAsync()
    {
        _accessRecords.Clear();
        return Task.CompletedTask;
    }
}

/// <summary>
/// Record of access patterns for an object.
/// </summary>
public sealed class AccessRecord
{
    /// <summary>
    /// Object identifier.
    /// </summary>
    public required string ObjectId { get; init; }

    /// <summary>
    /// Content type.
    /// </summary>
    public string? ContentType { get; init; }

    /// <summary>
    /// When tracking started.
    /// </summary>
    public required DateTime CreatedAt { get; init; }

    /// <summary>
    /// Last read access time.
    /// </summary>
    public DateTime? LastReadAt { get; set; }

    /// <summary>
    /// Last write access time.
    /// </summary>
    public DateTime? LastWriteAt { get; set; }

    /// <summary>
    /// Last access time (read or write).
    /// </summary>
    public DateTime LastAccessAt { get; set; }

    /// <summary>
    /// Total read count.
    /// </summary>
    public long ReadCount;

    /// <summary>
    /// Total write count.
    /// </summary>
    public long WriteCount;

    /// <summary>
    /// Total access count.
    /// </summary>
    public long TotalAccessCount => ReadCount + WriteCount;

    /// <summary>
    /// Time since last access.
    /// </summary>
    public TimeSpan InactivityDuration => DateTime.UtcNow - LastAccessAt;
}

/// <summary>
/// Statistics about access patterns.
/// </summary>
public sealed class AccessPatternStats
{
    /// <summary>
    /// Total tracked objects.
    /// </summary>
    public int TotalObjects { get; init; }

    /// <summary>
    /// Recently active objects.
    /// </summary>
    public int ActiveObjects { get; init; }

    /// <summary>
    /// Inactive but not yet stale objects.
    /// </summary>
    public int InactiveObjects { get; init; }

    /// <summary>
    /// Stale objects eligible for deletion.
    /// </summary>
    public int StaleObjects { get; init; }

    /// <summary>
    /// Average reads per object.
    /// </summary>
    public double AverageReadsPerObject { get; init; }

    /// <summary>
    /// Average writes per object.
    /// </summary>
    public double AverageWritesPerObject { get; init; }

    /// <summary>
    /// Most frequently accessed objects.
    /// </summary>
    public List<string>? MostAccessedObjects { get; init; }

    /// <summary>
    /// Least recently accessed objects.
    /// </summary>
    public List<string>? LeastAccessedObjects { get; init; }
}
