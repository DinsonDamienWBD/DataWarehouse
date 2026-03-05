// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

namespace DataWarehouse.SDK.Contracts.TamperProof;

/// <summary>
/// Complete tamper incident report with full attribution analysis and evidence.
/// Generated when tampering is detected during integrity verification operations.
/// </summary>
public sealed class TamperIncidentReport
{
    /// <summary>
    /// Unique identifier for this tamper incident.
    /// </summary>
    public required Guid IncidentId { get; init; }

    /// <summary>
    /// Identifier of the object that was tampered with.
    /// </summary>
    public required Guid ObjectId { get; init; }

    /// <summary>
    /// Timestamp when the tampering was detected.
    /// </summary>
    public required DateTimeOffset DetectedAt { get; init; }

    /// <summary>
    /// Expected hash value from the manifest (trusted source).
    /// </summary>
    public required string ExpectedHash { get; init; }

    /// <summary>
    /// Actual hash value computed from the stored data.
    /// Mismatch indicates tampering has occurred.
    /// </summary>
    public required string ActualHash { get; init; }

    /// <summary>
    /// Name of the storage instance where tampering was detected.
    /// Example: "Primary-SSD", "Archive-Tape-1".
    /// </summary>
    public required string AffectedInstance { get; init; }

    /// <summary>
    /// List of shard indices affected by the tampering.
    /// Null if entire object is affected or sharding not applicable.
    /// </summary>
    public List<int>? AffectedShards { get; init; }

    /// <summary>
    /// Recovery action taken when tampering was detected.
    /// </summary>
    public required TamperRecoveryBehavior RecoveryAction { get; init; }

    /// <summary>
    /// Whether the automatic recovery operation succeeded.
    /// False indicates data may be unavailable or manual intervention required.
    /// </summary>
    public required bool RecoverySucceeded { get; init; }

    /// <summary>
    /// Confidence level in the attribution analysis.
    /// Indicates how certain we are about who caused the tampering.
    /// </summary>
    public required AttributionConfidence AttributionConfidence { get; init; }

    /// <summary>
    /// Principal (user, service account, system) suspected of tampering.
    /// Null if attribution confidence is Unknown.
    /// </summary>
    public string? SuspectedPrincipal { get; init; }

    /// <summary>
    /// Access log entries related to this incident.
    /// Used for attribution analysis and forensic investigation.
    /// </summary>
    public List<AccessLogEntry>? RelatedAccessLogs { get; init; }

    /// <summary>
    /// Estimated earliest time the tampering could have occurred.
    /// Based on last verified read and first detected tampering.
    /// </summary>
    public DateTimeOffset? EstimatedTamperTimeFrom { get; init; }

    /// <summary>
    /// Estimated latest time the tampering could have occurred.
    /// Based on access patterns and detection time.
    /// </summary>
    public DateTimeOffset? EstimatedTamperTimeTo { get; init; }

    /// <summary>
    /// Human-readable explanation of how attribution was determined.
    /// Example: "Only principal with write access during time window",
    /// "Logged admin operation correlates with hash mismatch".
    /// </summary>
    public string? AttributionReasoning { get; init; }

    /// <summary>
    /// Indicates if tampering appears to be internal (logged operation) vs external (physical access, malware).
    /// Null if cannot be determined.
    /// </summary>
    public bool? IsInternalTampering { get; init; }

    /// <summary>
    /// Detailed evidence collected during tamper detection.
    /// </summary>
    public required TamperEvidence Evidence { get; init; }

    /// <summary>
    /// Creates a new tamper incident report for detected tampering.
    /// </summary>
    /// <param name="objectId">Identifier of the tampered object.</param>
    /// <param name="expectedHash">Expected hash from manifest.</param>
    /// <param name="actualHash">Actual computed hash.</param>
    /// <param name="affectedInstance">Storage instance where tampering was detected.</param>
    /// <param name="recoveryAction">Recovery action taken.</param>
    /// <param name="recoverySucceeded">Whether recovery succeeded.</param>
    /// <param name="evidence">Evidence collected during detection.</param>
    /// <returns>A new tamper incident report.</returns>
    public static TamperIncidentReport Create(
        Guid objectId,
        string expectedHash,
        string actualHash,
        string affectedInstance,
        TamperRecoveryBehavior recoveryAction,
        bool recoverySucceeded,
        TamperEvidence evidence)
    {
        return new TamperIncidentReport
        {
            IncidentId = Guid.NewGuid(),
            ObjectId = objectId,
            DetectedAt = DateTimeOffset.UtcNow,
            ExpectedHash = expectedHash,
            ActualHash = actualHash,
            AffectedInstance = affectedInstance,
            RecoveryAction = recoveryAction,
            RecoverySucceeded = recoverySucceeded,
            AttributionConfidence = AttributionConfidence.Unknown,
            Evidence = evidence
        };
    }

    /// <summary>
    /// Creates a tamper incident report with attribution analysis.
    /// </summary>
    /// <param name="objectId">Identifier of the tampered object.</param>
    /// <param name="expectedHash">Expected hash from manifest.</param>
    /// <param name="actualHash">Actual computed hash.</param>
    /// <param name="affectedInstance">Storage instance where tampering was detected.</param>
    /// <param name="recoveryAction">Recovery action taken.</param>
    /// <param name="recoverySucceeded">Whether recovery succeeded.</param>
    /// <param name="evidence">Evidence collected during detection.</param>
    /// <param name="attribution">Attribution analysis result.</param>
    /// <returns>A new tamper incident report with attribution.</returns>
    public static TamperIncidentReport CreateWithAttribution(
        Guid objectId,
        string expectedHash,
        string actualHash,
        string affectedInstance,
        TamperRecoveryBehavior recoveryAction,
        bool recoverySucceeded,
        TamperEvidence evidence,
        AttributionAnalysis attribution)
    {
        return new TamperIncidentReport
        {
            IncidentId = Guid.NewGuid(),
            ObjectId = objectId,
            DetectedAt = DateTimeOffset.UtcNow,
            ExpectedHash = expectedHash,
            ActualHash = actualHash,
            AffectedInstance = affectedInstance,
            RecoveryAction = recoveryAction,
            RecoverySucceeded = recoverySucceeded,
            AttributionConfidence = attribution.Confidence,
            SuspectedPrincipal = attribution.SuspectedPrincipal,
            RelatedAccessLogs = attribution.RelatedAccessLogs,
            EstimatedTamperTimeFrom = attribution.EstimatedTamperTimeFrom,
            EstimatedTamperTimeTo = attribution.EstimatedTamperTimeTo,
            AttributionReasoning = attribution.Reasoning,
            IsInternalTampering = attribution.IsInternalTampering,
            Evidence = evidence
        };
    }

    /// <summary>
    /// Converts this full incident report to a summary view.
    /// </summary>
    /// <returns>Summary of this incident.</returns>
    public TamperIncidentSummary ToSummary()
    {
        return new TamperIncidentSummary
        {
            IncidentId = IncidentId,
            ObjectId = ObjectId,
            DetectedAt = DetectedAt,
            AffectedInstance = AffectedInstance,
            RecoverySucceeded = RecoverySucceeded,
            AttributionConfidence = AttributionConfidence,
            SuspectedPrincipal = SuspectedPrincipal,
            IsInternalTampering = IsInternalTampering
        };
    }
}

/// <summary>
/// Summary view of a tamper incident for listing and monitoring.
/// Contains key information without full evidence details.
/// </summary>
public sealed class TamperIncidentSummary
{
    /// <summary>
    /// Unique identifier for this tamper incident.
    /// </summary>
    public required Guid IncidentId { get; init; }

    /// <summary>
    /// Identifier of the object that was tampered with.
    /// </summary>
    public required Guid ObjectId { get; init; }

    /// <summary>
    /// Timestamp when the tampering was detected.
    /// </summary>
    public required DateTimeOffset DetectedAt { get; init; }

    /// <summary>
    /// Name of the storage instance where tampering was detected.
    /// </summary>
    public required string AffectedInstance { get; init; }

    /// <summary>
    /// Whether the automatic recovery operation succeeded.
    /// </summary>
    public required bool RecoverySucceeded { get; init; }

    /// <summary>
    /// Confidence level in the attribution analysis.
    /// </summary>
    public required AttributionConfidence AttributionConfidence { get; init; }

    /// <summary>
    /// Principal suspected of tampering, if identified.
    /// </summary>
    public string? SuspectedPrincipal { get; init; }

    /// <summary>
    /// Indicates if tampering appears to be internal vs external.
    /// Null if cannot be determined.
    /// </summary>
    public bool? IsInternalTampering { get; init; }
}

/// <summary>
/// Result of attribution analysis for a tamper incident.
/// Determines who likely caused the tampering and when it occurred.
/// </summary>
public sealed class AttributionAnalysis
{
    /// <summary>
    /// Confidence level in this attribution.
    /// </summary>
    public required AttributionConfidence Confidence { get; init; }

    /// <summary>
    /// Principal (user, service account, system) suspected of tampering.
    /// Null if confidence is Unknown.
    /// </summary>
    public string? SuspectedPrincipal { get; init; }

    /// <summary>
    /// Access log entries that contributed to this attribution.
    /// </summary>
    public List<AccessLogEntry>? RelatedAccessLogs { get; init; }

    /// <summary>
    /// Estimated earliest time the tampering could have occurred.
    /// Based on last verified read and first detected tampering.
    /// </summary>
    public DateTimeOffset? EstimatedTamperTimeFrom { get; init; }

    /// <summary>
    /// Estimated latest time the tampering could have occurred.
    /// Based on access patterns and detection time.
    /// </summary>
    public DateTimeOffset? EstimatedTamperTimeTo { get; init; }

    /// <summary>
    /// Human-readable explanation of how attribution was determined.
    /// Example: "Only principal with write access during time window",
    /// "Logged admin operation correlates with hash mismatch".
    /// </summary>
    public string? Reasoning { get; init; }

    /// <summary>
    /// Indicates if tampering appears to be internal (logged operation) vs external (physical access, malware).
    /// Null if cannot be determined.
    /// </summary>
    public bool? IsInternalTampering { get; init; }

    /// <summary>
    /// Creates an attribution analysis with unknown confidence.
    /// Used when no attribution information is available.
    /// </summary>
    /// <returns>Attribution analysis with Unknown confidence.</returns>
    public static AttributionAnalysis CreateUnknown()
    {
        return new AttributionAnalysis
        {
            Confidence = AttributionConfidence.Unknown,
            Reasoning = "Insufficient access log data or external tampering"
        };
    }

    /// <summary>
    /// Creates an attribution analysis for a suspected principal.
    /// </summary>
    /// <param name="suspectedPrincipal">Principal suspected of tampering.</param>
    /// <param name="relatedAccessLogs">Related access log entries.</param>
    /// <param name="reasoning">Explanation of attribution logic.</param>
    /// <param name="estimatedTimeFrom">Earliest possible tamper time.</param>
    /// <param name="estimatedTimeTo">Latest possible tamper time.</param>
    /// <returns>Attribution analysis with Suspected confidence.</returns>
    public static AttributionAnalysis CreateSuspected(
        string suspectedPrincipal,
        List<AccessLogEntry> relatedAccessLogs,
        string reasoning,
        DateTimeOffset? estimatedTimeFrom = null,
        DateTimeOffset? estimatedTimeTo = null)
    {
        return new AttributionAnalysis
        {
            Confidence = AttributionConfidence.Suspected,
            SuspectedPrincipal = suspectedPrincipal,
            RelatedAccessLogs = relatedAccessLogs,
            Reasoning = reasoning,
            EstimatedTamperTimeFrom = estimatedTimeFrom,
            EstimatedTamperTimeTo = estimatedTimeTo,
            IsInternalTampering = relatedAccessLogs.Count > 0
        };
    }

    /// <summary>
    /// Creates an attribution analysis for a likely principal.
    /// </summary>
    /// <param name="suspectedPrincipal">Principal likely responsible for tampering.</param>
    /// <param name="relatedAccessLogs">Related access log entries.</param>
    /// <param name="reasoning">Explanation of attribution logic.</param>
    /// <param name="estimatedTimeFrom">Earliest possible tamper time.</param>
    /// <param name="estimatedTimeTo">Latest possible tamper time.</param>
    /// <returns>Attribution analysis with Likely confidence.</returns>
    public static AttributionAnalysis CreateLikely(
        string suspectedPrincipal,
        List<AccessLogEntry> relatedAccessLogs,
        string reasoning,
        DateTimeOffset? estimatedTimeFrom = null,
        DateTimeOffset? estimatedTimeTo = null)
    {
        return new AttributionAnalysis
        {
            Confidence = AttributionConfidence.Likely,
            SuspectedPrincipal = suspectedPrincipal,
            RelatedAccessLogs = relatedAccessLogs,
            Reasoning = reasoning,
            EstimatedTamperTimeFrom = estimatedTimeFrom,
            EstimatedTamperTimeTo = estimatedTimeTo,
            IsInternalTampering = true
        };
    }

    /// <summary>
    /// Creates an attribution analysis with confirmed principal.
    /// </summary>
    /// <param name="confirmedPrincipal">Principal confirmed to have caused tampering.</param>
    /// <param name="relatedAccessLogs">Related access log entries.</param>
    /// <param name="reasoning">Explanation of attribution logic.</param>
    /// <param name="tamperTime">Exact or near-exact time of tampering.</param>
    /// <returns>Attribution analysis with Confirmed confidence.</returns>
    public static AttributionAnalysis CreateConfirmed(
        string confirmedPrincipal,
        List<AccessLogEntry> relatedAccessLogs,
        string reasoning,
        DateTimeOffset tamperTime)
    {
        return new AttributionAnalysis
        {
            Confidence = AttributionConfidence.Confirmed,
            SuspectedPrincipal = confirmedPrincipal,
            RelatedAccessLogs = relatedAccessLogs,
            Reasoning = reasoning,
            EstimatedTamperTimeFrom = tamperTime,
            EstimatedTamperTimeTo = tamperTime,
            IsInternalTampering = true
        };
    }
}

/// <summary>
/// Evidence collected during tamper detection and analysis.
/// Contains forensic data for investigation and compliance reporting.
/// </summary>
public sealed class TamperEvidence
{
    /// <summary>
    /// Hash algorithm used for verification.
    /// </summary>
    public required HashAlgorithmType HashAlgorithm { get; init; }

    /// <summary>
    /// Size of the corrupted data in bytes.
    /// </summary>
    public required long CorruptedDataSize { get; init; }

    /// <summary>
    /// Size of the expected data in bytes (from manifest).
    /// Mismatch indicates data truncation or expansion.
    /// </summary>
    public required long ExpectedDataSize { get; init; }

    /// <summary>
    /// Checksum of the manifest entry itself.
    /// Used to verify the manifest hasn't been tampered with.
    /// </summary>
    public required string ManifestChecksum { get; init; }

    /// <summary>
    /// Timestamp of the last verified read before detection.
    /// Helps establish the tampering time window.
    /// </summary>
    public DateTimeOffset? LastVerifiedRead { get; init; }

    /// <summary>
    /// Instance from which data was recovered (if recovery succeeded).
    /// Null if no recovery attempted or recovery failed.
    /// </summary>
    public string? RecoverySource { get; init; }

    /// <summary>
    /// Hash of the recovered data (should match ExpectedHash if recovery succeeded).
    /// </summary>
    public string? RecoveredDataHash { get; init; }

    /// <summary>
    /// Number of bytes that differ between expected and actual data.
    /// Null if byte-level comparison not performed.
    /// </summary>
    public long? CorruptedByteCount { get; init; }

    /// <summary>
    /// Byte offsets where corruption was detected.
    /// Null if full data comparison not performed or too many differences.
    /// Limited to first 100 offsets to prevent excessive data.
    /// </summary>
    public List<long>? CorruptedByteOffsets { get; init; }

    /// <summary>
    /// File system metadata from the affected instance.
    /// May include modification time, permissions, etc.
    /// Useful for detecting external tampering.
    /// </summary>
    public Dictionary<string, string>? FileSystemMetadata { get; init; }

    /// <summary>
    /// Additional diagnostic information.
    /// Free-form dictionary for implementation-specific details.
    /// </summary>
    public Dictionary<string, string>? AdditionalDiagnostics { get; init; }

    /// <summary>
    /// Creates basic tamper evidence for hash mismatch.
    /// </summary>
    /// <param name="hashAlgorithm">Hash algorithm used.</param>
    /// <param name="corruptedDataSize">Size of corrupted data.</param>
    /// <param name="expectedDataSize">Expected data size from manifest.</param>
    /// <param name="manifestChecksum">Checksum of manifest entry.</param>
    /// <returns>Basic tamper evidence.</returns>
    public static TamperEvidence Create(
        HashAlgorithmType hashAlgorithm,
        long corruptedDataSize,
        long expectedDataSize,
        string manifestChecksum)
    {
        return new TamperEvidence
        {
            HashAlgorithm = hashAlgorithm,
            CorruptedDataSize = corruptedDataSize,
            ExpectedDataSize = expectedDataSize,
            ManifestChecksum = manifestChecksum
        };
    }

    /// <summary>
    /// Creates tamper evidence with recovery information.
    /// </summary>
    /// <param name="hashAlgorithm">Hash algorithm used.</param>
    /// <param name="corruptedDataSize">Size of corrupted data.</param>
    /// <param name="expectedDataSize">Expected data size from manifest.</param>
    /// <param name="manifestChecksum">Checksum of manifest entry.</param>
    /// <param name="recoverySource">Instance from which data was recovered.</param>
    /// <param name="recoveredDataHash">Hash of recovered data.</param>
    /// <returns>Tamper evidence with recovery details.</returns>
    public static TamperEvidence CreateWithRecovery(
        HashAlgorithmType hashAlgorithm,
        long corruptedDataSize,
        long expectedDataSize,
        string manifestChecksum,
        string recoverySource,
        string recoveredDataHash)
    {
        return new TamperEvidence
        {
            HashAlgorithm = hashAlgorithm,
            CorruptedDataSize = corruptedDataSize,
            ExpectedDataSize = expectedDataSize,
            ManifestChecksum = manifestChecksum,
            RecoverySource = recoverySource,
            RecoveredDataHash = recoveredDataHash
        };
    }

    /// <summary>
    /// Creates detailed tamper evidence with byte-level corruption analysis.
    /// </summary>
    /// <param name="hashAlgorithm">Hash algorithm used.</param>
    /// <param name="corruptedDataSize">Size of corrupted data.</param>
    /// <param name="expectedDataSize">Expected data size from manifest.</param>
    /// <param name="manifestChecksum">Checksum of manifest entry.</param>
    /// <param name="corruptedByteCount">Number of corrupted bytes.</param>
    /// <param name="corruptedByteOffsets">Offsets of corrupted bytes.</param>
    /// <returns>Detailed tamper evidence.</returns>
    public static TamperEvidence CreateDetailed(
        HashAlgorithmType hashAlgorithm,
        long corruptedDataSize,
        long expectedDataSize,
        string manifestChecksum,
        long corruptedByteCount,
        List<long> corruptedByteOffsets)
    {
        return new TamperEvidence
        {
            HashAlgorithm = hashAlgorithm,
            CorruptedDataSize = corruptedDataSize,
            ExpectedDataSize = expectedDataSize,
            ManifestChecksum = manifestChecksum,
            CorruptedByteCount = corruptedByteCount,
            CorruptedByteOffsets = corruptedByteOffsets
        };
    }
}
