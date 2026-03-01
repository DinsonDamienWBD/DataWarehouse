namespace DataWarehouse.Plugins.UltimateSustainability.Strategies.EnergyOptimization;

/// <summary>
/// Optimizes storage power by tiering data across hot, warm, and cold storage.
/// Moves infrequently accessed data to lower-power storage tiers.
/// </summary>
public sealed class StorageTieringStrategy : SustainabilityStrategyBase
{
    private readonly Dictionary<string, StorageTier> _tiers = new();
    private readonly Dictionary<string, DataObjectInfo> _objects = new();
    private readonly object _lock = new();

    /// <inheritdoc/>
    public override string StrategyId => "storage-tiering";
    /// <inheritdoc/>
    public override string DisplayName => "Storage Tiering";
    /// <inheritdoc/>
    public override SustainabilityCategory Category => SustainabilityCategory.EnergyOptimization;
    /// <inheritdoc/>
    public override SustainabilityCapabilities Capabilities =>
        SustainabilityCapabilities.ActiveControl | SustainabilityCapabilities.Scheduling;
    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Tiers data across hot, warm, and cold storage to optimize power consumption.";
    /// <inheritdoc/>
    public override string[] Tags => new[] { "storage", "tiering", "hot", "cold", "archive", "ssd", "hdd" };

    /// <summary>Days since last access for warm tier.</summary>
    public int WarmTierThresholdDays { get; set; } = 30;
    /// <summary>Days since last access for cold tier.</summary>
    public int ColdTierThresholdDays { get; set; } = 90;

    /// <summary>Registers a storage tier.</summary>
    public void RegisterTier(string tierId, string name, TierType type, double powerWattsPerTb)
    {
        lock (_lock)
        {
            _tiers[tierId] = new StorageTier
            {
                TierId = tierId,
                Name = name,
                Type = type,
                PowerWattsPerTb = powerWattsPerTb
            };
        }
    }

    /// <summary>Tracks a data object.</summary>
    public void TrackObject(string objectId, string currentTier, long sizeBytes, DateTimeOffset lastAccessed)
    {
        // Take a snapshot of the current state inside the lock so that EvaluateTieringRecommendations
        // operates on a consistent view without holding the lock during the evaluation.
        Dictionary<string, DataObjectInfo> snapshot;
        lock (_lock)
        {
            _objects[objectId] = new DataObjectInfo
            {
                ObjectId = objectId,
                CurrentTier = currentTier,
                SizeBytes = sizeBytes,
                LastAccessed = lastAccessed,
                CreatedAt = DateTimeOffset.UtcNow
            };
            snapshot = new Dictionary<string, DataObjectInfo>(_objects);
        }
        RecordOptimizationAction();
        EvaluateTieringRecommendations(snapshot);
    }

    /// <summary>Records an access to an object.</summary>
    public void RecordAccess(string objectId)
    {
        lock (_lock)
        {
            if (_objects.TryGetValue(objectId, out var obj))
            {
                _objects[objectId] = obj with { LastAccessed = DateTimeOffset.UtcNow, AccessCount = obj.AccessCount + 1 };
            }
        }
    }

    /// <summary>Gets tier recommendations.</summary>
    public IReadOnlyList<TieringRecommendation> GetTieringRecommendations()
    {
        var recommendations = new List<TieringRecommendation>();
        var now = DateTimeOffset.UtcNow;

        lock (_lock)
        {
            foreach (var obj in _objects.Values)
            {
                var daysSinceAccess = (now - obj.LastAccessed).TotalDays;
                var recommendedTier = daysSinceAccess >= ColdTierThresholdDays ? TierType.Cold
                    : daysSinceAccess >= WarmTierThresholdDays ? TierType.Warm
                    : TierType.Hot;

                var currentTier = _tiers.TryGetValue(obj.CurrentTier, out var t) ? t.Type : TierType.Hot;

                if (recommendedTier != currentTier && recommendedTier > currentTier)
                {
                    var targetTier = _tiers.Values.FirstOrDefault(x => x.Type == recommendedTier);
                    var sourceTier = _tiers.TryGetValue(obj.CurrentTier, out var s) ? s : null;

                    if (targetTier != null && sourceTier != null)
                    {
                        var sizeInTb = obj.SizeBytes / 1_099_511_627_776.0;
                        var powerSavings = (sourceTier.PowerWattsPerTb - targetTier.PowerWattsPerTb) * sizeInTb;

                        recommendations.Add(new TieringRecommendation
                        {
                            ObjectId = obj.ObjectId,
                            CurrentTier = obj.CurrentTier,
                            RecommendedTier = targetTier.TierId,
                            DaysSinceAccess = (int)daysSinceAccess,
                            SizeBytes = obj.SizeBytes,
                            EstimatedPowerSavingsWatts = powerSavings
                        });
                    }
                }
            }
        }

        return recommendations.OrderByDescending(r => r.EstimatedPowerSavingsWatts).ToList();
    }

    /// <summary>Gets total power consumption.</summary>
    public double GetTotalPowerWatts()
    {
        lock (_lock)
        {
            double totalPower = 0;
            foreach (var obj in _objects.Values)
            {
                if (_tiers.TryGetValue(obj.CurrentTier, out var tier))
                {
                    totalPower += tier.PowerWattsPerTb * (obj.SizeBytes / 1_099_511_627_776.0);
                }
            }
            return totalPower;
        }
    }

    private void EvaluateTieringRecommendations()
    {
        ClearRecommendations();
        var recs = GetTieringRecommendations();
        var totalSavings = recs.Sum(r => r.EstimatedPowerSavingsWatts);

        if (recs.Count > 0)
        {
            AddRecommendation(new SustainabilityRecommendation
            {
                RecommendationId = $"{StrategyId}-tiering",
                Type = "StorageTiering",
                Priority = 6,
                Description = $"{recs.Count} objects can be tiered for {totalSavings:F1}W savings.",
                EstimatedEnergySavingsWh = totalSavings * 24,
                CanAutoApply = true,
                Action = "apply-tiering"
            });
        }
    }
}

/// <summary>Tier type.</summary>
public enum TierType { Hot = 0, Warm = 1, Cold = 2, Archive = 3 }

/// <summary>Storage tier information.</summary>
public sealed record StorageTier
{
    public required string TierId { get; init; }
    public required string Name { get; init; }
    public required TierType Type { get; init; }
    public required double PowerWattsPerTb { get; init; }
}

/// <summary>Data object tracking info.</summary>
public sealed record DataObjectInfo
{
    public required string ObjectId { get; init; }
    public required string CurrentTier { get; init; }
    public required long SizeBytes { get; init; }
    public required DateTimeOffset LastAccessed { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public int AccessCount { get; init; }
}

/// <summary>Tiering recommendation.</summary>
public sealed record TieringRecommendation
{
    public required string ObjectId { get; init; }
    public required string CurrentTier { get; init; }
    public required string RecommendedTier { get; init; }
    public required int DaysSinceAccess { get; init; }
    public required long SizeBytes { get; init; }
    public required double EstimatedPowerSavingsWatts { get; init; }
}
