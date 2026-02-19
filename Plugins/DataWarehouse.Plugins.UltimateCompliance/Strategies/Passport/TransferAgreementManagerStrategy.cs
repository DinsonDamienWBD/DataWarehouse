using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Compliance;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.Passport;

/// <summary>
/// Manages the lifecycle of cross-border transfer agreements including creation, renewal,
/// revocation, and validation with full audit tracking.
/// <para>
/// On initialization, pre-configures bilateral agreements for common jurisdiction pairs
/// including EU internal, EU-to-adequate-country, US internal, and APEC CBPR transfers.
/// All agreement mutations are recorded in an immutable audit log.
/// </para>
/// </summary>
public sealed class TransferAgreementManagerStrategy : ComplianceStrategyBase
{
    private readonly ConcurrentDictionary<string, TransferAgreementRecord> _agreements = new();
    private readonly ConcurrentDictionary<string, TransferAgreementRecord> _agreementsById = new();
    private readonly ConcurrentDictionary<string, List<AgreementAuditEntry>> _auditLog = new();
    private readonly object _auditLock = new();

    private long _agreementsCreated;
    private long _agreementsRenewed;
    private long _agreementsRevoked;
    private long _agreementsExpired;

    /// <inheritdoc/>
    public override string StrategyId => "transfer-agreement-manager";

    /// <inheritdoc/>
    public override string StrategyName => "Transfer Agreement Manager";

    /// <inheritdoc/>
    public override string Framework => "CompliancePassport";

    /// <inheritdoc/>
    public override Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken cancellationToken = default)
    {
        base.InitializeAsync(configuration, cancellationToken);
        SeedPreConfiguredAgreements();
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------
    // Agreement lifecycle methods
    // ------------------------------------------------------------------

    /// <summary>
    /// Creates a new transfer agreement between two jurisdictions with the specified
    /// legal basis, conditions, and validity period.
    /// </summary>
    /// <param name="sourceJurisdiction">ISO code of the originating jurisdiction.</param>
    /// <param name="destJurisdiction">ISO code of the destination jurisdiction.</param>
    /// <param name="legalBasis">Legal basis for the transfer (e.g., "SCC", "BCR", "AdequacyDecision").</param>
    /// <param name="conditions">Conditions that must be met for transfers under this agreement.</param>
    /// <param name="validity">Duration for which the agreement remains valid.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The newly created transfer agreement record.</returns>
    public Task<TransferAgreementRecord> CreateAgreementAsync(
        string sourceJurisdiction,
        string destJurisdiction,
        string legalBasis,
        IReadOnlyList<string> conditions,
        TimeSpan validity,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceJurisdiction);
        ArgumentException.ThrowIfNullOrWhiteSpace(destJurisdiction);
        ArgumentException.ThrowIfNullOrWhiteSpace(legalBasis);
        ArgumentNullException.ThrowIfNull(conditions);
        ct.ThrowIfCancellationRequested();

        IncrementCounter("agreement_manager.create");

        var now = DateTimeOffset.UtcNow;
        var agreement = new TransferAgreementRecord
        {
            AgreementId = Guid.NewGuid().ToString("N"),
            SourceJurisdiction = sourceJurisdiction,
            DestinationJurisdiction = destJurisdiction,
            Decision = TransferDecision.Approved,
            Conditions = conditions.ToList(),
            NegotiatedAt = now,
            ExpiresAt = now.Add(validity),
            LegalBasis = legalBasis
        };

        var key = BuildKey(sourceJurisdiction, destJurisdiction);
        _agreements.AddOrUpdate(key, agreement, (_, _) => agreement);
        _agreementsById.TryAdd(agreement.AgreementId, agreement);

        RecordAudit(agreement.AgreementId, "Created",
            $"Agreement created for {sourceJurisdiction} -> {destJurisdiction} with legal basis {legalBasis}");

        Interlocked.Increment(ref _agreementsCreated);

        return Task.FromResult(agreement);
    }

    /// <summary>
    /// Renews an existing agreement by extending its expiration date.
    /// </summary>
    /// <param name="agreementId">Identifier of the agreement to renew.</param>
    /// <param name="extension">Duration to extend the agreement validity by.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The renewed agreement record with updated expiration.</returns>
    /// <exception cref="InvalidOperationException">If no agreement with the specified ID exists.</exception>
    public Task<TransferAgreementRecord> RenewAgreementAsync(
        string agreementId,
        TimeSpan extension,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agreementId);
        ct.ThrowIfCancellationRequested();

        IncrementCounter("agreement_manager.renew");

        if (!_agreementsById.TryGetValue(agreementId, out var existing))
        {
            throw new InvalidOperationException($"Agreement '{agreementId}' not found");
        }

        var now = DateTimeOffset.UtcNow;
        var baseExpiry = existing.ExpiresAt.HasValue && existing.ExpiresAt.Value > now
            ? existing.ExpiresAt.Value
            : now;

        var renewed = existing with
        {
            ExpiresAt = baseExpiry.Add(extension),
            NegotiatedAt = now,
            Decision = TransferDecision.Approved
        };

        var key = BuildKey(existing.SourceJurisdiction, existing.DestinationJurisdiction);
        _agreements.AddOrUpdate(key, renewed, (_, _) => renewed);
        _agreementsById.TryUpdate(agreementId, renewed, existing);

        RecordAudit(agreementId, "Renewed",
            $"Agreement extended by {extension.TotalDays:F0} days; new expiry: {renewed.ExpiresAt:O}");

        Interlocked.Increment(ref _agreementsRenewed);

        return Task.FromResult(renewed);
    }

    /// <summary>
    /// Revokes an agreement, preventing any further transfers under it.
    /// </summary>
    /// <param name="agreementId">Identifier of the agreement to revoke.</param>
    /// <param name="reason">Reason for revocation.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">If no agreement with the specified ID exists.</exception>
    public Task RevokeAgreementAsync(
        string agreementId,
        string reason,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agreementId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ct.ThrowIfCancellationRequested();

        IncrementCounter("agreement_manager.revoke");

        if (!_agreementsById.TryGetValue(agreementId, out var existing))
        {
            throw new InvalidOperationException($"Agreement '{agreementId}' not found");
        }

        var revoked = existing with
        {
            Decision = TransferDecision.Denied,
            ExpiresAt = DateTimeOffset.UtcNow // Expire immediately
        };

        var key = BuildKey(existing.SourceJurisdiction, existing.DestinationJurisdiction);
        _agreements.AddOrUpdate(key, revoked, (_, _) => revoked);
        _agreementsById.TryUpdate(agreementId, revoked, existing);

        RecordAudit(agreementId, "Revoked", $"Revoked: {reason}");

        Interlocked.Increment(ref _agreementsRevoked);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Retrieves the active agreement for a jurisdiction pair, if one exists.
    /// </summary>
    /// <param name="sourceJurisdiction">Source jurisdiction ISO code.</param>
    /// <param name="destJurisdiction">Destination jurisdiction ISO code.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The agreement record, or <c>null</c> if no active agreement exists.</returns>
    public Task<TransferAgreementRecord?> GetAgreementAsync(
        string sourceJurisdiction,
        string destJurisdiction,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceJurisdiction);
        ArgumentException.ThrowIfNullOrWhiteSpace(destJurisdiction);
        ct.ThrowIfCancellationRequested();

        IncrementCounter("agreement_manager.get");

        var key = BuildKey(sourceJurisdiction, destJurisdiction);
        _agreements.TryGetValue(key, out var agreement);

        return Task.FromResult(agreement);
    }

    /// <summary>
    /// Retrieves all managed agreements.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>All agreement records including expired and revoked ones.</returns>
    public Task<IReadOnlyList<TransferAgreementRecord>> GetAllAgreementsAsync(
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        IncrementCounter("agreement_manager.get_all");

        IReadOnlyList<TransferAgreementRecord> result = _agreementsById.Values.ToList();
        return Task.FromResult(result);
    }

    /// <summary>
    /// Retrieves all agreements that have expired.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Agreement records whose expiration date has passed.</returns>
    public Task<IReadOnlyList<TransferAgreementRecord>> GetExpiredAgreementsAsync(
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        IncrementCounter("agreement_manager.get_expired");

        var now = DateTimeOffset.UtcNow;
        IReadOnlyList<TransferAgreementRecord> result = _agreementsById.Values
            .Where(a => a.ExpiresAt.HasValue && a.ExpiresAt.Value <= now)
            .ToList();

        // Track expired count
        var expiredCount = result.Count;
        if (expiredCount > 0)
        {
            Interlocked.Exchange(ref _agreementsExpired, expiredCount);
        }

        return Task.FromResult(result);
    }

    /// <summary>
    /// Validates whether an agreement is currently valid (not expired and not revoked).
    /// </summary>
    /// <param name="agreementId">Identifier of the agreement to validate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><c>true</c> if the agreement is valid; <c>false</c> if expired, revoked, or not found.</returns>
    public Task<bool> ValidateAgreementAsync(
        string agreementId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agreementId);
        ct.ThrowIfCancellationRequested();

        IncrementCounter("agreement_manager.validate");

        if (!_agreementsById.TryGetValue(agreementId, out var agreement))
        {
            return Task.FromResult(false);
        }

        // Check revocation
        if (agreement.Decision == TransferDecision.Denied)
        {
            return Task.FromResult(false);
        }

        // Check expiry
        if (agreement.ExpiresAt.HasValue && agreement.ExpiresAt.Value <= DateTimeOffset.UtcNow)
        {
            return Task.FromResult(false);
        }

        return Task.FromResult(true);
    }

    /// <summary>
    /// Gets agreement management statistics.
    /// </summary>
    public AgreementManagerStatistics GetAgreementStatistics()
    {
        return new AgreementManagerStatistics
        {
            AgreementsCreated = Interlocked.Read(ref _agreementsCreated),
            AgreementsRenewed = Interlocked.Read(ref _agreementsRenewed),
            AgreementsRevoked = Interlocked.Read(ref _agreementsRevoked),
            AgreementsExpired = Interlocked.Read(ref _agreementsExpired),
            TotalActiveAgreements = _agreementsById.Values
                .Count(a => a.Decision != TransferDecision.Denied
                         && (!a.ExpiresAt.HasValue || a.ExpiresAt.Value > DateTimeOffset.UtcNow))
        };
    }

    // ------------------------------------------------------------------
    // ComplianceStrategyBase override
    // ------------------------------------------------------------------

    /// <inheritdoc/>
    protected override Task<ComplianceResult> CheckComplianceCoreAsync(
        ComplianceContext context,
        CancellationToken cancellationToken)
    {
        IncrementCounter("agreement_manager.check");

        var source = context.SourceLocation ?? string.Empty;
        var dest = context.DestinationLocation ?? string.Empty;

        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(dest))
        {
            return Task.FromResult(new ComplianceResult
            {
                IsCompliant = true,
                Framework = Framework,
                Status = ComplianceStatus.NotApplicable,
                Recommendations = new[] { "No cross-border transfer context; source or destination not specified" }
            });
        }

        var key = BuildKey(source, dest);
        if (_agreements.TryGetValue(key, out var agreement))
        {
            // Check if agreement is valid
            bool isValid = agreement.Decision != TransferDecision.Denied
                        && (!agreement.ExpiresAt.HasValue || agreement.ExpiresAt.Value > DateTimeOffset.UtcNow);

            if (isValid)
            {
                return Task.FromResult(new ComplianceResult
                {
                    IsCompliant = true,
                    Framework = Framework,
                    Status = ComplianceStatus.Compliant,
                    Metadata = new Dictionary<string, object>
                    {
                        ["AgreementId"] = agreement.AgreementId,
                        ["LegalBasis"] = agreement.LegalBasis ?? "Unknown",
                        ["ExpiresAt"] = agreement.ExpiresAt?.ToString("O") ?? "Never"
                    }
                });
            }

            // Agreement exists but is invalid
            var isExpired = agreement.ExpiresAt.HasValue && agreement.ExpiresAt.Value <= DateTimeOffset.UtcNow;
            var reason = agreement.Decision == TransferDecision.Denied
                ? "Agreement has been revoked"
                : isExpired ? "Agreement has expired" : "Agreement is not in approved state";

            return Task.FromResult(new ComplianceResult
            {
                IsCompliant = false,
                Framework = Framework,
                Status = ComplianceStatus.NonCompliant,
                Violations = new[]
                {
                    new ComplianceViolation
                    {
                        Code = "AGREE-001",
                        Description = $"Transfer agreement for {source} -> {dest} is invalid: {reason}",
                        Severity = ViolationSeverity.High,
                        Remediation = isExpired
                            ? "Renew the agreement via RenewAgreementAsync"
                            : "Create a new agreement via CreateAgreementAsync"
                    }
                }
            });
        }

        return Task.FromResult(new ComplianceResult
        {
            IsCompliant = false,
            Framework = Framework,
            Status = ComplianceStatus.NonCompliant,
            Violations = new[]
            {
                new ComplianceViolation
                {
                    Code = "AGREE-002",
                    Description = $"No transfer agreement exists for {source} -> {dest}",
                    Severity = ViolationSeverity.High,
                    Remediation = "Create a transfer agreement via CreateAgreementAsync"
                }
            },
            Recommendations = new[] { "Establish a bilateral transfer agreement before proceeding" }
        });
    }

    // ------------------------------------------------------------------
    // Pre-configured agreements
    // ------------------------------------------------------------------

    private void SeedPreConfiguredAgreements()
    {
        var now = DateTimeOffset.UtcNow;

        // EU internal (auto-approved, 10-year validity)
        SeedAgreement("EU", "EU", "AdequacyDecision",
            Array.Empty<string>(), TimeSpan.FromDays(3650), now);

        // EU -> UK: Adequacy decision with UK GDPR compliance condition
        SeedAgreement("EU", "UK", "AdequacyDecision",
            new[] { "uk_gdpr_compliance" }, TimeSpan.FromDays(1825), now);

        // EU -> JP: Adequacy decision with APPI supplementary rules
        SeedAgreement("EU", "JP", "AdequacyDecision",
            new[] { "appi_supplementary_rules" }, TimeSpan.FromDays(1825), now);

        // EU -> KR: Adequacy decision with PIPA compliance
        SeedAgreement("EU", "KR", "AdequacyDecision",
            new[] { "pipa_compliance" }, TimeSpan.FromDays(1825), now);

        // EU -> CH: Adequacy decision with FADP compliance
        SeedAgreement("EU", "CH", "AdequacyDecision",
            new[] { "fadp_compliance" }, TimeSpan.FromDays(1825), now);

        // US internal (sectoral compliance)
        SeedAgreement("US", "US", "IntraRegional",
            new[] { "sector_specific_compliance" }, TimeSpan.FromDays(3650), now);

        // APEC CBPR (generic APEC-to-APEC agreement)
        SeedAgreement("APEC", "APEC", "CBPR",
            new[] { "cbpr_certification" }, TimeSpan.FromDays(1825), now);
    }

    private void SeedAgreement(
        string source,
        string dest,
        string legalBasis,
        IReadOnlyList<string> conditions,
        TimeSpan validity,
        DateTimeOffset now)
    {
        var agreement = new TransferAgreementRecord
        {
            AgreementId = $"pre-{source.ToLowerInvariant()}-{dest.ToLowerInvariant()}-{legalBasis.ToLowerInvariant()}",
            SourceJurisdiction = source,
            DestinationJurisdiction = dest,
            Decision = TransferDecision.Approved,
            Conditions = conditions.ToList(),
            NegotiatedAt = now,
            ExpiresAt = now.Add(validity),
            LegalBasis = legalBasis
        };

        var key = BuildKey(source, dest);
        _agreements.TryAdd(key, agreement);
        _agreementsById.TryAdd(agreement.AgreementId, agreement);

        RecordAudit(agreement.AgreementId, "Seeded",
            $"Pre-configured agreement for {source} -> {dest} with legal basis {legalBasis}");

        Interlocked.Increment(ref _agreementsCreated);
    }

    // ------------------------------------------------------------------
    // Audit tracking
    // ------------------------------------------------------------------

    private void RecordAudit(string agreementId, string action, string details)
    {
        var entry = new AgreementAuditEntry
        {
            AuditId = Guid.NewGuid().ToString("N"),
            AgreementId = agreementId,
            Action = action,
            Details = details,
            Timestamp = DateTimeOffset.UtcNow
        };

        _auditLog.AddOrUpdate(
            agreementId,
            _ => new List<AgreementAuditEntry> { entry },
            (_, existing) =>
            {
                lock (_auditLock)
                {
                    existing.Add(entry);
                    return existing;
                }
            });
    }

    /// <summary>
    /// Retrieves the audit trail for a specific agreement.
    /// </summary>
    /// <param name="agreementId">Agreement identifier.</param>
    /// <returns>Audit entries ordered by timestamp descending.</returns>
    public IReadOnlyList<AgreementAuditEntry> GetAuditTrail(string agreementId)
    {
        if (_auditLog.TryGetValue(agreementId, out var entries))
        {
            lock (_auditLock)
            {
                return entries.OrderByDescending(e => e.Timestamp).ToList();
            }
        }

        return Array.Empty<AgreementAuditEntry>();
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static string BuildKey(string source, string dest) =>
        $"{source.ToUpperInvariant()}:{dest.ToUpperInvariant()}";

    // ------------------------------------------------------------------
    // Internal records
    // ------------------------------------------------------------------

    /// <summary>
    /// Records a mutation to a transfer agreement for audit purposes.
    /// </summary>
    public sealed record AgreementAuditEntry
    {
        /// <summary>Unique identifier for this audit record.</summary>
        public required string AuditId { get; init; }

        /// <summary>Agreement this audit entry refers to.</summary>
        public required string AgreementId { get; init; }

        /// <summary>Action performed (Created, Renewed, Revoked, Seeded).</summary>
        public required string Action { get; init; }

        /// <summary>Human-readable description of the change.</summary>
        public required string Details { get; init; }

        /// <summary>When the action was performed.</summary>
        public required DateTimeOffset Timestamp { get; init; }
    }

    /// <summary>
    /// Statistics for the agreement manager.
    /// </summary>
    public sealed class AgreementManagerStatistics
    {
        /// <summary>Total agreements created (including pre-configured).</summary>
        public long AgreementsCreated { get; init; }

        /// <summary>Total agreements renewed.</summary>
        public long AgreementsRenewed { get; init; }

        /// <summary>Total agreements revoked.</summary>
        public long AgreementsRevoked { get; init; }

        /// <summary>Total agreements that have expired.</summary>
        public long AgreementsExpired { get; init; }

        /// <summary>Currently active (non-expired, non-revoked) agreement count.</summary>
        public int TotalActiveAgreements { get; init; }
    }
}
