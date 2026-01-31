using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Sustainability;

namespace DataWarehouse.Plugins.CarbonAwareness;

/// <summary>
/// Carbon intensity provider using Electricity Maps API.
/// Provides real-time and forecasted carbon intensity data for regions worldwide.
/// https://www.electricitymaps.com/
/// API Documentation: https://static.electricitymaps.com/api/docs/index.html
/// </summary>
public class ElectricityMapsCarbonProviderPlugin : CarbonIntensityProviderPluginBase
{
    private const string BaseUrl = "https://api.electricitymap.org/v3";

    private readonly HttpClient _httpClient;
    private string? _apiKey;
    private bool _disposed;

    // Mapping from cloud regions to Electricity Maps zones
    // See: https://api.electricitymap.org/v3/zones
    private static readonly Dictionary<string, string> CloudRegionToZoneMapping = new(StringComparer.OrdinalIgnoreCase)
    {
        // AWS regions
        ["us-west-2"] = "US-NW-PACW",      // Pacific Northwest - Pacificorp West
        ["us-east-1"] = "US-MIDA-PJM",     // PJM Interconnection
        ["us-west-1"] = "US-CAL-CISO",     // California ISO
        ["eu-west-1"] = "IE",              // Ireland
        ["eu-central-1"] = "DE",           // Germany
        ["eu-north-1"] = "SE-SE1",         // Sweden North
        ["ap-northeast-1"] = "JP-TK",      // Japan Tokyo
        ["ap-southeast-1"] = "SG",         // Singapore
        ["ca-central-1"] = "CA-ON",        // Canada Ontario
        ["sa-east-1"] = "BR-S",            // Brazil South
        ["ap-south-1"] = "IN-MH",          // India Maharashtra
        ["ap-southeast-2"] = "AU-NSW",     // Australia New South Wales

        // Azure regions
        ["westus2"] = "US-NW-PACW",
        ["eastus"] = "US-MIDA-PJM",
        ["westeurope"] = "NL",             // Netherlands
        ["northeurope"] = "IE",            // Ireland
        ["germanywestcentral"] = "DE",
        ["swedencentral"] = "SE-SE3",

        // GCP regions
        ["us-west1"] = "US-NW-PACW",
        ["us-east4"] = "US-MIDA-PJM",
        ["europe-west1"] = "BE",           // Belgium
        ["europe-west4"] = "NL",           // Netherlands
        ["europe-north1"] = "FI",          // Finland
    };

    // Cache for zones list
    private IReadOnlyList<string>? _cachedZones;
    private DateTimeOffset _zonesCacheExpiry = DateTimeOffset.MinValue;
    private readonly TimeSpan _zonesCacheDuration = TimeSpan.FromHours(24);

    /// <summary>
    /// Initializes a new instance with a default HttpClient.
    /// </summary>
    public ElectricityMapsCarbonProviderPlugin() : this(new HttpClient())
    {
    }

    /// <summary>
    /// Initializes a new instance with a custom HttpClient for testing.
    /// </summary>
    /// <param name="httpClient">HTTP client to use for API calls.</param>
    public ElectricityMapsCarbonProviderPlugin(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _httpClient.BaseAddress = new Uri(BaseUrl);
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
    }

    /// <inheritdoc />
    public override string Id => "datawarehouse.carbon.electricitymaps";

    /// <inheritdoc />
    public override string Name => "Electricity Maps Carbon Provider";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    public override PluginCategory Category => PluginCategory.GovernanceProvider;

    /// <summary>
    /// Configures the Electricity Maps API key.
    /// Get your API key at: https://api-portal.electricitymaps.com/
    /// </summary>
    /// <param name="apiKey">API key from Electricity Maps.</param>
    public void Configure(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("API key cannot be null or empty", nameof(apiKey));

        _apiKey = apiKey;
        _httpClient.DefaultRequestHeaders.Clear();
        _httpClient.DefaultRequestHeaders.Add("auth-token", apiKey);
    }

    /// <inheritdoc />
    protected override async Task<CarbonIntensityData> FetchIntensityAsync(string regionId)
    {
        EnsureConfigured();

        var zone = MapRegionToZone(regionId);

        try
        {
            // GET /v3/carbon-intensity/latest?zone={zone}
            var response = await _httpClient.GetAsync($"/v3/carbon-intensity/latest?zone={Uri.EscapeDataString(zone)}");
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<ElectricityMapsLatestResponse>(JsonOptions);

            if (result == null)
            {
                throw new InvalidOperationException($"Empty response from Electricity Maps API for zone {zone}");
            }

            var intensity = result.CarbonIntensity;
            var level = ClassifyIntensity(intensity);

            // Fetch power breakdown to get renewable percentage
            var renewablePercent = await FetchRenewablePercentageAsync(zone);

            return new CarbonIntensityData(
                regionId,
                intensity,
                level,
                renewablePercent,
                result.DateTime,
                null
            );
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Failed to fetch carbon intensity for zone {zone}: {ex.Message}", ex);
        }
    }

    /// <inheritdoc />
    protected override async Task<IReadOnlyList<CarbonIntensityData>> FetchForecastAsync(string regionId, int hours)
    {
        EnsureConfigured();

        var zone = MapRegionToZone(regionId);

        try
        {
            // GET /v3/carbon-intensity/forecast?zone={zone}
            var response = await _httpClient.GetAsync($"/v3/carbon-intensity/forecast?zone={Uri.EscapeDataString(zone)}");
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<ElectricityMapsForecastResponse>(JsonOptions);

            if (result?.Forecast == null || result.Forecast.Count == 0)
            {
                throw new InvalidOperationException($"No forecast data available for zone {zone}");
            }

            // Get the current renewable percentage for reference
            var renewablePercent = await FetchRenewablePercentageAsync(zone);

            // Filter to requested hours and convert to our data model
            var forecast = result.Forecast
                .Where(f => f.DateTime <= DateTimeOffset.UtcNow.AddHours(hours))
                .OrderBy(f => f.DateTime)
                .Select(f => new CarbonIntensityData(
                    regionId,
                    f.CarbonIntensity,
                    ClassifyIntensity(f.CarbonIntensity),
                    renewablePercent, // Approximation - forecast doesn't include power breakdown
                    f.DateTime,
                    f.DateTime.AddMinutes(result.ForecastIntervalMinutes)
                ))
                .ToList();

            return forecast;
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Failed to fetch forecast for zone {zone}: {ex.Message}", ex);
        }
    }

    /// <inheritdoc />
    protected override async Task<IReadOnlyList<string>> FetchRegionsAsync()
    {
        EnsureConfigured();

        // Return cached zones if still valid
        if (_cachedZones != null && DateTimeOffset.UtcNow < _zonesCacheExpiry)
        {
            return _cachedZones;
        }

        try
        {
            // GET /v3/zones
            var response = await _httpClient.GetAsync("/v3/zones");
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<Dictionary<string, ElectricityMapsZoneInfo>>(JsonOptions);

            if (result == null)
            {
                throw new InvalidOperationException("Empty response from Electricity Maps zones API");
            }

            // Return both raw zones and mapped cloud regions
            var zones = result.Keys.ToList();
            var cloudRegions = CloudRegionToZoneMapping.Keys.ToList();

            _cachedZones = zones.Concat(cloudRegions).Distinct().ToList();
            _zonesCacheExpiry = DateTimeOffset.UtcNow.Add(_zonesCacheDuration);

            return _cachedZones;
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Failed to fetch available zones: {ex.Message}", ex);
        }
    }

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc />
    public override Task StopAsync()
    {
        ClearCache();
        _cachedZones = null;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Maps a cloud provider region to an Electricity Maps zone.
    /// </summary>
    /// <param name="regionId">Cloud region ID or Electricity Maps zone.</param>
    /// <returns>Electricity Maps zone identifier.</returns>
    public string MapRegionToZone(string regionId)
    {
        // First check if it's a known cloud region
        if (CloudRegionToZoneMapping.TryGetValue(regionId, out var zone))
        {
            return zone;
        }

        // Otherwise assume it's already an Electricity Maps zone
        return regionId;
    }

    /// <summary>
    /// Adds a custom region to zone mapping.
    /// </summary>
    /// <param name="regionId">Cloud region identifier.</param>
    /// <param name="zone">Electricity Maps zone code.</param>
    public void AddRegionMapping(string regionId, string zone)
    {
        CloudRegionToZoneMapping[regionId] = zone;
    }

    private async Task<double> FetchRenewablePercentageAsync(string zone)
    {
        try
        {
            // GET /v3/power-breakdown/latest?zone={zone}
            var response = await _httpClient.GetAsync($"/v3/power-breakdown/latest?zone={Uri.EscapeDataString(zone)}");

            if (!response.IsSuccessStatusCode)
            {
                return 0.0; // Fallback if power breakdown not available
            }

            var result = await response.Content.ReadFromJsonAsync<ElectricityMapsPowerBreakdownResponse>(JsonOptions);

            if (result?.PowerConsumptionBreakdown == null)
            {
                return 0.0;
            }

            // Calculate renewable percentage from power sources
            var breakdown = result.PowerConsumptionBreakdown;
            var total = breakdown.Values.Sum();

            if (total <= 0)
            {
                return 0.0;
            }

            // Renewable sources: wind, solar, hydro, geothermal, biomass
            var renewable =
                (breakdown.GetValueOrDefault("wind", 0) +
                 breakdown.GetValueOrDefault("solar", 0) +
                 breakdown.GetValueOrDefault("hydro", 0) +
                 breakdown.GetValueOrDefault("geothermal", 0) +
                 breakdown.GetValueOrDefault("biomass", 0) +
                 breakdown.GetValueOrDefault("hydro discharge", 0) +
                 breakdown.GetValueOrDefault("battery discharge", 0));

            return Math.Min(100.0, (renewable / total) * 100.0);
        }
        catch
        {
            return 0.0; // Fallback on error
        }
    }

    private void EnsureConfigured()
    {
        if (string.IsNullOrEmpty(_apiKey))
        {
            throw new InvalidOperationException(
                "Electricity Maps API key not configured. Call Configure(apiKey) before using this provider.");
        }
    }

    private static CarbonIntensityLevel ClassifyIntensity(double gramsCO2PerKwh)
    {
        return gramsCO2PerKwh switch
        {
            < 100 => CarbonIntensityLevel.VeryLow,
            < 200 => CarbonIntensityLevel.Low,
            < 350 => CarbonIntensityLevel.Medium,
            < 500 => CarbonIntensityLevel.High,
            _ => CarbonIntensityLevel.VeryHigh
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

    private sealed class ElectricityMapsLatestResponse
    {
        [JsonPropertyName("zone")]
        public string Zone { get; set; } = string.Empty;

        [JsonPropertyName("carbonIntensity")]
        public double CarbonIntensity { get; set; }

        [JsonPropertyName("datetime")]
        public DateTimeOffset DateTime { get; set; }

        [JsonPropertyName("updatedAt")]
        public DateTimeOffset UpdatedAt { get; set; }

        [JsonPropertyName("emissionFactorType")]
        public string? EmissionFactorType { get; set; }

        [JsonPropertyName("isEstimated")]
        public bool IsEstimated { get; set; }

        [JsonPropertyName("estimationMethod")]
        public string? EstimationMethod { get; set; }
    }

    private sealed class ElectricityMapsForecastResponse
    {
        [JsonPropertyName("zone")]
        public string Zone { get; set; } = string.Empty;

        [JsonPropertyName("forecast")]
        public List<ForecastEntry> Forecast { get; set; } = new();

        [JsonPropertyName("updatedAt")]
        public DateTimeOffset UpdatedAt { get; set; }

        // Default interval is typically 60 minutes
        public int ForecastIntervalMinutes { get; set; } = 60;
    }

    private sealed class ForecastEntry
    {
        [JsonPropertyName("carbonIntensity")]
        public double CarbonIntensity { get; set; }

        [JsonPropertyName("datetime")]
        public DateTimeOffset DateTime { get; set; }
    }

    private sealed class ElectricityMapsZoneInfo
    {
        [JsonPropertyName("zoneName")]
        public string ZoneName { get; set; } = string.Empty;

        [JsonPropertyName("countryName")]
        public string? CountryName { get; set; }
    }

    private sealed class ElectricityMapsPowerBreakdownResponse
    {
        [JsonPropertyName("zone")]
        public string Zone { get; set; } = string.Empty;

        [JsonPropertyName("datetime")]
        public DateTimeOffset DateTime { get; set; }

        [JsonPropertyName("powerConsumptionBreakdown")]
        public Dictionary<string, double>? PowerConsumptionBreakdown { get; set; }

        [JsonPropertyName("powerProductionBreakdown")]
        public Dictionary<string, double>? PowerProductionBreakdown { get; set; }

        [JsonPropertyName("fossilFreePercentage")]
        public double? FossilFreePercentage { get; set; }

        [JsonPropertyName("renewablePercentage")]
        public double? RenewablePercentage { get; set; }
    }
}
