using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Sustainability;

namespace DataWarehouse.Plugins.CarbonAwareness;

/// <summary>
/// Default green region selector for choosing low-carbon infrastructure locations.
/// Evaluates regions based on carbon intensity, renewable percentage, and optional constraints.
/// </summary>
public class DefaultGreenRegionSelectorPlugin : GreenRegionSelectorPluginBase
{
    // Simulated latency data from a reference point (ms)
    private static readonly Dictionary<string, double> _regionLatency = new()
    {
        ["us-west-2"] = 50,
        ["us-east-1"] = 30,
        ["eu-west-1"] = 120,
        ["eu-central-1"] = 110,
        ["eu-north-1"] = 140,
        ["ap-northeast-1"] = 180,
        ["ap-southeast-1"] = 200,
        ["ca-central-1"] = 45,
        ["sa-east-1"] = 160,
    };

    // Simulated availability data (percentage)
    private static readonly Dictionary<string, double> _regionAvailability = new()
    {
        ["us-west-2"] = 99.95,
        ["us-east-1"] = 99.99,
        ["eu-west-1"] = 99.95,
        ["eu-central-1"] = 99.95,
        ["eu-north-1"] = 99.90,
        ["ap-northeast-1"] = 99.95,
        ["ap-southeast-1"] = 99.90,
        ["ca-central-1"] = 99.95,
        ["sa-east-1"] = 99.90,
    };

    /// <inheritdoc />
    public override string Id => "datawarehouse.carbon.regionselector";

    /// <inheritdoc />
    public override string Name => "Green Region Selector";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    public override PluginCategory Category => PluginCategory.GovernanceProvider;

    /// <summary>
    /// Sets the carbon intensity provider for region evaluation.
    /// </summary>
    /// <param name="provider">Carbon intensity provider instance.</param>
    public void SetIntensityProvider(ICarbonIntensityProvider provider)
    {
        IntensityProvider = provider;
    }

    /// <summary>
    /// Calculates a composite score for region selection.
    /// Higher score = better choice.
    /// </summary>
    /// <param name="intensity">Carbon intensity data.</param>
    /// <param name="criteria">Selection criteria.</param>
    /// <returns>Composite score.</returns>
    protected override double CalculateScore(CarbonIntensityData intensity, RegionSelectionCriteria criteria)
    {
        double score = 0.0;

        // Carbon intensity component (inverted - lower is better)
        // Max 50 points for very low carbon (< 50 gCO2/kWh)
        score += Math.Max(0, 50 - (intensity.GramsCO2PerKwh / 10));

        // Renewable percentage component
        // Max 30 points for 100% renewable
        if (criteria.PreferRenewable)
        {
            score += intensity.RenewablePercentage * 0.3;
        }

        // Latency constraint
        if (criteria.MaxLatencyMs.HasValue &&
            _regionLatency.TryGetValue(intensity.RegionId, out var latency))
        {
            if (latency > criteria.MaxLatencyMs.Value)
            {
                score -= 100; // Heavily penalize exceeding latency constraint
            }
            else
            {
                // Bonus for low latency (max 10 points)
                score += (criteria.MaxLatencyMs.Value - latency) / criteria.MaxLatencyMs.Value * 10;
            }
        }

        // Availability constraint
        if (criteria.MinAvailability.HasValue &&
            _regionAvailability.TryGetValue(intensity.RegionId, out var availability))
        {
            if (availability < criteria.MinAvailability.Value)
            {
                score -= 100; // Heavily penalize not meeting availability requirement
            }
            else
            {
                // Small bonus for exceeding availability (max 10 points)
                score += (availability - criteria.MinAvailability.Value) * 10;
            }
        }

        return score;
    }

    /// <summary>
    /// Gets estimated latency for a region.
    /// </summary>
    /// <param name="regionId">Region identifier.</param>
    /// <returns>Estimated latency in milliseconds.</returns>
    public double GetRegionLatency(string regionId)
    {
        return _regionLatency.TryGetValue(regionId, out var latency) ? latency : 100.0;
    }

    /// <summary>
    /// Gets estimated availability for a region.
    /// </summary>
    /// <param name="regionId">Region identifier.</param>
    /// <returns>Availability percentage.</returns>
    public double GetRegionAvailability(string regionId)
    {
        return _regionAvailability.TryGetValue(regionId, out var availability) ? availability : 99.0;
    }

    /// <summary>
    /// Finds regions meeting all specified criteria.
    /// </summary>
    /// <param name="availableRegions">Candidate regions.</param>
    /// <param name="criteria">Selection criteria.</param>
    /// <returns>Regions that meet all criteria.</returns>
    public async Task<IReadOnlyList<string>> FindQualifyingRegionsAsync(
        string[] availableRegions,
        RegionSelectionCriteria criteria)
    {
        var qualifying = new List<string>();

        foreach (var region in availableRegions)
        {
            var meetsLatency = !criteria.MaxLatencyMs.HasValue ||
                (_regionLatency.TryGetValue(region, out var latency) && latency <= criteria.MaxLatencyMs.Value);

            var meetsAvailability = !criteria.MinAvailability.HasValue ||
                (_regionAvailability.TryGetValue(region, out var availability) && availability >= criteria.MinAvailability.Value);

            if (meetsLatency && meetsAvailability)
            {
                qualifying.Add(region);
            }
        }

        // If we have qualifying regions and prefer renewable, sort by renewable %
        if (qualifying.Count > 0 && criteria.PreferRenewable && IntensityProvider != null)
        {
            var intensities = await Task.WhenAll(
                qualifying.Select(r => IntensityProvider.GetCurrentIntensityAsync(r)));

            qualifying = intensities
                .OrderByDescending(i => i.RenewablePercentage)
                .Select(i => i.RegionId)
                .ToList();
        }

        return qualifying;
    }

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc />
    public override Task StopAsync() => Task.CompletedTask;
}
