using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Compliance;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.Passport;

/// <summary>
/// Implements the <see cref="ICrossBorderProtocol"/> for negotiating, evaluating, and logging
/// cross-border data transfers with legal-basis-aware agreement negotiation and full provenance.
/// <para>
/// For each jurisdiction pair the protocol determines the most appropriate legal basis
/// (Adequacy Decision, SCC, BCR, CBPR, Intra-Regional, or Derogation), evaluates the
/// <see cref="CompliancePassport"/> coverage, and produces a <see cref="TransferAgreementRecord"/>
/// with conditions. All transfers are logged with complete audit provenance.
/// </para>
/// </summary>
public sealed class CrossBorderTransferProtocolStrategy : ComplianceStrategyBase, ICrossBorderProtocol
{
    private readonly ConcurrentDictionary<string, TransferAgreementRecord> _agreements = new();
    private readonly ConcurrentDictionary<string, List<CrossBorderTransferLog>> _transferLogs = new();
    private readonly ConcurrentDictionary<string, TransferNegotiation> _pendingNegotiations = new();
    private readonly object _logLock = new();

    /// <inheritdoc/>
    public override string StrategyId => "cross-border-transfer-protocol";

    /// <inheritdoc/>
    public override string StrategyName => "Cross-Border Transfer Protocol";

    /// <inheritdoc/>
    public override string Framework => "CompliancePassport";

    // ------------------------------------------------------------------
    // ICrossBorderProtocol implementation
    // ------------------------------------------------------------------

    /// <inheritdoc/>
    public Task<TransferAgreementRecord> NegotiateTransferAsync(
        string sourceJurisdiction,
        string destJurisdiction,
        CompliancePassport passport,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceJurisdiction);
        ArgumentException.ThrowIfNullOrWhiteSpace(destJurisdiction);
        ArgumentNullException.ThrowIfNull(passport);
        ct.ThrowIfCancellationRequested();

        IncrementCounter("cross_border.negotiate");

        var key = BuildAgreementKey(sourceJurisdiction, destJurisdiction);

        // 1. Check for an existing active agreement
        if (_agreements.TryGetValue(key, out var existing) && IsAgreementActive(existing))
        {
            return Task.FromResult(existing);
        }

        // 2. Create a negotiation record
        var negotiation = new TransferNegotiation
        {
            NegotiationId = Guid.NewGuid().ToString("N"),
            SourceJurisdiction = sourceJurisdiction,
            DestinationJurisdiction = destJurisdiction,
            RequestedAt = DateTimeOffset.UtcNow,
            Status = NegotiationStatus.Pending,
            LegalBasisOptions = DetermineLegalBasisOptions(sourceJurisdiction, destJurisdiction)
        };

        _pendingNegotiations.TryAdd(negotiation.NegotiationId, negotiation);

        // 3. Select the best legal basis
        var legalBasis = SelectBestLegalBasis(negotiation.LegalBasisOptions);

        // 4. Determine conditions based on passport coverage and jurisdiction requirements
        var conditions = DetermineConditions(sourceJurisdiction, destJurisdiction, passport, legalBasis);

        // 5. Determine the decision based on passport validity
        var decision = DetermineDecision(passport, sourceJurisdiction, legalBasis);

        // 6. Create the agreement
        var now = DateTimeOffset.UtcNow;
        var agreement = new TransferAgreementRecord
        {
            AgreementId = Guid.NewGuid().ToString("N"),
            SourceJurisdiction = sourceJurisdiction,
            DestinationJurisdiction = destJurisdiction,
            Decision = decision,
            Conditions = conditions,
            NegotiatedAt = now,
            ExpiresAt = now.Add(GetAgreementValidity(legalBasis)),
            LegalBasis = legalBasis
        };

        // 7. Store the agreement
        _agreements.AddOrUpdate(key, agreement, (_, _) => agreement);

        // 8. Mark negotiation complete
        negotiation = negotiation with { Status = NegotiationStatus.Complete };
        _pendingNegotiations.TryUpdate(negotiation.NegotiationId, negotiation,
            _pendingNegotiations.GetValueOrDefault(negotiation.NegotiationId)!);

        IncrementCounter("cross_border.agreement_created");

        return Task.FromResult(agreement);
    }

    /// <inheritdoc/>
    public Task<TransferDecision> EvaluateTransferAsync(string transferId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transferId);
        ct.ThrowIfCancellationRequested();

        IncrementCounter("cross_border.evaluate");

        // Search for an agreement matching this transfer by looking up logs
        foreach (var logs in _transferLogs.Values)
        {
            lock (_logLock)
            {
                var log = logs.FirstOrDefault(l => l.TransferId == transferId);
                if (log is not null)
                {
                    // Found the transfer; check its agreement
                    if (log.AgreementId is not null)
                    {
                        var key = BuildAgreementKey(log.SourceJurisdiction, log.DestinationJurisdiction);
                        if (_agreements.TryGetValue(key, out var agreement))
                        {
                            if (!IsAgreementActive(agreement))
                            {
                                return Task.FromResult(TransferDecision.Denied);
                            }

                            return Task.FromResult(agreement.Decision);
                        }
                    }

                    return Task.FromResult(TransferDecision.Denied);
                }
            }
        }

        // Check pending negotiations
        foreach (var negotiation in _pendingNegotiations.Values)
        {
            if (negotiation.NegotiationId == transferId && negotiation.Status == NegotiationStatus.Pending)
            {
                return Task.FromResult(TransferDecision.PendingAgreement);
            }
        }

        // No matching transfer or negotiation found
        return Task.FromResult(TransferDecision.Denied);
    }

    /// <inheritdoc/>
    public Task LogTransferAsync(CrossBorderTransferLog log, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(log);
        ct.ThrowIfCancellationRequested();

        IncrementCounter("cross_border.log");

        _transferLogs.AddOrUpdate(
            log.ObjectId,
            _ => new List<CrossBorderTransferLog> { log },
            (_, existing) =>
            {
                lock (_logLock)
                {
                    existing.Add(log);
                    return existing;
                }
            });

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<CrossBorderTransferLog>> GetTransferHistoryAsync(
        string objectId, int limit, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        ct.ThrowIfCancellationRequested();

        IncrementCounter("cross_border.history");

        if (_transferLogs.TryGetValue(objectId, out var logs))
        {
            lock (_logLock)
            {
                IReadOnlyList<CrossBorderTransferLog> result = logs
                    .OrderByDescending(l => l.Timestamp)
                    .Take(limit > 0 ? limit : int.MaxValue)
                    .ToList();
                return Task.FromResult(result);
            }
        }

        return Task.FromResult<IReadOnlyList<CrossBorderTransferLog>>(Array.Empty<CrossBorderTransferLog>());
    }

    // ------------------------------------------------------------------
    // ComplianceStrategyBase override
    // ------------------------------------------------------------------

    /// <inheritdoc/>
    protected override Task<ComplianceResult> CheckComplianceCoreAsync(
        ComplianceContext context,
        CancellationToken cancellationToken)
    {
        IncrementCounter("cross_border.check");

        var source = context.SourceLocation ?? string.Empty;
        var dest = context.DestinationLocation ?? string.Empty;

        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(dest))
        {
            return Task.FromResult(new ComplianceResult
            {
                IsCompliant = true,
                Framework = Framework,
                Status = ComplianceStatus.NotApplicable,
                Recommendations = new[] { "No cross-border transfer detected; source or destination not specified" }
            });
        }

        // Same jurisdiction always compliant for cross-border purposes
        if (string.Equals(source, dest, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(new ComplianceResult
            {
                IsCompliant = true,
                Framework = Framework,
                Status = ComplianceStatus.Compliant,
                Metadata = new Dictionary<string, object>
                {
                    ["Note"] = "Same-jurisdiction transfer"
                }
            });
        }

        var key = BuildAgreementKey(source, dest);
        if (_agreements.TryGetValue(key, out var agreement) && IsAgreementActive(agreement))
        {
            var isApproved = agreement.Decision == TransferDecision.Approved
                          || agreement.Decision == TransferDecision.ConditionalApproval;

            return Task.FromResult(new ComplianceResult
            {
                IsCompliant = isApproved,
                Framework = Framework,
                Status = isApproved ? ComplianceStatus.Compliant : ComplianceStatus.NonCompliant,
                Metadata = new Dictionary<string, object>
                {
                    ["AgreementId"] = agreement.AgreementId,
                    ["LegalBasis"] = agreement.LegalBasis ?? "Unknown",
                    ["Decision"] = agreement.Decision.ToString()
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
                    Code = "XBORDER-001",
                    Description = $"No valid transfer agreement exists for {source} -> {dest}",
                    Severity = ViolationSeverity.High,
                    Remediation = "Negotiate a transfer agreement via NegotiateTransferAsync before transferring data"
                }
            },
            Recommendations = new[] { "Negotiate a cross-border transfer agreement before proceeding" }
        });
    }

    // ------------------------------------------------------------------
    // Legal basis determination
    // ------------------------------------------------------------------

    private static IReadOnlyList<string> DetermineLegalBasisOptions(string source, string dest)
    {
        var options = new List<string>();

        var sourceNorm = source.ToUpperInvariant();
        var destNorm = dest.ToUpperInvariant();

        bool sourceIsEu = IsEuMember(sourceNorm);
        bool destIsEu = IsEuMember(destNorm);
        bool destIsAdequate = AdequacyDecisionRegistry.IsAdequate(sourceNorm, destNorm);
        bool bothApec = IsApecCbprMember(sourceNorm) && IsApecCbprMember(destNorm);
        bool sameRegion = AreSameRegion(sourceNorm, destNorm);

        // EU -> EU: Intra-regional
        if (sourceIsEu && destIsEu)
        {
            options.Add("IntraRegional");
        }

        // EU -> Adequate country
        if (sourceIsEu && destIsAdequate)
        {
            options.Add("AdequacyDecision");
        }

        // EU -> Non-adequate: SCC
        if (sourceIsEu && !destIsEu && !destIsAdequate)
        {
            options.Add("SCC");
        }

        // BCR is always an option for multinational organizations
        options.Add("BCR");

        // APEC CBPR members
        if (bothApec)
        {
            options.Add("CBPR");
        }

        // Same region (non-EU)
        if (!sourceIsEu && !destIsEu && sameRegion)
        {
            options.Add("IntraRegional");
        }

        // Derogation is always a fallback
        if (options.Count == 0 || (!options.Contains("AdequacyDecision") && !options.Contains("IntraRegional")))
        {
            options.Add("Derogation");
        }

        return options.Distinct().ToList();
    }

    private static string SelectBestLegalBasis(IReadOnlyList<string> options)
    {
        // Priority order: IntraRegional > AdequacyDecision > CBPR > BCR > SCC > Derogation
        var priority = new[]
        {
            "IntraRegional", "AdequacyDecision", "CBPR", "BCR", "SCC", "Derogation"
        };

        foreach (var basis in priority)
        {
            if (options.Contains(basis))
                return basis;
        }

        return "Derogation";
    }

    private static List<string> DetermineConditions(
        string source,
        string dest,
        CompliancePassport passport,
        string legalBasis)
    {
        var conditions = new List<string>();

        var sourceNorm = source.ToUpperInvariant();
        var destNorm = dest.ToUpperInvariant();

        // Always require audit logging
        conditions.Add("audit_logging");

        // Check passport coverage to determine if fewer conditions are needed
        bool passportCoversSource = DoesPassportCoverJurisdiction(passport, sourceNorm);

        // Encryption required if either jurisdiction mandates it
        if (RequiresEncryption(sourceNorm) || RequiresEncryption(destNorm))
        {
            conditions.Add("encryption_required");
        }

        // GDPR/PIPL transfers require data minimization
        if (IsGdprJurisdiction(sourceNorm) || IsGdprJurisdiction(destNorm)
            || IsPiplJurisdiction(sourceNorm) || IsPiplJurisdiction(destNorm))
        {
            conditions.Add("data_minimization");
        }

        // GDPR transfers require purpose limitation
        if (IsGdprJurisdiction(sourceNorm) || IsGdprJurisdiction(destNorm))
        {
            conditions.Add("purpose_limitation");
        }

        // SCC-specific conditions
        if (legalBasis == "SCC")
        {
            conditions.Add("scc_signed");
            conditions.Add("impact_assessment_completed");
        }

        // BCR-specific conditions
        if (legalBasis == "BCR")
        {
            conditions.Add("bcr_approved");
        }

        // CBPR-specific conditions
        if (legalBasis == "CBPR")
        {
            conditions.Add("cbpr_certification");
        }

        // If passport covers all applicable regulations, reduce conditions
        if (passportCoversSource && passport.IsValid())
        {
            conditions.Remove("impact_assessment_completed");
        }

        return conditions.Distinct().ToList();
    }

    private static TransferDecision DetermineDecision(
        CompliancePassport passport,
        string sourceJurisdiction,
        string legalBasis)
    {
        if (!passport.IsValid())
        {
            return TransferDecision.EscalationRequired;
        }

        var sourceNorm = sourceJurisdiction.ToUpperInvariant();

        // Intra-regional and adequacy decisions with valid passport are approved
        if (legalBasis is "IntraRegional" or "AdequacyDecision")
        {
            return DoesPassportCoverJurisdiction(passport, sourceNorm)
                ? TransferDecision.Approved
                : TransferDecision.ConditionalApproval;
        }

        // SCC and BCR require conditional approval (conditions must be verified)
        if (legalBasis is "SCC" or "BCR")
        {
            return TransferDecision.ConditionalApproval;
        }

        // CBPR with valid passport
        if (legalBasis == "CBPR")
        {
            return TransferDecision.ConditionalApproval;
        }

        // Derogation always requires escalation
        return TransferDecision.EscalationRequired;
    }

    // ------------------------------------------------------------------
    // Adequacy Decision Registry
    // ------------------------------------------------------------------

    /// <summary>
    /// Registry of EU adequacy decisions. Countries recognized by the European Commission
    /// as providing an adequate level of data protection.
    /// </summary>
    private static class AdequacyDecisionRegistry
    {
        /// <summary>
        /// EU-adequate countries as of current regulatory landscape.
        /// JP=Japan, KR=South Korea, NZ=New Zealand, UK=United Kingdom, CH=Switzerland,
        /// IL=Israel, AR=Argentina, UY=Uruguay, CA=Canada (commercial),
        /// AD=Andorra, FO=Faroe Islands, GG=Guernsey, IM=Isle of Man, JE=Jersey.
        /// </summary>
        private static readonly HashSet<string> AdequateCountries = new(StringComparer.OrdinalIgnoreCase)
        {
            "JP", "KR", "NZ", "UK", "GB", "CH", "IL", "AR", "UY", "CA",
            "AD", "FO", "GG", "IM", "JE"
        };

        /// <summary>
        /// Determines whether a transfer from one jurisdiction to another is covered
        /// by an EU adequacy decision.
        /// </summary>
        /// <param name="fromJurisdiction">Source jurisdiction ISO code.</param>
        /// <param name="toJurisdiction">Destination jurisdiction ISO code.</param>
        /// <returns><c>true</c> if the destination is recognized as adequate by the source.</returns>
        public static bool IsAdequate(string fromJurisdiction, string toJurisdiction)
        {
            // Adequacy decisions are EU-centric: the source must be EU/EEA
            if (!IsEuMember(fromJurisdiction))
                return false;

            return AdequateCountries.Contains(toJurisdiction);
        }
    }

    // ------------------------------------------------------------------
    // Jurisdiction helpers
    // ------------------------------------------------------------------

    private static readonly HashSet<string> EuMembers = new(StringComparer.OrdinalIgnoreCase)
    {
        "AT", "BE", "BG", "HR", "CY", "CZ", "DK", "EE", "FI", "FR",
        "DE", "GR", "HU", "IE", "IT", "LV", "LT", "LU", "MT", "NL",
        "PL", "PT", "RO", "SK", "SI", "ES", "SE",
        // EEA members
        "IS", "LI", "NO"
    };

    private static readonly HashSet<string> ApecCbprMembers = new(StringComparer.OrdinalIgnoreCase)
    {
        "US", "JP", "KR", "AU", "NZ", "CA", "SG", "MX", "TW", "PH"
    };

    private static bool IsEuMember(string jurisdiction) =>
        EuMembers.Contains(jurisdiction);

    private static bool IsApecCbprMember(string jurisdiction) =>
        ApecCbprMembers.Contains(jurisdiction);

    private static bool AreSameRegion(string source, string dest)
    {
        // Simple region groupings for non-EU jurisdictions
        var region1 = GetRegion(source);
        var region2 = GetRegion(dest);
        return region1 is not null && string.Equals(region1, region2, StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetRegion(string jurisdiction) => jurisdiction switch
    {
        "US" or "CA" or "MX" => "NorthAmerica",
        "BR" or "AR" or "CL" or "CO" or "PE" or "UY" => "SouthAmerica",
        "CN" or "JP" or "KR" or "TW" or "HK" or "MO" => "EastAsia",
        "IN" or "PK" or "BD" or "LK" => "SouthAsia",
        "SG" or "MY" or "TH" or "ID" or "PH" or "VN" => "SoutheastAsia",
        "AU" or "NZ" => "Oceania",
        "AE" or "SA" or "QA" or "BH" or "KW" or "OM" => "MiddleEast",
        "ZA" or "NG" or "KE" or "GH" or "EG" => "Africa",
        "RU" or "BY" or "KZ" or "UZ" => "CIS",
        _ => null
    };

    private static bool RequiresEncryption(string jurisdiction)
    {
        // Jurisdictions that mandate encryption for cross-border transfers
        return jurisdiction is "CN" or "RU" or "IN" or "BR"
            || IsEuMember(jurisdiction)
            || jurisdiction is "KR" or "JP";
    }

    private static bool IsGdprJurisdiction(string jurisdiction) =>
        IsEuMember(jurisdiction) || jurisdiction is "UK" or "GB";

    private static bool IsPiplJurisdiction(string jurisdiction) =>
        jurisdiction is "CN";

    private static bool DoesPassportCoverJurisdiction(CompliancePassport passport, string jurisdiction)
    {
        if (IsGdprJurisdiction(jurisdiction))
            return passport.CoversRegulation("GDPR");
        if (jurisdiction is "US")
            return passport.CoversRegulation("CCPA") || passport.CoversRegulation("HIPAA");
        if (jurisdiction is "CN")
            return passport.CoversRegulation("PIPL");
        if (jurisdiction is "BR")
            return passport.CoversRegulation("LGPD");
        if (jurisdiction is "JP")
            return passport.CoversRegulation("APPI");
        if (jurisdiction is "KR")
            return passport.CoversRegulation("PIPA");

        // Generic coverage: any regulation is sufficient
        return passport.Entries.Count > 0;
    }

    private static bool IsAgreementActive(TransferAgreementRecord agreement)
    {
        if (agreement.Decision == TransferDecision.Denied)
            return false;

        return !agreement.ExpiresAt.HasValue || agreement.ExpiresAt.Value > DateTimeOffset.UtcNow;
    }

    private static string BuildAgreementKey(string source, string dest) =>
        $"{source.ToUpperInvariant()}:{dest.ToUpperInvariant()}";

    private static TimeSpan GetAgreementValidity(string legalBasis) => legalBasis switch
    {
        "AdequacyDecision" => TimeSpan.FromDays(1825), // 5 years
        "IntraRegional" => TimeSpan.FromDays(3650),     // 10 years
        "SCC" => TimeSpan.FromDays(730),                // 2 years
        "BCR" => TimeSpan.FromDays(1095),               // 3 years
        "CBPR" => TimeSpan.FromDays(730),               // 2 years
        "Derogation" => TimeSpan.FromDays(365),         // 1 year
        _ => TimeSpan.FromDays(365)
    };

    // ------------------------------------------------------------------
    // Internal records
    // ------------------------------------------------------------------

    /// <summary>
    /// Tracks the state of an in-progress transfer agreement negotiation.
    /// </summary>
    internal sealed record TransferNegotiation
    {
        /// <summary>Unique identifier for this negotiation.</summary>
        public required string NegotiationId { get; init; }

        /// <summary>Source jurisdiction ISO code.</summary>
        public required string SourceJurisdiction { get; init; }

        /// <summary>Destination jurisdiction ISO code.</summary>
        public required string DestinationJurisdiction { get; init; }

        /// <summary>When the negotiation was requested.</summary>
        public required DateTimeOffset RequestedAt { get; init; }

        /// <summary>Current status of the negotiation.</summary>
        public required NegotiationStatus Status { get; init; }

        /// <summary>Legal basis options determined for this jurisdiction pair.</summary>
        public required IReadOnlyList<string> LegalBasisOptions { get; init; }
    }

    /// <summary>
    /// Status of a transfer negotiation.
    /// </summary>
    internal enum NegotiationStatus
    {
        Pending,
        Complete,
        Failed
    }
}
