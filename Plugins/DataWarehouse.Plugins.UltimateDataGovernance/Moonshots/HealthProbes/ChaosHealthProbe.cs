using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Moonshots;
using DataWarehouse.SDK.Utilities;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateDataGovernance.Moonshots.HealthProbes;

/// <summary>
/// Health probe for the Chaos Vaccination moonshot (MoonshotId.ChaosVaccination).
/// Checks plugin responsiveness, vaccination schedule state, configuration validity,
/// and bus connectivity. Uses a longer health check interval (120s) because chaos
/// vaccination runs infrequently by design.
/// </summary>
public sealed class ChaosHealthProbe : IMoonshotHealthProbe
{
    private static readonly TimeSpan BusTimeout = TimeSpan.FromSeconds(5);

    private readonly IMessageBus _messageBus;
    private readonly MoonshotConfiguration _config;
    private readonly ILogger _logger;

    public MoonshotId MoonshotId => MoonshotId.ChaosVaccination;
    public TimeSpan HealthCheckInterval => TimeSpan.FromSeconds(120);

    public ChaosHealthProbe(
        IMessageBus messageBus,
        MoonshotConfiguration config,
        ILogger<ChaosHealthProbe> logger)
    {
        _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<MoonshotHealthReport> CheckHealthAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var components = new Dictionary<string, MoonshotComponentHealth>();

        var featureConfig = _config.GetEffectiveConfig(MoonshotId.ChaosVaccination);
        components["Config"] = featureConfig.Enabled
            ? new MoonshotComponentHealth("Config", MoonshotReadiness.Ready, "Chaos Vaccination enabled")
            : new MoonshotComponentHealth("Config", MoonshotReadiness.NotReady, "Chaos Vaccination disabled in configuration");

        components["Plugin"] = await CheckBusTopicAsync("chaos.health.ping", "Plugin", ct);

        components["Strategy"] = await CheckScheduleAsync(ct);

        components["Bus"] = await CheckBusTopicAsync("chaos.echo", "Bus", ct);

        sw.Stop();
        var overall = ComputeOverallReadiness(components);

        _logger.LogDebug("Chaos health check completed in {Duration}ms: {Readiness}",
            sw.ElapsedMilliseconds, overall);

        return new MoonshotHealthReport(
            MoonshotId.ChaosVaccination,
            overall,
            $"Chaos Vaccination: {overall}",
            components,
            DateTimeOffset.UtcNow,
            sw.Elapsed);
    }

    private async Task<MoonshotComponentHealth> CheckScheduleAsync(CancellationToken ct)
    {
        try
        {
            var response = await _messageBus.SendAsync(
                "chaos.schedule.status",
                new PluginMessage { Type = "strategies.list", SourcePluginId = "MoonshotHealthProbe" },
                BusTimeout, ct);

            if (!response.Success)
                return new MoonshotComponentHealth("Strategy", MoonshotReadiness.NotReady,
                    response.ErrorMessage ?? "Chaos schedule status request failed");

            if (response.Payload != null)
                return new MoonshotComponentHealth("Strategy", MoonshotReadiness.Ready,
                    "Vaccination schedule active");

            return new MoonshotComponentHealth("Strategy", MoonshotReadiness.Degraded,
                "No vaccination schedule configured");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new MoonshotComponentHealth("Strategy", MoonshotReadiness.NotReady,
                "Schedule status request timed out");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Chaos schedule check failed");
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
            _logger.LogWarning(ex, "Chaos health probe failed for topic {Topic}", topic);
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
