using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateSustainability.Strategies.CloudOptimization;

/// <summary>
/// Selects cloud regions based on carbon intensity of the local electricity grid.
/// Routes workloads to regions with lower carbon footprint while considering
/// latency and availability requirements.
/// </summary>
public sealed class CarbonAwareRegionSelectionStrategy : SustainabilityStrategyBase
{
    private readonly BoundedDictionary<string, RegionCarbonData> _regionData = new BoundedDictionary<string, RegionCarbonData>(1000);
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
        _updateTimer = new Timer(async _ => { try { await UpdateCarbonDataAsync(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Timer callback failed: {ex.Message}"); } }, null, TimeSpan.Zero, TimeSpan.FromHours(1));
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
        // Take a thread-safe snapshot of each region's mutable carbon intensity before scoring.
        // UpdateCarbonDataAsync writes CarbonIntensity/LastUpdated under the region lock,
        // so we must read under the same lock to avoid torn reads.
        var snapshot = _regionData.Values
            .Select(r =>
            {
                double carbonIntensity;
                lock (r) { carbonIntensity = r.CarbonIntensity; }
                return (r.Provider, r.RegionId, r.LatencyMs, r.CostMultiplier, CarbonIntensity: carbonIntensity, Source: r);
            })
            .Where(s => s.Provider == provider)
            .Where(s => maxLatencyMs == null || s.LatencyMs <= maxLatencyMs)
            .ToList();

        if (preferredRegions != null)
        {
            var preferred = snapshot.Where(s => preferredRegions.Contains(s.RegionId)).ToList();
            if (preferred.Any()) snapshot = preferred;
        }

        if (!snapshot.Any())
            return new RegionSelectionResult { Success = false, Reason = "No suitable regions found" };

        // Score each region using the snapshot values
        var maxCarbon = snapshot.Max(s => s.CarbonIntensity);
        var minCarbon = snapshot.Min(s => s.CarbonIntensity);
        var maxLatency = snapshot.Max(s => s.LatencyMs);
        var maxCost = snapshot.Max(s => s.CostMultiplier);

        // Reconstruct a list of RegionCarbonData using snapshot carbon values for consistent scoring
        var candidates = snapshot.Select(s => s.Source).ToList();

        var scored = snapshot.Select(s =>
        {
            var carbonScore = maxCarbon > minCarbon ? 1 - (s.CarbonIntensity - minCarbon) / (maxCarbon - minCarbon) : 1;
            var latencyScore = maxLatency > 0 ? 1 - (double)s.LatencyMs / maxLatency : 1;
            // Guard against division by zero when all regions have the same cost multiplier (maxCost == 1.0).
            var costScore = maxCost > 1.0 ? 1 - (s.CostMultiplier - 1.0) / (maxCost - 1.0) : 1.0;

            var cw = prioritizeCarbon ? CarbonWeight : CarbonWeight * 0.5;
            var lw = prioritizeCarbon ? LatencyWeight : LatencyWeight * 1.5;

            return new
            {
                Region = s.Source,
                SnapshotCarbon = s.CarbonIntensity,
                Score = carbonScore * cw + latencyScore * lw + costScore * CostWeight
            };
        }).OrderByDescending(x => x.Score).ToList();

        var selected = scored.First().Region;
        var baseline = snapshot.OrderBy(s => s.LatencyMs).First();
        var carbonSaved = baseline.CarbonIntensity - scored.First().SnapshotCarbon;

        RecordOptimizationAction();
        if (carbonSaved > 0) RecordCarbonAvoided(carbonSaved);

        return new RegionSelectionResult
        {
            Success = true,
            SelectedRegion = selected.RegionId,
            Provider = provider,
            CarbonIntensity = scored.First().SnapshotCarbon,
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
        // Apply time-of-day variation using a local snapshot to avoid data races.
        // RegionCarbonData fields are mutated under a per-region lock so that SelectRegion
        // (which reads the same fields) doesn't observe torn state.
        var hour = DateTime.UtcNow.Hour;
        var solarFactor = hour >= 10 && hour <= 16 ? 0.8 : 1.1;
        var now = DateTimeOffset.UtcNow;

        foreach (var kvp in _regionData)
        {
            var region = kvp.Value;
            lock (region)
            {
                region.CarbonIntensity *= solarFactor;
                region.LastUpdated = now;
            }
        }

        await Task.CompletedTask;
        UpdateRecommendations();
    }

    private void UpdateRecommendations()
    {
        ClearRecommendations();
        if (!_regionData.Any()) return;

        // Read CarbonIntensity under lock to avoid racing with UpdateCarbonDataAsync
        var snapshots = _regionData.Values.Select(r => { double ci; lock (r) { ci = r.CarbonIntensity; } return (r.Name, ci); }).ToList();
        var greenest = snapshots.OrderBy(s => s.ci).First();

        AddRecommendation(new SustainabilityRecommendation
        {
            RecommendationId = $"{StrategyId}-greenest",
            Type = "GreenestRegion",
            Priority = 5,
            Description = $"Greenest region: {greenest.Name} ({greenest.ci:F0} gCO2e/kWh)",
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
