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
/// Wires sovereignty zone decisions to compliance passport evidence chains.
/// When a sovereignty zone check completes, the decision is recorded as evidence
/// in the object's compliance passport. Zone configuration changes trigger
/// re-evaluation of affected passports.
/// This bridges SovereigntyMesh -> CompliancePassports.
/// </summary>
public sealed class ComplianceSovereigntyWiring
{
    private readonly IMessageBus _messageBus;
    private readonly MoonshotConfiguration _config;
    private readonly ILogger _logger;
    private IDisposable? _zoneCheckSubscription;
    private IDisposable? _zoneChangedSubscription;

    public ComplianceSovereigntyWiring(
        IMessageBus messageBus,
        MoonshotConfiguration config,
        ILogger logger)
    {
        _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Registers bus subscriptions for sovereignty zone events.
    /// Only activates if both CompliancePassports and SovereigntyMesh moonshots are enabled.
    /// </summary>
    public Task RegisterAsync(CancellationToken ct)
    {
        if (!_config.IsEnabled(MoonshotId.CompliancePassports) ||
            !_config.IsEnabled(MoonshotId.SovereigntyMesh))
        {
            _logger.LogInformation(
                "ComplianceSovereigntyWiring skipped: CompliancePassports={ComplianceEnabled}, SovereigntyMesh={SovereigntyEnabled}",
                _config.IsEnabled(MoonshotId.CompliancePassports),
                _config.IsEnabled(MoonshotId.SovereigntyMesh));
            return Task.CompletedTask;
        }

        _zoneCheckSubscription = _messageBus.Subscribe(
            "sovereignty.zone.check.completed",
            HandleZoneCheckCompletedAsync);

        _zoneChangedSubscription = _messageBus.Subscribe(
            "sovereignty.zone.changed",
            HandleZoneChangedAsync);

        _logger.LogInformation(
            "ComplianceSovereigntyWiring registered: sovereignty.zone.check.completed -> compliance.passport.add-evidence, " +
            "sovereignty.zone.changed -> compliance.passport.re-evaluate");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Unregisters all bus subscriptions and releases resources.
    /// </summary>
    public Task UnregisterAsync()
    {
        _zoneCheckSubscription?.Dispose();
        _zoneCheckSubscription = null;
        _zoneChangedSubscription?.Dispose();
        _zoneChangedSubscription = null;
        _logger.LogInformation("ComplianceSovereigntyWiring unregistered");
        return Task.CompletedTask;
    }

    private async Task HandleZoneCheckCompletedAsync(PluginMessage message)
    {
        try
        {
            if (!_config.IsEnabled(MoonshotId.CompliancePassports) ||
                !_config.IsEnabled(MoonshotId.SovereigntyMesh))
                return;

            var objectId = message.Payload.TryGetValue("objectId", out var oid) ? oid?.ToString() : null;
            if (string.IsNullOrEmpty(objectId))
            {
                _logger.LogWarning("ComplianceSovereigntyWiring: zone check completed with no objectId");
                return;
            }

            var zoneId = message.Payload.TryGetValue("zoneId", out var zid) ? zid?.ToString() ?? "unknown" : "unknown";
            var decision = message.Payload.TryGetValue("decision", out var dec) ? dec?.ToString() ?? "Unknown" : "Unknown";

            var evidencePayload = new Dictionary<string, object>
            {
                ["objectId"] = objectId,
                ["evidenceType"] = "SovereigntyZoneCheck",
                ["zoneId"] = zoneId,
                ["decision"] = decision,
                ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
                ["source"] = "ComplianceSovereigntyWiring"
            };

            var evidenceMessage = new PluginMessage
            {
                Type = "compliance.passport.add-evidence",
                SourcePluginId = "CrossMoonshotWiring",
                Payload = evidencePayload,
                CorrelationId = message.CorrelationId
            };

            await _messageBus.PublishAsync("compliance.passport.add-evidence", evidenceMessage);

            _logger.LogDebug(
                "ComplianceSovereigntyWiring: added sovereignty evidence to passport for {ObjectId} (zone={ZoneId}, decision={Decision})",
                objectId, zoneId, decision);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ComplianceSovereigntyWiring: error processing sovereignty.zone.check.completed");
        }
    }

    private async Task HandleZoneChangedAsync(PluginMessage message)
    {
        try
        {
            if (!_config.IsEnabled(MoonshotId.CompliancePassports) ||
                !_config.IsEnabled(MoonshotId.SovereigntyMesh))
                return;

            var zoneId = message.Payload.TryGetValue("zoneId", out var zid) ? zid?.ToString() ?? "unknown" : "unknown";

            var reEvalPayload = new Dictionary<string, object>
            {
                ["zoneFilter"] = zoneId,
                ["reason"] = "SovereigntyZoneConfigurationChanged",
                ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
                ["source"] = "ComplianceSovereigntyWiring"
            };

            var reEvalMessage = new PluginMessage
            {
                Type = "compliance.passport.re-evaluate",
                SourcePluginId = "CrossMoonshotWiring",
                Payload = reEvalPayload,
                CorrelationId = message.CorrelationId
            };

            await _messageBus.PublishAsync("compliance.passport.re-evaluate", reEvalMessage);

            _logger.LogDebug(
                "ComplianceSovereigntyWiring: triggered passport re-evaluation for zone {ZoneId}",
                zoneId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ComplianceSovereigntyWiring: error processing sovereignty.zone.changed");
        }
    }
}
