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
/// Wires storage placement decisions to universal fabric namespace registration.
/// When a placement decision is made, the placement node is encoded into a dw:// address
/// and registered in the fabric namespace. Placement migrations update the address.
/// This bridges ZeroGravityStorage -> UniversalFabric.
/// </summary>
public sealed class FabricPlacementWiring
{
    private readonly IMessageBus _messageBus;
    private readonly MoonshotConfiguration _config;
    private readonly ILogger _logger;
    private IDisposable? _placementSubscription;
    private IDisposable? _migrationSubscription;

    public FabricPlacementWiring(
        IMessageBus messageBus,
        MoonshotConfiguration config,
        ILogger logger)
    {
        _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Registers bus subscriptions for storage placement and migration events.
    /// Only activates if both ZeroGravityStorage and UniversalFabric moonshots are enabled.
    /// </summary>
    public Task RegisterAsync(CancellationToken ct)
    {
        if (!_config.IsEnabled(MoonshotId.ZeroGravityStorage) ||
            !_config.IsEnabled(MoonshotId.UniversalFabric))
        {
            _logger.LogInformation(
                "FabricPlacementWiring skipped: ZeroGravityStorage={StorageEnabled}, UniversalFabric={FabricEnabled}",
                _config.IsEnabled(MoonshotId.ZeroGravityStorage),
                _config.IsEnabled(MoonshotId.UniversalFabric));
            return Task.CompletedTask;
        }

        _placementSubscription = _messageBus.Subscribe(
            "storage.placement.completed",
            HandlePlacementCompletedAsync);

        _migrationSubscription = _messageBus.Subscribe(
            "storage.placement.migrated",
            HandlePlacementMigratedAsync);

        _logger.LogInformation(
            "FabricPlacementWiring registered: storage.placement.completed -> fabric.namespace.register, " +
            "storage.placement.migrated -> fabric.namespace.update");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Unregisters all bus subscriptions and releases resources.
    /// </summary>
    public Task UnregisterAsync()
    {
        _placementSubscription?.Dispose();
        _placementSubscription = null;
        _migrationSubscription?.Dispose();
        _migrationSubscription = null;
        _logger.LogInformation("FabricPlacementWiring unregistered");
        return Task.CompletedTask;
    }

    private async Task HandlePlacementCompletedAsync(PluginMessage message)
    {
        try
        {
            if (!_config.IsEnabled(MoonshotId.ZeroGravityStorage) ||
                !_config.IsEnabled(MoonshotId.UniversalFabric))
                return;

            var objectId = message.Payload.TryGetValue("objectId", out var oid) ? oid?.ToString() : null;
            if (string.IsNullOrEmpty(objectId))
            {
                _logger.LogWarning("FabricPlacementWiring: placement completed with no objectId");
                return;
            }

            var nodeId = message.Payload.TryGetValue("nodeId", out var nid)
                ? nid?.ToString() ?? "unknown" : "unknown";

            // Encode placement node into dw:// address
            var dwAddress = $"dw://node@{nodeId}/{objectId}";

            var registerPayload = new Dictionary<string, object>
            {
                ["objectId"] = objectId,
                ["dwAddress"] = dwAddress,
                ["nodeId"] = nodeId,
                ["operation"] = "Register",
                ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
                ["source"] = "FabricPlacementWiring"
            };

            var registerMessage = new PluginMessage
            {
                Type = MoonshotBusTopics.FabricNamespaceRegister,
                SourcePluginId = "CrossMoonshotWiring",
                Payload = registerPayload,
                CorrelationId = message.CorrelationId
            };

            await _messageBus.PublishAsync(MoonshotBusTopics.FabricNamespaceRegister, registerMessage);

            _logger.LogDebug(
                "FabricPlacementWiring: registered {ObjectId} at {DwAddress}",
                objectId, dwAddress);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FabricPlacementWiring: error processing storage.placement.completed");
        }
    }

    private async Task HandlePlacementMigratedAsync(PluginMessage message)
    {
        try
        {
            if (!_config.IsEnabled(MoonshotId.ZeroGravityStorage) ||
                !_config.IsEnabled(MoonshotId.UniversalFabric))
                return;

            var objectId = message.Payload.TryGetValue("objectId", out var oid) ? oid?.ToString() : null;
            if (string.IsNullOrEmpty(objectId))
            {
                _logger.LogWarning("FabricPlacementWiring: placement migrated with no objectId");
                return;
            }

            var newNodeId = message.Payload.TryGetValue("newNodeId", out var nid)
                ? nid?.ToString() ?? "unknown" : "unknown";
            var previousNodeId = message.Payload.TryGetValue("previousNodeId", out var pid)
                ? pid?.ToString() ?? "unknown" : "unknown";

            // Encode new placement node into updated dw:// address
            var newDwAddress = $"dw://node@{newNodeId}/{objectId}";

            var updatePayload = new Dictionary<string, object>
            {
                ["objectId"] = objectId,
                ["dwAddress"] = newDwAddress,
                ["nodeId"] = newNodeId,
                ["previousNodeId"] = previousNodeId,
                ["operation"] = "Update",
                ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
                ["source"] = "FabricPlacementWiring"
            };

            var updateMessage = new PluginMessage
            {
                Type = "fabric.namespace.update",
                SourcePluginId = "CrossMoonshotWiring",
                Payload = updatePayload,
                CorrelationId = message.CorrelationId
            };

            await _messageBus.PublishAsync("fabric.namespace.update", updateMessage);

            _logger.LogDebug(
                "FabricPlacementWiring: updated {ObjectId} address from node@{PreviousNode} to {NewAddress}",
                objectId, previousNodeId, newDwAddress);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FabricPlacementWiring: error processing storage.placement.migrated");
        }
    }
}
