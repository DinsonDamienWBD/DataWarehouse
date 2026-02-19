using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Moonshots;
using DataWarehouse.SDK.Utilities;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateDataGovernance.Moonshots.CrossMoonshot;

/// <summary>
/// Wires chaos vaccination immune memory to sovereignty zone resilience scoring.
/// When a chaos experiment completes and the system survives, the immune memory
/// entry feeds into sovereignty zone resilience scoring. This creates a feedback
/// loop where zones that survive more faults get higher resilience scores.
/// This bridges ChaosVaccination -> SovereigntyMesh.
/// </summary>
public sealed class ChaosImmunityWiring
{
    private readonly IMessageBus _messageBus;
    private readonly MoonshotConfiguration _config;
    private readonly ILogger _logger;
    private IDisposable? _experimentSubscription;
    private IDisposable? _immuneMemorySubscription;

    public ChaosImmunityWiring(
        IMessageBus messageBus,
        MoonshotConfiguration config,
        ILogger logger)
    {
        _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Registers bus subscriptions for chaos experiment and immune memory events.
    /// Only activates if both ChaosVaccination and SovereigntyMesh moonshots are enabled.
    /// </summary>
    public Task RegisterAsync(CancellationToken ct)
    {
        if (!_config.IsEnabled(MoonshotId.ChaosVaccination) ||
            !_config.IsEnabled(MoonshotId.SovereigntyMesh))
        {
            _logger.LogInformation(
                "ChaosImmunityWiring skipped: ChaosVaccination={ChaosEnabled}, SovereigntyMesh={SovereigntyEnabled}",
                _config.IsEnabled(MoonshotId.ChaosVaccination),
                _config.IsEnabled(MoonshotId.SovereigntyMesh));
            return Task.CompletedTask;
        }

        _experimentSubscription = _messageBus.Subscribe(
            "chaos.experiment.completed",
            HandleExperimentCompletedAsync);

        _immuneMemorySubscription = _messageBus.Subscribe(
            "chaos.immune.memory.created",
            HandleImmuneMemoryCreatedAsync);

        _logger.LogInformation(
            "ChaosImmunityWiring registered: chaos.experiment.completed -> sovereignty.zone.resilience.update, " +
            "chaos.immune.memory.created -> moonshot.health.resilience.improved");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Unregisters all bus subscriptions and releases resources.
    /// </summary>
    public Task UnregisterAsync()
    {
        _experimentSubscription?.Dispose();
        _experimentSubscription = null;
        _immuneMemorySubscription?.Dispose();
        _immuneMemorySubscription = null;
        _logger.LogInformation("ChaosImmunityWiring unregistered");
        return Task.CompletedTask;
    }

    private async Task HandleExperimentCompletedAsync(PluginMessage message)
    {
        try
        {
            if (!_config.IsEnabled(MoonshotId.ChaosVaccination) ||
                !_config.IsEnabled(MoonshotId.SovereigntyMesh))
                return;

            var survived = message.Payload.TryGetValue("survived", out var sv) && sv is bool b && b;
            if (!survived)
            {
                _logger.LogDebug("ChaosImmunityWiring: experiment did not survive, no resilience update");
                return;
            }

            var zoneId = message.Payload.TryGetValue("zoneId", out var zid)
                ? zid?.ToString() ?? "default" : "default";
            var faultType = message.Payload.TryGetValue("faultType", out var ft)
                ? ft?.ToString() ?? "Unknown" : "Unknown";

            var resiliencePayload = new Dictionary<string, object>
            {
                ["zoneId"] = zoneId,
                ["faultType"] = faultType,
                ["survivalStatus"] = "Survived",
                ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
                ["source"] = "ChaosImmunityWiring"
            };

            var resilienceMessage = new PluginMessage
            {
                Type = "sovereignty.zone.resilience.update",
                SourcePluginId = "CrossMoonshotWiring",
                Payload = resiliencePayload,
                CorrelationId = message.CorrelationId
            };

            await _messageBus.PublishAsync("sovereignty.zone.resilience.update", resilienceMessage);

            _logger.LogDebug(
                "ChaosImmunityWiring: updated zone {ZoneId} resilience for survived fault {FaultType}",
                zoneId, faultType);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ChaosImmunityWiring: error processing chaos.experiment.completed");
        }
    }

    private async Task HandleImmuneMemoryCreatedAsync(PluginMessage message)
    {
        try
        {
            if (!_config.IsEnabled(MoonshotId.ChaosVaccination) ||
                !_config.IsEnabled(MoonshotId.SovereigntyMesh))
                return;

            var faultType = message.Payload.TryGetValue("faultType", out var ft)
                ? ft?.ToString() ?? "Unknown" : "Unknown";
            var memoryId = message.Payload.TryGetValue("memoryId", out var mid)
                ? mid?.ToString() ?? "unknown" : "unknown";

            var healthPayload = new Dictionary<string, object>
            {
                ["faultType"] = faultType,
                ["memoryId"] = memoryId,
                ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
                ["source"] = "ChaosImmunityWiring"
            };

            var healthMessage = new PluginMessage
            {
                Type = "moonshot.health.resilience.improved",
                SourcePluginId = "CrossMoonshotWiring",
                Payload = healthPayload,
                CorrelationId = message.CorrelationId
            };

            await _messageBus.PublishAsync("moonshot.health.resilience.improved", healthMessage);

            _logger.LogDebug(
                "ChaosImmunityWiring: published resilience improvement for fault {FaultType} (memory={MemoryId})",
                faultType, memoryId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ChaosImmunityWiring: error processing chaos.immune.memory.created");
        }
    }
}
