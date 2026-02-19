// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Contracts.TamperProof;

/// <summary>
/// Specifies the mechanism used to enforce cryptographic time-locks on objects.
/// Determines whether locks are enforced in software, hardware HSM, cloud-native
/// immutability features, or a combination of approaches.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 59: Crypto time-lock types")]
public enum TimeLockMode
{
    /// <summary>
    /// Software-enforced time-lock using filesystem ACLs and clock-based expiry.
    /// Can be bypassed with administrative access (all overrides are logged and audited).
    /// Suitable for environments without HSM or cloud-native immutability support.
    /// </summary>
    Software,

    /// <summary>
    /// Hardware Security Module (HSM) backed time-release keys.
    /// The decryption key is sealed inside the HSM and cannot be released until the
    /// lock expiry time, providing tamper-resistant enforcement independent of software.
    /// </summary>
    HardwareHsm,

    /// <summary>
    /// Cloud-native immutability enforcement such as S3 Object Lock or Azure Immutable Blob
    /// in temporal mode. Delegates lock enforcement to the cloud provider's immutability
    /// guarantees, which cannot be bypassed even by account administrators during the lock period.
    /// </summary>
    CloudNative,

    /// <summary>
    /// Hybrid enforcement combining software-based locking with hardware HSM verification.
    /// Software enforces the lock locally while the HSM independently verifies that the
    /// lock period has not been tampered with. Provides defense-in-depth against both
    /// software and configuration-level attacks.
    /// </summary>
    Hybrid
}

/// <summary>
/// Specifies the type of condition required to unlock a time-locked object.
/// Multiple conditions can be combined for multi-factor unlock requirements.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 59: Crypto time-lock types")]
public enum UnlockConditionType
{
    /// <summary>
    /// Unlock when the UTC clock reaches the specified expiry time.
    /// The most common condition -- the object becomes accessible after the lock duration elapses.
    /// </summary>
    TimeExpiry,

    /// <summary>
    /// Unlock requires M-of-N quorum approval from designated key holders.
    /// Used for high-value objects where no single party should have unilateral unlock authority.
    /// The required number of approvals is specified in <see cref="UnlockCondition.RequiredApprovals"/>.
    /// </summary>
    MultiPartyApproval,

    /// <summary>
    /// Emergency break-glass override that bypasses normal unlock conditions.
    /// All break-glass operations are fully audited with mandatory justification.
    /// Intended for disaster recovery or critical business continuity scenarios only.
    /// </summary>
    EmergencyBreakGlass,

    /// <summary>
    /// Unlock authorized by a regulatory or compliance authority.
    /// Used when objects are locked for regulatory compliance and can only be released
    /// by the governing authority (e.g., SEC, GDPR data protection officer).
    /// </summary>
    ComplianceRelease,

    /// <summary>
    /// Permanent lock that can never be unlocked. The object is sealed for archival forever.
    /// Used for permanent retention requirements where data must be preserved indefinitely
    /// without any possibility of modification or deletion.
    /// </summary>
    NeverUnlock
}

/// <summary>
/// Specifies the ransomware vaccination protection level applied to a time-locked object.
/// Higher levels provide stronger guarantees against ransomware and data tampering
/// at the cost of increased storage overhead and processing time.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 59: Crypto time-lock types")]
public enum VaccinationLevel
{
    /// <summary>
    /// No ransomware vaccination protection. Object has standard time-lock only.
    /// </summary>
    None,

    /// <summary>
    /// Basic protection: time-lock enforcement only.
    /// Prevents modification during the lock period but does not verify integrity
    /// or provide cryptographic proof of authenticity.
    /// </summary>
    Basic,

    /// <summary>
    /// Enhanced protection: time-lock combined with integrity verification.
    /// Includes periodic hash verification to detect any unauthorized modifications.
    /// Detects both ransomware encryption and silent data corruption.
    /// </summary>
    Enhanced,

    /// <summary>
    /// Maximum protection: time-lock + integrity check + post-quantum cryptographic
    /// signature + blockchain anchor. Provides the strongest possible guarantee that
    /// the object has not been tampered with, is resilient against quantum computing
    /// attacks, and has an independently verifiable proof of existence on a blockchain.
    /// </summary>
    Maximum
}
