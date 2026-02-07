using System.Collections.Concurrent;
using System.Diagnostics;
using DataWarehouse.SDK.Contracts.IntelligenceAware;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.AiEnhanced;

/// <summary>
/// Storage tier for data placement decisions.
/// </summary>
public enum StorageTier
{
    /// <summary>
    /// Hot tier - fastest access, highest cost.
    /// </summary>
    Hot = 0,

    /// <summary>
    /// Warm tier - balanced access speed and cost.
    /// </summary>
    Warm = 1,

    /// <summary>
    /// Cool tier - infrequent access, lower cost.
    /// </summary>
    Cool = 2,

    /// <summary>
    /// Cold tier - rare access, lowest cost.
    /// </summary>
    Cold = 3,

    /// <summary>
    /// Archive tier - long-term storage.
    /// </summary>
    Archive = 4
}

/// <summary>
/// Data placement recommendation from AI.
/// </summary>
public sealed class PlacementRecommendation
{
    /// <summary>
    /// Recommended storage tier.
    /// </summary>
    public required StorageTier RecommendedTier { get; init; }

    /// <summary>
    /// Confidence in the recommendation (0.0-1.0).
    /// </summary>
    public double Confidence { get; init; }

    /// <summary>
    /// Predicted access frequency (accesses per day).
    /// </summary>
    public double PredictedAccessFrequency { get; init; }

    /// <summary>
    /// Predicted next access time.
    /// </summary>
    public TimeSpan? PredictedNextAccess { get; init; }

    /// <summary>
    /// Reasoning for the recommendation.
    /// </summary>
    public string? Reasoning { get; init; }

    /// <summary>
    /// Whether cross-tier optimization is recommended.
    /// </summary>
    public bool RecommendsCrossTierOptimization { get; init; }

    /// <summary>
    /// Suggested replication factor.
    /// </summary>
    public int SuggestedReplicationFactor { get; init; } = 1;
}

/// <summary>
/// Access pattern for a data object.
/// </summary>
public sealed class AccessPattern
{
    /// <summary>
    /// Object identifier.
    /// </summary>
    public required string ObjectId { get; init; }

    /// <summary>
    /// Number of reads in the observation window.
    /// </summary>
    public long ReadCount { get; init; }

    /// <summary>
    /// Number of writes in the observation window.
    /// </summary>
    public long WriteCount { get; init; }

    /// <summary>
    /// Last access time.
    /// </summary>
    public DateTime? LastAccess { get; init; }

    /// <summary>
    /// Object size in bytes.
    /// </summary>
    public long SizeBytes { get; init; }

    /// <summary>
    /// Object content type.
    /// </summary>
    public string? ContentType { get; init; }

    /// <summary>
    /// Object creation time.
    /// </summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>
    /// Current storage tier.
    /// </summary>
    public StorageTier CurrentTier { get; init; }

    /// <summary>
    /// Custom metadata for AI analysis.
    /// </summary>
    public Dictionary<string, object>? Metadata { get; init; }
}

/// <summary>
/// AI-driven data placement and orchestration strategy.
/// Analyzes access patterns to optimize data placement across storage tiers.
/// </summary>
/// <remarks>
/// Features:
/// - AI-powered access pattern analysis
/// - Cross-tier optimization recommendations
/// - Workload prediction for proactive placement
/// - Graceful fallback to heuristic-based placement
/// - Thread-safe operation tracking
/// </remarks>
public sealed class AiDataOrchestratorStrategy : AiEnhancedStrategyBase
{
    private readonly ConcurrentDictionary<string, AccessPattern> _accessPatterns = new();
    private readonly ConcurrentDictionary<string, PlacementRecommendation> _recommendations = new();
    private readonly object _analysisLock = new();
    private readonly TimeSpan _observationWindow;
    private DateTime _lastBatchAnalysis = DateTime.MinValue;

    /// <summary>
    /// Initializes a new AiDataOrchestratorStrategy.
    /// </summary>
    public AiDataOrchestratorStrategy() : this(TimeSpan.FromDays(7)) { }

    /// <summary>
    /// Initializes a new AiDataOrchestratorStrategy with custom observation window.
    /// </summary>
    /// <param name="observationWindow">Time window for access pattern analysis.</param>
    public AiDataOrchestratorStrategy(TimeSpan observationWindow)
    {
        _observationWindow = observationWindow;
    }

    /// <inheritdoc/>
    public override string StrategyId => "ai.orchestrator";

    /// <inheritdoc/>
    public override string DisplayName => "AI Data Orchestrator";

    /// <inheritdoc/>
    public override AiEnhancedCategory AiCategory => AiEnhancedCategory.Orchestration;

    /// <inheritdoc/>
    public override IntelligenceCapabilities RequiredCapabilities =>
        IntelligenceCapabilities.AccessPatternPrediction | IntelligenceCapabilities.TieringRecommendation;

    /// <inheritdoc/>
    public override DataManagementCapabilities Capabilities { get; } = new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = true,
        SupportsTransactions = false,
        SupportsTTL = false,
        MaxThroughput = 10_000,
        TypicalLatencyMs = 5.0
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "AI-driven data placement and orchestration strategy using machine learning to analyze access patterns, " +
        "predict workloads, and optimize data placement across storage tiers for cost and performance.";

    /// <inheritdoc/>
    public override string[] Tags => ["ai", "orchestration", "tiering", "placement", "prediction", "optimization"];

    /// <summary>
    /// Records an access event for pattern analysis.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <param name="isWrite">Whether this is a write operation.</param>
    /// <param name="sizeBytes">Object size in bytes.</param>
    /// <param name="contentType">Content type.</param>
    /// <param name="currentTier">Current storage tier.</param>
    /// <param name="metadata">Additional metadata.</param>
    public void RecordAccess(
        string objectId,
        bool isWrite,
        long sizeBytes,
        string? contentType = null,
        StorageTier currentTier = StorageTier.Hot,
        Dictionary<string, object>? metadata = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);

        _accessPatterns.AddOrUpdate(
            objectId,
            _ => new AccessPattern
            {
                ObjectId = objectId,
                ReadCount = isWrite ? 0 : 1,
                WriteCount = isWrite ? 1 : 0,
                LastAccess = DateTime.UtcNow,
                SizeBytes = sizeBytes,
                ContentType = contentType,
                CreatedAt = DateTime.UtcNow,
                CurrentTier = currentTier,
                Metadata = metadata
            },
            (_, existing) => new AccessPattern
            {
                ObjectId = objectId,
                ReadCount = existing.ReadCount + (isWrite ? 0 : 1),
                WriteCount = existing.WriteCount + (isWrite ? 1 : 0),
                LastAccess = DateTime.UtcNow,
                SizeBytes = sizeBytes,
                ContentType = contentType ?? existing.ContentType,
                CreatedAt = existing.CreatedAt,
                CurrentTier = currentTier,
                Metadata = metadata ?? existing.Metadata
            });
    }

    /// <summary>
    /// Gets a placement recommendation for an object.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <param name="forceRefresh">Force a new analysis instead of using cached recommendation.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Placement recommendation or null if no data available.</returns>
    public async Task<PlacementRecommendation?> GetPlacementRecommendationAsync(
        string objectId,
        bool forceRefresh = false,
        CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);

        // Check cache
        if (!forceRefresh && _recommendations.TryGetValue(objectId, out var cached))
        {
            return cached;
        }

        // Get access pattern
        if (!_accessPatterns.TryGetValue(objectId, out var pattern))
        {
            return null;
        }

        var sw = Stopwatch.StartNew();
        PlacementRecommendation recommendation;

        if (IsAiAvailable)
        {
            recommendation = await GetAiRecommendationAsync(pattern, ct) ?? GetFallbackRecommendation(pattern);
            RecordAiOperation(true, false, recommendation.Confidence, sw.Elapsed.TotalMilliseconds);
        }
        else
        {
            recommendation = GetFallbackRecommendation(pattern);
            RecordAiOperation(false, false, 0, sw.Elapsed.TotalMilliseconds);
        }

        _recommendations[objectId] = recommendation;
        return recommendation;
    }

    /// <summary>
    /// Analyzes all tracked objects and generates placement recommendations.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Dictionary of object IDs to recommendations.</returns>
    public async Task<IReadOnlyDictionary<string, PlacementRecommendation>> AnalyzeAllAsync(CancellationToken ct = default)
    {
        ThrowIfNotInitialized();

        var results = new Dictionary<string, PlacementRecommendation>();
        var patterns = _accessPatterns.Values.ToList();

        if (patterns.Count == 0)
            return results;

        var sw = Stopwatch.StartNew();

        if (IsAiAvailable)
        {
            // Batch AI analysis
            var batchResult = await GetAiBatchRecommendationsAsync(patterns, ct);
            if (batchResult != null)
            {
                foreach (var (objectId, rec) in batchResult)
                {
                    results[objectId] = rec;
                    _recommendations[objectId] = rec;
                }
                RecordAiOperation(true, false, batchResult.Values.Average(r => r.Confidence), sw.Elapsed.TotalMilliseconds);
            }
            else
            {
                // AI failed, use fallback
                foreach (var pattern in patterns)
                {
                    var rec = GetFallbackRecommendation(pattern);
                    results[pattern.ObjectId] = rec;
                    _recommendations[pattern.ObjectId] = rec;
                }
                RecordAiOperation(false, false, 0, sw.Elapsed.TotalMilliseconds);
            }
        }
        else
        {
            // Fallback analysis
            foreach (var pattern in patterns)
            {
                var rec = GetFallbackRecommendation(pattern);
                results[pattern.ObjectId] = rec;
                _recommendations[pattern.ObjectId] = rec;
            }
            RecordAiOperation(false, false, 0, sw.Elapsed.TotalMilliseconds);
        }

        _lastBatchAnalysis = DateTime.UtcNow;
        return results;
    }

    /// <summary>
    /// Gets objects that should be migrated to a different tier.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of objects with recommended tier changes.</returns>
    public async Task<IReadOnlyList<(string ObjectId, StorageTier CurrentTier, StorageTier RecommendedTier)>> GetMigrationCandidatesAsync(
        CancellationToken ct = default)
    {
        var recommendations = await AnalyzeAllAsync(ct);
        var candidates = new List<(string, StorageTier, StorageTier)>();

        foreach (var (objectId, rec) in recommendations)
        {
            if (_accessPatterns.TryGetValue(objectId, out var pattern))
            {
                if (rec.RecommendedTier != pattern.CurrentTier && rec.Confidence >= 0.7)
                {
                    candidates.Add((objectId, pattern.CurrentTier, rec.RecommendedTier));
                }
            }
        }

        return candidates.OrderByDescending(c => Math.Abs((int)c.Item2 - (int)c.Item3)).ToList();
    }

    /// <summary>
    /// Clears tracking data older than the observation window.
    /// </summary>
    public void PruneOldData()
    {
        var cutoff = DateTime.UtcNow - _observationWindow;
        var toRemove = _accessPatterns
            .Where(kvp => kvp.Value.LastAccess < cutoff)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in toRemove)
        {
            _accessPatterns.TryRemove(key, out _);
            _recommendations.TryRemove(key, out _);
        }
    }

    private async Task<PlacementRecommendation?> GetAiRecommendationAsync(AccessPattern pattern, CancellationToken ct)
    {
        var inputData = new Dictionary<string, object>
        {
            ["objectId"] = pattern.ObjectId,
            ["readCount"] = pattern.ReadCount,
            ["writeCount"] = pattern.WriteCount,
            ["sizeBytes"] = pattern.SizeBytes,
            ["contentType"] = pattern.ContentType ?? "unknown",
            ["ageHours"] = (DateTime.UtcNow - pattern.CreatedAt).TotalHours,
            ["lastAccessHours"] = pattern.LastAccess.HasValue ? (DateTime.UtcNow - pattern.LastAccess.Value).TotalHours : -1,
            ["currentTier"] = (int)pattern.CurrentTier
        };

        if (pattern.Metadata != null)
        {
            inputData["metadata"] = pattern.Metadata;
        }

        var prediction = await RequestPredictionAsync("storage_tiering", inputData, DefaultContext, ct);

        if (prediction.HasValue)
        {
            var tier = StorageTier.Hot;
            if (prediction.Value.Prediction is int tierInt)
                tier = (StorageTier)tierInt;
            else if (prediction.Value.Prediction is string tierStr && Enum.TryParse<StorageTier>(tierStr, out var parsed))
                tier = parsed;

            var metadata = prediction.Value.Metadata;
            return new PlacementRecommendation
            {
                RecommendedTier = tier,
                Confidence = prediction.Value.Confidence,
                PredictedAccessFrequency = metadata.TryGetValue("accessFrequency", out var freq) && freq is double f ? f : 0,
                PredictedNextAccess = metadata.TryGetValue("nextAccessHours", out var next) && next is double h ? TimeSpan.FromHours(h) : null,
                Reasoning = metadata.TryGetValue("reasoning", out var reason) ? reason?.ToString() : null,
                RecommendsCrossTierOptimization = metadata.TryGetValue("crossTierOptimization", out var cross) && cross is true,
                SuggestedReplicationFactor = metadata.TryGetValue("replicationFactor", out var rep) && rep is int r ? r : 1
            };
        }

        return null;
    }

    private async Task<Dictionary<string, PlacementRecommendation>?> GetAiBatchRecommendationsAsync(
        List<AccessPattern> patterns,
        CancellationToken ct)
    {
        var inputData = new Dictionary<string, object>
        {
            ["patterns"] = patterns.Select(p => new Dictionary<string, object>
            {
                ["objectId"] = p.ObjectId,
                ["readCount"] = p.ReadCount,
                ["writeCount"] = p.WriteCount,
                ["sizeBytes"] = p.SizeBytes,
                ["contentType"] = p.ContentType ?? "unknown",
                ["ageHours"] = (DateTime.UtcNow - p.CreatedAt).TotalHours,
                ["lastAccessHours"] = p.LastAccess.HasValue ? (DateTime.UtcNow - p.LastAccess.Value).TotalHours : -1,
                ["currentTier"] = (int)p.CurrentTier
            }).ToList()
        };

        var prediction = await RequestPredictionAsync("batch_storage_tiering", inputData, DefaultContext, ct);

        if (prediction.HasValue && prediction.Value.Prediction is object[] results)
        {
            var recommendations = new Dictionary<string, PlacementRecommendation>();

            foreach (var result in results.OfType<Dictionary<string, object>>())
            {
                if (result.TryGetValue("objectId", out var idObj) && idObj is string objectId)
                {
                    var tier = StorageTier.Hot;
                    if (result.TryGetValue("tier", out var tierObj))
                    {
                        if (tierObj is int tierInt)
                            tier = (StorageTier)tierInt;
                        else if (tierObj is string tierStr && Enum.TryParse<StorageTier>(tierStr, out var parsed))
                            tier = parsed;
                    }

                    recommendations[objectId] = new PlacementRecommendation
                    {
                        RecommendedTier = tier,
                        Confidence = result.TryGetValue("confidence", out var conf) && conf is double c ? c : 0.5,
                        PredictedAccessFrequency = result.TryGetValue("accessFrequency", out var freq) && freq is double f ? f : 0,
                        Reasoning = result.TryGetValue("reasoning", out var reason) ? reason?.ToString() : null
                    };
                }
            }

            return recommendations;
        }

        return null;
    }

    private PlacementRecommendation GetFallbackRecommendation(AccessPattern pattern)
    {
        // Heuristic-based placement
        var totalAccesses = pattern.ReadCount + pattern.WriteCount;
        var hoursSinceLastAccess = pattern.LastAccess.HasValue
            ? (DateTime.UtcNow - pattern.LastAccess.Value).TotalHours
            : double.MaxValue;
        var ageHours = (DateTime.UtcNow - pattern.CreatedAt).TotalHours;

        // Calculate access frequency (per day)
        var accessFrequency = ageHours > 0 ? (totalAccesses / ageHours) * 24 : 0;

        StorageTier tier;
        string reasoning;

        if (hoursSinceLastAccess > 24 * 365) // Not accessed in a year
        {
            tier = StorageTier.Archive;
            reasoning = "No access in over a year - recommend archive tier";
        }
        else if (hoursSinceLastAccess > 24 * 90) // Not accessed in 90 days
        {
            tier = StorageTier.Cold;
            reasoning = "No access in 90+ days - recommend cold tier";
        }
        else if (hoursSinceLastAccess > 24 * 30 || accessFrequency < 0.1) // Not accessed in 30 days or very low frequency
        {
            tier = StorageTier.Cool;
            reasoning = "Low access frequency or 30+ days since last access - recommend cool tier";
        }
        else if (accessFrequency > 10) // High frequency
        {
            tier = StorageTier.Hot;
            reasoning = "High access frequency - recommend hot tier";
        }
        else
        {
            tier = StorageTier.Warm;
            reasoning = "Moderate access pattern - recommend warm tier";
        }

        return new PlacementRecommendation
        {
            RecommendedTier = tier,
            Confidence = 0.5, // Lower confidence for heuristic-based
            PredictedAccessFrequency = accessFrequency,
            PredictedNextAccess = accessFrequency > 0 ? TimeSpan.FromHours(24 / accessFrequency) : null,
            Reasoning = reasoning,
            RecommendsCrossTierOptimization = tier != pattern.CurrentTier && Math.Abs((int)tier - (int)pattern.CurrentTier) > 1,
            SuggestedReplicationFactor = tier == StorageTier.Hot ? 2 : 1
        };
    }
}
