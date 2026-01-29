// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using DataWarehouse.SDK.AI;
using System.Collections.Concurrent;
using System.Text.Json;
using IExtendedAIProvider = DataWarehouse.Plugins.AIAgents.IExtendedAIProvider;

namespace DataWarehouse.Plugins.AIAgents.Capabilities.AIOps;

/// <summary>
/// ML-driven data placement engine for optimal storage tiering.
/// Uses statistical analysis and AI-enhanced predictions to determine optimal storage tiers
/// based on access patterns, cost considerations, and performance requirements.
/// </summary>
public sealed class TieringEngine : IDisposable
{
    private readonly IExtendedAIProvider? _aiProvider;
    private readonly AccessPatternPredictor _accessPredictor;
    private readonly ConcurrentDictionary<string, TierAssignment> _assignments;
    private readonly ConcurrentQueue<TieringDecision> _decisionHistory;
    private readonly SemaphoreSlim _migrationLock;
    private readonly TieringEngineConfig _config;
    private readonly Timer? _optimizationTimer;
    private long _totalMigrations;
    private long _successfulMigrations;
    private bool _disposed;

    /// <summary>
    /// Creates a new tiering engine instance.
    /// </summary>
    /// <param name="aiProvider">Optional AI provider for enhanced recommendations.</param>
    /// <param name="config">Engine configuration.</param>
    public TieringEngine(IExtendedAIProvider? aiProvider = null, TieringEngineConfig? config = null)
    {
        _aiProvider = aiProvider;
        _config = config ?? new TieringEngineConfig();
        _accessPredictor = new AccessPatternPredictor(_config.HistorySize);
        _assignments = new ConcurrentDictionary<string, TierAssignment>();
        _decisionHistory = new ConcurrentQueue<TieringDecision>();
        _migrationLock = new SemaphoreSlim(_config.MaxConcurrentMigrations);

        if (_config.EnableAutonomousOptimization)
        {
            _optimizationTimer = new Timer(
                _ => _ = OptimizationCycleAsync(CancellationToken.None),
                null,
                TimeSpan.FromMinutes(5),
                TimeSpan.FromMinutes(_config.OptimizationIntervalMinutes));
        }
    }

    /// <summary>
    /// Records a data access event for pattern learning.
    /// </summary>
    /// <param name="resourceId">The resource identifier.</param>
    /// <param name="eventType">The type of access event.</param>
    /// <param name="sizeBytes">The size of the accessed data in bytes.</param>
    /// <param name="metadata">Optional metadata about the access.</param>
    public void RecordAccess(string resourceId, AccessEventType eventType, long sizeBytes, Dictionary<string, object>? metadata = null)
    {
        _accessPredictor.RecordAccess(resourceId, eventType, sizeBytes, metadata);
    }

    /// <summary>
    /// Gets a tiering recommendation for a resource using ML and optionally AI analysis.
    /// </summary>
    /// <param name="resourceId">The resource identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The tiering recommendation.</returns>
    public async Task<TieringRecommendation> GetRecommendationAsync(string resourceId, CancellationToken ct = default)
    {
        // Get ML-based prediction
        var mlRecommendation = _accessPredictor.RecommendTier(resourceId);
        var accessPrediction = _accessPredictor.PredictAccess(resourceId, TimeSpan.FromDays(7));

        // If confidence is high enough, use ML prediction directly
        if (mlRecommendation.Confidence > _config.HighConfidenceThreshold)
        {
            return new TieringRecommendation
            {
                ResourceId = resourceId,
                RecommendedTier = mlRecommendation.RecommendedTier,
                Confidence = mlRecommendation.Confidence,
                Source = RecommendationSource.MLModel,
                Reason = mlRecommendation.Reason,
                AccessPrediction = accessPrediction,
                EstimatedCostSavings = CalculateCostSavings(resourceId, mlRecommendation.RecommendedTier)
            };
        }

        // For lower confidence, enhance with AI analysis if available
        if (_aiProvider != null && _aiProvider.IsAvailable)
        {
            try
            {
                return await GetAIEnhancedRecommendationAsync(resourceId, mlRecommendation, accessPrediction, ct);
            }
            catch
            {
                // Graceful degradation: fall back to ML recommendation
            }
        }

        return new TieringRecommendation
        {
            ResourceId = resourceId,
            RecommendedTier = mlRecommendation.RecommendedTier,
            Confidence = mlRecommendation.Confidence,
            Source = RecommendationSource.MLModel,
            Reason = mlRecommendation.Reason,
            AccessPrediction = accessPrediction,
            EstimatedCostSavings = CalculateCostSavings(resourceId, mlRecommendation.RecommendedTier)
        };
    }

    /// <summary>
    /// Executes a tier migration for a resource.
    /// </summary>
    /// <param name="resourceId">The resource identifier.</param>
    /// <param name="targetTier">The target storage tier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The migration result.</returns>
    public async Task<MigrationResult> MigrateAsync(string resourceId, StorageTier targetTier, CancellationToken ct = default)
    {
        if (!await _migrationLock.WaitAsync(TimeSpan.FromSeconds(30), ct))
        {
            return new MigrationResult
            {
                ResourceId = resourceId,
                Success = false,
                Reason = "Migration queue full, please retry later"
            };
        }

        try
        {
            Interlocked.Increment(ref _totalMigrations);

            var currentAssignment = _assignments.GetValueOrDefault(resourceId);
            var previousTier = currentAssignment?.CurrentTier ?? StorageTier.Standard;

            if (previousTier == targetTier)
            {
                return new MigrationResult
                {
                    ResourceId = resourceId,
                    Success = true,
                    PreviousTier = previousTier,
                    NewTier = targetTier,
                    Reason = "Already in target tier"
                };
            }

            // Record the decision
            var decision = new TieringDecision
            {
                ResourceId = resourceId,
                Timestamp = DateTime.UtcNow,
                FromTier = previousTier,
                ToTier = targetTier,
                DecisionType = TieringDecisionType.Migration
            };

            // Perform migration (actual storage migration would be handled by storage provider)
            var newAssignment = new TierAssignment
            {
                ResourceId = resourceId,
                CurrentTier = targetTier,
                AssignedAt = DateTime.UtcNow,
                PreviousTier = previousTier
            };

            _assignments[resourceId] = newAssignment;
            _decisionHistory.Enqueue(decision);

            // Trim decision history
            while (_decisionHistory.Count > 10000)
            {
                _decisionHistory.TryDequeue(out _);
            }

            Interlocked.Increment(ref _successfulMigrations);

            return new MigrationResult
            {
                ResourceId = resourceId,
                Success = true,
                PreviousTier = previousTier,
                NewTier = targetTier,
                MigrationTime = DateTime.UtcNow,
                Reason = $"Successfully migrated from {previousTier} to {targetTier}"
            };
        }
        finally
        {
            _migrationLock.Release();
        }
    }

    /// <summary>
    /// Runs an optimization cycle to identify and migrate resources.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The optimization cycle result.</returns>
    public async Task<TieringOptimizationResult> OptimizationCycleAsync(CancellationToken ct = default)
    {
        var startTime = DateTime.UtcNow;
        var evaluated = 0;
        var migrated = 0;
        var errors = new List<string>();

        var patterns = _accessPredictor.GetAllPatterns().ToList();

        foreach (var pattern in patterns.Take(_config.MaxResourcesPerCycle))
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                evaluated++;
                var recommendation = await GetRecommendationAsync(pattern.ResourceId, ct);

                var currentTier = _assignments.GetValueOrDefault(pattern.ResourceId)?.CurrentTier ?? StorageTier.Standard;

                if (recommendation.RecommendedTier != currentTier &&
                    recommendation.Confidence >= _config.MigrationConfidenceThreshold)
                {
                    var result = await MigrateAsync(pattern.ResourceId, recommendation.RecommendedTier, ct);
                    if (result.Success)
                    {
                        migrated++;
                    }
                }
            }
            catch (Exception ex)
            {
                errors.Add($"{pattern.ResourceId}: {ex.Message}");
            }
        }

        return new TieringOptimizationResult
        {
            StartTime = startTime,
            EndTime = DateTime.UtcNow,
            ResourcesEvaluated = evaluated,
            ResourcesMigrated = migrated,
            Errors = errors
        };
    }

    /// <summary>
    /// Gets tiering statistics.
    /// </summary>
    /// <returns>The tiering statistics.</returns>
    public TieringStatistics GetStatistics()
    {
        var tierCounts = _assignments.Values
            .GroupBy(a => a.CurrentTier)
            .ToDictionary(g => g.Key, g => g.Count());

        return new TieringStatistics
        {
            TotalTrackedResources = _accessPredictor.GetAllPatterns().Count(),
            TotalAssignments = _assignments.Count,
            TierDistribution = tierCounts,
            TotalMigrations = Interlocked.Read(ref _totalMigrations),
            SuccessfulMigrations = Interlocked.Read(ref _successfulMigrations),
            MigrationSuccessRate = _totalMigrations > 0
                ? (double)_successfulMigrations / _totalMigrations
                : 1.0,
            RecentDecisions = _decisionHistory.TakeLast(100).ToList()
        };
    }

    private static CompletionRequest ToCompletionRequest(string prompt, string? model = null) => new()
    {
        Prompt = prompt,
        Model = model!
    };

    private async Task<TieringRecommendation> GetAIEnhancedRecommendationAsync(
        string resourceId,
        TierRecommendation mlRecommendation,
        AccessPrediction accessPrediction,
        CancellationToken ct)
    {
        if (_aiProvider == null)
            throw new InvalidOperationException("AI provider not available");

        var prompt = $@"Analyze the following storage tiering recommendation and provide your assessment:

Resource ID: {resourceId}
ML Recommended Tier: {mlRecommendation.RecommendedTier}
ML Confidence: {mlRecommendation.Confidence:P1}
ML Reason: {mlRecommendation.Reason}

Access Pattern:
- Accesses per day: {mlRecommendation.AccessesPerDay:F2}
- Days since last access: {mlRecommendation.DaysSinceLastAccess:F1}
- Access probability (next 7 days): {accessPrediction.Probability:P1}
- Recency factor: {accessPrediction.RecencyFactor:F3}
- Frequency factor: {accessPrediction.HourlyFactor:F3}

Available tiers: Hot (frequent access), Warm (moderate), Standard (default), Cool (infrequent), Archive (rare)

Respond with a JSON object containing:
- recommendedTier: the tier you recommend (Hot, Warm, Standard, Cool, or Archive)
- confidence: your confidence 0-1
- reason: brief explanation
- additionalInsights: any other observations";

        var request = ToCompletionRequest(prompt);

        var response = await _aiProvider.CompleteAsync(request, ct);

        if (!string.IsNullOrEmpty(response.Text))
        {
            try
            {
                var aiResult = ParseAITieringResponse(response.Text);

                // Combine ML and AI recommendations
                var combinedConfidence = (mlRecommendation.Confidence + aiResult.Confidence) / 2;
                var finalTier = combinedConfidence > 0.7 ? aiResult.RecommendedTier : mlRecommendation.RecommendedTier;

                return new TieringRecommendation
                {
                    ResourceId = resourceId,
                    RecommendedTier = finalTier,
                    Confidence = combinedConfidence,
                    Source = RecommendationSource.AIEnhanced,
                    Reason = aiResult.Reason,
                    AccessPrediction = accessPrediction,
                    EstimatedCostSavings = CalculateCostSavings(resourceId, finalTier),
                    AIInsights = aiResult.AdditionalInsights
                };
            }
            catch
            {
                // Fall through to ML-only recommendation
            }
        }

        return new TieringRecommendation
        {
            ResourceId = resourceId,
            RecommendedTier = mlRecommendation.RecommendedTier,
            Confidence = mlRecommendation.Confidence,
            Source = RecommendationSource.MLModel,
            Reason = mlRecommendation.Reason,
            AccessPrediction = accessPrediction,
            EstimatedCostSavings = CalculateCostSavings(resourceId, mlRecommendation.RecommendedTier)
        };
    }

    private AITieringResponse ParseAITieringResponse(string content)
    {
        var jsonStart = content.IndexOf('{');
        var jsonEnd = content.LastIndexOf('}');

        if (jsonStart >= 0 && jsonEnd > jsonStart)
        {
            var json = content.Substring(jsonStart, jsonEnd - jsonStart + 1);
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tierStr = root.GetProperty("recommendedTier").GetString() ?? "Standard";
            var tier = Enum.TryParse<StorageTier>(tierStr, true, out var t) ? t : StorageTier.Standard;

            return new AITieringResponse
            {
                RecommendedTier = tier,
                Confidence = root.TryGetProperty("confidence", out var conf) ? conf.GetDouble() : 0.5,
                Reason = root.TryGetProperty("reason", out var reason) ? reason.GetString() ?? "" : "",
                AdditionalInsights = root.TryGetProperty("additionalInsights", out var insights) ? insights.GetString() : null
            };
        }

        throw new InvalidOperationException("Could not parse AI response");
    }

    private double CalculateCostSavings(string resourceId, StorageTier targetTier)
    {
        var currentTier = _assignments.GetValueOrDefault(resourceId)?.CurrentTier ?? StorageTier.Standard;

        // Cost multipliers relative to Standard tier
        var tierCosts = new Dictionary<StorageTier, double>
        {
            [StorageTier.Hot] = 2.0,
            [StorageTier.Warm] = 1.2,
            [StorageTier.Standard] = 1.0,
            [StorageTier.Cool] = 0.5,
            [StorageTier.Archive] = 0.1
        };

        var currentCost = tierCosts.GetValueOrDefault(currentTier, 1.0);
        var targetCost = tierCosts.GetValueOrDefault(targetTier, 1.0);

        // Return percentage savings (negative means cost increase)
        return (currentCost - targetCost) / currentCost;
    }

    /// <summary>
    /// Releases all resources used by the engine.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _optimizationTimer?.Dispose();
        _migrationLock.Dispose();
    }
}

#region Configuration

/// <summary>
/// Configuration for the tiering engine.
/// </summary>
public sealed class TieringEngineConfig
{
    /// <summary>Gets or sets the history size for access pattern tracking.</summary>
    public int HistorySize { get; set; } = 1000;

    /// <summary>Gets or sets the maximum concurrent migrations.</summary>
    public int MaxConcurrentMigrations { get; set; } = 10;

    /// <summary>Gets or sets the optimization interval in minutes.</summary>
    public int OptimizationIntervalMinutes { get; set; } = 15;

    /// <summary>Gets or sets the maximum resources to evaluate per cycle.</summary>
    public int MaxResourcesPerCycle { get; set; } = 100;

    /// <summary>Gets or sets the high confidence threshold for ML-only decisions.</summary>
    public double HighConfidenceThreshold { get; set; } = 0.85;

    /// <summary>Gets or sets the minimum confidence threshold for migrations.</summary>
    public double MigrationConfidenceThreshold { get; set; } = 0.75;

    /// <summary>Gets or sets whether to enable autonomous optimization.</summary>
    public bool EnableAutonomousOptimization { get; set; } = true;
}

#endregion

#region Types

/// <summary>
/// Type of tiering decision.
/// </summary>
public enum TieringDecisionType
{
    /// <summary>Initial tier assignment.</summary>
    Initial,
    /// <summary>Migration between tiers.</summary>
    Migration,
    /// <summary>Optimization-driven change.</summary>
    Optimization,
    /// <summary>Manual change.</summary>
    Manual
}

/// <summary>
/// Current tier assignment for a resource.
/// </summary>
public sealed class TierAssignment
{
    /// <summary>Gets or sets the resource identifier.</summary>
    public string ResourceId { get; init; } = string.Empty;

    /// <summary>Gets or sets the current storage tier.</summary>
    public StorageTier CurrentTier { get; init; }

    /// <summary>Gets or sets the previous storage tier.</summary>
    public StorageTier? PreviousTier { get; init; }

    /// <summary>Gets or sets when the tier was assigned.</summary>
    public DateTime AssignedAt { get; init; }
}

/// <summary>
/// Record of a tiering decision.
/// </summary>
public sealed class TieringDecision
{
    /// <summary>Gets or sets the resource identifier.</summary>
    public string ResourceId { get; init; } = string.Empty;

    /// <summary>Gets or sets when the decision was made.</summary>
    public DateTime Timestamp { get; init; }

    /// <summary>Gets or sets the source tier.</summary>
    public StorageTier FromTier { get; init; }

    /// <summary>Gets or sets the target tier.</summary>
    public StorageTier ToTier { get; init; }

    /// <summary>Gets or sets the type of decision.</summary>
    public TieringDecisionType DecisionType { get; init; }
}

/// <summary>
/// Tiering recommendation result.
/// </summary>
public sealed class TieringRecommendation
{
    /// <summary>Gets or sets the resource identifier.</summary>
    public string ResourceId { get; init; } = string.Empty;

    /// <summary>Gets or sets the recommended storage tier.</summary>
    public StorageTier RecommendedTier { get; init; }

    /// <summary>Gets or sets the confidence level (0-1).</summary>
    public double Confidence { get; init; }

    /// <summary>Gets or sets the source of the recommendation.</summary>
    public RecommendationSource Source { get; init; }

    /// <summary>Gets or sets the reason for the recommendation.</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>Gets or sets the access prediction.</summary>
    public AccessPrediction? AccessPrediction { get; init; }

    /// <summary>Gets or sets the estimated cost savings (percentage).</summary>
    public double EstimatedCostSavings { get; init; }

    /// <summary>Gets or sets additional AI insights.</summary>
    public string? AIInsights { get; init; }
}

/// <summary>
/// Result of a migration operation.
/// </summary>
public sealed class MigrationResult
{
    /// <summary>Gets or sets the resource identifier.</summary>
    public string ResourceId { get; init; } = string.Empty;

    /// <summary>Gets or sets whether the migration succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>Gets or sets the previous tier.</summary>
    public StorageTier PreviousTier { get; init; }

    /// <summary>Gets or sets the new tier.</summary>
    public StorageTier NewTier { get; init; }

    /// <summary>Gets or sets when the migration completed.</summary>
    public DateTime MigrationTime { get; init; }

    /// <summary>Gets or sets the result reason.</summary>
    public string Reason { get; init; } = string.Empty;
}

/// <summary>
/// Result of an optimization cycle.
/// </summary>
public sealed class TieringOptimizationResult
{
    /// <summary>Gets or sets the start time.</summary>
    public DateTime StartTime { get; init; }

    /// <summary>Gets or sets the end time.</summary>
    public DateTime EndTime { get; init; }

    /// <summary>Gets the duration.</summary>
    public TimeSpan Duration => EndTime - StartTime;

    /// <summary>Gets or sets the number of resources evaluated.</summary>
    public int ResourcesEvaluated { get; init; }

    /// <summary>Gets or sets the number of resources migrated.</summary>
    public int ResourcesMigrated { get; init; }

    /// <summary>Gets or sets any errors that occurred.</summary>
    public List<string> Errors { get; init; } = new();
}

/// <summary>
/// Tiering engine statistics.
/// </summary>
public sealed class TieringStatistics
{
    /// <summary>Gets or sets the total tracked resources.</summary>
    public int TotalTrackedResources { get; init; }

    /// <summary>Gets or sets the total assignments.</summary>
    public int TotalAssignments { get; init; }

    /// <summary>Gets or sets the tier distribution.</summary>
    public Dictionary<StorageTier, int> TierDistribution { get; init; } = new();

    /// <summary>Gets or sets the total migrations.</summary>
    public long TotalMigrations { get; init; }

    /// <summary>Gets or sets the successful migrations.</summary>
    public long SuccessfulMigrations { get; init; }

    /// <summary>Gets or sets the migration success rate.</summary>
    public double MigrationSuccessRate { get; init; }

    /// <summary>Gets or sets recent decisions.</summary>
    public List<TieringDecision> RecentDecisions { get; init; } = new();
}

internal sealed class AITieringResponse
{
    public StorageTier RecommendedTier { get; init; }
    public double Confidence { get; init; }
    public string Reason { get; init; } = string.Empty;
    public string? AdditionalInsights { get; init; }
}

#endregion
