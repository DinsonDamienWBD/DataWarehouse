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
/// Health probe for the Crypto Time Locks moonshot (MoonshotId.CryptoTimeLocks).
/// Checks plugin responsiveness, time lock provider availability (at least one required),
/// configuration validity, and bus connectivity.
/// </summary>
public sealed class TimeLockHealthProbe : IMoonshotHealthProbe
{
    private static readonly TimeSpan BusTimeout = TimeSpan.FromSeconds(5);

    private readonly IMessageBus _messageBus;
    private readonly MoonshotConfiguration _config;
    private readonly ILogger _logger;

    public MoonshotId MoonshotId => MoonshotId.CryptoTimeLocks;
    public TimeSpan HealthCheckInterval => TimeSpan.FromSeconds(60);

    public TimeLockHealthProbe(
        IMessageBus messageBus,
        MoonshotConfiguration config,
        ILogger<TimeLockHealthProbe> logger)
    {
        _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<MoonshotHealthReport> CheckHealthAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var components = new Dictionary<string, MoonshotComponentHealth>();

        var featureConfig = _config.GetEffectiveConfig(MoonshotId.CryptoTimeLocks);
        components["Config"] = featureConfig.Enabled
            ? new MoonshotComponentHealth("Config", MoonshotReadiness.Ready, "Crypto Time Locks enabled")
            : new MoonshotComponentHealth("Config", MoonshotReadiness.NotReady, "Crypto Time Locks disabled in configuration");

        components["Plugin"] = await CheckBusTopicAsync("tamperproof.timelock.health.ping", "Plugin", ct);

        components["Strategy"] = await CheckProvidersAsync(ct);

        components["Bus"] = await CheckBusTopicAsync("tamperproof.echo", "Bus", ct);

        sw.Stop();
        var overall = ComputeOverallReadiness(components);

        _logger.LogDebug("TimeLock health check completed in {Duration}ms: {Readiness}",
            sw.ElapsedMilliseconds, overall);

        return new MoonshotHealthReport(
            MoonshotId.CryptoTimeLocks,
            overall,
            $"Crypto Time Locks: {overall}",
            components,
            DateTimeOffset.UtcNow,
            sw.Elapsed);
    }

    private async Task<MoonshotComponentHealth> CheckProvidersAsync(CancellationToken ct)
    {
        try
        {
            var response = await _messageBus.SendAsync(
                "tamperproof.timelock.providers.list",
                new PluginMessage { Type = "strategies.list", SourcePluginId = "MoonshotHealthProbe" },
                BusTimeout, ct);

            if (!response.Success)
                return new MoonshotComponentHealth("Strategy", MoonshotReadiness.NotReady,
                    response.ErrorMessage ?? "Time lock provider list request failed");

            if (response.Payload is ICollection<object> providers && providers.Count > 0)
                return new MoonshotComponentHealth("Strategy", MoonshotReadiness.Ready,
                    $"{providers.Count} time lock provider(s) registered");

            if (response.Payload != null)
                return new MoonshotComponentHealth("Strategy", MoonshotReadiness.Ready,
                    "Time lock providers available");

            return new MoonshotComponentHealth("Strategy", MoonshotReadiness.Degraded,
                "No time lock providers registered");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new MoonshotComponentHealth("Strategy", MoonshotReadiness.NotReady,
                "Provider list request timed out");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TimeLock provider check failed");
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
            _logger.LogWarning(ex, "TimeLock health probe failed for topic {Topic}", topic);
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
