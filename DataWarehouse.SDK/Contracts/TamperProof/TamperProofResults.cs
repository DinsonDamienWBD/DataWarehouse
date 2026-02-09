// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

namespace DataWarehouse.SDK.Contracts.TamperProof;

/// <summary>
/// Result of a secure write operation to tamper-proof storage.
/// Contains all information needed to read back and verify the written data.
/// </summary>
public class SecureWriteResult
{
    /// <summary>
    /// Whether the write operation succeeded completely across all tiers.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// Unique identifier for the written object.
    /// </summary>
    public required Guid ObjectId { get; init; }

    /// <summary>
    /// Version number of this object (1 for initial write, incremented for corrections).
    /// </summary>
    public required int Version { get; init; }

    /// <summary>
    /// Integrity hash of the original (unpadded, unsplit) data.
    /// Used for verification during reads.
    /// </summary>
    public required IntegrityHash IntegrityHash { get; init; }

    /// <summary>
    /// Manifest ID where metadata is stored.
    /// </summary>
    public required string ManifestId { get; init; }

    /// <summary>
    /// WORM record ID in the vault tier.
    /// </summary>
    public required string WormRecordId { get; init; }

    /// <summary>
    /// Blockchain anchor ID for audit trail.
    /// May be null if batched and not yet anchored.
    /// </summary>
    public string? BlockchainAnchorId { get; init; }

    /// <summary>
    /// Timestamp when the write completed.
    /// </summary>
    public required DateTimeOffset CompletedAt { get; init; }

    /// <summary>
    /// Write context that was applied (author, comment, etc.).
    /// </summary>
    public required WriteContextRecord WriteContext { get; init; }

    /// <summary>
    /// Number of data shards created.
    /// </summary>
    public required int ShardCount { get; init; }

    /// <summary>
    /// Total size of the original data in bytes.
    /// </summary>
    public required long OriginalSizeBytes { get; init; }

    /// <summary>
    /// Total size after padding in bytes.
    /// </summary>
    public required long PaddedSizeBytes { get; init; }

    /// <summary>
    /// Error message if Success is false.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Warning messages even if operation succeeded.
    /// Example: "WORM write succeeded but blockchain anchor pending".
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Degradation state after write (may be degraded if optional tier failed).
    /// </summary>
    public InstanceDegradationState DegradationState { get; init; } = InstanceDegradationState.Healthy;

    /// <summary>
    /// Creates a successful write result.
    /// </summary>
    public static SecureWriteResult CreateSuccess(
        Guid objectId,
        int version,
        IntegrityHash integrityHash,
        string manifestId,
        string wormRecordId,
        WriteContextRecord writeContext,
        int shardCount,
        long originalSizeBytes,
        long paddedSizeBytes,
        string? blockchainAnchorId = null,
        IReadOnlyList<string>? warnings = null)
    {
        return new SecureWriteResult
        {
            Success = true,
            ObjectId = objectId,
            Version = version,
            IntegrityHash = integrityHash,
            ManifestId = manifestId,
            WormRecordId = wormRecordId,
            BlockchainAnchorId = blockchainAnchorId,
            CompletedAt = DateTimeOffset.UtcNow,
            WriteContext = writeContext,
            ShardCount = shardCount,
            OriginalSizeBytes = originalSizeBytes,
            PaddedSizeBytes = paddedSizeBytes,
            Warnings = warnings ?? Array.Empty<string>()
        };
    }

    /// <summary>
    /// Creates a failed write result.
    /// </summary>
    public static SecureWriteResult CreateFailure(
        string errorMessage,
        Guid objectId = default,
        InstanceDegradationState degradationState = InstanceDegradationState.Corrupted)
    {
        return new SecureWriteResult
        {
            Success = false,
            ObjectId = objectId,
            Version = 0,
            IntegrityHash = IntegrityHash.Empty(),
            ManifestId = string.Empty,
            WormRecordId = string.Empty,
            CompletedAt = DateTimeOffset.UtcNow,
            WriteContext = new WriteContextRecord
            {
                Author = "system",
                Comment = "Failed write",
                Timestamp = DateTimeOffset.UtcNow
            },
            ShardCount = 0,
            OriginalSizeBytes = 0,
            PaddedSizeBytes = 0,
            ErrorMessage = errorMessage,
            DegradationState = degradationState
        };
    }
}

/// <summary>
/// Result of a secure read operation from tamper-proof storage.
/// </summary>
public class SecureReadResult
{
    /// <summary>
    /// Whether the read operation succeeded.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// Object ID that was read.
    /// </summary>
    public required Guid ObjectId { get; init; }

    /// <summary>
    /// Version that was read.
    /// </summary>
    public required int Version { get; init; }

    /// <summary>
    /// The reconstructed data (unpadded, reassembled).
    /// Only populated if Success is true.
    /// </summary>
    public byte[]? Data { get; init; }

    /// <summary>
    /// Integrity verification result.
    /// </summary>
    public required IntegrityVerificationResult IntegrityVerification { get; init; }

    /// <summary>
    /// Whether tampering was detected and recovery was performed.
    /// </summary>
    public required bool RecoveryPerformed { get; init; }

    /// <summary>
    /// Details of recovery if it was performed.
    /// </summary>
    public RecoveryResult? RecoveryDetails { get; init; }

    /// <summary>
    /// Timestamp when the read completed.
    /// </summary>
    public required DateTimeOffset CompletedAt { get; init; }

    /// <summary>
    /// Original write context from when the object was written.
    /// </summary>
    public WriteContextRecord? OriginalWriteContext { get; init; }

    /// <summary>
    /// Error message if Success is false.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Read mode that was used.
    /// </summary>
    public required ReadMode ReadMode { get; init; }

    /// <summary>
    /// Blockchain verification result (populated for Audit mode).
    /// </summary>
    public BlockchainVerificationDetails? BlockchainVerification { get; init; }

    /// <summary>
    /// Creates a successful read result.
    /// </summary>
    public static SecureReadResult CreateSuccess(
        Guid objectId,
        int version,
        byte[] data,
        IntegrityVerificationResult integrityVerification,
        ReadMode readMode,
        WriteContextRecord? originalWriteContext = null,
        bool recoveryPerformed = false,
        RecoveryResult? recoveryDetails = null)
    {
        return new SecureReadResult
        {
            Success = true,
            ObjectId = objectId,
            Version = version,
            Data = data,
            IntegrityVerification = integrityVerification,
            RecoveryPerformed = recoveryPerformed,
            RecoveryDetails = recoveryDetails,
            CompletedAt = DateTimeOffset.UtcNow,
            OriginalWriteContext = originalWriteContext,
            ReadMode = readMode
        };
    }

    /// <summary>
    /// Creates a successful read result with blockchain verification details.
    /// </summary>
    public static SecureReadResult CreateSuccess(
        Guid objectId,
        int version,
        byte[] data,
        IntegrityVerificationResult integrityVerification,
        ReadMode readMode,
        WriteContextRecord? originalWriteContext,
        bool recoveryPerformed,
        RecoveryResult? recoveryDetails,
        BlockchainVerificationDetails? blockchainVerification)
    {
        return new SecureReadResult
        {
            Success = true,
            ObjectId = objectId,
            Version = version,
            Data = data,
            IntegrityVerification = integrityVerification,
            RecoveryPerformed = recoveryPerformed,
            RecoveryDetails = recoveryDetails,
            CompletedAt = DateTimeOffset.UtcNow,
            OriginalWriteContext = originalWriteContext,
            ReadMode = readMode,
            BlockchainVerification = blockchainVerification
        };
    }

    /// <summary>
    /// Creates a failed read result.
    /// </summary>
    public static SecureReadResult CreateFailure(
        Guid objectId,
        int version,
        string errorMessage,
        ReadMode readMode,
        IntegrityVerificationResult? integrityVerification = null)
    {
        return new SecureReadResult
        {
            Success = false,
            ObjectId = objectId,
            Version = version,
            Data = null,
            IntegrityVerification = integrityVerification ?? IntegrityVerificationResult.CreateFailed("Read failed before verification"),
            RecoveryPerformed = false,
            CompletedAt = DateTimeOffset.UtcNow,
            ErrorMessage = errorMessage,
            ReadMode = readMode
        };
    }
}

/// <summary>
/// Result of a correction operation (creating a new version of an existing object).
/// </summary>
public class CorrectionResult
{
    /// <summary>
    /// Whether the correction succeeded.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// Original object ID that was corrected.
    /// </summary>
    public required Guid OriginalObjectId { get; init; }

    /// <summary>
    /// Original version that was corrected.
    /// </summary>
    public required int OriginalVersion { get; init; }

    /// <summary>
    /// New object ID for the corrected version.
    /// </summary>
    public required Guid NewObjectId { get; init; }

    /// <summary>
    /// New version number (incremented from original).
    /// </summary>
    public required int NewVersion { get; init; }

    /// <summary>
    /// Write result for the new version.
    /// </summary>
    public SecureWriteResult? WriteResult { get; init; }

    /// <summary>
    /// Correction context that was applied.
    /// </summary>
    public CorrectionContextRecord? CorrectionContext { get; init; }

    /// <summary>
    /// Audit chain linking to previous versions.
    /// </summary>
    public AuditChain? AuditChain { get; init; }

    /// <summary>
    /// Timestamp when the correction completed.
    /// </summary>
    public required DateTimeOffset CompletedAt { get; init; }

    /// <summary>
    /// Error message if Success is false.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Creates a successful correction result.
    /// </summary>
    public static CorrectionResult CreateSuccess(
        Guid originalObjectId,
        int originalVersion,
        Guid newObjectId,
        int newVersion,
        SecureWriteResult writeResult,
        CorrectionContextRecord correctionContext,
        AuditChain? auditChain = null)
    {
        return new CorrectionResult
        {
            Success = true,
            OriginalObjectId = originalObjectId,
            OriginalVersion = originalVersion,
            NewObjectId = newObjectId,
            NewVersion = newVersion,
            WriteResult = writeResult,
            CorrectionContext = correctionContext,
            AuditChain = auditChain,
            CompletedAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>
    /// Creates a failed correction result.
    /// </summary>
    public static CorrectionResult CreateFailure(
        Guid originalObjectId,
        int originalVersion,
        string errorMessage)
    {
        return new CorrectionResult
        {
            Success = false,
            OriginalObjectId = originalObjectId,
            OriginalVersion = originalVersion,
            NewObjectId = Guid.Empty,
            NewVersion = 0,
            CompletedAt = DateTimeOffset.UtcNow,
            ErrorMessage = errorMessage
        };
    }
}

/// <summary>
/// Result of an audit operation (retrieving full history and verification chain).
/// </summary>
public class AuditResult
{
    /// <summary>
    /// Whether the audit operation succeeded.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// Object ID that was audited.
    /// </summary>
    public required Guid ObjectId { get; init; }

    /// <summary>
    /// Complete audit chain showing all versions and corrections.
    /// </summary>
    public AuditChain? AuditChain { get; init; }

    /// <summary>
    /// All blockchain anchors for this object across all versions.
    /// </summary>
    public IReadOnlyList<BlockchainAnchor> BlockchainAnchors { get; init; } = Array.Empty<BlockchainAnchor>();

    /// <summary>
    /// All access logs for this object.
    /// </summary>
    public IReadOnlyList<AccessLog> AccessLogs { get; init; } = Array.Empty<AccessLog>();

    /// <summary>
    /// Integrity verification results for all versions.
    /// </summary>
    public IReadOnlyDictionary<int, IntegrityVerificationResult> IntegrityResults { get; init; }
        = new Dictionary<int, IntegrityVerificationResult>();

    /// <summary>
    /// Any tampering incidents detected.
    /// </summary>
    public IReadOnlyList<TamperIncident> TamperIncidents { get; init; } = Array.Empty<TamperIncident>();

    /// <summary>
    /// Timestamp when the audit completed.
    /// </summary>
    public required DateTimeOffset CompletedAt { get; init; }

    /// <summary>
    /// Error message if Success is false.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Creates a successful audit result.
    /// </summary>
    public static AuditResult CreateSuccess(
        Guid objectId,
        AuditChain auditChain,
        IReadOnlyList<BlockchainAnchor>? blockchainAnchors = null,
        IReadOnlyList<AccessLog>? accessLogs = null,
        IReadOnlyDictionary<int, IntegrityVerificationResult>? integrityResults = null,
        IReadOnlyList<TamperIncident>? tamperIncidents = null)
    {
        return new AuditResult
        {
            Success = true,
            ObjectId = objectId,
            AuditChain = auditChain,
            BlockchainAnchors = blockchainAnchors ?? Array.Empty<BlockchainAnchor>(),
            AccessLogs = accessLogs ?? Array.Empty<AccessLog>(),
            IntegrityResults = integrityResults ?? new Dictionary<int, IntegrityVerificationResult>(),
            TamperIncidents = tamperIncidents ?? Array.Empty<TamperIncident>(),
            CompletedAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>
    /// Creates a failed audit result.
    /// </summary>
    public static AuditResult CreateFailure(Guid objectId, string errorMessage)
    {
        return new AuditResult
        {
            Success = false,
            ObjectId = objectId,
            CompletedAt = DateTimeOffset.UtcNow,
            ErrorMessage = errorMessage
        };
    }
}

/// <summary>
/// Result of a recovery operation from WORM storage.
/// </summary>
public class RecoveryResult
{
    /// <summary>
    /// Whether the recovery succeeded.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// Object ID that was recovered.
    /// </summary>
    public required Guid ObjectId { get; init; }

    /// <summary>
    /// Version that was recovered.
    /// </summary>
    public required int Version { get; init; }

    /// <summary>
    /// Source of the recovery (WORM, Blockchain, etc.).
    /// </summary>
    public required string RecoverySource { get; init; }

    /// <summary>
    /// Details of what was recovered (which shards, etc.).
    /// </summary>
    public required string RecoveryDetails { get; init; }

    /// <summary>
    /// Tamper incident that triggered the recovery.
    /// </summary>
    public TamperIncident? TamperIncident { get; init; }

    /// <summary>
    /// Timestamp when the recovery completed.
    /// </summary>
    public required DateTimeOffset CompletedAt { get; init; }

    /// <summary>
    /// Error message if Success is false.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Indicates that manual intervention is required to complete recovery.
    /// Set to true when recovery behavior is ManualOnly or when automatic
    /// recovery has failed and administrator action is needed.
    /// </summary>
    public bool RequiresManualIntervention { get; init; } = false;

    /// <summary>
    /// The recovery behavior that was applied during this operation.
    /// </summary>
    public RecoveryBehavior Behavior { get; init; } = RecoveryBehavior.AutoRecover;

    /// <summary>
    /// Creates a successful recovery result.
    /// </summary>
    public static RecoveryResult CreateSuccess(
        Guid objectId,
        int version,
        string recoverySource,
        string recoveryDetails,
        TamperIncident? tamperIncident = null)
    {
        return new RecoveryResult
        {
            Success = true,
            ObjectId = objectId,
            Version = version,
            RecoverySource = recoverySource,
            RecoveryDetails = recoveryDetails,
            TamperIncident = tamperIncident,
            CompletedAt = DateTimeOffset.UtcNow,
            RequiresManualIntervention = false,
            Behavior = RecoveryBehavior.AutoRecover
        };
    }

    /// <summary>
    /// Creates a failed recovery result.
    /// </summary>
    public static RecoveryResult CreateFailure(
        Guid objectId,
        int version,
        string errorMessage)
    {
        return new RecoveryResult
        {
            Success = false,
            ObjectId = objectId,
            Version = version,
            RecoverySource = "N/A",
            RecoveryDetails = "Recovery failed",
            CompletedAt = DateTimeOffset.UtcNow,
            ErrorMessage = errorMessage,
            RequiresManualIntervention = false,
            Behavior = RecoveryBehavior.AutoRecover
        };
    }

    /// <summary>
    /// Creates a recovery result indicating manual intervention is required.
    /// Used when ManualOnly recovery behavior is configured.
    /// </summary>
    public static RecoveryResult CreateManualRequired(
        Guid objectId,
        int version,
        string details)
    {
        return new RecoveryResult
        {
            Success = false,
            ObjectId = objectId,
            Version = version,
            RecoverySource = "N/A",
            RecoveryDetails = details,
            CompletedAt = DateTimeOffset.UtcNow,
            RequiresManualIntervention = true,
            Behavior = RecoveryBehavior.ManualOnly,
            ErrorMessage = "Manual intervention required - automatic recovery is disabled"
        };
    }
}

/// <summary>
/// Defines the recovery behavior applied during tamper detection.
/// </summary>
public enum RecoveryBehavior
{
    /// <summary>
    /// Automatic recovery from backup sources (WORM, RAID parity).
    /// </summary>
    AutoRecover,

    /// <summary>
    /// Manual intervention required - no automatic recovery performed.
    /// Only logging and alerting occurs.
    /// </summary>
    ManualOnly,

    /// <summary>
    /// Fail closed - seal the affected block/shard and prevent all access.
    /// </summary>
    FailClosed,

    /// <summary>
    /// Alert only - log and notify but allow continued access to potentially corrupted data.
    /// </summary>
    AlertOnly
}

/// <summary>
/// Result of a secure correction operation with full audit trail.
/// Contains provenance information for compliance and forensic purposes.
/// </summary>
public class SecureCorrectionResult
{
    /// <summary>
    /// Whether the secure correction succeeded.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// Unique identifier for this audit entry.
    /// Can be used to trace the correction in audit logs.
    /// </summary>
    public required Guid AuditId { get; init; }

    /// <summary>
    /// Timestamp when the correction was applied.
    /// </summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// Principal who authorized the correction.
    /// Extracted from the authorization token.
    /// </summary>
    public required string AuthorizedBy { get; init; }

    /// <summary>
    /// Hash of the original (corrupted) data before correction.
    /// </summary>
    public required string OriginalHash { get; init; }

    /// <summary>
    /// Hash of the corrected data after the operation.
    /// </summary>
    public required string NewHash { get; init; }

    /// <summary>
    /// Reason provided for the correction.
    /// Required for audit trail and compliance.
    /// </summary>
    public required string Reason { get; init; }

    /// <summary>
    /// Block ID that was corrected.
    /// </summary>
    public required Guid BlockId { get; init; }

    /// <summary>
    /// Version number after correction.
    /// </summary>
    public int? NewVersion { get; init; }

    /// <summary>
    /// Provenance chain linking this correction to previous versions.
    /// </summary>
    public IReadOnlyList<ProvenanceEntry>? ProvenanceChain { get; init; }

    /// <summary>
    /// Error message if Success is false.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Creates a successful secure correction result.
    /// </summary>
    public static SecureCorrectionResult CreateSuccess(
        Guid auditId,
        string authorizedBy,
        string originalHash,
        string newHash,
        string reason,
        Guid blockId,
        int? newVersion = null,
        IReadOnlyList<ProvenanceEntry>? provenanceChain = null)
    {
        return new SecureCorrectionResult
        {
            Success = true,
            AuditId = auditId,
            Timestamp = DateTimeOffset.UtcNow,
            AuthorizedBy = authorizedBy,
            OriginalHash = originalHash,
            NewHash = newHash,
            Reason = reason,
            BlockId = blockId,
            NewVersion = newVersion,
            ProvenanceChain = provenanceChain
        };
    }

    /// <summary>
    /// Creates a failed secure correction result.
    /// </summary>
    public static SecureCorrectionResult CreateFailure(
        Guid blockId,
        string reason,
        string errorMessage)
    {
        return new SecureCorrectionResult
        {
            Success = false,
            AuditId = Guid.Empty,
            Timestamp = DateTimeOffset.UtcNow,
            AuthorizedBy = string.Empty,
            OriginalHash = string.Empty,
            NewHash = string.Empty,
            Reason = reason,
            BlockId = blockId,
            ErrorMessage = errorMessage
        };
    }
}

/// <summary>
/// Entry in a provenance chain tracking data lineage.
/// </summary>
public class ProvenanceEntry
{
    /// <summary>
    /// Unique identifier for this provenance entry.
    /// </summary>
    public required Guid EntryId { get; init; }

    /// <summary>
    /// Type of operation that created this entry.
    /// </summary>
    public required ProvenanceOperationType OperationType { get; init; }

    /// <summary>
    /// Timestamp when this operation occurred.
    /// </summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// Principal who performed the operation.
    /// </summary>
    public required string Principal { get; init; }

    /// <summary>
    /// Hash of the data at this point in the chain.
    /// </summary>
    public required string DataHash { get; init; }

    /// <summary>
    /// Reference to the previous entry in the chain.
    /// Null for the initial entry.
    /// </summary>
    public Guid? PreviousEntryId { get; init; }

    /// <summary>
    /// Additional metadata about this operation.
    /// </summary>
    public Dictionary<string, string>? Metadata { get; init; }
}

/// <summary>
/// Types of operations that can create provenance entries.
/// </summary>
public enum ProvenanceOperationType
{
    /// <summary>Initial creation of the data.</summary>
    Create,

    /// <summary>Standard write operation.</summary>
    Write,

    /// <summary>Correction of existing data.</summary>
    Correct,

    /// <summary>Recovery from backup.</summary>
    Recover,

    /// <summary>Secure correction with authorization.</summary>
    SecureCorrect,

    /// <summary>Migration from another system.</summary>
    Migrate
}

/// <summary>
/// Integrity hash value with algorithm type.
/// Used for verifying data has not been tampered with.
/// </summary>
public class IntegrityHash
{
    /// <summary>
    /// Hash algorithm used.
    /// </summary>
    public required HashAlgorithmType Algorithm { get; init; }

    /// <summary>
    /// Hash value as hex string.
    /// </summary>
    public required string HashValue { get; init; }

    /// <summary>
    /// Timestamp when this hash was computed.
    /// </summary>
    public required DateTimeOffset ComputedAt { get; init; }

    /// <summary>
    /// Creates an integrity hash.
    /// </summary>
    public static IntegrityHash Create(HashAlgorithmType algorithm, string hashValue)
    {
        return new IntegrityHash
        {
            Algorithm = algorithm,
            HashValue = hashValue,
            ComputedAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>
    /// Creates an empty integrity hash (for failed operations).
    /// </summary>
    public static IntegrityHash Empty()
    {
        return new IntegrityHash
        {
            Algorithm = HashAlgorithmType.SHA256,
            HashValue = string.Empty,
            ComputedAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>
    /// Converts to string representation.
    /// </summary>
    public override string ToString() => $"{Algorithm}:{HashValue}";

    /// <summary>
    /// Parses from string representation.
    /// </summary>
    public static IntegrityHash Parse(string value)
    {
        var parts = value.Split(':', 2);
        if (parts.Length != 2)
        {
            throw new ArgumentException($"Invalid integrity hash format: {value}");
        }

        if (!Enum.TryParse<HashAlgorithmType>(parts[0], out var algorithm))
        {
            throw new ArgumentException($"Invalid hash algorithm: {parts[0]}");
        }

        return new IntegrityHash
        {
            Algorithm = algorithm,
            HashValue = parts[1],
            ComputedAt = DateTimeOffset.UtcNow
        };
    }
}

/// <summary>
/// Shard-level hash for individual RAID shards.
/// </summary>
public class ShardHash
{
    /// <summary>
    /// Shard index (0-based).
    /// </summary>
    public required int ShardIndex { get; init; }

    /// <summary>
    /// Hash of this shard's data.
    /// </summary>
    public required IntegrityHash Hash { get; init; }

    /// <summary>
    /// Size of this shard in bytes.
    /// </summary>
    public required long SizeBytes { get; init; }

    /// <summary>
    /// Whether this is a parity shard.
    /// </summary>
    public required bool IsParity { get; init; }

    /// <summary>
    /// Creates a shard hash.
    /// </summary>
    public static ShardHash Create(int shardIndex, IntegrityHash hash, long sizeBytes, bool isParity)
    {
        return new ShardHash
        {
            ShardIndex = shardIndex,
            Hash = hash,
            SizeBytes = sizeBytes,
            IsParity = isParity
        };
    }
}

/// <summary>
/// Result of integrity verification.
/// </summary>
public class IntegrityVerificationResult
{
    /// <summary>
    /// Whether integrity verification passed.
    /// </summary>
    public required bool IntegrityValid { get; init; }

    /// <summary>
    /// Expected integrity hash from manifest.
    /// </summary>
    public IntegrityHash? ExpectedHash { get; init; }

    /// <summary>
    /// Actual computed hash from reconstructed data.
    /// </summary>
    public IntegrityHash? ActualHash { get; init; }

    /// <summary>
    /// Shard-level verification results.
    /// </summary>
    public IReadOnlyList<ShardVerificationResult> ShardResults { get; init; } = Array.Empty<ShardVerificationResult>();

    /// <summary>
    /// Whether blockchain anchor was verified (only in Audit mode).
    /// </summary>
    public bool? BlockchainVerified { get; init; }

    /// <summary>
    /// Error message if verification failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Timestamp when verification was performed.
    /// </summary>
    public required DateTimeOffset VerifiedAt { get; init; }

    /// <summary>
    /// Creates a successful verification result.
    /// </summary>
    public static IntegrityVerificationResult CreateValid(
        IntegrityHash expectedHash,
        IntegrityHash actualHash,
        IReadOnlyList<ShardVerificationResult>? shardResults = null,
        bool? blockchainVerified = null)
    {
        return new IntegrityVerificationResult
        {
            IntegrityValid = true,
            ExpectedHash = expectedHash,
            ActualHash = actualHash,
            ShardResults = shardResults ?? Array.Empty<ShardVerificationResult>(),
            BlockchainVerified = blockchainVerified,
            VerifiedAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>
    /// Creates a failed verification result.
    /// </summary>
    public static IntegrityVerificationResult CreateFailed(
        string errorMessage,
        IntegrityHash? expectedHash = null,
        IntegrityHash? actualHash = null,
        IReadOnlyList<ShardVerificationResult>? shardResults = null)
    {
        return new IntegrityVerificationResult
        {
            IntegrityValid = false,
            ExpectedHash = expectedHash,
            ActualHash = actualHash,
            ShardResults = shardResults ?? Array.Empty<ShardVerificationResult>(),
            ErrorMessage = errorMessage,
            VerifiedAt = DateTimeOffset.UtcNow
        };
    }
}

/// <summary>
/// Verification result for a single shard.
/// </summary>
public class ShardVerificationResult
{
    /// <summary>
    /// Shard index.
    /// </summary>
    public required int ShardIndex { get; init; }

    /// <summary>
    /// Whether this shard passed verification.
    /// </summary>
    public required bool Valid { get; init; }

    /// <summary>
    /// Expected hash for this shard.
    /// </summary>
    public IntegrityHash? ExpectedHash { get; init; }

    /// <summary>
    /// Actual computed hash for this shard.
    /// </summary>
    public IntegrityHash? ActualHash { get; init; }

    /// <summary>
    /// Error message if verification failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Creates a valid shard verification result.
    /// </summary>
    public static ShardVerificationResult CreateValid(int shardIndex, IntegrityHash expectedHash, IntegrityHash actualHash)
    {
        return new ShardVerificationResult
        {
            ShardIndex = shardIndex,
            Valid = true,
            ExpectedHash = expectedHash,
            ActualHash = actualHash
        };
    }

    /// <summary>
    /// Creates a failed shard verification result.
    /// </summary>
    public static ShardVerificationResult CreateFailed(
        int shardIndex,
        string errorMessage,
        IntegrityHash? expectedHash = null,
        IntegrityHash? actualHash = null)
    {
        return new ShardVerificationResult
        {
            ShardIndex = shardIndex,
            Valid = false,
            ExpectedHash = expectedHash,
            ActualHash = actualHash,
            ErrorMessage = errorMessage
        };
    }
}

/// <summary>
/// Audit chain showing all versions and corrections of an object.
/// Provides complete provenance from creation through all modifications.
/// </summary>
public class AuditChain
{
    /// <summary>
    /// Root object ID (the original).
    /// </summary>
    public required Guid RootObjectId { get; init; }

    /// <summary>
    /// All entries in the chain, ordered by version.
    /// </summary>
    public required IReadOnlyList<AuditChainEntry> Entries { get; init; }

    /// <summary>
    /// Total number of versions.
    /// </summary>
    public int TotalVersions => Entries.Count;

    /// <summary>
    /// Latest version number.
    /// </summary>
    public int LatestVersion => Entries.Count > 0 ? Entries[^1].Version : 0;

    /// <summary>
    /// Gets entry for a specific version.
    /// </summary>
    public AuditChainEntry? GetVersion(int version)
    {
        return Entries.FirstOrDefault(e => e.Version == version);
    }

    /// <summary>
    /// Gets all corrections (versions > 1).
    /// </summary>
    public IEnumerable<AuditChainEntry> GetCorrections()
    {
        return Entries.Where(e => e.Version > 1);
    }

    /// <summary>
    /// Creates an audit chain.
    /// </summary>
    public static AuditChain Create(Guid rootObjectId, IReadOnlyList<AuditChainEntry> entries)
    {
        return new AuditChain
        {
            RootObjectId = rootObjectId,
            Entries = entries.OrderBy(e => e.Version).ToList()
        };
    }
}

/// <summary>
/// Single entry in an audit chain.
/// </summary>
public class AuditChainEntry
{
    /// <summary>
    /// Object ID for this version.
    /// </summary>
    public required Guid ObjectId { get; init; }

    /// <summary>
    /// Version number.
    /// </summary>
    public required int Version { get; init; }

    /// <summary>
    /// Write context for this version.
    /// </summary>
    public required WriteContextRecord WriteContext { get; init; }

    /// <summary>
    /// Correction context if this is a correction (version > 1).
    /// </summary>
    public CorrectionContextRecord? CorrectionContext { get; init; }

    /// <summary>
    /// Integrity hash for this version.
    /// </summary>
    public required IntegrityHash IntegrityHash { get; init; }

    /// <summary>
    /// Manifest ID for this version.
    /// </summary>
    public required string ManifestId { get; init; }

    /// <summary>
    /// WORM record ID for this version.
    /// </summary>
    public required string WormRecordId { get; init; }

    /// <summary>
    /// Blockchain anchor ID for this version.
    /// </summary>
    public string? BlockchainAnchorId { get; init; }

    /// <summary>
    /// Reference to previous version (null for version 1).
    /// </summary>
    public Guid? PreviousObjectId { get; init; }

    /// <summary>
    /// Original size in bytes.
    /// </summary>
    public required long OriginalSizeBytes { get; init; }

    /// <summary>
    /// Timestamp when this version was created.
    /// </summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Creates an audit chain entry.
    /// </summary>
    public static AuditChainEntry Create(
        Guid objectId,
        int version,
        WriteContextRecord writeContext,
        IntegrityHash integrityHash,
        string manifestId,
        string wormRecordId,
        long originalSizeBytes,
        Guid? previousObjectId = null,
        CorrectionContextRecord? correctionContext = null,
        string? blockchainAnchorId = null)
    {
        return new AuditChainEntry
        {
            ObjectId = objectId,
            Version = version,
            WriteContext = writeContext,
            CorrectionContext = correctionContext,
            IntegrityHash = integrityHash,
            ManifestId = manifestId,
            WormRecordId = wormRecordId,
            BlockchainAnchorId = blockchainAnchorId,
            PreviousObjectId = previousObjectId,
            OriginalSizeBytes = originalSizeBytes,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }
}

/// <summary>
/// Result of a transactional write across all 4 tiers.
/// Tracks the state of each tier's write operation.
/// </summary>
public class TransactionResult
{
    /// <summary>
    /// Whether the entire transaction succeeded.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// Object ID for this transaction.
    /// </summary>
    public required Guid ObjectId { get; init; }

    /// <summary>
    /// Data tier write result.
    /// </summary>
    public TierWriteResult? DataTierResult { get; init; }

    /// <summary>
    /// Metadata tier write result.
    /// </summary>
    public TierWriteResult? MetadataTierResult { get; init; }

    /// <summary>
    /// WORM tier write result.
    /// </summary>
    public TierWriteResult? WormTierResult { get; init; }

    /// <summary>
    /// Blockchain tier write result.
    /// </summary>
    public TierWriteResult? BlockchainTierResult { get; init; }

    /// <summary>
    /// Overall degradation state after transaction.
    /// </summary>
    public required InstanceDegradationState DegradationState { get; init; }

    /// <summary>
    /// Whether rollback was attempted.
    /// </summary>
    public required bool RollbackAttempted { get; init; }

    /// <summary>
    /// Result of rollback if attempted.
    /// </summary>
    public RollbackResult? RollbackResult { get; init; }

    /// <summary>
    /// Error message if transaction failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Timestamp when transaction completed.
    /// </summary>
    public required DateTimeOffset CompletedAt { get; init; }

    /// <summary>
    /// Creates a successful transaction result.
    /// </summary>
    public static TransactionResult CreateSuccess(
        Guid objectId,
        TierWriteResult dataResult,
        TierWriteResult metadataResult,
        TierWriteResult wormResult,
        TierWriteResult blockchainResult)
    {
        return new TransactionResult
        {
            Success = true,
            ObjectId = objectId,
            DataTierResult = dataResult,
            MetadataTierResult = metadataResult,
            WormTierResult = wormResult,
            BlockchainTierResult = blockchainResult,
            DegradationState = InstanceDegradationState.Healthy,
            RollbackAttempted = false,
            CompletedAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>
    /// Creates a failed transaction result.
    /// </summary>
    public static TransactionResult CreateFailure(
        Guid objectId,
        string errorMessage,
        InstanceDegradationState degradationState,
        bool rollbackAttempted = false,
        RollbackResult? rollbackResult = null,
        TierWriteResult? dataResult = null,
        TierWriteResult? metadataResult = null,
        TierWriteResult? wormResult = null,
        TierWriteResult? blockchainResult = null)
    {
        return new TransactionResult
        {
            Success = false,
            ObjectId = objectId,
            DataTierResult = dataResult,
            MetadataTierResult = metadataResult,
            WormTierResult = wormResult,
            BlockchainTierResult = blockchainResult,
            DegradationState = degradationState,
            RollbackAttempted = rollbackAttempted,
            RollbackResult = rollbackResult,
            ErrorMessage = errorMessage,
            CompletedAt = DateTimeOffset.UtcNow
        };
    }
}

/// <summary>
/// Result of a write operation to a single storage tier.
/// </summary>
public class TierWriteResult
{
    /// <summary>
    /// Tier name (Data, Metadata, WORM, Blockchain).
    /// </summary>
    public required string TierName { get; init; }

    /// <summary>
    /// Whether the write to this tier succeeded.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// Storage instance ID.
    /// </summary>
    public required string InstanceId { get; init; }

    /// <summary>
    /// Resource ID written to this tier (path, record ID, etc.).
    /// </summary>
    public string? ResourceId { get; init; }

    /// <summary>
    /// Bytes written to this tier.
    /// </summary>
    public long? BytesWritten { get; init; }

    /// <summary>
    /// Error message if write failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Duration of the write operation.
    /// </summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>
    /// Creates a successful tier write result.
    /// </summary>
    public static TierWriteResult CreateSuccess(
        string tierName,
        string instanceId,
        string resourceId,
        long bytesWritten,
        TimeSpan? duration = null)
    {
        return new TierWriteResult
        {
            TierName = tierName,
            Success = true,
            InstanceId = instanceId,
            ResourceId = resourceId,
            BytesWritten = bytesWritten,
            Duration = duration
        };
    }

    /// <summary>
    /// Creates a failed tier write result.
    /// </summary>
    public static TierWriteResult CreateFailure(
        string tierName,
        string instanceId,
        string errorMessage,
        TimeSpan? duration = null)
    {
        return new TierWriteResult
        {
            TierName = tierName,
            Success = false,
            InstanceId = instanceId,
            ErrorMessage = errorMessage,
            Duration = duration
        };
    }
}

/// <summary>
/// Result of a rollback operation after transaction failure.
/// </summary>
public class RollbackResult
{
    /// <summary>
    /// Whether the rollback succeeded completely.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// Object ID that was being rolled back.
    /// </summary>
    public required Guid ObjectId { get; init; }

    /// <summary>
    /// Results of rollback for each tier.
    /// </summary>
    public required IReadOnlyList<TierRollbackResult> TierResults { get; init; }

    /// <summary>
    /// Orphaned WORM records that could not be rolled back.
    /// </summary>
    public IReadOnlyList<OrphanedWormRecord> OrphanedRecords { get; init; } = Array.Empty<OrphanedWormRecord>();

    /// <summary>
    /// Error message if rollback failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Timestamp when rollback completed.
    /// </summary>
    public required DateTimeOffset CompletedAt { get; init; }

    /// <summary>
    /// Creates a successful rollback result.
    /// </summary>
    public static RollbackResult CreateSuccess(
        Guid objectId,
        IReadOnlyList<TierRollbackResult> tierResults,
        IReadOnlyList<OrphanedWormRecord>? orphanedRecords = null)
    {
        return new RollbackResult
        {
            Success = true,
            ObjectId = objectId,
            TierResults = tierResults,
            OrphanedRecords = orphanedRecords ?? Array.Empty<OrphanedWormRecord>(),
            CompletedAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>
    /// Creates a failed rollback result.
    /// </summary>
    public static RollbackResult CreateFailure(
        Guid objectId,
        string errorMessage,
        IReadOnlyList<TierRollbackResult>? tierResults = null,
        IReadOnlyList<OrphanedWormRecord>? orphanedRecords = null)
    {
        return new RollbackResult
        {
            Success = false,
            ObjectId = objectId,
            TierResults = tierResults ?? Array.Empty<TierRollbackResult>(),
            OrphanedRecords = orphanedRecords ?? Array.Empty<OrphanedWormRecord>(),
            ErrorMessage = errorMessage,
            CompletedAt = DateTimeOffset.UtcNow
        };
    }
}

/// <summary>
/// Result of rolling back a single tier.
/// </summary>
public class TierRollbackResult
{
    /// <summary>
    /// Tier name.
    /// </summary>
    public required string TierName { get; init; }

    /// <summary>
    /// Whether the rollback succeeded for this tier.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// Action taken (deleted, skipped, etc.).
    /// </summary>
    public required string Action { get; init; }

    /// <summary>
    /// Error message if rollback failed.
    /// </summary>
    public string? ErrorMessage { get; init; }
}

// OrphanedWormRecord is defined in TamperProofManifest.cs

/// <summary>
/// Blockchain anchor record.
/// </summary>
public class BlockchainAnchor
{
    /// <summary>
    /// Anchor ID.
    /// </summary>
    public required string AnchorId { get; init; }

    /// <summary>
    /// Object ID this anchor is for.
    /// </summary>
    public required Guid ObjectId { get; init; }

    /// <summary>
    /// Version number.
    /// </summary>
    public required int Version { get; init; }

    /// <summary>
    /// Integrity hash that was anchored.
    /// </summary>
    public required IntegrityHash IntegrityHash { get; init; }

    /// <summary>
    /// Timestamp when anchored.
    /// </summary>
    public required DateTimeOffset AnchoredAt { get; init; }

    /// <summary>
    /// Blockchain transaction ID or block number.
    /// </summary>
    public string? BlockchainTxId { get; init; }

    /// <summary>
    /// Number of confirmations received.
    /// </summary>
    public int? Confirmations { get; init; }
}

/// <summary>
/// Access log entry.
/// </summary>
public class AccessLog
{
    /// <summary>
    /// Object ID that was accessed.
    /// </summary>
    public required Guid ObjectId { get; init; }

    /// <summary>
    /// Version that was accessed.
    /// </summary>
    public required int Version { get; init; }

    /// <summary>
    /// Type of access.
    /// </summary>
    public required AccessType AccessType { get; init; }

    /// <summary>
    /// Principal who accessed.
    /// </summary>
    public required string Principal { get; init; }

    /// <summary>
    /// Timestamp of access.
    /// </summary>
    public required DateTimeOffset AccessedAt { get; init; }

    /// <summary>
    /// Client IP address.
    /// </summary>
    public string? ClientIp { get; init; }

    /// <summary>
    /// Session ID.
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// Whether the access was successful.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// Error message if access failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Additional details about the access (verification summary, etc.).
    /// </summary>
    public string? Details { get; init; }
}

/// <summary>
/// Tamper incident record.
/// </summary>
public class TamperIncident
{
    /// <summary>
    /// Incident ID.
    /// </summary>
    public required Guid IncidentId { get; init; }

    /// <summary>
    /// Object ID that was tampered with.
    /// </summary>
    public required Guid ObjectId { get; init; }

    /// <summary>
    /// Version that was tampered with.
    /// </summary>
    public required int Version { get; init; }

    /// <summary>
    /// When the tampering was detected.
    /// </summary>
    public required DateTimeOffset DetectedAt { get; init; }

    /// <summary>
    /// What was tampered with (shards, manifest, etc.).
    /// </summary>
    public required string TamperedComponent { get; init; }

    /// <summary>
    /// Expected hash.
    /// </summary>
    public IntegrityHash? ExpectedHash { get; init; }

    /// <summary>
    /// Actual hash found.
    /// </summary>
    public IntegrityHash? ActualHash { get; init; }

    /// <summary>
    /// Attribution confidence.
    /// </summary>
    public required AttributionConfidence AttributionConfidence { get; init; }

    /// <summary>
    /// Suspected principal (if attributed).
    /// </summary>
    public string? SuspectedPrincipal { get; init; }

    /// <summary>
    /// Whether recovery was performed.
    /// </summary>
    public required bool RecoveryPerformed { get; init; }

    /// <summary>
    /// Recovery details if performed.
    /// </summary>
    public string? RecoveryDetails { get; init; }

    /// <summary>
    /// Whether administrators were notified.
    /// </summary>
    public required bool AdminNotified { get; init; }

    /// <summary>
    /// Additional details about the access.
    /// </summary>
    public string? Details { get; init; }
}

/// <summary>
/// Details of blockchain verification for Audit mode reads.
/// </summary>
public class BlockchainVerificationDetails
{
    /// <summary>
    /// Whether the blockchain anchor was verified.
    /// </summary>
    public required bool Verified { get; init; }

    /// <summary>
    /// Block number where the anchor was found.
    /// </summary>
    public long? BlockNumber { get; init; }

    /// <summary>
    /// Timestamp when the anchor was created.
    /// </summary>
    public DateTimeOffset? AnchoredAt { get; init; }

    /// <summary>
    /// Number of confirmations.
    /// </summary>
    public int? Confirmations { get; init; }

    /// <summary>
    /// The blockchain anchor record.
    /// </summary>
    public BlockchainAnchor? Anchor { get; init; }

    /// <summary>
    /// Error message if verification failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Creates successful verification details.
    /// </summary>
    public static BlockchainVerificationDetails CreateSuccess(
        long blockNumber,
        DateTimeOffset anchoredAt,
        int confirmations,
        BlockchainAnchor? anchor = null)
    {
        return new BlockchainVerificationDetails
        {
            Verified = true,
            BlockNumber = blockNumber,
            AnchoredAt = anchoredAt,
            Confirmations = confirmations,
            Anchor = anchor
        };
    }

    /// <summary>
    /// Creates failed verification details.
    /// </summary>
    public static BlockchainVerificationDetails CreateFailure(string errorMessage)
    {
        return new BlockchainVerificationDetails
        {
            Verified = false,
            ErrorMessage = errorMessage
        };
    }

    /// <summary>
    /// Creates from a BlockchainVerificationResult from the Services namespace.
    /// </summary>
    public static BlockchainVerificationDetails? FromServiceResult(object? serviceResult)
    {
        if (serviceResult == null) return null;

        // Use reflection to avoid circular reference between SDK and Plugin
        var type = serviceResult.GetType();
        var success = (bool?)type.GetProperty("Success")?.GetValue(serviceResult) ?? false;

        if (success)
        {
            return new BlockchainVerificationDetails
            {
                Verified = true,
                BlockNumber = (long?)type.GetProperty("BlockNumber")?.GetValue(serviceResult),
                AnchoredAt = (DateTimeOffset?)type.GetProperty("AnchoredAt")?.GetValue(serviceResult),
                Confirmations = (int?)type.GetProperty("Confirmations")?.GetValue(serviceResult)
            };
        }
        else
        {
            return new BlockchainVerificationDetails
            {
                Verified = false,
                ErrorMessage = (string?)type.GetProperty("ErrorMessage")?.GetValue(serviceResult)
            };
        }
    }
}

/// <summary>
/// Exception thrown when attempting to modify a sealed block or shard.
/// Sealed data is permanently locked and cannot be modified under any circumstances.
/// </summary>
public class BlockSealedException : Exception
{
    /// <summary>
    /// The sealed block ID that was attempted to be modified.
    /// </summary>
    public Guid BlockId { get; }

    /// <summary>
    /// When the block was sealed.
    /// </summary>
    public DateTime SealedAt { get; }

    /// <summary>
    /// The reason the block was sealed.
    /// </summary>
    public string Reason { get; }

    /// <summary>
    /// The shard index if a specific shard was sealed (null for entire block).
    /// </summary>
    public int? ShardIndex { get; init; }

    /// <summary>
    /// Creates a new BlockSealedException.
    /// </summary>
    /// <param name="blockId">The sealed block ID.</param>
    /// <param name="sealedAt">When the block was sealed.</param>
    /// <param name="reason">The reason for sealing.</param>
    public BlockSealedException(Guid blockId, DateTime sealedAt, string reason)
        : base($"Block {blockId} is sealed and cannot be modified. Sealed at: {sealedAt:O}. Reason: {reason}")
    {
        BlockId = blockId;
        SealedAt = sealedAt;
        Reason = reason;
    }

    /// <summary>
    /// Creates a new BlockSealedException with an inner exception.
    /// </summary>
    /// <param name="blockId">The sealed block ID.</param>
    /// <param name="sealedAt">When the block was sealed.</param>
    /// <param name="reason">The reason for sealing.</param>
    /// <param name="innerException">The inner exception.</param>
    public BlockSealedException(Guid blockId, DateTime sealedAt, string reason, Exception innerException)
        : base($"Block {blockId} is sealed and cannot be modified. Sealed at: {sealedAt:O}. Reason: {reason}", innerException)
    {
        BlockId = blockId;
        SealedAt = sealedAt;
        Reason = reason;
    }

    /// <summary>
    /// Creates a BlockSealedException for a sealed shard.
    /// </summary>
    /// <param name="blockId">The block ID containing the sealed shard.</param>
    /// <param name="shardIndex">The sealed shard index.</param>
    /// <param name="sealedAt">When the shard was sealed.</param>
    /// <param name="reason">The reason for sealing.</param>
    /// <returns>A new BlockSealedException for the shard.</returns>
    public static BlockSealedException ForShard(Guid blockId, int shardIndex, DateTime sealedAt, string reason)
    {
        return new BlockSealedException(blockId, sealedAt, reason)
        {
            ShardIndex = shardIndex
        };
    }
}
