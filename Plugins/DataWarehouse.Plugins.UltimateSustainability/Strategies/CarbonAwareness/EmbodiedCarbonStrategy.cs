namespace DataWarehouse.Plugins.UltimateSustainability.Strategies.CarbonAwareness;

/// <summary>
/// Tracks and optimizes embodied carbon in hardware and infrastructure.
/// Considers the lifecycle carbon footprint of servers, storage, and networking equipment.
/// </summary>
public sealed class EmbodiedCarbonStrategy : SustainabilityStrategyBase
{
    private readonly Dictionary<string, HardwareAsset> _assets = new();
    private readonly object _lock = new();

    /// <inheritdoc/>
    public override string StrategyId => "embodied-carbon";
    /// <inheritdoc/>
    public override string DisplayName => "Embodied Carbon Tracking";
    /// <inheritdoc/>
    public override SustainabilityCategory Category => SustainabilityCategory.CarbonAwareness;
    /// <inheritdoc/>
    public override SustainabilityCapabilities Capabilities =>
        SustainabilityCapabilities.CarbonCalculation | SustainabilityCapabilities.Reporting;
    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Tracks embodied carbon in hardware lifecycle including manufacturing, transport, and disposal.";
    /// <inheritdoc/>
    public override string[] Tags => new[] { "carbon", "embodied", "lifecycle", "hardware", "scope3" };

    /// <summary>Default hardware lifespan in years.</summary>
    public int DefaultLifespanYears { get; set; } = 5;

    /// <summary>Registers a hardware asset.</summary>
    public void RegisterAsset(string assetId, string assetType, double embodiedCarbonKg, DateTimeOffset purchaseDate, int? lifespanYears = null)
    {
        lock (_lock)
        {
            _assets[assetId] = new HardwareAsset
            {
                AssetId = assetId,
                AssetType = assetType,
                EmbodiedCarbonKg = embodiedCarbonKg,
                PurchaseDate = purchaseDate,
                LifespanYears = lifespanYears ?? DefaultLifespanYears
            };
        }
        RecordOptimizationAction();
    }

    /// <summary>Calculates amortized daily carbon for an asset.</summary>
    public double GetDailyCarbonGrams(string assetId)
    {
        lock (_lock)
        {
            if (!_assets.TryGetValue(assetId, out var asset)) return 0;
            var totalDays = asset.LifespanYears * 365;
            return (asset.EmbodiedCarbonKg * 1000) / totalDays;
        }
    }

    /// <summary>Gets total embodied carbon across all assets.</summary>
    public double GetTotalEmbodiedCarbonKg()
    {
        lock (_lock) return _assets.Values.Sum(a => a.EmbodiedCarbonKg);
    }

    /// <summary>Gets total amortized daily carbon.</summary>
    public double GetTotalDailyCarbonGrams()
    {
        lock (_lock) return _assets.Values.Sum(a => (a.EmbodiedCarbonKg * 1000) / (a.LifespanYears * 365));
    }

    /// <summary>Gets assets nearing end of life.</summary>
    public IReadOnlyList<HardwareAsset> GetAssetsNearingEndOfLife(int monthsThreshold = 6)
    {
        var threshold = DateTimeOffset.UtcNow.AddMonths(-monthsThreshold);
        lock (_lock)
        {
            return _assets.Values
                .Where(a => a.PurchaseDate.AddYears(a.LifespanYears) <= threshold)
                .OrderBy(a => a.PurchaseDate.AddYears(a.LifespanYears))
                .ToList();
        }
    }
}

/// <summary>Hardware asset information.</summary>
public sealed class HardwareAsset
{
    public required string AssetId { get; init; }
    public required string AssetType { get; init; }
    public required double EmbodiedCarbonKg { get; init; }
    public required DateTimeOffset PurchaseDate { get; init; }
    public required int LifespanYears { get; init; }
}
