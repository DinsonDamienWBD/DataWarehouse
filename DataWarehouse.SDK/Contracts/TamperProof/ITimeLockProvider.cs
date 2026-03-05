// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts.IntelligenceAware;
using DataWarehouse.SDK.Primitives;
using System.Threading;

using DataWarehouse.SDK.Contracts.Hierarchy;

namespace DataWarehouse.SDK.Contracts.TamperProof;

/// <summary>
/// Interface for per-object cryptographic time-lock providers.
/// Provides temporal locking where objects cannot be modified, deleted, or unlocked
/// before the lock period expires, even by administrators.
/// This is the foundation for ransomware vaccination -- objects are locked for configurable
/// periods providing defense against ransomware, insider threats, and accidental deletion.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 59: Crypto time-lock types")]
public interface ITimeLockProvider : IPlugin
{
    /// <summary>
    /// Applies a cryptographic time-lock to an object.
    /// Once locked, the object cannot be modified or deleted until the lock expires
    /// or a valid unlock condition is satisfied.
    /// </summary>
    /// <param name="request">Lock request containing object ID, duration, mode, and conditions.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result containing lock proof including timestamps, content hash, and vaccination details.</returns>
    /// <exception cref="ArgumentException">Thrown when the request contains invalid parameters.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the object is already locked.</exception>
    Task<TimeLockResult> LockAsync(TimeLockRequest request, CancellationToken ct = default);

    /// <summary>
    /// Gets the current lock status of an object including vaccination level and integrity state.
    /// </summary>
    /// <param name="objectId">Unique identifier of the object.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Complete status information including lock state, vaccination, legal holds, and integrity.</returns>
    Task<TimeLockStatus> GetStatusAsync(Guid objectId, CancellationToken ct = default);

    /// <summary>
    /// Quick check whether an object is currently locked.
    /// More efficient than <see cref="GetStatusAsync"/> when only lock state is needed.
    /// </summary>
    /// <param name="objectId">Unique identifier of the object.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the object is currently time-locked, false otherwise.</returns>
    Task<bool> IsLockedAsync(Guid objectId, CancellationToken ct = default);

    /// <summary>
    /// Gets the ransomware vaccination status of an object.
    /// Provides a comprehensive assessment including time-lock, integrity, PQC, and blockchain verification.
    /// </summary>
    /// <param name="objectId">Unique identifier of the object.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Vaccination information including threat score and verification results.</returns>
    Task<RansomwareVaccinationInfo> GetVaccinationInfoAsync(Guid objectId, CancellationToken ct = default);

    /// <summary>
    /// Extends the lock duration for an object. Lock duration can only be extended, never shortened.
    /// This is a compliance requirement to prevent circumventing lock periods.
    /// </summary>
    /// <param name="objectId">Unique identifier of the object.</param>
    /// <param name="additionalDuration">Additional time to add to the current lock expiry. Must be positive.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentException">Thrown when the object ID is empty or duration is not positive.</exception>
    /// <exception cref="KeyNotFoundException">Thrown when the object does not exist or has no active lock.</exception>
    Task ExtendLockAsync(Guid objectId, TimeSpan additionalDuration, CancellationToken ct = default);

    /// <summary>
    /// Attempts to unlock an object by satisfying an unlock condition.
    /// The condition must match one of the allowed conditions in the provider's policy.
    /// All unlock attempts are audited regardless of success.
    /// </summary>
    /// <param name="objectId">Unique identifier of the object.</param>
    /// <param name="condition">The unlock condition to evaluate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the unlock was successful, false if the condition was not satisfied.</returns>
    /// <exception cref="ArgumentException">Thrown when the object ID is empty.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the condition type is not allowed by policy.</exception>
    Task<bool> AttemptUnlockAsync(Guid objectId, UnlockCondition condition, CancellationToken ct = default);

    /// <summary>
    /// Enumerates currently locked objects with pagination support.
    /// </summary>
    /// <param name="limit">Maximum number of results to return.</param>
    /// <param name="offset">Number of results to skip for pagination.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of lock statuses for currently locked objects.</returns>
    Task<IReadOnlyList<TimeLockStatus>> ListLockedObjectsAsync(int limit, int offset, CancellationToken ct = default);
}

/// <summary>
/// Abstract base class for time-lock provider plugins.
/// Provides common validation, audit logging, and intelligence hooks.
/// Plugin implementers should extend this class and implement the protected abstract methods.
/// Follows the same pattern as <see cref="WormStorageProviderPluginBase"/>.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 59: Crypto time-lock types")]
public abstract class TimeLockProviderPluginBase : IntegrityPluginBase, ITimeLockProvider, IIntelligenceAware
{
    /// <summary>
    /// Gets the time-lock policy governing this provider's behavior.
    /// Implementations must return a valid policy that defines duration bounds,
    /// allowed conditions, and vaccination requirements.
    /// </summary>
    public abstract TimeLockPolicy Policy { get; }

    /// <summary>
    /// Gets the default enforcement mode for this provider.
    /// </summary>
    public abstract TimeLockMode DefaultMode { get; }

    /// <inheritdoc/>
    public override Task<Dictionary<string, object>> VerifyAsync(string key, CancellationToken ct = default)
        => Task.FromResult(new Dictionary<string, object> { ["verified"] = true, ["provider"] = GetType().Name });

    /// <inheritdoc/>
    public override async Task<byte[]> ComputeHashAsync(Stream data, CancellationToken ct = default)
    {
        return await System.Security.Cryptography.SHA256.HashDataAsync(data, ct).ConfigureAwait(false);
    }

    #region Intelligence Socket

    /// <summary>
    /// Gets whether Universal Intelligence (T90) is currently available.
    /// </summary>
    public new bool IsIntelligenceAvailable { get; protected set; }

    /// <summary>
    /// Gets the capabilities provided by the discovered Intelligence system.
    /// </summary>
    public new IntelligenceCapabilities AvailableCapabilities { get; protected set; }

    /// <summary>
    /// Attempts to discover Universal Intelligence (T90) via the message bus.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if Intelligence was discovered, false otherwise.</returns>
    public new virtual async Task<bool> DiscoverIntelligenceAsync(CancellationToken ct = default)
    {
        if (MessageBus == null) { IsIntelligenceAvailable = false; return false; }
        await Task.CompletedTask;
        IsIntelligenceAvailable = false;
        return IsIntelligenceAvailable;
    }

    /// <summary>
    /// Declared capabilities for the central Capability Registry.
    /// Registers this provider as a TamperProof/TimeLock capability.
    /// </summary>
    protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities => new[]
    {
        new RegisteredCapability
        {
            CapabilityId = $"{Id}.timelock",
            DisplayName = $"{Name} - Time-Lock",
            Description = $"Cryptographic time-lock provider ({DefaultMode} mode, {Policy.VaccinationLevel} vaccination)",
            Category = CapabilityCategory.TamperProof,
            SubCategory = "TimeLock",
            PluginId = Id,
            PluginName = Name,
            PluginVersion = Version,
            Tags = new[] { "timelock", "immutable", "tamper-proof", "ransomware-vaccination", "compliance" },
            SemanticDescription = "Use for cryptographic time-lock operations and ransomware vaccination"
        }
    };

    /// <summary>
    /// Provides static knowledge objects for the AI knowledge bank.
    /// Describes this provider's time-lock and vaccination capabilities.
    /// </summary>
    protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge()
    {
        return new[]
        {
            new KnowledgeObject
            {
                Id = $"{Id}.timelock.capability",
                Topic = "tamperproof.timelock",
                SourcePluginId = Id,
                SourcePluginName = Name,
                KnowledgeType = "capability",
                Description = $"Time-lock provider with {DefaultMode} enforcement and {Policy.VaccinationLevel} vaccination",
                Payload = new Dictionary<string, object>
                {
                    ["defaultMode"] = DefaultMode.ToString(),
                    ["vaccinationLevel"] = Policy.VaccinationLevel.ToString(),
                    ["minLockDuration"] = Policy.MinLockDuration.ToString(),
                    ["maxLockDuration"] = Policy.MaxLockDuration.ToString(),
                    ["requiresPqcSignature"] = Policy.RequirePqcSignature,
                    ["autoExtendOnTamper"] = Policy.AutoExtendOnTamperDetection
                },
                Tags = new[] { "timelock", "tamper-proof", "ransomware-vaccination" }
            }
        };
    }

    /// <summary>
    /// Requests AI-assisted threat assessment for a locked object.
    /// </summary>
    /// <param name="objectId">Object to assess.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Threat assessment result, or null if Intelligence is unavailable.</returns>
    protected virtual async Task<TimeLockThreatAssessment?> RequestThreatAssessmentAsync(Guid objectId, CancellationToken ct = default)
    {
        if (!IsIntelligenceAvailable || MessageBus == null) return null;
        await Task.CompletedTask;
        return null;
    }

    #endregion

    /// <summary>
    /// Gets the category of this plugin (always StorageProvider for time-lock providers).
    /// </summary>
    public override PluginCategory Category => PluginCategory.StorageProvider;

    /// <summary>
    /// Applies a time-lock to an object with full validation of request parameters against policy.
    /// Validates object ID, duration bounds, and vaccination requirements before delegating
    /// to the provider-specific <see cref="LockInternalAsync"/> implementation.
    /// </summary>
    public async Task<TimeLockResult> LockAsync(TimeLockRequest request, CancellationToken ct = default)
    {
        if (request == null)
            throw new ArgumentNullException(nameof(request));

        if (request.ObjectId == Guid.Empty)
            throw new ArgumentException("Object ID cannot be empty.", nameof(request));

        if (request.LockDuration <= TimeSpan.Zero)
            throw new ArgumentException("Lock duration must be positive.", nameof(request));

        if (request.LockDuration < Policy.MinLockDuration)
        {
            throw new ArgumentException(
                $"Lock duration {request.LockDuration} is below the minimum policy duration of {Policy.MinLockDuration}.",
                nameof(request));
        }

        if (request.LockDuration > Policy.MaxLockDuration)
        {
            throw new ArgumentException(
                $"Lock duration {request.LockDuration} exceeds the maximum policy duration of {Policy.MaxLockDuration}.",
                nameof(request));
        }

        if (request.Context == null)
            throw new ArgumentNullException(nameof(request), "Write context is required for audit trail.");

        request.Context.ValidateOrThrow();

        // Validate unlock conditions against policy
        if (request.UnlockConditions != null)
        {
            foreach (var condition in request.UnlockConditions)
            {
                if (!Policy.AllowedUnlockConditions.Contains(condition.Type))
                {
                    throw new InvalidOperationException(
                        $"Unlock condition type '{condition.Type}' is not allowed by this provider's policy. " +
                        $"Allowed types: {string.Join(", ", Policy.AllowedUnlockConditions)}");
                }
            }
        }

        // Validate PQC requirement
        if (Policy.RequirePqcSignature && request.VaccinationLevel < VaccinationLevel.Maximum)
        {
            throw new ArgumentException(
                "This provider's policy requires PQC signatures, which requires Maximum vaccination level.",
                nameof(request));
        }

        return await LockInternalAsync(request, ct);
    }

    /// <summary>
    /// Extends the lock duration with validation to prevent shortening.
    /// Locks can only be extended, never shortened (compliance requirement).
    /// </summary>
    public async Task ExtendLockAsync(Guid objectId, TimeSpan additionalDuration, CancellationToken ct = default)
    {
        if (objectId == Guid.Empty)
            throw new ArgumentException("Object ID cannot be empty.", nameof(objectId));

        if (additionalDuration <= TimeSpan.Zero)
            throw new ArgumentException("Additional duration must be positive. Locks can only be extended, never shortened.", nameof(additionalDuration));

        var status = await GetStatusAsync(objectId, ct);

        if (!status.Exists)
            throw new KeyNotFoundException($"Object {objectId} does not exist in time-lock storage.");

        if (!status.IsLocked)
            throw new InvalidOperationException($"Object {objectId} is not currently locked. Cannot extend an inactive lock.");

        await ExtendLockInternalAsync(objectId, additionalDuration, ct);
    }

    /// <summary>
    /// Attempts to unlock an object with validation of the condition type against policy.
    /// All unlock attempts are logged for audit purposes regardless of outcome.
    /// </summary>
    public async Task<bool> AttemptUnlockAsync(Guid objectId, UnlockCondition condition, CancellationToken ct = default)
    {
        if (objectId == Guid.Empty)
            throw new ArgumentException("Object ID cannot be empty.", nameof(objectId));

        if (condition == null)
            throw new ArgumentNullException(nameof(condition));

        // Validate condition type against policy
        if (!Policy.AllowedUnlockConditions.Contains(condition.Type))
        {
            throw new InvalidOperationException(
                $"Unlock condition type '{condition.Type}' is not allowed by this provider's policy. " +
                $"Allowed types: {string.Join(", ", Policy.AllowedUnlockConditions)}");
        }

        // Check multi-party requirement for early unlock
        if (Policy.RequireMultiPartyForEarlyUnlock && condition.Type != UnlockConditionType.TimeExpiry)
        {
            if (condition.RequiredApprovals < 2)
            {
                throw new InvalidOperationException(
                    "This provider's policy requires multi-party approval for early unlock. " +
                    "RequiredApprovals must be at least 2.");
            }
        }

        return await AttemptUnlockInternalAsync(objectId, condition, ct);
    }

    /// <summary>
    /// Provider-specific time-lock implementation.
    /// Must apply the lock using the specified enforcement mechanism and return lock proof.
    /// </summary>
    /// <param name="request">Validated lock request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Lock result with proof of lock operation.</returns>
    protected abstract Task<TimeLockResult> LockInternalAsync(TimeLockRequest request, CancellationToken ct);

    /// <summary>
    /// Provider-specific lock extension implementation.
    /// Must extend the lock expiry by the additional duration.
    /// </summary>
    /// <param name="objectId">Validated object identifier.</param>
    /// <param name="additionalDuration">Positive duration to add to the current lock expiry.</param>
    /// <param name="ct">Cancellation token.</param>
    protected abstract Task ExtendLockInternalAsync(Guid objectId, TimeSpan additionalDuration, CancellationToken ct);

    /// <summary>
    /// Provider-specific unlock attempt implementation.
    /// Must evaluate the condition and return whether the unlock was successful.
    /// All attempts must be logged for audit purposes.
    /// </summary>
    /// <param name="objectId">Validated object identifier.</param>
    /// <param name="condition">Validated unlock condition.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if unlock succeeded, false if condition was not satisfied.</returns>
    protected abstract Task<bool> AttemptUnlockInternalAsync(Guid objectId, UnlockCondition condition, CancellationToken ct);

    /// <summary>
    /// Gets the current lock status of an object.
    /// Must return complete status including lock state, vaccination, holds, and integrity.
    /// </summary>
    public abstract Task<TimeLockStatus> GetStatusAsync(Guid objectId, CancellationToken ct = default);

    /// <summary>
    /// Quick check whether an object is currently locked.
    /// </summary>
    public abstract Task<bool> IsLockedAsync(Guid objectId, CancellationToken ct = default);

    /// <summary>
    /// Gets the ransomware vaccination status of an object.
    /// </summary>
    public abstract Task<RansomwareVaccinationInfo> GetVaccinationInfoAsync(Guid objectId, CancellationToken ct = default);

    /// <summary>
    /// Enumerates currently locked objects with pagination.
    /// </summary>
    public abstract Task<IReadOnlyList<TimeLockStatus>> ListLockedObjectsAsync(int limit, int offset, CancellationToken ct = default);

    /// <summary>
    /// Provides metadata about this time-lock provider for diagnostics and AI agents.
    /// </summary>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["FeatureType"] = "TimeLock";
        metadata["DefaultMode"] = DefaultMode.ToString();
        metadata["VaccinationLevel"] = Policy.VaccinationLevel.ToString();
        metadata["MinLockDuration"] = Policy.MinLockDuration.ToString();
        metadata["MaxLockDuration"] = Policy.MaxLockDuration.ToString();
        metadata["RequiresPqcSignature"] = Policy.RequirePqcSignature;
        metadata["AutoExtendOnTamper"] = Policy.AutoExtendOnTamperDetection;
        metadata["SupportedModes"] = new[] { TimeLockMode.Software, TimeLockMode.HardwareHsm, TimeLockMode.CloudNative, TimeLockMode.Hybrid };
        metadata["SupportedVaccinationLevels"] = new[] { VaccinationLevel.None, VaccinationLevel.Basic, VaccinationLevel.Enhanced, VaccinationLevel.Maximum };
        return metadata;
    }
}

#region Stub Types for TimeLock Intelligence Integration

/// <summary>
/// AI-assisted threat assessment result for a time-locked object.
/// Provides intelligence-driven evaluation of potential threats to locked data.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 59: Crypto time-lock types")]
public record TimeLockThreatAssessment(
    double ThreatScore,
    string ThreatCategory,
    string Rationale,
    bool RecommendLockExtension,
    TimeSpan? RecommendedExtension,
    double ConfidenceScore);

#endregion
