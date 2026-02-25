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
/// Wires consciousness value scores to semantic sync fidelity levels.
/// Higher-value data gets higher fidelity synchronization (real-time, full content);
/// lower-value data gets reduced fidelity (summary only, or no sync).
/// This bridges DataConsciousness -> SemanticSync.
/// </summary>
public sealed class SyncConsciousnessWiring
{
    private readonly IMessageBus _messageBus;
    private readonly MoonshotConfiguration _config;
    private readonly ILogger _logger;
    private IDisposable? _subscription;

    public SyncConsciousnessWiring(
        IMessageBus messageBus,
        MoonshotConfiguration config,
        ILogger logger)
    {
        _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Registers bus subscription for consciousness score completed events.
    /// Only activates if both SemanticSync and DataConsciousness moonshots are enabled.
    /// </summary>
    public Task RegisterAsync(CancellationToken ct)
    {
        if (!_config.IsEnabled(MoonshotId.SemanticSync) ||
            !_config.IsEnabled(MoonshotId.DataConsciousness))
        {
            _logger.LogInformation(
                "SyncConsciousnessWiring skipped: SemanticSync={SyncEnabled}, DataConsciousness={ConsciousnessEnabled}",
                _config.IsEnabled(MoonshotId.SemanticSync),
                _config.IsEnabled(MoonshotId.DataConsciousness));
            return Task.CompletedTask;
        }

        _subscription = _messageBus.Subscribe(
            "consciousness.score.completed",
            HandleConsciousnessScoreCompletedAsync);

        _logger.LogInformation("SyncConsciousnessWiring registered: consciousness.score.completed -> semanticsync.fidelity.set");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Unregisters the bus subscription and releases resources.
    /// </summary>
    public Task UnregisterAsync()
    {
        _subscription?.Dispose();
        _subscription = null;
        _logger.LogInformation("SyncConsciousnessWiring unregistered");
        return Task.CompletedTask;
    }

    private async Task HandleConsciousnessScoreCompletedAsync(PluginMessage message)
    {
        try
        {
            if (!_config.IsEnabled(MoonshotId.SemanticSync) ||
                !_config.IsEnabled(MoonshotId.DataConsciousness))
                return;

            var objectId = message.Payload.TryGetValue("objectId", out var oid) ? oid?.ToString() : null;
            if (string.IsNullOrEmpty(objectId))
            {
                _logger.LogWarning("SyncConsciousnessWiring: received score.completed with no objectId");
                return;
            }

            var scoreValue = message.Payload.TryGetValue("score", out var sv) && sv is double dScore
                ? dScore
                : message.Payload.TryGetValue("score", out sv) && sv is int iScore
                    ? (double)iScore
                    : 0.0;

            var fidelity = MapScoreToFidelity(scoreValue);

            var fidelityPayload = new Dictionary<string, object>
            {
                ["objectId"] = objectId,
                ["fidelityLevel"] = fidelity,
                ["consciousnessScore"] = scoreValue,
                ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
                ["source"] = "SyncConsciousnessWiring"
            };

            var fidelityMessage = new PluginMessage
            {
                Type = "semanticsync.fidelity.set",
                SourcePluginId = "CrossMoonshotWiring",
                Payload = fidelityPayload,
                CorrelationId = message.CorrelationId
            };

            await _messageBus.PublishAsync("semanticsync.fidelity.set", fidelityMessage);

            _logger.LogDebug(
                "SyncConsciousnessWiring: set fidelity for {ObjectId} to {Fidelity} (score={Score})",
                objectId, fidelity, scoreValue);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SyncConsciousnessWiring: error processing consciousness.score.completed");
        }
    }

    /// <summary>
    /// Maps consciousness value score to sync fidelity level.
    /// Higher value = higher fidelity synchronization.
    /// </summary>
    private static string MapScoreToFidelity(double score) => score switch
    {
        >= 80 => "FullFidelity",      // Sync everything, real-time
        >= 50 => "StandardFidelity",   // Sync with batch, 5-min delay
        >= 20 => "SummaryOnly",        // Sync summaries, not raw data
        _ => "NoSync"                  // Don't sync, archive locally
    };
}
