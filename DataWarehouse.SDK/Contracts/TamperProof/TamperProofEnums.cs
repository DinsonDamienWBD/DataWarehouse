// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

namespace DataWarehouse.SDK.Contracts.TamperProof;

/// <summary>
/// Hash algorithm for integrity verification in tamper-proof storage.
/// </summary>
public enum HashAlgorithmType
{
    /// <summary>SHA-256 hash algorithm (256-bit output).</summary>
    SHA256,

    /// <summary>SHA-384 hash algorithm (384-bit output).</summary>
    SHA384,

    /// <summary>SHA-512 hash algorithm (512-bit output).</summary>
    SHA512,

    /// <summary>Blake3 hash algorithm (256-bit output, faster than SHA).</summary>
    Blake3
}

/// <summary>
/// Consensus mode for multi-node tamper-proof deployments.
/// </summary>
public enum ConsensusMode
{
    /// <summary>
    /// Single writer, no consensus needed. Fast but no high availability.
    /// Suitable for single-node deployments or when external coordination exists.
    /// </summary>
    SingleWriter,

    /// <summary>
    /// Raft-based consensus for writes. Slower but provides strong consistency.
    /// Requires quorum of nodes for write operations.
    /// </summary>
    RaftConsensus
}

/// <summary>
/// WORM (Write-Once-Read-Many) enforcement mechanism.
/// </summary>
public enum WormEnforcementMode
{
    /// <summary>
    /// Software-enforced immutability. Can be bypassed with admin access (logged).
    /// Suitable for compliance where hardware WORM is not available.
    /// </summary>
    Software,

    /// <summary>
    /// Hardware-enforced immutability (e.g., AWS S3 Object Lock, Azure Immutable Blob).
    /// Cannot be bypassed even by administrators during retention period.
    /// </summary>
    HardwareIntegrated,

    /// <summary>
    /// Software primary with hardware backup verification.
    /// Uses software enforcement locally with periodic verification against hardware WORM.
    /// </summary>
    Hybrid
}

/// <summary>
/// Behavior when tampering is detected during read operations.
/// This is a runtime-changeable behavioral setting.
/// </summary>
public enum TamperRecoveryBehavior
{
    /// <summary>
    /// Automatically recover from WORM, log internally, serve recovered data.
    /// User is not notified of the recovery unless they check logs.
    /// </summary>
    AutoRecoverSilent,

    /// <summary>
    /// Recover from WORM, generate incident report, alert admin.
    /// Data is served after recovery, with notification to administrators.
    /// </summary>
    AutoRecoverWithReport,

    /// <summary>
    /// Do not serve data. Alert admin and wait for manual intervention.
    /// Most secure option - prevents potentially corrupted data from being served.
    /// </summary>
    AlertAndWait
}

/// <summary>
/// Read verification level for tamper-proof storage operations.
/// Higher verification levels are slower but provide stronger guarantees.
/// </summary>
public enum ReadMode
{
    /// <summary>
    /// Skip full verification, trust shard-level hashes only. Fastest mode.
    /// Suitable for high-throughput scenarios where occasional tampering is acceptable.
    /// </summary>
    Fast,

    /// <summary>
    /// Verify reconstructed data against manifest hash. Default mode.
    /// Provides good balance between security and performance.
    /// </summary>
    Verified,

    /// <summary>
    /// Full verification including blockchain anchor check and access logging.
    /// Slowest but provides complete chain-of-custody verification.
    /// </summary>
    Audit
}

/// <summary>
/// Instance degradation state for tamper-proof storage instances.
/// Indicates the operational health of individual storage tiers.
/// </summary>
public enum InstanceDegradationState
{
    /// <summary>All operations normal. Full redundancy and recovery capability.</summary>
    Healthy,

    /// <summary>
    /// Some redundancy lost but fully operational.
    /// Example: RAID array with one failed disk but still functional.
    /// </summary>
    Degraded,

    /// <summary>
    /// Read-only mode. Writes are blocked until issue is resolved.
    /// Data can still be read and verified.
    /// </summary>
    DegradedReadOnly,

    /// <summary>
    /// Operating but WORM recovery unavailable.
    /// Data is accessible but automatic recovery from WORM is not possible.
    /// </summary>
    DegradedNoRecovery,

    /// <summary>
    /// Instance unreachable. No operations possible.
    /// May be due to network, storage, or configuration issues.
    /// </summary>
    Offline,

    /// <summary>
    /// Data corruption detected. Requires manual intervention.
    /// Operations blocked until administrator resolves the issue.
    /// </summary>
    Corrupted
}

/// <summary>
/// Type of access for logging and attribution purposes.
/// </summary>
public enum AccessType
{
    /// <summary>Read operation on object data.</summary>
    Read,

    /// <summary>Write operation creating new object.</summary>
    Write,

    /// <summary>Correction operation creating new version of existing object.</summary>
    Correct,

    /// <summary>Delete operation (for non-WORM tiers).</summary>
    Delete,

    /// <summary>Read operation on metadata only.</summary>
    MetadataRead,

    /// <summary>Write operation on metadata only.</summary>
    MetadataWrite,

    /// <summary>Administrative operation (config change, manual recovery, etc.).</summary>
    AdminOperation,

    /// <summary>System maintenance operation (garbage collection, compaction, etc.).</summary>
    SystemMaintenance
}

/// <summary>
/// Confidence level for tamper attribution analysis.
/// Indicates how certain we are about who tampered with the data.
/// </summary>
public enum AttributionConfidence
{
    /// <summary>
    /// Cannot determine who tampered.
    /// May indicate external/physical access, or system failure.
    /// </summary>
    Unknown,

    /// <summary>
    /// Possible suspect based on access patterns.
    /// Multiple principals accessed during the suspected time window.
    /// </summary>
    Suspected,

    /// <summary>
    /// Strong correlation with specific access.
    /// One principal had exclusive access during the suspected time window.
    /// </summary>
    Likely,

    /// <summary>
    /// Definitive attribution.
    /// Logged write operation directly correlates with hash mismatch.
    /// </summary>
    Confirmed
}

/// <summary>
/// Behavior when a transactional write fails partway through.
/// Determines how to handle partial writes, especially to WORM storage.
/// </summary>
public enum TransactionFailureBehavior
{
    /// <summary>
    /// Strict mode: If any write fails (including WORM), rollback all and fail the transaction.
    /// WORM writes that cannot be rolled back become orphaned.
    /// </summary>
    Strict,

    /// <summary>
    /// Degraded mode: If non-critical write fails (e.g., WORM), continue with degraded state.
    /// Data is usable but without full protection until issue is resolved.
    /// </summary>
    AllowDegraded
}

/// <summary>
/// Status of an orphaned WORM record from a failed transaction.
/// </summary>
public enum OrphanedWormStatus
{
    /// <summary>Orphaned due to transaction failure after WORM write.</summary>
    TransactionFailed,

    /// <summary>Marked for cleanup when retention expires.</summary>
    PendingExpiry,

    /// <summary>Retention expired, can be cleaned up.</summary>
    Expired,

    /// <summary>Manually reviewed by administrator.</summary>
    Reviewed
}
