using System.Collections.Concurrent;
using System.Diagnostics;
using DataWarehouse.SDK.Contracts.IntelligenceAware;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.AiEnhanced;

/// <summary>
/// Lifecycle action to take on data.
/// </summary>
public enum LifecycleAction
{
    /// <summary>
    /// No action needed.
    /// </summary>
    None,

    /// <summary>
    /// Keep data as-is.
    /// </summary>
    Retain,

    /// <summary>
    /// Move to a different storage tier.
    /// </summary>
    Migrate,

    /// <summary>
    /// Archive for long-term storage.
    /// </summary>
    Archive,

    /// <summary>
    /// Mark for deletion after review.
    /// </summary>
    MarkForDeletion,

    /// <summary>
    /// Immediately delete.
    /// </summary>
    Delete,

    /// <summary>
    /// Apply additional compression.
    /// </summary>
    Compress,

    /// <summary>
    /// Increase replication for durability.
    /// </summary>
    IncreaseReplication,

    /// <summary>
    /// Decrease replication to save costs.
    /// </summary>
    DecreaseReplication
}

/// <summary>
/// Importance level of data.
/// </summary>
public enum ImportanceLevel
{
    /// <summary>
    /// Critical - must never be lost.
    /// </summary>
    Critical = 100,

    /// <summary>
    /// High importance.
    /// </summary>
    High = 75,

    /// <summary>
    /// Normal importance.
    /// </summary>
    Normal = 50,

    /// <summary>
    /// Low importance.
    /// </summary>
    Low = 25,

    /// <summary>
    /// Minimal importance - candidate for deletion.
    /// </summary>
    Minimal = 10,

    /// <summary>
    /// Unknown importance - needs analysis.
    /// </summary>
    Unknown = 0
}

/// <summary>
/// Lifecycle prediction for a data object.
/// </summary>
public sealed class LifecyclePrediction
{
    /// <summary>
    /// Object identifier.
    /// </summary>
    public required string ObjectId { get; init; }

    /// <summary>
    /// Predicted importance level.
    /// </summary>
    public ImportanceLevel Importance { get; init; }

    /// <summary>
    /// Recommended lifecycle action.
    /// </summary>
    public LifecycleAction RecommendedAction { get; init; }

    /// <summary>
    /// Confidence in the prediction (0.0-1.0).
    /// </summary>
    public double Confidence { get; init; }

    /// <summary>
    /// Predicted future access probability (0.0-1.0).
    /// </summary>
    public double FutureAccessProbability { get; init; }

    /// <summary>
    /// Predicted time until next access.
    /// </summary>
    public TimeSpan? PredictedNextAccess { get; init; }

    /// <summary>
    /// Recommended retention period.
    /// </summary>
    public TimeSpan? RecommendedRetention { get; init; }

    /// <summary>
    /// Reasoning for the prediction.
    /// </summary>
    public string? Reasoning { get; init; }

    /// <summary>
    /// Risk score if action is not taken (0.0-1.0).
    /// </summary>
    public double RiskScore { get; init; }

    /// <summary>
    /// Estimated cost impact of recommended action.
    /// </summary>
    public decimal? EstimatedCostImpact { get; init; }

    /// <summary>
    /// Whether this prediction was AI-generated.
    /// </summary>
    public bool IsAiGenerated { get; init; }

    /// <summary>
    /// When this prediction was generated.
    /// </summary>
    public DateTime GeneratedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Data object metadata for lifecycle analysis.
/// </summary>
public sealed class DataObjectInfo
{
    /// <summary>
    /// Object identifier.
    /// </summary>
    public required string ObjectId { get; init; }

    /// <summary>
    /// Object creation time.
    /// </summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>
    /// Last access time.
    /// </summary>
    public DateTime? LastAccessedAt { get; init; }

    /// <summary>
    /// Last modification time.
    /// </summary>
    public DateTime? LastModifiedAt { get; init; }

    /// <summary>
    /// Object size in bytes.
    /// </summary>
    public long SizeBytes { get; init; }

    /// <summary>
    /// Content type.
    /// </summary>
    public string? ContentType { get; init; }

    /// <summary>
    /// Current storage tier.
    /// </summary>
    public StorageTier CurrentTier { get; init; }

    /// <summary>
    /// Total access count.
    /// </summary>
    public long AccessCount { get; init; }

    /// <summary>
    /// Whether the object has compliance requirements.
    /// </summary>
    public bool HasComplianceRequirements { get; init; }

    /// <summary>
    /// Minimum retention period if compliance-bound.
    /// </summary>
    public TimeSpan? MinimumRetention { get; init; }

    /// <summary>
    /// Owner or creator identifier.
    /// </summary>
    public string? OwnerId { get; init; }

    /// <summary>
    /// Tags associated with the object.
    /// </summary>
    public string[]? Tags { get; init; }

    /// <summary>
    /// Additional metadata.
    /// </summary>
    public Dictionary<string, object>? Metadata { get; init; }
}

/// <summary>
/// Predictive data lifecycle strategy using AI to predict data importance
/// and recommend proactive lifecycle actions.
/// </summary>
/// <remarks>
/// Features:
/// - AI-powered importance scoring
/// - Future access pattern prediction
/// - Proactive lifecycle recommendations
/// - Compliance-aware predictions
/// - Risk assessment
/// - Cost impact estimation
/// </remarks>
public sealed class PredictiveDataLifecycleStrategy : AiEnhancedStrategyBase
{
    private readonly ConcurrentDictionary<string, DataObjectInfo> _trackedObjects = new();
    private readonly ConcurrentDictionary<string, LifecyclePrediction> _predictions = new();
    private readonly TimeSpan _predictionCacheTtl;
    private readonly object _analysisLock = new();

    /// <summary>
    /// Initializes a new PredictiveDataLifecycleStrategy with default cache TTL.
    /// </summary>
    public PredictiveDataLifecycleStrategy() : this(TimeSpan.FromHours(6)) { }

    /// <summary>
    /// Initializes a new PredictiveDataLifecycleStrategy with custom cache TTL.
    /// </summary>
    /// <param name="predictionCacheTtl">How long to cache predictions.</param>
    public PredictiveDataLifecycleStrategy(TimeSpan predictionCacheTtl)
    {
        _predictionCacheTtl = predictionCacheTtl;
    }

    /// <inheritdoc/>
    public override string StrategyId => "ai.predictive-lifecycle";

    /// <inheritdoc/>
    public override string DisplayName => "Predictive Data Lifecycle";

    /// <inheritdoc/>
    public override AiEnhancedCategory AiCategory => AiEnhancedCategory.Lifecycle;

    /// <inheritdoc/>
    public override IntelligenceCapabilities RequiredCapabilities =>
        IntelligenceCapabilities.DataLifecyclePrediction | IntelligenceCapabilities.Prediction;

    /// <inheritdoc/>
    public override DataManagementCapabilities Capabilities { get; } = new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = true,
        SupportsTransactions = false,
        SupportsTTL = false,
        MaxThroughput = 5_000,
        TypicalLatencyMs = 10.0
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Predictive data lifecycle strategy using AI to predict data importance, " +
        "future access patterns, and recommend proactive lifecycle actions for cost optimization and compliance.";

    /// <inheritdoc/>
    public override string[] Tags => ["ai", "lifecycle", "prediction", "importance", "retention", "compliance"];

    /// <summary>
    /// Registers a data object for lifecycle tracking.
    /// </summary>
    /// <param name="info">Data object information.</param>
    public void RegisterObject(DataObjectInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentException.ThrowIfNullOrWhiteSpace(info.ObjectId);

        _trackedObjects[info.ObjectId] = info;
    }

    /// <summary>
    /// Updates access information for a tracked object.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <param name="isWrite">Whether this was a write operation.</param>
    public void RecordAccess(string objectId, bool isWrite = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);

        if (_trackedObjects.TryGetValue(objectId, out var existing))
        {
            _trackedObjects[objectId] = new DataObjectInfo
            {
                ObjectId = existing.ObjectId,
                CreatedAt = existing.CreatedAt,
                LastAccessedAt = DateTime.UtcNow,
                LastModifiedAt = isWrite ? DateTime.UtcNow : existing.LastModifiedAt,
                SizeBytes = existing.SizeBytes,
                ContentType = existing.ContentType,
                CurrentTier = existing.CurrentTier,
                AccessCount = existing.AccessCount + 1,
                HasComplianceRequirements = existing.HasComplianceRequirements,
                MinimumRetention = existing.MinimumRetention,
                OwnerId = existing.OwnerId,
                Tags = existing.Tags,
                Metadata = existing.Metadata
            };

            // Invalidate cached prediction
            _predictions.TryRemove(objectId, out _);
        }
    }

    /// <summary>
    /// Gets a lifecycle prediction for an object.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <param name="forceRefresh">Force a new prediction instead of using cache.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Lifecycle prediction or null if object not tracked.</returns>
    public async Task<LifecyclePrediction?> GetPredictionAsync(
        string objectId,
        bool forceRefresh = false,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);

        // Check cache
        if (!forceRefresh && _predictions.TryGetValue(objectId, out var cached))
        {
            if (DateTime.UtcNow - cached.GeneratedAt < _predictionCacheTtl)
            {
                return cached;
            }
        }

        // Get object info
        if (!_trackedObjects.TryGetValue(objectId, out var info))
        {
            return null;
        }

        var sw = Stopwatch.StartNew();
        LifecyclePrediction prediction;

        if (IsAiAvailable)
        {
            prediction = await GetAiPredictionAsync(info, ct) ?? GetFallbackPrediction(info);
            RecordAiOperation(prediction.IsAiGenerated, false, prediction.Confidence, sw.Elapsed.TotalMilliseconds);
        }
        else
        {
            prediction = GetFallbackPrediction(info);
            RecordAiOperation(false, false, 0, sw.Elapsed.TotalMilliseconds);
        }

        _predictions[objectId] = prediction;
        return prediction;
    }

    /// <summary>
    /// Gets predictions for all tracked objects.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Dictionary of object IDs to predictions.</returns>
    public async Task<IReadOnlyDictionary<string, LifecyclePrediction>> GetAllPredictionsAsync(CancellationToken ct = default)
    {
        ThrowIfNotInitialized();

        var results = new Dictionary<string, LifecyclePrediction>();

        foreach (var objectId in _trackedObjects.Keys)
        {
            var prediction = await GetPredictionAsync(objectId, false, ct);
            if (prediction != null)
            {
                results[objectId] = prediction;
            }
        }

        return results;
    }

    /// <summary>
    /// Gets objects that need attention based on predictions.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Objects requiring action, ordered by priority.</returns>
    public async Task<IReadOnlyList<LifecyclePrediction>> GetActionableItemsAsync(CancellationToken ct = default)
    {
        var predictions = await GetAllPredictionsAsync(ct);

        return predictions.Values
            .Where(p => p.RecommendedAction != LifecycleAction.None && p.RecommendedAction != LifecycleAction.Retain)
            .OrderByDescending(p => p.RiskScore)
            .ThenByDescending(p => p.Confidence)
            .ToList();
    }

    /// <summary>
    /// Gets objects by importance level.
    /// </summary>
    /// <param name="minImportance">Minimum importance level.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Objects meeting the importance criteria.</returns>
    public async Task<IReadOnlyList<LifecyclePrediction>> GetByImportanceAsync(
        ImportanceLevel minImportance,
        CancellationToken ct = default)
    {
        var predictions = await GetAllPredictionsAsync(ct);

        return predictions.Values
            .Where(p => p.Importance >= minImportance)
            .OrderByDescending(p => p.Importance)
            .ToList();
    }

    /// <summary>
    /// Gets deletion candidates based on predictions.
    /// </summary>
    /// <param name="minConfidence">Minimum confidence for deletion recommendation.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Objects safe to delete.</returns>
    public async Task<IReadOnlyList<LifecyclePrediction>> GetDeletionCandidatesAsync(
        double minConfidence = 0.8,
        CancellationToken ct = default)
    {
        var predictions = await GetAllPredictionsAsync(ct);

        return predictions.Values
            .Where(p =>
                (p.RecommendedAction == LifecycleAction.Delete || p.RecommendedAction == LifecycleAction.MarkForDeletion) &&
                p.Confidence >= minConfidence &&
                p.Importance <= ImportanceLevel.Low)
            .OrderByDescending(p => p.Confidence)
            .ToList();
    }

    /// <summary>
    /// Removes an object from tracking.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <returns>True if removed.</returns>
    public bool RemoveObject(string objectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        _predictions.TryRemove(objectId, out _);
        return _trackedObjects.TryRemove(objectId, out _);
    }

    private async Task<LifecyclePrediction?> GetAiPredictionAsync(DataObjectInfo info, CancellationToken ct)
    {
        var inputData = new Dictionary<string, object>
        {
            ["objectId"] = info.ObjectId,
            ["createdAt"] = info.CreatedAt,
            ["ageHours"] = (DateTime.UtcNow - info.CreatedAt).TotalHours,
            ["lastAccessHours"] = info.LastAccessedAt.HasValue ? (DateTime.UtcNow - info.LastAccessedAt.Value).TotalHours : -1,
            ["lastModifiedHours"] = info.LastModifiedAt.HasValue ? (DateTime.UtcNow - info.LastModifiedAt.Value).TotalHours : -1,
            ["sizeBytes"] = info.SizeBytes,
            ["contentType"] = info.ContentType ?? "unknown",
            ["currentTier"] = (int)info.CurrentTier,
            ["accessCount"] = info.AccessCount,
            ["hasCompliance"] = info.HasComplianceRequirements
        };

        if (info.Tags != null)
            inputData["tags"] = info.Tags;
        if (info.Metadata != null)
            inputData["metadata"] = info.Metadata;

        var prediction = await RequestPredictionAsync("data_lifecycle", inputData, DefaultContext, ct);

        if (prediction.HasValue)
        {
            var metadata = prediction.Value.Metadata;

            var importance = ImportanceLevel.Normal;
            if (metadata.TryGetValue("importance", out var impObj))
            {
                if (impObj is int impInt)
                    importance = (ImportanceLevel)impInt;
                else if (impObj is string impStr && Enum.TryParse<ImportanceLevel>(impStr, out var parsed))
                    importance = parsed;
            }

            var action = LifecycleAction.None;
            if (metadata.TryGetValue("action", out var actObj))
            {
                if (actObj is int actInt)
                    action = (LifecycleAction)actInt;
                else if (actObj is string actStr && Enum.TryParse<LifecycleAction>(actStr, out var parsedAct))
                    action = parsedAct;
            }

            // Respect compliance requirements
            if (info.HasComplianceRequirements && info.MinimumRetention.HasValue)
            {
                var age = DateTime.UtcNow - info.CreatedAt;
                if (age < info.MinimumRetention.Value)
                {
                    action = action == LifecycleAction.Delete || action == LifecycleAction.MarkForDeletion
                        ? LifecycleAction.Retain
                        : action;
                }
            }

            return new LifecyclePrediction
            {
                ObjectId = info.ObjectId,
                Importance = importance,
                RecommendedAction = action,
                Confidence = prediction.Value.Confidence,
                FutureAccessProbability = metadata.TryGetValue("futureAccessProb", out var fap) && fap is double d ? d : 0.5,
                PredictedNextAccess = metadata.TryGetValue("nextAccessHours", out var nah) && nah is double h ? TimeSpan.FromHours(h) : null,
                RecommendedRetention = metadata.TryGetValue("retentionDays", out var rd) && rd is double days ? TimeSpan.FromDays(days) : null,
                Reasoning = metadata.TryGetValue("reasoning", out var reason) ? reason?.ToString() : null,
                RiskScore = metadata.TryGetValue("riskScore", out var risk) && risk is double r ? r : 0.0,
                EstimatedCostImpact = metadata.TryGetValue("costImpact", out var cost) && cost is double c ? (decimal)c : null,
                IsAiGenerated = true
            };
        }

        return null;
    }

    private LifecyclePrediction GetFallbackPrediction(DataObjectInfo info)
    {
        var age = DateTime.UtcNow - info.CreatedAt;
        var daysSinceAccess = info.LastAccessedAt.HasValue
            ? (DateTime.UtcNow - info.LastAccessedAt.Value).TotalDays
            : age.TotalDays;

        // Calculate access frequency
        var accessFrequency = age.TotalDays > 0 ? info.AccessCount / age.TotalDays : 0;

        // Determine importance
        ImportanceLevel importance;
        if (info.HasComplianceRequirements)
        {
            importance = ImportanceLevel.High;
        }
        else if (accessFrequency > 10)
        {
            importance = ImportanceLevel.High;
        }
        else if (accessFrequency > 1)
        {
            importance = ImportanceLevel.Normal;
        }
        else if (daysSinceAccess > 365)
        {
            importance = ImportanceLevel.Minimal;
        }
        else if (daysSinceAccess > 90)
        {
            importance = ImportanceLevel.Low;
        }
        else
        {
            importance = ImportanceLevel.Normal;
        }

        // Determine action
        LifecycleAction action;
        string reasoning;

        if (info.HasComplianceRequirements && info.MinimumRetention.HasValue && age < info.MinimumRetention.Value)
        {
            action = LifecycleAction.Retain;
            reasoning = $"Compliance requires retention until {(info.CreatedAt + info.MinimumRetention.Value):d}";
        }
        else if (daysSinceAccess > 365 && !info.HasComplianceRequirements)
        {
            action = LifecycleAction.MarkForDeletion;
            reasoning = "No access in over a year - candidate for deletion";
        }
        else if (daysSinceAccess > 180)
        {
            action = LifecycleAction.Archive;
            reasoning = "No access in 6+ months - recommend archival";
        }
        else if (daysSinceAccess > 90 && info.CurrentTier == StorageTier.Hot)
        {
            action = LifecycleAction.Migrate;
            reasoning = "Low access frequency - recommend cooler storage tier";
        }
        else if (accessFrequency > 100 && info.CurrentTier != StorageTier.Hot)
        {
            action = LifecycleAction.Migrate;
            reasoning = "High access frequency - recommend hot storage tier";
        }
        else
        {
            action = LifecycleAction.Retain;
            reasoning = "Access pattern is appropriate for current configuration";
        }

        return new LifecyclePrediction
        {
            ObjectId = info.ObjectId,
            Importance = importance,
            RecommendedAction = action,
            Confidence = 0.5,
            FutureAccessProbability = accessFrequency > 1 ? 0.7 : 0.2,
            PredictedNextAccess = accessFrequency > 0 ? TimeSpan.FromDays(1 / accessFrequency) : null,
            RecommendedRetention = info.HasComplianceRequirements ? info.MinimumRetention : null,
            Reasoning = reasoning,
            RiskScore = action == LifecycleAction.MarkForDeletion ? 0.2 : 0.0,
            IsAiGenerated = false
        };
    }
}
