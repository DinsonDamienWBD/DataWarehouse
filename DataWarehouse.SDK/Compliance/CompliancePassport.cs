namespace DataWarehouse.SDK.Compliance;

/// <summary>
/// A portable per-object compliance certification record.
/// <para>
/// A CompliancePassport is the authoritative proof that a data object (or collection/tenant/system)
/// has been assessed against one or more regulatory frameworks and meets specified compliance
/// requirements. Passports carry regulation entries, audit timestamps, evidence chains,
/// digital signatures, and expiration metadata.
/// </para>
/// <para>
/// Passports are designed to be portable across sovereignty zones, enabling cross-border
/// data transfers with verifiable compliance provenance.
/// </para>
/// </summary>
public sealed record CompliancePassport
{
    /// <summary>
    /// Unique identifier for this passport, typically a UUID-based string.
    /// Used to reference, verify, and revoke passports.
    /// </summary>
    public required string PassportId { get; init; }

    /// <summary>
    /// Identifier of the data object this passport certifies.
    /// May reference an object, collection, tenant, or system depending on <see cref="Scope"/>.
    /// </summary>
    public required string ObjectId { get; init; }

    /// <summary>
    /// Current lifecycle status of this passport.
    /// </summary>
    public required PassportStatus Status { get; init; }

    /// <summary>
    /// Scope of certification coverage for this passport.
    /// </summary>
    public required PassportScope Scope { get; init; }

    /// <summary>
    /// Compliance entries, one per regulation or control assessed.
    /// Each entry records the compliance status for a specific regulation and control.
    /// </summary>
    public required IReadOnlyList<PassportEntry> Entries { get; init; }

    /// <summary>
    /// Chain of evidence supporting the compliance assertions in this passport.
    /// Evidence links provide verifiable proof of compliance assessment.
    /// </summary>
    public required IReadOnlyList<EvidenceLink> EvidenceChain { get; init; }

    /// <summary>
    /// Timestamp when this passport was originally issued.
    /// </summary>
    public required DateTimeOffset IssuedAt { get; init; }

    /// <summary>
    /// Timestamp when this passport expires and must be renewed.
    /// </summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>
    /// Timestamp of the most recent verification of this passport, if any.
    /// </summary>
    public DateTimeOffset? LastVerifiedAt { get; init; }

    /// <summary>
    /// Identifier of the entity (user, system, or service) that issued this passport.
    /// </summary>
    public required string IssuerId { get; init; }

    /// <summary>
    /// Optional tenant identifier scoping this passport to a specific tenant boundary.
    /// </summary>
    public string? TenantId { get; init; }

    /// <summary>
    /// Optional extensible metadata associated with this passport.
    /// Can carry custom properties such as tags, classification labels, or provenance data.
    /// </summary>
    public IReadOnlyDictionary<string, object>? Metadata { get; init; }

    /// <summary>
    /// Cryptographic digital signature of the passport contents.
    /// Used to verify that the passport has not been tampered with after issuance.
    /// </summary>
    public byte[]? DigitalSignature { get; init; }

    /// <summary>
    /// Checks whether this passport is currently valid.
    /// A passport is valid if its status is <see cref="PassportStatus.Active"/>
    /// and it has not yet expired.
    /// </summary>
    /// <returns><c>true</c> if the passport is active and not expired; otherwise <c>false</c>.</returns>
    public bool IsValid() => Status == PassportStatus.Active && ExpiresAt > DateTimeOffset.UtcNow;

    /// <summary>
    /// Checks whether this passport covers a specific regulation.
    /// </summary>
    /// <param name="regulationId">The regulation identifier to check (e.g., "GDPR", "HIPAA").</param>
    /// <returns><c>true</c> if any entry in this passport references the specified regulation.</returns>
    public bool CoversRegulation(string regulationId) =>
        Entries.Any(e => string.Equals(e.RegulationId, regulationId, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// A single compliance entry within a <see cref="CompliancePassport"/>.
/// <para>
/// Each entry records the assessment result for a specific regulation and control,
/// including the compliance score, assessment timestamp, and any conditions attached
/// to the compliance status.
/// </para>
/// </summary>
public sealed record PassportEntry
{
    /// <summary>
    /// Regulation identifier (e.g., "GDPR", "HIPAA", "SOX", "PCI-DSS").
    /// </summary>
    public required string RegulationId { get; init; }

    /// <summary>
    /// Specific control identifier within the regulation (e.g., "GDPR-Art17", "HIPAA-164.312").
    /// </summary>
    public required string ControlId { get; init; }

    /// <summary>
    /// Compliance status for this regulation/control combination.
    /// </summary>
    public required PassportStatus Status { get; init; }

    /// <summary>
    /// Timestamp when this entry was last assessed.
    /// </summary>
    public required DateTimeOffset AssessedAt { get; init; }

    /// <summary>
    /// Timestamp when the next compliance assessment is due, if scheduled.
    /// </summary>
    public DateTimeOffset? NextAssessmentDue { get; init; }

    /// <summary>
    /// Quantitative compliance score ranging from 0.0 (non-compliant) to 1.0 (fully compliant).
    /// </summary>
    public double ComplianceScore { get; init; }

    /// <summary>
    /// Optional list of conditions that must be maintained for continued compliance.
    /// </summary>
    public IReadOnlyList<string>? Conditions { get; init; }

    /// <summary>
    /// Optional evidence data supporting the compliance assessment for this entry.
    /// </summary>
    public IReadOnlyDictionary<string, object>? Evidence { get; init; }
}

/// <summary>
/// A link to evidence supporting a compliance passport assertion.
/// <para>
/// Evidence links provide verifiable references to artifacts, audit reports,
/// certificates, and other proof of compliance. Each link includes a content hash
/// for integrity verification.
/// </para>
/// </summary>
public sealed record EvidenceLink
{
    /// <summary>
    /// Unique identifier for this evidence artifact.
    /// </summary>
    public required string EvidenceId { get; init; }

    /// <summary>
    /// Type of evidence this link represents.
    /// </summary>
    public required EvidenceType Type { get; init; }

    /// <summary>
    /// Human-readable description of what this evidence proves.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// Timestamp when this evidence was collected or generated.
    /// </summary>
    public required DateTimeOffset CollectedAt { get; init; }

    /// <summary>
    /// Optional URI reference to the evidence artifact (e.g., storage path, URL).
    /// </summary>
    public string? ArtifactReference { get; init; }

    /// <summary>
    /// Optional SHA-256 hash of the evidence content for integrity verification.
    /// </summary>
    public byte[]? ContentHash { get; init; }

    /// <summary>
    /// Optional identifier of the entity that verified this evidence.
    /// </summary>
    public string? VerifierId { get; init; }
}

/// <summary>
/// Result of verifying a <see cref="CompliancePassport"/>.
/// <para>
/// Contains the verification outcome including signature validity, entry currency,
/// and any failure reasons. Used to determine whether a passport can be trusted
/// for compliance decisions.
/// </para>
/// </summary>
public sealed record PassportVerificationResult
{
    /// <summary>
    /// Whether the passport passed all verification checks.
    /// </summary>
    public required bool IsValid { get; init; }

    /// <summary>
    /// The passport that was verified.
    /// </summary>
    public required CompliancePassport Passport { get; init; }

    /// <summary>
    /// List of reasons why verification failed, if any.
    /// Empty when <see cref="IsValid"/> is <c>true</c>.
    /// </summary>
    public IReadOnlyList<string> FailureReasons { get; init; } = [];

    /// <summary>
    /// Timestamp when this verification was performed.
    /// </summary>
    public DateTimeOffset VerifiedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Whether the digital signature on the passport is valid.
    /// </summary>
    public bool SignatureValid { get; init; }

    /// <summary>
    /// Whether all passport entries are current (not past their next assessment due date).
    /// </summary>
    public bool AllEntriesCurrent { get; init; }
}
