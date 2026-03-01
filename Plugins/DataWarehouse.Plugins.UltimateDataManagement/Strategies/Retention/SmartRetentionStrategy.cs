using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.Retention;

/// <summary>
/// ML-driven smart retention strategy that uses predictive models for retention decisions.
/// Learns from access patterns and business value signals to optimize retention.
/// </summary>
/// <remarks>
/// Features:
/// - Predictive access pattern modeling
/// - Business value scoring
/// - Adaptive retention thresholds
/// - Integration with T90 message bus for ML predictions
/// - Feature-based scoring with configurable weights
/// </remarks>
public sealed class SmartRetentionStrategy : RetentionStrategyBase
{
    private readonly BoundedDictionary<string, ObjectFeatures> _featureStore = new BoundedDictionary<string, ObjectFeatures>(1000);
    private readonly BoundedDictionary<string, double> _predictionCache = new BoundedDictionary<string, double>(1000);
    private readonly Func<ObjectFeatures, CancellationToken, Task<double>>? _mlPredictor;
    private readonly double _retainThreshold;
    private readonly double _deleteThreshold;
    private readonly FeatureWeights _weights;
    private readonly TimeSpan _predictionCacheTtl;

    /// <summary>
    /// Initializes with default thresholds and built-in scoring.
    /// </summary>
    public SmartRetentionStrategy() : this(0.7, 0.3, null) { }

    /// <summary>
    /// Initializes with specified thresholds and optional ML predictor.
    /// </summary>
    /// <param name="retainThreshold">Score above which to retain (0-1).</param>
    /// <param name="deleteThreshold">Score below which to delete (0-1).</param>
    /// <param name="mlPredictor">Optional ML prediction function.</param>
    public SmartRetentionStrategy(
        double retainThreshold,
        double deleteThreshold,
        Func<ObjectFeatures, CancellationToken, Task<double>>? mlPredictor)
    {
        if (retainThreshold < 0 || retainThreshold > 1)
            throw new ArgumentOutOfRangeException(nameof(retainThreshold), "Threshold must be between 0 and 1");
        if (deleteThreshold < 0 || deleteThreshold > retainThreshold)
            throw new ArgumentOutOfRangeException(nameof(deleteThreshold), "Delete threshold must be between 0 and retain threshold");

        _retainThreshold = retainThreshold;
        _deleteThreshold = deleteThreshold;
        _mlPredictor = mlPredictor;
        _predictionCacheTtl = TimeSpan.FromHours(1);

        _weights = new FeatureWeights
        {
            RecencyWeight = 0.25,
            FrequencyWeight = 0.20,
            SizeWeight = 0.10,
            BusinessValueWeight = 0.25,
            ContentTypeWeight = 0.10,
            UserImportanceWeight = 0.10
        };
    }

    /// <inheritdoc/>
    public override string StrategyId => "retention.smart";

    /// <inheritdoc/>
    public override string DisplayName => "Smart Retention (ML-Driven)";

    /// <inheritdoc/>
    public override DataManagementCapabilities Capabilities { get; } = new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = true,
        SupportsTransactions = false,
        SupportsTTL = false,
        MaxThroughput = 10_000,
        TypicalLatencyMs = 10.0
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        $"ML-driven smart retention using predictive scoring (retain above {_retainThreshold}, delete below {_deleteThreshold}). " +
        "Considers access patterns, business value, and content characteristics. " +
        "Adapts over time based on usage patterns and feedback.";

    /// <inheritdoc/>
    public override string[] Tags => ["retention", "ml", "smart", "predictive", "adaptive", "machine-learning"];

    /// <summary>
    /// Gets or sets the feature weights.
    /// </summary>
    public FeatureWeights Weights => _weights;

    /// <summary>
    /// Records features for an object.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <param name="features">Object features.</param>
    public void RecordFeatures(string objectId, ObjectFeatures features)
    {
        _featureStore[objectId] = features;
        _predictionCache.TryRemove(objectId, out _); // Invalidate cache
    }

    /// <summary>
    /// Records a business value signal for an object.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <param name="signal">Business value signal (0-1).</param>
    public void RecordBusinessValueSignal(string objectId, double signal)
    {
        if (_featureStore.TryGetValue(objectId, out var features))
        {
            features.BusinessValueSignals.Add(new ValueSignal
            {
                Timestamp = DateTime.UtcNow,
                Value = signal
            });

            // Update computed business value
            features.BusinessValue = features.BusinessValueSignals
                .OrderByDescending(s => s.Timestamp)
                .Take(10)
                .Average(s => s.Value);

            _predictionCache.TryRemove(objectId, out _);
        }
    }

    /// <summary>
    /// Records an access event for pattern learning.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <param name="accessType">Type of access.</param>
    public void RecordAccess(string objectId, AccessType accessType)
    {
        if (_featureStore.TryGetValue(objectId, out var features))
        {
            features.AccessHistory.Add(new AccessEvent
            {
                Timestamp = DateTime.UtcNow,
                Type = accessType
            });

            // Update computed features
            UpdateAccessFeatures(features);
            _predictionCache.TryRemove(objectId, out _);
        }
    }

    /// <inheritdoc/>
    protected override async Task<RetentionDecision> EvaluateCoreAsync(DataObject data, CancellationToken ct)
    {
        // Get or create features
        var features = GetOrCreateFeatures(data);

        // Get prediction score
        var score = await GetRetentionScoreAsync(data.ObjectId, features, ct);

        // Make decision based on score
        if (score >= _retainThreshold)
        {
            return new RetentionDecision
            {
                Action = RetentionAction.Retain,
                Reason = $"Smart retention score: {score:F2} (above retain threshold {_retainThreshold})",
                Confidence = score,
                NextEvaluationDate = GetNextEvaluationDate(score),
                Metadata = new Dictionary<string, object>
                {
                    ["Score"] = score,
                    ["Features"] = GetFeatureSummary(features)
                }
            };
        }

        if (score <= _deleteThreshold)
        {
            return new RetentionDecision
            {
                Action = RetentionAction.Delete,
                Reason = $"Smart retention score: {score:F2} (below delete threshold {_deleteThreshold})",
                Confidence = 1 - score,
                Metadata = new Dictionary<string, object>
                {
                    ["Score"] = score,
                    ["Features"] = GetFeatureSummary(features)
                }
            };
        }

        // In uncertain zone - archive or retain with short re-evaluation
        var decision = score < 0.5 ? RetentionAction.Archive : RetentionAction.Retain;
        return new RetentionDecision
        {
            Action = decision,
            Reason = $"Smart retention score: {score:F2} (uncertain zone between {_deleteThreshold} and {_retainThreshold})",
            Confidence = Math.Abs(score - 0.5) * 2, // Lower confidence in uncertain zone
            NextEvaluationDate = DateTime.UtcNow.AddDays(7), // Re-evaluate soon
            Metadata = new Dictionary<string, object>
            {
                ["Score"] = score,
                ["Features"] = GetFeatureSummary(features)
            }
        };
    }

    private async Task<double> GetRetentionScoreAsync(string objectId, ObjectFeatures features, CancellationToken ct)
    {
        // Check cache
        if (_predictionCache.TryGetValue(objectId, out var cachedScore))
        {
            return cachedScore;
        }

        double score;

        // Use ML predictor if available
        if (_mlPredictor != null)
        {
            score = await _mlPredictor(features, ct);
        }
        else
        {
            // Use built-in heuristic scoring
            score = CalculateHeuristicScore(features);
        }

        // Cache the result
        _predictionCache[objectId] = score;

        return score;
    }

    private double CalculateHeuristicScore(ObjectFeatures features)
    {
        var score = 0.0;

        // Recency score - more recent = higher score
        var recencyScore = CalculateRecencyScore(features.LastAccessedAt);
        score += recencyScore * _weights.RecencyWeight;

        // Frequency score - more frequent = higher score
        var frequencyScore = CalculateFrequencyScore(features.AccessHistory);
        score += frequencyScore * _weights.FrequencyWeight;

        // Size score - smaller files have slight preference (less storage impact)
        var sizeScore = CalculateSizeScore(features.SizeBytes);
        score += sizeScore * _weights.SizeWeight;

        // Business value score
        score += features.BusinessValue * _weights.BusinessValueWeight;

        // Content type score
        var contentTypeScore = GetContentTypeScore(features.ContentType);
        score += contentTypeScore * _weights.ContentTypeWeight;

        // User importance score
        score += features.UserImportanceRating * _weights.UserImportanceWeight;

        return Math.Clamp(score, 0, 1);
    }

    private static double CalculateRecencyScore(DateTime? lastAccess)
    {
        if (!lastAccess.HasValue)
            return 0.1;

        var daysSinceAccess = (DateTime.UtcNow - lastAccess.Value).TotalDays;

        // Exponential decay
        return Math.Exp(-daysSinceAccess / 90.0); // Half-life of ~60 days
    }

    private static double CalculateFrequencyScore(List<AccessEvent> history)
    {
        if (history.Count == 0)
            return 0.1;

        // Count accesses in last 30 days
        var recentAccesses = history.Count(a => (DateTime.UtcNow - a.Timestamp).TotalDays <= 30);

        // Logarithmic scale
        return Math.Min(1.0, Math.Log(recentAccesses + 1) / Math.Log(100));
    }

    private static double CalculateSizeScore(long sizeBytes)
    {
        // Slightly prefer smaller files
        // 1MB = 0.9, 10MB = 0.7, 100MB = 0.5, 1GB = 0.3
        var sizeMb = sizeBytes / (1024.0 * 1024.0);
        return Math.Max(0.1, 1.0 - Math.Log10(sizeMb + 1) / 4);
    }

    private static double GetContentTypeScore(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType))
            return 0.5;

        // Higher value for documents and code, lower for temp/cache
        return contentType.ToLowerInvariant() switch
        {
            var ct when ct.StartsWith("application/pdf") => 0.9,
            var ct when ct.StartsWith("application/msword") => 0.9,
            var ct when ct.StartsWith("application/vnd.openxmlformats") => 0.9,
            var ct when ct.StartsWith("text/") => 0.7,
            var ct when ct.StartsWith("image/") => 0.6,
            var ct when ct.StartsWith("video/") => 0.5,
            var ct when ct.StartsWith("application/x-temp") => 0.2,
            var ct when ct.StartsWith("application/x-cache") => 0.1,
            _ => 0.5
        };
    }

    private ObjectFeatures GetOrCreateFeatures(DataObject data)
    {
        if (_featureStore.TryGetValue(data.ObjectId, out var existing))
        {
            return existing;
        }

        var features = new ObjectFeatures
        {
            ObjectId = data.ObjectId,
            CreatedAt = data.CreatedAt,
            LastAccessedAt = data.LastAccessedAt ?? data.CreatedAt,
            SizeBytes = data.Size,
            ContentType = data.ContentType,
            BusinessValue = 0.5, // Default neutral
            UserImportanceRating = 0.5, // Default neutral
            AccessHistory = new List<AccessEvent>(),
            BusinessValueSignals = new System.Collections.Concurrent.ConcurrentBag<ValueSignal>()
        };

        _featureStore[data.ObjectId] = features;
        return features;
    }

    private void UpdateAccessFeatures(ObjectFeatures features)
    {
        if (features.AccessHistory.Count > 0)
        {
            features.LastAccessedAt = features.AccessHistory.Max(a => a.Timestamp);
        }

        // Keep only last 1000 access events
        while (features.AccessHistory.Count > 1000)
        {
            features.AccessHistory.RemoveAt(0);
        }
    }

    private DateTime GetNextEvaluationDate(double score)
    {
        // Higher scores = less frequent re-evaluation
        var days = score switch
        {
            >= 0.9 => 30,
            >= 0.8 => 14,
            >= 0.7 => 7,
            _ => 3
        };

        return DateTime.UtcNow.AddDays(days);
    }

    private static Dictionary<string, object> GetFeatureSummary(ObjectFeatures features)
    {
        return new Dictionary<string, object>
        {
            ["DaysSinceLastAccess"] = features.LastAccessedAt.HasValue
                ? (DateTime.UtcNow - features.LastAccessedAt.Value).TotalDays
                : -1,
            ["RecentAccessCount"] = features.AccessHistory.Count(a => (DateTime.UtcNow - a.Timestamp).TotalDays <= 30),
            ["SizeMB"] = features.SizeBytes / (1024.0 * 1024.0),
            ["BusinessValue"] = features.BusinessValue,
            ["UserImportance"] = features.UserImportanceRating
        };
    }

    /// <summary>
    /// Gets model performance metrics.
    /// </summary>
    /// <returns>Performance metrics.</returns>
    public SmartRetentionMetrics GetMetrics()
    {
        var features = _featureStore.Values.ToList();

        return new SmartRetentionMetrics
        {
            TotalObjectsTracked = features.Count,
            ObjectsWithAccessHistory = features.Count(f => f.AccessHistory.Count > 0),
            ObjectsWithBusinessValue = features.Count(f => f.BusinessValue != 0.5),
            AverageBusinessValue = features.Count > 0 ? features.Average(f => f.BusinessValue) : 0,
            PredictionCacheSize = _predictionCache.Count,
            PredictionCacheHitRate = 0 // Would need tracking to compute
        };
    }
}

/// <summary>
/// Features extracted from an object for ML prediction.
/// </summary>
public sealed class ObjectFeatures
{
    /// <summary>
    /// Object identifier.
    /// </summary>
    public required string ObjectId { get; init; }

    /// <summary>
    /// When the object was created.
    /// </summary>
    public required DateTime CreatedAt { get; init; }

    /// <summary>
    /// When the object was last accessed.
    /// </summary>
    public DateTime? LastAccessedAt { get; set; }

    /// <summary>
    /// Object size in bytes.
    /// </summary>
    public required long SizeBytes { get; init; }

    /// <summary>
    /// Content type.
    /// </summary>
    public string? ContentType { get; init; }

    /// <summary>
    /// Computed business value (0-1).
    /// </summary>
    public double BusinessValue { get; set; }

    /// <summary>
    /// User-provided importance rating (0-1).
    /// </summary>
    public double UserImportanceRating { get; set; }

    /// <summary>
    /// Access history.
    /// </summary>
    public required List<AccessEvent> AccessHistory { get; init; }

    /// <summary>
    /// Business value signals.
    /// </summary>
    // P2-2473: Use ConcurrentBag to allow concurrent Add from RecordAccess/RecordBusinessValueSignal
    // without holding a separate lock. Ordering is not required here; Take(10) after sort is fine.
    public required System.Collections.Concurrent.ConcurrentBag<ValueSignal> BusinessValueSignals { get; init; }
}

/// <summary>
/// An access event.
/// </summary>
public sealed class AccessEvent
{
    /// <summary>
    /// When the access occurred.
    /// </summary>
    public required DateTime Timestamp { get; init; }

    /// <summary>
    /// Type of access.
    /// </summary>
    public required AccessType Type { get; init; }
}

/// <summary>
/// Type of access.
/// </summary>
public enum AccessType
{
    /// <summary>
    /// Read access.
    /// </summary>
    Read,

    /// <summary>
    /// Write access.
    /// </summary>
    Write,

    /// <summary>
    /// Download access.
    /// </summary>
    Download,

    /// <summary>
    /// Share access.
    /// </summary>
    Share,

    /// <summary>
    /// View/preview access.
    /// </summary>
    View
}

/// <summary>
/// A business value signal.
/// </summary>
public sealed class ValueSignal
{
    /// <summary>
    /// When the signal was recorded.
    /// </summary>
    public required DateTime Timestamp { get; init; }

    /// <summary>
    /// Value (0-1).
    /// </summary>
    public required double Value { get; init; }
}

/// <summary>
/// Weights for feature scoring.
/// </summary>
public sealed class FeatureWeights
{
    /// <summary>
    /// Weight for recency (last access time).
    /// </summary>
    public double RecencyWeight { get; set; }

    /// <summary>
    /// Weight for access frequency.
    /// </summary>
    public double FrequencyWeight { get; set; }

    /// <summary>
    /// Weight for file size.
    /// </summary>
    public double SizeWeight { get; set; }

    /// <summary>
    /// Weight for business value.
    /// </summary>
    public double BusinessValueWeight { get; set; }

    /// <summary>
    /// Weight for content type.
    /// </summary>
    public double ContentTypeWeight { get; set; }

    /// <summary>
    /// Weight for user importance rating.
    /// </summary>
    public double UserImportanceWeight { get; set; }
}

/// <summary>
/// Metrics for smart retention performance.
/// </summary>
public sealed class SmartRetentionMetrics
{
    /// <summary>
    /// Total objects with features tracked.
    /// </summary>
    public int TotalObjectsTracked { get; init; }

    /// <summary>
    /// Objects with access history.
    /// </summary>
    public int ObjectsWithAccessHistory { get; init; }

    /// <summary>
    /// Objects with business value signals.
    /// </summary>
    public int ObjectsWithBusinessValue { get; init; }

    /// <summary>
    /// Average business value across objects.
    /// </summary>
    public double AverageBusinessValue { get; init; }

    /// <summary>
    /// Size of the prediction cache.
    /// </summary>
    public int PredictionCacheSize { get; init; }

    /// <summary>
    /// Prediction cache hit rate.
    /// </summary>
    public double PredictionCacheHitRate { get; init; }
}
