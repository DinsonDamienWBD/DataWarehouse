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
/// Health probe for the Zero-Gravity Storage moonshot (MoonshotId.ZeroGravityStorage).
/// Checks plugin responsiveness, available storage nodes (CRUSH needs at least 3 for
/// proper distribution), configuration validity, and bus connectivity.
/// </summary>
public sealed class PlacementHealthProbe : IMoonshotHealthProbe
{
    private static readonly TimeSpan BusTimeout = TimeSpan.FromSeconds(5);
    private const int MinNodesForCrush = 3;

    private readonly IMessageBus _messageBus;
    private readonly MoonshotConfiguration _config;
    private readonly ILogger _logger;

    public MoonshotId MoonshotId => MoonshotId.ZeroGravityStorage;
    public TimeSpan HealthCheckInterval => TimeSpan.FromSeconds(30);

    public PlacementHealthProbe(
        IMessageBus messageBus,
        MoonshotConfiguration config,
        ILogger<PlacementHealthProbe> logger)
    {
        _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<MoonshotHealthReport> CheckHealthAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var components = new Dictionary<string, MoonshotComponentHealth>();

        var featureConfig = _config.GetEffectiveConfig(MoonshotId.ZeroGravityStorage);
        components["Config"] = featureConfig.Enabled
            ? new MoonshotComponentHealth("Config", MoonshotReadiness.Ready, "Zero-Gravity Storage enabled")
            : new MoonshotComponentHealth("Config", MoonshotReadiness.NotReady, "Zero-Gravity Storage disabled in configuration");

        components["Plugin"] = await CheckBusTopicAsync("storage.placement.health.ping", "Plugin", ct);

        components["Strategy"] = await CheckStorageNodesAsync(ct);

        components["Bus"] = await CheckBusTopicAsync("storage.echo", "Bus", ct);

        sw.Stop();
        var overall = ComputeOverallReadiness(components);

        _logger.LogDebug("Placement health check completed in {Duration}ms: {Readiness}",
            sw.ElapsedMilliseconds, overall);

        return new MoonshotHealthReport(
            MoonshotId.ZeroGravityStorage,
            overall,
            $"Zero-Gravity Storage: {overall}",
            components,
            DateTimeOffset.UtcNow,
            sw.Elapsed);
    }

    private async Task<MoonshotComponentHealth> CheckStorageNodesAsync(CancellationToken ct)
    {
        try
        {
            var response = await _messageBus.SendAsync(
                "storage.nodes.list",
                new PluginMessage { Type = "strategies.list", SourcePluginId = "MoonshotHealthProbe" },
                BusTimeout, ct);

            if (!response.Success)
                return new MoonshotComponentHealth("Strategy", MoonshotReadiness.NotReady,
                    response.ErrorMessage ?? "Storage node list request failed");

            if (response.Payload is ICollection<object> nodes)
            {
                if (nodes.Count == 0)
                    return new MoonshotComponentHealth("Strategy", MoonshotReadiness.NotReady,
                        "No storage nodes available");

                if (nodes.Count < MinNodesForCrush)
                    return new MoonshotComponentHealth("Strategy", MoonshotReadiness.Degraded,
                        $"Only {nodes.Count} storage node(s) available; CRUSH needs at least {MinNodesForCrush} for proper distribution",
                        new Dictionary<string, string> { ["node_count"] = nodes.Count.ToString() });

                return new MoonshotComponentHealth("Strategy", MoonshotReadiness.Ready,
                    $"{nodes.Count} storage node(s) available",
                    new Dictionary<string, string> { ["node_count"] = nodes.Count.ToString() });
            }

            if (response.Payload != null)
                return new MoonshotComponentHealth("Strategy", MoonshotReadiness.Ready,
                    "Storage nodes available");

            return new MoonshotComponentHealth("Strategy", MoonshotReadiness.Degraded,
                "No storage node information returned");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new MoonshotComponentHealth("Strategy", MoonshotReadiness.NotReady,
                "Storage node list request timed out");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Placement storage node check failed");
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
            _logger.LogWarning(ex, "Placement health probe failed for topic {Topic}", topic);
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
