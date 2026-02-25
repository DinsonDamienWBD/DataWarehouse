using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DataWarehouse.SDK.Contracts.Carbon;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateSustainability.Strategies.GreenPlacement;

/// <summary>
/// Integrates with the WattTime API v3 (https://api.watttime.org/v3) to fetch real-time
/// marginal operating emissions rate (MOER) data for electricity grid regions.
/// Supports authentication with token caching, region mapping from cloud providers to
/// balancing authorities, response caching with configurable TTL, and exponential backoff
/// for rate-limited requests.
/// </summary>
public sealed class WattTimeGridApiStrategy : SustainabilityStrategyBase
{
    private const string DefaultApiEndpoint = "https://api.watttime.org/v3";
    private const int DefaultCacheTtlSeconds = 300; // 5 minutes
    private const int MaxRetries = 3;
    private const double MoerToGCO2ePerKwhFactor = 453.592 / 1000.0; // lbs CO2/MWh -> gCO2e/kWh

    private readonly HttpClient _httpClient;
    private readonly BoundedDictionary<string, (GridCarbonData Data, DateTimeOffset Expiry)> _cache = new BoundedDictionary<string, (GridCarbonData Data, DateTimeOffset Expiry)>(1000);

    private string? _bearerToken;
    private DateTimeOffset _tokenExpiry = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _authLock = new(1, 1);

    /// <summary>
    /// Maps common cloud provider regions to WattTime balancing authority identifiers.
    /// WattTime uses balancing authority abbreviations as region codes.
    /// </summary>
    private static readonly Dictionary<string, string> CloudRegionToBalancingAuthority = new(StringComparer.OrdinalIgnoreCase)
    {
        // AWS regions
        ["us-east-1"] = "PJM",
        ["us-east-2"] = "MISO",
        ["us-west-1"] = "CAISO",
        ["us-west-2"] = "BPAT",
        ["eu-west-1"] = "IE",
        ["eu-west-2"] = "GB",
        ["eu-central-1"] = "DE",
        ["eu-north-1"] = "SE",
        ["ap-southeast-1"] = "SG",
        ["ap-northeast-1"] = "JP-TK",
        ["ap-south-1"] = "IN-WR",
        ["ca-central-1"] = "IESO",
        // Azure regions
        ["eastus"] = "PJM",
        ["westus2"] = "BPAT",
        ["northeurope"] = "IE",
        ["westeurope"] = "NL",
        ["uksouth"] = "GB",
        ["swedencentral"] = "SE",
        // GCP regions
        ["us-central1"] = "MISO",
        ["us-east4"] = "PJM",
        ["europe-west1"] = "BE",
        ["europe-north1"] = "SE",
        ["asia-east1"] = "TW",
    };

    /// <summary>
    /// WattTime API username. Must be configured for the strategy to be available.
    /// </summary>
    public string? Username { get; set; }

    /// <summary>
    /// WattTime API password. Must be configured for the strategy to be available.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// WattTime API endpoint. Defaults to https://api.watttime.org/v3.
    /// </summary>
    public string ApiEndpoint { get; set; } = DefaultApiEndpoint;

    /// <summary>
    /// Cache time-to-live in seconds. Defaults to 300 (5 minutes).
    /// </summary>
    public int CacheTtlSeconds { get; set; } = DefaultCacheTtlSeconds;

    /// <inheritdoc/>
    public override string StrategyId => "watttime-grid-api";

    /// <inheritdoc/>
    public override string DisplayName => "WattTime Grid Carbon API";

    /// <inheritdoc/>
    public override SustainabilityCategory Category => SustainabilityCategory.CarbonAwareness;

    /// <inheritdoc/>
    public override SustainabilityCapabilities Capabilities =>
        SustainabilityCapabilities.ExternalIntegration |
        SustainabilityCapabilities.RealTimeMonitoring |
        SustainabilityCapabilities.CarbonCalculation;

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Real-time grid carbon intensity from WattTime API v3. " +
        "Fetches marginal operating emissions rate (MOER) data for electricity grid balancing authorities. " +
        "Supports US, EU, and APAC regions with automatic cloud-region-to-grid-zone mapping.";

    /// <inheritdoc/>
    public override string[] Tags => new[]
    {
        "carbon", "grid", "watttime", "moer", "real-time", "api",
        "emissions", "marginal", "balancing-authority"
    };

    /// <summary>
    /// Creates a new WattTimeGridApiStrategy with the provided HTTP client.
    /// Uses IHttpClientFactory pattern -- accepts HttpClient via constructor.
    /// </summary>
    /// <param name="httpClient">Pre-configured HttpClient instance (typically from IHttpClientFactory).</param>
    public WattTimeGridApiStrategy(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
    }

    /// <summary>
    /// Creates a new WattTimeGridApiStrategy with a default HttpClient.
    /// Provided for auto-discovery scenarios; prefer the HttpClient constructor for production.
    /// </summary>
    public WattTimeGridApiStrategy() : this(new HttpClient())
    {
    }

    /// <summary>
    /// Returns true if API credentials (username and password) are configured.
    /// </summary>
    public bool IsAvailable()
    {
        return !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(Password);
    }

    /// <summary>
    /// Fetches current grid carbon intensity data for the specified region.
    /// Uses cached data if available and not expired, otherwise queries WattTime API.
    /// Returns estimated data on API failure to ensure callers always get a result.
    /// </summary>
    /// <param name="region">Cloud region or WattTime balancing authority identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Grid carbon data with MOER-based intensity values.</returns>
    public async Task<GridCarbonData> GetGridCarbonDataAsync(string region, CancellationToken ct = default)
    {
        var balancingAuthority = ResolveBalancingAuthority(region);
        var cacheKey = $"watttime:{balancingAuthority}";

        // Check cache first
        if (_cache.TryGetValue(cacheKey, out var cached) && cached.Expiry > DateTimeOffset.UtcNow)
        {
            return cached.Data;
        }

        try
        {
            await EnsureAuthenticatedAsync(ct);

            var data = await FetchSignalIndexWithRetryAsync(balancingAuthority, ct);
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

            return CreateEstimatedData(balancingAuthority);
        }
    }

    /// <summary>
    /// Fetches 24-hour carbon intensity forecast for the specified region.
    /// </summary>
    /// <param name="region">Cloud region or WattTime balancing authority identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Forecasted grid carbon data.</returns>
    public async Task<GridCarbonData> GetForecastAsync(string region, CancellationToken ct = default)
    {
        var balancingAuthority = ResolveBalancingAuthority(region);

        try
        {
            await EnsureAuthenticatedAsync(ct);
            return await FetchForecastWithRetryAsync(balancingAuthority, ct);
        }
        catch (Exception)
        {
            return CreateEstimatedData(balancingAuthority, forecastHours: 24);
        }
    }

    /// <summary>
    /// Resolves a cloud provider region to a WattTime balancing authority,
    /// or returns the input if it is already a balancing authority code.
    /// </summary>
    public static string ResolveBalancingAuthority(string region)
    {
        if (string.IsNullOrWhiteSpace(region))
            return "PJM"; // Default to US PJM

        return CloudRegionToBalancingAuthority.TryGetValue(region, out var ba) ? ba : region;
    }

    /// <summary>
    /// Authenticates with the WattTime API using Basic auth and caches the bearer token.
    /// Re-authenticates when the token is expired or missing.
    /// </summary>
    private async Task EnsureAuthenticatedAsync(CancellationToken ct)
    {
        if (_bearerToken != null && _tokenExpiry > DateTimeOffset.UtcNow)
            return;

        await _authLock.WaitAsync(ct);
        try
        {
            // Double-check after acquiring lock
            if (_bearerToken != null && _tokenExpiry > DateTimeOffset.UtcNow)
                return;

            var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiEndpoint}/login");
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Username}:{Password}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);

            var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var loginResponse = await response.Content.ReadFromJsonAsync<WattTimeLoginResponse>(cancellationToken: ct);
            _bearerToken = loginResponse?.Token;

            // WattTime tokens are valid for 30 minutes; refresh at 25 minutes to be safe
            _tokenExpiry = DateTimeOffset.UtcNow.AddMinutes(25);
        }
        finally
        {
            _authLock.Release();
        }
    }

    /// <summary>
    /// Fetches signal index (MOER) data with exponential backoff retry for rate limits.
    /// </summary>
    private async Task<GridCarbonData> FetchSignalIndexWithRetryAsync(string balancingAuthority, CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(1);

        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get,
                    $"{ApiEndpoint}/signal-index?region={Uri.EscapeDataString(balancingAuthority)}&signal_type=co2_moer");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _bearerToken);

                var response = await _httpClient.SendAsync(request, ct);

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    // Token expired, force re-auth and retry
                    _bearerToken = null;
                    _tokenExpiry = DateTimeOffset.MinValue;
                    await EnsureAuthenticatedAsync(ct);
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    if (attempt < MaxRetries)
                    {
                        await Task.Delay(delay, ct);
                        delay *= 2; // Exponential backoff
                        continue;
                    }
                    throw new HttpRequestException("WattTime API rate limit exceeded after max retries.");
                }

                response.EnsureSuccessStatusCode();

                var signalResponse = await response.Content.ReadFromJsonAsync<WattTimeSignalResponse>(cancellationToken: ct);

                if (signalResponse?.Data == null || signalResponse.Data.Count == 0)
                {
                    return CreateEstimatedData(balancingAuthority);
                }

                var latestPoint = signalResponse.Data[0];
                var moer = latestPoint.Value;
                var carbonIntensity = moer * MoerToGCO2ePerKwhFactor;

                return new GridCarbonData
                {
                    Region = balancingAuthority,
                    Timestamp = latestPoint.PointTime ?? DateTimeOffset.UtcNow,
                    CarbonIntensityGCO2ePerKwh = carbonIntensity,
                    RenewablePercentage = EstimateRenewableFromMoer(moer),
                    Source = GridDataSource.WattTime,
                    ForecastHours = 0,
                    MarginalIntensity = carbonIntensity
                };
            }
            catch (HttpRequestException) when (attempt < MaxRetries)
            {
                await Task.Delay(delay, ct);
                delay *= 2;
            }
        }

        return CreateEstimatedData(balancingAuthority);
    }

    /// <summary>
    /// Fetches 24-hour forecast data with exponential backoff retry.
    /// </summary>
    private async Task<GridCarbonData> FetchForecastWithRetryAsync(string balancingAuthority, CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(1);

        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get,
                    $"{ApiEndpoint}/forecast?region={Uri.EscapeDataString(balancingAuthority)}");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _bearerToken);

                var response = await _httpClient.SendAsync(request, ct);

                if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < MaxRetries)
                {
                    await Task.Delay(delay, ct);
                    delay *= 2;
                    continue;
                }

                response.EnsureSuccessStatusCode();

                var forecastResponse = await response.Content.ReadFromJsonAsync<WattTimeSignalResponse>(cancellationToken: ct);
                if (forecastResponse?.Data == null || forecastResponse.Data.Count == 0)
                {
                    return CreateEstimatedData(balancingAuthority, forecastHours: 24);
                }

                // Average over forecast data points
                var avgMoer = forecastResponse.Data.Average(d => d.Value);
                var carbonIntensity = avgMoer * MoerToGCO2ePerKwhFactor;

                return new GridCarbonData
                {
                    Region = balancingAuthority,
                    Timestamp = DateTimeOffset.UtcNow,
                    CarbonIntensityGCO2ePerKwh = carbonIntensity,
                    RenewablePercentage = EstimateRenewableFromMoer(avgMoer),
                    Source = GridDataSource.WattTime,
                    ForecastHours = 24,
                    MarginalIntensity = forecastResponse.Data[0].Value * MoerToGCO2ePerKwhFactor,
                    AverageIntensity = carbonIntensity
                };
            }
            catch (HttpRequestException) when (attempt < MaxRetries)
            {
                await Task.Delay(delay, ct);
                delay *= 2;
            }
        }

        return CreateEstimatedData(balancingAuthority, forecastHours: 24);
    }

    /// <summary>
    /// Estimates renewable percentage from MOER value.
    /// Lower MOER indicates cleaner grid (higher renewable share).
    /// Scale: MOER 0 -> 100% renewable, MOER >= 2000 lbs/MWh -> 0% renewable.
    /// </summary>
    private static double EstimateRenewableFromMoer(double moer)
    {
        // MOER is in lbs CO2/MWh. Typical range: 0-2000.
        // 0 = fully renewable, 2000+ = heavily fossil
        var pct = Math.Max(0, Math.Min(100, (1 - moer / 2000.0) * 100));
        return Math.Round(pct, 1);
    }

    /// <summary>
    /// Creates estimated grid carbon data when the API is unavailable.
    /// Uses global average carbon intensity as a conservative fallback.
    /// </summary>
    private static GridCarbonData CreateEstimatedData(string region, int forecastHours = 0)
    {
        // Global average carbon intensity ~475 gCO2/kWh (IEA 2023)
        return new GridCarbonData
        {
            Region = region,
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
        // Pre-warm: clear stale cache entries on init
        _cache.Clear();
        _bearerToken = null;
        _tokenExpiry = DateTimeOffset.MinValue;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task DisposeCoreAsync()
    {
        _cache.Clear();
        _bearerToken = null;
        _authLock.Dispose();
        return Task.CompletedTask;
    }

    #region WattTime API DTOs

    private sealed class WattTimeLoginResponse
    {
        [JsonPropertyName("token")]
        public string? Token { get; set; }
    }

    private sealed class WattTimeSignalResponse
    {
        [JsonPropertyName("data")]
        public List<WattTimeDataPoint>? Data { get; set; }

        [JsonPropertyName("meta")]
        public WattTimeMetadata? Meta { get; set; }
    }

    private sealed class WattTimeDataPoint
    {
        [JsonPropertyName("point_time")]
        public DateTimeOffset? PointTime { get; set; }

        [JsonPropertyName("value")]
        public double Value { get; set; }

        [JsonPropertyName("frequency")]
        public int? Frequency { get; set; }

        [JsonPropertyName("market")]
        public string? Market { get; set; }
    }

    private sealed class WattTimeMetadata
    {
        [JsonPropertyName("region")]
        public string? Region { get; set; }

        [JsonPropertyName("signal_type")]
        public string? SignalType { get; set; }

        [JsonPropertyName("model")]
        public WattTimeModelInfo? Model { get; set; }
    }

    private sealed class WattTimeModelInfo
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("date")]
        public string? Date { get; set; }
    }

    #endregion
}
