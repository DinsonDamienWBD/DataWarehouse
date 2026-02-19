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
/// Wires consciousness scores to automatic tag attachment.
/// When a consciousness score is computed for an object, system tags are auto-attached
/// carrying the score value, grade, liability, and recommended action.
/// This bridges DataConsciousness -> UniversalTags.
/// </summary>
public sealed class TagConsciousnessWiring
{
    private readonly IMessageBus _messageBus;
    private readonly MoonshotConfiguration _config;
    private readonly ILogger _logger;
    private IDisposable? _subscription;

    public TagConsciousnessWiring(
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
    /// Only activates if both UniversalTags and DataConsciousness moonshots are enabled.
    /// </summary>
    public Task RegisterAsync(CancellationToken ct)
    {
        if (!_config.IsEnabled(MoonshotId.UniversalTags) ||
            !_config.IsEnabled(MoonshotId.DataConsciousness))
        {
            _logger.LogInformation(
                "TagConsciousnessWiring skipped: UniversalTags={TagsEnabled}, DataConsciousness={ConsciousnessEnabled}",
                _config.IsEnabled(MoonshotId.UniversalTags),
                _config.IsEnabled(MoonshotId.DataConsciousness));
            return Task.CompletedTask;
        }

        _subscription = _messageBus.Subscribe(
            "consciousness.score.completed",
            HandleConsciousnessScoreCompletedAsync);

        _logger.LogInformation("TagConsciousnessWiring registered: consciousness.score.completed -> tags.system.attach");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Unregisters the bus subscription and releases resources.
    /// </summary>
    public Task UnregisterAsync()
    {
        _subscription?.Dispose();
        _subscription = null;
        _logger.LogInformation("TagConsciousnessWiring unregistered");
        return Task.CompletedTask;
    }

    private async Task HandleConsciousnessScoreCompletedAsync(PluginMessage message)
    {
        try
        {
            if (!_config.IsEnabled(MoonshotId.UniversalTags) ||
                !_config.IsEnabled(MoonshotId.DataConsciousness))
                return;

            var objectId = message.Payload.TryGetValue("objectId", out var oid) ? oid?.ToString() : null;
            if (string.IsNullOrEmpty(objectId))
            {
                _logger.LogWarning("TagConsciousnessWiring: received score.completed with no objectId");
                return;
            }

            var scoreValue = message.Payload.TryGetValue("score", out var sv) && sv is double dScore
                ? dScore
                : message.Payload.TryGetValue("score", out sv) && sv is int iScore
                    ? (double)iScore
                    : 0.0;

            var grade = DeriveGrade(scoreValue);
            var liabilityScore = message.Payload.TryGetValue("liability", out var ls) && ls is double dLiability
                ? dLiability
                : 0.0;
            var action = DeriveAction(scoreValue);

            var tagPayload = new Dictionary<string, object>
            {
                ["objectId"] = objectId,
                ["tagSource"] = "System",
                ["tags"] = new Dictionary<string, string>
                {
                    ["dw:consciousness:value"] = scoreValue.ToString("F1"),
                    ["dw:consciousness:grade"] = grade,
                    ["dw:consciousness:liability"] = liabilityScore.ToString("F1"),
                    ["dw:consciousness:action"] = action
                }
            };

            var tagMessage = new PluginMessage
            {
                Type = "tags.system.attach",
                SourcePluginId = "CrossMoonshotWiring",
                Payload = tagPayload,
                CorrelationId = message.CorrelationId
            };

            await _messageBus.PublishAsync("tags.system.attach", tagMessage);

            _logger.LogDebug(
                "TagConsciousnessWiring: attached consciousness tags to {ObjectId} (score={Score}, grade={Grade})",
                objectId, scoreValue, grade);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TagConsciousnessWiring: error processing consciousness.score.completed");
        }
    }

    private static string DeriveGrade(double score) => score switch
    {
        >= 80 => "Critical",
        >= 60 => "High",
        >= 40 => "Medium",
        >= 20 => "Low",
        _ => "Negligible"
    };

    private static string DeriveAction(double score) => score switch
    {
        >= 80 => "Protect",
        >= 60 => "Monitor",
        >= 40 => "Review",
        >= 20 => "Archive",
        _ => "Ignore"
    };
}
