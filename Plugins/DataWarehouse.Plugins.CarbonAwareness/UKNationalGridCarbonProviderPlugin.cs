using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Sustainability;

namespace DataWarehouse.Plugins.CarbonAwareness;

/// <summary>
/// Carbon intensity provider using UK National Grid Carbon Intensity API.
/// Provides real-time and forecasted carbon intensity data for Great Britain.
/// https://carbonintensity.org.uk/
/// API Documentation: https://carbon-intensity.github.io/api-definitions/
/// </summary>
public class UKNationalGridCarbonProviderPlugin : CarbonIntensityProviderPluginBase
{
    private const string BaseUrl = "https://api.carbonintensity.org.uk";

    private readonly HttpClient _httpClient;
    private bool _disposed;

    // UK DNO (Distribution Network Operator) region codes
    // See: https://carbon-intensity.github.io/api-definitions/#region-list
    private static readonly Dictionary<string, string> CloudRegionToDnoMapping = new(StringComparer.OrdinalIgnoreCase)
    {
        // AWS regions
        ["eu-west-2"] = "1",              // London (Southern Electric)
        ["eu-west-1"] = "14",             // Ireland (Northern Ireland)

        // Azure regions
        ["uksouth"] = "1",                // London (Southern Electric)
        ["ukwest"] = "12",                // Wales (Western Power Distribution South Wales)

        // GCP regions
        ["europe-west2"] = "1",           // London (Southern Electric)

        // UK Regional codes by DNO
        ["london"] = "1",                 // Southern Electric
        ["south-east"] = "2",             // South Eastern Power Networks
        ["east-england"] = "3",           // Eastern Power Networks
        ["east-midlands"] = "4",          // East Midlands
        ["midlands"] = "5",               // West Midlands
        ["north-east"] = "6",             // North Eastern
        ["north-west"] = "7",             // North Western
        ["yorkshire"] = "8",              // Yorkshire
        ["north-scotland"] = "9",         // Scottish Hydro Electric
        ["south-scotland"] = "10",        // Scottish Power
        ["south-wales"] = "11",           // South Wales
        ["south-west"] = "12",            // South West
        ["wales"] = "12",                 // Western Power Distribution South Wales
        ["northern-ireland"] = "14",      // Northern Ireland
        ["south"] = "15",                 // Southern Electric
        ["north"] = "16",                 // Scottish and Southern Energy
        ["england"] = "1",                // Default to London
    };

    // Cache for regions list
    private IReadOnlyList<string>? _cachedRegions;
    private DateTimeOffset _regionsCacheExpiry = DateTimeOffset.MinValue;
    private readonly TimeSpan _regionsCacheDuration = TimeSpan.FromHours(24);

    /// <summary>
    /// Initializes a new instance with a default HttpClient.
    /// </summary>
    public UKNationalGridCarbonProviderPlugin() : this(new HttpClient())
    {
    }

    /// <summary>
    /// Initializes a new instance with a custom HttpClient for testing.
    /// </summary>
    /// <param name="httpClient">HTTP client to use for API calls.</param>
    public UKNationalGridCarbonProviderPlugin(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _httpClient.BaseAddress = new Uri(BaseUrl);
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
        // UK National Grid API is free and doesn't require authentication
    }

    /// <inheritdoc />
    public override string Id => "datawarehouse.carbon.uknationalgrid";

    /// <inheritdoc />
    public override string Name => "UK National Grid Carbon Provider";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    public override PluginCategory Category => PluginCategory.GovernanceProvider;

    /// <summary>
    /// Cache duration for UK National Grid is 30 minutes (data updates every 30 minutes).
    /// </summary>
    protected override TimeSpan CacheDuration => TimeSpan.FromMinutes(30);

    /// <inheritdoc />
    protected override async Task<CarbonIntensityData> FetchIntensityAsync(string regionId)
    {
        var dnoId = MapRegionToDno(regionId);

        try
        {
            string endpoint;
            if (string.IsNullOrEmpty(dnoId) || dnoId == "GB")
            {
                // GET /intensity - National intensity
                endpoint = "/intensity";
            }
            else
            {
                // GET /regional/regionid/{dnoId} - Regional intensity
                endpoint = $"/regional/regionid/{Uri.EscapeDataString(dnoId)}";
            }

            var response = await _httpClient.GetAsync(endpoint);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<UKNationalGridIntensityResponse>(JsonOptions);

            if (result?.Data == null || result.Data.Count == 0)
            {
                throw new InvalidOperationException($"Empty response from UK National Grid API for region {regionId}");
            }

            var firstData = result.Data[0];
            var intensityData = firstData.Intensity;

            // Use actual value if available, otherwise use forecast
            var intensity = intensityData.Actual ?? intensityData.Forecast;
            var level = ConvertIndexToLevel(intensityData.Index);

            // Calculate approximate renewable percentage based on intensity
            // UK grid: Very low (<100) = ~70% renewable, Very high (>500) = ~20% renewable
            var renewablePercent = CalculateRenewablePercentage(intensity);

            return new CarbonIntensityData(
                regionId,
                intensity,
                level,
                renewablePercent,
                firstData.From,
                firstData.To
            );
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Failed to fetch carbon intensity for region {regionId}: {ex.Message}", ex);
        }
    }

    /// <inheritdoc />
    protected override async Task<IReadOnlyList<CarbonIntensityData>> FetchForecastAsync(string regionId, int hours)
    {
        var dnoId = MapRegionToDno(regionId);

        try
        {
            // GET /intensity/{from}/fw{hours}h or /regional/intensity/{from}/fw{hours}h/regionid/{dnoId}
            var fromTime = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mmZ");

            string endpoint;
            if (string.IsNullOrEmpty(dnoId) || dnoId == "GB")
            {
                // National forecast
                endpoint = $"/intensity/{Uri.EscapeDataString(fromTime)}/fw{hours}h";
            }
            else
            {
                // Regional forecast
                endpoint = $"/regional/intensity/{Uri.EscapeDataString(fromTime)}/fw{hours}h/regionid/{Uri.EscapeDataString(dnoId)}";
            }

            var response = await _httpClient.GetAsync(endpoint);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<UKNationalGridIntensityResponse>(JsonOptions);

            if (result?.Data == null || result.Data.Count == 0)
            {
                throw new InvalidOperationException($"No forecast data available for region {regionId}");
            }

            // Convert to our data model
            var forecast = result.Data
                .OrderBy(f => f.From)
                .Select(f =>
                {
                    var intensity = f.Intensity.Forecast;
                    var level = ConvertIndexToLevel(f.Intensity.Index);
                    var renewablePercent = CalculateRenewablePercentage(intensity);

                    return new CarbonIntensityData(
                        regionId,
                        intensity,
                        level,
                        renewablePercent,
                        f.From,
                        f.To
                    );
                })
                .ToList();

            return forecast;
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Failed to fetch forecast for region {regionId}: {ex.Message}", ex);
        }
    }

    /// <inheritdoc />
    protected override async Task<IReadOnlyList<string>> FetchRegionsAsync()
    {
        // Return cached regions if still valid
        if (_cachedRegions != null && DateTimeOffset.UtcNow < _regionsCacheExpiry)
        {
            return _cachedRegions;
        }

        try
        {
            // GET /regional
            var response = await _httpClient.GetAsync("/regional");
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<UKNationalGridRegionalResponse>(JsonOptions);

            if (result?.Data == null || result.Data.Count == 0)
            {
                throw new InvalidOperationException("Empty response from UK National Grid regional API");
            }

            // Extract all region IDs and short names
            var regions = result.Data[0].Regions
                .SelectMany(r => new[] { r.RegionId.ToString(), r.ShortName, r.DnoRegion })
                .Where(r => !string.IsNullOrEmpty(r))
                .Distinct()
                .ToList();

            // Add cloud region mappings
            var cloudRegions = CloudRegionToDnoMapping.Keys.ToList();

            // Add national identifier
            var allRegions = new List<string> { "GB", "national" };
            allRegions.AddRange(regions);
            allRegions.AddRange(cloudRegions);

            _cachedRegions = allRegions.Distinct().ToList();
            _regionsCacheExpiry = DateTimeOffset.UtcNow.Add(_regionsCacheDuration);

            return _cachedRegions;
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Failed to fetch available regions: {ex.Message}", ex);
        }
    }

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc />
    public override Task StopAsync()
    {
        ClearCache();
        _cachedRegions = null;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Maps a cloud provider region or friendly name to a UK DNO region ID.
    /// </summary>
    /// <param name="regionId">Cloud region ID, DNO ID, or friendly name.</param>
    /// <returns>UK DNO region identifier (1-17) or empty for national.</returns>
    public string MapRegionToDno(string regionId)
    {
        // Check if it's a known cloud region or friendly name
        if (CloudRegionToDnoMapping.TryGetValue(regionId, out var dnoId))
        {
            return dnoId;
        }

        // Check if it's "GB" or "national" for national data
        if (string.Equals(regionId, "GB", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(regionId, "national", StringComparison.OrdinalIgnoreCase))
        {
            return "GB";
        }

        // Otherwise assume it's already a DNO ID (1-17) or region code
        return regionId;
    }

    /// <summary>
    /// Adds a custom region to DNO mapping.
    /// </summary>
    /// <param name="regionId">Cloud region identifier or friendly name.</param>
    /// <param name="dnoId">UK DNO region ID (1-17).</param>
    public void AddRegionMapping(string regionId, string dnoId)
    {
        CloudRegionToDnoMapping[regionId] = dnoId;
    }

    /// <summary>
    /// Converts UK National Grid index string to CarbonIntensityLevel enum.
    /// Index values: "very low", "low", "moderate", "high", "very high"
    /// </summary>
    private static CarbonIntensityLevel ConvertIndexToLevel(string index)
    {
        return index?.ToLowerInvariant() switch
        {
            "very low" => CarbonIntensityLevel.VeryLow,
            "low" => CarbonIntensityLevel.Low,
            "moderate" => CarbonIntensityLevel.Medium,
            "high" => CarbonIntensityLevel.High,
            "very high" => CarbonIntensityLevel.VeryHigh,
            _ => CarbonIntensityLevel.Medium
        };
    }

    /// <summary>
    /// Calculates approximate renewable percentage based on carbon intensity.
    /// UK grid correlation: lower intensity = higher renewable generation.
    /// </summary>
    private static double CalculateRenewablePercentage(double gramsCO2PerKwh)
    {
        // UK grid typical ranges:
        // Very low (<100): ~70% renewable (wind/solar dominant)
        // Low (100-200): ~50% renewable
        // Moderate (200-350): ~35% renewable
        // High (350-500): ~25% renewable
        // Very high (>500): ~20% renewable (fossil fuel dominant)

        return gramsCO2PerKwh switch
        {
            < 100 => 70.0,
            < 200 => 50.0,
            < 350 => 35.0,
            < 500 => 25.0,
            _ => 20.0
        };
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Disposes the HTTP client.
    /// </summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _httpClient.Dispose();
            _disposed = true;
        }
    }

    // API Response Models

    private sealed class UKNationalGridIntensityResponse
    {
        [JsonPropertyName("data")]
        public List<IntensityData> Data { get; set; } = new();
    }

    private sealed class IntensityData
    {
        [JsonPropertyName("from")]
        public DateTimeOffset From { get; set; }

        [JsonPropertyName("to")]
        public DateTimeOffset To { get; set; }

        [JsonPropertyName("intensity")]
        public IntensityInfo Intensity { get; set; } = new();
    }

    private sealed class IntensityInfo
    {
        [JsonPropertyName("forecast")]
        public double Forecast { get; set; }

        [JsonPropertyName("actual")]
        public double? Actual { get; set; }

        [JsonPropertyName("index")]
        public string Index { get; set; } = string.Empty;
    }

    private sealed class UKNationalGridRegionalResponse
    {
        [JsonPropertyName("data")]
        public List<RegionalData> Data { get; set; } = new();
    }

    private sealed class RegionalData
    {
        [JsonPropertyName("from")]
        public DateTimeOffset From { get; set; }

        [JsonPropertyName("to")]
        public DateTimeOffset To { get; set; }

        [JsonPropertyName("regions")]
        public List<RegionInfo> Regions { get; set; } = new();
    }

    private sealed class RegionInfo
    {
        [JsonPropertyName("regionid")]
        public int RegionId { get; set; }

        [JsonPropertyName("dnoregion")]
        public string DnoRegion { get; set; } = string.Empty;

        [JsonPropertyName("shortname")]
        public string ShortName { get; set; } = string.Empty;

        [JsonPropertyName("intensity")]
        public IntensityInfo? Intensity { get; set; }

        [JsonPropertyName("generationmix")]
        public List<GenerationMix>? GenerationMix { get; set; }
    }

    private sealed class GenerationMix
    {
        [JsonPropertyName("fuel")]
        public string Fuel { get; set; } = string.Empty;

        [JsonPropertyName("perc")]
        public double Percentage { get; set; }
    }
}
