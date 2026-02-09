using System.Collections.Concurrent;

namespace DataWarehouse.Plugins.UltimateSustainability.Strategies.CloudOptimization;

/// <summary>
/// Selects cloud regions based on carbon intensity of the local electricity grid.
/// Routes workloads to regions with lower carbon footprint while considering
/// latency and availability requirements.
/// </summary>
public sealed class CarbonAwareRegionSelectionStrategy : SustainabilityStrategyBase
{
    private readonly ConcurrentDictionary<string, RegionCarbonData> _regionData = new();
    private Timer? _updateTimer;

    /// <inheritdoc/>
    public override string StrategyId => "carbon-aware-region-selection";
    /// <inheritdoc/>
    public override string DisplayName => "Carbon-Aware Region Selection";
    /// <inheritdoc/>
    public override SustainabilityCategory Category => SustainabilityCategory.CloudOptimization;
    /// <inheritdoc/>
    public override SustainabilityCapabilities Capabilities =>
        SustainabilityCapabilities.CarbonCalculation | SustainabilityCapabilities.ExternalIntegration | SustainabilityCapabilities.PredictiveAnalytics;
    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Selects cloud regions based on grid carbon intensity to minimize workload carbon footprint.";
    /// <inheritdoc/>
    public override string[] Tags => new[] { "cloud", "region", "carbon", "routing", "green" };

    /// <summary>Maximum acceptable latency (ms).</summary>
    public int MaxLatencyMs { get; set; } = 100;

    /// <summary>Weight for carbon intensity in selection (0-1).</summary>
    public double CarbonWeight { get; set; } = 0.7;

    /// <summary>Weight for latency in selection (0-1).</summary>
    public double LatencyWeight { get; set; } = 0.2;

    /// <summary>Weight for cost in selection (0-1).</summary>
    public double CostWeight { get; set; } = 0.1;

    /// <inheritdoc/>
    protected override Task InitializeCoreAsync(CancellationToken ct)
    {
        InitializeRegionData();
        _updateTimer = new Timer(async _ => await UpdateCarbonDataAsync(), null, TimeSpan.Zero, TimeSpan.FromHours(1));
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task DisposeCoreAsync()
    {
        _updateTimer?.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Selects optimal region for a workload.</summary>
    public RegionSelectionResult SelectRegion(
        string provider,
        IEnumerable<string>? preferredRegions = null,
        int? maxLatencyMs = null,
        bool prioritizeCarbon = true)
    {
        var candidates = _regionData.Values
            .Where(r => r.Provider == provider)
            .Where(r => maxLatencyMs == null || r.LatencyMs <= maxLatencyMs)
            .ToList();

        if (preferredRegions != null)
        {
            var preferred = candidates.Where(r => preferredRegions.Contains(r.RegionId)).ToList();
            if (preferred.Any()) candidates = preferred;
        }

        if (!candidates.Any())
            return new RegionSelectionResult { Success = false, Reason = "No suitable regions found" };

        // Score each region
        var maxCarbon = candidates.Max(r => r.CarbonIntensity);
        var minCarbon = candidates.Min(r => r.CarbonIntensity);
        var maxLatency = candidates.Max(r => r.LatencyMs);
        var maxCost = candidates.Max(r => r.CostMultiplier);

        var scored = candidates.Select(r =>
        {
            var carbonScore = maxCarbon > minCarbon ? 1 - (r.CarbonIntensity - minCarbon) / (maxCarbon - minCarbon) : 1;
            var latencyScore = maxLatency > 0 ? 1 - r.LatencyMs / maxLatency : 1;
            var costScore = maxCost > 0 ? 1 - (r.CostMultiplier - 1) / (maxCost - 1) : 1;

            var cw = prioritizeCarbon ? CarbonWeight : CarbonWeight * 0.5;
            var lw = prioritizeCarbon ? LatencyWeight : LatencyWeight * 1.5;

            return new
            {
                Region = r,
                Score = carbonScore * cw + latencyScore * lw + costScore * CostWeight
            };
        }).OrderByDescending(x => x.Score).ToList();

        var selected = scored.First().Region;
        var baseline = candidates.OrderBy(r => r.LatencyMs).First();
        var carbonSaved = baseline.CarbonIntensity - selected.CarbonIntensity;

        RecordOptimizationAction();
        if (carbonSaved > 0) RecordCarbonAvoided(carbonSaved);

        return new RegionSelectionResult
        {
            Success = true,
            SelectedRegion = selected.RegionId,
            Provider = provider,
            CarbonIntensity = selected.CarbonIntensity,
            LatencyMs = selected.LatencyMs,
            CostMultiplier = selected.CostMultiplier,
            CarbonSavedGCO2ePerKwh = carbonSaved,
            AlternativeRegions = scored.Skip(1).Take(3).Select(x => x.Region.RegionId).ToList()
        };
    }

    /// <summary>Gets all region carbon data.</summary>
    public IReadOnlyList<RegionCarbonData> GetRegionData()
    {
        return _regionData.Values.OrderBy(r => r.CarbonIntensity).ToList().AsReadOnly();
    }

    private void InitializeRegionData()
    {
        // AWS regions
        AddRegion("AWS", "us-west-2", "US West (Oregon)", 250, 20, 1.0);
        AddRegion("AWS", "us-east-1", "US East (N. Virginia)", 380, 10, 1.0);
        AddRegion("AWS", "eu-west-1", "Europe (Ireland)", 300, 80, 1.05);
        AddRegion("AWS", "eu-north-1", "Europe (Stockholm)", 50, 90, 1.1);
        AddRegion("AWS", "ca-central-1", "Canada (Central)", 120, 30, 1.05);
        AddRegion("AWS", "ap-southeast-2", "Asia Pacific (Sydney)", 550, 150, 1.1);

        // GCP regions
        AddRegion("GCP", "us-west1", "US West (Oregon)", 250, 20, 1.0);
        AddRegion("GCP", "us-central1", "US Central (Iowa)", 400, 25, 1.0);
        AddRegion("GCP", "europe-north1", "Europe (Finland)", 100, 85, 1.05);
        AddRegion("GCP", "europe-west1", "Europe (Belgium)", 180, 80, 1.0);

        // Azure regions
        AddRegion("Azure", "westus2", "West US 2", 280, 20, 1.0);
        AddRegion("Azure", "eastus", "East US", 370, 10, 1.0);
        AddRegion("Azure", "northeurope", "North Europe", 300, 80, 1.05);
        AddRegion("Azure", "swedencentral", "Sweden Central", 30, 90, 1.1);
    }

    private void AddRegion(string provider, string regionId, string name, double carbonIntensity, int latencyMs, double costMultiplier)
    {
        var key = $"{provider}:{regionId}";
        _regionData[key] = new RegionCarbonData
        {
            Provider = provider,
            RegionId = regionId,
            Name = name,
            CarbonIntensity = carbonIntensity,
            LatencyMs = latencyMs,
            CostMultiplier = costMultiplier,
            LastUpdated = DateTimeOffset.UtcNow
        };
    }

    private async Task UpdateCarbonDataAsync()
    {
        // Would fetch real-time carbon data from APIs
        // Simulating time-of-day variation
        var hour = DateTime.UtcNow.Hour;
        var solarFactor = hour >= 10 && hour <= 16 ? 0.8 : 1.1;

        foreach (var region in _regionData.Values)
        {
            region.CarbonIntensity *= solarFactor;
            region.LastUpdated = DateTimeOffset.UtcNow;
        }

        await Task.CompletedTask;
        UpdateRecommendations();
    }

    private void UpdateRecommendations()
    {
        ClearRecommendations();
        var greenestRegion = _regionData.Values.OrderBy(r => r.CarbonIntensity).First();
        AddRecommendation(new SustainabilityRecommendation
        {
            RecommendationId = $"{StrategyId}-greenest",
            Type = "GreenestRegion",
            Priority = 5,
            Description = $"Greenest region: {greenestRegion.Name} ({greenestRegion.CarbonIntensity:F0} gCO2e/kWh)",
            CanAutoApply = false
        });
    }
}

/// <summary>Region carbon data.</summary>
public sealed class RegionCarbonData
{
    public required string Provider { get; init; }
    public required string RegionId { get; init; }
    public required string Name { get; init; }
    public double CarbonIntensity { get; set; }
    public required int LatencyMs { get; init; }
    public required double CostMultiplier { get; init; }
    public DateTimeOffset LastUpdated { get; set; }
}

/// <summary>Region selection result.</summary>
public sealed record RegionSelectionResult
{
    public required bool Success { get; init; }
    public string? Reason { get; init; }
    public string? SelectedRegion { get; init; }
    public string? Provider { get; init; }
    public double CarbonIntensity { get; init; }
    public int LatencyMs { get; init; }
    public double CostMultiplier { get; init; }
    public double CarbonSavedGCO2ePerKwh { get; init; }
    public List<string>? AlternativeRegions { get; init; }
}
