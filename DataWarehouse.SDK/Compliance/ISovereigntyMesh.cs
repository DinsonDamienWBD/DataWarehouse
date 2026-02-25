namespace DataWarehouse.SDK.Compliance;

/// <summary>
/// Represents a sovereignty zone that enforces data governance rules for specific jurisdictions.
/// <para>
/// A sovereignty zone defines the regulatory boundaries and enforcement rules for data
/// within a set of jurisdictions. Zones evaluate data operations against their action rules
/// and required regulations, enabling declarative governance enforcement.
/// </para>
/// </summary>
public interface ISovereigntyZone
{
    /// <summary>
    /// Unique identifier for this sovereignty zone (e.g., "eu-gdpr-zone", "us-hipaa-zone").
    /// </summary>
    string ZoneId { get; }

    /// <summary>
    /// Human-readable display name for this zone.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// ISO country codes of jurisdictions governed by this zone (e.g., "DE", "FR", "US-CA").
    /// </summary>
    IReadOnlyList<string> Jurisdictions { get; }

    /// <summary>
    /// Regulation identifiers that must be enforced within this zone (e.g., "GDPR", "HIPAA").
    /// </summary>
    IReadOnlyList<string> RequiredRegulations { get; }

    /// <summary>
    /// Tag-based action rules where the key is a tag pattern (e.g., "pii", "sensitive-health")
    /// and the value is the enforcement action to apply when the tag matches.
    /// </summary>
    IReadOnlyDictionary<string, ZoneAction> ActionRules { get; }

    /// <summary>
    /// Whether this zone is currently active and enforcing its rules.
    /// Inactive zones are bypassed during enforcement evaluation.
    /// </summary>
    bool IsActive { get; }

    /// <summary>
    /// Evaluates a data operation against this zone's rules and regulations.
    /// </summary>
    /// <param name="objectId">Identifier of the data object being evaluated.</param>
    /// <param name="passport">Optional compliance passport for the object, used to verify regulation coverage.</param>
    /// <param name="context">Additional context for evaluation (e.g., tags, classification, operation type).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The enforcement action determined by this zone's rules.</returns>
    Task<ZoneAction> EvaluateAsync(
        string objectId,
        CompliancePassport? passport,
        IReadOnlyDictionary<string, object> context,
        CancellationToken ct);
}

/// <summary>
/// Enforces sovereignty zone policies for data operations and cross-zone transfers.
/// <para>
/// The zone enforcer is responsible for evaluating whether data can move between zones,
/// registering and managing zone definitions, and producing enforcement results
/// that downstream systems use to gate data operations.
/// </para>
/// </summary>
public interface IZoneEnforcer
{
    /// <summary>
    /// Enforces sovereignty rules for a data transfer between two zones.
    /// Evaluates the source and destination zone policies, checks passport coverage,
    /// and returns the enforcement decision.
    /// </summary>
    /// <param name="objectId">Identifier of the data object being transferred.</param>
    /// <param name="sourceZoneId">Zone the data is currently in.</param>
    /// <param name="destinationZoneId">Zone the data is being transferred to.</param>
    /// <param name="passport">Optional compliance passport for the data object.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Enforcement result with the action, denial reason (if any), and required actions.</returns>
    Task<ZoneEnforcementResult> EnforceAsync(
        string objectId,
        string sourceZoneId,
        string destinationZoneId,
        CompliancePassport? passport,
        CancellationToken ct);

    /// <summary>
    /// Retrieves all sovereignty zones that apply to a specific jurisdiction.
    /// </summary>
    /// <param name="jurisdictionCode">ISO country code or region code.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of zones governing the specified jurisdiction.</returns>
    Task<IReadOnlyList<ISovereigntyZone>> GetZonesForJurisdictionAsync(string jurisdictionCode, CancellationToken ct);

    /// <summary>
    /// Retrieves a specific sovereignty zone by its identifier.
    /// </summary>
    /// <param name="zoneId">Unique zone identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The zone if found; otherwise <c>null</c>.</returns>
    Task<ISovereigntyZone?> GetZoneAsync(string zoneId, CancellationToken ct);

    /// <summary>
    /// Registers a new sovereignty zone in the enforcement system.
    /// </summary>
    /// <param name="zone">The zone definition to register.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RegisterZoneAsync(ISovereigntyZone zone, CancellationToken ct);

    /// <summary>
    /// Deactivates a sovereignty zone, preventing it from enforcing rules.
    /// The zone definition is retained but marked as inactive.
    /// </summary>
    /// <param name="zoneId">Unique zone identifier to deactivate.</param>
    /// <param name="ct">Cancellation token.</param>
    Task DeactivateZoneAsync(string zoneId, CancellationToken ct);
}

/// <summary>
/// Result of a sovereignty zone enforcement evaluation.
/// <para>
/// Contains the enforcement decision, the action to take, any denial reasons,
/// and a list of required actions that must be completed before the operation can proceed.
/// </para>
/// </summary>
public sealed record ZoneEnforcementResult
{
    /// <summary>
    /// Whether the operation is allowed to proceed (possibly with conditions).
    /// </summary>
    public required bool Allowed { get; init; }

    /// <summary>
    /// The specific enforcement action determined by zone policy.
    /// </summary>
    public required ZoneAction Action { get; init; }

    /// <summary>
    /// Reason for denial, if the operation was not allowed.
    /// </summary>
    public string? DenialReason { get; init; }

    /// <summary>
    /// List of actions that must be completed before the transfer can proceed
    /// (e.g., "encrypt-data", "obtain-dpo-approval", "anonymize-pii").
    /// </summary>
    public IReadOnlyList<string>? RequiredActions { get; init; }

    /// <summary>
    /// Identifier of the source zone in this enforcement evaluation.
    /// </summary>
    public required string SourceZoneId { get; init; }

    /// <summary>
    /// Identifier of the destination zone in this enforcement evaluation.
    /// </summary>
    public required string DestinationZoneId { get; init; }

    /// <summary>
    /// Timestamp when this enforcement evaluation was performed.
    /// </summary>
    public DateTimeOffset EvaluatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Protocol for negotiating, evaluating, and logging cross-border data transfers.
/// <para>
/// Manages the lifecycle of cross-border data transfers including negotiating transfer
/// agreements based on legal bases (SCCs, BCRs, adequacy decisions), evaluating
/// pending transfers, and maintaining an auditable transfer history.
/// </para>
/// </summary>
public interface ICrossBorderProtocol
{
    /// <summary>
    /// Negotiates a transfer agreement between two jurisdictions based on applicable
    /// legal bases and the compliance passport of the data being transferred.
    /// </summary>
    /// <param name="sourceJurisdiction">ISO code of the originating jurisdiction.</param>
    /// <param name="destJurisdiction">ISO code of the destination jurisdiction.</param>
    /// <param name="passport">Compliance passport for the data being transferred.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A transfer agreement record with the negotiation outcome and conditions.</returns>
    Task<TransferAgreementRecord> NegotiateTransferAsync(
        string sourceJurisdiction,
        string destJurisdiction,
        CompliancePassport passport,
        CancellationToken ct);

    /// <summary>
    /// Evaluates the current status of a previously negotiated transfer.
    /// </summary>
    /// <param name="transferId">Identifier of the transfer to evaluate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The current transfer decision.</returns>
    Task<TransferDecision> EvaluateTransferAsync(string transferId, CancellationToken ct);

    /// <summary>
    /// Logs a completed or attempted cross-border transfer for audit purposes.
    /// </summary>
    /// <param name="log">Transfer log entry to record.</param>
    /// <param name="ct">Cancellation token.</param>
    Task LogTransferAsync(CrossBorderTransferLog log, CancellationToken ct);

    /// <summary>
    /// Retrieves the transfer history for a specific data object.
    /// </summary>
    /// <param name="objectId">Identifier of the data object.</param>
    /// <param name="limit">Maximum number of history entries to return.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Transfer history ordered by most recent first.</returns>
    Task<IReadOnlyList<CrossBorderTransferLog>> GetTransferHistoryAsync(string objectId, int limit, CancellationToken ct);
}

/// <summary>
/// Record of a negotiated cross-border data transfer agreement.
/// <para>
/// Captures the legal basis, conditions, and expiration of an agreement between
/// two jurisdictions for data transfer. Supported legal bases include Standard
/// Contractual Clauses (SCC), Binding Corporate Rules (BCR), and Adequacy Decisions.
/// </para>
/// </summary>
public sealed record TransferAgreementRecord
{
    /// <summary>
    /// Unique identifier for this transfer agreement.
    /// </summary>
    public required string AgreementId { get; init; }

    /// <summary>
    /// ISO code of the jurisdiction where data originates.
    /// </summary>
    public required string SourceJurisdiction { get; init; }

    /// <summary>
    /// ISO code of the jurisdiction where data is being sent.
    /// </summary>
    public required string DestinationJurisdiction { get; init; }

    /// <summary>
    /// Decision outcome of the transfer negotiation.
    /// </summary>
    public required TransferDecision Decision { get; init; }

    /// <summary>
    /// Optional conditions that must be met for the transfer to proceed
    /// (e.g., "data-must-be-encrypted", "dpo-approval-required").
    /// </summary>
    public IReadOnlyList<string>? Conditions { get; init; }

    /// <summary>
    /// Timestamp when this agreement was negotiated.
    /// </summary>
    public DateTimeOffset NegotiatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Optional expiration timestamp for the agreement. After expiration, a new negotiation is required.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>
    /// Legal basis for the transfer (e.g., "SCC" for Standard Contractual Clauses,
    /// "BCR" for Binding Corporate Rules, "AdequacyDecision" for EU adequacy decision).
    /// </summary>
    public string? LegalBasis { get; init; }
}

/// <summary>
/// Audit log entry for a cross-border data transfer.
/// <para>
/// Records the details of a completed or attempted cross-border transfer including
/// the data object, jurisdictions, passport reference, decision, and any associated
/// transfer agreement.
/// </para>
/// </summary>
public sealed record CrossBorderTransferLog
{
    /// <summary>
    /// Unique identifier for this transfer event.
    /// </summary>
    public required string TransferId { get; init; }

    /// <summary>
    /// Identifier of the data object that was transferred.
    /// </summary>
    public required string ObjectId { get; init; }

    /// <summary>
    /// ISO code of the originating jurisdiction.
    /// </summary>
    public required string SourceJurisdiction { get; init; }

    /// <summary>
    /// ISO code of the destination jurisdiction.
    /// </summary>
    public required string DestinationJurisdiction { get; init; }

    /// <summary>
    /// Identifier of the compliance passport associated with the transferred data.
    /// </summary>
    public required string PassportId { get; init; }

    /// <summary>
    /// Decision outcome for this transfer.
    /// </summary>
    public required TransferDecision Decision { get; init; }

    /// <summary>
    /// Optional identifier of the transfer agreement that authorized this transfer.
    /// </summary>
    public string? AgreementId { get; init; }

    /// <summary>
    /// Timestamp when this transfer occurred.
    /// </summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Optional extensible metadata for the transfer (e.g., transfer method, data categories).
    /// </summary>
    public IReadOnlyDictionary<string, object>? Metadata { get; init; }
}

/// <summary>
/// Top-level orchestrator for sovereignty mesh operations.
/// <para>
/// The sovereignty mesh provides a unified interface for zone enforcement,
/// cross-border transfer protocols, passport issuance, and sovereignty checks.
/// It coordinates between <see cref="IZoneEnforcer"/> and <see cref="ICrossBorderProtocol"/>
/// to provide declarative, policy-driven data governance.
/// </para>
/// </summary>
public interface ISovereigntyMesh
{
    /// <summary>
    /// The zone enforcer responsible for evaluating zone policies and managing zone definitions.
    /// </summary>
    IZoneEnforcer ZoneEnforcer { get; }

    /// <summary>
    /// The cross-border protocol handler for negotiating and logging international data transfers.
    /// </summary>
    ICrossBorderProtocol CrossBorderProtocol { get; }

    /// <summary>
    /// Issues a new compliance passport for a data object, certifying it against
    /// the specified regulations.
    /// </summary>
    /// <param name="objectId">Identifier of the data object to certify.</param>
    /// <param name="regulations">List of regulation identifiers to certify against (e.g., "GDPR", "HIPAA").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A newly issued compliance passport.</returns>
    Task<CompliancePassport> IssuePassportAsync(string objectId, IReadOnlyList<string> regulations, CancellationToken ct);

    /// <summary>
    /// Verifies the validity of an existing compliance passport, checking signature,
    /// entry currency, and status.
    /// </summary>
    /// <param name="passportId">Identifier of the passport to verify.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Verification result with validity status and any failure reasons.</returns>
    Task<PassportVerificationResult> VerifyPassportAsync(string passportId, CancellationToken ct);

    /// <summary>
    /// Performs a sovereignty check for moving data between two locations,
    /// evaluating zone policies and transfer requirements.
    /// </summary>
    /// <param name="objectId">Identifier of the data object.</param>
    /// <param name="sourceLocation">Source location or jurisdiction code.</param>
    /// <param name="destLocation">Destination location or jurisdiction code.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Zone enforcement result with the decision and any required actions.</returns>
    Task<ZoneEnforcementResult> CheckSovereigntyAsync(
        string objectId,
        string sourceLocation,
        string destLocation,
        CancellationToken ct);
}
