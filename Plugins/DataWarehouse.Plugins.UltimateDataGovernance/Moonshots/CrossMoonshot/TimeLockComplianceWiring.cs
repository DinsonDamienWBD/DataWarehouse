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
/// Wires compliance passport issuance to cryptographic time-lock application.
/// When a passport is issued, regulatory retention requirements determine lock duration.
/// When a passport expires, time-locks are released if eligible.
/// This bridges CompliancePassports -> CryptoTimeLocks.
/// </summary>
public sealed class TimeLockComplianceWiring
{
    /// <summary>
    /// Known regulatory retention durations mapped by regulation identifier.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, TimeSpan> RegulationRetentionPeriods =
        new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase)
        {
            ["SOX"] = TimeSpan.FromDays(7 * 365),       // SOX: 7 years
            ["GDPR"] = TimeSpan.FromDays(3 * 365),      // GDPR minimum: 3 years (configurable)
            ["HIPAA"] = TimeSpan.FromDays(6 * 365),      // HIPAA: 6 years
            ["SEC-17a-4"] = TimeSpan.FromDays(7 * 365),  // SEC 17a-4: 7 years
            ["PCI-DSS"] = TimeSpan.FromDays(1 * 365),    // PCI-DSS: 1 year
            ["FINRA"] = TimeSpan.FromDays(6 * 365),      // FINRA: 6 years
            ["MiFID-II"] = TimeSpan.FromDays(5 * 365),   // MiFID II: 5 years
            ["CCPA"] = TimeSpan.FromDays(2 * 365),       // CCPA: 2 years
        };

    /// <summary>
    /// Regulations that require full immutability (maximum vaccination level).
    /// </summary>
    private static readonly HashSet<string> ImmutabilityRegulations =
        new(StringComparer.OrdinalIgnoreCase) { "SEC-17a-4", "FINRA", "SOX" };

    private readonly IMessageBus _messageBus;
    private readonly MoonshotConfiguration _config;
    private readonly ILogger _logger;
    private IDisposable? _issuedSubscription;
    private IDisposable? _expiredSubscription;

    public TimeLockComplianceWiring(
        IMessageBus messageBus,
        MoonshotConfiguration config,
        ILogger logger)
    {
        _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Registers bus subscriptions for compliance passport lifecycle events.
    /// Only activates if both CompliancePassports and CryptoTimeLocks moonshots are enabled.
    /// </summary>
    public Task RegisterAsync(CancellationToken ct)
    {
        if (!_config.IsEnabled(MoonshotId.CompliancePassports) ||
            !_config.IsEnabled(MoonshotId.CryptoTimeLocks))
        {
            _logger.LogInformation(
                "TimeLockComplianceWiring skipped: CompliancePassports={ComplianceEnabled}, CryptoTimeLocks={TimeLocksEnabled}",
                _config.IsEnabled(MoonshotId.CompliancePassports),
                _config.IsEnabled(MoonshotId.CryptoTimeLocks));
            return Task.CompletedTask;
        }

        _issuedSubscription = _messageBus.Subscribe(
            "compliance.passport.issued",
            HandlePassportIssuedAsync);

        _expiredSubscription = _messageBus.Subscribe(
            "compliance.passport.expired",
            HandlePassportExpiredAsync);

        _logger.LogInformation(
            "TimeLockComplianceWiring registered: compliance.passport.issued -> tamperproof.timelock.apply, " +
            "compliance.passport.expired -> tamperproof.timelock.release-if-eligible");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Unregisters all bus subscriptions and releases resources.
    /// </summary>
    public Task UnregisterAsync()
    {
        _issuedSubscription?.Dispose();
        _issuedSubscription = null;
        _expiredSubscription?.Dispose();
        _expiredSubscription = null;
        _logger.LogInformation("TimeLockComplianceWiring unregistered");
        return Task.CompletedTask;
    }

    private async Task HandlePassportIssuedAsync(PluginMessage message)
    {
        try
        {
            if (!_config.IsEnabled(MoonshotId.CompliancePassports) ||
                !_config.IsEnabled(MoonshotId.CryptoTimeLocks))
                return;

            var objectId = message.Payload.TryGetValue("objectId", out var oid) ? oid?.ToString() : null;
            if (string.IsNullOrEmpty(objectId))
            {
                _logger.LogWarning("TimeLockComplianceWiring: passport issued with no objectId");
                return;
            }

            var regulation = message.Payload.TryGetValue("regulation", out var reg)
                ? reg?.ToString() ?? string.Empty : string.Empty;

            if (string.IsNullOrEmpty(regulation))
            {
                _logger.LogDebug("TimeLockComplianceWiring: passport for {ObjectId} has no regulation, skipping time-lock", objectId);
                return;
            }

            if (!RegulationRetentionPeriods.TryGetValue(regulation, out var retentionDuration))
            {
                _logger.LogDebug(
                    "TimeLockComplianceWiring: regulation {Regulation} has no known retention period, skipping time-lock",
                    regulation);
                return;
            }

            var requiresImmutability = ImmutabilityRegulations.Contains(regulation);
            var vaccinationLevel = requiresImmutability ? "Maximum" : "Standard";

            var lockPayload = new Dictionary<string, object>
            {
                ["objectId"] = objectId,
                ["duration"] = retentionDuration.TotalSeconds,
                ["durationDescription"] = $"{retentionDuration.TotalDays / 365:F0} years ({regulation})",
                ["regulation"] = regulation,
                ["vaccinationLevel"] = vaccinationLevel,
                ["requiresImmutability"] = requiresImmutability,
                ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
                ["source"] = "TimeLockComplianceWiring"
            };

            var lockMessage = new PluginMessage
            {
                Type = "tamperproof.timelock.apply",
                SourcePluginId = "CrossMoonshotWiring",
                Payload = lockPayload,
                CorrelationId = message.CorrelationId
            };

            await _messageBus.PublishAsync("tamperproof.timelock.apply", lockMessage);

            _logger.LogDebug(
                "TimeLockComplianceWiring: applied time-lock to {ObjectId} for {Regulation} ({Duration} days, immutable={Immutable})",
                objectId, regulation, retentionDuration.TotalDays, requiresImmutability);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TimeLockComplianceWiring: error processing compliance.passport.issued");
        }
    }

    private async Task HandlePassportExpiredAsync(PluginMessage message)
    {
        try
        {
            if (!_config.IsEnabled(MoonshotId.CompliancePassports) ||
                !_config.IsEnabled(MoonshotId.CryptoTimeLocks))
                return;

            var objectId = message.Payload.TryGetValue("objectId", out var oid) ? oid?.ToString() : null;
            if (string.IsNullOrEmpty(objectId))
            {
                _logger.LogWarning("TimeLockComplianceWiring: passport expired with no objectId");
                return;
            }

            var releasePayload = new Dictionary<string, object>
            {
                ["objectId"] = objectId,
                ["reason"] = "CompliancePassportExpired",
                ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
                ["source"] = "TimeLockComplianceWiring"
            };

            var releaseMessage = new PluginMessage
            {
                Type = "tamperproof.timelock.release-if-eligible",
                SourcePluginId = "CrossMoonshotWiring",
                Payload = releasePayload,
                CorrelationId = message.CorrelationId
            };

            await _messageBus.PublishAsync("tamperproof.timelock.release-if-eligible", releaseMessage);

            _logger.LogDebug(
                "TimeLockComplianceWiring: requested time-lock release for {ObjectId} (passport expired)",
                objectId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TimeLockComplianceWiring: error processing compliance.passport.expired");
        }
    }
}
