using System.Net.Http;
using System.Text.Json;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateSustainability.Strategies;

/// <summary>
/// Renewable routing preference strategy. Routes storage/compute to regions with highest
/// renewable energy percentage using Electricity Maps API data with caching and fallback.
/// </summary>
public sealed class RenewableRoutingStrategy : SustainabilityStrategyBase
{
    private readonly BoundedDictionary<string, RenewableRegionData> _regionData = new BoundedDictionary<string, RenewableRegionData>(1000);
    private readonly BoundedDictionary<string, List<RoutingDecision>> _routingHistory = new BoundedDictionary<string, List<RoutingDecision>>(1000);
    private readonly HttpClient _httpClient;
    private string _apiToken = "";

    public override string StrategyId => "renewable-routing";
    public override string DisplayName => "Renewable Energy Routing";
    public override SustainabilityCategory Category => SustainabilityCategory.CloudOptimization;
    public override SustainabilityCapabilities Capabilities =>
        SustainabilityCapabilities.ExternalIntegration | SustainabilityCapabilities.Scheduling |
        SustainabilityCapabilities.CarbonCalculation | SustainabilityCapabilities.Reporting;
    public override string SemanticDescription =>
        "Routes workloads to regions with highest renewable energy percentage using Electricity Maps API data.";
    public override string[] Tags => new[] { "renewable", "routing", "electricity-maps", "green-energy" };

    public RenewableRoutingStrategy()
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        InitializeDefaultRegionData();
    }

    /// <summary>
    /// Configures the Electricity Maps API token.
    /// </summary>
    public void Configure(string electricityMapsApiToken)
    {
        _apiToken = electricityMapsApiToken;
        if (!string.IsNullOrEmpty(_apiToken))
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("auth-token", _apiToken);
    }

    /// <summary>
    /// Fetches current renewable energy percentage for a region from Electricity Maps API.
    /// </summary>
    public async Task<RenewableRegionData?> FetchRegionDataAsync(string zone, CancellationToken ct = default)
    {
        // Check cache first
        if (_regionData.TryGetValue(zone, out var cached) &&
            DateTimeOffset.UtcNow - cached.UpdatedAt < TimeSpan.FromMinutes(15))
            return cached;

        try
        {
            using var response = await _httpClient.GetAsync(
                $"https://api.electricitymap.org/v3/power-breakdown/latest?zone={zone}", ct);

            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var renewablePercentage = root.TryGetProperty("renewablePercentage", out var rp) ? rp.GetDouble() : 0;
                var fossilFreePercentage = root.TryGetProperty("fossilFreePercentage", out var ff) ? ff.GetDouble() : 0;

                var data = new RenewableRegionData
                {
                    Zone = zone,
                    RenewablePercentage = renewablePercentage,
                    FossilFreePercentage = fossilFreePercentage,
                    CarbonIntensityGCo2PerKwh = root.TryGetProperty("carbonIntensity", out var ci) ? ci.GetDouble() : 0,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    Source = "ElectricityMaps"
                };

                _regionData[zone] = data;
                return data;
            }
        }
        catch
        {

            // Fallback to cached/static data
            System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
        }

        return _regionData.TryGetValue(zone, out var fallback) ? fallback : null;
    }

    /// <summary>
    /// Recommends the best region for workload based on renewable energy availability.
    /// </summary>
    public async Task<RenewableRoutingRecommendation> RecommendRegionAsync(
        string workloadId, string[] candidateZones, double estimatedKwh,
        double minRenewablePercentage = 0, CancellationToken ct = default)
    {
        var regionResults = new List<RenewableRegionData>();

        foreach (var zone in candidateZones)
        {
            var data = await FetchRegionDataAsync(zone, ct);
            if (data != null) regionResults.Add(data);
        }

        if (regionResults.Count == 0)
            return new RenewableRoutingRecommendation
            {
                WorkloadId = workloadId,
                HasRecommendation = false,
                Reason = "No region data available"
            };

        // Sort by renewable percentage (highest first)
        regionResults = regionResults.OrderByDescending(r => r.RenewablePercentage).ToList();

        // Apply minimum threshold
        var eligible = regionResults.Where(r => r.RenewablePercentage >= minRenewablePercentage).ToList();
        if (eligible.Count == 0)
            eligible = regionResults; // Fallback to all if none meet threshold

        var best = eligible.First();

        var decision = new RoutingDecision
        {
            WorkloadId = workloadId,
            SelectedZone = best.Zone,
            RenewablePercentage = best.RenewablePercentage,
            EstimatedKwh = estimatedKwh,
            EstimatedRenewableKwh = estimatedKwh * best.RenewablePercentage / 100.0,
            DecidedAt = DateTimeOffset.UtcNow
        };

        _routingHistory.AddOrUpdate(
            workloadId,
            _ => new List<RoutingDecision> { decision },
            (_, list) => { lock (list) { list.Add(decision); } return list; });

        return new RenewableRoutingRecommendation
        {
            WorkloadId = workloadId,
            HasRecommendation = true,
            RecommendedZone = best.Zone,
            RenewablePercentage = best.RenewablePercentage,
            FossilFreePercentage = best.FossilFreePercentage,
            CarbonIntensity = best.CarbonIntensityGCo2PerKwh,
            EstimatedRenewableKwh = estimatedKwh * best.RenewablePercentage / 100.0,
            Rankings = regionResults.Select((r, i) => new RenewableRanking
            {
                Rank = i + 1,
                Zone = r.Zone,
                RenewablePercentage = r.RenewablePercentage,
                CarbonIntensity = r.CarbonIntensityGCo2PerKwh
            }).ToList()
        };
    }

    /// <summary>
    /// Updates region data manually (for testing or static data).
    /// </summary>
    public void UpdateRegionData(string zone, double renewablePercentage,
        double fossilFreePercentage, double carbonIntensity)
    {
        _regionData[zone] = new RenewableRegionData
        {
            Zone = zone,
            RenewablePercentage = renewablePercentage,
            FossilFreePercentage = fossilFreePercentage,
            CarbonIntensityGCo2PerKwh = carbonIntensity,
            UpdatedAt = DateTimeOffset.UtcNow,
            Source = "Manual"
        };
    }

    /// <summary>
    /// Gets all tracked region data sorted by renewable percentage.
    /// </summary>
    public IReadOnlyList<RenewableRegionData> GetAllRegions() =>
        _regionData.Values.OrderByDescending(r => r.RenewablePercentage).ToList().AsReadOnly();

    private void InitializeDefaultRegionData()
    {
        // Static fallback data for when APIs are unavailable
        var defaults = new (string Zone, double Renewable, double FossilFree, double Carbon)[]
        {
            ("NO-NO1", 98.5, 99.2, 17.0),   // Norway (hydro)
            ("IS", 100.0, 100.0, 8.0),       // Iceland (geothermal + hydro)
            ("SE-SE1", 95.0, 97.0, 20.0),    // Sweden North
            ("FR", 88.0, 92.0, 55.0),        // France (nuclear + renewable)
            ("CA-QC", 96.0, 98.0, 15.0),     // Quebec (hydro)
            ("NZ", 82.0, 82.0, 90.0),        // New Zealand
            ("US-CAL-CISO", 45.0, 50.0, 200.0), // California
            ("DE", 50.0, 55.0, 300.0),       // Germany
            ("US-MIDA-PJM", 15.0, 35.0, 400.0), // PJM (US Mid-Atlantic)
            ("AU-NSW", 25.0, 25.0, 650.0),   // Australia NSW
            ("PL", 20.0, 20.0, 650.0),       // Poland
            ("IN-WE", 25.0, 30.0, 700.0),    // India West
        };

        foreach (var (zone, renewable, fossilFree, carbon) in defaults)
        {
            _regionData[zone] = new RenewableRegionData
            {
                Zone = zone,
                RenewablePercentage = renewable,
                FossilFreePercentage = fossilFree,
                CarbonIntensityGCo2PerKwh = carbon,
                UpdatedAt = DateTimeOffset.UtcNow,
                Source = "StaticDefault"
            };
        }
    }
}

#region Models

public sealed record RenewableRegionData
{
    public required string Zone { get; init; }
    public double RenewablePercentage { get; init; }
    public double FossilFreePercentage { get; init; }
    public double CarbonIntensityGCo2PerKwh { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public required string Source { get; init; }
}

public sealed record RenewableRoutingRecommendation
{
    public required string WorkloadId { get; init; }
    public bool HasRecommendation { get; init; }
    public string? RecommendedZone { get; init; }
    public double RenewablePercentage { get; init; }
    public double FossilFreePercentage { get; init; }
    public double CarbonIntensity { get; init; }
    public double EstimatedRenewableKwh { get; init; }
    public string? Reason { get; init; }
    public List<RenewableRanking> Rankings { get; init; } = new();
}

public sealed record RenewableRanking
{
    public int Rank { get; init; }
    public required string Zone { get; init; }
    public double RenewablePercentage { get; init; }
    public double CarbonIntensity { get; init; }
}

public sealed record RoutingDecision
{
    public required string WorkloadId { get; init; }
    public required string SelectedZone { get; init; }
    public double RenewablePercentage { get; init; }
    public double EstimatedKwh { get; init; }
    public double EstimatedRenewableKwh { get; init; }
    public DateTimeOffset DecidedAt { get; init; }
}

#endregion
