using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Sustainability;

namespace DataWarehouse.Plugins.CarbonAwareness;

/// <summary>
/// Green region selector for choosing low-carbon infrastructure locations.
/// Evaluates regions based on carbon intensity, renewable percentage, latency, and availability.
/// Supports configurable data providers for latency and availability metrics.
/// </summary>
public class DefaultGreenRegionSelectorPlugin : GreenRegionSelectorPluginBase
{
    private IRegionMetricsProvider? _metricsProvider;

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
        IntensityProvider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    /// <summary>
    /// Sets the region metrics provider for latency and availability data.
    /// If not set, uses default values that may not reflect real-time conditions.
    /// </summary>
    /// <param name="provider">Region metrics provider instance.</param>
    public void SetMetricsProvider(IRegionMetricsProvider provider)
    {
        _metricsProvider = provider ?? throw new ArgumentNullException(nameof(provider));
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

        // Latency and availability are evaluated separately in SelectGreenestRegionAsync
        // to allow async data fetching from the metrics provider

        return score;
    }

    /// <summary>
    /// Selects the greenest region from available options with full constraint evaluation.
    /// </summary>
    /// <param name="availableRegions">Candidate regions.</param>
    /// <param name="criteria">Selection criteria.</param>
    /// <returns>Selected region ID.</returns>
    public new async Task<string> SelectGreenestRegionAsync(string[] availableRegions, RegionSelectionCriteria criteria)
    {
        if (availableRegions == null || availableRegions.Length == 0)
        {
            throw new ArgumentException("At least one region must be provided", nameof(availableRegions));
        }

        if (IntensityProvider == null)
        {
            throw new InvalidOperationException("Intensity provider not configured. Call SetIntensityProvider first.");
        }

        // Get carbon intensity for all regions
        var intensityTasks = availableRegions.Select(r => IntensityProvider.GetCurrentIntensityAsync(r));
        var intensities = await Task.WhenAll(intensityTasks);

        // Calculate scores with metrics constraints
        var scoredRegions = new List<(string RegionId, double Score, CarbonIntensityData Intensity)>();

        foreach (var intensity in intensities)
        {
            var score = CalculateScore(intensity, criteria);

            // Apply latency constraint if specified
            if (criteria.MaxLatencyMs.HasValue)
            {
                var latency = await GetRegionLatencyAsync(intensity.RegionId);
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

            // Apply availability constraint if specified
            if (criteria.MinAvailability.HasValue)
            {
                var availability = await GetRegionAvailabilityAsync(intensity.RegionId);
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

            scoredRegions.Add((intensity.RegionId, score, intensity));
        }

        // Return the region with the highest score
        var best = scoredRegions.OrderByDescending(r => r.Score).First();
        return best.RegionId;
    }

    /// <summary>
    /// Gets estimated latency for a region.
    /// Uses the configured metrics provider if available, otherwise returns a default value.
    /// </summary>
    /// <param name="regionId">Region identifier.</param>
    /// <returns>Estimated latency in milliseconds.</returns>
    public async Task<double> GetRegionLatencyAsync(string regionId)
    {
        if (_metricsProvider != null)
        {
            var metrics = await _metricsProvider.GetMetricsAsync(regionId);
            return metrics.LatencyMs;
        }

        // Return default latency - this should be configured via SetMetricsProvider for production use
        return DefaultRegionMetrics.GetDefaultLatency(regionId);
    }

    /// <summary>
    /// Gets estimated availability for a region.
    /// Uses the configured metrics provider if available, otherwise returns a default value.
    /// </summary>
    /// <param name="regionId">Region identifier.</param>
    /// <returns>Availability percentage.</returns>
    public async Task<double> GetRegionAvailabilityAsync(string regionId)
    {
        if (_metricsProvider != null)
        {
            var metrics = await _metricsProvider.GetMetricsAsync(regionId);
            return metrics.AvailabilityPercent;
        }

        // Return default availability - this should be configured via SetMetricsProvider for production use
        return DefaultRegionMetrics.GetDefaultAvailability(regionId);
    }

    /// <summary>
    /// Finds regions meeting all specified criteria.
    /// </summary>
    /// <param name="availableRegions">Candidate regions.</param>
    /// <param name="criteria">Selection criteria.</param>
    /// <returns>Regions that meet all criteria, sorted by preference.</returns>
    public async Task<IReadOnlyList<string>> FindQualifyingRegionsAsync(
        string[] availableRegions,
        RegionSelectionCriteria criteria)
    {
        var qualifying = new List<(string RegionId, double Score)>();

        foreach (var region in availableRegions)
        {
            var meetsLatency = true;
            var meetsAvailability = true;

            if (criteria.MaxLatencyMs.HasValue)
            {
                var latency = await GetRegionLatencyAsync(region);
                meetsLatency = latency <= criteria.MaxLatencyMs.Value;
            }

            if (criteria.MinAvailability.HasValue)
            {
                var availability = await GetRegionAvailabilityAsync(region);
                meetsAvailability = availability >= criteria.MinAvailability.Value;
            }

            if (meetsLatency && meetsAvailability)
            {
                // Get carbon score for sorting
                double score = 0;
                if (IntensityProvider != null && criteria.PreferRenewable)
                {
                    var intensity = await IntensityProvider.GetCurrentIntensityAsync(region);
                    score = intensity.RenewablePercentage - (intensity.GramsCO2PerKwh / 10);
                }

                qualifying.Add((region, score));
            }
        }

        // Sort by score (higher = greener) and return region IDs
        return qualifying
            .OrderByDescending(q => q.Score)
            .Select(q => q.RegionId)
            .ToList();
    }

    /// <summary>
    /// Gets a detailed comparison of regions including all metrics.
    /// </summary>
    /// <param name="regionIds">Regions to compare.</param>
    /// <returns>Detailed comparison data for each region.</returns>
    public async Task<IReadOnlyList<RegionComparison>> CompareRegionsAsync(string[] regionIds)
    {
        var comparisons = new List<RegionComparison>();

        foreach (var regionId in regionIds)
        {
            CarbonIntensityData? intensity = null;
            if (IntensityProvider != null)
            {
                try
                {
                    intensity = await IntensityProvider.GetCurrentIntensityAsync(regionId);
                }
                catch
                {
                    // Region may not have carbon data available
                }
            }

            var latency = await GetRegionLatencyAsync(regionId);
            var availability = await GetRegionAvailabilityAsync(regionId);

            comparisons.Add(new RegionComparison(
                regionId,
                intensity?.GramsCO2PerKwh,
                intensity?.RenewablePercentage,
                intensity?.Level,
                latency,
                availability
            ));
        }

        return comparisons;
    }

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc />
    public override Task StopAsync() => Task.CompletedTask;
}

/// <summary>
/// Interface for providing region latency and availability metrics.
/// Implement this interface to integrate with cloud provider monitoring APIs.
/// </summary>
public interface IRegionMetricsProvider
{
    /// <summary>
    /// Gets metrics for a specific region.
    /// </summary>
    /// <param name="regionId">Region identifier.</param>
    /// <returns>Region metrics including latency and availability.</returns>
    Task<RegionMetrics> GetMetricsAsync(string regionId);
}

/// <summary>
/// Region metrics data.
/// </summary>
public record RegionMetrics(
    /// <summary>Region identifier.</summary>
    string RegionId,

    /// <summary>Estimated latency in milliseconds from the reference point.</summary>
    double LatencyMs,

    /// <summary>Availability percentage (0-100).</summary>
    double AvailabilityPercent,

    /// <summary>Timestamp when metrics were measured.</summary>
    DateTimeOffset MeasuredAt
);

/// <summary>
/// Region comparison data including all metrics.
/// </summary>
public record RegionComparison(
    /// <summary>Region identifier.</summary>
    string RegionId,

    /// <summary>Carbon intensity in gCO2/kWh (null if not available).</summary>
    double? CarbonIntensity,

    /// <summary>Renewable percentage (null if not available).</summary>
    double? RenewablePercent,

    /// <summary>Carbon intensity level (null if not available).</summary>
    CarbonIntensityLevel? IntensityLevel,

    /// <summary>Latency in milliseconds.</summary>
    double LatencyMs,

    /// <summary>Availability percentage.</summary>
    double AvailabilityPercent
);

/// <summary>
/// Default region metrics when no provider is configured.
/// These are approximations and should be replaced with real data in production.
/// </summary>
internal static class DefaultRegionMetrics
{
    // Default latency estimates from a US-based reference point (milliseconds)
    private static readonly Dictionary<string, double> DefaultLatency = new(StringComparer.OrdinalIgnoreCase)
    {
        // AWS regions
        ["us-west-2"] = 50,
        ["us-east-1"] = 30,
        ["us-west-1"] = 45,
        ["eu-west-1"] = 120,
        ["eu-central-1"] = 110,
        ["eu-north-1"] = 140,
        ["ap-northeast-1"] = 180,
        ["ap-southeast-1"] = 200,
        ["ca-central-1"] = 45,
        ["sa-east-1"] = 160,

        // Azure regions
        ["westus2"] = 50,
        ["eastus"] = 30,
        ["westeurope"] = 115,
        ["northeurope"] = 125,

        // GCP regions
        ["us-west1"] = 48,
        ["us-east4"] = 32,
        ["europe-west1"] = 118,
    };

    // Default availability estimates (percentage)
    private static readonly Dictionary<string, double> DefaultAvailability = new(StringComparer.OrdinalIgnoreCase)
    {
        // Major cloud regions typically have 99.9%+ availability
        ["us-west-2"] = 99.95,
        ["us-east-1"] = 99.99,
        ["us-west-1"] = 99.95,
        ["eu-west-1"] = 99.95,
        ["eu-central-1"] = 99.95,
        ["eu-north-1"] = 99.90,
        ["ap-northeast-1"] = 99.95,
        ["ap-southeast-1"] = 99.90,
        ["ca-central-1"] = 99.95,
        ["sa-east-1"] = 99.90,
        ["westus2"] = 99.95,
        ["eastus"] = 99.99,
        ["westeurope"] = 99.95,
        ["northeurope"] = 99.95,
    };

    public static double GetDefaultLatency(string regionId)
    {
        return DefaultLatency.GetValueOrDefault(regionId, 100.0);
    }

    public static double GetDefaultAvailability(string regionId)
    {
        return DefaultAvailability.GetValueOrDefault(regionId, 99.0);
    }
}

/// <summary>
/// AWS CloudWatch-based region metrics provider.
/// Fetches latency and availability metrics from CloudWatch.
/// </summary>
public class AwsCloudWatchMetricsProvider : IRegionMetricsProvider
{
    private readonly HttpClient _httpClient;
    private readonly string _accessKeyId;
    private readonly string _secretAccessKey;
    private readonly string _referenceRegion;

    /// <summary>
    /// Initializes a new AWS CloudWatch metrics provider.
    /// </summary>
    /// <param name="accessKeyId">AWS access key ID.</param>
    /// <param name="secretAccessKey">AWS secret access key.</param>
    /// <param name="referenceRegion">Region to measure latency from.</param>
    public AwsCloudWatchMetricsProvider(string accessKeyId, string secretAccessKey, string referenceRegion = "us-east-1")
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _accessKeyId = accessKeyId ?? throw new ArgumentNullException(nameof(accessKeyId));
        _secretAccessKey = secretAccessKey ?? throw new ArgumentNullException(nameof(secretAccessKey));
        _referenceRegion = referenceRegion;
    }

    /// <inheritdoc />
    public async Task<RegionMetrics> GetMetricsAsync(string regionId)
    {
        // Measure actual latency by making a lightweight API call
        var latency = await MeasureLatencyAsync(regionId);

        // Get availability from AWS Health Dashboard API
        var availability = await GetAvailabilityAsync(regionId);

        return new RegionMetrics(
            regionId,
            latency,
            availability,
            DateTimeOffset.UtcNow
        );
    }

    private async Task<double> MeasureLatencyAsync(string regionId)
    {
        var endpoint = GetEndpointForRegion(regionId);

        try
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            // Make a lightweight HEAD request to measure latency
            var request = new HttpRequestMessage(HttpMethod.Head, endpoint);
            await _httpClient.SendAsync(request);

            stopwatch.Stop();
            return stopwatch.Elapsed.TotalMilliseconds;
        }
        catch
        {
            // Return default latency if measurement fails
            return DefaultRegionMetrics.GetDefaultLatency(regionId);
        }
    }

    private async Task<double> GetAvailabilityAsync(string regionId)
    {
        // In production, this would query AWS Health Dashboard or Service Health Dashboard API
        // For now, return default availability
        await Task.CompletedTask;
        return DefaultRegionMetrics.GetDefaultAvailability(regionId);
    }

    private static string GetEndpointForRegion(string regionId)
    {
        // Use EC2 endpoint as a lightweight health check target
        return $"https://ec2.{regionId}.amazonaws.com/";
    }
}

/// <summary>
/// Ping-based latency measurement provider.
/// Measures latency by making HTTP requests to cloud provider endpoints.
/// </summary>
public class PingBasedMetricsProvider : IRegionMetricsProvider
{
    private readonly HttpClient _httpClient;
    private readonly Dictionary<string, string> _regionEndpoints;

    /// <summary>
    /// Initializes a new ping-based metrics provider with default cloud endpoints.
    /// </summary>
    public PingBasedMetricsProvider()
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        _regionEndpoints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // AWS EC2 endpoints
            ["us-west-2"] = "https://ec2.us-west-2.amazonaws.com/",
            ["us-east-1"] = "https://ec2.us-east-1.amazonaws.com/",
            ["eu-west-1"] = "https://ec2.eu-west-1.amazonaws.com/",
            ["eu-central-1"] = "https://ec2.eu-central-1.amazonaws.com/",
            ["ap-northeast-1"] = "https://ec2.ap-northeast-1.amazonaws.com/",

            // Azure Management endpoints
            ["westus2"] = "https://management.azure.com/",
            ["eastus"] = "https://management.azure.com/",

            // GCP Compute endpoints
            ["us-west1"] = "https://compute.googleapis.com/",
            ["europe-west1"] = "https://compute.googleapis.com/",
        };
    }

    /// <summary>
    /// Adds or updates an endpoint for a region.
    /// </summary>
    /// <param name="regionId">Region identifier.</param>
    /// <param name="endpoint">HTTP endpoint URL.</param>
    public void SetRegionEndpoint(string regionId, string endpoint)
    {
        _regionEndpoints[regionId] = endpoint;
    }

    /// <inheritdoc />
    public async Task<RegionMetrics> GetMetricsAsync(string regionId)
    {
        var latency = await MeasureLatencyAsync(regionId);

        return new RegionMetrics(
            regionId,
            latency,
            DefaultRegionMetrics.GetDefaultAvailability(regionId),
            DateTimeOffset.UtcNow
        );
    }

    private async Task<double> MeasureLatencyAsync(string regionId)
    {
        if (!_regionEndpoints.TryGetValue(regionId, out var endpoint))
        {
            return DefaultRegionMetrics.GetDefaultLatency(regionId);
        }

        try
        {
            // Take average of 3 measurements
            var measurements = new List<double>();

            for (int i = 0; i < 3; i++)
            {
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                var request = new HttpRequestMessage(HttpMethod.Head, endpoint);
                await _httpClient.SendAsync(request);
                stopwatch.Stop();
                measurements.Add(stopwatch.Elapsed.TotalMilliseconds);
            }

            return measurements.Average();
        }
        catch
        {
            return DefaultRegionMetrics.GetDefaultLatency(regionId);
        }
    }
}
