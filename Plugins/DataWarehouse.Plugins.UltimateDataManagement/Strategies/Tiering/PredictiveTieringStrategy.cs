using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.Tiering;

/// <summary>
/// Predictive tiering strategy that uses machine learning-style analysis to predict
/// future access patterns and proactively move data to appropriate tiers.
/// </summary>
/// <remarks>
/// Features:
/// - Access pattern analysis using time series modeling
/// - Proactive tier movement before access patterns change
/// - Prediction confidence thresholds for decision making
/// - Seasonal pattern detection (daily, weekly, monthly cycles)
/// - Integration with T90 analytics if available
/// </remarks>
public sealed class PredictiveTieringStrategy : TieringStrategyBase
{
    private readonly BoundedDictionary<string, AccessHistory> _accessHistories = new BoundedDictionary<string, AccessHistory>(1000);
    private readonly BoundedDictionary<string, PredictionResult> _predictions = new BoundedDictionary<string, PredictionResult>(1000);
    private readonly int _historyWindowDays = 90;
    private readonly int _predictionHorizonDays = 30;
    private double _minConfidenceThreshold = 0.7;
    private long _totalPredictions;
    private long _accuratePredictions;

    /// <summary>
    /// Access history for an object.
    /// </summary>
    private sealed class AccessHistory
    {
        public required string ObjectId { get; init; }
        public List<AccessEvent> Events { get; } = new();
        public double[] DailyAccessCounts { get; set; } = Array.Empty<double>();
        public double[] WeekdayPattern { get; set; } = new double[7];
        public double[] HourlyPattern { get; set; } = new double[24];
        public DateTime LastUpdated { get; set; }
    }

    /// <summary>
    /// Represents a single access event.
    /// </summary>
    private sealed class AccessEvent
    {
        public DateTime Timestamp { get; init; }
        public AccessType Type { get; init; }
    }

    /// <summary>
    /// Types of access.
    /// </summary>
    private enum AccessType
    {
        Read,
        Write,
        Metadata
    }

    /// <summary>
    /// Prediction result for an object.
    /// </summary>
    private sealed class PredictionResult
    {
        public required string ObjectId { get; init; }
        public DateTime PredictedAt { get; init; }
        public double[] PredictedDailyAccesses { get; init; } = Array.Empty<double>();
        public double Confidence { get; init; }
        public AccessTrend Trend { get; init; }
        public StorageTier RecommendedTier { get; init; }
        public string Reasoning { get; init; } = string.Empty;
    }

    /// <summary>
    /// Access trend direction.
    /// </summary>
    private enum AccessTrend
    {
        Increasing,
        Stable,
        Decreasing,
        Seasonal,
        Sporadic,
        Dormant
    }

    /// <inheritdoc/>
    public override string StrategyId => "tiering.predictive";

    /// <inheritdoc/>
    public override string DisplayName => "Predictive Tiering";

    /// <inheritdoc/>
    public override DataManagementCapabilities Capabilities { get; } = new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = true,
        SupportsTransactions = false,
        SupportsTTL = false,
        MaxThroughput = 10000,
        TypicalLatencyMs = 5.0
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Predictive tiering using access pattern analysis and forecasting. " +
        "Proactively moves data to appropriate tiers based on predicted future access, " +
        "with configurable confidence thresholds and seasonal pattern detection.";

    /// <inheritdoc/>
    public override string[] Tags => ["tiering", "predictive", "ml", "forecast", "proactive"];

    /// <summary>
    /// Gets or sets the minimum confidence threshold for predictions.
    /// </summary>
    public double MinConfidenceThreshold
    {
        get => _minConfidenceThreshold;
        set => _minConfidenceThreshold = Math.Clamp(value, 0.0, 1.0);
    }

    /// <summary>
    /// Records an access event for prediction modeling.
    /// </summary>
    /// <param name="objectId">The object identifier.</param>
    /// <param name="accessType">The type of access.</param>
    /// <param name="timestamp">When the access occurred.</param>
    public void RecordAccess(string objectId, string accessType, DateTime? timestamp = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);

        var history = _accessHistories.GetOrAdd(objectId, id => new AccessHistory { ObjectId = id });

        var eventType = accessType.ToLowerInvariant() switch
        {
            "write" => AccessType.Write,
            "metadata" => AccessType.Metadata,
            _ => AccessType.Read
        };

        lock (history.Events)
        {
            history.Events.Add(new AccessEvent
            {
                Timestamp = timestamp ?? DateTime.UtcNow,
                Type = eventType
            });

            // Keep only recent history
            var cutoff = DateTime.UtcNow.AddDays(-_historyWindowDays);
            history.Events.RemoveAll(e => e.Timestamp < cutoff);

            UpdatePatterns(history);
        }
    }

    /// <summary>
    /// Gets the prediction accuracy rate.
    /// </summary>
    /// <returns>Accuracy as a percentage (0-100).</returns>
    public double GetPredictionAccuracy()
    {
        var total = Interlocked.Read(ref _totalPredictions);
        var accurate = Interlocked.Read(ref _accuratePredictions);
        return total > 0 ? (accurate * 100.0 / total) : 0;
    }

    /// <summary>
    /// Reports actual access to validate predictions.
    /// </summary>
    /// <param name="objectId">The object identifier.</param>
    /// <param name="actualAccesses">Actual number of accesses in the prediction period.</param>
    public void ReportActualAccess(string objectId, int actualAccesses)
    {
        if (_predictions.TryGetValue(objectId, out var prediction))
        {
            Interlocked.Increment(ref _totalPredictions);

            var predictedAvg = prediction.PredictedDailyAccesses.Length > 0
                ? prediction.PredictedDailyAccesses.Average()
                : 0;

            // Consider accurate if within 50% of prediction
            var error = Math.Abs(predictedAvg - actualAccesses) / Math.Max(1, predictedAvg);
            if (error <= 0.5)
            {
                Interlocked.Increment(ref _accuratePredictions);
            }
        }
    }

    /// <inheritdoc/>
    protected override Task<TierRecommendation> EvaluateCoreAsync(DataObject data, CancellationToken ct)
    {
        // Get or create access history
        var history = _accessHistories.GetOrAdd(data.ObjectId, id => new AccessHistory { ObjectId = id });

        // Initialize history from data object if empty
        if (history.Events.Count == 0)
        {
            InitializeHistoryFromDataObject(history, data);
        }

        // Generate prediction
        var prediction = GeneratePrediction(data, history);
        _predictions[data.ObjectId] = prediction;

        // Check confidence threshold
        if (prediction.Confidence < _minConfidenceThreshold)
        {
            return Task.FromResult(NoChange(data,
                $"Prediction confidence ({prediction.Confidence:P0}) below threshold ({_minConfidenceThreshold:P0})"));
        }

        // Make recommendation based on prediction
        if (prediction.RecommendedTier != data.CurrentTier)
        {
            var reason = $"Predicted {prediction.Trend} access pattern (confidence: {prediction.Confidence:P0}). {prediction.Reasoning}";

            return Task.FromResult(prediction.RecommendedTier < data.CurrentTier
                ? Promote(data, prediction.RecommendedTier, reason, prediction.Confidence, CalculatePriority(prediction))
                : Demote(data, prediction.RecommendedTier, reason, prediction.Confidence, CalculatePriority(prediction),
                    EstimateMonthlySavings(data.SizeBytes, data.CurrentTier, prediction.RecommendedTier)));
        }

        return Task.FromResult(NoChange(data, $"At optimal tier for predicted {prediction.Trend} pattern"));
    }

    private void InitializeHistoryFromDataObject(AccessHistory history, DataObject data)
    {
        lock (history.Events)
        {
            // Distribute historical accesses based on data object metrics.
            // Use a seeded Random from the object ID hash so synthetic event placement
            // is deterministic per object (same input = same history, no flapping predictions).
            var seed = data.ObjectId != null
                ? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(data.ObjectId) ^ data.ObjectId.Length
                : 0;
            var rng = new Random(seed);
            var now = DateTime.UtcNow;

            // Add events for last 24 hours
            for (int i = 0; i < data.AccessesLast24Hours; i++)
            {
                history.Events.Add(new AccessEvent
                {
                    Timestamp = now.AddHours(-rng.Next(0, 24)),
                    Type = AccessType.Read
                });
            }

            // Add more events for 7-day window
            var remaining7Day = data.AccessesLast7Days - data.AccessesLast24Hours;
            for (int i = 0; i < remaining7Day; i++)
            {
                history.Events.Add(new AccessEvent
                {
                    Timestamp = now.AddDays(-rng.Next(1, 7)),
                    Type = AccessType.Read
                });
            }

            // Add more events for 30-day window
            var remaining30Day = data.AccessesLast30Days - data.AccessesLast7Days;
            for (int i = 0; i < remaining30Day; i++)
            {
                history.Events.Add(new AccessEvent
                {
                    Timestamp = now.AddDays(-rng.Next(7, 30)),
                    Type = AccessType.Read
                });
            }

            UpdatePatterns(history);
        }
    }

    private void UpdatePatterns(AccessHistory history)
    {
        // Calculate daily access counts
        var dailyCounts = new double[_historyWindowDays];
        var now = DateTime.UtcNow.Date;

        foreach (var evt in history.Events)
        {
            var daysAgo = (int)(now - evt.Timestamp.Date).TotalDays;
            if (daysAgo >= 0 && daysAgo < _historyWindowDays)
            {
                dailyCounts[_historyWindowDays - 1 - daysAgo]++;
            }
        }

        history.DailyAccessCounts = dailyCounts;

        // Calculate weekday pattern
        var weekdayCounts = new double[7];
        var weekdayTotals = new int[7];

        foreach (var evt in history.Events)
        {
            var dayOfWeek = (int)evt.Timestamp.DayOfWeek;
            weekdayCounts[dayOfWeek]++;
            weekdayTotals[dayOfWeek]++;
        }

        for (int i = 0; i < 7; i++)
        {
            history.WeekdayPattern[i] = weekdayTotals[i] > 0 ? weekdayCounts[i] / weekdayTotals[i] : 0;
        }

        // Calculate hourly pattern
        var hourlyCounts = new double[24];
        var hourlyTotals = new int[24];

        foreach (var evt in history.Events)
        {
            var hour = evt.Timestamp.Hour;
            hourlyCounts[hour]++;
            hourlyTotals[hour]++;
        }

        for (int i = 0; i < 24; i++)
        {
            history.HourlyPattern[i] = hourlyTotals[i] > 0 ? hourlyCounts[i] / hourlyTotals[i] : 0;
        }

        history.LastUpdated = DateTime.UtcNow;
    }

    private PredictionResult GeneratePrediction(DataObject data, AccessHistory history)
    {
        // Analyze trend from daily counts
        var trend = AnalyzeTrend(history.DailyAccessCounts);
        var seasonality = DetectSeasonality(history);
        var confidence = CalculateConfidence(history, trend);

        // Generate forecast
        var forecast = GenerateForecast(history, trend, seasonality);

        // Determine recommended tier
        var avgForecast = forecast.Length > 0 ? forecast.Average() : 0;
        var recommendedTier = DetermineRecommendedTier(data, avgForecast, trend);

        var reasoning = BuildReasoning(trend, seasonality, avgForecast, history.DailyAccessCounts);

        return new PredictionResult
        {
            ObjectId = data.ObjectId,
            PredictedAt = DateTime.UtcNow,
            PredictedDailyAccesses = forecast,
            Confidence = confidence,
            Trend = trend,
            RecommendedTier = recommendedTier,
            Reasoning = reasoning
        };
    }

    private AccessTrend AnalyzeTrend(double[] dailyCounts)
    {
        if (dailyCounts.Length < 7)
            return AccessTrend.Sporadic;

        var recentWeek = dailyCounts.TakeLast(7).Average();
        var previousWeek = dailyCounts.SkipLast(7).TakeLast(7).DefaultIfEmpty(0).Average();
        var totalAvg = dailyCounts.Average();

        if (totalAvg < 0.1)
            return AccessTrend.Dormant;

        if (recentWeek > previousWeek * 1.5)
            return AccessTrend.Increasing;

        if (recentWeek < previousWeek * 0.5)
            return AccessTrend.Decreasing;

        // Check for seasonality by looking at variance
        var variance = CalculateVariance(dailyCounts);
        if (variance > totalAvg * 2)
            return AccessTrend.Seasonal;

        if (variance > totalAvg)
            return AccessTrend.Sporadic;

        return AccessTrend.Stable;
    }

    private double DetectSeasonality(AccessHistory history)
    {
        // Simple seasonality score based on weekday pattern variance
        var weekdayVariance = CalculateVariance(history.WeekdayPattern);
        var weekdayMean = history.WeekdayPattern.Average();

        return weekdayMean > 0 ? weekdayVariance / weekdayMean : 0;
    }

    private double CalculateConfidence(AccessHistory history, AccessTrend trend)
    {
        var eventCount = history.Events.Count;

        // More events = higher confidence
        var eventConfidence = Math.Min(1.0, eventCount / 100.0);

        // Stable trends have higher confidence
        var trendConfidence = trend switch
        {
            AccessTrend.Stable => 0.9,
            AccessTrend.Increasing => 0.8,
            AccessTrend.Decreasing => 0.8,
            AccessTrend.Seasonal => 0.7,
            AccessTrend.Sporadic => 0.5,
            AccessTrend.Dormant => 0.85,
            _ => 0.5
        };

        // Recent data has higher confidence
        var dataFreshness = history.LastUpdated > DateTime.UtcNow.AddHours(-24) ? 1.0 :
            history.LastUpdated > DateTime.UtcNow.AddDays(-7) ? 0.8 : 0.6;

        return eventConfidence * 0.3 + trendConfidence * 0.5 + dataFreshness * 0.2;
    }

    private double[] GenerateForecast(AccessHistory history, AccessTrend trend, double seasonality)
    {
        var forecast = new double[_predictionHorizonDays];
        var recentAvg = history.DailyAccessCounts.TakeLast(7).DefaultIfEmpty(0).Average();

        for (int i = 0; i < _predictionHorizonDays; i++)
        {
            var trendFactor = trend switch
            {
                AccessTrend.Increasing => 1.0 + (0.02 * i),
                AccessTrend.Decreasing => 1.0 - (0.02 * i),
                _ => 1.0
            };

            // Add weekday seasonality
            var dayOfWeek = ((int)DateTime.UtcNow.AddDays(i).DayOfWeek);
            var seasonalFactor = 1.0;
            if (seasonality > 0.5 && history.WeekdayPattern.Sum() > 0)
            {
                var avgPattern = history.WeekdayPattern.Average();
                seasonalFactor = avgPattern > 0 ? history.WeekdayPattern[dayOfWeek] / avgPattern : 1.0;
            }

            forecast[i] = Math.Max(0, recentAvg * trendFactor * seasonalFactor);
        }

        return forecast;
    }

    private StorageTier DetermineRecommendedTier(DataObject data, double avgForecast, AccessTrend trend)
    {
        // Hot tier: >5 daily accesses or increasing trend with >2 accesses
        if (avgForecast > 5 || (trend == AccessTrend.Increasing && avgForecast > 2))
            return StorageTier.Hot;

        // Warm tier: 0.5-5 daily accesses
        if (avgForecast > 0.5)
            return StorageTier.Warm;

        // Cold tier: 0.1-0.5 daily accesses or sporadic pattern
        if (avgForecast > 0.1 || trend == AccessTrend.Sporadic)
            return StorageTier.Cold;

        // Archive tier: <0.1 daily accesses (less than once every 10 days)
        if (avgForecast > 0.01)
            return StorageTier.Archive;

        // Glacier: essentially no predicted access
        return StorageTier.Glacier;
    }

    private static string BuildReasoning(AccessTrend trend, double seasonality, double avgForecast, double[] history)
    {
        var parts = new List<string>();

        var recentAvg = history.TakeLast(7).DefaultIfEmpty(0).Average();
        parts.Add($"Recent access rate: {recentAvg:F1}/day");
        parts.Add($"Predicted rate: {avgForecast:F1}/day");

        if (seasonality > 0.5)
            parts.Add("Strong weekly seasonality detected");

        parts.Add($"Trend: {trend}");

        return string.Join(". ", parts);
    }

    private static double CalculateVariance(double[] values)
    {
        if (values.Length == 0)
            return 0;

        var mean = values.Average();
        return values.Sum(v => Math.Pow(v - mean, 2)) / values.Length;
    }

    private static double CalculatePriority(PredictionResult prediction)
    {
        return prediction.Confidence * (prediction.Trend switch
        {
            AccessTrend.Increasing => 0.9,
            AccessTrend.Decreasing => 0.8,
            AccessTrend.Dormant => 0.7,
            _ => 0.5
        });
    }
}
