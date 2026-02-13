using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts.Storage;

namespace DataWarehouse.SDK.Storage.Services;

/// <summary>
/// Default in-memory tier manager implementation (AD-03).
/// Tracks object tier assignments and provides time-based placement recommendations.
/// Actual data movement between tiers is delegated to the storage backend.
/// </summary>
public sealed class DefaultTierManager : ITierManager
{
    private readonly ConcurrentDictionary<string, TierEntry> _tierMap = new();

    /// <summary>Days since last access before recommending Cool tier. Default: 30.</summary>
    public int CoolThresholdDays { get; set; } = 30;

    /// <summary>Days since last access before recommending Cold tier. Default: 90.</summary>
    public int ColdThresholdDays { get; set; } = 90;

    /// <summary>Days since last access before recommending Archive tier. Default: 365.</summary>
    public int ArchiveThresholdDays { get; set; } = 365;

    /// <inheritdoc/>
    public Task<string> MoveToTierAsync(string key, StorageTier targetTier, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        _tierMap.AddOrUpdate(
            key,
            _ => new TierEntry { Tier = targetTier, LastAccessed = DateTime.UtcNow, Size = 0 },
            (_, existing) => existing with { Tier = targetTier, LastAccessed = DateTime.UtcNow });

        return Task.FromResult(key);
    }

    /// <inheritdoc/>
    public Task<StorageTier> GetCurrentTierAsync(string key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (_tierMap.TryGetValue(key, out var entry))
            return Task.FromResult(entry.Tier);

        return Task.FromResult(StorageTier.Hot);
    }

    /// <inheritdoc/>
    public Task<TierStatistics> GetTierStatisticsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var entries = _tierMap.Values.ToList();

        var objectCounts = entries
            .GroupBy(e => e.Tier)
            .ToDictionary(g => g.Key, g => (long)g.Count());

        var totalSizeBytes = entries
            .GroupBy(e => e.Tier)
            .ToDictionary(g => g.Key, g => g.Sum(e => e.Size));

        return Task.FromResult(new TierStatistics
        {
            ObjectCounts = objectCounts,
            TotalSizeBytes = totalSizeBytes
        });
    }

    /// <inheritdoc/>
    public Task<TierRecommendation?> EvaluateTierPlacementAsync(string key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (!_tierMap.TryGetValue(key, out var entry))
            return Task.FromResult<TierRecommendation?>(null);

        var daysSinceAccess = (DateTime.UtcNow - entry.LastAccessed).TotalDays;

        StorageTier recommended;
        string reason;

        if (daysSinceAccess >= ArchiveThresholdDays)
        {
            recommended = StorageTier.Archive;
            reason = $"Not accessed in {(int)daysSinceAccess} days (threshold: {ArchiveThresholdDays})";
        }
        else if (daysSinceAccess >= ColdThresholdDays)
        {
            recommended = StorageTier.Cold;
            reason = $"Not accessed in {(int)daysSinceAccess} days (threshold: {ColdThresholdDays})";
        }
        else if (daysSinceAccess >= CoolThresholdDays)
        {
            recommended = StorageTier.Warm;
            reason = $"Not accessed in {(int)daysSinceAccess} days (threshold: {CoolThresholdDays})";
        }
        else
        {
            // No change recommended
            return Task.FromResult<TierRecommendation?>(null);
        }

        // Only recommend if it would actually move to a different tier
        if (recommended == entry.Tier)
            return Task.FromResult<TierRecommendation?>(null);

        return Task.FromResult<TierRecommendation?>(new TierRecommendation
        {
            Key = key,
            CurrentTier = entry.Tier,
            RecommendedTier = recommended,
            Reason = reason
        });
    }

    /// <summary>
    /// Records an access for tracking purposes (updates last accessed time).
    /// </summary>
    /// <param name="key">The storage key that was accessed.</param>
    /// <param name="size">The object size in bytes (optional, for statistics).</param>
    public void RecordAccess(string key, long size = 0)
    {
        _tierMap.AddOrUpdate(
            key,
            _ => new TierEntry { Tier = StorageTier.Hot, LastAccessed = DateTime.UtcNow, Size = size },
            (_, existing) => existing with { LastAccessed = DateTime.UtcNow, Size = size > 0 ? size : existing.Size });
    }

    private record TierEntry
    {
        public StorageTier Tier { get; init; }
        public DateTime LastAccessed { get; init; }
        public long Size { get; init; }
    }
}
