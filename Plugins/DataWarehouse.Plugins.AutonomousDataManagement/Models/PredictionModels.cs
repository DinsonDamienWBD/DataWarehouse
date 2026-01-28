using DataWarehouse.SDK.AI;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.AutonomousDataManagement.Models;

/// <summary>
/// Access pattern predictor for tiering optimization.
/// Uses statistical methods with AI-enhanced analysis.
/// </summary>
public sealed class AccessPatternPredictor
{
    private readonly ConcurrentDictionary<string, AccessPatternData> _patterns;
    private readonly ExponentialMovingAverage _ema;
    private readonly int _historySize;

    public AccessPatternPredictor(int historySize = 1000)
    {
        _patterns = new ConcurrentDictionary<string, AccessPatternData>();
        _ema = new ExponentialMovingAverage(0.1);
        _historySize = historySize;
    }

    /// <summary>
    /// Records an access event for pattern learning.
    /// </summary>
    public void RecordAccess(string resourceId, AccessEventType eventType, long sizeBytes, Dictionary<string, object>? metadata = null)
    {
        var pattern = _patterns.GetOrAdd(resourceId, _ => new AccessPatternData { ResourceId = resourceId });

        lock (pattern)
        {
            var now = DateTime.UtcNow;
            pattern.TotalAccesses++;
            pattern.LastAccessTime = now;
            pattern.TotalBytesAccessed += sizeBytes;

            if (pattern.FirstAccessTime == default)
                pattern.FirstAccessTime = now;

            // Track access type counts
            switch (eventType)
            {
                case AccessEventType.Read:
                    pattern.ReadCount++;
                    break;
                case AccessEventType.Write:
                    pattern.WriteCount++;
                    break;
                case AccessEventType.Delete:
                    pattern.DeleteCount++;
                    break;
            }

            // Update hourly distribution
            var hour = now.Hour;
            pattern.HourlyDistribution[hour]++;

            // Update daily pattern
            var dayOfWeek = (int)now.DayOfWeek;
            pattern.DailyDistribution[dayOfWeek]++;

            // Add to access history
            pattern.AccessHistory.Add(new AccessRecord
            {
                Timestamp = now,
                EventType = eventType,
                SizeBytes = sizeBytes
            });

            // Trim history if needed
            while (pattern.AccessHistory.Count > _historySize)
            {
                pattern.AccessHistory.RemoveAt(0);
            }

            // Update derived metrics
            UpdateDerivedMetrics(pattern);
        }
    }

    /// <summary>
    /// Predicts access probability for the next time period.
    /// </summary>
    public AccessPrediction PredictAccess(string resourceId, TimeSpan predictionWindow)
    {
        if (!_patterns.TryGetValue(resourceId, out var pattern))
        {
            return new AccessPrediction
            {
                ResourceId = resourceId,
                Probability = 0.5,
                Confidence = 0.0,
                Reason = "No historical data available"
            };
        }

        lock (pattern)
        {
            var now = DateTime.UtcNow;
            var currentHour = now.Hour;
            var dayOfWeek = (int)now.DayOfWeek;

            // Calculate base probability from historical frequency
            var daysSinceFirstAccess = Math.Max(1, (now - pattern.FirstAccessTime).TotalDays);
            var accessesPerDay = pattern.TotalAccesses / daysSinceFirstAccess;
            var windowDays = predictionWindow.TotalDays;
            var baseProb = Math.Min(1.0, accessesPerDay * windowDays / (1 + accessesPerDay * windowDays));

            // Adjust for time-of-day pattern
            var maxHourlyAccess = pattern.HourlyDistribution.Values.Max();
            var hourlyFactor = maxHourlyAccess > 0
                ? (double)pattern.HourlyDistribution[currentHour] / maxHourlyAccess
                : 1.0;

            // Adjust for day-of-week pattern
            var maxDailyAccess = pattern.DailyDistribution.Values.Max();
            var dailyFactor = maxDailyAccess > 0
                ? (double)pattern.DailyDistribution[dayOfWeek] / maxDailyAccess
                : 1.0;

            // Recency factor (exponential decay)
            var daysSinceLastAccess = (now - pattern.LastAccessTime).TotalDays;
            var recencyFactor = Math.Exp(-daysSinceLastAccess / 7.0);

            // Combined probability
            var probability = baseProb * 0.4 + hourlyFactor * 0.2 + dailyFactor * 0.2 + recencyFactor * 0.2;

            // Confidence based on data volume
            var confidence = Math.Min(1.0, pattern.TotalAccesses / 100.0);

            return new AccessPrediction
            {
                ResourceId = resourceId,
                Probability = Math.Clamp(probability, 0, 1),
                Confidence = confidence,
                PredictedAccessesPerDay = accessesPerDay,
                HourlyFactor = hourlyFactor,
                DailyFactor = dailyFactor,
                RecencyFactor = recencyFactor,
                Reason = GeneratePredictionReason(pattern, probability)
            };
        }
    }

    /// <summary>
    /// Gets the optimal storage tier for a resource based on access patterns.
    /// </summary>
    public TierRecommendation RecommendTier(string resourceId)
    {
        if (!_patterns.TryGetValue(resourceId, out var pattern))
        {
            return new TierRecommendation
            {
                ResourceId = resourceId,
                RecommendedTier = StorageTier.Standard,
                Confidence = 0.0,
                Reason = "No access history available"
            };
        }

        lock (pattern)
        {
            var now = DateTime.UtcNow;
            var daysSinceFirstAccess = Math.Max(1, (now - pattern.FirstAccessTime).TotalDays);
            var daysSinceLastAccess = (now - pattern.LastAccessTime).TotalDays;
            var accessesPerDay = pattern.TotalAccesses / daysSinceFirstAccess;

            StorageTier tier;
            string reason;

            if (accessesPerDay > 10 || daysSinceLastAccess < 1)
            {
                tier = StorageTier.Hot;
                reason = $"High access frequency ({accessesPerDay:F1}/day) or recent access";
            }
            else if (accessesPerDay > 1 || daysSinceLastAccess < 7)
            {
                tier = StorageTier.Warm;
                reason = $"Moderate access frequency ({accessesPerDay:F2}/day)";
            }
            else if (accessesPerDay > 0.1 || daysSinceLastAccess < 30)
            {
                tier = StorageTier.Standard;
                reason = $"Low access frequency ({accessesPerDay:F3}/day)";
            }
            else if (daysSinceLastAccess < 90)
            {
                tier = StorageTier.Cool;
                reason = $"Infrequent access ({daysSinceLastAccess:F0} days since last access)";
            }
            else
            {
                tier = StorageTier.Archive;
                reason = $"Rarely accessed ({daysSinceLastAccess:F0} days since last access)";
            }

            var confidence = Math.Min(1.0, pattern.TotalAccesses / 50.0);

            return new TierRecommendation
            {
                ResourceId = resourceId,
                RecommendedTier = tier,
                Confidence = confidence,
                AccessesPerDay = accessesPerDay,
                DaysSinceLastAccess = daysSinceLastAccess,
                Reason = reason
            };
        }
    }

    /// <summary>
    /// Gets all tracked patterns.
    /// </summary>
    public IEnumerable<AccessPatternSummary> GetAllPatterns()
    {
        return _patterns.Values.Select(p =>
        {
            lock (p)
            {
                var daysSince = Math.Max(1, (DateTime.UtcNow - p.FirstAccessTime).TotalDays);
                return new AccessPatternSummary
                {
                    ResourceId = p.ResourceId,
                    TotalAccesses = p.TotalAccesses,
                    AccessesPerDay = p.TotalAccesses / daysSince,
                    LastAccessTime = p.LastAccessTime,
                    ReadWriteRatio = p.WriteCount > 0 ? (double)p.ReadCount / p.WriteCount : p.ReadCount
                };
            }
        });
    }

    private void UpdateDerivedMetrics(AccessPatternData pattern)
    {
        if (pattern.AccessHistory.Count < 2) return;

        // Calculate access intervals
        var intervals = new List<double>();
        for (int i = 1; i < pattern.AccessHistory.Count; i++)
        {
            var interval = (pattern.AccessHistory[i].Timestamp - pattern.AccessHistory[i - 1].Timestamp).TotalHours;
            intervals.Add(interval);
        }

        pattern.AverageAccessInterval = intervals.Average();
        pattern.AccessIntervalStdDev = CalculateStdDev(intervals);

        // Update EMA of access rate
        var recentInterval = intervals.LastOrDefault();
        if (recentInterval > 0)
        {
            pattern.EmaAccessRate = _ema.Update(1.0 / recentInterval);
        }
    }

    private double CalculateStdDev(List<double> values)
    {
        if (values.Count < 2) return 0;
        var mean = values.Average();
        var sumSquares = values.Sum(v => Math.Pow(v - mean, 2));
        return Math.Sqrt(sumSquares / values.Count);
    }

    private string GeneratePredictionReason(AccessPatternData pattern, double probability)
    {
        var daysSinceLastAccess = (DateTime.UtcNow - pattern.LastAccessTime).TotalDays;
        var accessesPerDay = pattern.TotalAccesses / Math.Max(1, (DateTime.UtcNow - pattern.FirstAccessTime).TotalDays);

        if (probability > 0.8)
            return $"High probability based on frequent access ({accessesPerDay:F1}/day)";
        else if (probability > 0.5)
            return $"Moderate probability, last accessed {daysSinceLastAccess:F1} days ago";
        else if (probability > 0.2)
            return $"Lower probability, infrequent access pattern";
        else
            return $"Low probability, data appears cold ({daysSinceLastAccess:F0} days since last access)";
    }
}

/// <summary>
/// Failure predictor using statistical analysis and ML patterns.
/// </summary>
public sealed class FailurePredictor
{
    private readonly ConcurrentDictionary<string, ComponentHealthData> _healthData;
    private readonly List<FailureEvent> _historicalFailures;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private const int MaxHistoricalFailures = 10000;

    public FailurePredictor()
    {
        _healthData = new ConcurrentDictionary<string, ComponentHealthData>();
        _historicalFailures = new List<FailureEvent>();
    }

    /// <summary>
    /// Records health metrics for a component.
    /// </summary>
    public void RecordMetrics(string componentId, HealthMetrics metrics)
    {
        var health = _healthData.GetOrAdd(componentId, _ => new ComponentHealthData { ComponentId = componentId });

        lock (health)
        {
            health.LastUpdateTime = DateTime.UtcNow;
            health.MetricsHistory.Add(new TimestampedMetrics
            {
                Timestamp = DateTime.UtcNow,
                Metrics = metrics
            });

            // Trim history
            while (health.MetricsHistory.Count > 1000)
            {
                health.MetricsHistory.RemoveAt(0);
            }

            // Update anomaly detection
            health.CpuAnomalyScore = CalculateAnomalyScore(health.MetricsHistory.Select(m => m.Metrics.CpuUsage).ToList(), metrics.CpuUsage);
            health.MemoryAnomalyScore = CalculateAnomalyScore(health.MetricsHistory.Select(m => m.Metrics.MemoryUsage).ToList(), metrics.MemoryUsage);
            health.DiskAnomalyScore = CalculateAnomalyScore(health.MetricsHistory.Select(m => m.Metrics.DiskUsage).ToList(), metrics.DiskUsage);
            health.ErrorRateAnomalyScore = CalculateAnomalyScore(health.MetricsHistory.Select(m => m.Metrics.ErrorRate).ToList(), metrics.ErrorRate);
        }
    }

    /// <summary>
    /// Records a failure event for learning.
    /// </summary>
    public async Task RecordFailureAsync(string componentId, string failureType, string description)
    {
        await _lock.WaitAsync();
        try
        {
            _historicalFailures.Add(new FailureEvent
            {
                ComponentId = componentId,
                FailureType = failureType,
                Description = description,
                Timestamp = DateTime.UtcNow,
                PrecedingMetrics = GetRecentMetrics(componentId, TimeSpan.FromHours(1))
            });

            while (_historicalFailures.Count > MaxHistoricalFailures)
            {
                _historicalFailures.RemoveAt(0);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Predicts failure probability for a component.
    /// </summary>
    public FailurePrediction PredictFailure(string componentId)
    {
        if (!_healthData.TryGetValue(componentId, out var health))
        {
            return new FailurePrediction
            {
                ComponentId = componentId,
                Probability = 0,
                Confidence = 0,
                TimeToFailure = TimeSpan.MaxValue,
                Reason = "No health data available"
            };
        }

        lock (health)
        {
            // Calculate composite anomaly score
            var anomalyScore = (health.CpuAnomalyScore + health.MemoryAnomalyScore + health.DiskAnomalyScore + health.ErrorRateAnomalyScore) / 4.0;

            // Check for critical thresholds
            var latestMetrics = health.MetricsHistory.LastOrDefault()?.Metrics;
            var criticalFlags = 0;
            var warnings = new List<string>();

            if (latestMetrics != null)
            {
                if (latestMetrics.CpuUsage > 90)
                {
                    criticalFlags++;
                    warnings.Add($"CPU critically high ({latestMetrics.CpuUsage:F1}%)");
                }
                if (latestMetrics.MemoryUsage > 90)
                {
                    criticalFlags++;
                    warnings.Add($"Memory critically high ({latestMetrics.MemoryUsage:F1}%)");
                }
                if (latestMetrics.DiskUsage > 95)
                {
                    criticalFlags++;
                    warnings.Add($"Disk nearly full ({latestMetrics.DiskUsage:F1}%)");
                }
                if (latestMetrics.ErrorRate > 0.1)
                {
                    criticalFlags++;
                    warnings.Add($"High error rate ({latestMetrics.ErrorRate:P2})");
                }
            }

            // Calculate probability
            var baseProbability = AIMath.Sigmoid((float)(anomalyScore - 2.0));
            var criticalBoost = criticalFlags * 0.15;
            var probability = Math.Min(1.0, baseProbability + criticalBoost);

            // Estimate time to failure based on trends
            var timeToFailure = EstimateTimeToFailure(health);

            var confidence = Math.Min(1.0, health.MetricsHistory.Count / 100.0);

            return new FailurePrediction
            {
                ComponentId = componentId,
                Probability = probability,
                Confidence = confidence,
                TimeToFailure = timeToFailure,
                AnomalyScore = anomalyScore,
                CriticalWarnings = warnings,
                Reason = probability > 0.5
                    ? $"High failure risk: {string.Join(", ", warnings)}"
                    : "System health within normal parameters"
            };
        }
    }

    /// <summary>
    /// Gets components at risk of failure.
    /// </summary>
    public IEnumerable<FailurePrediction> GetAtRiskComponents(double minProbability = 0.3)
    {
        return _healthData.Keys
            .Select(PredictFailure)
            .Where(p => p.Probability >= minProbability)
            .OrderByDescending(p => p.Probability);
    }

    private double CalculateAnomalyScore(List<double> history, double currentValue)
    {
        if (history.Count < 10) return 0;

        var mean = history.Average();
        var stdDev = Math.Sqrt(history.Sum(x => Math.Pow(x - mean, 2)) / history.Count);

        if (stdDev < 0.001) return 0;

        // Z-score
        return Math.Abs((currentValue - mean) / stdDev);
    }

    private TimeSpan EstimateTimeToFailure(ComponentHealthData health)
    {
        if (health.MetricsHistory.Count < 10)
            return TimeSpan.FromDays(365); // No data, assume healthy

        // Simple linear extrapolation for resource exhaustion
        var recentMetrics = health.MetricsHistory.TakeLast(20).ToList();
        var diskTrend = CalculateTrend(recentMetrics.Select(m => m.Metrics.DiskUsage).ToList());
        var memoryTrend = CalculateTrend(recentMetrics.Select(m => m.Metrics.MemoryUsage).ToList());

        var hoursToFullDisk = diskTrend > 0.001 ? (100 - recentMetrics.Last().Metrics.DiskUsage) / diskTrend : double.MaxValue;
        var hoursToFullMemory = memoryTrend > 0.001 ? (100 - recentMetrics.Last().Metrics.MemoryUsage) / memoryTrend : double.MaxValue;

        var minHours = Math.Min(hoursToFullDisk, hoursToFullMemory);
        return minHours < double.MaxValue ? TimeSpan.FromHours(minHours) : TimeSpan.FromDays(365);
    }

    private double CalculateTrend(List<double> values)
    {
        if (values.Count < 2) return 0;

        // Simple linear regression
        var n = values.Count;
        var sumX = Enumerable.Range(0, n).Sum();
        var sumY = values.Sum();
        var sumXY = Enumerable.Range(0, n).Select(i => i * values[i]).Sum();
        var sumX2 = Enumerable.Range(0, n).Select(i => i * i).Sum();

        var slope = (n * sumXY - sumX * sumY) / (n * sumX2 - sumX * sumX);
        return slope;
    }

    private List<HealthMetrics> GetRecentMetrics(string componentId, TimeSpan window)
    {
        if (!_healthData.TryGetValue(componentId, out var health))
            return new List<HealthMetrics>();

        var cutoff = DateTime.UtcNow - window;
        lock (health)
        {
            return health.MetricsHistory
                .Where(m => m.Timestamp >= cutoff)
                .Select(m => m.Metrics)
                .ToList();
        }
    }
}

#region Supporting Types

public enum AccessEventType
{
    Read,
    Write,
    Delete,
    List
}

/// <summary>
/// Storage tier classification for tiering engine.
/// This is a plugin-local enum that matches the tiering logic requirements.
/// </summary>
public enum StorageTier
{
    Hot,
    Warm,
    Standard,
    Cool,
    Archive
}

public sealed class AccessPatternData
{
    public string ResourceId { get; init; } = string.Empty;
    public DateTime FirstAccessTime { get; set; }
    public DateTime LastAccessTime { get; set; }
    public long TotalAccesses { get; set; }
    public long TotalBytesAccessed { get; set; }
    public long ReadCount { get; set; }
    public long WriteCount { get; set; }
    public long DeleteCount { get; set; }
    public Dictionary<int, long> HourlyDistribution { get; } = Enumerable.Range(0, 24).ToDictionary(h => h, _ => 0L);
    public Dictionary<int, long> DailyDistribution { get; } = Enumerable.Range(0, 7).ToDictionary(d => d, _ => 0L);
    public List<AccessRecord> AccessHistory { get; } = new();
    public double AverageAccessInterval { get; set; }
    public double AccessIntervalStdDev { get; set; }
    public double EmaAccessRate { get; set; }
}

public sealed class AccessRecord
{
    public DateTime Timestamp { get; init; }
    public AccessEventType EventType { get; init; }
    public long SizeBytes { get; init; }
}

public sealed class AccessPrediction
{
    public string ResourceId { get; init; } = string.Empty;
    public double Probability { get; init; }
    public double Confidence { get; init; }
    public double PredictedAccessesPerDay { get; init; }
    public double HourlyFactor { get; init; }
    public double DailyFactor { get; init; }
    public double RecencyFactor { get; init; }
    public string Reason { get; init; } = string.Empty;
}

public sealed class TierRecommendation
{
    public string ResourceId { get; init; } = string.Empty;
    public StorageTier RecommendedTier { get; init; }
    public double Confidence { get; init; }
    public double AccessesPerDay { get; init; }
    public double DaysSinceLastAccess { get; init; }
    public string Reason { get; init; } = string.Empty;
}

public sealed class AccessPatternSummary
{
    public string ResourceId { get; init; } = string.Empty;
    public long TotalAccesses { get; init; }
    public double AccessesPerDay { get; init; }
    public DateTime LastAccessTime { get; init; }
    public double ReadWriteRatio { get; init; }
}

public sealed class ComponentHealthData
{
    public string ComponentId { get; init; } = string.Empty;
    public DateTime LastUpdateTime { get; set; }
    public List<TimestampedMetrics> MetricsHistory { get; } = new();
    public double CpuAnomalyScore { get; set; }
    public double MemoryAnomalyScore { get; set; }
    public double DiskAnomalyScore { get; set; }
    public double ErrorRateAnomalyScore { get; set; }
}

public sealed class TimestampedMetrics
{
    public DateTime Timestamp { get; init; }
    public HealthMetrics Metrics { get; init; } = new();
}

public sealed class HealthMetrics
{
    public double CpuUsage { get; init; }
    public double MemoryUsage { get; init; }
    public double DiskUsage { get; init; }
    public double NetworkIn { get; init; }
    public double NetworkOut { get; init; }
    public double ErrorRate { get; init; }
    public double Latency { get; init; }
    public int ActiveConnections { get; init; }
}

public sealed class FailureEvent
{
    public string ComponentId { get; init; } = string.Empty;
    public string FailureType { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; }
    public List<HealthMetrics> PrecedingMetrics { get; init; } = new();
}

public sealed class FailurePrediction
{
    public string ComponentId { get; init; } = string.Empty;
    public double Probability { get; init; }
    public double Confidence { get; init; }
    public TimeSpan TimeToFailure { get; init; }
    public double AnomalyScore { get; init; }
    public List<string> CriticalWarnings { get; init; } = new();
    public string Reason { get; init; } = string.Empty;
}

/// <summary>
/// Exponential Moving Average calculator.
/// </summary>
internal sealed class ExponentialMovingAverage
{
    private readonly double _alpha;
    private double _value;
    private bool _initialized;

    public ExponentialMovingAverage(double alpha)
    {
        _alpha = alpha;
    }

    public double Update(double newValue)
    {
        if (!_initialized)
        {
            _value = newValue;
            _initialized = true;
        }
        else
        {
            _value = _alpha * newValue + (1 - _alpha) * _value;
        }
        return _value;
    }

    public double Value => _value;
}

#endregion
