using DataWarehouse.SDK.AI;
using DataWarehouse.Plugins.AutonomousDataManagement.Models;
using DataWarehouse.Plugins.AutonomousDataManagement.Providers;
using System.Collections.Concurrent;
using System.Text.Json;

namespace DataWarehouse.Plugins.AutonomousDataManagement.Engine;

/// <summary>
/// Self-optimizing tiering engine that uses ML and AI to determine optimal storage placement.
/// </summary>
public sealed class TieringEngine : IDisposable
{
    private readonly AIProviderSelector _providerSelector;
    private readonly AccessPatternPredictor _accessPredictor;
    private readonly ConcurrentDictionary<string, TierAssignment> _assignments;
    private readonly ConcurrentQueue<TieringDecision> _decisionHistory;
    private readonly SemaphoreSlim _migrationLock;
    private readonly TieringEngineConfig _config;
    private readonly Timer _optimizationTimer;
    private long _totalMigrations;
    private long _successfulMigrations;
    private bool _disposed;

    public TieringEngine(AIProviderSelector providerSelector, TieringEngineConfig? config = null)
    {
        _providerSelector = providerSelector ?? throw new ArgumentNullException(nameof(providerSelector));
        _config = config ?? new TieringEngineConfig();
        _accessPredictor = new AccessPatternPredictor(_config.HistorySize);
        _assignments = new ConcurrentDictionary<string, TierAssignment>();
        _decisionHistory = new ConcurrentQueue<TieringDecision>();
        _migrationLock = new SemaphoreSlim(_config.MaxConcurrentMigrations);

        _optimizationTimer = new Timer(
            _ => _ = OptimizationCycleAsync(CancellationToken.None),
            null,
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(_config.OptimizationIntervalMinutes));
    }

    /// <summary>
    /// Records a data access event.
    /// </summary>
    public void RecordAccess(string resourceId, AccessEventType eventType, long sizeBytes, Dictionary<string, object>? metadata = null)
    {
        _accessPredictor.RecordAccess(resourceId, eventType, sizeBytes, metadata);
    }

    /// <summary>
    /// Gets the recommended tier for a resource using ML + AI analysis.
    /// </summary>
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

        // For lower confidence, enhance with AI analysis
        try
        {
            var aiRecommendation = await GetAIEnhancedRecommendationAsync(resourceId, mlRecommendation, accessPrediction, ct);
            return aiRecommendation;
        }
        catch (Exception)
        {
            // Fall back to ML recommendation if AI fails
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
    }

    /// <summary>
    /// Executes a tier migration for a resource.
    /// </summary>
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
    public async Task<OptimizationCycleResult> OptimizationCycleAsync(CancellationToken ct = default)
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

        return new OptimizationCycleResult
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

    private async Task<TieringRecommendation> GetAIEnhancedRecommendationAsync(
        string resourceId,
        TierRecommendation mlRecommendation,
        AccessPrediction accessPrediction,
        CancellationToken ct)
    {
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

        var response = await _providerSelector.ExecuteWithFailoverAsync(
            AIOpsCapabilities.TieringOptimization,
            prompt,
            null,
            ct);

        if (response.Success && !string.IsNullOrEmpty(response.Content))
        {
            try
            {
                // Parse AI response
                var aiResult = ParseAITieringResponse(response.Content);

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
        // Extract JSON from response
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _optimizationTimer.Dispose();
        _migrationLock.Dispose();
    }
}

#region Supporting Types

public sealed class TieringEngineConfig
{
    public int HistorySize { get; set; } = 1000;
    public int MaxConcurrentMigrations { get; set; } = 10;
    public int OptimizationIntervalMinutes { get; set; } = 15;
    public int MaxResourcesPerCycle { get; set; } = 100;
    public double HighConfidenceThreshold { get; set; } = 0.85;
    public double MigrationConfidenceThreshold { get; set; } = 0.75;
}

public sealed class TierAssignment
{
    public string ResourceId { get; init; } = string.Empty;
    public StorageTier CurrentTier { get; init; }
    public StorageTier? PreviousTier { get; init; }
    public DateTime AssignedAt { get; init; }
}

public sealed class TieringDecision
{
    public string ResourceId { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; }
    public StorageTier FromTier { get; init; }
    public StorageTier ToTier { get; init; }
    public TieringDecisionType DecisionType { get; init; }
}

public enum TieringDecisionType
{
    Initial,
    Migration,
    Optimization,
    Manual
}

public enum RecommendationSource
{
    MLModel,
    AIEnhanced,
    RuleBased,
    Manual
}

public sealed class TieringRecommendation
{
    public string ResourceId { get; init; } = string.Empty;
    public StorageTier RecommendedTier { get; init; }
    public double Confidence { get; init; }
    public RecommendationSource Source { get; init; }
    public string Reason { get; init; } = string.Empty;
    public AccessPrediction? AccessPrediction { get; init; }
    public double EstimatedCostSavings { get; init; }
    public string? AIInsights { get; init; }
}

public sealed class MigrationResult
{
    public string ResourceId { get; init; } = string.Empty;
    public bool Success { get; init; }
    public StorageTier PreviousTier { get; init; }
    public StorageTier NewTier { get; init; }
    public DateTime MigrationTime { get; init; }
    public string Reason { get; init; } = string.Empty;
}

public sealed class OptimizationCycleResult
{
    public DateTime StartTime { get; init; }
    public DateTime EndTime { get; init; }
    public int ResourcesEvaluated { get; init; }
    public int ResourcesMigrated { get; init; }
    public List<string> Errors { get; init; } = new();
    public TimeSpan Duration => EndTime - StartTime;
}

public sealed class TieringStatistics
{
    public int TotalTrackedResources { get; init; }
    public int TotalAssignments { get; init; }
    public Dictionary<StorageTier, int> TierDistribution { get; init; } = new();
    public long TotalMigrations { get; init; }
    public long SuccessfulMigrations { get; init; }
    public double MigrationSuccessRate { get; init; }
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
