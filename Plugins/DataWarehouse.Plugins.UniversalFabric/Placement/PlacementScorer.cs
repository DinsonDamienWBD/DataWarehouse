using DataWarehouse.SDK.Storage.Fabric;
using System;
using System.Collections.Generic;
using System.Linq;

using StorageCapabilities = DataWarehouse.SDK.Contracts.Storage.StorageCapabilities;
using StorageHealthInfo = DataWarehouse.SDK.Contracts.Storage.StorageHealthInfo;
using StorageTier = DataWarehouse.SDK.Contracts.Storage.StorageTier;
using HealthStatus = DataWarehouse.SDK.Contracts.Storage.HealthStatus;

namespace DataWarehouse.Plugins.UniversalFabric.Placement;

/// <summary>
/// Configurable weights for the multi-factor placement scoring algorithm.
/// Each weight controls the relative importance of a scoring factor.
/// All weights should be non-negative and ideally sum to 1.0 for normalized scoring.
/// </summary>
public record PlacementWeights
{
    /// <summary>
    /// Weight for tier match scoring (exact tier match vs adjacent tier vs mismatch).
    /// Default: 0.25.
    /// </summary>
    public double TierWeight { get; init; } = 0.25;

    /// <summary>
    /// Weight for tag match scoring (proportion of required tags present on backend).
    /// Default: 0.20.
    /// </summary>
    public double TagWeight { get; init; } = 0.20;

    /// <summary>
    /// Weight for region match scoring (preferred region match).
    /// Default: 0.15.
    /// </summary>
    public double RegionWeight { get; init; } = 0.15;

    /// <summary>
    /// Weight for capacity scoring (available capacity relative to expected object size).
    /// Default: 0.15.
    /// </summary>
    public double CapacityWeight { get; init; } = 0.15;

    /// <summary>
    /// Weight for priority scoring (lower backend priority values score higher).
    /// Default: 0.10.
    /// </summary>
    public double PriorityWeight { get; init; } = 0.10;

    /// <summary>
    /// Weight for health scoring (healthy/degraded/unhealthy status).
    /// Default: 0.10.
    /// </summary>
    public double HealthWeight { get; init; } = 0.10;

    /// <summary>
    /// Weight for capability match scoring (encryption, versioning, etc.).
    /// Default: 0.05.
    /// </summary>
    public double CapabilityWeight { get; init; } = 0.05;
}

/// <summary>
/// Multi-factor scoring engine that evaluates storage backends against placement hints
/// and health information to produce a normalized score between 0.0 and 1.0.
/// </summary>
/// <remarks>
/// <para>
/// The scorer evaluates seven factors: tier match, tag match, region match, available capacity,
/// backend priority, health status, and capability match. Each factor produces a sub-score
/// between 0.0 and 1.0, which is then weighted according to the configured <see cref="PlacementWeights"/>.
/// </para>
/// <para>
/// The scorer is stateless and thread-safe. Create one instance and reuse across all
/// concurrent placement operations.
/// </para>
/// </remarks>
public class PlacementScorer
{
    private readonly PlacementWeights _weights;

    /// <summary>
    /// Maximum priority value used for normalization. Backends with priority >= this value score 0.0.
    /// </summary>
    private const int MaxPriorityForNormalization = 1000;

    /// <summary>
    /// Initializes a new instance of <see cref="PlacementScorer"/> with optional custom weights.
    /// </summary>
    /// <param name="weights">Custom scoring weights, or null to use defaults.</param>
    public PlacementScorer(PlacementWeights? weights = null)
    {
        _weights = weights ?? new PlacementWeights();
    }

    /// <summary>
    /// Gets the configured scoring weights.
    /// </summary>
    public PlacementWeights Weights => _weights;

    /// <summary>
    /// Scores a backend for a given placement request, producing a value between 0.0 and 1.0.
    /// Higher scores indicate better placement suitability.
    /// </summary>
    /// <param name="backend">The backend descriptor to evaluate.</param>
    /// <param name="hints">The placement hints describing the desired placement characteristics.</param>
    /// <param name="health">Optional health information for the backend. Null assumes healthy.</param>
    /// <returns>A normalized score between 0.0 and 1.0.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="backend"/> or <paramref name="hints"/> is null.</exception>
    public double Score(BackendDescriptor backend, StoragePlacementHints hints, StorageHealthInfo? health)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(hints);

        var breakdown = ScoreWithBreakdown(backend, hints, health);
        return breakdown.TotalScore;
    }

    /// <summary>
    /// Scores a backend and returns a detailed breakdown of each scoring factor.
    /// </summary>
    /// <param name="backend">The backend descriptor to evaluate.</param>
    /// <param name="hints">The placement hints describing desired characteristics.</param>
    /// <param name="health">Optional health information for the backend. Null assumes healthy.</param>
    /// <returns>A score breakdown with per-factor scores and the weighted total.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="backend"/> or <paramref name="hints"/> is null.</exception>
    public ScoreBreakdown ScoreWithBreakdown(BackendDescriptor backend, StoragePlacementHints hints, StorageHealthInfo? health)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(hints);

        var tierScore = ScoreTier(backend.Tier, hints.PreferredTier);
        var tagScore = ScoreTags(backend.Tags, hints.RequiredTags);
        var regionScore = ScoreRegion(backend.Region, hints.PreferredRegion);
        var capacityScore = ScoreCapacity(backend.MaxCapacityBytes, health?.AvailableCapacity, hints.ExpectedSizeBytes);
        var priorityScore = ScorePriority(backend.Priority);
        var healthScore = ScoreHealth(health);
        var capabilityScore = ScoreCapabilities(backend.Capabilities, hints);

        var totalScore =
            tierScore * _weights.TierWeight +
            tagScore * _weights.TagWeight +
            regionScore * _weights.RegionWeight +
            capacityScore * _weights.CapacityWeight +
            priorityScore * _weights.PriorityWeight +
            healthScore * _weights.HealthWeight +
            capabilityScore * _weights.CapabilityWeight;

        return new ScoreBreakdown
        {
            TierScore = tierScore,
            TagScore = tagScore,
            RegionScore = regionScore,
            CapacityScore = capacityScore,
            PriorityScore = priorityScore,
            HealthScore = healthScore,
            CapabilityScore = capabilityScore,
            TotalScore = Math.Clamp(totalScore, 0.0, 1.0)
        };
    }

    /// <summary>
    /// Scores tier match: 1.0 for exact match, 0.5 for adjacent tier, 0.0 for distant mismatch.
    /// If no preferred tier is specified, all tiers score 1.0.
    /// </summary>
    private static double ScoreTier(StorageTier backendTier, StorageTier? preferredTier)
    {
        if (preferredTier is null)
            return 1.0;

        if (backendTier == preferredTier.Value)
            return 1.0;

        var distance = Math.Abs((int)backendTier - (int)preferredTier.Value);
        return distance == 1 ? 0.5 : 0.0;
    }

    /// <summary>
    /// Scores tag match: proportion of required tags present on the backend.
    /// If no required tags, scores 1.0.
    /// </summary>
    private static double ScoreTags(IReadOnlySet<string> backendTags, IReadOnlySet<string>? requiredTags)
    {
        if (requiredTags is null || requiredTags.Count == 0)
            return 1.0;

        if (backendTags.Count == 0)
            return 0.0;

        var matchCount = requiredTags.Count(tag => backendTags.Contains(tag));
        return (double)matchCount / requiredTags.Count;
    }

    /// <summary>
    /// Scores region match: 1.0 if matches preferred region, 0.5 if no preference or no region on backend.
    /// </summary>
    private static double ScoreRegion(string? backendRegion, string? preferredRegion)
    {
        if (preferredRegion is null)
            return 1.0;

        if (backendRegion is null)
            return 0.5;

        return string.Equals(backendRegion, preferredRegion, StringComparison.OrdinalIgnoreCase)
            ? 1.0
            : 0.3;
    }

    /// <summary>
    /// Scores capacity: ratio of available capacity to expected size, capped at 1.0.
    /// If capacity is unknown or no expected size, scores 0.75 (neutral).
    /// </summary>
    private static double ScoreCapacity(long? maxCapacity, long? availableCapacity, long? expectedSize)
    {
        if (expectedSize is null || expectedSize.Value <= 0)
            return 0.75;

        if (availableCapacity is not null)
        {
            if (availableCapacity.Value <= 0)
                return 0.0;

            var ratio = (double)availableCapacity.Value / expectedSize.Value;
            return Math.Clamp(ratio / 10.0, 0.0, 1.0); // 10x headroom = 1.0
        }

        if (maxCapacity is not null)
        {
            // If we only know max capacity (no used info), assume 50% utilization
            var estimatedAvailable = maxCapacity.Value / 2;
            if (estimatedAvailable <= 0)
                return 0.0;

            var ratio = (double)estimatedAvailable / expectedSize.Value;
            return Math.Clamp(ratio / 10.0, 0.0, 1.0);
        }

        return 0.75; // Unknown capacity, neutral score
    }

    /// <summary>
    /// Scores priority: normalized as 1.0 - (priority / maxPriority). Lower priority values score higher.
    /// </summary>
    private static double ScorePriority(int priority)
    {
        var normalized = (double)Math.Clamp(priority, 0, MaxPriorityForNormalization) / MaxPriorityForNormalization;
        return 1.0 - normalized;
    }

    /// <summary>
    /// Scores health: 1.0 for healthy, 0.5 for degraded, 0.0 for unhealthy.
    /// Null health info assumes healthy (1.0).
    /// </summary>
    private static double ScoreHealth(StorageHealthInfo? health)
    {
        if (health is null)
            return 1.0;

        return health.Status switch
        {
            HealthStatus.Healthy => 1.0,
            HealthStatus.Degraded => 0.5,
            _ => 0.0
        };
    }

    /// <summary>
    /// Scores capability match: 1.0 if all required capabilities are met, reduced proportionally for misses.
    /// </summary>
    private static double ScoreCapabilities(StorageCapabilities capabilities, StoragePlacementHints hints)
    {
        var requirements = 0;
        var met = 0;

        if (hints.RequireEncryption)
        {
            requirements++;
            if (capabilities.SupportsEncryption)
                met++;
        }

        if (hints.RequireVersioning)
        {
            requirements++;
            if (capabilities.SupportsVersioning)
                met++;
        }

        if (requirements == 0)
            return 1.0;

        return (double)met / requirements;
    }
}

/// <summary>
/// Detailed breakdown of a backend placement score, showing each factor's individual score.
/// </summary>
public record ScoreBreakdown
{
    /// <summary>Score for tier match (0.0 to 1.0).</summary>
    public double TierScore { get; init; }

    /// <summary>Score for tag match (0.0 to 1.0).</summary>
    public double TagScore { get; init; }

    /// <summary>Score for region match (0.0 to 1.0).</summary>
    public double RegionScore { get; init; }

    /// <summary>Score for available capacity (0.0 to 1.0).</summary>
    public double CapacityScore { get; init; }

    /// <summary>Score for backend priority (0.0 to 1.0).</summary>
    public double PriorityScore { get; init; }

    /// <summary>Score for health status (0.0 to 1.0).</summary>
    public double HealthScore { get; init; }

    /// <summary>Score for capability match (0.0 to 1.0).</summary>
    public double CapabilityScore { get; init; }

    /// <summary>Weighted total score (0.0 to 1.0).</summary>
    public double TotalScore { get; init; }

    /// <summary>
    /// Converts the breakdown to a dictionary for serialization and observability.
    /// </summary>
    public IReadOnlyDictionary<string, double> ToDictionary() => new Dictionary<string, double>
    {
        ["tier"] = TierScore,
        ["tags"] = TagScore,
        ["region"] = RegionScore,
        ["capacity"] = CapacityScore,
        ["priority"] = PriorityScore,
        ["health"] = HealthScore,
        ["capability"] = CapabilityScore,
        ["total"] = TotalScore
    };
}
