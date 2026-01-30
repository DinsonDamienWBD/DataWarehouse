using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Sustainability;

namespace DataWarehouse.Plugins.CarbonAwareness;

/// <summary>
/// Carbon intensity provider using WattTime API.
/// Provides real-time and forecasted marginal emissions data for US grid regions.
/// https://www.watttime.org/
/// </summary>
public class WattTimeCarbonProviderPlugin : CarbonIntensityProviderPluginBase
{
    private string? _username;
    private string? _password;
    private string? _token;
    private DateTimeOffset _tokenExpiry = DateTimeOffset.MinValue;
    private readonly HttpClient _httpClient = new();

    // WattTime balancing authority regions (US-focused)
    private static readonly Dictionary<string, (string Ba, double BaseIntensity)> _regionMapping = new()
    {
        ["us-west-2"] = ("BPAT", 150.0),      // Bonneville Power Administration
        ["us-east-1"] = ("PJM", 380.0),       // PJM Interconnection
        ["us-west-1"] = ("CAISO_NORTH", 220.0), // California ISO North
        ["us-central-1"] = ("MISO", 450.0),   // Midcontinent ISO
        ["us-southwest-1"] = ("AZPS", 400.0), // Arizona Public Service
        ["us-texas-1"] = ("ERCOT", 420.0),    // Electric Reliability Council of Texas
        ["us-midwest-1"] = ("SPP", 480.0),    // Southwest Power Pool
        ["us-southeast-1"] = ("SOCO", 410.0), // Southern Company
    };

    /// <inheritdoc />
    public override string Id => "datawarehouse.carbon.watttime";

    /// <inheritdoc />
    public override string Name => "WattTime Carbon Provider";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    public override PluginCategory Category => PluginCategory.GovernanceProvider;

    /// <summary>
    /// Cache duration for WattTime is 5 minutes.
    /// </summary>
    protected override TimeSpan CacheDuration => TimeSpan.FromMinutes(5);

    /// <summary>
    /// Configures WattTime credentials.
    /// </summary>
    /// <param name="username">WattTime username.</param>
    /// <param name="password">WattTime password.</param>
    public void Configure(string username, string password)
    {
        _username = username;
        _password = password;
    }

    /// <inheritdoc />
    protected override async Task<CarbonIntensityData> FetchIntensityAsync(string regionId)
    {
        await EnsureAuthenticatedAsync();

        // In production, call: GET https://api.watttime.org/v3/signal-index?region={ba}
        await Task.Delay(50); // Simulate API latency

        if (_regionMapping.TryGetValue(regionId, out var mapping))
        {
            // Simulate MOER (Marginal Operating Emissions Rate) data
            var variance = Random.Shared.NextDouble() * 50 - 25;
            var intensity = Math.Max(0, mapping.BaseIntensity + variance);
            var level = ClassifyIntensity(intensity);
            var renewable = CalculateRenewable(intensity);

            return new CarbonIntensityData(
                regionId,
                intensity,
                level,
                renewable,
                DateTimeOffset.UtcNow,
                null
            );
        }

        // Unknown region
        return new CarbonIntensityData(
            regionId,
            400.0,
            CarbonIntensityLevel.High,
            20.0,
            DateTimeOffset.UtcNow,
            null
        );
    }

    /// <inheritdoc />
    protected override async Task<IReadOnlyList<CarbonIntensityData>> FetchForecastAsync(string regionId, int hours)
    {
        await EnsureAuthenticatedAsync();

        // In production, call: GET https://api.watttime.org/v3/forecast?region={ba}
        await Task.Delay(100); // Simulate API latency

        var forecast = new List<CarbonIntensityData>();

        if (!_regionMapping.TryGetValue(regionId, out var mapping))
        {
            mapping = ("UNKNOWN", 400.0);
        }

        for (int i = 0; i < hours; i++)
        {
            // WattTime uses 5-minute intervals, aggregate to hourly
            var hour = (DateTimeOffset.UtcNow.Hour + i) % 24;
            var dayOfWeek = DateTimeOffset.UtcNow.AddHours(i).DayOfWeek;

            // Weekend typically has lower demand
            var weekendMultiplier = dayOfWeek == DayOfWeek.Saturday || dayOfWeek == DayOfWeek.Sunday ? 0.85 : 1.0;

            // Time-of-day pattern
            var todMultiplier = hour switch
            {
                >= 0 and < 5 => 0.65,   // Deep night - lowest
                >= 5 and < 8 => 0.85,   // Early morning
                >= 8 and < 12 => 1.1,   // Morning peak
                >= 12 and < 14 => 1.0,  // Lunch dip
                >= 14 and < 19 => 1.2,  // Afternoon peak
                >= 19 and < 22 => 0.95, // Evening
                _ => 0.75               // Late night
            };

            var intensity = mapping.BaseIntensity * todMultiplier * weekendMultiplier;
            var forecastTime = DateTimeOffset.UtcNow.AddHours(i);

            forecast.Add(new CarbonIntensityData(
                regionId,
                intensity,
                ClassifyIntensity(intensity),
                CalculateRenewable(intensity),
                forecastTime,
                forecastTime.AddHours(1)
            ));
        }

        return forecast;
    }

    /// <inheritdoc />
    protected override Task<IReadOnlyList<string>> FetchRegionsAsync()
    {
        return Task.FromResult<IReadOnlyList<string>>(_regionMapping.Keys.ToList());
    }

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc />
    public override Task StopAsync()
    {
        _token = null;
        _tokenExpiry = DateTimeOffset.MinValue;
        return Task.CompletedTask;
    }

    private async Task EnsureAuthenticatedAsync()
    {
        if (!string.IsNullOrEmpty(_token) && DateTimeOffset.UtcNow < _tokenExpiry)
            return;

        // In production, call: GET https://api.watttime.org/login with Basic auth
        await Task.Delay(20); // Simulate auth latency
        _token = $"wt_token_{Guid.NewGuid():N}";
        _tokenExpiry = DateTimeOffset.UtcNow.AddMinutes(30);
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

    private static double CalculateRenewable(double intensity)
    {
        // Inverse relationship: lower intensity = higher renewable
        return Math.Max(0, Math.Min(100, 100 - (intensity / 6)));
    }
}
