using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DataWarehouse.SDK.Contracts.Carbon;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateSustainability.Strategies.GreenPlacement;

/// <summary>
/// Integrates with the Electricity Maps API v3 (https://api.electricitymap.org/v3) to fetch
/// real-time carbon intensity and power breakdown data for global electricity grid zones.
/// Provides a fallback/alternative to WattTime with broader international coverage.
/// Supports zone mapping from cloud regions, response caching, and graceful degradation.
/// </summary>
public sealed class ElectricityMapsApiStrategy : SustainabilityStrategyBase
{
    private const string DefaultApiEndpoint = "https://api.electricitymap.org/v3";
    private const int DefaultCacheTtlSeconds = 300; // 5 minutes
    private const int MaxRetries = 3;

    private readonly HttpClient _httpClient;
    private readonly BoundedDictionary<string, (GridCarbonData Data, DateTimeOffset Expiry)> _cache = new BoundedDictionary<string, (GridCarbonData Data, DateTimeOffset Expiry)>(1000);

    /// <summary>
    /// Maps common cloud provider regions to Electricity Maps zone identifiers.
    /// EM zones follow the ISO 3166-1/2 standard with power grid subdivisions.
    /// </summary>
    private static readonly Dictionary<string, string> CloudRegionToEmZone = new(StringComparer.OrdinalIgnoreCase)
    {
        // AWS regions
        ["us-east-1"] = "US-PJM",
        ["us-east-2"] = "US-MIDW-MISO",
        ["us-west-1"] = "US-CAL-CISO",
        ["us-west-2"] = "US-NW-BPAT",
        ["eu-west-1"] = "IE",
        ["eu-west-2"] = "GB",
        ["eu-central-1"] = "DE",
        ["eu-north-1"] = "SE",
        ["ap-southeast-1"] = "SG",
        ["ap-northeast-1"] = "JP-TK",
        ["ap-south-1"] = "IN-WE",
        ["ca-central-1"] = "CA-ON",
        // Azure regions
        ["eastus"] = "US-PJM",
        ["westus2"] = "US-NW-BPAT",
        ["northeurope"] = "IE",
        ["westeurope"] = "NL",
        ["uksouth"] = "GB",
        ["swedencentral"] = "SE",
        // GCP regions
        ["us-central1"] = "US-MIDW-MISO",
        ["us-east4"] = "US-PJM",
        ["europe-west1"] = "BE",
        ["europe-north1"] = "SE",
        ["asia-east1"] = "TW",
    };

    /// <summary>
    /// Electricity Maps API key. Must be configured for the strategy to be available.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Electricity Maps API endpoint. Defaults to https://api.electricitymap.org/v3.
    /// </summary>
    public string ApiEndpoint { get; set; } = DefaultApiEndpoint;

    /// <summary>
    /// Cache time-to-live in seconds. Defaults to 300 (5 minutes).
    /// </summary>
    public int CacheTtlSeconds { get; set; } = DefaultCacheTtlSeconds;

    /// <inheritdoc/>
    public override string StrategyId => "electricitymaps-grid-api";

    /// <inheritdoc/>
    public override string DisplayName => "Electricity Maps Grid Carbon API";

    /// <inheritdoc/>
    public override SustainabilityCategory Category => SustainabilityCategory.CarbonAwareness;

    /// <inheritdoc/>
    public override SustainabilityCapabilities Capabilities =>
        SustainabilityCapabilities.ExternalIntegration |
        SustainabilityCapabilities.RealTimeMonitoring |
        SustainabilityCapabilities.CarbonCalculation;

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Real-time grid carbon intensity from Electricity Maps API. " +
        "Provides carbon intensity (gCO2eq/kWh) and power breakdown (renewable vs fossil percentages) " +
        "for global electricity grid zones with automatic cloud-region-to-zone mapping.";

    /// <inheritdoc/>
    public override string[] Tags => new[]
    {
        "carbon", "grid", "electricity-maps", "real-time", "api",
        "emissions", "renewable", "power-breakdown", "global"
    };

    /// <summary>
    /// Creates a new ElectricityMapsApiStrategy with the provided HTTP client.
    /// Uses IHttpClientFactory pattern -- accepts HttpClient via constructor.
    /// </summary>
    /// <param name="httpClient">Pre-configured HttpClient instance (typically from IHttpClientFactory).</param>
    public ElectricityMapsApiStrategy(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
    }

    /// <summary>
    /// Creates a new ElectricityMapsApiStrategy with a default HttpClient.
    /// Provided for auto-discovery scenarios; prefer the HttpClient constructor for production.
    /// </summary>
    public ElectricityMapsApiStrategy() : this(new HttpClient())
    {
    }

    /// <summary>
    /// Returns true if the API key is configured.
    /// </summary>
    public bool IsAvailable()
    {
        return !string.IsNullOrWhiteSpace(ApiKey);
    }

    /// <summary>
    /// Fetches current grid carbon intensity and renewable percentage for the specified region.
    /// Combines carbon intensity and power breakdown endpoints for comprehensive data.
    /// Uses cached data if available and not expired.
    /// Returns estimated data on API failure to ensure callers always get a result.
    /// </summary>
    /// <param name="region">Cloud region or Electricity Maps zone identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Grid carbon data with intensity and renewable mix.</returns>
    public async Task<GridCarbonData> GetGridCarbonDataAsync(string region, CancellationToken ct = default)
    {
        var zone = ResolveZone(region);
        var cacheKey = $"em:{zone}";

        // Check cache first
        if (_cache.TryGetValue(cacheKey, out var cached) && cached.Expiry > DateTimeOffset.UtcNow)
        {
            return cached.Data;
        }

        try
        {
            // Fetch carbon intensity and power breakdown in parallel
            var intensityTask = FetchCarbonIntensityWithRetryAsync(zone, ct);
            var breakdownTask = FetchPowerBreakdownWithRetryAsync(zone, ct);

            await Task.WhenAll(intensityTask, breakdownTask);

            var intensity = await intensityTask;
            var breakdown = await breakdownTask;

            var data = new GridCarbonData
            {
                Region = zone,
                Timestamp = intensity.UpdatedAt ?? DateTimeOffset.UtcNow,
                CarbonIntensityGCO2ePerKwh = intensity.CarbonIntensity ?? 475.0,
                RenewablePercentage = breakdown.RenewablePercentage ?? 30.0,
                Source = GridDataSource.ElectricityMaps,
                ForecastHours = 0,
                MarginalIntensity = intensity.CarbonIntensity
            };

            _cache[cacheKey] = (data, DateTimeOffset.UtcNow.AddSeconds(CacheTtlSeconds));
            RecordSample(0, data.CarbonIntensityGCO2ePerKwh);
            return data;
        }
        catch (Exception)
        {
            // On failure, return cached data even if expired, or estimation
            if (_cache.TryGetValue(cacheKey, out var stale))
            {
                return stale.Data;
            }

            return CreateEstimatedData(zone);
        }
    }

    /// <summary>
    /// Fetches carbon intensity forecast for the specified region.
    /// </summary>
    /// <param name="region">Cloud region or Electricity Maps zone identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Forecasted grid carbon data.</returns>
    public async Task<GridCarbonData> GetForecastAsync(string region, CancellationToken ct = default)
    {
        var zone = ResolveZone(region);

        try
        {
            return await FetchForecastWithRetryAsync(zone, ct);
        }
        catch (Exception)
        {
            return CreateEstimatedData(zone, forecastHours: 24);
        }
    }

    /// <summary>
    /// Resolves a cloud provider region to an Electricity Maps zone,
    /// or returns the input if it is already a zone code.
    /// </summary>
    public static string ResolveZone(string region)
    {
        if (string.IsNullOrWhiteSpace(region))
            return "US-PJM"; // Default to US PJM

        return CloudRegionToEmZone.TryGetValue(region, out var zone) ? zone : region;
    }

    /// <summary>
    /// Fetches carbon intensity with exponential backoff retry for rate limits.
    /// </summary>
    private async Task<EmCarbonIntensityResponse> FetchCarbonIntensityWithRetryAsync(string zone, CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(1);

        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get,
                    $"{ApiEndpoint}/carbon-intensity/latest?zone={Uri.EscapeDataString(zone)}");
                request.Headers.Add("auth-token", ApiKey);

                var response = await _httpClient.SendAsync(request, ct);

                if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < MaxRetries)
                {
                    await Task.Delay(delay, ct);
                    delay *= 2;
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                {
                    throw new HttpRequestException($"Electricity Maps API authentication failed. Check API key.");
                }

                response.EnsureSuccessStatusCode();

                var result = await response.Content.ReadFromJsonAsync<EmCarbonIntensityResponse>(cancellationToken: ct);
                return result ?? new EmCarbonIntensityResponse();
            }
            catch (HttpRequestException) when (attempt < MaxRetries)
            {
                await Task.Delay(delay, ct);
                delay *= 2;
            }
        }

        return new EmCarbonIntensityResponse();
    }

    /// <summary>
    /// Fetches power breakdown (renewable vs fossil) with exponential backoff retry.
    /// </summary>
    private async Task<EmPowerBreakdownResponse> FetchPowerBreakdownWithRetryAsync(string zone, CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(1);

        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get,
                    $"{ApiEndpoint}/power-breakdown/latest?zone={Uri.EscapeDataString(zone)}");
                request.Headers.Add("auth-token", ApiKey);

                var response = await _httpClient.SendAsync(request, ct);

                if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < MaxRetries)
                {
                    await Task.Delay(delay, ct);
                    delay *= 2;
                    continue;
                }

                response.EnsureSuccessStatusCode();

                var result = await response.Content.ReadFromJsonAsync<EmPowerBreakdownResponse>(cancellationToken: ct);
                return result ?? new EmPowerBreakdownResponse();
            }
            catch (HttpRequestException) when (attempt < MaxRetries)
            {
                await Task.Delay(delay, ct);
                delay *= 2;
            }
        }

        return new EmPowerBreakdownResponse();
    }

    /// <summary>
    /// Fetches carbon intensity forecast with retry.
    /// </summary>
    private async Task<GridCarbonData> FetchForecastWithRetryAsync(string zone, CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(1);

        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get,
                    $"{ApiEndpoint}/carbon-intensity/forecast?zone={Uri.EscapeDataString(zone)}");
                request.Headers.Add("auth-token", ApiKey);

                var response = await _httpClient.SendAsync(request, ct);

                if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < MaxRetries)
                {
                    await Task.Delay(delay, ct);
                    delay *= 2;
                    continue;
                }

                response.EnsureSuccessStatusCode();

                var forecastResponse = await response.Content.ReadFromJsonAsync<EmForecastResponse>(cancellationToken: ct);
                if (forecastResponse?.Forecast == null || forecastResponse.Forecast.Count == 0)
                {
                    return CreateEstimatedData(zone, forecastHours: 24);
                }

                var avgIntensity = forecastResponse.Forecast.Average(f => f.CarbonIntensity ?? 475.0);
                var forecastHours = forecastResponse.Forecast.Count > 0
                    ? (int)Math.Ceiling((forecastResponse.Forecast[^1].Datetime - forecastResponse.Forecast[0].Datetime)?.TotalHours ?? 24)
                    : 24;

                return new GridCarbonData
                {
                    Region = zone,
                    Timestamp = DateTimeOffset.UtcNow,
                    CarbonIntensityGCO2ePerKwh = avgIntensity,
                    RenewablePercentage = EstimateRenewableFromIntensity(avgIntensity),
                    Source = GridDataSource.ElectricityMaps,
                    ForecastHours = Math.Max(1, forecastHours),
                    AverageIntensity = avgIntensity,
                    MarginalIntensity = forecastResponse.Forecast[0].CarbonIntensity
                };
            }
            catch (HttpRequestException) when (attempt < MaxRetries)
            {
                await Task.Delay(delay, ct);
                delay *= 2;
            }
        }

        return CreateEstimatedData(zone, forecastHours: 24);
    }

    /// <summary>
    /// Estimates renewable percentage from carbon intensity.
    /// Lower intensity implies higher renewable share.
    /// Scale: 0 gCO2/kWh -> 100% renewable, >= 900 gCO2/kWh -> 0%.
    /// </summary>
    private static double EstimateRenewableFromIntensity(double intensityGCO2ePerKwh)
    {
        var pct = Math.Max(0, Math.Min(100, (1 - intensityGCO2ePerKwh / 900.0) * 100));
        return Math.Round(pct, 1);
    }

    /// <summary>
    /// Creates estimated grid carbon data when the API is unavailable.
    /// Uses global average carbon intensity as a conservative fallback.
    /// </summary>
    private static GridCarbonData CreateEstimatedData(string zone, int forecastHours = 0)
    {
        return new GridCarbonData
        {
            Region = zone,
            Timestamp = DateTimeOffset.UtcNow,
            CarbonIntensityGCO2ePerKwh = 475.0,
            RenewablePercentage = 30.0,
            Source = GridDataSource.Estimation,
            ForecastHours = forecastHours,
            AverageIntensity = 475.0
        };
    }

    /// <inheritdoc/>
    protected override Task InitializeCoreAsync(CancellationToken ct)
    {
        _cache.Clear();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task DisposeCoreAsync()
    {
        _cache.Clear();
        return Task.CompletedTask;
    }

    #region Electricity Maps API DTOs

    private sealed class EmCarbonIntensityResponse
    {
        [JsonPropertyName("zone")]
        public string? Zone { get; set; }

        [JsonPropertyName("carbonIntensity")]
        public double? CarbonIntensity { get; set; }

        [JsonPropertyName("datetime")]
        public DateTimeOffset? Datetime { get; set; }

        [JsonPropertyName("updatedAt")]
        public DateTimeOffset? UpdatedAt { get; set; }

        [JsonPropertyName("emissionFactorType")]
        public string? EmissionFactorType { get; set; }

        [JsonPropertyName("isEstimated")]
        public bool? IsEstimated { get; set; }

        [JsonPropertyName("estimationMethod")]
        public string? EstimationMethod { get; set; }
    }

    private sealed class EmPowerBreakdownResponse
    {
        [JsonPropertyName("zone")]
        public string? Zone { get; set; }

        [JsonPropertyName("datetime")]
        public DateTimeOffset? Datetime { get; set; }

        [JsonPropertyName("renewablePercentage")]
        public double? RenewablePercentage { get; set; }

        [JsonPropertyName("fossilFreePercentage")]
        public double? FossilFreePercentage { get; set; }

        [JsonPropertyName("powerConsumptionTotal")]
        public double? PowerConsumptionTotal { get; set; }

        [JsonPropertyName("powerProductionTotal")]
        public double? PowerProductionTotal { get; set; }

        [JsonPropertyName("powerImportTotal")]
        public double? PowerImportTotal { get; set; }

        [JsonPropertyName("powerExportTotal")]
        public double? PowerExportTotal { get; set; }

        [JsonPropertyName("isEstimated")]
        public bool? IsEstimated { get; set; }
    }

    private sealed class EmForecastResponse
    {
        [JsonPropertyName("zone")]
        public string? Zone { get; set; }

        [JsonPropertyName("forecast")]
        public List<EmForecastPoint>? Forecast { get; set; }

        [JsonPropertyName("updatedAt")]
        public DateTimeOffset? UpdatedAt { get; set; }
    }

    private sealed class EmForecastPoint
    {
        [JsonPropertyName("carbonIntensity")]
        public double? CarbonIntensity { get; set; }

        [JsonPropertyName("datetime")]
        public DateTimeOffset? Datetime { get; set; }
    }

    #endregion
}
