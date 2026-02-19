// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.TamperProof;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.TamperProof.TimeLock;

/// <summary>
/// Message bus integration for time-lock events.
/// Provides topic constants and publish methods for cross-plugin coordination
/// of time-lock operations including lock, unlock, extend, tamper detection,
/// vaccination scans, and policy evaluations.
/// Follows the same pattern as <see cref="Services.MessageBusIntegrationService"/>.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 59: Time-lock engine")]
public static class TimeLockMessageBusIntegration
{
    /// <summary>
    /// Topic published when an object is time-locked.
    /// Payload includes ObjectId, LockId, LockedAt, UnlocksAt, VaccinationLevel, ContentHash, Provider.
    /// </summary>
    public const string TimeLockLocked = "timelock.locked";

    /// <summary>
    /// Topic published when an object is unlocked (lock released).
    /// Payload includes ObjectId, LockId, UnlockReason, EmergencyUnlock, UnlockedAt, Provider.
    /// </summary>
    public const string TimeLockUnlocked = "timelock.unlocked";

    /// <summary>
    /// Topic published when a lock duration is extended.
    /// Payload includes ObjectId, LockId, PreviousUnlocksAt, NewUnlocksAt, AdditionalDuration, Provider.
    /// </summary>
    public const string TimeLockExtended = "timelock.extended";

    /// <summary>
    /// Topic published when tampering is detected on a time-locked object.
    /// Payload includes ObjectId, LockId, Details, DetectedAt, Severity, Provider.
    /// </summary>
    public const string TimeLockTamperDetected = "timelock.tamper.detected";

    /// <summary>
    /// Topic published when a vaccination scan is completed on a time-locked object.
    /// Payload includes ObjectId, VaccinationLevel, ThreatScore, IntegrityVerified, ScannedAt.
    /// </summary>
    public const string TimeLockVaccinationScan = "timelock.vaccination.scan";

    /// <summary>
    /// Topic published when a time-lock policy is evaluated for an object.
    /// Payload includes DataClassification, ComplianceFramework, ContentType, SelectedRule, LockDuration, VaccinationLevel.
    /// </summary>
    public const string TimeLockPolicyEvaluated = "timelock.policy.evaluated";

    /// <summary>
    /// Publishes a lock event when an object is time-locked.
    /// </summary>
    /// <param name="bus">Message bus instance.</param>
    /// <param name="result">The lock result containing all lock proof details.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task PublishLockEventAsync(IMessageBus bus, TimeLockResult result, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(result);

        var message = new PluginMessage
        {
            Type = TimeLockLocked,
            Payload = new Dictionary<string, object>
            {
                ["ObjectId"] = result.ObjectId,
                ["LockId"] = result.LockId,
                ["LockedAt"] = result.LockedAt,
                ["UnlocksAt"] = result.UnlocksAt,
                ["VaccinationLevel"] = result.VaccinationLevel.ToString(),
                ["ContentHash"] = result.ContentHash,
                ["TimeLockMode"] = result.TimeLockMode.ToString(),
                ["PublishedAt"] = DateTimeOffset.UtcNow
            }
        };

        await bus.PublishAsync(TimeLockLocked, message, ct);
    }

    /// <summary>
    /// Publishes an unlock event when an object's time-lock is released.
    /// </summary>
    /// <param name="bus">Message bus instance.</param>
    /// <param name="objectId">The object that was unlocked.</param>
    /// <param name="lockId">The lock instance identifier.</param>
    /// <param name="reason">The unlock condition type that was satisfied.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task PublishUnlockEventAsync(
        IMessageBus bus,
        Guid objectId,
        string lockId,
        UnlockConditionType reason,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bus);

        if (objectId == Guid.Empty)
            throw new ArgumentException("Object ID cannot be empty.", nameof(objectId));

        var message = new PluginMessage
        {
            Type = TimeLockUnlocked,
            Payload = new Dictionary<string, object>
            {
                ["ObjectId"] = objectId,
                ["LockId"] = lockId ?? string.Empty,
                ["UnlockReason"] = reason.ToString(),
                ["UnlockedAt"] = DateTimeOffset.UtcNow,
                ["PublishedAt"] = DateTimeOffset.UtcNow
            }
        };

        await bus.PublishAsync(TimeLockUnlocked, message, ct);
    }

    /// <summary>
    /// Publishes an extend event when a lock duration is increased.
    /// </summary>
    /// <param name="bus">Message bus instance.</param>
    /// <param name="objectId">The object whose lock was extended.</param>
    /// <param name="newDuration">The new total remaining duration after extension.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task PublishExtendEventAsync(
        IMessageBus bus,
        Guid objectId,
        TimeSpan newDuration,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bus);

        if (objectId == Guid.Empty)
            throw new ArgumentException("Object ID cannot be empty.", nameof(objectId));

        if (newDuration <= TimeSpan.Zero)
            throw new ArgumentException("New duration must be positive.", nameof(newDuration));

        var message = new PluginMessage
        {
            Type = TimeLockExtended,
            Payload = new Dictionary<string, object>
            {
                ["ObjectId"] = objectId,
                ["NewDuration"] = newDuration.ToString(),
                ["ExtendedAt"] = DateTimeOffset.UtcNow,
                ["PublishedAt"] = DateTimeOffset.UtcNow
            }
        };

        await bus.PublishAsync(TimeLockExtended, message, ct);
    }

    /// <summary>
    /// Publishes a tamper detection event when integrity verification fails on a locked object.
    /// </summary>
    /// <param name="bus">Message bus instance.</param>
    /// <param name="objectId">The object where tampering was detected.</param>
    /// <param name="details">Detailed description of the tampering detected.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task PublishTamperDetectedEventAsync(
        IMessageBus bus,
        Guid objectId,
        string details,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bus);

        if (objectId == Guid.Empty)
            throw new ArgumentException("Object ID cannot be empty.", nameof(objectId));

        var message = new PluginMessage
        {
            Type = TimeLockTamperDetected,
            Payload = new Dictionary<string, object>
            {
                ["ObjectId"] = objectId,
                ["Details"] = details ?? string.Empty,
                ["DetectedAt"] = DateTimeOffset.UtcNow,
                ["Severity"] = "Critical",
                ["PublishedAt"] = DateTimeOffset.UtcNow
            }
        };

        await bus.PublishAsync(TimeLockTamperDetected, message, ct);
    }

    /// <summary>
    /// Publishes a vaccination scan result event.
    /// </summary>
    /// <param name="bus">Message bus instance.</param>
    /// <param name="objectId">The scanned object.</param>
    /// <param name="vaccinationInfo">Vaccination scan results.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task PublishVaccinationScanEventAsync(
        IMessageBus bus,
        Guid objectId,
        RansomwareVaccinationInfo vaccinationInfo,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(vaccinationInfo);

        var message = new PluginMessage
        {
            Type = TimeLockVaccinationScan,
            Payload = new Dictionary<string, object>
            {
                ["ObjectId"] = objectId,
                ["VaccinationLevel"] = vaccinationInfo.VaccinationLevel.ToString(),
                ["ThreatScore"] = vaccinationInfo.ThreatScore,
                ["IntegrityVerified"] = vaccinationInfo.IntegrityVerified,
                ["TimeLockActive"] = vaccinationInfo.TimeLockActive,
                ["ScannedAt"] = vaccinationInfo.LastScanAt,
                ["PublishedAt"] = DateTimeOffset.UtcNow
            }
        };

        await bus.PublishAsync(TimeLockVaccinationScan, message, ct);
    }

    /// <summary>
    /// Publishes a policy evaluation event when rules are applied to determine lock parameters.
    /// </summary>
    /// <param name="bus">Message bus instance.</param>
    /// <param name="dataClassification">Data classification that was evaluated.</param>
    /// <param name="complianceFramework">Compliance framework that was evaluated.</param>
    /// <param name="contentType">Content type that was evaluated.</param>
    /// <param name="selectedRuleName">The rule that matched and was applied.</param>
    /// <param name="policy">The resulting policy from evaluation.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task PublishPolicyEvaluatedEventAsync(
        IMessageBus bus,
        string? dataClassification,
        string? complianceFramework,
        string? contentType,
        string selectedRuleName,
        TimeLockPolicy policy,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(policy);

        var message = new PluginMessage
        {
            Type = TimeLockPolicyEvaluated,
            Payload = new Dictionary<string, object>
            {
                ["DataClassification"] = dataClassification ?? string.Empty,
                ["ComplianceFramework"] = complianceFramework ?? string.Empty,
                ["ContentType"] = contentType ?? string.Empty,
                ["SelectedRule"] = selectedRuleName,
                ["LockDuration"] = policy.DefaultLockDuration.ToString(),
                ["VaccinationLevel"] = policy.VaccinationLevel.ToString(),
                ["EvaluatedAt"] = DateTimeOffset.UtcNow,
                ["PublishedAt"] = DateTimeOffset.UtcNow
            }
        };

        await bus.PublishAsync(TimeLockPolicyEvaluated, message, ct);
    }
}
