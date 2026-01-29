// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using DataWarehouse.SDK.AI;
using System.Collections.Concurrent;
using IExtendedAIProvider = DataWarehouse.Plugins.AIAgents.IExtendedAIProvider;

namespace DataWarehouse.Plugins.AIAgents.Capabilities.AIOps;

/// <summary>
/// Prediction models wrapper providing access pattern prediction and failure prediction.
/// </summary>
public sealed class PredictionModels
{
    private readonly AccessPatternPredictor _accessPredictor;
    private readonly FailurePredictor _failurePredictor;

    /// <summary>
    /// Creates a new prediction models instance.
    /// </summary>
    /// <param name="aiProvider">Optional AI provider (not used currently, for future enhancement).</param>
    /// <param name="config">Optional configuration.</param>
    public PredictionModels(IExtendedAIProvider? aiProvider = null, PredictionModelsConfig? config = null)
    {
        var cfg = config ?? new PredictionModelsConfig();
        _accessPredictor = new AccessPatternPredictor(cfg.HistorySize);
        _failurePredictor = new FailurePredictor();
    }

    /// <summary>
    /// Gets the access pattern predictor.
    /// </summary>
    public AccessPatternPredictor AccessPredictor => _accessPredictor;

    /// <summary>
    /// Gets the failure predictor.
    /// </summary>
    public FailurePredictor FailurePredictor => _failurePredictor;

    /// <summary>
    /// Gets prediction statistics.
    /// </summary>
    public PredictionStatistics GetStatistics()
    {
        var atRiskComponents = _failurePredictor.GetAtRiskComponents(0.3).ToList();
        var predictions = atRiskComponents.ToList();

        return new PredictionStatistics
        {
            ActivePredictions = predictions.Count,
            AverageConfidence = predictions.Any() ? predictions.Average(p => p.Confidence) : 0,
            ComponentsAtRisk = atRiskComponents.Count
        };
    }
}

/// <summary>
/// Configuration for prediction models.
/// </summary>
public sealed class PredictionModelsConfig
{
    /// <summary>Gets or sets the history size.</summary>
    public int HistorySize { get; set; } = 1000;

    /// <summary>Gets or sets the minimum data points for prediction.</summary>
    public int MinDataPointsForPrediction { get; set; } = 30;

    /// <summary>Gets or sets whether to enable autonomous prediction.</summary>
    public bool EnableAutonomousPrediction { get; set; } = true;
}

/// <summary>
/// Prediction models statistics.
/// </summary>
public sealed class PredictionStatistics
{
    /// <summary>Gets or sets the number of active predictions.</summary>
    public int ActivePredictions { get; init; }

    /// <summary>Gets or sets the average confidence level.</summary>
    public double AverageConfidence { get; init; }

    /// <summary>Gets or sets the number of components at risk.</summary>
    public int ComponentsAtRisk { get; init; }
}

/// <summary>
/// Access pattern predictor for tiering optimization.
/// Uses statistical methods with AI-enhanced analysis for data placement predictions.
/// </summary>
/// <remarks>
/// <para>
/// The access pattern predictor provides:
/// - Access event recording and pattern learning
/// - Time-of-day and day-of-week pattern analysis
/// - Recency-based access probability prediction
/// - Storage tier recommendations based on access patterns
/// </para>
/// </remarks>
public sealed class AccessPatternPredictor
{
    private readonly ConcurrentDictionary<string, AccessPatternData> _patterns;
    private readonly ExponentialMovingAverage _ema;
    private readonly int _historySize;

    /// <summary>
    /// Creates a new access pattern predictor.
    /// </summary>
    /// <param name="historySize">The maximum history size to track.</param>
    public AccessPatternPredictor(int historySize = 1000)
    {
        _patterns = new ConcurrentDictionary<string, AccessPatternData>();
        _ema = new ExponentialMovingAverage(0.1);
        _historySize = historySize;
    }

    /// <summary>
    /// Records an access event for pattern learning.
    /// </summary>
    /// <param name="resourceId">The resource identifier.</param>
    /// <param name="eventType">The type of access event.</param>
    /// <param name="sizeBytes">The size of the access in bytes.</param>
    /// <param name="metadata">Optional metadata about the access.</param>
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
    /// <param name="resourceId">The resource identifier.</param>
    /// <param name="predictionWindow">The time window to predict.</param>
    /// <returns>The access prediction.</returns>
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
    /// <param name="resourceId">The resource identifier.</param>
    /// <returns>The tier recommendation.</returns>
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
    /// <returns>Collection of access pattern summaries.</returns>
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

    private static double CalculateStdDev(List<double> values)
    {
        if (values.Count < 2) return 0;
        var mean = values.Average();
        var sumSquares = values.Sum(v => Math.Pow(v - mean, 2));
        return Math.Sqrt(sumSquares / values.Count);
    }

    private static string GeneratePredictionReason(AccessPatternData pattern, double probability)
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
/// Failure predictor using statistical analysis and pattern recognition.
/// </summary>
/// <remarks>
/// <para>
/// The failure predictor provides:
/// - Component health metric recording
/// - Anomaly score computation per metric
/// - Failure probability prediction
/// - Time-to-failure estimation
/// - At-risk component identification
/// </para>
/// </remarks>
public sealed class FailurePredictor
{
    private readonly ConcurrentDictionary<string, ComponentHealthData> _healthData;
    private readonly List<FailureEvent> _historicalFailures;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private const int MaxHistoricalFailures = 10000;

    /// <summary>
    /// Creates a new failure predictor.
    /// </summary>
    public FailurePredictor()
    {
        _healthData = new ConcurrentDictionary<string, ComponentHealthData>();
        _historicalFailures = new List<FailureEvent>();
    }

    /// <summary>
    /// Records health metrics for a component.
    /// </summary>
    /// <param name="componentId">The component identifier.</param>
    /// <param name="metrics">The health metrics.</param>
    public void RecordMetrics(string componentId, HealthMetrics metrics)
    {
        var health = _healthData.GetOrAdd(componentId, _ => new ComponentHealthData { ComponentId = componentId });

        lock (health)
        {
            health.LastUpdateTime = DateTime.UtcNow;
            health.MetricsHistory.Add(new TimestampedHealthMetrics
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
    /// <param name="componentId">The component identifier.</param>
    /// <param name="failureType">The type of failure.</param>
    /// <param name="description">Description of the failure.</param>
    /// <returns>Task representing the async operation.</returns>
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
    /// <param name="componentId">The component identifier.</param>
    /// <returns>The failure prediction.</returns>
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
    /// <param name="minProbability">Minimum probability threshold.</param>
    /// <returns>Collection of components at risk.</returns>
    public IEnumerable<FailurePrediction> GetAtRiskComponents(double minProbability = 0.3)
    {
        return _healthData.Keys
            .Select(PredictFailure)
            .Where(p => p.Probability >= minProbability)
            .OrderByDescending(p => p.Probability);
    }

    private static double CalculateAnomalyScore(List<double> history, double currentValue)
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

    private static double CalculateTrend(List<double> values)
    {
        if (values.Count < 2) return 0;

        var n = values.Count;
        var sumX = Enumerable.Range(0, n).Sum();
        var sumY = values.Sum();
        var sumXY = Enumerable.Range(0, n).Select(i => i * values[i]).Sum();
        var sumX2 = Enumerable.Range(0, n).Select(i => i * i).Sum();

        var denominator = n * sumX2 - sumX * sumX;
        return denominator != 0 ? (n * sumXY - sumX * sumY) / denominator : 0;
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

/// <summary>
/// Exponential Moving Average calculator.
/// </summary>
internal sealed class ExponentialMovingAverage
{
    private readonly double _alpha;
    private double _value;
    private bool _initialized;

    /// <summary>
    /// Creates a new EMA calculator.
    /// </summary>
    /// <param name="alpha">The smoothing factor (0-1).</param>
    public ExponentialMovingAverage(double alpha)
    {
        _alpha = alpha;
    }

    /// <summary>
    /// Updates the EMA with a new value.
    /// </summary>
    /// <param name="newValue">The new value.</param>
    /// <returns>The updated EMA value.</returns>
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

    /// <summary>
    /// Gets the current EMA value.
    /// </summary>
    public double Value => _value;
}

#region Supporting Types

/// <summary>
/// Type of access event.
/// </summary>
public enum AccessEventType
{
    /// <summary>Read operation.</summary>
    Read,
    /// <summary>Write operation.</summary>
    Write,
    /// <summary>Delete operation.</summary>
    Delete,
    /// <summary>List operation.</summary>
    List
}

/// <summary>
/// Storage tier classification.
/// </summary>
public enum StorageTier
{
    /// <summary>Frequently accessed data.</summary>
    Hot,
    /// <summary>Moderately accessed data.</summary>
    Warm,
    /// <summary>Standard access patterns.</summary>
    Standard,
    /// <summary>Infrequently accessed data.</summary>
    Cool,
    /// <summary>Rarely accessed data.</summary>
    Archive
}

/// <summary>
/// Source of a recommendation.
/// </summary>
public enum RecommendationSource
{
    /// <summary>From ML model only.</summary>
    MLModel,
    /// <summary>Rule-based recommendation.</summary>
    RuleBased,
    /// <summary>Rule-based recommendation (alias).</summary>
    Rule = RuleBased,
    /// <summary>AI-generated recommendation.</summary>
    AI,
    /// <summary>Enhanced with AI analysis (alias).</summary>
    AIEnhanced = AI,
    /// <summary>Historical pattern-based recommendation.</summary>
    Historical,
    /// <summary>Manual override.</summary>
    Manual
}

/// <summary>
/// Access pattern data for a resource.
/// </summary>
public sealed class AccessPatternData
{
    /// <summary>Gets or sets the resource identifier.</summary>
    public string ResourceId { get; init; } = string.Empty;

    /// <summary>Gets or sets the first access time.</summary>
    public DateTime FirstAccessTime { get; set; }

    /// <summary>Gets or sets the last access time.</summary>
    public DateTime LastAccessTime { get; set; }

    /// <summary>Gets or sets the total access count.</summary>
    public long TotalAccesses { get; set; }

    /// <summary>Gets or sets the total bytes accessed.</summary>
    public long TotalBytesAccessed { get; set; }

    /// <summary>Gets or sets the read count.</summary>
    public long ReadCount { get; set; }

    /// <summary>Gets or sets the write count.</summary>
    public long WriteCount { get; set; }

    /// <summary>Gets or sets the delete count.</summary>
    public long DeleteCount { get; set; }

    /// <summary>Gets the hourly distribution.</summary>
    public Dictionary<int, long> HourlyDistribution { get; } = Enumerable.Range(0, 24).ToDictionary(h => h, _ => 0L);

    /// <summary>Gets the daily distribution.</summary>
    public Dictionary<int, long> DailyDistribution { get; } = Enumerable.Range(0, 7).ToDictionary(d => d, _ => 0L);

    /// <summary>Gets the access history.</summary>
    public List<AccessRecord> AccessHistory { get; } = new();

    /// <summary>Gets or sets the average access interval.</summary>
    public double AverageAccessInterval { get; set; }

    /// <summary>Gets or sets the access interval standard deviation.</summary>
    public double AccessIntervalStdDev { get; set; }

    /// <summary>Gets or sets the EMA access rate.</summary>
    public double EmaAccessRate { get; set; }
}

/// <summary>
/// Record of an access event.
/// </summary>
public sealed class AccessRecord
{
    /// <summary>Gets or sets the timestamp.</summary>
    public DateTime Timestamp { get; init; }

    /// <summary>Gets or sets the event type.</summary>
    public AccessEventType EventType { get; init; }

    /// <summary>Gets or sets the size in bytes.</summary>
    public long SizeBytes { get; init; }
}

/// <summary>
/// Access prediction result.
/// </summary>
public sealed class AccessPrediction
{
    /// <summary>Gets or sets the resource identifier.</summary>
    public string ResourceId { get; init; } = string.Empty;

    /// <summary>Gets or sets the access probability.</summary>
    public double Probability { get; init; }

    /// <summary>Gets or sets the confidence level.</summary>
    public double Confidence { get; init; }

    /// <summary>Gets or sets the predicted accesses per day.</summary>
    public double PredictedAccessesPerDay { get; init; }

    /// <summary>Gets or sets the hourly factor.</summary>
    public double HourlyFactor { get; init; }

    /// <summary>Gets or sets the daily factor.</summary>
    public double DailyFactor { get; init; }

    /// <summary>Gets or sets the recency factor.</summary>
    public double RecencyFactor { get; init; }

    /// <summary>Gets or sets the reason.</summary>
    public string Reason { get; init; } = string.Empty;
}

/// <summary>
/// Tier recommendation result.
/// </summary>
public sealed class TierRecommendation
{
    /// <summary>Gets or sets the resource identifier.</summary>
    public string ResourceId { get; init; } = string.Empty;

    /// <summary>Gets or sets the recommended tier.</summary>
    public StorageTier RecommendedTier { get; init; }

    /// <summary>Gets or sets the confidence level.</summary>
    public double Confidence { get; init; }

    /// <summary>Gets or sets the accesses per day.</summary>
    public double AccessesPerDay { get; init; }

    /// <summary>Gets or sets days since last access.</summary>
    public double DaysSinceLastAccess { get; init; }

    /// <summary>Gets or sets the reason.</summary>
    public string Reason { get; init; } = string.Empty;
}

/// <summary>
/// Access pattern summary.
/// </summary>
public sealed class AccessPatternSummary
{
    /// <summary>Gets or sets the resource identifier.</summary>
    public string ResourceId { get; init; } = string.Empty;

    /// <summary>Gets or sets the total accesses.</summary>
    public long TotalAccesses { get; init; }

    /// <summary>Gets or sets accesses per day.</summary>
    public double AccessesPerDay { get; init; }

    /// <summary>Gets or sets the last access time.</summary>
    public DateTime LastAccessTime { get; init; }

    /// <summary>Gets or sets the read/write ratio.</summary>
    public double ReadWriteRatio { get; init; }
}

/// <summary>
/// Component health data.
/// </summary>
public sealed class ComponentHealthData
{
    /// <summary>Gets or sets the component identifier.</summary>
    public string ComponentId { get; init; } = string.Empty;

    /// <summary>Gets or sets the last update time.</summary>
    public DateTime LastUpdateTime { get; set; }

    /// <summary>Gets the metrics history.</summary>
    public List<TimestampedHealthMetrics> MetricsHistory { get; } = new();

    /// <summary>Gets or sets the CPU anomaly score.</summary>
    public double CpuAnomalyScore { get; set; }

    /// <summary>Gets or sets the memory anomaly score.</summary>
    public double MemoryAnomalyScore { get; set; }

    /// <summary>Gets or sets the disk anomaly score.</summary>
    public double DiskAnomalyScore { get; set; }

    /// <summary>Gets or sets the error rate anomaly score.</summary>
    public double ErrorRateAnomalyScore { get; set; }
}

/// <summary>
/// Timestamped health metrics.
/// </summary>
public sealed class TimestampedHealthMetrics
{
    /// <summary>Gets or sets the timestamp.</summary>
    public DateTime Timestamp { get; init; }

    /// <summary>Gets or sets the metrics.</summary>
    public HealthMetrics Metrics { get; init; } = new();
}

/// <summary>
/// Health metrics for a component.
/// </summary>
public sealed class HealthMetrics
{
    /// <summary>Gets or sets the CPU usage percentage.</summary>
    public double CpuUsage { get; init; }

    /// <summary>Gets or sets the memory usage percentage.</summary>
    public double MemoryUsage { get; init; }

    /// <summary>Gets or sets the disk usage percentage.</summary>
    public double DiskUsage { get; init; }

    /// <summary>Gets or sets the network input.</summary>
    public double NetworkIn { get; init; }

    /// <summary>Gets or sets the network output.</summary>
    public double NetworkOut { get; init; }

    /// <summary>Gets or sets the error rate.</summary>
    public double ErrorRate { get; init; }

    /// <summary>Gets or sets the latency.</summary>
    public double Latency { get; init; }

    /// <summary>Gets or sets the active connections.</summary>
    public int ActiveConnections { get; init; }
}

/// <summary>
/// Failure event record.
/// </summary>
public sealed class FailureEvent
{
    /// <summary>Gets or sets the component identifier.</summary>
    public string ComponentId { get; init; } = string.Empty;

    /// <summary>Gets or sets the failure type.</summary>
    public string FailureType { get; init; } = string.Empty;

    /// <summary>Gets or sets the description.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Gets or sets the timestamp.</summary>
    public DateTime Timestamp { get; init; }

    /// <summary>Gets or sets the preceding metrics.</summary>
    public List<HealthMetrics> PrecedingMetrics { get; init; } = new();
}

/// <summary>
/// Failure prediction result.
/// </summary>
public sealed class FailurePrediction
{
    /// <summary>Gets or sets the component identifier.</summary>
    public string ComponentId { get; init; } = string.Empty;

    /// <summary>Gets or sets the failure probability.</summary>
    public double Probability { get; init; }

    /// <summary>Gets or sets the confidence level.</summary>
    public double Confidence { get; init; }

    /// <summary>Gets or sets the estimated time to failure.</summary>
    public TimeSpan TimeToFailure { get; init; }

    /// <summary>Gets or sets the anomaly score.</summary>
    public double AnomalyScore { get; init; }

    /// <summary>Gets or sets critical warnings.</summary>
    public List<string> CriticalWarnings { get; init; } = new();

    /// <summary>Gets or sets the reason.</summary>
    public string Reason { get; init; } = string.Empty;
}

#endregion
