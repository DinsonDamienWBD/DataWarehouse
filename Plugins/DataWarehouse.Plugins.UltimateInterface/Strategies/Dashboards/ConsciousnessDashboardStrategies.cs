using System.Collections.Concurrent;
using DataWarehouse.SDK.Contracts.Consciousness;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateInterface.Dashboards.Strategies;

/// <summary>
/// Aggregate consciousness statistics for dashboard consumption.
/// Mirrors the shape of ConsciousnessStatistics from the governance plugin
/// but is locally defined to preserve plugin isolation.
/// </summary>
/// <param name="TotalScored">Total number of objects that have been scored.</param>
/// <param name="AverageScore">Mean composite consciousness score across all objects.</param>
/// <param name="MedianScore">Median composite consciousness score.</param>
/// <param name="ByGrade">Count of objects in each consciousness grade.</param>
/// <param name="ByAction">Count of objects for each recommended action.</param>
/// <param name="DarkDataCount">Number of objects with composite score below 25 (dark data).</param>
/// <param name="ComputedAt">UTC timestamp when these statistics were computed.</param>
public sealed record DashboardConsciousnessStatistics(
    int TotalScored,
    double AverageScore,
    double MedianScore,
    Dictionary<ConsciousnessGrade, int> ByGrade,
    Dictionary<ConsciousnessAction, int> ByAction,
    int DarkDataCount,
    DateTime ComputedAt);

/// <summary>
/// Aggregate statistics for the consciousness overview dashboard, including
/// score distribution, top value/liability objects, and generation timestamp.
/// </summary>
/// <param name="Statistics">Aggregate consciousness statistics from the score store.</param>
/// <param name="Distribution">Score distribution buckets (0-10, 10-20, ..., 90-100).</param>
/// <param name="TopValue">Top N objects by highest consciousness score.</param>
/// <param name="TopLiability">Top N objects by highest liability score.</param>
/// <param name="GeneratedAt">UTC timestamp when this dashboard data was generated.</param>
public sealed record ConsciousnessDashboardData(
    DashboardConsciousnessStatistics Statistics,
    List<ConsciousnessDistributionBucket> Distribution,
    List<ConsciousnessTopN> TopValue,
    List<ConsciousnessTopN> TopLiability,
    DateTime GeneratedAt);

/// <summary>
/// Represents a score distribution bucket for histogram visualization.
/// </summary>
/// <param name="RangeStart">Inclusive lower bound of the score range.</param>
/// <param name="RangeEnd">Exclusive upper bound of the score range.</param>
/// <param name="ObjectCount">Number of objects whose scores fall within this range.</param>
/// <param name="TotalSizeBytes">Aggregate storage size of objects in this bucket.</param>
public sealed record ConsciousnessDistributionBucket(
    int RangeStart,
    int RangeEnd,
    int ObjectCount,
    long TotalSizeBytes);

/// <summary>
/// Represents a top-N entry for consciousness dashboard leaderboards.
/// </summary>
/// <param name="ObjectId">Unique identifier of the data object.</param>
/// <param name="Score">The relevant score (composite, value, or liability).</param>
/// <param name="Grade">The consciousness grade of the object.</param>
/// <param name="Description">Human-readable description of why this object ranks highly.</param>
public sealed record ConsciousnessTopN(
    string ObjectId,
    double Score,
    ConsciousnessGrade Grade,
    string Description);

/// <summary>
/// Represents a point-in-time snapshot of consciousness score trends for time-series visualization.
/// </summary>
/// <param name="Timestamp">The point in time this snapshot represents.</param>
/// <param name="AverageScore">Average composite consciousness score at this point.</param>
/// <param name="TotalObjects">Total number of scored objects at this point.</param>
/// <param name="GradeDistribution">Distribution of objects across consciousness grades.</param>
public sealed record ConsciousnessTrendPoint(
    DateTime Timestamp,
    double AverageScore,
    int TotalObjects,
    Dictionary<ConsciousnessGrade, int> GradeDistribution);

/// <summary>
/// Represents a dark data analysis report for the dark data dashboard.
/// </summary>
/// <param name="TotalDarkObjects">Total number of objects classified as dark data (score &lt; 25).</param>
/// <param name="DarkDataSizeBytes">Total storage consumed by dark data objects.</param>
/// <param name="DarkDataPercentage">Percentage of total data estate that is dark.</param>
/// <param name="TopCandidates">Top dark data objects most likely needing remediation.</param>
/// <param name="ReportGeneratedAt">UTC timestamp when this report was generated.</param>
public sealed record DarkDataReport(
    int TotalDarkObjects,
    long DarkDataSizeBytes,
    double DarkDataPercentage,
    List<DarkDataCandidate> TopCandidates,
    DateTime ReportGeneratedAt);

/// <summary>
/// Represents a dark data candidate object for remediation prioritization.
/// </summary>
/// <param name="ObjectId">Unique identifier of the dark data object.</param>
/// <param name="Score">Current composite consciousness score.</param>
/// <param name="EstimatedSizeBytes">Estimated storage size of the object in bytes.</param>
/// <param name="DarkSinceDays">Number of days the object has been classified as dark data.</param>
/// <param name="Reason">Human-readable reason why this object is classified as dark.</param>
public sealed record DarkDataCandidate(
    string ObjectId,
    double Score,
    long EstimatedSizeBytes,
    int DarkSinceDays,
    string Reason);

/// <summary>
/// Dashboard strategy that generates a comprehensive consciousness overview including
/// grade distribution, action distribution, top-N value and liability objects, and
/// score distribution buckets for histogram visualization.
/// </summary>
/// <remarks>
/// Reads from a <see cref="ConsciousnessScoreProvider"/> delegate that abstracts the
/// consciousness score store. Computes distribution buckets in 10-point intervals
/// from 0 to 100. All computations use thread-safe in-memory operations.
/// </remarks>
public sealed class ConsciousnessOverviewDashboardStrategy
{
    private readonly Func<IReadOnlyList<ConsciousnessScore>> _scoreProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConsciousnessOverviewDashboardStrategy"/> class.
    /// </summary>
    /// <param name="scoreProvider">
    /// A delegate that provides all current consciousness scores. Typically wraps
    /// ConsciousnessScoreStore.GetAllScores().
    /// </param>
    public ConsciousnessOverviewDashboardStrategy(Func<IReadOnlyList<ConsciousnessScore>> scoreProvider)
    {
        _scoreProvider = scoreProvider ?? throw new ArgumentNullException(nameof(scoreProvider));
    }

    /// <summary>
    /// Generates the consciousness overview dashboard data with grade distribution,
    /// top value/liability objects, and score distribution buckets.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="ConsciousnessDashboardData"/> containing all overview metrics.</returns>
    public Task<ConsciousnessDashboardData> GenerateOverviewAsync(CancellationToken ct = default)
    {
        var allScores = _scoreProvider();

        // Compute aggregate statistics
        var statistics = ComputeStatistics(allScores);

        // Compute distribution buckets: 0-10, 10-20, ..., 90-100
        var distribution = new List<ConsciousnessDistributionBucket>();
        for (int rangeStart = 0; rangeStart < 100; rangeStart += 10)
        {
            int rangeEnd = rangeStart + 10;
            int start = rangeStart; // captured for lambda
            int end = rangeEnd;
            var inBucket = allScores.Where(s =>
                s.CompositeScore >= start && (end == 100 ? s.CompositeScore <= end : s.CompositeScore < end)).ToList();

            distribution.Add(new ConsciousnessDistributionBucket(
                RangeStart: rangeStart,
                RangeEnd: rangeEnd,
                ObjectCount: inBucket.Count,
                TotalSizeBytes: 0)); // Size requires metadata lookup; zero for in-memory
        }

        // Top 10 highest value (highest composite score)
        var topValue = allScores
            .OrderByDescending(s => s.Value.OverallScore)
            .Take(10)
            .Select(s => new ConsciousnessTopN(
                ObjectId: s.ObjectId,
                Score: s.Value.OverallScore,
                Grade: s.Grade,
                Description: $"Value score {s.Value.OverallScore:F1}, action: {s.RecommendedAction}"))
            .ToList();

        // Top 10 highest liability
        var topLiability = allScores
            .OrderByDescending(s => s.Liability.OverallScore)
            .Take(10)
            .Select(s => new ConsciousnessTopN(
                ObjectId: s.ObjectId,
                Score: s.Liability.OverallScore,
                Grade: s.Grade,
                Description: $"Liability score {s.Liability.OverallScore:F1}, action: {s.RecommendedAction}"))
            .ToList();

        return Task.FromResult(new ConsciousnessDashboardData(
            Statistics: statistics,
            Distribution: distribution,
            TopValue: topValue,
            TopLiability: topLiability,
            GeneratedAt: DateTime.UtcNow));
    }

    /// <summary>
    /// Computes aggregate statistics from the provided scores.
    /// </summary>
    private static DashboardConsciousnessStatistics ComputeStatistics(IReadOnlyList<ConsciousnessScore> scores)
    {
        if (scores.Count == 0)
        {
            return new DashboardConsciousnessStatistics(
                TotalScored: 0,
                AverageScore: 0.0,
                MedianScore: 0.0,
                ByGrade: new Dictionary<ConsciousnessGrade, int>(),
                ByAction: new Dictionary<ConsciousnessAction, int>(),
                DarkDataCount: 0,
                ComputedAt: DateTime.UtcNow);
        }

        double average = scores.Average(s => s.CompositeScore);

        var sorted = scores.OrderBy(s => s.CompositeScore).ToList();
        int mid = sorted.Count / 2;
        double median = sorted.Count % 2 == 0
            ? (sorted[mid - 1].CompositeScore + sorted[mid].CompositeScore) / 2.0
            : sorted[mid].CompositeScore;

        var byGrade = new Dictionary<ConsciousnessGrade, int>();
        foreach (var grade in Enum.GetValues<ConsciousnessGrade>())
        {
            int count = scores.Count(s => s.Grade == grade);
            if (count > 0) byGrade[grade] = count;
        }

        var byAction = new Dictionary<ConsciousnessAction, int>();
        foreach (var action in Enum.GetValues<ConsciousnessAction>())
        {
            int count = scores.Count(s => s.RecommendedAction == action);
            if (count > 0) byAction[action] = count;
        }

        int darkDataCount = scores.Count(s => s.CompositeScore < 25.0);

        return new DashboardConsciousnessStatistics(
            TotalScored: scores.Count,
            AverageScore: average,
            MedianScore: median,
            ByGrade: byGrade,
            ByAction: byAction,
            DarkDataCount: darkDataCount,
            ComputedAt: DateTime.UtcNow);
    }
}

/// <summary>
/// Dashboard strategy that tracks consciousness score trends over time, providing
/// daily/weekly/monthly averages, grade migration tracking, and archive/purge activity timelines.
/// </summary>
/// <remarks>
/// Stores trend snapshots in a <see cref="ConcurrentDictionary{DateTime, ConsciousnessTrendPoint}"/>
/// keyed by UTC timestamp. Snapshots are recorded periodically via <see cref="RecordSnapshot"/>.
/// Trend generation filters snapshots by time range and interval for flexible time-series queries.
/// </remarks>
public sealed class ConsciousnessTrendDashboardStrategy
{
    private readonly BoundedDictionary<DateTime, ConsciousnessTrendPoint> _snapshots = new BoundedDictionary<DateTime, ConsciousnessTrendPoint>(1000);

    /// <summary>
    /// Gets the total number of recorded snapshots.
    /// </summary>
    public int SnapshotCount => _snapshots.Count;

    /// <summary>
    /// Records a point-in-time snapshot of consciousness statistics for trend tracking.
    /// Should be called periodically (e.g., hourly or daily) by a background job.
    /// </summary>
    /// <param name="statistics">The current aggregate consciousness statistics.</param>
    public void RecordSnapshot(DashboardConsciousnessStatistics statistics)
    {
        ArgumentNullException.ThrowIfNull(statistics);

        var gradeDistribution = new Dictionary<ConsciousnessGrade, int>(statistics.ByGrade);

        var point = new ConsciousnessTrendPoint(
            Timestamp: statistics.ComputedAt,
            AverageScore: statistics.AverageScore,
            TotalObjects: statistics.TotalScored,
            GradeDistribution: gradeDistribution);

        _snapshots[statistics.ComputedAt] = point;
    }

    /// <summary>
    /// Generates trend data for the specified time range, resampled to the given interval.
    /// </summary>
    /// <param name="from">Start of the time range (inclusive).</param>
    /// <param name="to">End of the time range (inclusive).</param>
    /// <param name="interval">The resampling interval (e.g., 1 day, 1 hour).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A list of <see cref="ConsciousnessTrendPoint"/> entries, one per interval bucket.
    /// If multiple snapshots fall within an interval, the latest snapshot is used.
    /// Empty intervals are filled with the most recent preceding snapshot.
    /// </returns>
    public Task<List<ConsciousnessTrendPoint>> GenerateTrendAsync(
        DateTime from, DateTime to, TimeSpan interval, CancellationToken ct = default)
    {
        if (interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval), "Interval must be positive.");
        if (to < from)
            throw new ArgumentOutOfRangeException(nameof(to), "End time must be after start time.");

        // Get all snapshots in range, sorted by time
        var inRange = _snapshots.Values
            .Where(s => s.Timestamp >= from && s.Timestamp <= to)
            .OrderBy(s => s.Timestamp)
            .ToList();

        var result = new List<ConsciousnessTrendPoint>();

        if (inRange.Count == 0)
            return Task.FromResult(result);

        // Bucket snapshots into intervals
        var currentBucketStart = from;
        int snapshotIndex = 0;
        ConsciousnessTrendPoint? lastKnown = null;

        while (currentBucketStart <= to)
        {
            ct.ThrowIfCancellationRequested();

            var bucketEnd = currentBucketStart + interval;
            ConsciousnessTrendPoint? bucketPoint = null;

            // Find the latest snapshot in this bucket
            while (snapshotIndex < inRange.Count && inRange[snapshotIndex].Timestamp < bucketEnd)
            {
                if (inRange[snapshotIndex].Timestamp >= currentBucketStart)
                {
                    bucketPoint = inRange[snapshotIndex];
                }
                snapshotIndex++;
            }

            // Use bucket point, or fill with last known
            var pointToAdd = bucketPoint ?? lastKnown;
            if (pointToAdd != null)
            {
                // Re-timestamp to bucket start for uniform time series
                result.Add(pointToAdd with { Timestamp = currentBucketStart });
                lastKnown = pointToAdd;
            }

            currentBucketStart = bucketEnd;
        }

        return Task.FromResult(result);
    }

    /// <summary>
    /// Gets the raw snapshot at a specific timestamp, if available.
    /// </summary>
    /// <param name="timestamp">The exact timestamp to look up.</param>
    /// <returns>The trend point, or null if no snapshot exists at that time.</returns>
    public ConsciousnessTrendPoint? GetSnapshot(DateTime timestamp)
    {
        return _snapshots.TryGetValue(timestamp, out var point) ? point : null;
    }
}

/// <summary>
/// Dashboard strategy for dark data analytics, providing visibility into data objects
/// with insufficient consciousness (score &lt; 25), tracking remediation progress,
/// and monitoring dark data discovery events.
/// </summary>
/// <remarks>
/// Subscribes to "consciousness.dark_data.discovered" events to update dark data metrics.
/// Tracks remediation by monitoring objects that transition from dark to scored states.
/// All storage is in-memory using <see cref="ConcurrentDictionary{TKey, TValue}"/>.
/// </remarks>
public sealed class DarkDataDashboardStrategy
{
    private readonly Func<IReadOnlyList<ConsciousnessScore>> _scoreProvider;
    private readonly BoundedDictionary<string, DarkDataCandidate> _darkObjects = new BoundedDictionary<string, DarkDataCandidate>(1000);
    private readonly BoundedDictionary<string, DateTime> _remediatedObjects = new BoundedDictionary<string, DateTime>(1000);
    private readonly BoundedDictionary<string, DateTime> _discoveredAt = new BoundedDictionary<string, DateTime>(1000);

    /// <summary>
    /// Initializes a new instance of the <see cref="DarkDataDashboardStrategy"/> class.
    /// </summary>
    /// <param name="scoreProvider">
    /// A delegate that provides all current consciousness scores. Typically wraps
    /// ConsciousnessScoreStore.GetAllScores().
    /// </param>
    public DarkDataDashboardStrategy(Func<IReadOnlyList<ConsciousnessScore>> scoreProvider)
    {
        _scoreProvider = scoreProvider ?? throw new ArgumentNullException(nameof(scoreProvider));
    }

    /// <summary>
    /// Records a dark data discovery event. Should be called when a "consciousness.dark_data.discovered"
    /// event is received from the message bus.
    /// </summary>
    /// <param name="objectId">The discovered dark data object identifier.</param>
    /// <param name="estimatedSizeBytes">Estimated storage size of the dark object.</param>
    /// <param name="reason">Reason for dark data classification.</param>
    public void RecordDiscovery(string objectId, long estimatedSizeBytes, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);

        var now = DateTime.UtcNow;
        _discoveredAt.TryAdd(objectId, now);

        var discoveredDate = _discoveredAt.GetValueOrDefault(objectId, now);
        int darkDays = (int)(now - discoveredDate).TotalDays;

        _darkObjects[objectId] = new DarkDataCandidate(
            ObjectId: objectId,
            Score: 0.0,
            EstimatedSizeBytes: estimatedSizeBytes,
            DarkSinceDays: darkDays,
            Reason: reason);
    }

    /// <summary>
    /// Records that a previously dark data object has been remediated (scored or classified).
    /// </summary>
    /// <param name="objectId">The remediated object identifier.</param>
    public void RecordRemediation(string objectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        _remediatedObjects[objectId] = DateTime.UtcNow;
        _darkObjects.TryRemove(objectId, out _);
    }

    /// <summary>
    /// Gets the total number of remediated objects since tracking began.
    /// </summary>
    public int RemediatedCount => _remediatedObjects.Count;

    /// <summary>
    /// Generates a comprehensive dark data report combining discovered dark objects
    /// with current consciousness scores to provide up-to-date dark data analytics.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="DarkDataReport"/> with current dark data metrics.</returns>
    public Task<DarkDataReport> GenerateReportAsync(CancellationToken ct = default)
    {
        var allScores = _scoreProvider();
        var now = DateTime.UtcNow;

        // Dark objects from current scores (composite < 25)
        var darkFromScores = allScores
            .Where(s => s.CompositeScore < 25.0)
            .ToList();

        int totalObjects = allScores.Count;
        int totalDark = darkFromScores.Count;

        // Merge with tracked dark objects for candidates
        var candidates = new List<DarkDataCandidate>();

        foreach (var score in darkFromScores)
        {
            ct.ThrowIfCancellationRequested();

            _discoveredAt.TryGetValue(score.ObjectId, out var discoveredDate);
            int darkDays = discoveredDate != default
                ? (int)(now - discoveredDate).TotalDays
                : 0;

            // Check if we have a tracked candidate with more detail
            if (_darkObjects.TryGetValue(score.ObjectId, out var tracked))
            {
                candidates.Add(tracked with
                {
                    Score = score.CompositeScore,
                    DarkSinceDays = darkDays
                });
            }
            else
            {
                candidates.Add(new DarkDataCandidate(
                    ObjectId: score.ObjectId,
                    Score: score.CompositeScore,
                    EstimatedSizeBytes: 0,
                    DarkSinceDays: darkDays,
                    Reason: DetermineReason(score)));
            }
        }

        // Also include tracked dark objects not in current scores (not yet scored)
        foreach (var kvp in _darkObjects)
        {
            if (!darkFromScores.Any(s => s.ObjectId == kvp.Key))
            {
                candidates.Add(kvp.Value);
                totalDark++;
            }
        }

        // Sort by score ascending (worst first), then by size descending
        candidates.Sort((a, b) =>
        {
            int scoreCompare = a.Score.CompareTo(b.Score);
            return scoreCompare != 0 ? scoreCompare : b.EstimatedSizeBytes.CompareTo(a.EstimatedSizeBytes);
        });

        // Take top candidates for the report
        var topCandidates = candidates.Take(50).ToList();

        long totalDarkSize = candidates.Sum(c => c.EstimatedSizeBytes);
        double darkPercentage = totalObjects > 0
            ? 100.0 * totalDark / (totalObjects + _darkObjects.Count(kvp => !darkFromScores.Any(s => s.ObjectId == kvp.Key)))
            : _darkObjects.Count > 0 ? 100.0 : 0.0;

        return Task.FromResult(new DarkDataReport(
            TotalDarkObjects: totalDark,
            DarkDataSizeBytes: totalDarkSize,
            DarkDataPercentage: Math.Round(darkPercentage, 2),
            TopCandidates: topCandidates,
            ReportGeneratedAt: now));
    }

    /// <summary>
    /// Determines a human-readable reason for dark data classification based on score dimensions.
    /// </summary>
    private static string DetermineReason(ConsciousnessScore score)
    {
        if (score.Value.OverallScore < 10.0)
            return "Extremely low value: no access patterns, no downstream consumers";
        if (score.Value.OverallScore < 25.0 && score.Liability.OverallScore < 25.0)
            return "Low value and low liability: candidate for archive or purge";
        if (score.Liability.OverallScore >= 50.0)
            return "Low consciousness with high liability: regulatory risk exposure";
        return "Insufficient consciousness characterization: needs classification";
    }
}
