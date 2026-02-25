using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts.Storage;

namespace DataWarehouse.SDK.Storage.Services;

/// <summary>
/// Composable tier management service (AD-03).
/// Extracted from TieredStoragePluginBase to enable composition without inheritance.
/// Any storage plugin can use tier management by accepting an ITierManager dependency.
/// </summary>
public interface ITierManager
{
    /// <summary>Moves an object to the specified storage tier.</summary>
    /// <param name="key">The storage key of the object to move.</param>
    /// <param name="targetTier">The target storage tier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The new key/location of the object after the move.</returns>
    Task<string> MoveToTierAsync(string key, StorageTier targetTier, CancellationToken ct = default);

    /// <summary>Gets the current tier of an object.</summary>
    /// <param name="key">The storage key of the object.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The current storage tier of the object.</returns>
    Task<StorageTier> GetCurrentTierAsync(string key, CancellationToken ct = default);

    /// <summary>Gets tier statistics (object count, size per tier).</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Aggregated statistics across all tiers.</returns>
    Task<TierStatistics> GetTierStatisticsAsync(CancellationToken ct = default);

    /// <summary>Evaluates whether an object should be moved based on access patterns.</summary>
    /// <param name="key">The storage key of the object to evaluate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A recommendation if the object should be moved, or null if no change is recommended.</returns>
    Task<TierRecommendation?> EvaluateTierPlacementAsync(string key, CancellationToken ct = default);
}

/// <summary>
/// Aggregated statistics across storage tiers.
/// </summary>
public record TierStatistics
{
    /// <summary>Number of objects in each tier.</summary>
    public IReadOnlyDictionary<StorageTier, long> ObjectCounts { get; init; } = new Dictionary<StorageTier, long>();

    /// <summary>Total size in bytes per tier.</summary>
    public IReadOnlyDictionary<StorageTier, long> TotalSizeBytes { get; init; } = new Dictionary<StorageTier, long>();
}

/// <summary>
/// Recommendation for moving an object to a different tier.
/// </summary>
public record TierRecommendation
{
    /// <summary>The storage key of the object.</summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>The current tier of the object.</summary>
    public StorageTier CurrentTier { get; init; }

    /// <summary>The recommended tier for the object.</summary>
    public StorageTier RecommendedTier { get; init; }

    /// <summary>Reason for the recommendation.</summary>
    public string Reason { get; init; } = string.Empty;
}
