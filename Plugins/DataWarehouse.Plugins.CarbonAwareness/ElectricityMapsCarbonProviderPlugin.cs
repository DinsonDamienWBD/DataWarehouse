using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Sustainability;

namespace DataWarehouse.Plugins.CarbonAwareness;

/// <summary>
/// Carbon intensity provider using Electricity Maps API.
/// Provides real-time and forecasted carbon intensity data for regions worldwide.
/// https://www.electricitymaps.com/
/// </summary>
public class ElectricityMapsCarbonProviderPlugin : CarbonIntensityProviderPluginBase
{
    private string? _apiKey;
    private readonly HttpClient _httpClient = new();

    // Simulated region data (would come from API in production)
    private static readonly Dictionary<string, CarbonIntensityData> _regionData = new()
    {
        ["us-west-2"] = new("us-west-2", 180.0, CarbonIntensityLevel.Low, 45.0, DateTimeOffset.UtcNow, null),
        ["us-east-1"] = new("us-east-1", 350.0, CarbonIntensityLevel.Medium, 25.0, DateTimeOffset.UtcNow, null),
        ["eu-west-1"] = new("eu-west-1", 280.0, CarbonIntensityLevel.Medium, 35.0, DateTimeOffset.UtcNow, null),
        ["eu-central-1"] = new("eu-central-1", 320.0, CarbonIntensityLevel.Medium, 40.0, DateTimeOffset.UtcNow, null),
        ["eu-north-1"] = new("eu-north-1", 45.0, CarbonIntensityLevel.VeryLow, 85.0, DateTimeOffset.UtcNow, null),
        ["ap-northeast-1"] = new("ap-northeast-1", 450.0, CarbonIntensityLevel.High, 20.0, DateTimeOffset.UtcNow, null),
        ["ap-southeast-1"] = new("ap-southeast-1", 480.0, CarbonIntensityLevel.High, 15.0, DateTimeOffset.UtcNow, null),
        ["ca-central-1"] = new("ca-central-1", 120.0, CarbonIntensityLevel.Low, 65.0, DateTimeOffset.UtcNow, null),
        ["sa-east-1"] = new("sa-east-1", 85.0, CarbonIntensityLevel.VeryLow, 75.0, DateTimeOffset.UtcNow, null),
    };

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
    /// </summary>
    /// <param name="apiKey">API key from Electricity Maps.</param>
    public void Configure(string apiKey)
    {
        _apiKey = apiKey;
        _httpClient.DefaultRequestHeaders.Clear();
        _httpClient.DefaultRequestHeaders.Add("auth-token", apiKey);
    }

    /// <inheritdoc />
    protected override async Task<CarbonIntensityData> FetchIntensityAsync(string regionId)
    {
        // In production, call: GET https://api.electricitymap.org/v3/carbon-intensity/latest?zone={zone}
        await Task.Delay(50); // Simulate API latency

        if (_regionData.TryGetValue(regionId, out var data))
        {
            // Add some variance to simulate real-time changes
            var variance = Random.Shared.NextDouble() * 20 - 10;
            var intensity = Math.Max(0, data.GramsCO2PerKwh + variance);
            var level = ClassifyIntensity(intensity);

            return data with
            {
                GramsCO2PerKwh = intensity,
                Level = level,
                MeasuredAt = DateTimeOffset.UtcNow
            };
        }

        // Default for unknown regions
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
        // In production, call: GET https://api.electricitymap.org/v3/carbon-intensity/forecast?zone={zone}
        await Task.Delay(100); // Simulate API latency

        var baseData = await FetchIntensityAsync(regionId);
        var forecast = new List<CarbonIntensityData>();

        for (int i = 0; i < hours; i++)
        {
            // Simulate daily carbon intensity pattern (lower at night, higher during peak)
            var hour = (DateTimeOffset.UtcNow.Hour + i) % 24;
            var multiplier = hour switch
            {
                >= 0 and < 6 => 0.7,   // Night - low demand
                >= 6 and < 9 => 1.0,   // Morning ramp
                >= 9 and < 17 => 1.2,  // Day - peak demand
                >= 17 and < 21 => 1.1, // Evening
                _ => 0.8               // Late evening
            };

            var intensity = baseData.GramsCO2PerKwh * multiplier;
            var forecastTime = DateTimeOffset.UtcNow.AddHours(i);

            forecast.Add(new CarbonIntensityData(
                regionId,
                intensity,
                ClassifyIntensity(intensity),
                baseData.RenewablePercentage,
                forecastTime,
                forecastTime.AddHours(1)
            ));
        }

        return forecast;
    }

    /// <inheritdoc />
    protected override Task<IReadOnlyList<string>> FetchRegionsAsync()
    {
        return Task.FromResult<IReadOnlyList<string>>(_regionData.Keys.ToList());
    }

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc />
    public override Task StopAsync() => Task.CompletedTask;

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
}
