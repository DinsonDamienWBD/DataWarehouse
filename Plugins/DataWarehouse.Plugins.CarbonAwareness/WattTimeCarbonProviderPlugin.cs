using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Sustainability;

namespace DataWarehouse.Plugins.CarbonAwareness;

/// <summary>
/// Carbon intensity provider using WattTime API.
/// Provides real-time and forecasted marginal emissions data for grid regions.
/// https://www.watttime.org/
/// API Documentation: https://docs.watttime.org/
/// </summary>
public class WattTimeCarbonProviderPlugin : CarbonIntensityProviderPluginBase
{
    private const string BaseUrl = "https://api.watttime.org";

    private readonly HttpClient _httpClient;
    private string? _username;
    private string? _password;
    private string? _token;
    private DateTimeOffset _tokenExpiry = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _authLock = new(1, 1);
    private bool _disposed;

    // WattTime balancing authority regions (US grid operators)
    // See: https://docs.watttime.org/guides/grid-regions
    private static readonly Dictionary<string, string> CloudRegionToBaMapping = new(StringComparer.OrdinalIgnoreCase)
    {
        // AWS regions
        ["us-west-2"] = "BPAT",              // Bonneville Power Administration
        ["us-east-1"] = "PJM",               // PJM Interconnection
        ["us-west-1"] = "CAISO_NORTH",       // California ISO North
        ["us-central-1"] = "MISO",           // Midcontinent ISO
        ["us-southwest-1"] = "AZPS",         // Arizona Public Service
        ["us-texas-1"] = "ERCOT",            // Electric Reliability Council of Texas

        // Azure regions
        ["westus2"] = "BPAT",
        ["westus3"] = "AZPS",
        ["eastus"] = "PJM",
        ["eastus2"] = "PJM",
        ["centralus"] = "SPP",               // Southwest Power Pool
        ["southcentralus"] = "ERCOT",

        // GCP regions
        ["us-west1"] = "CAISO_NORTH",
        ["us-west2"] = "CAISO_SOUTH",
        ["us-west3"] = "PACE",
        ["us-west4"] = "NEVP",
        ["us-east4"] = "PJM",
        ["us-central1"] = "MISO",
    };

    // Cache for regions list
    private IReadOnlyList<string>? _cachedRegions;
    private DateTimeOffset _regionsCacheExpiry = DateTimeOffset.MinValue;
    private readonly TimeSpan _regionsCacheDuration = TimeSpan.FromHours(24);

    /// <summary>
    /// Initializes a new instance with a default HttpClient.
    /// </summary>
    public WattTimeCarbonProviderPlugin() : this(new HttpClient())
    {
    }

    /// <summary>
    /// Initializes a new instance with a custom HttpClient for testing.
    /// </summary>
    /// <param name="httpClient">HTTP client to use for API calls.</param>
    public WattTimeCarbonProviderPlugin(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _httpClient.BaseAddress = new Uri(BaseUrl);
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
    }

    /// <inheritdoc />
    public override string Id => "datawarehouse.carbon.watttime";

    /// <inheritdoc />
    public override string Name => "WattTime Carbon Provider";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    public override PluginCategory Category => PluginCategory.GovernanceProvider;

    /// <summary>
    /// Cache duration for WattTime is 5 minutes (data updates every 5 minutes).
    /// </summary>
    protected override TimeSpan CacheDuration => TimeSpan.FromMinutes(5);

    /// <summary>
    /// Configures WattTime credentials.
    /// Register at: https://www.watttime.org/api-documentation/
    /// </summary>
    /// <param name="username">WattTime username.</param>
    /// <param name="password">WattTime password.</param>
    public void Configure(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username))
            throw new ArgumentException("Username cannot be null or empty", nameof(username));
        if (string.IsNullOrWhiteSpace(password))
            throw new ArgumentException("Password cannot be null or empty", nameof(password));

        _username = username;
        _password = password;
        _token = null;
        _tokenExpiry = DateTimeOffset.MinValue;
    }

    /// <inheritdoc />
    protected override async Task<CarbonIntensityData> FetchIntensityAsync(string regionId)
    {
        EnsureConfigured();
        await EnsureAuthenticatedAsync();

        var ba = MapRegionToBa(regionId);

        try
        {
            // GET /v3/signal-index?region={ba}
            // Returns the signal index (0-100) where higher = dirtier grid
            var request = new HttpRequestMessage(HttpMethod.Get, $"/v3/signal-index?region={Uri.EscapeDataString(ba)}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<WattTimeSignalResponse>(JsonOptions);

            if (result == null)
            {
                throw new InvalidOperationException($"Empty response from WattTime API for region {ba}");
            }

            // WattTime v3 returns a signal index (0-100), we need to convert to gCO2/kWh
            // Higher signal = higher marginal emissions
            // Approximate conversion: signal 0 = ~50 gCO2/kWh, signal 100 = ~900 gCO2/kWh
            var intensity = ConvertSignalToIntensity(result.Data[0].Value);
            var level = ClassifyIntensity(intensity);

            // Calculate renewable percentage (inverse of signal)
            var renewablePercent = Math.Max(0, 100 - result.Data[0].Value);

            return new CarbonIntensityData(
                regionId,
                intensity,
                level,
                renewablePercent,
                result.Data[0].PointTime,
                null
            );
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Failed to fetch carbon intensity for region {ba}: {ex.Message}", ex);
        }
    }

    /// <inheritdoc />
    protected override async Task<IReadOnlyList<CarbonIntensityData>> FetchForecastAsync(string regionId, int hours)
    {
        EnsureConfigured();
        await EnsureAuthenticatedAsync();

        var ba = MapRegionToBa(regionId);

        try
        {
            // GET /v3/forecast?region={ba}
            var request = new HttpRequestMessage(HttpMethod.Get, $"/v3/forecast?region={Uri.EscapeDataString(ba)}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<WattTimeForecastResponse>(JsonOptions);

            if (result?.Data == null || result.Data.Count == 0)
            {
                throw new InvalidOperationException($"No forecast data available for region {ba}");
            }

            // Filter to requested hours and convert to our data model
            var endTime = DateTimeOffset.UtcNow.AddHours(hours);
            var forecast = result.Data
                .Where(f => f.PointTime <= endTime)
                .OrderBy(f => f.PointTime)
                .Select(f =>
                {
                    var intensity = ConvertSignalToIntensity(f.Value);
                    return new CarbonIntensityData(
                        regionId,
                        intensity,
                        ClassifyIntensity(intensity),
                        Math.Max(0, 100 - f.Value), // Approximate renewable %
                        f.PointTime,
                        f.PointTime.AddMinutes(5) // WattTime forecasts are 5-minute intervals
                    );
                })
                .ToList();

            return forecast;
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Failed to fetch forecast for region {ba}: {ex.Message}", ex);
        }
    }

    /// <inheritdoc />
    protected override async Task<IReadOnlyList<string>> FetchRegionsAsync()
    {
        EnsureConfigured();
        await EnsureAuthenticatedAsync();

        // Return cached regions if still valid
        if (_cachedRegions != null && DateTimeOffset.UtcNow < _regionsCacheExpiry)
        {
            return _cachedRegions;
        }

        try
        {
            // GET /v3/ba-access
            var request = new HttpRequestMessage(HttpMethod.Get, "/v3/ba-access");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<WattTimeRegionsResponse>(JsonOptions);

            if (result?.SignalRegions == null)
            {
                throw new InvalidOperationException("Empty response from WattTime regions API");
            }

            // Get the list of regions the user has access to
            var regions = result.SignalRegions.SelectMany(sr => sr.Regions).ToList();
            var cloudRegions = CloudRegionToBaMapping.Keys.ToList();

            _cachedRegions = regions.Concat(cloudRegions).Distinct().ToList();
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
        _token = null;
        _tokenExpiry = DateTimeOffset.MinValue;
        ClearCache();
        _cachedRegions = null;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Maps a cloud provider region to a WattTime balancing authority.
    /// </summary>
    /// <param name="regionId">Cloud region ID or WattTime BA code.</param>
    /// <returns>WattTime balancing authority code.</returns>
    public string MapRegionToBa(string regionId)
    {
        // First check if it's a known cloud region
        if (CloudRegionToBaMapping.TryGetValue(regionId, out var ba))
        {
            return ba;
        }

        // Otherwise assume it's already a WattTime BA code
        return regionId;
    }

    /// <summary>
    /// Adds a custom region to balancing authority mapping.
    /// </summary>
    /// <param name="regionId">Cloud region identifier.</param>
    /// <param name="ba">WattTime balancing authority code.</param>
    public void AddRegionMapping(string regionId, string ba)
    {
        CloudRegionToBaMapping[regionId] = ba;
    }

    /// <summary>
    /// Gets the MOER (Marginal Operating Emissions Rate) for a region.
    /// This is the actual gCO2/kWh value from historical data.
    /// </summary>
    /// <param name="regionId">Region identifier.</param>
    /// <param name="startTime">Start time for historical data.</param>
    /// <param name="endTime">End time for historical data.</param>
    /// <returns>List of MOER data points.</returns>
    public async Task<IReadOnlyList<MoerDataPoint>> GetHistoricalMoerAsync(
        string regionId,
        DateTimeOffset startTime,
        DateTimeOffset endTime)
    {
        EnsureConfigured();
        await EnsureAuthenticatedAsync();

        var ba = MapRegionToBa(regionId);

        try
        {
            // GET /v3/historical?region={ba}&start={start}&end={end}
            var url = $"/v3/historical?region={Uri.EscapeDataString(ba)}" +
                      $"&start={startTime:yyyy-MM-ddTHH:mm:ssZ}" +
                      $"&end={endTime:yyyy-MM-ddTHH:mm:ssZ}";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<WattTimeHistoricalResponse>(JsonOptions);

            if (result?.Data == null)
            {
                return Array.Empty<MoerDataPoint>();
            }

            return result.Data
                .Select(d => new MoerDataPoint(
                    d.PointTime,
                    d.Value,
                    d.Version ?? "unknown"
                ))
                .ToList();
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Failed to fetch historical MOER for region {ba}: {ex.Message}", ex);
        }
    }

    private async Task EnsureAuthenticatedAsync()
    {
        // Check if token is still valid (with 1 minute buffer)
        if (!string.IsNullOrEmpty(_token) && DateTimeOffset.UtcNow.AddMinutes(1) < _tokenExpiry)
        {
            return;
        }

        await _authLock.WaitAsync();
        try
        {
            // Double-check after acquiring lock
            if (!string.IsNullOrEmpty(_token) && DateTimeOffset.UtcNow.AddMinutes(1) < _tokenExpiry)
            {
                return;
            }

            // GET /login with Basic auth
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_username}:{_password}"));
            var request = new HttpRequestMessage(HttpMethod.Get, "/login");
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<WattTimeLoginResponse>(JsonOptions);

            if (result?.Token == null)
            {
                throw new InvalidOperationException("Failed to obtain WattTime access token");
            }

            _token = result.Token;
            // WattTime tokens typically expire in 30 minutes
            _tokenExpiry = DateTimeOffset.UtcNow.AddMinutes(30);
        }
        finally
        {
            _authLock.Release();
        }
    }

    private void EnsureConfigured()
    {
        if (string.IsNullOrEmpty(_username) || string.IsNullOrEmpty(_password))
        {
            throw new InvalidOperationException(
                "WattTime credentials not configured. Call Configure(username, password) before using this provider.");
        }
    }

    /// <summary>
    /// Converts WattTime signal index (0-100) to approximate gCO2/kWh.
    /// Signal index 0 = very clean grid, 100 = very dirty grid.
    /// </summary>
    private static double ConvertSignalToIntensity(double signalIndex)
    {
        // Linear approximation: signal 0 = 50 gCO2/kWh, signal 100 = 900 gCO2/kWh
        // This is an approximation; actual MOER varies by region
        return 50 + (signalIndex * 8.5);
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
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    /// <summary>
    /// Disposes resources.
    /// </summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _authLock.Dispose();
            _httpClient.Dispose();
            _disposed = true;
        }
    }

    // API Response Models

    private sealed class WattTimeLoginResponse
    {
        [JsonPropertyName("token")]
        public string? Token { get; set; }
    }

    private sealed class WattTimeSignalResponse
    {
        [JsonPropertyName("meta")]
        public WattTimeMeta? Meta { get; set; }

        [JsonPropertyName("data")]
        public List<WattTimeDataPoint> Data { get; set; } = new();
    }

    private sealed class WattTimeForecastResponse
    {
        [JsonPropertyName("meta")]
        public WattTimeMeta? Meta { get; set; }

        [JsonPropertyName("data")]
        public List<WattTimeDataPoint> Data { get; set; } = new();
    }

    private sealed class WattTimeHistoricalResponse
    {
        [JsonPropertyName("meta")]
        public WattTimeMeta? Meta { get; set; }

        [JsonPropertyName("data")]
        public List<WattTimeHistoricalDataPoint> Data { get; set; } = new();
    }

    private sealed class WattTimeMeta
    {
        [JsonPropertyName("region")]
        public string? Region { get; set; }

        [JsonPropertyName("signal_type")]
        public string? SignalType { get; set; }
    }

    private sealed class WattTimeDataPoint
    {
        [JsonPropertyName("point_time")]
        public DateTimeOffset PointTime { get; set; }

        [JsonPropertyName("value")]
        public double Value { get; set; }
    }

    private sealed class WattTimeHistoricalDataPoint
    {
        [JsonPropertyName("point_time")]
        public DateTimeOffset PointTime { get; set; }

        [JsonPropertyName("value")]
        public double Value { get; set; }

        [JsonPropertyName("version")]
        public string? Version { get; set; }
    }

    private sealed class WattTimeRegionsResponse
    {
        [JsonPropertyName("signal_regions")]
        public List<SignalRegion> SignalRegions { get; set; } = new();
    }

    private sealed class SignalRegion
    {
        [JsonPropertyName("signal_type")]
        public string? SignalType { get; set; }

        [JsonPropertyName("regions")]
        public List<string> Regions { get; set; } = new();
    }

    /// <summary>
    /// MOER (Marginal Operating Emissions Rate) data point.
    /// </summary>
    public record MoerDataPoint(
        DateTimeOffset Timestamp,
        double Value,
        string Version
    );
}
