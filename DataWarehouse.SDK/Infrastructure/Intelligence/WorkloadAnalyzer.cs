using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Infrastructure.Intelligence;

/// <summary>
/// Time-of-day workload classification based on UTC hour.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 77: AI Policy Intelligence (AIPI-03)")]
public enum TimeOfDayPattern
{
    /// <summary>Insufficient data to classify.</summary>
    Unknown = 0,

    /// <summary>Off-peak hours: 0:00-6:00 UTC and 22:00-24:00 UTC.</summary>
    OffPeak = 1,

    /// <summary>Normal business hours: 6:00-9:00 UTC and 17:00-22:00 UTC.</summary>
    Normal = 2,

    /// <summary>Peak business hours: 9:00-17:00 UTC.</summary>
    Peak = 3
}

/// <summary>
/// Seasonal workload trend based on multi-day observation rate comparison.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 77: AI Policy Intelligence (AIPI-03)")]
public enum SeasonalTrend
{
    /// <summary>Insufficient data to determine trend (less than 14 days).</summary>
    Unknown = 0,

    /// <summary>Workload is stable (within 10% of previous period).</summary>
    Stable = 1,

    /// <summary>Workload is increasing (more than 10% above previous period).</summary>
    Rising = 2,

    /// <summary>Workload is decreasing (more than 10% below previous period).</summary>
    Falling = 3
}

/// <summary>
/// Point-in-time workload profile describing observation throughput and patterns.
/// </summary>
/// <param name="ObservationsPerSecond">Current observation throughput rate.</param>
/// <param name="TimeOfDay">Current time-of-day classification.</param>
/// <param name="Season">Current seasonal trend based on multi-day comparison.</param>
/// <param name="IsBurstDetected">True if current rate exceeds 3x rolling average.</param>
/// <param name="LastBurstAt">Timestamp of the most recent burst event.</param>
/// <param name="BurstCount">Total burst events detected in this session.</param>
[SdkCompatibility("6.0.0", Notes = "Phase 77: AI Policy Intelligence (AIPI-03)")]
public sealed record WorkloadProfile(
    double ObservationsPerSecond,
    TimeOfDayPattern TimeOfDay,
    SeasonalTrend Season,
    bool IsBurstDetected,
    DateTimeOffset LastBurstAt,
    int BurstCount
);

/// <summary>
/// AI advisor that analyzes workload patterns from observation event streams.
/// Tracks per-minute observation counts over a 24-hour rolling window, classifies
/// time-of-day patterns, detects seasonal trends via 7-day comparison, and
/// identifies burst events when current throughput exceeds 3x the rolling baseline.
/// </summary>
/// <remarks>
/// AIPI-03: Workload-aware context for AI policy recommendations.
/// Internal tracking uses a circular buffer of 1-minute buckets (1440 = 24 hours).
/// Seasonal trend comparison requires 14+ days of accumulated data; returns Unknown
/// if insufficient data is available.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 77: AI Policy Intelligence (AIPI-03)")]
public sealed class WorkloadAnalyzer : IAiAdvisor
{
    private volatile WorkloadProfile _currentProfile;

    // Per-minute observation count tracking (24-hour circular buffer)
    private readonly int[] _minuteBuckets;
    private readonly long[] _minuteTimestamps; // minute epoch for each bucket
    private int _bucketWriteIndex;
    private int _bucketCount;

    // Daily accumulation for seasonal trend (28-day circular buffer of daily totals)
    private readonly long[] _dailyTotals;
    private readonly int[] _dailyDayNumbers; // day-of-epoch for each entry
    private int _dailyWriteIndex;
    private int _dailyCount;

    // Session burst tracking
    private int _burstCount;
    private DateTimeOffset _lastBurstAt;

    /// <summary>Number of 1-minute buckets (24 hours).</summary>
    private const int MinuteBucketCount = 1440;

    /// <summary>Number of daily buckets for seasonal trend (28 days).</summary>
    private const int DailyBucketCount = 28;

    /// <summary>Rolling average window for baseline calculation (60 minutes).</summary>
    private const int BaselineWindowMinutes = 60;

    /// <summary>Burst detection threshold multiplier over rolling average.</summary>
    private const double BurstThresholdMultiplier = 3.0;

    /// <summary>Seasonal trend change threshold (10%).</summary>
    private const double SeasonalChangeThreshold = 0.10;

    /// <summary>
    /// Creates a new WorkloadAnalyzer advisor.
    /// </summary>
    public WorkloadAnalyzer()
    {
        _minuteBuckets = new int[MinuteBucketCount];
        _minuteTimestamps = new long[MinuteBucketCount];
        _bucketWriteIndex = 0;
        _bucketCount = 0;

        _dailyTotals = new long[DailyBucketCount];
        _dailyDayNumbers = new int[DailyBucketCount];
        _dailyWriteIndex = 0;
        _dailyCount = 0;

        _burstCount = 0;
        _lastBurstAt = DateTimeOffset.MinValue;

        _currentProfile = new WorkloadProfile(
            ObservationsPerSecond: 0,
            TimeOfDay: ClassifyTimeOfDay(DateTimeOffset.UtcNow),
            Season: SeasonalTrend.Unknown,
            IsBurstDetected: false,
            LastBurstAt: DateTimeOffset.MinValue,
            BurstCount: 0
        );
    }

    /// <inheritdoc />
    public string AdvisorId => "workload_analyzer";

    /// <summary>
    /// Latest workload profile. Updated via volatile reference swap.
    /// </summary>
    public WorkloadProfile CurrentProfile => _currentProfile;

    /// <summary>
    /// True during Peak hours or when a burst is currently detected.
    /// Indicates the system should prioritize throughput over conservation.
    /// </summary>
    public bool ShouldOptimizeForThroughput
    {
        get
        {
            WorkloadProfile profile = _currentProfile;
            return profile.TimeOfDay == TimeOfDayPattern.Peak || profile.IsBurstDetected;
        }
    }

    /// <summary>
    /// True during OffPeak hours with a Falling seasonal trend.
    /// Indicates the system should conserve resources (reduce caching, batch sizes, etc.).
    /// </summary>
    public bool ShouldConserveResources
    {
        get
        {
            WorkloadProfile profile = _currentProfile;
            return profile.TimeOfDay == TimeOfDayPattern.OffPeak &&
                   profile.Season == SeasonalTrend.Falling;
        }
    }

    /// <inheritdoc />
    public Task ProcessObservationsAsync(
        IReadOnlyList<ObservationEvent> batch,
        CancellationToken ct)
    {
        if (batch.Count == 0) return Task.CompletedTask;

        DateTimeOffset now = DateTimeOffset.UtcNow;
        long currentMinuteEpoch = GetMinuteEpoch(now);
        int currentDayNumber = GetDayNumber(now);

        // Update per-minute bucket
        UpdateMinuteBucket(currentMinuteEpoch, batch.Count);

        // Update daily total
        UpdateDailyBucket(currentDayNumber, batch.Count);

        // Calculate current throughput (observations per second over last minute)
        double obsPerSecond = GetCurrentBucketCount(currentMinuteEpoch) / 60.0;

        // Calculate rolling average for burst detection
        double rollingAverage = CalculateRollingAverage(currentMinuteEpoch);
        int currentMinuteCount = GetCurrentBucketCount(currentMinuteEpoch);

        // Burst detection: current minute count > 3x rolling 60-minute average
        bool isBurst = rollingAverage > 0 &&
                       currentMinuteCount > BurstThresholdMultiplier * rollingAverage;

        if (isBurst)
        {
            _burstCount++;
            _lastBurstAt = now;
        }

        // Classify time of day and seasonal trend
        TimeOfDayPattern timeOfDay = ClassifyTimeOfDay(now);
        SeasonalTrend season = CalculateSeasonalTrend();

        // Update profile via volatile reference swap
        _currentProfile = new WorkloadProfile(
            ObservationsPerSecond: Math.Round(obsPerSecond, 2),
            TimeOfDay: timeOfDay,
            Season: season,
            IsBurstDetected: isBurst,
            LastBurstAt: _lastBurstAt,
            BurstCount: _burstCount
        );

        return Task.CompletedTask;
    }

    private void UpdateMinuteBucket(long minuteEpoch, int count)
    {
        // Check if we're still in the same minute as the last write
        if (_bucketCount > 0)
        {
            int lastIndex = (_bucketWriteIndex - 1 + MinuteBucketCount) % MinuteBucketCount;
            if (_minuteTimestamps[lastIndex] == minuteEpoch)
            {
                _minuteBuckets[lastIndex] += count;
                return;
            }
        }

        // New minute — advance to next bucket
        _minuteTimestamps[_bucketWriteIndex] = minuteEpoch;
        _minuteBuckets[_bucketWriteIndex] = count;
        _bucketWriteIndex = (_bucketWriteIndex + 1) % MinuteBucketCount;
        if (_bucketCount < MinuteBucketCount) _bucketCount++;
    }

    private void UpdateDailyBucket(int dayNumber, int count)
    {
        if (_dailyCount > 0)
        {
            int lastIndex = (_dailyWriteIndex - 1 + DailyBucketCount) % DailyBucketCount;
            if (_dailyDayNumbers[lastIndex] == dayNumber)
            {
                _dailyTotals[lastIndex] += count;
                return;
            }
        }

        // New day — advance to next bucket
        _dailyDayNumbers[_dailyWriteIndex] = dayNumber;
        _dailyTotals[_dailyWriteIndex] = count;
        _dailyWriteIndex = (_dailyWriteIndex + 1) % DailyBucketCount;
        if (_dailyCount < DailyBucketCount) _dailyCount++;
    }

    private int GetCurrentBucketCount(long minuteEpoch)
    {
        if (_bucketCount == 0) return 0;

        int lastIndex = (_bucketWriteIndex - 1 + MinuteBucketCount) % MinuteBucketCount;
        if (_minuteTimestamps[lastIndex] == minuteEpoch)
        {
            return _minuteBuckets[lastIndex];
        }

        return 0;
    }

    private double CalculateRollingAverage(long currentMinuteEpoch)
    {
        if (_bucketCount == 0) return 0;

        long windowStart = currentMinuteEpoch - BaselineWindowMinutes;
        long sum = 0;
        int counted = 0;

        for (int i = 0; i < _bucketCount; i++)
        {
            int idx = (_bucketWriteIndex - 1 - i + MinuteBucketCount * 2) % MinuteBucketCount;
            long ts = _minuteTimestamps[idx];

            if (ts < windowStart) break;
            if (ts == currentMinuteEpoch) continue; // Exclude current minute from baseline

            sum += _minuteBuckets[idx];
            counted++;
        }

        return counted > 0 ? (double)sum / counted : 0;
    }

    private SeasonalTrend CalculateSeasonalTrend()
    {
        if (_dailyCount < 14) return SeasonalTrend.Unknown;

        // Compare last 7 days to previous 7 days
        long recentSum = 0;
        long previousSum = 0;
        int recentCount = 0;
        int previousCount = 0;

        for (int i = 0; i < _dailyCount && (recentCount < 7 || previousCount < 7); i++)
        {
            int idx = (_dailyWriteIndex - 1 - i + DailyBucketCount * 2) % DailyBucketCount;

            if (recentCount < 7)
            {
                recentSum += _dailyTotals[idx];
                recentCount++;
            }
            else if (previousCount < 7)
            {
                previousSum += _dailyTotals[idx];
                previousCount++;
            }
        }

        if (previousCount < 7 || previousSum == 0) return SeasonalTrend.Unknown;

        double recentAvg = (double)recentSum / recentCount;
        double previousAvg = (double)previousSum / previousCount;
        double changeRatio = (recentAvg - previousAvg) / previousAvg;

        if (changeRatio > SeasonalChangeThreshold) return SeasonalTrend.Rising;
        if (changeRatio < -SeasonalChangeThreshold) return SeasonalTrend.Falling;
        return SeasonalTrend.Stable;
    }

    private static TimeOfDayPattern ClassifyTimeOfDay(DateTimeOffset timestamp)
    {
        int hour = timestamp.UtcDateTime.Hour;

        return hour switch
        {
            >= 0 and < 6 => TimeOfDayPattern.OffPeak,
            >= 6 and < 9 => TimeOfDayPattern.Normal,
            >= 9 and < 17 => TimeOfDayPattern.Peak,
            >= 17 and < 22 => TimeOfDayPattern.Normal,
            _ => TimeOfDayPattern.OffPeak // 22-24
        };
    }

    private static long GetMinuteEpoch(DateTimeOffset timestamp)
    {
        return timestamp.ToUnixTimeSeconds() / 60;
    }

    private static int GetDayNumber(DateTimeOffset timestamp)
    {
        return (int)(timestamp.ToUnixTimeSeconds() / 86400);
    }
}
