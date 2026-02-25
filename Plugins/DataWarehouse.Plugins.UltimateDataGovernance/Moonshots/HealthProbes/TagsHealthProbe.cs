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
/// Health probe for the Universal Tags moonshot (MoonshotId.UniversalTags).
/// Checks plugin responsiveness, schema availability, tag index status,
/// and bus connectivity via message bus ping/request patterns.
/// </summary>
public sealed class TagsHealthProbe : IMoonshotHealthProbe
{
    private static readonly TimeSpan BusTimeout = TimeSpan.FromSeconds(5);

    private readonly IMessageBus _messageBus;
    private readonly MoonshotConfiguration _config;
    private readonly ILogger _logger;

    public MoonshotId MoonshotId => MoonshotId.UniversalTags;
    public TimeSpan HealthCheckInterval => TimeSpan.FromSeconds(30);

    public TagsHealthProbe(
        IMessageBus messageBus,
        MoonshotConfiguration config,
        ILogger<TagsHealthProbe> logger)
    {
        _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<MoonshotHealthReport> CheckHealthAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var components = new Dictionary<string, MoonshotComponentHealth>();

        // Config check
        var featureConfig = _config.GetEffectiveConfig(MoonshotId.UniversalTags);
        components["Config"] = featureConfig.Enabled
            ? new MoonshotComponentHealth("Config", MoonshotReadiness.Ready, "Universal Tags enabled")
            : new MoonshotComponentHealth("Config", MoonshotReadiness.NotReady, "Universal Tags disabled in configuration");

        // Plugin ping check
        components["Plugin"] = await CheckBusTopicAsync("tags.health.ping", "Plugin", ct);

        // Strategy check - verify schema list returns at least the default schema
        components["Strategy"] = await CheckStrategyAsync("tags.schema.list", "Strategy",
            "No tag schemas registered", ct);

        // Bus check - verify index status
        components["Bus"] = await CheckBusTopicAsync("tags.index.status", "Bus", ct);

        sw.Stop();
        var overall = ComputeOverallReadiness(components);

        _logger.LogDebug("Tags health check completed in {Duration}ms: {Readiness}",
            sw.ElapsedMilliseconds, overall);

        return new MoonshotHealthReport(
            MoonshotId.UniversalTags,
            overall,
            $"Universal Tags: {overall}",
            components,
            DateTimeOffset.UtcNow,
            sw.Elapsed);
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
            _logger.LogWarning(ex, "Tags health probe failed for topic {Topic}", topic);
            return new MoonshotComponentHealth(componentName, MoonshotReadiness.Unknown,
                $"Error checking {topic}: {ex.Message}");
        }
    }

    private async Task<MoonshotComponentHealth> CheckStrategyAsync(
        string topic, string componentName, string emptyMessage, CancellationToken ct)
    {
        try
        {
            var response = await _messageBus.SendAsync(
                topic,
                new PluginMessage { Type = "strategies.list", SourcePluginId = "MoonshotHealthProbe" },
                BusTimeout, ct);

            if (!response.Success)
                return new MoonshotComponentHealth(componentName, MoonshotReadiness.NotReady,
                    response.ErrorMessage ?? "Strategy list request failed");

            if (response.Payload is ICollection<object> list && list.Count > 0)
                return new MoonshotComponentHealth(componentName, MoonshotReadiness.Ready,
                    $"{list.Count} schema(s) registered");

            if (response.Payload != null)
                return new MoonshotComponentHealth(componentName, MoonshotReadiness.Ready,
                    "Schemas available");

            return new MoonshotComponentHealth(componentName, MoonshotReadiness.Degraded, emptyMessage);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new MoonshotComponentHealth(componentName, MoonshotReadiness.NotReady,
                $"{topic} timed out");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tags strategy check failed for topic {Topic}", topic);
            return new MoonshotComponentHealth(componentName, MoonshotReadiness.Unknown,
                $"Error: {ex.Message}");
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
                case MoonshotReadiness.NotReady:
                    hasNotReady = true;
                    break;
                case MoonshotReadiness.Degraded:
                    hasDegraded = true;
                    break;
                case MoonshotReadiness.Unknown:
                    hasDegraded = true;
                    break;
            }
        }

        if (hasNotReady) return MoonshotReadiness.NotReady;
        if (hasDegraded) return MoonshotReadiness.Degraded;
        return MoonshotReadiness.Ready;
    }
}
