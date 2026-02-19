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
/// Wires carbon intensity changes to storage placement recalculation.
/// Significant carbon intensity changes trigger batch recalculation of placements,
/// and budget exceedances shift new placements to renewable-powered nodes.
/// This bridges CarbonAwareLifecycle -> ZeroGravityStorage.
/// </summary>
public sealed class PlacementCarbonWiring
{
    private const double SignificantDeltaThreshold = 0.20; // 20% change

    private readonly IMessageBus _messageBus;
    private readonly MoonshotConfiguration _config;
    private readonly ILogger _logger;
    private IDisposable? _intensitySubscription;
    private IDisposable? _budgetSubscription;

    public PlacementCarbonWiring(
        IMessageBus messageBus,
        MoonshotConfiguration config,
        ILogger logger)
    {
        _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Registers bus subscriptions for carbon intensity and budget events.
    /// Only activates if both CarbonAwareLifecycle and ZeroGravityStorage moonshots are enabled.
    /// </summary>
    public Task RegisterAsync(CancellationToken ct)
    {
        if (!_config.IsEnabled(MoonshotId.CarbonAwareLifecycle) ||
            !_config.IsEnabled(MoonshotId.ZeroGravityStorage))
        {
            _logger.LogInformation(
                "PlacementCarbonWiring skipped: CarbonAwareLifecycle={CarbonEnabled}, ZeroGravityStorage={StorageEnabled}",
                _config.IsEnabled(MoonshotId.CarbonAwareLifecycle),
                _config.IsEnabled(MoonshotId.ZeroGravityStorage));
            return Task.CompletedTask;
        }

        _intensitySubscription = _messageBus.Subscribe(
            "carbon.intensity.updated",
            HandleCarbonIntensityUpdatedAsync);

        _budgetSubscription = _messageBus.Subscribe(
            "carbon.budget.exceeded",
            HandleCarbonBudgetExceededAsync);

        _logger.LogInformation(
            "PlacementCarbonWiring registered: carbon.intensity.updated -> storage.placement.recalculate-batch, " +
            "carbon.budget.exceeded -> storage.placement.prefer-renewable");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Unregisters all bus subscriptions and releases resources.
    /// </summary>
    public Task UnregisterAsync()
    {
        _intensitySubscription?.Dispose();
        _intensitySubscription = null;
        _budgetSubscription?.Dispose();
        _budgetSubscription = null;
        _logger.LogInformation("PlacementCarbonWiring unregistered");
        return Task.CompletedTask;
    }

    private async Task HandleCarbonIntensityUpdatedAsync(PluginMessage message)
    {
        try
        {
            if (!_config.IsEnabled(MoonshotId.CarbonAwareLifecycle) ||
                !_config.IsEnabled(MoonshotId.ZeroGravityStorage))
                return;

            var previousIntensity = message.Payload.TryGetValue("previousIntensity", out var pi) && pi is double prev
                ? prev : 0.0;
            var currentIntensity = message.Payload.TryGetValue("currentIntensity", out var ci) && ci is double curr
                ? curr : 0.0;

            // Only trigger recalculation on significant delta (>20%)
            if (previousIntensity > 0)
            {
                var delta = Math.Abs(currentIntensity - previousIntensity) / previousIntensity;
                if (delta < SignificantDeltaThreshold)
                {
                    _logger.LogDebug(
                        "PlacementCarbonWiring: carbon intensity delta {Delta:P1} below threshold, skipping recalculation",
                        delta);
                    return;
                }
            }

            var storageClass = message.Payload.TryGetValue("storageClass", out var sc)
                ? sc?.ToString() ?? "default" : "default";

            var recalcPayload = new Dictionary<string, object>
            {
                ["storageClass"] = storageClass,
                ["reason"] = "CarbonIntensityChange",
                ["previousIntensity"] = previousIntensity,
                ["currentIntensity"] = currentIntensity,
                ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
                ["source"] = "PlacementCarbonWiring"
            };

            var recalcMessage = new PluginMessage
            {
                Type = "storage.placement.recalculate-batch",
                SourcePluginId = "CrossMoonshotWiring",
                Payload = recalcPayload,
                CorrelationId = message.CorrelationId
            };

            await _messageBus.PublishAsync("storage.placement.recalculate-batch", recalcMessage);

            _logger.LogDebug(
                "PlacementCarbonWiring: triggered placement recalculation for storageClass={StorageClass} due to carbon intensity change",
                storageClass);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PlacementCarbonWiring: error processing carbon.intensity.updated");
        }
    }

    private async Task HandleCarbonBudgetExceededAsync(PluginMessage message)
    {
        try
        {
            if (!_config.IsEnabled(MoonshotId.CarbonAwareLifecycle) ||
                !_config.IsEnabled(MoonshotId.ZeroGravityStorage))
                return;

            var budgetId = message.Payload.TryGetValue("budgetId", out var bid)
                ? bid?.ToString() ?? "default" : "default";
            var usagePercent = message.Payload.TryGetValue("usagePercent", out var up) && up is double usage
                ? usage : 100.0;

            var renewablePayload = new Dictionary<string, object>
            {
                ["reason"] = "CarbonBudgetExceeded",
                ["budgetId"] = budgetId,
                ["usagePercent"] = usagePercent,
                ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
                ["source"] = "PlacementCarbonWiring"
            };

            var renewableMessage = new PluginMessage
            {
                Type = "storage.placement.prefer-renewable",
                SourcePluginId = "CrossMoonshotWiring",
                Payload = renewablePayload,
                CorrelationId = message.CorrelationId
            };

            await _messageBus.PublishAsync("storage.placement.prefer-renewable", renewableMessage);

            _logger.LogInformation(
                "PlacementCarbonWiring: carbon budget {BudgetId} exceeded ({UsagePercent:F1}%), shifting placements to renewable nodes",
                budgetId, usagePercent);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PlacementCarbonWiring: error processing carbon.budget.exceeded");
        }
    }
}
