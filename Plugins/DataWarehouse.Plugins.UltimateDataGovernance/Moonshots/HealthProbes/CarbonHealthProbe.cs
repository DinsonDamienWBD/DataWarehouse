using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Moonshots;
using DataWarehouse.SDK.Utilities;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateDataGovernance.Moonshots.HealthProbes;

/// <summary>
/// Health probe for the Carbon-Aware Lifecycle moonshot (MoonshotId.CarbonAwareLifecycle).
/// Checks plugin responsiveness, carbon intensity data freshness (marks Degraded if data
/// is stale beyond 1 hour), configuration validity, and bus connectivity.
/// Uses a longer health check interval (300s) as carbon data changes slowly.
/// </summary>
public sealed class CarbonHealthProbe : IMoonshotHealthProbe
{
    private static readonly TimeSpan BusTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxCarbonDataAge = TimeSpan.FromHours(1);

    private readonly IMessageBus _messageBus;
    private readonly MoonshotConfiguration _config;
    private readonly ILogger _logger;

    public MoonshotId MoonshotId => MoonshotId.CarbonAwareLifecycle;
    public TimeSpan HealthCheckInterval => TimeSpan.FromSeconds(300);

    public CarbonHealthProbe(
        IMessageBus messageBus,
        MoonshotConfiguration config,
        ILogger<CarbonHealthProbe> logger)
    {
        _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<MoonshotHealthReport> CheckHealthAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var components = new Dictionary<string, MoonshotComponentHealth>();

        var featureConfig = _config.GetEffectiveConfig(MoonshotId.CarbonAwareLifecycle);
        components["Config"] = featureConfig.Enabled
            ? new MoonshotComponentHealth("Config", MoonshotReadiness.Ready, "Carbon-Aware Lifecycle enabled")
            : new MoonshotComponentHealth("Config", MoonshotReadiness.NotReady, "Carbon-Aware Lifecycle disabled in configuration");

        components["Plugin"] = await CheckBusTopicAsync("carbon.health.ping", "Plugin", ct);

        components["Strategy"] = await CheckCarbonIntensityAsync(ct);

        components["Bus"] = await CheckBusTopicAsync("carbon.echo", "Bus", ct);

        sw.Stop();
        var overall = ComputeOverallReadiness(components);

        _logger.LogDebug("Carbon health check completed in {Duration}ms: {Readiness}",
            sw.ElapsedMilliseconds, overall);

        return new MoonshotHealthReport(
            MoonshotId.CarbonAwareLifecycle,
            overall,
            $"Carbon-Aware Lifecycle: {overall}",
            components,
            DateTimeOffset.UtcNow,
            sw.Elapsed);
    }

    private async Task<MoonshotComponentHealth> CheckCarbonIntensityAsync(CancellationToken ct)
    {
        try
        {
            var response = await _messageBus.SendAsync(
                "carbon.intensity.current",
                new PluginMessage { Type = "strategies.list", SourcePluginId = "MoonshotHealthProbe" },
                BusTimeout, ct);

            if (!response.Success)
                return new MoonshotComponentHealth("Strategy", MoonshotReadiness.NotReady,
                    response.ErrorMessage ?? "Carbon intensity request failed");

            if (response.Payload == null)
                return new MoonshotComponentHealth("Strategy", MoonshotReadiness.Degraded,
                    "No carbon intensity data available");

            // Check if carbon data includes a timestamp and whether it is stale
            if (response.Metadata.TryGetValue("timestamp", out var tsObj))
            {
                if (tsObj is DateTimeOffset ts)
                {
                    var age = DateTimeOffset.UtcNow - ts;
                    if (age > MaxCarbonDataAge)
                        return new MoonshotComponentHealth("Strategy", MoonshotReadiness.Degraded,
                            $"Carbon intensity data is stale ({age.TotalMinutes:F0} minutes old; max {MaxCarbonDataAge.TotalMinutes:F0} minutes)",
                            new Dictionary<string, string>
                            {
                                ["data_age_minutes"] = age.TotalMinutes.ToString("F1", CultureInfo.InvariantCulture)
                            });
                }
                else if (tsObj is string tsStr && DateTimeOffset.TryParse(tsStr, CultureInfo.InvariantCulture,
                             DateTimeStyles.None, out var parsedTs))
                {
                    var age = DateTimeOffset.UtcNow - parsedTs;
                    if (age > MaxCarbonDataAge)
                        return new MoonshotComponentHealth("Strategy", MoonshotReadiness.Degraded,
                            $"Carbon intensity data is stale ({age.TotalMinutes:F0} minutes old; max {MaxCarbonDataAge.TotalMinutes:F0} minutes)",
                            new Dictionary<string, string>
                            {
                                ["data_age_minutes"] = age.TotalMinutes.ToString("F1", CultureInfo.InvariantCulture)
                            });
                }
            }

            return new MoonshotComponentHealth("Strategy", MoonshotReadiness.Ready,
                "Carbon intensity data available and fresh");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new MoonshotComponentHealth("Strategy", MoonshotReadiness.NotReady,
                "Carbon intensity request timed out");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Carbon intensity check failed");
            return new MoonshotComponentHealth("Strategy", MoonshotReadiness.Unknown,
                $"Error: {ex.Message}");
        }
    }

    private async Task<MoonshotComponentHealth> CheckBusTopicAsync(
        string topic, string componentName, CancellationToken ct)
    {
        try
        {
            var response = await _messageBus.SendAsync(
                topic,
                new PluginMessage { Type = "health.ping", SourcePluginId = "MoonshotHealthProbe" },
                BusTimeout, ct);

            return response.Success
                ? new MoonshotComponentHealth(componentName, MoonshotReadiness.Ready, $"{topic} responsive")
                : new MoonshotComponentHealth(componentName, MoonshotReadiness.NotReady,
                    response.ErrorMessage ?? $"{topic} returned error");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new MoonshotComponentHealth(componentName, MoonshotReadiness.NotReady,
                $"{topic} timed out after {BusTimeout.TotalSeconds}s");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Carbon health probe failed for topic {Topic}", topic);
            return new MoonshotComponentHealth(componentName, MoonshotReadiness.Unknown,
                $"Error checking {topic}: {ex.Message}");
        }
    }

    private static MoonshotReadiness ComputeOverallReadiness(
        Dictionary<string, MoonshotComponentHealth> components)
    {
        var hasNotReady = false;
        var hasDegraded = false;

        foreach (var component in components.Values)
        {
            switch (component.Readiness)
            {
                case MoonshotReadiness.NotReady: hasNotReady = true; break;
                case MoonshotReadiness.Degraded: hasDegraded = true; break;
                case MoonshotReadiness.Unknown: hasDegraded = true; break;
            }
        }

        if (hasNotReady) return MoonshotReadiness.NotReady;
        if (hasDegraded) return MoonshotReadiness.Degraded;
        return MoonshotReadiness.Ready;
    }
}
