using System.Collections.Concurrent;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.Memory.Regeneration;

/// <summary>
/// Comprehensive metrics tracking for regeneration operations.
/// Provides success rates, accuracy distribution, performance statistics, and trend analysis.
/// </summary>
public sealed class RegenerationMetrics
{
    private readonly BoundedDictionary<string, StrategyMetrics> _strategyMetrics = new BoundedDictionary<string, StrategyMetrics>(1000);
    private readonly ConcurrentQueue<MetricEvent> _eventLog = new();
    private readonly MetricsConfiguration _config;
    private readonly object _aggregateLock = new();
    // Finding 3221: Separate counter for event log size — ConcurrentQueue.Count is O(n).
    private long _eventLogCount;

    private long _totalOperations;
    private long _successfulOperations;
    private long _failedOperations;
    private double _accuracySum;
    private double _accuracySumSquared;
    private TimeSpan _totalDuration;
    private DateTime _startTime;

    /// <summary>
    /// Initializes a new metrics instance.
    /// </summary>
    /// <param name="config">Metrics configuration.</param>
    public RegenerationMetrics(MetricsConfiguration? config = null)
    {
        _config = config ?? new MetricsConfiguration();
        _startTime = DateTime.UtcNow;
    }

    /// <summary>
    /// Records a successful regeneration operation.
    /// </summary>
    /// <param name="strategyId">Strategy that performed the operation.</param>
    /// <param name="accuracy">Achieved accuracy (0-1).</param>
    /// <param name="duration">Operation duration.</param>
    public void RecordSuccess(string strategyId, double accuracy, TimeSpan duration)
    {
        lock (_aggregateLock)
        {
            _totalOperations++;
            _successfulOperations++;
            _accuracySum += accuracy;
            _accuracySumSquared += accuracy * accuracy;
            _totalDuration += duration;
        }

        var metrics = GetOrCreateStrategyMetrics(strategyId);
        metrics.RecordSuccess(accuracy, duration);

        LogEvent(new MetricEvent
        {
            Timestamp = DateTime.UtcNow,
            EventType = MetricEventType.Success,
            StrategyId = strategyId,
            Accuracy = accuracy,
            Duration = duration
        });
    }

    /// <summary>
    /// Records a failed regeneration operation.
    /// </summary>
    /// <param name="strategyId">Strategy that attempted the operation.</param>
    /// <param name="errorType">Type of error that occurred.</param>
    /// <param name="duration">Operation duration before failure.</param>
    public void RecordFailure(string strategyId, string errorType, TimeSpan duration)
    {
        lock (_aggregateLock)
        {
            _totalOperations++;
            _failedOperations++;
            _totalDuration += duration;
        }

        var metrics = GetOrCreateStrategyMetrics(strategyId);
        metrics.RecordFailure(errorType, duration);

        LogEvent(new MetricEvent
        {
            Timestamp = DateTime.UtcNow,
            EventType = MetricEventType.Failure,
            StrategyId = strategyId,
            ErrorType = errorType,
            Duration = duration
        });
    }

    /// <summary>
    /// Records a verification result.
    /// </summary>
    /// <param name="strategyId">Strategy being verified.</param>
    /// <param name="score">Verification score.</param>
    /// <param name="passed">Whether verification passed.</param>
    public void RecordVerification(string strategyId, double score, bool passed)
    {
        var metrics = GetOrCreateStrategyMetrics(strategyId);
        metrics.RecordVerification(score, passed);

        LogEvent(new MetricEvent
        {
            Timestamp = DateTime.UtcNow,
            EventType = MetricEventType.Verification,
            StrategyId = strategyId,
            Accuracy = score,
            VerificationPassed = passed
        });
    }

    /// <summary>
    /// Gets aggregate statistics across all strategies.
    /// </summary>
    /// <returns>Aggregate statistics.</returns>
    public AggregateStatistics GetAggregateStatistics()
    {
        lock (_aggregateLock)
        {
            var meanAccuracy = _successfulOperations > 0
                ? _accuracySum / _successfulOperations
                : 0;

            var varianceAccuracy = _successfulOperations > 1
                ? (_accuracySumSquared - (_accuracySum * _accuracySum / _successfulOperations)) / (_successfulOperations - 1)
                : 0;

            var stdDevAccuracy = Math.Sqrt(Math.Max(0, varianceAccuracy));

            return new AggregateStatistics
            {
                TotalOperations = _totalOperations,
                SuccessfulOperations = _successfulOperations,
                FailedOperations = _failedOperations,
                SuccessRate = _totalOperations > 0
                    ? (double)_successfulOperations / _totalOperations
                    : 0,
                MeanAccuracy = meanAccuracy,
                StandardDeviationAccuracy = stdDevAccuracy,
                MinAccuracy = GetMinAccuracy(),
                MaxAccuracy = GetMaxAccuracy(),
                P99Accuracy = GetPercentileAccuracy(0.99),
                P999Accuracy = GetPercentileAccuracy(0.999),
                P9999Accuracy = GetPercentileAccuracy(0.9999),
                AverageDuration = _totalOperations > 0
                    ? TimeSpan.FromTicks(_totalDuration.Ticks / _totalOperations)
                    : TimeSpan.Zero,
                TotalDuration = _totalDuration,
                UptimeDuration = DateTime.UtcNow - _startTime,
                OperationsPerSecond = CalculateOpsPerSecond()
            };
        }
    }

    /// <summary>
    /// Gets metrics for a specific strategy.
    /// </summary>
    /// <param name="strategyId">The strategy ID.</param>
    /// <returns>Strategy metrics, or null if not found.</returns>
    public StrategyStatistics? GetStrategyStatistics(string strategyId)
    {
        if (_strategyMetrics.TryGetValue(strategyId, out var metrics))
        {
            return metrics.GetStatistics();
        }
        return null;
    }

    /// <summary>
    /// Gets metrics for all strategies.
    /// </summary>
    /// <returns>Dictionary of strategy metrics.</returns>
    public IReadOnlyDictionary<string, StrategyStatistics> GetAllStrategyStatistics()
    {
        return _strategyMetrics
            .ToDictionary(kv => kv.Key, kv => kv.Value.GetStatistics());
    }

    /// <summary>
    /// Gets accuracy distribution histogram.
    /// </summary>
    /// <param name="bucketCount">Number of histogram buckets.</param>
    /// <returns>Accuracy distribution data.</returns>
    public AccuracyDistribution GetAccuracyDistribution(int bucketCount = 20)
    {
        var accuracies = GetRecentAccuracies(1000);

        if (accuracies.Count == 0)
        {
            return new AccuracyDistribution
            {
                Buckets = Enumerable.Range(0, bucketCount)
                    .Select(i => new DistributionBucket
                    {
                        RangeStart = (double)i / bucketCount,
                        RangeEnd = (double)(i + 1) / bucketCount,
                        Count = 0
                    }).ToList()
            };
        }

        var bucketSize = 1.0 / bucketCount;
        var buckets = new int[bucketCount];

        foreach (var accuracy in accuracies)
        {
            var bucketIndex = Math.Min((int)(accuracy / bucketSize), bucketCount - 1);
            buckets[bucketIndex]++;
        }

        return new AccuracyDistribution
        {
            Buckets = Enumerable.Range(0, bucketCount)
                .Select(i => new DistributionBucket
                {
                    RangeStart = i * bucketSize,
                    RangeEnd = (i + 1) * bucketSize,
                    Count = buckets[i],
                    Percentage = (double)buckets[i] / accuracies.Count
                }).ToList(),
            TotalSamples = accuracies.Count,
            Mean = accuracies.Average(),
            Median = GetMedian(accuracies),
            Mode = GetMode(accuracies, bucketCount)
        };
    }

    /// <summary>
    /// Gets performance trends over time.
    /// </summary>
    /// <param name="periodMinutes">Period in minutes for each data point.</param>
    /// <param name="pointCount">Number of data points to return.</param>
    /// <returns>Trend data.</returns>
    public TrendData GetPerformanceTrends(int periodMinutes = 5, int pointCount = 12)
    {
        var now = DateTime.UtcNow;
        var points = new List<TrendPoint>();

        for (int i = pointCount - 1; i >= 0; i--)
        {
            var periodStart = now.AddMinutes(-periodMinutes * (i + 1));
            var periodEnd = now.AddMinutes(-periodMinutes * i);

            var periodEvents = _eventLog
                .Where(e => e.Timestamp >= periodStart && e.Timestamp < periodEnd)
                .ToList();

            var successCount = periodEvents.Count(e => e.EventType == MetricEventType.Success);
            var failureCount = periodEvents.Count(e => e.EventType == MetricEventType.Failure);
            var totalCount = successCount + failureCount;

            points.Add(new TrendPoint
            {
                Timestamp = periodEnd,
                SuccessRate = totalCount > 0 ? (double)successCount / totalCount : 0,
                AverageAccuracy = periodEvents
                    .Where(e => e.EventType == MetricEventType.Success)
                    .Select(e => e.Accuracy)
                    .DefaultIfEmpty(0)
                    .Average(),
                OperationCount = totalCount,
                AverageDuration = periodEvents
                    .Select(e => e.Duration)
                    .DefaultIfEmpty(TimeSpan.Zero)
                    .Average(d => d.TotalMilliseconds)
            });
        }

        return new TrendData
        {
            Points = points,
            PeriodMinutes = periodMinutes,
            OverallTrend = CalculateTrend(points)
        };
    }

    /// <summary>
    /// Gets error analysis data.
    /// </summary>
    /// <returns>Error analysis with categorization.</returns>
    public ErrorAnalysis GetErrorAnalysis()
    {
        var errorsByType = _strategyMetrics.Values
            .SelectMany(m => m.GetErrorDistribution())
            .GroupBy(kv => kv.Key)
            .ToDictionary(g => g.Key, g => g.Sum(kv => kv.Value));

        var errorsByStrategy = _strategyMetrics
            .ToDictionary(
                kv => kv.Key,
                kv => kv.Value.GetStatistics().FailedOperations);

        var recentErrors = _eventLog
            .Where(e => e.EventType == MetricEventType.Failure)
            .OrderByDescending(e => e.Timestamp)
            .Take(100)
            .Select(e => new ErrorEvent
            {
                Timestamp = e.Timestamp,
                StrategyId = e.StrategyId,
                ErrorType = e.ErrorType ?? "Unknown"
            })
            .ToList();

        return new ErrorAnalysis
        {
            ErrorsByType = errorsByType,
            ErrorsByStrategy = errorsByStrategy,
            TotalErrors = errorsByType.Values.Sum(),
            RecentErrors = recentErrors,
            MostCommonError = errorsByType
                .OrderByDescending(kv => kv.Value)
                .FirstOrDefault().Key ?? "None",
            ErrorRate = _totalOperations > 0
                ? (double)_failedOperations / _totalOperations
                : 0
        };
    }

    /// <summary>
    /// Gets a comprehensive report of all metrics.
    /// </summary>
    /// <returns>Complete metrics report.</returns>
    public MetricsReport GetReport()
    {
        return new MetricsReport
        {
            GeneratedAt = DateTime.UtcNow,
            AggregateStatistics = GetAggregateStatistics(),
            StrategyStatistics = GetAllStrategyStatistics(),
            AccuracyDistribution = GetAccuracyDistribution(),
            PerformanceTrends = GetPerformanceTrends(),
            ErrorAnalysis = GetErrorAnalysis(),
            TopPerformingStrategies = GetTopStrategies(5),
            RecommendedStrategies = GetRecommendedStrategies()
        };
    }

    /// <summary>
    /// Resets all metrics.
    /// </summary>
    public void Reset()
    {
        lock (_aggregateLock)
        {
            _totalOperations = 0;
            _successfulOperations = 0;
            _failedOperations = 0;
            _accuracySum = 0;
            _accuracySumSquared = 0;
            _totalDuration = TimeSpan.Zero;
            _startTime = DateTime.UtcNow;
        }

        _strategyMetrics.Clear();

        while (_eventLog.TryDequeue(out _)) { }
    }

    private StrategyMetrics GetOrCreateStrategyMetrics(string strategyId)
    {
        return _strategyMetrics.GetOrAdd(strategyId, _ => new StrategyMetrics(strategyId));
    }

    private void LogEvent(MetricEvent evt)
    {
        _eventLog.Enqueue(evt);

        // Finding 3221: ConcurrentQueue.Count is O(n) and not atomic with TryDequeue.
        // Use a separate counter to bound the log size without per-Count enumeration.
        // Accept that trimming is approximate (at most a few entries over the limit).
        if (Interlocked.Read(ref _eventLogCount) > _config.MaxEventLogSize)
        {
            if (_eventLog.TryDequeue(out _))
            {
                Interlocked.Decrement(ref _eventLogCount);
            }
        }
        else
        {
            Interlocked.Increment(ref _eventLogCount);
        }
    }

    private List<double> GetRecentAccuracies(int count)
    {
        return _eventLog
            .Where(e => e.EventType == MetricEventType.Success)
            .OrderByDescending(e => e.Timestamp)
            .Take(count)
            .Select(e => e.Accuracy)
            .ToList();
    }

    private double GetMinAccuracy()
    {
        var accuracies = GetRecentAccuracies(1000);
        return accuracies.Count > 0 ? accuracies.Min() : 0;
    }

    private double GetMaxAccuracy()
    {
        var accuracies = GetRecentAccuracies(1000);
        return accuracies.Count > 0 ? accuracies.Max() : 0;
    }

    private double GetPercentileAccuracy(double percentile)
    {
        var accuracies = GetRecentAccuracies(1000);
        if (accuracies.Count == 0) return 0;

        accuracies.Sort();
        var index = (int)Math.Ceiling(percentile * accuracies.Count) - 1;
        return accuracies[Math.Max(0, Math.Min(index, accuracies.Count - 1))];
    }

    private double GetMedian(List<double> values)
    {
        if (values.Count == 0) return 0;

        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;

        return sorted.Count % 2 == 0
            ? (sorted[mid - 1] + sorted[mid]) / 2.0
            : sorted[mid];
    }

    private double GetMode(List<double> values, int bucketCount)
    {
        if (values.Count == 0) return 0;

        var bucketSize = 1.0 / bucketCount;
        var buckets = new Dictionary<int, int>();

        foreach (var value in values)
        {
            var bucket = (int)(value / bucketSize);
            buckets[bucket] = buckets.GetValueOrDefault(bucket, 0) + 1;
        }

        var modeBucket = buckets.OrderByDescending(kv => kv.Value).First().Key;
        return (modeBucket + 0.5) * bucketSize;
    }

    private double CalculateOpsPerSecond()
    {
        var uptime = DateTime.UtcNow - _startTime;
        return uptime.TotalSeconds > 0 ? _totalOperations / uptime.TotalSeconds : 0;
    }

    private TrendDirection CalculateTrend(List<TrendPoint> points)
    {
        if (points.Count < 2) return TrendDirection.Stable;

        var firstHalf = points.Take(points.Count / 2).Average(p => p.SuccessRate);
        var secondHalf = points.Skip(points.Count / 2).Average(p => p.SuccessRate);

        var diff = secondHalf - firstHalf;
        if (diff > 0.05) return TrendDirection.Improving;
        if (diff < -0.05) return TrendDirection.Degrading;
        return TrendDirection.Stable;
    }

    private List<StrategyRanking> GetTopStrategies(int count)
    {
        return _strategyMetrics.Values
            .Select(m => m.GetStatistics())
            .Where(s => s.TotalOperations >= 10) // Minimum sample size
            .OrderByDescending(s => s.MeanAccuracy * s.SuccessRate)
            .Take(count)
            .Select((s, i) => new StrategyRanking
            {
                Rank = i + 1,
                StrategyId = s.StrategyId,
                Score = s.MeanAccuracy * s.SuccessRate,
                SuccessRate = s.SuccessRate,
                MeanAccuracy = s.MeanAccuracy,
                TotalOperations = s.TotalOperations
            })
            .ToList();
    }

    private List<string> GetRecommendedStrategies()
    {
        return _strategyMetrics.Values
            .Select(m => m.GetStatistics())
            .Where(s => s.TotalOperations >= 10 && s.SuccessRate >= 0.95 && s.MeanAccuracy >= 0.9999)
            .OrderByDescending(s => s.MeanAccuracy)
            .Take(3)
            .Select(s => s.StrategyId)
            .ToList();
    }
}

/// <summary>
/// Metrics for a single strategy.
/// </summary>
internal sealed class StrategyMetrics
{
    private readonly string _strategyId;
    private readonly object _lock = new();
    private readonly BoundedDictionary<string, int> _errorCounts = new BoundedDictionary<string, int>(1000);
    private readonly List<double> _recentAccuracies = new();
    private readonly List<double> _recentDurations = new();

    private long _totalOperations;
    private long _successfulOperations;
    private long _failedOperations;
    private long _verificationsPassed;
    private long _verificationsTotal;
    private double _accuracySum;
    private double _accuracySumSquared;
    private TimeSpan _totalDuration;
    private double _minAccuracy = double.MaxValue;
    private double _maxAccuracy = double.MinValue;

    public StrategyMetrics(string strategyId)
    {
        _strategyId = strategyId;
    }

    public void RecordSuccess(double accuracy, TimeSpan duration)
    {
        lock (_lock)
        {
            _totalOperations++;
            _successfulOperations++;
            _accuracySum += accuracy;
            _accuracySumSquared += accuracy * accuracy;
            _totalDuration += duration;
            _minAccuracy = Math.Min(_minAccuracy, accuracy);
            _maxAccuracy = Math.Max(_maxAccuracy, accuracy);

            _recentAccuracies.Add(accuracy);
            _recentDurations.Add(duration.TotalMilliseconds);

            if (_recentAccuracies.Count > 1000)
            {
                _recentAccuracies.RemoveAt(0);
                _recentDurations.RemoveAt(0);
            }
        }
    }

    public void RecordFailure(string errorType, TimeSpan duration)
    {
        lock (_lock)
        {
            _totalOperations++;
            _failedOperations++;
            _totalDuration += duration;
        }

        _errorCounts.AddOrUpdate(errorType, 1, (_, count) => count + 1);
    }

    public void RecordVerification(double score, bool passed)
    {
        lock (_lock)
        {
            _verificationsTotal++;
            if (passed) _verificationsPassed++;
        }
    }

    public StrategyStatistics GetStatistics()
    {
        lock (_lock)
        {
            var meanAccuracy = _successfulOperations > 0
                ? _accuracySum / _successfulOperations
                : 0;

            var varianceAccuracy = _successfulOperations > 1
                ? (_accuracySumSquared - (_accuracySum * _accuracySum / _successfulOperations)) / (_successfulOperations - 1)
                : 0;

            return new StrategyStatistics
            {
                StrategyId = _strategyId,
                TotalOperations = _totalOperations,
                SuccessfulOperations = _successfulOperations,
                FailedOperations = _failedOperations,
                SuccessRate = _totalOperations > 0
                    ? (double)_successfulOperations / _totalOperations
                    : 0,
                MeanAccuracy = meanAccuracy,
                StandardDeviationAccuracy = Math.Sqrt(Math.Max(0, varianceAccuracy)),
                MinAccuracy = _minAccuracy == double.MaxValue ? 0 : _minAccuracy,
                MaxAccuracy = _maxAccuracy == double.MinValue ? 0 : _maxAccuracy,
                AverageDuration = _totalOperations > 0
                    ? TimeSpan.FromTicks(_totalDuration.Ticks / _totalOperations)
                    : TimeSpan.Zero,
                VerificationPassRate = _verificationsTotal > 0
                    ? (double)_verificationsPassed / _verificationsTotal
                    : 0,
                RecentAccuracyTrend = CalculateRecentTrend()
            };
        }
    }

    public Dictionary<string, int> GetErrorDistribution()
    {
        return new Dictionary<string, int>(_errorCounts);
    }

    private double CalculateRecentTrend()
    {
        // Finding 3220: Snapshot the list under lock before computing trend to avoid
        // reading a partially-written or resized list.
        double[] snapshot;
        lock (_lock)
        {
            if (_recentAccuracies.Count < 20) return 0;
            snapshot = _recentAccuracies.ToArray();
        }

        var half = snapshot.Length / 2;
        var firstHalf = snapshot.Take(half).Average();
        var secondHalf = snapshot.Skip(half).Average();

        return secondHalf - firstHalf;
    }
}

/// <summary>
/// Configuration for metrics collection.
/// </summary>
public sealed record MetricsConfiguration
{
    /// <summary>Maximum events to keep in log.</summary>
    public int MaxEventLogSize { get; init; } = 10000;

    /// <summary>Enable detailed event logging.</summary>
    public bool EnableDetailedLogging { get; init; } = true;
}

/// <summary>
/// A single metric event.
/// </summary>
internal sealed record MetricEvent
{
    public DateTime Timestamp { get; init; }
    public MetricEventType EventType { get; init; }
    public string StrategyId { get; init; } = "";
    public double Accuracy { get; init; }
    public TimeSpan Duration { get; init; }
    public string? ErrorType { get; init; }
    public bool VerificationPassed { get; init; }
}

/// <summary>
/// Metric event type.
/// </summary>
internal enum MetricEventType
{
    Success,
    Failure,
    Verification
}

/// <summary>
/// Aggregate statistics across all strategies.
/// </summary>
public sealed record AggregateStatistics
{
    /// <summary>Total operations performed.</summary>
    public long TotalOperations { get; init; }

    /// <summary>Successful operations count.</summary>
    public long SuccessfulOperations { get; init; }

    /// <summary>Failed operations count.</summary>
    public long FailedOperations { get; init; }

    /// <summary>Overall success rate.</summary>
    public double SuccessRate { get; init; }

    /// <summary>Mean accuracy of successful operations.</summary>
    public double MeanAccuracy { get; init; }

    /// <summary>Standard deviation of accuracy.</summary>
    public double StandardDeviationAccuracy { get; init; }

    /// <summary>Minimum observed accuracy.</summary>
    public double MinAccuracy { get; init; }

    /// <summary>Maximum observed accuracy.</summary>
    public double MaxAccuracy { get; init; }

    /// <summary>99th percentile accuracy.</summary>
    public double P99Accuracy { get; init; }

    /// <summary>99.9th percentile accuracy.</summary>
    public double P999Accuracy { get; init; }

    /// <summary>99.99th percentile accuracy.</summary>
    public double P9999Accuracy { get; init; }

    /// <summary>Average operation duration.</summary>
    public TimeSpan AverageDuration { get; init; }

    /// <summary>Total operation duration.</summary>
    public TimeSpan TotalDuration { get; init; }

    /// <summary>System uptime.</summary>
    public TimeSpan UptimeDuration { get; init; }

    /// <summary>Operations per second.</summary>
    public double OperationsPerSecond { get; init; }
}

/// <summary>
/// Statistics for a single strategy.
/// </summary>
public sealed record StrategyStatistics
{
    /// <summary>Strategy identifier.</summary>
    public string StrategyId { get; init; } = "";

    /// <summary>Total operations.</summary>
    public long TotalOperations { get; init; }

    /// <summary>Successful operations.</summary>
    public long SuccessfulOperations { get; init; }

    /// <summary>Failed operations.</summary>
    public long FailedOperations { get; init; }

    /// <summary>Success rate.</summary>
    public double SuccessRate { get; init; }

    /// <summary>Mean accuracy.</summary>
    public double MeanAccuracy { get; init; }

    /// <summary>Standard deviation of accuracy.</summary>
    public double StandardDeviationAccuracy { get; init; }

    /// <summary>Minimum accuracy.</summary>
    public double MinAccuracy { get; init; }

    /// <summary>Maximum accuracy.</summary>
    public double MaxAccuracy { get; init; }

    /// <summary>Average duration.</summary>
    public TimeSpan AverageDuration { get; init; }

    /// <summary>Verification pass rate.</summary>
    public double VerificationPassRate { get; init; }

    /// <summary>Recent accuracy trend (positive = improving).</summary>
    public double RecentAccuracyTrend { get; init; }
}

/// <summary>
/// Accuracy distribution histogram.
/// </summary>
public sealed record AccuracyDistribution
{
    /// <summary>Histogram buckets.</summary>
    public List<DistributionBucket> Buckets { get; init; } = new();

    /// <summary>Total samples in distribution.</summary>
    public int TotalSamples { get; init; }

    /// <summary>Mean value.</summary>
    public double Mean { get; init; }

    /// <summary>Median value.</summary>
    public double Median { get; init; }

    /// <summary>Mode value.</summary>
    public double Mode { get; init; }
}

/// <summary>
/// A bucket in the distribution histogram.
/// </summary>
public sealed record DistributionBucket
{
    /// <summary>Range start (inclusive).</summary>
    public double RangeStart { get; init; }

    /// <summary>Range end (exclusive).</summary>
    public double RangeEnd { get; init; }

    /// <summary>Count in this bucket.</summary>
    public int Count { get; init; }

    /// <summary>Percentage of total.</summary>
    public double Percentage { get; init; }
}

/// <summary>
/// Performance trend data.
/// </summary>
public sealed record TrendData
{
    /// <summary>Trend data points.</summary>
    public List<TrendPoint> Points { get; init; } = new();

    /// <summary>Period in minutes for each point.</summary>
    public int PeriodMinutes { get; init; }

    /// <summary>Overall trend direction.</summary>
    public TrendDirection OverallTrend { get; init; }
}

/// <summary>
/// A single trend data point.
/// </summary>
public sealed record TrendPoint
{
    /// <summary>Point timestamp.</summary>
    public DateTime Timestamp { get; init; }

    /// <summary>Success rate during period.</summary>
    public double SuccessRate { get; init; }

    /// <summary>Average accuracy during period.</summary>
    public double AverageAccuracy { get; init; }

    /// <summary>Operation count during period.</summary>
    public int OperationCount { get; init; }

    /// <summary>Average duration in milliseconds.</summary>
    public double AverageDuration { get; init; }
}

/// <summary>
/// Trend direction.
/// </summary>
public enum TrendDirection
{
    /// <summary>Performance improving.</summary>
    Improving,

    /// <summary>Performance stable.</summary>
    Stable,

    /// <summary>Performance degrading.</summary>
    Degrading
}

/// <summary>
/// Error analysis data.
/// </summary>
public sealed record ErrorAnalysis
{
    /// <summary>Errors grouped by type.</summary>
    public Dictionary<string, int> ErrorsByType { get; init; } = new();

    /// <summary>Errors grouped by strategy.</summary>
    public Dictionary<string, long> ErrorsByStrategy { get; init; } = new();

    /// <summary>Total error count.</summary>
    public int TotalErrors { get; init; }

    /// <summary>Recent error events.</summary>
    public List<ErrorEvent> RecentErrors { get; init; } = new();

    /// <summary>Most common error type.</summary>
    public string MostCommonError { get; init; } = "";

    /// <summary>Overall error rate.</summary>
    public double ErrorRate { get; init; }
}

/// <summary>
/// A recorded error event.
/// </summary>
public sealed record ErrorEvent
{
    /// <summary>Event timestamp.</summary>
    public DateTime Timestamp { get; init; }

    /// <summary>Strategy that failed.</summary>
    public string StrategyId { get; init; } = "";

    /// <summary>Error type.</summary>
    public string ErrorType { get; init; } = "";
}

/// <summary>
/// Strategy ranking information.
/// </summary>
public sealed record StrategyRanking
{
    /// <summary>Rank position.</summary>
    public int Rank { get; init; }

    /// <summary>Strategy identifier.</summary>
    public string StrategyId { get; init; } = "";

    /// <summary>Composite score.</summary>
    public double Score { get; init; }

    /// <summary>Success rate.</summary>
    public double SuccessRate { get; init; }

    /// <summary>Mean accuracy.</summary>
    public double MeanAccuracy { get; init; }

    /// <summary>Total operations.</summary>
    public long TotalOperations { get; init; }
}

/// <summary>
/// Complete metrics report.
/// </summary>
public sealed record MetricsReport
{
    /// <summary>Report generation timestamp.</summary>
    public DateTime GeneratedAt { get; init; }

    /// <summary>Aggregate statistics.</summary>
    public AggregateStatistics AggregateStatistics { get; init; } = new();

    /// <summary>Per-strategy statistics.</summary>
    public IReadOnlyDictionary<string, StrategyStatistics> StrategyStatistics { get; init; } =
        new Dictionary<string, StrategyStatistics>();

    /// <summary>Accuracy distribution.</summary>
    public AccuracyDistribution AccuracyDistribution { get; init; } = new();

    /// <summary>Performance trends.</summary>
    public TrendData PerformanceTrends { get; init; } = new();

    /// <summary>Error analysis.</summary>
    public ErrorAnalysis ErrorAnalysis { get; init; } = new();

    /// <summary>Top performing strategies.</summary>
    public List<StrategyRanking> TopPerformingStrategies { get; init; } = new();

    /// <summary>Recommended strategies based on performance.</summary>
    public List<string> RecommendedStrategies { get; init; } = new();
}
