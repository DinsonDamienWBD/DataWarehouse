// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Contracts.TamperProof;

/// <summary>
/// Defines the policy governing time-lock behavior for a provider or storage tier.
/// Specifies duration bounds, allowed unlock conditions, vaccination requirements,
/// and automatic tamper-response behavior.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 59: Crypto time-lock types")]
public record TimeLockPolicy
{
    /// <summary>
    /// Minimum allowed lock duration. Requests with shorter durations are rejected.
    /// Enforces compliance minimums (e.g., SEC 17a-4 requires minimum retention periods).
    /// </summary>
    public required TimeSpan MinLockDuration { get; init; }

    /// <summary>
    /// Maximum allowed lock duration. Requests with longer durations are rejected.
    /// Prevents accidentally locking objects for unreasonable periods.
    /// </summary>
    public required TimeSpan MaxLockDuration { get; init; }

    /// <summary>
    /// Default lock duration applied when a request does not specify an explicit duration.
    /// Must be between <see cref="MinLockDuration"/> and <see cref="MaxLockDuration"/>.
    /// </summary>
    public required TimeSpan DefaultLockDuration { get; init; }

    /// <summary>
    /// Unlock condition types permitted by this policy.
    /// Requests containing conditions not in this list are rejected.
    /// </summary>
    public required UnlockConditionType[] AllowedUnlockConditions { get; init; }

    /// <summary>
    /// When true, any attempt to unlock before the lock expiry requires multi-party approval,
    /// regardless of the unlock condition type specified.
    /// </summary>
    public required bool RequireMultiPartyForEarlyUnlock { get; init; }

    /// <summary>
    /// Default vaccination level applied to objects locked under this policy.
    /// </summary>
    public required VaccinationLevel VaccinationLevel { get; init; }

    /// <summary>
    /// When true, the lock duration is automatically extended if tampering is detected.
    /// This prevents attackers from waiting out a lock period while modifying the object.
    /// </summary>
    public required bool AutoExtendOnTamperDetection { get; init; }

    /// <summary>
    /// When true, all locked objects must include a post-quantum cryptographic signature.
    /// Required for Maximum vaccination level; optional for other levels.
    /// </summary>
    public required bool RequirePqcSignature { get; init; }
}

/// <summary>
/// Result of a successful time-lock operation.
/// Contains proof of lock including timestamps, content hash, and vaccination details.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 59: Crypto time-lock types")]
public class TimeLockResult
{
    /// <summary>
    /// Unique identifier of the locked object.
    /// </summary>
    public required Guid ObjectId { get; init; }

    /// <summary>
    /// Unique identifier for this lock instance. Used to reference the lock in
    /// extend, unlock, and status operations. Provider-specific format.
    /// </summary>
    public required string LockId { get; init; }

    /// <summary>
    /// UTC timestamp when the lock was applied.
    /// </summary>
    public required DateTimeOffset LockedAt { get; init; }

    /// <summary>
    /// UTC timestamp when the lock expires and the object becomes accessible.
    /// For <see cref="UnlockConditionType.NeverUnlock"/>, this is <see cref="DateTimeOffset.MaxValue"/>.
    /// </summary>
    public required DateTimeOffset UnlocksAt { get; init; }

    /// <summary>
    /// The enforcement mechanism used for this lock.
    /// </summary>
    public required TimeLockMode TimeLockMode { get; init; }

    /// <summary>
    /// Cryptographic hash of the object content at the time of locking.
    /// Used for integrity verification throughout the lock period.
    /// </summary>
    public required string ContentHash { get; init; }

    /// <summary>
    /// Ransomware vaccination level applied to this locked object.
    /// </summary>
    public required VaccinationLevel VaccinationLevel { get; init; }

    /// <summary>
    /// Post-quantum cryptographic signature algorithm used, if applicable.
    /// Null when PQC signatures are not required or not supported by the provider.
    /// Examples: "CRYSTALS-Dilithium", "SPHINCS+", "FALCON".
    /// </summary>
    public string? PqcSignatureAlgorithm { get; init; }

    /// <summary>
    /// Provider-specific metadata about the lock operation.
    /// May include HSM key IDs, cloud lock version IDs, blockchain transaction hashes, etc.
    /// </summary>
    public Dictionary<string, string>? ProviderMetadata { get; init; }
}

/// <summary>
/// Current status of a time-locked object.
/// Provides complete view of lock state, vaccination level, legal holds, and integrity.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 59: Crypto time-lock types")]
public class TimeLockStatus
{
    /// <summary>
    /// Whether the object exists in the time-lock provider's records.
    /// </summary>
    public required bool Exists { get; init; }

    /// <summary>
    /// Object identifier.
    /// </summary>
    public required Guid ObjectId { get; init; }

    /// <summary>
    /// Whether the object is currently locked.
    /// Computed from <see cref="UnlocksAt"/>: true if the current time is before the unlock time.
    /// </summary>
    public bool IsLocked => UnlocksAt.HasValue && DateTimeOffset.UtcNow < UnlocksAt.Value;

    /// <summary>
    /// UTC timestamp when the lock expires.
    /// Null if the object is not locked or does not exist.
    /// </summary>
    public DateTimeOffset? UnlocksAt { get; init; }

    /// <summary>
    /// UTC timestamp when the lock was applied.
    /// Null if the object is not locked or does not exist.
    /// </summary>
    public DateTimeOffset? LockedAt { get; init; }

    /// <summary>
    /// Lock instance identifier.
    /// Null if the object is not locked or does not exist.
    /// </summary>
    public string? LockId { get; init; }

    /// <summary>
    /// The enforcement mechanism used for this lock.
    /// </summary>
    public required TimeLockMode TimeLockMode { get; init; }

    /// <summary>
    /// Ransomware vaccination level applied to this object.
    /// </summary>
    public required VaccinationLevel VaccinationLevel { get; init; }

    /// <summary>
    /// Active legal holds on this object that prevent unlock even after time expiry.
    /// Empty if no legal holds are active.
    /// </summary>
    public IReadOnlyList<LegalHold> ActiveHolds { get; init; } = Array.Empty<LegalHold>();

    /// <summary>
    /// Whether tampering has been detected on this object since it was locked.
    /// When true, the object may have been compromised and should be investigated.
    /// </summary>
    public required bool TamperDetected { get; init; }

    /// <summary>
    /// UTC timestamp of the last integrity check performed on this object.
    /// Null if no integrity checks have been performed.
    /// </summary>
    public DateTimeOffset? LastIntegrityCheck { get; init; }
}

/// <summary>
/// Defines a condition that must be satisfied to unlock a time-locked object.
/// Multiple conditions can be required for multi-factor unlock.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 59: Crypto time-lock types")]
public record UnlockCondition
{
    /// <summary>
    /// The type of unlock condition.
    /// </summary>
    public required UnlockConditionType Type { get; init; }

    /// <summary>
    /// Condition-specific parameters. Contents depend on <see cref="Type"/>:
    /// <list type="bullet">
    /// <item><description><see cref="UnlockConditionType.TimeExpiry"/>: "ExpiryUtc" (DateTimeOffset)</description></item>
    /// <item><description><see cref="UnlockConditionType.MultiPartyApproval"/>: "QuorumSize" (int), "ApproverIds" (string[])</description></item>
    /// <item><description><see cref="UnlockConditionType.EmergencyBreakGlass"/>: "Justification" (string), "AuthorizedBy" (string)</description></item>
    /// <item><description><see cref="UnlockConditionType.ComplianceRelease"/>: "AuthorityId" (string), "ReleaseOrder" (string)</description></item>
    /// <item><description><see cref="UnlockConditionType.NeverUnlock"/>: no parameters required</description></item>
    /// </list>
    /// </summary>
    public required Dictionary<string, object> Parameters { get; init; }

    /// <summary>
    /// Number of approvals required to satisfy this condition.
    /// Defaults to 1. For <see cref="UnlockConditionType.MultiPartyApproval"/>,
    /// this represents the M in an M-of-N quorum.
    /// </summary>
    public int RequiredApprovals { get; init; } = 1;

    /// <summary>
    /// Optional expiry time for this unlock condition.
    /// If the condition is not satisfied by this time, it becomes invalid
    /// and a new condition must be submitted. Null means no expiry.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; init; }
}

/// <summary>
/// Provides a comprehensive view of an object's ransomware vaccination status.
/// Combines time-lock, integrity, cryptographic, and blockchain verification results
/// into a single assessment with an overall threat score.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 59: Crypto time-lock types")]
public record RansomwareVaccinationInfo
{
    /// <summary>
    /// The vaccination protection level applied to this object.
    /// </summary>
    public required VaccinationLevel VaccinationLevel { get; init; }

    /// <summary>
    /// Whether a time-lock is currently active on this object.
    /// </summary>
    public required bool TimeLockActive { get; init; }

    /// <summary>
    /// Whether the object's integrity has been verified (content hash matches).
    /// </summary>
    public required bool IntegrityVerified { get; init; }

    /// <summary>
    /// Whether the post-quantum cryptographic signature is valid.
    /// Null if PQC signatures are not configured for this object.
    /// </summary>
    public bool? PqcSignatureValid { get; init; }

    /// <summary>
    /// Whether the object's existence has been anchored to a blockchain.
    /// Null if blockchain anchoring is not configured for this object.
    /// </summary>
    public bool? BlockchainAnchored { get; init; }

    /// <summary>
    /// UTC timestamp of the last ransomware vaccination scan for this object.
    /// </summary>
    public required DateTimeOffset LastScanAt { get; init; }

    /// <summary>
    /// Overall threat score from 0.0 (no threat detected) to 1.0 (confirmed compromise).
    /// Calculated from the combination of all vaccination checks. Values above 0.5
    /// indicate elevated risk and should trigger investigation.
    /// </summary>
    public required double ThreatScore { get; init; }
}

/// <summary>
/// Request to apply a cryptographic time-lock to an object.
/// Specifies the lock duration, enforcement mechanism, vaccination level,
/// and conditions under which the lock can be released.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 59: Crypto time-lock types")]
public record TimeLockRequest
{
    /// <summary>
    /// Unique identifier of the object to lock.
    /// </summary>
    public required Guid ObjectId { get; init; }

    /// <summary>
    /// Duration for which the object should be locked.
    /// Must be within the provider's policy bounds
    /// (<see cref="TimeLockPolicy.MinLockDuration"/> to <see cref="TimeLockPolicy.MaxLockDuration"/>).
    /// </summary>
    public required TimeSpan LockDuration { get; init; }

    /// <summary>
    /// The enforcement mechanism to use for this lock.
    /// </summary>
    public required TimeLockMode TimeLockMode { get; init; }

    /// <summary>
    /// Ransomware vaccination level to apply to the locked object.
    /// </summary>
    public required VaccinationLevel VaccinationLevel { get; init; }

    /// <summary>
    /// Conditions that must be satisfied to unlock this object before time expiry.
    /// If empty, the object can only be unlocked by time expiry.
    /// </summary>
    public required UnlockCondition[] UnlockConditions { get; init; }

    /// <summary>
    /// Write context providing author attribution and audit trail for the lock operation.
    /// </summary>
    public required WriteContext Context { get; init; }
}
