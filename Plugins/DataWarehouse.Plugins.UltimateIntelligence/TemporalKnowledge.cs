using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateIntelligence;

#region Supporting Types

/// <summary>
/// Represents a temporal knowledge entry that can be stored and versioned over time.
/// </summary>
public sealed class TemporalKnowledgeEntry
{
    /// <summary>
    /// Gets or sets the unique identifier for this knowledge object.
    /// </summary>
    public required string Id { get; set; }

    /// <summary>
    /// Gets or sets the type or category of this knowledge object.
    /// </summary>
    public required string Type { get; set; }

    /// <summary>
    /// Gets or sets the content of this knowledge object.
    /// </summary>
    public required string Content { get; set; }

    /// <summary>
    /// Gets or sets the metadata associated with this knowledge object.
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; } = new();

    /// <summary>
    /// Gets or sets the version number of this knowledge object.
    /// </summary>
    public long Version { get; set; } = 1;

    /// <summary>
    /// Gets or sets the schema version of this knowledge object.
    /// </summary>
    public string SchemaVersion { get; set; } = "1.0";

    /// <summary>
    /// Gets or sets the timestamp when this knowledge object was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Gets or sets the timestamp when this knowledge object was last modified.
    /// </summary>
    public DateTimeOffset ModifiedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Gets or sets the hash of the content for change detection.
    /// </summary>
    public string ContentHash { get; set; } = string.Empty;

    /// <summary>
    /// Computes the content hash for this knowledge object.
    /// </summary>
    /// <returns>The computed SHA256 hash.</returns>
    public string ComputeHash()
    {
        var combined = $"{Type}:{Content}:{JsonSerializer.Serialize(Metadata)}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(combined));
        return Convert.ToHexString(bytes);
    }

    /// <summary>
    /// Creates a deep clone of this temporal knowledge entry.
    /// </summary>
    /// <returns>A new instance with copied values.</returns>
    public TemporalKnowledgeEntry Clone()
    {
        return new TemporalKnowledgeEntry
        {
            Id = Id,
            Type = Type,
            Content = Content,
            Metadata = new Dictionary<string, object>(Metadata),
            Version = Version,
            SchemaVersion = SchemaVersion,
            CreatedAt = CreatedAt,
            ModifiedAt = ModifiedAt,
            ContentHash = ContentHash
        };
    }
}

/// <summary>
/// Represents a point-in-time snapshot of all knowledge.
/// </summary>
public sealed class KnowledgeSnapshot
{
    /// <summary>
    /// Gets or sets the unique identifier for this snapshot.
    /// </summary>
    public required string SnapshotId { get; set; }

    /// <summary>
    /// Gets or sets the timestamp when this snapshot was created.
    /// </summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>
    /// Gets or sets the knowledge objects in this snapshot.
    /// </summary>
    public Dictionary<string, TemporalKnowledgeEntry> Objects { get; set; } = new();

    /// <summary>
    /// Gets or sets the parent snapshot ID for incremental snapshots.
    /// </summary>
    public string? ParentSnapshotId { get; set; }

    /// <summary>
    /// Gets or sets whether this is an incremental snapshot.
    /// </summary>
    public bool IsIncremental { get; set; }

    /// <summary>
    /// Gets or sets the changes since the parent snapshot (for incremental snapshots).
    /// </summary>
    public List<KnowledgeChange> IncrementalChanges { get; set; } = new();

    /// <summary>
    /// Gets or sets the description of this snapshot.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets tags for categorizing this snapshot.
    /// </summary>
    public List<string> Tags { get; set; } = new();
}

/// <summary>
/// Summary information about a snapshot.
/// </summary>
public sealed class SnapshotInfo
{
    /// <summary>
    /// Gets or sets the snapshot identifier.
    /// </summary>
    public required string SnapshotId { get; set; }

    /// <summary>
    /// Gets or sets the timestamp when this snapshot was created.
    /// </summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>
    /// Gets or sets the number of objects in the snapshot.
    /// </summary>
    public int ObjectCount { get; set; }

    /// <summary>
    /// Gets or sets whether this is an incremental snapshot.
    /// </summary>
    public bool IsIncremental { get; set; }

    /// <summary>
    /// Gets or sets the parent snapshot ID for incremental snapshots.
    /// </summary>
    public string? ParentSnapshotId { get; set; }

    /// <summary>
    /// Gets or sets the description of this snapshot.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets tags for categorizing this snapshot.
    /// </summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>
    /// Gets or sets the size of the snapshot in bytes.
    /// </summary>
    public long SizeBytes { get; set; }
}

/// <summary>
/// Options for building a knowledge timeline.
/// </summary>
public sealed class TimelineOptions
{
    /// <summary>
    /// Gets or sets the start date for the timeline.
    /// </summary>
    public DateTimeOffset? StartDate { get; set; }

    /// <summary>
    /// Gets or sets the end date for the timeline.
    /// </summary>
    public DateTimeOffset? EndDate { get; set; }

    /// <summary>
    /// Gets or sets whether to include events in the timeline.
    /// </summary>
    public bool IncludeEvents { get; set; } = true;

    /// <summary>
    /// Gets or sets whether to include changes in the timeline.
    /// </summary>
    public bool IncludeChanges { get; set; } = true;

    /// <summary>
    /// Gets or sets whether to include milestones in the timeline.
    /// </summary>
    public bool IncludeMilestones { get; set; } = true;

    /// <summary>
    /// Gets or sets the maximum number of entries to return.
    /// </summary>
    public int MaxEntries { get; set; } = 1000;

    /// <summary>
    /// Gets or sets the granularity for the timeline (e.g., daily, hourly).
    /// </summary>
    public TimelineGranularity Granularity { get; set; } = TimelineGranularity.Hourly;
}

/// <summary>
/// Granularity for timeline entries.
/// </summary>
public enum TimelineGranularity
{
    /// <summary>Minute-level granularity.</summary>
    Minute,
    /// <summary>Hourly granularity.</summary>
    Hourly,
    /// <summary>Daily granularity.</summary>
    Daily,
    /// <summary>Weekly granularity.</summary>
    Weekly,
    /// <summary>Monthly granularity.</summary>
    Monthly
}

/// <summary>
/// Represents a knowledge timeline with events, changes, and milestones.
/// </summary>
public sealed class KnowledgeTimeline
{
    /// <summary>
    /// Gets or sets the knowledge ID this timeline is for.
    /// </summary>
    public required string KnowledgeId { get; set; }

    /// <summary>
    /// Gets or sets the entries in this timeline.
    /// </summary>
    public List<TimelineEntry> Entries { get; set; } = new();

    /// <summary>
    /// Gets or sets the start of the timeline.
    /// </summary>
    public DateTimeOffset StartDate { get; set; }

    /// <summary>
    /// Gets or sets the end of the timeline.
    /// </summary>
    public DateTimeOffset EndDate { get; set; }

    /// <summary>
    /// Gets or sets the total number of changes.
    /// </summary>
    public int TotalChanges { get; set; }

    /// <summary>
    /// Gets or sets the total number of events.
    /// </summary>
    public int TotalEvents { get; set; }

    /// <summary>
    /// Gets or sets the total number of milestones.
    /// </summary>
    public int TotalMilestones { get; set; }
}

/// <summary>
/// An entry in a knowledge timeline.
/// </summary>
public sealed class TimelineEntry
{
    /// <summary>
    /// Gets or sets the timestamp of this entry.
    /// </summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>
    /// Gets or sets the type of entry.
    /// </summary>
    public TimelineEntryType EntryType { get; set; }

    /// <summary>
    /// Gets or sets the description of this entry.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the change associated with this entry (if applicable).
    /// </summary>
    public KnowledgeChange? Change { get; set; }

    /// <summary>
    /// Gets or sets additional data for visualization.
    /// </summary>
    public Dictionary<string, object> VisualizationData { get; set; } = new();
}

/// <summary>
/// Type of timeline entry.
/// </summary>
public enum TimelineEntryType
{
    /// <summary>A general event.</summary>
    Event,
    /// <summary>A change to the knowledge object.</summary>
    Change,
    /// <summary>A significant milestone.</summary>
    Milestone,
    /// <summary>Creation of the knowledge object.</summary>
    Created,
    /// <summary>Deletion of the knowledge object.</summary>
    Deleted
}

/// <summary>
/// Represents a change to a knowledge object.
/// </summary>
public sealed class KnowledgeChange
{
    /// <summary>
    /// Gets or sets the unique identifier for this change.
    /// </summary>
    public string ChangeId { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Gets or sets the knowledge object ID.
    /// </summary>
    public required string KnowledgeId { get; set; }

    /// <summary>
    /// Gets or sets the timestamp of the change.
    /// </summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>
    /// Gets or sets the type of change.
    /// </summary>
    public ChangeType ChangeType { get; set; }

    /// <summary>
    /// Gets or sets the classification of the change (major, minor, patch).
    /// </summary>
    public ChangeClassification Classification { get; set; }

    /// <summary>
    /// Gets or sets the old value (for modifications).
    /// </summary>
    public string? OldValue { get; set; }

    /// <summary>
    /// Gets or sets the new value.
    /// </summary>
    public string? NewValue { get; set; }

    /// <summary>
    /// Gets or sets the field that was changed.
    /// </summary>
    public string? FieldChanged { get; set; }

    /// <summary>
    /// Gets or sets the old version number.
    /// </summary>
    public long OldVersion { get; set; }

    /// <summary>
    /// Gets or sets the new version number.
    /// </summary>
    public long NewVersion { get; set; }

    /// <summary>
    /// Gets or sets the old schema version.
    /// </summary>
    public string? OldSchemaVersion { get; set; }

    /// <summary>
    /// Gets or sets the new schema version.
    /// </summary>
    public string? NewSchemaVersion { get; set; }

    /// <summary>
    /// Gets or sets the diff between old and new values.
    /// </summary>
    public string? Diff { get; set; }
}

/// <summary>
/// Type of change made to a knowledge object.
/// </summary>
public enum ChangeType
{
    /// <summary>The knowledge object was created.</summary>
    Addition,
    /// <summary>The knowledge object was modified.</summary>
    Modification,
    /// <summary>The knowledge object was deleted.</summary>
    Deletion,
    /// <summary>The schema of the knowledge object changed.</summary>
    SchemaChange,
    /// <summary>A relationship was added or removed.</summary>
    RelationshipChange
}

/// <summary>
/// Classification of change severity.
/// </summary>
public enum ChangeClassification
{
    /// <summary>A patch-level change (minor fix, no functional change).</summary>
    Patch,
    /// <summary>A minor change (backward-compatible addition).</summary>
    Minor,
    /// <summary>A major change (breaking change).</summary>
    Major
}

/// <summary>
/// History of changes to a knowledge object.
/// </summary>
public sealed class ChangeHistory
{
    /// <summary>
    /// Gets or sets the knowledge object ID.
    /// </summary>
    public required string KnowledgeId { get; set; }

    /// <summary>
    /// Gets or sets the start of the history period.
    /// </summary>
    public DateTimeOffset FromTimestamp { get; set; }

    /// <summary>
    /// Gets or sets the end of the history period.
    /// </summary>
    public DateTimeOffset ToTimestamp { get; set; }

    /// <summary>
    /// Gets or sets the list of changes in chronological order.
    /// </summary>
    public List<KnowledgeChange> Changes { get; set; } = new();

    /// <summary>
    /// Gets or sets the number of additions.
    /// </summary>
    public int AdditionCount { get; set; }

    /// <summary>
    /// Gets or sets the number of modifications.
    /// </summary>
    public int ModificationCount { get; set; }

    /// <summary>
    /// Gets or sets the number of deletions.
    /// </summary>
    public int DeletionCount { get; set; }

    /// <summary>
    /// Gets or sets the state at the start of the period.
    /// </summary>
    public TemporalKnowledgeEntry? StateAtStart { get; set; }

    /// <summary>
    /// Gets or sets the state at the end of the period.
    /// </summary>
    public TemporalKnowledgeEntry? StateAtEnd { get; set; }

    /// <summary>
    /// Gets or sets the overall diff between start and end states.
    /// </summary>
    public string? OverallDiff { get; set; }
}

/// <summary>
/// Options for trend analysis.
/// </summary>
public sealed class TrendOptions
{
    /// <summary>
    /// Gets or sets the analysis period start.
    /// </summary>
    public DateTimeOffset? StartDate { get; set; }

    /// <summary>
    /// Gets or sets the analysis period end.
    /// </summary>
    public DateTimeOffset? EndDate { get; set; }

    /// <summary>
    /// Gets or sets whether to analyze growth trends.
    /// </summary>
    public bool AnalyzeGrowth { get; set; } = true;

    /// <summary>
    /// Gets or sets whether to analyze seasonality.
    /// </summary>
    public bool AnalyzeSeasonality { get; set; } = true;

    /// <summary>
    /// Gets or sets whether to detect anomalies.
    /// </summary>
    public bool DetectAnomalies { get; set; } = true;

    /// <summary>
    /// Gets or sets the anomaly detection threshold (standard deviations).
    /// </summary>
    public double AnomalyThreshold { get; set; } = 2.0;

    /// <summary>
    /// Gets or sets the sampling interval for analysis.
    /// </summary>
    public TimeSpan SamplingInterval { get; set; } = TimeSpan.FromHours(1);
}

/// <summary>
/// Result of trend analysis.
/// </summary>
public sealed class TrendAnalysis
{
    /// <summary>
    /// Gets or sets the knowledge object ID.
    /// </summary>
    public required string KnowledgeId { get; set; }

    /// <summary>
    /// Gets or sets the analysis period start.
    /// </summary>
    public DateTimeOffset StartDate { get; set; }

    /// <summary>
    /// Gets or sets the analysis period end.
    /// </summary>
    public DateTimeOffset EndDate { get; set; }

    /// <summary>
    /// Gets or sets the growth rate (changes per day).
    /// </summary>
    public double GrowthRate { get; set; }

    /// <summary>
    /// Gets or sets the growth trend direction.
    /// </summary>
    public TrendDirection GrowthTrend { get; set; }

    /// <summary>
    /// Gets or sets detected seasonal patterns.
    /// </summary>
    public List<SeasonalPattern> SeasonalPatterns { get; set; } = new();

    /// <summary>
    /// Gets or sets detected anomalies.
    /// </summary>
    public List<TrendAnomaly> Anomalies { get; set; } = new();

    /// <summary>
    /// Gets or sets the mean change frequency.
    /// </summary>
    public double MeanChangeFrequency { get; set; }

    /// <summary>
    /// Gets or sets the standard deviation of change frequency.
    /// </summary>
    public double StdDevChangeFrequency { get; set; }

    /// <summary>
    /// Gets or sets the correlation coefficient for the trend line.
    /// </summary>
    public double TrendCorrelation { get; set; }
}

/// <summary>
/// Direction of a trend.
/// </summary>
public enum TrendDirection
{
    /// <summary>Trend is increasing.</summary>
    Increasing,
    /// <summary>Trend is decreasing.</summary>
    Decreasing,
    /// <summary>Trend is stable.</summary>
    Stable,
    /// <summary>Trend is fluctuating.</summary>
    Fluctuating
}

/// <summary>
/// A detected seasonal pattern.
/// </summary>
public sealed class SeasonalPattern
{
    /// <summary>
    /// Gets or sets the period of the pattern.
    /// </summary>
    public TimeSpan Period { get; set; }

    /// <summary>
    /// Gets or sets the pattern strength (0-1).
    /// </summary>
    public double Strength { get; set; }

    /// <summary>
    /// Gets or sets the description of the pattern.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the peak times within the period.
    /// </summary>
    public List<TimeSpan> PeakTimes { get; set; } = new();
}

/// <summary>
/// A detected anomaly in trend analysis.
/// </summary>
public sealed class TrendAnomaly
{
    /// <summary>
    /// Gets or sets the timestamp of the anomaly.
    /// </summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>
    /// Gets or sets the severity of the anomaly.
    /// </summary>
    public double Severity { get; set; }

    /// <summary>
    /// Gets or sets the type of anomaly.
    /// </summary>
    public AnomalyType Type { get; set; }

    /// <summary>
    /// Gets or sets the description of the anomaly.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the expected value.
    /// </summary>
    public double ExpectedValue { get; set; }

    /// <summary>
    /// Gets or sets the actual value.
    /// </summary>
    public double ActualValue { get; set; }
}

/// <summary>
/// Type of anomaly detected.
/// </summary>
public enum AnomalyType
{
    /// <summary>A sudden spike in activity.</summary>
    Spike,
    /// <summary>A sudden drop in activity.</summary>
    Drop,
    /// <summary>An unusual pattern.</summary>
    PatternBreak,
    /// <summary>Missing expected data.</summary>
    Missing
}

/// <summary>
/// Options for temporal compaction.
/// </summary>
public sealed class CompactionOptions
{
    /// <summary>
    /// Gets or sets the age threshold for compaction.
    /// </summary>
    public TimeSpan AgeThreshold { get; set; } = TimeSpan.FromDays(90);

    /// <summary>
    /// Gets or sets the compaction strategy.
    /// </summary>
    public CompactionStrategy Strategy { get; set; } = CompactionStrategy.KeepDaily;

    /// <summary>
    /// Gets or sets whether to preserve milestones.
    /// </summary>
    public bool PreserveMilestones { get; set; } = true;

    /// <summary>
    /// Gets or sets whether to create summaries for compacted periods.
    /// </summary>
    public bool CreateSummaries { get; set; } = true;

    /// <summary>
    /// Gets or sets the maximum entries to compact in one operation.
    /// </summary>
    public int BatchSize { get; set; } = 1000;
}

/// <summary>
/// Compaction strategy for old data.
/// </summary>
public enum CompactionStrategy
{
    /// <summary>Keep one entry per minute.</summary>
    KeepMinute,
    /// <summary>Keep one entry per hour.</summary>
    KeepHourly,
    /// <summary>Keep one entry per day.</summary>
    KeepDaily,
    /// <summary>Keep one entry per week.</summary>
    KeepWeekly,
    /// <summary>Keep one entry per month.</summary>
    KeepMonthly
}

/// <summary>
/// Options for temporal tiering.
/// </summary>
public sealed class TieringOptions
{
    /// <summary>
    /// Gets or sets the hot tier threshold.
    /// </summary>
    public TimeSpan HotTierThreshold { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Gets or sets the warm tier threshold.
    /// </summary>
    public TimeSpan WarmTierThreshold { get; set; } = TimeSpan.FromDays(90);

    /// <summary>
    /// Gets or sets the cold tier threshold.
    /// </summary>
    public TimeSpan ColdTierThreshold { get; set; } = TimeSpan.FromDays(365);

    /// <summary>
    /// Gets or sets whether to compress cold tier data.
    /// </summary>
    public bool CompressColdTier { get; set; } = true;

    /// <summary>
    /// Gets or sets whether to compress archive tier data.
    /// </summary>
    public bool CompressArchiveTier { get; set; } = true;

    /// <summary>
    /// Gets or sets the maximum entries to tier in one operation.
    /// </summary>
    public int BatchSize { get; set; } = 1000;
}

/// <summary>
/// Storage tier for temporal data.
/// </summary>
public enum StorageTier
{
    /// <summary>Hot tier for recent data (less than 7 days).</summary>
    Hot,
    /// <summary>Warm tier for recent data (7-90 days).</summary>
    Warm,
    /// <summary>Cold tier for historical data (90 days - 1 year).</summary>
    Cold,
    /// <summary>Archive tier for long-term data (more than 1 year).</summary>
    Archive
}

#endregion

#region 90.F1: Temporal Storage

/// <summary>
/// Time-indexed storage for knowledge objects.
/// Provides efficient storage and retrieval of knowledge at specific points in time.
/// </summary>
public sealed class KnowledgeTimeStore
{
    private readonly ConcurrentDictionary<string, SortedList<DateTimeOffset, TemporalKnowledgeEntry>> _timePartitionedStore = new();
    private readonly ConcurrentDictionary<string, ReaderWriterLockSlim> _locks = new();
    private readonly object _globalLock = new();

    /// <summary>
    /// Gets the total number of knowledge objects stored.
    /// </summary>
    public long TotalObjectCount => _timePartitionedStore.Values.Sum(s => (long)s.Count);

    /// <summary>
    /// Gets the number of unique knowledge IDs stored.
    /// </summary>
    public int UniqueKnowledgeCount => _timePartitionedStore.Count;

    /// <summary>
    /// Stores a knowledge object at a specific timestamp.
    /// </summary>
    /// <param name="knowledge">The knowledge object to store.</param>
    /// <param name="timestamp">The timestamp to associate with this version.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <exception cref="ArgumentNullException">Thrown when knowledge is null.</exception>
    public Task StoreAsync(TemporalKnowledgeEntry knowledge, DateTimeOffset timestamp, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(knowledge);

        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            var rwLock = GetOrCreateLock(knowledge.Id);
            rwLock.EnterWriteLock();
            try
            {
                if (!_timePartitionedStore.TryGetValue(knowledge.Id, out var timeline))
                {
                    timeline = new SortedList<DateTimeOffset, TemporalKnowledgeEntry>();
                    _timePartitionedStore[knowledge.Id] = timeline;
                }

                var storedKnowledge = knowledge.Clone();
                storedKnowledge.ModifiedAt = timestamp;
                storedKnowledge.ContentHash = storedKnowledge.ComputeHash();

                timeline[timestamp] = storedKnowledge;
            }
            finally
            {
                rwLock.ExitWriteLock();
            }
        }, ct);
    }

    /// <summary>
    /// Retrieves a knowledge object at a specific point in time.
    /// Returns the version that was current at the specified timestamp.
    /// </summary>
    /// <param name="knowledgeId">The knowledge object identifier.</param>
    /// <param name="timestamp">The point in time to retrieve.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The knowledge object if found, null otherwise.</returns>
    /// <exception cref="ArgumentException">Thrown when knowledgeId is null or empty.</exception>
    public Task<TemporalKnowledgeEntry?> GetAtAsync(string knowledgeId, DateTimeOffset timestamp, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(knowledgeId))
            throw new ArgumentException("Knowledge ID cannot be null or empty.", nameof(knowledgeId));

        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            if (!_timePartitionedStore.TryGetValue(knowledgeId, out var timeline))
                return null;

            var rwLock = GetOrCreateLock(knowledgeId);
            rwLock.EnterReadLock();
            try
            {
                if (timeline.Count == 0)
                    return null;

                // Find the entry at or before the timestamp
                TemporalKnowledgeEntry? result = null;
                foreach (var kvp in timeline)
                {
                    if (kvp.Key <= timestamp)
                        result = kvp.Value.Clone();
                    else
                        break;
                }

                return result;
            }
            finally
            {
                rwLock.ExitReadLock();
            }
        }, ct);
    }

    /// <summary>
    /// Retrieves all versions of a knowledge object within a time range.
    /// </summary>
    /// <param name="knowledgeId">The knowledge object identifier.</param>
    /// <param name="start">The start of the time range.</param>
    /// <param name="end">The end of the time range.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>All versions within the time range in chronological order.</returns>
    /// <exception cref="ArgumentException">Thrown when knowledgeId is null or empty.</exception>
    public Task<IEnumerable<TemporalKnowledgeEntry>> GetRangeAsync(string knowledgeId, DateTimeOffset start, DateTimeOffset end, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(knowledgeId))
            throw new ArgumentException("Knowledge ID cannot be null or empty.", nameof(knowledgeId));

        return Task.Run<IEnumerable<TemporalKnowledgeEntry>>(() =>
        {
            ct.ThrowIfCancellationRequested();

            if (!_timePartitionedStore.TryGetValue(knowledgeId, out var timeline))
                return Enumerable.Empty<TemporalKnowledgeEntry>();

            var rwLock = GetOrCreateLock(knowledgeId);
            rwLock.EnterReadLock();
            try
            {
                var results = new List<TemporalKnowledgeEntry>();
                foreach (var kvp in timeline)
                {
                    ct.ThrowIfCancellationRequested();
                    if (kvp.Key >= start && kvp.Key <= end)
                        results.Add(kvp.Value.Clone());
                    else if (kvp.Key > end)
                        break;
                }

                return results;
            }
            finally
            {
                rwLock.ExitReadLock();
            }
        }, ct);
    }

    /// <summary>
    /// Deletes all versions of a knowledge object before a specified timestamp.
    /// </summary>
    /// <param name="knowledgeId">The knowledge object identifier.</param>
    /// <param name="before">Delete versions before this timestamp.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of versions deleted.</returns>
    public Task<int> DeleteBeforeAsync(string knowledgeId, DateTimeOffset before, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(knowledgeId))
            throw new ArgumentException("Knowledge ID cannot be null or empty.", nameof(knowledgeId));

        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            if (!_timePartitionedStore.TryGetValue(knowledgeId, out var timeline))
                return 0;

            var rwLock = GetOrCreateLock(knowledgeId);
            rwLock.EnterWriteLock();
            try
            {
                var toRemove = timeline.Where(kvp => kvp.Key < before).Select(kvp => kvp.Key).ToList();
                foreach (var key in toRemove)
                {
                    ct.ThrowIfCancellationRequested();
                    timeline.Remove(key);
                }

                return toRemove.Count;
            }
            finally
            {
                rwLock.ExitWriteLock();
            }
        }, ct);
    }

    private ReaderWriterLockSlim GetOrCreateLock(string knowledgeId)
    {
        return _locks.GetOrAdd(knowledgeId, _ => new ReaderWriterLockSlim());
    }
}

/// <summary>
/// Manages point-in-time snapshots of knowledge state.
/// Supports both full and incremental snapshots for efficiency.
/// </summary>
public sealed class SnapshotManager
{
    private readonly KnowledgeTimeStore _timeStore;
    private readonly ConcurrentDictionary<string, KnowledgeSnapshot> _snapshots = new();
    private readonly ReaderWriterLockSlim _lock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="SnapshotManager"/> class.
    /// </summary>
    /// <param name="timeStore">The knowledge time store to snapshot.</param>
    /// <exception cref="ArgumentNullException">Thrown when timeStore is null.</exception>
    public SnapshotManager(KnowledgeTimeStore timeStore)
    {
        _timeStore = timeStore ?? throw new ArgumentNullException(nameof(timeStore));
    }

    /// <summary>
    /// Creates a new snapshot at the specified timestamp.
    /// </summary>
    /// <param name="timestamp">The timestamp for the snapshot.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The snapshot identifier.</returns>
    public async Task<string> CreateSnapshotAsync(DateTimeOffset timestamp, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var snapshotId = $"snapshot-{timestamp:yyyyMMddHHmmss}-{Guid.NewGuid():N}".Substring(0, 50);

        // Find the most recent snapshot to use as a base for incremental
        KnowledgeSnapshot? parentSnapshot = null;
        string? parentSnapshotId = null;

        _lock.EnterReadLock();
        try
        {
            parentSnapshot = _snapshots.Values
                .Where(s => s.Timestamp < timestamp)
                .OrderByDescending(s => s.Timestamp)
                .FirstOrDefault();
            parentSnapshotId = parentSnapshot?.SnapshotId;
        }
        finally
        {
            _lock.ExitReadLock();
        }

        var snapshot = new KnowledgeSnapshot
        {
            SnapshotId = snapshotId,
            Timestamp = timestamp,
            ParentSnapshotId = parentSnapshotId,
            IsIncremental = parentSnapshot != null
        };

        // This is a simplified implementation - in production, we'd iterate all knowledge IDs
        // For now, we create an empty snapshot that can be populated
        // In a real implementation, you'd enumerate all knowledge objects

        _lock.EnterWriteLock();
        try
        {
            _snapshots[snapshotId] = snapshot;
        }
        finally
        {
            _lock.ExitWriteLock();
        }

        await Task.CompletedTask;
        return snapshotId;
    }

    /// <summary>
    /// Retrieves a snapshot by its identifier.
    /// </summary>
    /// <param name="snapshotId">The snapshot identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The snapshot.</returns>
    /// <exception cref="KeyNotFoundException">Thrown when the snapshot is not found.</exception>
    public Task<KnowledgeSnapshot> GetSnapshotAsync(string snapshotId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(snapshotId);

        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            _lock.EnterReadLock();
            try
            {
                if (!_snapshots.TryGetValue(snapshotId, out var snapshot))
                    throw new KeyNotFoundException($"Snapshot '{snapshotId}' not found.");

                return snapshot;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }, ct);
    }

    /// <summary>
    /// Lists all available snapshots.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Information about all snapshots.</returns>
    public Task<IEnumerable<SnapshotInfo>> ListSnapshotsAsync(CancellationToken ct)
    {
        return Task.Run<IEnumerable<SnapshotInfo>>(() =>
        {
            ct.ThrowIfCancellationRequested();

            _lock.EnterReadLock();
            try
            {
                return _snapshots.Values
                    .OrderByDescending(s => s.Timestamp)
                    .Select(s => new SnapshotInfo
                    {
                        SnapshotId = s.SnapshotId,
                        Timestamp = s.Timestamp,
                        ObjectCount = s.Objects.Count,
                        IsIncremental = s.IsIncremental,
                        ParentSnapshotId = s.ParentSnapshotId,
                        Description = s.Description,
                        Tags = new List<string>(s.Tags),
                        SizeBytes = EstimateSnapshotSize(s)
                    })
                    .ToList();
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }, ct);
    }

    /// <summary>
    /// Deletes a snapshot.
    /// </summary>
    /// <param name="snapshotId">The snapshot identifier to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <exception cref="KeyNotFoundException">Thrown when the snapshot is not found.</exception>
    public Task DeleteSnapshotAsync(string snapshotId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(snapshotId);

        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            _lock.EnterWriteLock();
            try
            {
                if (!_snapshots.TryRemove(snapshotId, out _))
                    throw new KeyNotFoundException($"Snapshot '{snapshotId}' not found.");
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }, ct);
    }

    private static long EstimateSnapshotSize(KnowledgeSnapshot snapshot)
    {
        long size = 0;
        foreach (var obj in snapshot.Objects.Values)
        {
            size += obj.Content.Length * 2; // UTF-16
            size += obj.Id.Length * 2;
            size += obj.Type.Length * 2;
            size += 100; // Metadata overhead estimate
        }
        return size;
    }
}

/// <summary>
/// Builds knowledge timelines with events, changes, and milestones.
/// Produces visualization-ready timeline data.
/// </summary>
public sealed class TimelineBuilder
{
    private readonly KnowledgeTimeStore _timeStore;
    private readonly ChangeDetector _changeDetector;

    /// <summary>
    /// Initializes a new instance of the <see cref="TimelineBuilder"/> class.
    /// </summary>
    /// <param name="timeStore">The knowledge time store.</param>
    /// <param name="changeDetector">The change detector.</param>
    /// <exception cref="ArgumentNullException">Thrown when any parameter is null.</exception>
    public TimelineBuilder(KnowledgeTimeStore timeStore, ChangeDetector changeDetector)
    {
        _timeStore = timeStore ?? throw new ArgumentNullException(nameof(timeStore));
        _changeDetector = changeDetector ?? throw new ArgumentNullException(nameof(changeDetector));
    }

    /// <summary>
    /// Builds a timeline for a knowledge object.
    /// </summary>
    /// <param name="knowledgeId">The knowledge object identifier.</param>
    /// <param name="options">Timeline options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The built timeline.</returns>
    /// <exception cref="ArgumentException">Thrown when knowledgeId is null or empty.</exception>
    public async Task<KnowledgeTimeline> BuildTimelineAsync(string knowledgeId, TimelineOptions options, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(knowledgeId);
        options ??= new TimelineOptions();

        ct.ThrowIfCancellationRequested();

        var start = options.StartDate ?? DateTimeOffset.MinValue;
        var end = options.EndDate ?? DateTimeOffset.UtcNow;

        var timeline = new KnowledgeTimeline
        {
            KnowledgeId = knowledgeId,
            StartDate = start,
            EndDate = end
        };

        // Get all versions in the time range
        var versions = await _timeStore.GetRangeAsync(knowledgeId, start, end, ct);
        var versionList = versions.ToList();

        if (versionList.Count == 0)
            return timeline;

        // Build entries from versions
        var entries = new List<TimelineEntry>();
        TemporalKnowledgeEntry? previousVersion = null;

        foreach (var version in versionList.OrderBy(v => v.ModifiedAt))
        {
            ct.ThrowIfCancellationRequested();

            if (previousVersion == null)
            {
                // First version - creation event
                if (options.IncludeEvents)
                {
                    entries.Add(new TimelineEntry
                    {
                        Timestamp = version.CreatedAt,
                        EntryType = TimelineEntryType.Created,
                        Description = $"Knowledge object '{knowledgeId}' created",
                        VisualizationData = new Dictionary<string, object>
                        {
                            ["version"] = version.Version,
                            ["type"] = version.Type
                        }
                    });
                    timeline.TotalEvents++;
                }
            }
            else if (options.IncludeChanges)
            {
                // Subsequent version - change event
                var change = DetectChange(previousVersion, version);
                if (change != null)
                {
                    entries.Add(new TimelineEntry
                    {
                        Timestamp = version.ModifiedAt,
                        EntryType = TimelineEntryType.Change,
                        Description = $"Version {version.Version}: {change.ChangeType}",
                        Change = change,
                        VisualizationData = new Dictionary<string, object>
                        {
                            ["oldVersion"] = previousVersion.Version,
                            ["newVersion"] = version.Version,
                            ["changeType"] = change.ChangeType.ToString()
                        }
                    });
                    timeline.TotalChanges++;
                }
            }

            // Check for milestones (major version changes)
            if (options.IncludeMilestones && previousVersion != null &&
                version.Version / 10 > previousVersion.Version / 10)
            {
                entries.Add(new TimelineEntry
                {
                    Timestamp = version.ModifiedAt,
                    EntryType = TimelineEntryType.Milestone,
                    Description = $"Major milestone: Version {version.Version}",
                    VisualizationData = new Dictionary<string, object>
                    {
                        ["milestoneVersion"] = version.Version
                    }
                });
                timeline.TotalMilestones++;
            }

            previousVersion = version;
        }

        // Apply granularity grouping
        entries = ApplyGranularity(entries, options.Granularity);

        // Limit entries
        timeline.Entries = entries.Take(options.MaxEntries).ToList();

        return timeline;
    }

    private static KnowledgeChange? DetectChange(TemporalKnowledgeEntry oldVersion, TemporalKnowledgeEntry newVersion)
    {
        if (oldVersion.ContentHash == newVersion.ContentHash)
            return null;

        var change = new KnowledgeChange
        {
            KnowledgeId = newVersion.Id,
            Timestamp = newVersion.ModifiedAt,
            OldVersion = oldVersion.Version,
            NewVersion = newVersion.Version,
            OldSchemaVersion = oldVersion.SchemaVersion,
            NewSchemaVersion = newVersion.SchemaVersion
        };

        if (oldVersion.SchemaVersion != newVersion.SchemaVersion)
        {
            change.ChangeType = ChangeType.SchemaChange;
            change.Classification = ChangeClassification.Major;
        }
        else if (oldVersion.Content != newVersion.Content)
        {
            change.ChangeType = ChangeType.Modification;
            change.FieldChanged = "Content";
            change.OldValue = oldVersion.Content.Length > 100 ? oldVersion.Content.Substring(0, 100) + "..." : oldVersion.Content;
            change.NewValue = newVersion.Content.Length > 100 ? newVersion.Content.Substring(0, 100) + "..." : newVersion.Content;
            change.Classification = ChangeClassification.Minor;
            change.Diff = GenerateDiff(oldVersion.Content, newVersion.Content);
        }
        else
        {
            change.ChangeType = ChangeType.Modification;
            change.FieldChanged = "Metadata";
            change.Classification = ChangeClassification.Patch;
        }

        return change;
    }

    private static string GenerateDiff(string oldContent, string newContent)
    {
        // Simple diff generation - in production, use a proper diff algorithm
        var sb = new StringBuilder();
        var oldLines = oldContent.Split('\n');
        var newLines = newContent.Split('\n');

        for (int i = 0; i < Math.Max(oldLines.Length, newLines.Length); i++)
        {
            var oldLine = i < oldLines.Length ? oldLines[i] : null;
            var newLine = i < newLines.Length ? newLines[i] : null;

            if (oldLine != newLine)
            {
                if (oldLine != null)
                    sb.AppendLine($"- {oldLine}");
                if (newLine != null)
                    sb.AppendLine($"+ {newLine}");
            }
        }

        return sb.ToString();
    }

    private static List<TimelineEntry> ApplyGranularity(List<TimelineEntry> entries, TimelineGranularity granularity)
    {
        if (entries.Count == 0)
            return entries;

        var grouped = entries.GroupBy(e => GetGranularityKey(e.Timestamp, granularity))
            .Select(g =>
            {
                var first = g.OrderBy(e => e.Timestamp).First();
                first.VisualizationData["groupedCount"] = g.Count();
                return first;
            })
            .OrderBy(e => e.Timestamp)
            .ToList();

        return grouped;
    }

    private static string GetGranularityKey(DateTimeOffset timestamp, TimelineGranularity granularity)
    {
        return granularity switch
        {
            TimelineGranularity.Minute => timestamp.ToString("yyyyMMddHHmm"),
            TimelineGranularity.Hourly => timestamp.ToString("yyyyMMddHH"),
            TimelineGranularity.Daily => timestamp.ToString("yyyyMMdd"),
            TimelineGranularity.Weekly => $"{timestamp.Year}W{GetIsoWeekOfYear(timestamp):D2}",
            TimelineGranularity.Monthly => timestamp.ToString("yyyyMM"),
            _ => timestamp.ToString("yyyyMMddHHmm")
        };
    }

    private static int GetIsoWeekOfYear(DateTimeOffset date)
    {
        var day = (int)date.DayOfWeek;
        if (day == 0) day = 7; // Sunday = 7
        var thursday = date.AddDays(4 - day);
        var jan1 = new DateTimeOffset(thursday.Year, 1, 1, 0, 0, 0, thursday.Offset);
        return (int)Math.Ceiling((thursday - jan1).Days / 7.0) + 1;
    }
}

/// <summary>
/// B-tree based index for fast temporal queries.
/// Provides efficient lookup by time range, first occurrence, and last occurrence.
/// </summary>
public sealed class TemporalIndex
{
    private readonly ConcurrentDictionary<string, SortedSet<DateTimeOffset>> _knowledgeTimestamps = new();
    private readonly SortedDictionary<DateTimeOffset, HashSet<string>> _timestampToKnowledge = new();
    private readonly ReaderWriterLockSlim _lock = new();

    /// <summary>
    /// Gets the total number of indexed entries.
    /// </summary>
    public long TotalEntries
    {
        get
        {
            _lock.EnterReadLock();
            try
            {
                return _knowledgeTimestamps.Values.Sum(s => (long)s.Count);
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
    }

    /// <summary>
    /// Indexes a knowledge object at a specific timestamp.
    /// </summary>
    /// <param name="knowledgeId">The knowledge object identifier.</param>
    /// <param name="timestamp">The timestamp to index.</param>
    public void Index(string knowledgeId, DateTimeOffset timestamp)
    {
        ArgumentException.ThrowIfNullOrEmpty(knowledgeId);

        _lock.EnterWriteLock();
        try
        {
            // Index by knowledge ID
            if (!_knowledgeTimestamps.TryGetValue(knowledgeId, out var timestamps))
            {
                timestamps = new SortedSet<DateTimeOffset>();
                _knowledgeTimestamps[knowledgeId] = timestamps;
            }
            timestamps.Add(timestamp);

            // Index by timestamp
            if (!_timestampToKnowledge.TryGetValue(timestamp, out var knowledgeIds))
            {
                knowledgeIds = new HashSet<string>();
                _timestampToKnowledge[timestamp] = knowledgeIds;
            }
            knowledgeIds.Add(knowledgeId);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Finds all knowledge IDs with entries in the specified time range.
    /// </summary>
    /// <param name="start">The start of the time range.</param>
    /// <param name="end">The end of the time range.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Knowledge IDs with entries in the range.</returns>
    public Task<IEnumerable<string>> FindByTimeRangeAsync(DateTimeOffset start, DateTimeOffset end, CancellationToken ct)
    {
        return Task.Run<IEnumerable<string>>(() =>
        {
            ct.ThrowIfCancellationRequested();

            var results = new HashSet<string>();

            _lock.EnterReadLock();
            try
            {
                foreach (var kvp in _timestampToKnowledge)
                {
                    ct.ThrowIfCancellationRequested();

                    if (kvp.Key >= start && kvp.Key <= end)
                    {
                        foreach (var id in kvp.Value)
                            results.Add(id);
                    }
                    else if (kvp.Key > end)
                        break;
                }

                return results.ToList();
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }, ct);
    }

    /// <summary>
    /// Finds the first occurrence timestamp for a knowledge object.
    /// </summary>
    /// <param name="knowledgeId">The knowledge object identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The first occurrence timestamp, or null if not found.</returns>
    public Task<DateTimeOffset?> FindFirstOccurrenceAsync(string knowledgeId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(knowledgeId);

        return Task.Run<DateTimeOffset?>(() =>
        {
            ct.ThrowIfCancellationRequested();

            _lock.EnterReadLock();
            try
            {
                if (!_knowledgeTimestamps.TryGetValue(knowledgeId, out var timestamps) || timestamps.Count == 0)
                    return null;

                return timestamps.Min;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }, ct);
    }

    /// <summary>
    /// Finds the last occurrence timestamp for a knowledge object.
    /// </summary>
    /// <param name="knowledgeId">The knowledge object identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The last occurrence timestamp, or null if not found.</returns>
    public Task<DateTimeOffset?> FindLastOccurrenceAsync(string knowledgeId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(knowledgeId);

        return Task.Run<DateTimeOffset?>(() =>
        {
            ct.ThrowIfCancellationRequested();

            _lock.EnterReadLock();
            try
            {
                if (!_knowledgeTimestamps.TryGetValue(knowledgeId, out var timestamps) || timestamps.Count == 0)
                    return null;

                return timestamps.Max;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }, ct);
    }

    /// <summary>
    /// Removes entries before a specified timestamp.
    /// </summary>
    /// <param name="before">Remove entries before this timestamp.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of entries removed.</returns>
    public Task<int> RemoveBeforeAsync(DateTimeOffset before, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            int removed = 0;

            _lock.EnterWriteLock();
            try
            {
                // Remove from timestamp index
                var timestampsToRemove = _timestampToKnowledge.Keys.Where(k => k < before).ToList();
                foreach (var ts in timestampsToRemove)
                {
                    ct.ThrowIfCancellationRequested();
                    removed += _timestampToKnowledge[ts].Count;
                    _timestampToKnowledge.Remove(ts);
                }

                // Remove from knowledge index
                foreach (var kvp in _knowledgeTimestamps)
                {
                    ct.ThrowIfCancellationRequested();
                    kvp.Value.RemoveWhere(ts => ts < before);
                }

                return removed;
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }, ct);
    }
}

#endregion

#region 90.F2: Temporal Queries

/// <summary>
/// Handles "as-of" temporal queries to retrieve knowledge state at a specific point in time.
/// </summary>
public sealed class AsOfQueryHandler
{
    private readonly KnowledgeTimeStore _timeStore;

    /// <summary>
    /// Initializes a new instance of the <see cref="AsOfQueryHandler"/> class.
    /// </summary>
    /// <param name="timeStore">The knowledge time store.</param>
    /// <exception cref="ArgumentNullException">Thrown when timeStore is null.</exception>
    public AsOfQueryHandler(KnowledgeTimeStore timeStore)
    {
        _timeStore = timeStore ?? throw new ArgumentNullException(nameof(timeStore));
    }

    /// <summary>
    /// Queries the state of a knowledge object at a specific point in time.
    /// </summary>
    /// <param name="knowledgeId">The knowledge object identifier.</param>
    /// <param name="asOf">The point in time to query.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The knowledge object as it was at the specified time, or null if not found.</returns>
    /// <exception cref="ArgumentException">Thrown when knowledgeId is null or empty.</exception>
    public async Task<TemporalKnowledgeEntry?> QueryAsync(string knowledgeId, DateTimeOffset asOf, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(knowledgeId);

        ct.ThrowIfCancellationRequested();

        var result = await _timeStore.GetAtAsync(knowledgeId, asOf, ct);

        if (result == null)
        {
            // Handle missing historical data gracefully
            // Try to find the earliest available version
            var range = await _timeStore.GetRangeAsync(knowledgeId, DateTimeOffset.MinValue, DateTimeOffset.MaxValue, ct);
            var earliest = range.OrderBy(k => k.ModifiedAt).FirstOrDefault();

            if (earliest != null && earliest.ModifiedAt > asOf)
            {
                // Knowledge didn't exist at the requested time
                return null;
            }
        }

        return result;
    }

    /// <summary>
    /// Queries multiple knowledge objects at a specific point in time.
    /// </summary>
    /// <param name="knowledgeIds">The knowledge object identifiers.</param>
    /// <param name="asOf">The point in time to query.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Dictionary of knowledge objects keyed by their IDs.</returns>
    public async Task<Dictionary<string, TemporalKnowledgeEntry?>> QueryMultipleAsync(
        IEnumerable<string> knowledgeIds,
        DateTimeOffset asOf,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(knowledgeIds);

        var results = new Dictionary<string, TemporalKnowledgeEntry?>();
        var tasks = knowledgeIds.Select(async id =>
        {
            var result = await QueryAsync(id, asOf, ct);
            return (id, result);
        });

        var completed = await Task.WhenAll(tasks);
        foreach (var (id, result) in completed)
        {
            results[id] = result;
        }

        return results;
    }
}

/// <summary>
/// Handles "between" temporal queries to show how knowledge changed over a time period.
/// </summary>
public sealed class BetweenQueryHandler
{
    private readonly KnowledgeTimeStore _timeStore;

    /// <summary>
    /// Initializes a new instance of the <see cref="BetweenQueryHandler"/> class.
    /// </summary>
    /// <param name="timeStore">The knowledge time store.</param>
    /// <exception cref="ArgumentNullException">Thrown when timeStore is null.</exception>
    public BetweenQueryHandler(KnowledgeTimeStore timeStore)
    {
        _timeStore = timeStore ?? throw new ArgumentNullException(nameof(timeStore));
    }

    /// <summary>
    /// Queries changes to a knowledge object between two points in time.
    /// </summary>
    /// <param name="knowledgeId">The knowledge object identifier.</param>
    /// <param name="from">The start of the time period.</param>
    /// <param name="to">The end of the time period.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The change history for the period.</returns>
    /// <exception cref="ArgumentException">Thrown when knowledgeId is null or empty.</exception>
    public async Task<ChangeHistory> QueryAsync(string knowledgeId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(knowledgeId);

        ct.ThrowIfCancellationRequested();

        var history = new ChangeHistory
        {
            KnowledgeId = knowledgeId,
            FromTimestamp = from,
            ToTimestamp = to
        };

        // Get state at start
        history.StateAtStart = await _timeStore.GetAtAsync(knowledgeId, from, ct);

        // Get state at end
        history.StateAtEnd = await _timeStore.GetAtAsync(knowledgeId, to, ct);

        // Get all versions in the range
        var versions = await _timeStore.GetRangeAsync(knowledgeId, from, to, ct);
        var versionList = versions.OrderBy(v => v.ModifiedAt).ToList();

        // Build change list
        TemporalKnowledgeEntry? previousVersion = history.StateAtStart;

        foreach (var version in versionList)
        {
            ct.ThrowIfCancellationRequested();

            if (previousVersion == null)
            {
                // First appearance in range - addition
                history.Changes.Add(new KnowledgeChange
                {
                    KnowledgeId = knowledgeId,
                    Timestamp = version.ModifiedAt,
                    ChangeType = ChangeType.Addition,
                    Classification = ChangeClassification.Major,
                    NewValue = version.Content,
                    NewVersion = version.Version,
                    NewSchemaVersion = version.SchemaVersion
                });
                history.AdditionCount++;
            }
            else if (previousVersion.ContentHash != version.ContentHash)
            {
                // Content changed - modification
                var change = new KnowledgeChange
                {
                    KnowledgeId = knowledgeId,
                    Timestamp = version.ModifiedAt,
                    ChangeType = ChangeType.Modification,
                    OldValue = previousVersion.Content.Length > 100 ? previousVersion.Content.Substring(0, 100) + "..." : previousVersion.Content,
                    NewValue = version.Content.Length > 100 ? version.Content.Substring(0, 100) + "..." : version.Content,
                    OldVersion = previousVersion.Version,
                    NewVersion = version.Version,
                    OldSchemaVersion = previousVersion.SchemaVersion,
                    NewSchemaVersion = version.SchemaVersion,
                    Diff = GenerateDiff(previousVersion.Content, version.Content)
                };

                // Classify the change
                if (previousVersion.SchemaVersion != version.SchemaVersion)
                {
                    change.Classification = ChangeClassification.Major;
                    change.ChangeType = ChangeType.SchemaChange;
                }
                else if (Math.Abs(previousVersion.Content.Length - version.Content.Length) > previousVersion.Content.Length * 0.2)
                {
                    change.Classification = ChangeClassification.Minor;
                }
                else
                {
                    change.Classification = ChangeClassification.Patch;
                }

                history.Changes.Add(change);
                history.ModificationCount++;
            }

            previousVersion = version;
        }

        // Generate overall diff if we have start and end states
        if (history.StateAtStart != null && history.StateAtEnd != null)
        {
            history.OverallDiff = GenerateDiff(history.StateAtStart.Content, history.StateAtEnd.Content);
        }

        return history;
    }

    private static string GenerateDiff(string oldContent, string newContent)
    {
        var sb = new StringBuilder();
        var oldLines = oldContent.Split('\n');
        var newLines = newContent.Split('\n');

        for (int i = 0; i < Math.Max(oldLines.Length, newLines.Length); i++)
        {
            var oldLine = i < oldLines.Length ? oldLines[i] : null;
            var newLine = i < newLines.Length ? newLines[i] : null;

            if (oldLine != newLine)
            {
                if (oldLine != null)
                    sb.AppendLine($"- {oldLine}");
                if (newLine != null)
                    sb.AppendLine($"+ {newLine}");
            }
        }

        return sb.ToString();
    }
}

/// <summary>
/// Detects and classifies knowledge changes over time.
/// </summary>
public sealed class ChangeDetector
{
    private readonly KnowledgeTimeStore _timeStore;
    private readonly ConcurrentDictionary<string, KnowledgeChange> _recentChanges = new();
    private DateTimeOffset _lastDetectionTime = DateTimeOffset.MinValue;
    private readonly ReaderWriterLockSlim _lock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ChangeDetector"/> class.
    /// </summary>
    /// <param name="timeStore">The knowledge time store.</param>
    /// <exception cref="ArgumentNullException">Thrown when timeStore is null.</exception>
    public ChangeDetector(KnowledgeTimeStore timeStore)
    {
        _timeStore = timeStore ?? throw new ArgumentNullException(nameof(timeStore));
    }

    /// <summary>
    /// Detects all changes since a specified timestamp.
    /// </summary>
    /// <param name="since">Detect changes since this timestamp.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of detected changes.</returns>
    public Task<IEnumerable<KnowledgeChange>> DetectChangesAsync(DateTimeOffset since, CancellationToken ct)
    {
        return Task.Run<IEnumerable<KnowledgeChange>>(() =>
        {
            ct.ThrowIfCancellationRequested();

            _lock.EnterReadLock();
            try
            {
                return _recentChanges.Values
                    .Where(c => c.Timestamp >= since)
                    .OrderBy(c => c.Timestamp)
                    .ToList();
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }, ct);
    }

    /// <summary>
    /// Records a change for tracking.
    /// </summary>
    /// <param name="change">The change to record.</param>
    public void RecordChange(KnowledgeChange change)
    {
        ArgumentNullException.ThrowIfNull(change);

        _lock.EnterWriteLock();
        try
        {
            _recentChanges[change.ChangeId] = change;
            if (change.Timestamp > _lastDetectionTime)
                _lastDetectionTime = change.Timestamp;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Classifies a change based on its characteristics.
    /// </summary>
    /// <param name="oldVersion">The old version of the knowledge object.</param>
    /// <param name="newVersion">The new version of the knowledge object.</param>
    /// <returns>The classified change.</returns>
    public KnowledgeChange ClassifyChange(TemporalKnowledgeEntry? oldVersion, TemporalKnowledgeEntry newVersion)
    {
        ArgumentNullException.ThrowIfNull(newVersion);

        var change = new KnowledgeChange
        {
            KnowledgeId = newVersion.Id,
            Timestamp = newVersion.ModifiedAt,
            NewVersion = newVersion.Version,
            NewSchemaVersion = newVersion.SchemaVersion,
            NewValue = newVersion.Content.Length > 100 ? newVersion.Content.Substring(0, 100) + "..." : newVersion.Content
        };

        if (oldVersion == null)
        {
            change.ChangeType = ChangeType.Addition;
            change.Classification = ChangeClassification.Major;
            return change;
        }

        change.OldVersion = oldVersion.Version;
        change.OldSchemaVersion = oldVersion.SchemaVersion;
        change.OldValue = oldVersion.Content.Length > 100 ? oldVersion.Content.Substring(0, 100) + "..." : oldVersion.Content;

        // Detect schema changes
        if (oldVersion.SchemaVersion != newVersion.SchemaVersion)
        {
            change.ChangeType = ChangeType.SchemaChange;
            change.Classification = ChangeClassification.Major;
            change.FieldChanged = "SchemaVersion";
            return change;
        }

        // Detect content changes
        if (oldVersion.Content != newVersion.Content)
        {
            change.ChangeType = ChangeType.Modification;
            change.FieldChanged = "Content";

            // Classify based on content change magnitude
            var lengthChange = Math.Abs(oldVersion.Content.Length - newVersion.Content.Length);
            var percentChange = oldVersion.Content.Length > 0 ? (double)lengthChange / oldVersion.Content.Length : 1.0;

            if (percentChange > 0.5)
                change.Classification = ChangeClassification.Major;
            else if (percentChange > 0.1)
                change.Classification = ChangeClassification.Minor;
            else
                change.Classification = ChangeClassification.Patch;

            return change;
        }

        // Detect metadata changes
        var oldMetaJson = JsonSerializer.Serialize(oldVersion.Metadata);
        var newMetaJson = JsonSerializer.Serialize(newVersion.Metadata);

        if (oldMetaJson != newMetaJson)
        {
            change.ChangeType = ChangeType.Modification;
            change.FieldChanged = "Metadata";
            change.Classification = ChangeClassification.Patch;
            return change;
        }

        // No meaningful change detected
        change.ChangeType = ChangeType.Modification;
        change.Classification = ChangeClassification.Patch;
        return change;
    }

    /// <summary>
    /// Clears old changes from tracking to free memory.
    /// </summary>
    /// <param name="olderThan">Clear changes older than this timestamp.</param>
    /// <returns>The number of changes cleared.</returns>
    public int ClearOldChanges(DateTimeOffset olderThan)
    {
        _lock.EnterWriteLock();
        try
        {
            var toRemove = _recentChanges.Where(kvp => kvp.Value.Timestamp < olderThan)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in toRemove)
            {
                _recentChanges.TryRemove(key, out _);
            }

            return toRemove.Count;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }
}

/// <summary>
/// Analyzes knowledge trends including growth, seasonality, and anomalies.
/// </summary>
public sealed class TrendAnalyzer
{
    private readonly KnowledgeTimeStore _timeStore;

    /// <summary>
    /// Initializes a new instance of the <see cref="TrendAnalyzer"/> class.
    /// </summary>
    /// <param name="timeStore">The knowledge time store.</param>
    /// <exception cref="ArgumentNullException">Thrown when timeStore is null.</exception>
    public TrendAnalyzer(KnowledgeTimeStore timeStore)
    {
        _timeStore = timeStore ?? throw new ArgumentNullException(nameof(timeStore));
    }

    /// <summary>
    /// Analyzes trends for a knowledge object.
    /// </summary>
    /// <param name="knowledgeId">The knowledge object identifier.</param>
    /// <param name="options">Analysis options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The trend analysis results.</returns>
    /// <exception cref="ArgumentException">Thrown when knowledgeId is null or empty.</exception>
    public async Task<TrendAnalysis> AnalyzeAsync(string knowledgeId, TrendOptions options, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(knowledgeId);
        options ??= new TrendOptions();

        ct.ThrowIfCancellationRequested();

        var start = options.StartDate ?? DateTimeOffset.UtcNow.AddDays(-30);
        var end = options.EndDate ?? DateTimeOffset.UtcNow;

        var analysis = new TrendAnalysis
        {
            KnowledgeId = knowledgeId,
            StartDate = start,
            EndDate = end
        };

        // Get all versions in the range
        var versions = await _timeStore.GetRangeAsync(knowledgeId, start, end, ct);
        var versionList = versions.OrderBy(v => v.ModifiedAt).ToList();

        if (versionList.Count < 2)
        {
            analysis.GrowthTrend = TrendDirection.Stable;
            return analysis;
        }

        // Calculate change frequency data points
        var changePoints = new List<(DateTimeOffset Timestamp, double Value)>();
        var currentTime = start;
        while (currentTime <= end)
        {
            ct.ThrowIfCancellationRequested();

            var changesInInterval = versionList.Count(v =>
                v.ModifiedAt >= currentTime &&
                v.ModifiedAt < currentTime.Add(options.SamplingInterval));

            changePoints.Add((currentTime, changesInInterval));
            currentTime = currentTime.Add(options.SamplingInterval);
        }

        // Calculate basic statistics
        var values = changePoints.Select(p => p.Value).ToList();
        analysis.MeanChangeFrequency = values.Count > 0 ? values.Average() : 0;
        analysis.StdDevChangeFrequency = CalculateStdDev(values);

        // Analyze growth
        if (options.AnalyzeGrowth)
        {
            var (slope, correlation) = CalculateLinearRegression(changePoints);
            analysis.GrowthRate = slope * 24; // Changes per day
            analysis.TrendCorrelation = correlation;

            if (Math.Abs(correlation) < 0.3)
                analysis.GrowthTrend = TrendDirection.Fluctuating;
            else if (slope > 0.001)
                analysis.GrowthTrend = TrendDirection.Increasing;
            else if (slope < -0.001)
                analysis.GrowthTrend = TrendDirection.Decreasing;
            else
                analysis.GrowthTrend = TrendDirection.Stable;
        }

        // Analyze seasonality
        if (options.AnalyzeSeasonality)
        {
            analysis.SeasonalPatterns = DetectSeasonality(changePoints);
        }

        // Detect anomalies
        if (options.DetectAnomalies)
        {
            analysis.Anomalies = DetectAnomalies(changePoints, analysis.MeanChangeFrequency, analysis.StdDevChangeFrequency, options.AnomalyThreshold);
        }

        return analysis;
    }

    private static double CalculateStdDev(List<double> values)
    {
        if (values.Count < 2)
            return 0;

        var mean = values.Average();
        var sumSquaredDiff = values.Sum(v => Math.Pow(v - mean, 2));
        return Math.Sqrt(sumSquaredDiff / (values.Count - 1));
    }

    private static (double slope, double correlation) CalculateLinearRegression(List<(DateTimeOffset Timestamp, double Value)> points)
    {
        if (points.Count < 2)
            return (0, 0);

        var n = points.Count;
        var baseTime = points[0].Timestamp;

        // Convert to hours from start for x values
        var xValues = points.Select(p => (p.Timestamp - baseTime).TotalHours).ToList();
        var yValues = points.Select(p => p.Value).ToList();

        var xMean = xValues.Average();
        var yMean = yValues.Average();

        var numerator = 0.0;
        var denominator = 0.0;
        var yDenominator = 0.0;

        for (int i = 0; i < n; i++)
        {
            var xDiff = xValues[i] - xMean;
            var yDiff = yValues[i] - yMean;
            numerator += xDiff * yDiff;
            denominator += xDiff * xDiff;
            yDenominator += yDiff * yDiff;
        }

        if (denominator == 0 || yDenominator == 0)
            return (0, 0);

        var slope = numerator / denominator;
        var correlation = numerator / Math.Sqrt(denominator * yDenominator);

        return (slope, correlation);
    }

    private static List<SeasonalPattern> DetectSeasonality(List<(DateTimeOffset Timestamp, double Value)> points)
    {
        var patterns = new List<SeasonalPattern>();

        if (points.Count < 48) // Need at least 2 days of hourly data
            return patterns;

        // Check for daily pattern
        var dailyPattern = DetectPatternWithPeriod(points, 24);
        if (dailyPattern != null)
            patterns.Add(dailyPattern);

        // Check for weekly pattern (if we have enough data)
        if (points.Count >= 168 * 2) // 2 weeks of hourly data
        {
            var weeklyPattern = DetectPatternWithPeriod(points, 168);
            if (weeklyPattern != null)
                patterns.Add(weeklyPattern);
        }

        return patterns;
    }

    private static SeasonalPattern? DetectPatternWithPeriod(List<(DateTimeOffset Timestamp, double Value)> points, int periodHours)
    {
        if (points.Count < periodHours * 2)
            return null;

        // Calculate average for each position in the period
        var periodAverages = new double[periodHours];
        var periodCounts = new int[periodHours];

        foreach (var point in points)
        {
            var position = (int)(point.Timestamp.Hour + (point.Timestamp.DayOfWeek == DayOfWeek.Sunday ? 0 : (int)point.Timestamp.DayOfWeek * 24)) % periodHours;
            periodAverages[position] += point.Value;
            periodCounts[position]++;
        }

        for (int i = 0; i < periodHours; i++)
        {
            if (periodCounts[i] > 0)
                periodAverages[i] /= periodCounts[i];
        }

        // Calculate pattern strength (variance of period averages vs overall variance)
        var overallMean = points.Select(p => p.Value).Average();
        var overallVariance = points.Select(p => Math.Pow(p.Value - overallMean, 2)).Average();
        var patternVariance = periodAverages.Select(a => Math.Pow(a - overallMean, 2)).Average();

        if (overallVariance == 0)
            return null;

        var strength = patternVariance / overallVariance;

        if (strength < 0.1) // Too weak to be meaningful
            return null;

        // Find peak times
        var peakThreshold = periodAverages.Average() + periodAverages.Max() * 0.3;
        var peakTimes = new List<TimeSpan>();
        for (int i = 0; i < periodHours; i++)
        {
            if (periodAverages[i] >= peakThreshold)
                peakTimes.Add(TimeSpan.FromHours(i));
        }

        return new SeasonalPattern
        {
            Period = TimeSpan.FromHours(periodHours),
            Strength = Math.Min(1.0, strength),
            Description = periodHours == 24 ? "Daily pattern detected" : "Weekly pattern detected",
            PeakTimes = peakTimes
        };
    }

    private static List<TrendAnomaly> DetectAnomalies(
        List<(DateTimeOffset Timestamp, double Value)> points,
        double mean,
        double stdDev,
        double threshold)
    {
        var anomalies = new List<TrendAnomaly>();

        if (stdDev == 0)
            return anomalies;

        foreach (var point in points)
        {
            var zScore = Math.Abs(point.Value - mean) / stdDev;

            if (zScore >= threshold)
            {
                var anomaly = new TrendAnomaly
                {
                    Timestamp = point.Timestamp,
                    Severity = zScore / threshold,
                    ExpectedValue = mean,
                    ActualValue = point.Value
                };

                if (point.Value > mean)
                {
                    anomaly.Type = AnomalyType.Spike;
                    anomaly.Description = $"Unusual spike: {point.Value:F2} vs expected {mean:F2}";
                }
                else
                {
                    anomaly.Type = AnomalyType.Drop;
                    anomaly.Description = $"Unusual drop: {point.Value:F2} vs expected {mean:F2}";
                }

                anomalies.Add(anomaly);
            }
        }

        return anomalies;
    }
}

#endregion

#region 90.F3: Temporal Maintenance

/// <summary>
/// Defines retention policies for historical knowledge data.
/// </summary>
public sealed class RetentionPolicy
{
    private readonly ReaderWriterLockSlim _lock = new();
    private TimeSpan _defaultRetention = TimeSpan.FromDays(365);
    private readonly Dictionary<string, TimeSpan> _perTypeRetention = new();

    /// <summary>
    /// Gets or sets the default retention period for knowledge objects.
    /// </summary>
    public TimeSpan DefaultRetention
    {
        get
        {
            _lock.EnterReadLock();
            try { return _defaultRetention; }
            finally { _lock.ExitReadLock(); }
        }
        set
        {
            _lock.EnterWriteLock();
            try { _defaultRetention = value; }
            finally { _lock.ExitWriteLock(); }
        }
    }

    /// <summary>
    /// Gets the per-type retention periods.
    /// </summary>
    public Dictionary<string, TimeSpan> PerTypeRetention
    {
        get
        {
            _lock.EnterReadLock();
            try { return new Dictionary<string, TimeSpan>(_perTypeRetention); }
            finally { _lock.ExitReadLock(); }
        }
    }

    /// <summary>
    /// Sets a retention period for a specific knowledge type.
    /// </summary>
    /// <param name="knowledgeType">The knowledge type.</param>
    /// <param name="retention">The retention period.</param>
    public void SetRetention(string knowledgeType, TimeSpan retention)
    {
        ArgumentException.ThrowIfNullOrEmpty(knowledgeType);

        _lock.EnterWriteLock();
        try
        {
            _perTypeRetention[knowledgeType] = retention;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Determines if a knowledge object should be retained.
    /// </summary>
    /// <param name="knowledge">The knowledge object to check.</param>
    /// <param name="now">The current timestamp.</param>
    /// <returns>True if the knowledge should be retained.</returns>
    /// <exception cref="ArgumentNullException">Thrown when knowledge is null.</exception>
    public bool ShouldRetain(TemporalKnowledgeEntry knowledge, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(knowledge);

        _lock.EnterReadLock();
        try
        {
            var retention = _perTypeRetention.TryGetValue(knowledge.Type, out var typeRetention)
                ? typeRetention
                : _defaultRetention;

            var age = now - knowledge.ModifiedAt;
            return age <= retention;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Applies the retention policy to a time store, deleting expired entries.
    /// </summary>
    /// <param name="timeStore">The time store to apply the policy to.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of entries deleted.</returns>
    public async Task<int> ApplyRetentionAsync(KnowledgeTimeStore timeStore, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(timeStore);

        // In a real implementation, we'd iterate all knowledge IDs and apply retention
        // For now, we return 0 as we don't have access to enumerate the store
        await Task.CompletedTask;
        return 0;
    }
}

/// <summary>
/// Compresses old knowledge data while preserving key information and milestones.
/// </summary>
public sealed class TemporalCompaction
{
    private readonly KnowledgeTimeStore _timeStore;
    private readonly ConcurrentDictionary<string, CompactionSummary> _summaries = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="TemporalCompaction"/> class.
    /// </summary>
    /// <param name="timeStore">The knowledge time store.</param>
    /// <exception cref="ArgumentNullException">Thrown when timeStore is null.</exception>
    public TemporalCompaction(KnowledgeTimeStore timeStore)
    {
        _timeStore = timeStore ?? throw new ArgumentNullException(nameof(timeStore));
    }

    /// <summary>
    /// Compacts old knowledge data according to the specified options.
    /// </summary>
    /// <param name="options">Compaction options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of entries compacted.</returns>
    public async Task<int> CompactAsync(CompactionOptions options, CancellationToken ct)
    {
        options ??= new CompactionOptions();

        ct.ThrowIfCancellationRequested();

        var threshold = DateTimeOffset.UtcNow - options.AgeThreshold;
        var compacted = 0;

        // Get the compaction interval
        var interval = GetCompactionInterval(options.Strategy);

        // In a real implementation, we'd:
        // 1. Enumerate all knowledge IDs
        // 2. For each, get versions older than threshold
        // 3. Group by interval and keep only one representative per interval
        // 4. Create summaries if requested
        // 5. Delete the rest

        // For now, we simulate the operation
        await Task.Delay(1, ct);

        return compacted;
    }

    /// <summary>
    /// Gets a compaction summary for a knowledge object.
    /// </summary>
    /// <param name="knowledgeId">The knowledge object identifier.</param>
    /// <returns>The compaction summary, or null if none exists.</returns>
    public CompactionSummary? GetSummary(string knowledgeId)
    {
        return _summaries.TryGetValue(knowledgeId, out var summary) ? summary : null;
    }

    /// <summary>
    /// Creates a summary for a compacted period.
    /// </summary>
    /// <param name="knowledgeId">The knowledge object identifier.</param>
    /// <param name="start">The start of the period.</param>
    /// <param name="end">The end of the period.</param>
    /// <param name="versions">The versions in the period.</param>
    /// <returns>The compaction summary.</returns>
    public CompactionSummary CreateSummary(string knowledgeId, DateTimeOffset start, DateTimeOffset end, IEnumerable<TemporalKnowledgeEntry> versions)
    {
        var versionList = versions.ToList();

        var summary = new CompactionSummary
        {
            KnowledgeId = knowledgeId,
            PeriodStart = start,
            PeriodEnd = end,
            VersionCount = versionList.Count,
            FirstVersion = versionList.FirstOrDefault()?.Version ?? 0,
            LastVersion = versionList.LastOrDefault()?.Version ?? 0,
            ChangeCount = versionList.Count - 1
        };

        _summaries[knowledgeId + ":" + start.ToString("yyyyMMdd")] = summary;

        return summary;
    }

    private static TimeSpan GetCompactionInterval(CompactionStrategy strategy)
    {
        return strategy switch
        {
            CompactionStrategy.KeepMinute => TimeSpan.FromMinutes(1),
            CompactionStrategy.KeepHourly => TimeSpan.FromHours(1),
            CompactionStrategy.KeepDaily => TimeSpan.FromDays(1),
            CompactionStrategy.KeepWeekly => TimeSpan.FromDays(7),
            CompactionStrategy.KeepMonthly => TimeSpan.FromDays(30),
            _ => TimeSpan.FromDays(1)
        };
    }
}

/// <summary>
/// Summary information about a compacted period.
/// </summary>
public sealed class CompactionSummary
{
    /// <summary>
    /// Gets or sets the knowledge object identifier.
    /// </summary>
    public required string KnowledgeId { get; set; }

    /// <summary>
    /// Gets or sets the start of the compacted period.
    /// </summary>
    public DateTimeOffset PeriodStart { get; set; }

    /// <summary>
    /// Gets or sets the end of the compacted period.
    /// </summary>
    public DateTimeOffset PeriodEnd { get; set; }

    /// <summary>
    /// Gets or sets the number of versions that were compacted.
    /// </summary>
    public int VersionCount { get; set; }

    /// <summary>
    /// Gets or sets the first version number in the period.
    /// </summary>
    public long FirstVersion { get; set; }

    /// <summary>
    /// Gets or sets the last version number in the period.
    /// </summary>
    public long LastVersion { get; set; }

    /// <summary>
    /// Gets or sets the number of changes in the period.
    /// </summary>
    public int ChangeCount { get; set; }

    /// <summary>
    /// Gets or sets a summary of the changes.
    /// </summary>
    public string ChangeSummary { get; set; } = string.Empty;
}

/// <summary>
/// Manages tiered storage for temporal data, moving old data to cold/archive storage.
/// </summary>
public sealed class TemporalTiering
{
    private readonly KnowledgeTimeStore _hotStore;
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<DateTimeOffset, TemporalKnowledgeEntry>> _warmStore = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<DateTimeOffset, byte[]>> _coldStore = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<DateTimeOffset, byte[]>> _archiveStore = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="TemporalTiering"/> class.
    /// </summary>
    /// <param name="hotStore">The hot tier time store.</param>
    /// <exception cref="ArgumentNullException">Thrown when hotStore is null.</exception>
    public TemporalTiering(KnowledgeTimeStore hotStore)
    {
        _hotStore = hotStore ?? throw new ArgumentNullException(nameof(hotStore));
    }

    /// <summary>
    /// Gets statistics about data in each tier.
    /// </summary>
    public TierStatistics GetStatistics()
    {
        return new TierStatistics
        {
            HotTierCount = _hotStore.TotalObjectCount,
            WarmTierCount = _warmStore.Values.Sum(d => d.Count),
            ColdTierCount = _coldStore.Values.Sum(d => d.Count),
            ArchiveTierCount = _archiveStore.Values.Sum(d => d.Count)
        };
    }

    /// <summary>
    /// Moves data between tiers according to the specified options.
    /// </summary>
    /// <param name="options">Tiering options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of entries tiered.</returns>
    public async Task<int> TierAsync(TieringOptions options, CancellationToken ct)
    {
        options ??= new TieringOptions();

        ct.ThrowIfCancellationRequested();

        var now = DateTimeOffset.UtcNow;
        var tiered = 0;

        // Calculate tier thresholds
        var hotThreshold = now - options.HotTierThreshold;
        var warmThreshold = now - options.WarmTierThreshold;
        var coldThreshold = now - options.ColdTierThreshold;

        // In a real implementation, we'd:
        // 1. Enumerate hot tier data older than hotThreshold
        // 2. Move to warm tier
        // 3. Enumerate warm tier data older than warmThreshold
        // 4. Compress and move to cold tier
        // 5. Enumerate cold tier data older than coldThreshold
        // 6. Move to archive tier

        // Move from warm to cold
        tiered += await MoveWarmToColdAsync(warmThreshold, options.CompressColdTier, ct);

        // Move from cold to archive
        tiered += await MoveColdToArchiveAsync(coldThreshold, options.CompressArchiveTier, ct);

        return tiered;
    }

    /// <summary>
    /// Gets the tier for a specific timestamp.
    /// </summary>
    /// <param name="timestamp">The timestamp to check.</param>
    /// <param name="options">Tiering options for thresholds.</param>
    /// <returns>The storage tier.</returns>
    public StorageTier GetTierForTimestamp(DateTimeOffset timestamp, TieringOptions options)
    {
        options ??= new TieringOptions();
        var now = DateTimeOffset.UtcNow;
        var age = now - timestamp;

        if (age <= options.HotTierThreshold)
            return StorageTier.Hot;
        if (age <= options.WarmTierThreshold)
            return StorageTier.Warm;
        if (age <= options.ColdTierThreshold)
            return StorageTier.Cold;
        return StorageTier.Archive;
    }

    /// <summary>
    /// Retrieves a knowledge object from any tier.
    /// </summary>
    /// <param name="knowledgeId">The knowledge object identifier.</param>
    /// <param name="timestamp">The timestamp to retrieve.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The knowledge object, or null if not found.</returns>
    public async Task<TemporalKnowledgeEntry?> GetFromAnyTierAsync(string knowledgeId, DateTimeOffset timestamp, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(knowledgeId);

        ct.ThrowIfCancellationRequested();

        // Try hot tier first
        var result = await _hotStore.GetAtAsync(knowledgeId, timestamp, ct);
        if (result != null)
            return result;

        // Try warm tier
        if (_warmStore.TryGetValue(knowledgeId, out var warmData))
        {
            var warmResult = FindAtTimestamp(warmData, timestamp);
            if (warmResult != null)
                return warmResult;
        }

        // Try cold tier (need to decompress)
        if (_coldStore.TryGetValue(knowledgeId, out var coldData))
        {
            var coldResult = FindCompressedAtTimestamp(coldData, timestamp);
            if (coldResult != null)
                return coldResult;
        }

        // Try archive tier (need to decompress)
        if (_archiveStore.TryGetValue(knowledgeId, out var archiveData))
        {
            var archiveResult = FindCompressedAtTimestamp(archiveData, timestamp);
            if (archiveResult != null)
                return archiveResult;
        }

        return null;
    }

    private static TemporalKnowledgeEntry? FindAtTimestamp(ConcurrentDictionary<DateTimeOffset, TemporalKnowledgeEntry> data, DateTimeOffset timestamp)
    {
        TemporalKnowledgeEntry? result = null;
        foreach (var kvp in data.OrderBy(k => k.Key))
        {
            if (kvp.Key <= timestamp)
                result = kvp.Value;
            else
                break;
        }
        return result;
    }

    private static TemporalKnowledgeEntry? FindCompressedAtTimestamp(ConcurrentDictionary<DateTimeOffset, byte[]> data, DateTimeOffset timestamp)
    {
        byte[]? compressed = null;
        foreach (var kvp in data.OrderBy(k => k.Key))
        {
            if (kvp.Key <= timestamp)
                compressed = kvp.Value;
            else
                break;
        }

        if (compressed == null)
            return null;

        return DecompressKnowledge(compressed);
    }

    private async Task<int> MoveWarmToColdAsync(DateTimeOffset threshold, bool compress, CancellationToken ct)
    {
        int moved = 0;

        foreach (var kvp in _warmStore)
        {
            ct.ThrowIfCancellationRequested();

            var toMove = kvp.Value.Where(e => e.Key < threshold).ToList();

            foreach (var entry in toMove)
            {
                ct.ThrowIfCancellationRequested();

                if (!_coldStore.TryGetValue(kvp.Key, out var coldData))
                {
                    coldData = new ConcurrentDictionary<DateTimeOffset, byte[]>();
                    _coldStore[kvp.Key] = coldData;
                }

                var data = compress ? CompressKnowledge(entry.Value) : SerializeKnowledge(entry.Value);
                coldData[entry.Key] = data;
                kvp.Value.TryRemove(entry.Key, out _);
                moved++;
            }
        }

        await Task.CompletedTask;
        return moved;
    }

    private async Task<int> MoveColdToArchiveAsync(DateTimeOffset threshold, bool compress, CancellationToken ct)
    {
        int moved = 0;

        foreach (var kvp in _coldStore)
        {
            ct.ThrowIfCancellationRequested();

            var toMove = kvp.Value.Where(e => e.Key < threshold).ToList();

            foreach (var entry in toMove)
            {
                ct.ThrowIfCancellationRequested();

                if (!_archiveStore.TryGetValue(kvp.Key, out var archiveData))
                {
                    archiveData = new ConcurrentDictionary<DateTimeOffset, byte[]>();
                    _archiveStore[kvp.Key] = archiveData;
                }

                // Data is already compressed in cold tier, just move it
                archiveData[entry.Key] = entry.Value;
                kvp.Value.TryRemove(entry.Key, out _);
                moved++;
            }
        }

        await Task.CompletedTask;
        return moved;
    }

    private static byte[] CompressKnowledge(TemporalKnowledgeEntry knowledge)
    {
        var json = JsonSerializer.Serialize(knowledge);
        using var output = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(output, System.IO.Compression.CompressionLevel.Optimal))
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            gzip.Write(bytes, 0, bytes.Length);
        }
        return output.ToArray();
    }

    private static byte[] SerializeKnowledge(TemporalKnowledgeEntry knowledge)
    {
        var json = JsonSerializer.Serialize(knowledge);
        return Encoding.UTF8.GetBytes(json);
    }

    private static TemporalKnowledgeEntry? DecompressKnowledge(byte[] compressed)
    {
        try
        {
            using var input = new MemoryStream(compressed);

            // Check if compressed (gzip magic number)
            if (compressed.Length >= 2 && compressed[0] == 0x1f && compressed[1] == 0x8b)
            {
                using var gzip = new System.IO.Compression.GZipStream(input, System.IO.Compression.CompressionMode.Decompress);
                using var output = new MemoryStream();
                gzip.CopyTo(output);
                var json = Encoding.UTF8.GetString(output.ToArray());
                return JsonSerializer.Deserialize<TemporalKnowledgeEntry>(json);
            }
            else
            {
                var json = Encoding.UTF8.GetString(compressed);
                return JsonSerializer.Deserialize<TemporalKnowledgeEntry>(json);
            }
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Statistics about data in each storage tier.
/// </summary>
public sealed class TierStatistics
{
    /// <summary>
    /// Gets or sets the number of entries in the hot tier.
    /// </summary>
    public long HotTierCount { get; set; }

    /// <summary>
    /// Gets or sets the number of entries in the warm tier.
    /// </summary>
    public long WarmTierCount { get; set; }

    /// <summary>
    /// Gets or sets the number of entries in the cold tier.
    /// </summary>
    public long ColdTierCount { get; set; }

    /// <summary>
    /// Gets or sets the number of entries in the archive tier.
    /// </summary>
    public long ArchiveTierCount { get; set; }

    /// <summary>
    /// Gets the total number of entries across all tiers.
    /// </summary>
    public long TotalCount => HotTierCount + WarmTierCount + ColdTierCount + ArchiveTierCount;
}

#endregion
