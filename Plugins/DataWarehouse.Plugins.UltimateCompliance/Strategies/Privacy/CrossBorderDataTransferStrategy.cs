using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.Privacy
{
    /// <summary>
    /// T124.8: Cross-Border Data Transfer Strategy
    /// Manages lawful international data transfers under GDPR Chapter V and other regulations.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Transfer Mechanisms:
    /// - Adequacy Decisions (GDPR Article 45)
    /// - Standard Contractual Clauses (GDPR Article 46)
    /// - Binding Corporate Rules (GDPR Article 47)
    /// - Data Privacy Framework (EU-US DPF)
    /// - Derogations (GDPR Article 49)
    /// </para>
    /// <para>
    /// Transfer Impact Assessment:
    /// - Legal framework analysis of destination country
    /// - Assessment of access by public authorities
    /// - Evaluation of supplementary measures needed
    /// - Documentation of transfer justification
    /// </para>
    /// </remarks>
    public sealed class CrossBorderDataTransferStrategy : ComplianceStrategyBase
    {
        private readonly BoundedDictionary<string, TransferMechanism> _mechanisms = new BoundedDictionary<string, TransferMechanism>(1000);
        private readonly BoundedDictionary<string, CountryAssessment> _countryAssessments = new BoundedDictionary<string, CountryAssessment>(1000);
        private readonly BoundedDictionary<string, DataTransferRecord> _transfers = new BoundedDictionary<string, DataTransferRecord>(1000);
        private readonly BoundedDictionary<string, TransferImpactAssessment> _tias = new BoundedDictionary<string, TransferImpactAssessment>(1000);
        private readonly ConcurrentBag<TransferAuditEntry> _auditLog = new();

        private bool _requireTia = true;
        private bool _blockUnauthorizedTransfers = true;

        /// <inheritdoc/>
        public override string StrategyId => "cross-border-data-transfer";

        /// <inheritdoc/>
        public override string StrategyName => "Cross-Border Data Transfer";

        /// <inheritdoc/>
        public override string Framework => "DataPrivacy";

        /// <inheritdoc/>
        public override Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken cancellationToken = default)
        {
            if (configuration.TryGetValue("RequireTia", out var tiaObj) && tiaObj is bool tia)
                _requireTia = tia;

            if (configuration.TryGetValue("BlockUnauthorizedTransfers", out var blockObj) && blockObj is bool block)
                _blockUnauthorizedTransfers = block;

            InitializeDefaultMechanisms();
            InitializeCountryAssessments();
            return base.InitializeAsync(configuration, cancellationToken);
        }

        /// <summary>
        /// Checks if a data transfer is permitted.
        /// </summary>
        public TransferCheckResult CheckTransfer(TransferCheckInput input)
        {
            ArgumentNullException.ThrowIfNull(input);

            var checks = new List<TransferCheckDetail>();
            var blockers = new List<string>();
            var warnings = new List<string>();

            // Check if source and destination are the same
            if (input.SourceCountry.Equals(input.DestinationCountry, StringComparison.OrdinalIgnoreCase))
            {
                return new TransferCheckResult
                {
                    IsPermitted = true,
                    Reason = "No cross-border transfer (same country)",
                    CheckDetails = checks
                };
            }

            // Check if both are in EEA
            if (IsEeaCountry(input.SourceCountry) && IsEeaCountry(input.DestinationCountry))
            {
                return new TransferCheckResult
                {
                    IsPermitted = true,
                    Reason = "Intra-EEA transfer (no restrictions)",
                    CheckDetails = checks
                };
            }

            // Check adequacy decision
            var adequacyCheck = CheckAdequacy(input.DestinationCountry);
            checks.Add(adequacyCheck);
            if (adequacyCheck.Passed)
            {
                return new TransferCheckResult
                {
                    IsPermitted = true,
                    MechanismUsed = "Adequacy Decision",
                    Reason = adequacyCheck.Details,
                    CheckDetails = checks
                };
            }

            // Check for approved transfer mechanism
            var mechanism = FindApplicableMechanism(input);
            if (mechanism != null)
            {
                checks.Add(new TransferCheckDetail
                {
                    CheckName = "Transfer Mechanism",
                    Passed = true,
                    Details = $"Using mechanism: {mechanism.Name}"
                });

                // Check if TIA is required
                if (_requireTia && mechanism.RequiresTia)
                {
                    var tia = _tias.Values.FirstOrDefault(t =>
                        t.SourceCountry == input.SourceCountry &&
                        t.DestinationCountry == input.DestinationCountry &&
                        t.Status == TiaStatus.Approved &&
                        (!t.ExpiresAt.HasValue || t.ExpiresAt.Value > DateTime.UtcNow));

                    if (tia == null)
                    {
                        blockers.Add("Transfer Impact Assessment required but not found or expired");
                    }
                    else
                    {
                        checks.Add(new TransferCheckDetail
                        {
                            CheckName = "Transfer Impact Assessment",
                            Passed = true,
                            Details = $"TIA approved on {tia.ApprovedAt:d}"
                        });
                    }
                }

                // Check supplementary measures if required
                if (mechanism.RequiresSupplementaryMeasures)
                {
                    var hasSupplementary = input.SupplementaryMeasures?.Length > 0;
                    checks.Add(new TransferCheckDetail
                    {
                        CheckName = "Supplementary Measures",
                        Passed = hasSupplementary,
                        Details = hasSupplementary
                            ? $"Measures in place: {string.Join(", ", input.SupplementaryMeasures!)}"
                            : "Supplementary measures required"
                    });

                    if (!hasSupplementary)
                    {
                        warnings.Add("Supplementary measures may be required based on destination country assessment");
                    }
                }

                var isPermitted = blockers.Count == 0;
                return new TransferCheckResult
                {
                    IsPermitted = isPermitted,
                    MechanismUsed = mechanism.MechanismId,
                    Reason = isPermitted
                        ? $"Transfer permitted using {mechanism.Name}"
                        : $"Transfer blocked: {string.Join("; ", blockers)}",
                    CheckDetails = checks,
                    Warnings = warnings,
                    Blockers = blockers
                };
            }

            // Check for derogation
            var derogationCheck = CheckDerogation(input);
            checks.Add(derogationCheck);
            if (derogationCheck.Passed)
            {
                return new TransferCheckResult
                {
                    IsPermitted = true,
                    MechanismUsed = "Derogation",
                    Reason = derogationCheck.Details,
                    CheckDetails = checks,
                    Warnings = new[] { "Derogation should only be used occasionally and not for systematic transfers" }
                };
            }

            // No valid mechanism found
            return new TransferCheckResult
            {
                IsPermitted = !_blockUnauthorizedTransfers,
                Reason = "No valid transfer mechanism found",
                CheckDetails = checks,
                Blockers = _blockUnauthorizedTransfers
                    ? new[] { "No adequacy decision, approved mechanism, or applicable derogation" }
                    : Array.Empty<string>()
            };
        }

        /// <summary>
        /// Records a data transfer.
        /// </summary>
        public RecordTransferResult RecordTransfer(RecordTransferInput input)
        {
            ArgumentNullException.ThrowIfNull(input);

            // Check if transfer is permitted
            var checkResult = CheckTransfer(new TransferCheckInput
            {
                SourceCountry = input.SourceCountry,
                DestinationCountry = input.DestinationCountry,
                DataCategories = input.DataCategories,
                Purpose = input.Purpose
            });

            if (!checkResult.IsPermitted && _blockUnauthorizedTransfers)
            {
                return new RecordTransferResult
                {
                    Success = false,
                    ErrorMessage = checkResult.Reason,
                    Blockers = checkResult.Blockers
                };
            }

            var transferId = Guid.NewGuid().ToString();
            var record = new DataTransferRecord
            {
                TransferId = transferId,
                SourceCountry = input.SourceCountry,
                DestinationCountry = input.DestinationCountry,
                DataCategories = input.DataCategories ?? Array.Empty<string>(),
                Purpose = input.Purpose,
                RecipientName = input.RecipientName,
                RecipientType = input.RecipientType,
                MechanismUsed = checkResult.MechanismUsed,
                TransferredAt = DateTime.UtcNow,
                RecordedBy = input.RecordedBy,
                DataVolume = input.DataVolume,
                IsRecurring = input.IsRecurring,
                Warnings = checkResult.Warnings?.ToArray()
            };

            _transfers[transferId] = record;

            _auditLog.Add(new TransferAuditEntry
            {
                EntryId = Guid.NewGuid().ToString(),
                TransferId = transferId,
                Action = TransferAction.TransferRecorded,
                Timestamp = DateTime.UtcNow,
                PerformedBy = input.RecordedBy,
                Details = $"Transfer to {input.DestinationCountry} using {checkResult.MechanismUsed ?? "unknown"} mechanism"
            });

            return new RecordTransferResult
            {
                Success = true,
                TransferId = transferId,
                MechanismUsed = checkResult.MechanismUsed,
                Warnings = checkResult.Warnings
            };
        }

        /// <summary>
        /// Creates a Transfer Impact Assessment.
        /// </summary>
        public CreateTiaResult CreateTia(CreateTiaInput input)
        {
            ArgumentNullException.ThrowIfNull(input);

            var tiaId = Guid.NewGuid().ToString();

            // Get country assessment
            var countryAssessment = _countryAssessments.TryGetValue(input.DestinationCountry, out var ca)
                ? ca : null;

            var tia = new TransferImpactAssessment
            {
                TiaId = tiaId,
                Name = input.Name,
                SourceCountry = input.SourceCountry,
                DestinationCountry = input.DestinationCountry,
                DataCategories = input.DataCategories ?? Array.Empty<string>(),
                TransferPurpose = input.TransferPurpose,
                RecipientType = input.RecipientType,
                Status = TiaStatus.Draft,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = input.CreatedBy,
                CountryRiskLevel = countryAssessment?.RiskLevel ?? CountryRiskLevel.Unknown,
                LegalFrameworkAssessment = input.LegalFrameworkAssessment,
                PublicAuthorityAccessRisk = input.PublicAuthorityAccessRisk,
                SupplementaryMeasures = input.SupplementaryMeasures ?? Array.Empty<SupplementaryMeasure>()
            };

            _tias[tiaId] = tia;

            _auditLog.Add(new TransferAuditEntry
            {
                EntryId = Guid.NewGuid().ToString(),
                TiaId = tiaId,
                Action = TransferAction.TiaCreated,
                Timestamp = DateTime.UtcNow,
                PerformedBy = input.CreatedBy
            });

            return new CreateTiaResult
            {
                Success = true,
                TiaId = tiaId,
                Status = TiaStatus.Draft
            };
        }

        /// <summary>
        /// Approves a Transfer Impact Assessment.
        /// </summary>
        public ApproveTiaResult ApproveTia(string tiaId, string approvedBy, DateTime? expiresAt = null)
        {
            if (!_tias.TryGetValue(tiaId, out var tia))
            {
                return new ApproveTiaResult
                {
                    Success = false,
                    ErrorMessage = "TIA not found"
                };
            }

            if (tia.Status != TiaStatus.Draft && tia.Status != TiaStatus.UnderReview)
            {
                return new ApproveTiaResult
                {
                    Success = false,
                    ErrorMessage = $"Cannot approve TIA in {tia.Status} status"
                };
            }

            _tias[tiaId] = tia with
            {
                Status = TiaStatus.Approved,
                ApprovedAt = DateTime.UtcNow,
                ApprovedBy = approvedBy,
                ExpiresAt = expiresAt ?? DateTime.UtcNow.AddYears(1)
            };

            _auditLog.Add(new TransferAuditEntry
            {
                EntryId = Guid.NewGuid().ToString(),
                TiaId = tiaId,
                Action = TransferAction.TiaApproved,
                Timestamp = DateTime.UtcNow,
                PerformedBy = approvedBy
            });

            return new ApproveTiaResult
            {
                Success = true,
                TiaId = tiaId,
                ExpiresAt = expiresAt ?? DateTime.UtcNow.AddYears(1)
            };
        }

        /// <summary>
        /// Registers a transfer mechanism.
        /// </summary>
        public void RegisterMechanism(TransferMechanism mechanism)
        {
            ArgumentNullException.ThrowIfNull(mechanism);
            _mechanisms[mechanism.MechanismId] = mechanism;
        }

        /// <summary>
        /// Gets all transfer records.
        /// </summary>
        public IReadOnlyList<DataTransferRecord> GetTransfers(string? destinationCountry = null)
        {
            var query = _transfers.Values.AsEnumerable();
            if (!string.IsNullOrEmpty(destinationCountry))
                query = query.Where(t => t.DestinationCountry.Equals(destinationCountry, StringComparison.OrdinalIgnoreCase));

            return query.OrderByDescending(t => t.TransferredAt).ToList();
        }

        /// <summary>
        /// Gets all Transfer Impact Assessments.
        /// </summary>
        public IReadOnlyList<TransferImpactAssessment> GetTias(TiaStatus? status = null)
        {
            var query = _tias.Values.AsEnumerable();
            if (status.HasValue)
                query = query.Where(t => t.Status == status.Value);

            return query.OrderByDescending(t => t.CreatedAt).ToList();
        }

        /// <summary>
        /// Gets country assessment.
        /// </summary>
        public CountryAssessment? GetCountryAssessment(string countryCode)
        {
            return _countryAssessments.TryGetValue(countryCode.ToUpperInvariant(), out var ca) ? ca : null;
        }

        /// <summary>
        /// Gets audit log entries.
        /// </summary>
        public IReadOnlyList<TransferAuditEntry> GetAuditLog(int count = 100)
        {
            return _auditLog.OrderByDescending(e => e.Timestamp).Take(count).ToList();
        }

        /// <inheritdoc/>
        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("cross_border_data_transfer.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            // Check for transfers without proper mechanism
            if (!string.IsNullOrEmpty(context.SourceLocation) && !string.IsNullOrEmpty(context.DestinationLocation))
            {
                var checkResult = CheckTransfer(new TransferCheckInput
                {
                    SourceCountry = context.SourceLocation,
                    DestinationCountry = context.DestinationLocation,
                    DataCategories = context.DataSubjectCategories.ToArray(),
                    Purpose = context.ProcessingPurposes.FirstOrDefault()
                });

                if (!checkResult.IsPermitted)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "TRANSFER-001",
                        Description = $"Invalid cross-border transfer: {checkResult.Reason}",
                        Severity = ViolationSeverity.Critical,
                        Remediation = "Establish valid transfer mechanism before proceeding",
                        RegulatoryReference = "GDPR Chapter V"
                    });
                }
                else if (checkResult.Warnings?.Count > 0)
                {
                    foreach (var warning in checkResult.Warnings)
                    {
                        recommendations.Add(warning);
                    }
                }
            }

            // Check for expiring TIAs
            var expiringTias = _tias.Values
                .Where(t => t.Status == TiaStatus.Approved &&
                           t.ExpiresAt.HasValue &&
                           t.ExpiresAt.Value < DateTime.UtcNow.AddDays(30))
                .ToList();

            if (expiringTias.Count > 0)
            {
                recommendations.Add($"{expiringTias.Count} Transfer Impact Assessment(s) expire within 30 days");
            }

            // Check for transfers to high-risk countries without supplementary measures
            var highRiskTransfers = _transfers.Values
                .Where(t => t.TransferredAt > DateTime.UtcNow.AddDays(-30))
                .Where(t => _countryAssessments.TryGetValue(t.DestinationCountry.ToUpperInvariant(), out var ca) &&
                           ca.RiskLevel >= CountryRiskLevel.High)
                .Count();

            if (highRiskTransfers > 0)
            {
                recommendations.Add($"{highRiskTransfers} transfer(s) to high-risk countries in last 30 days. Review supplementary measures.");
            }

            var hasHighViolations = violations.Any(v => v.Severity >= ViolationSeverity.High);
            var isCompliant = !hasHighViolations;
            var status = violations.Count == 0 ? ComplianceStatus.Compliant :
                        hasHighViolations ? ComplianceStatus.NonCompliant :
                        ComplianceStatus.PartiallyCompliant;

            return Task.FromResult(new ComplianceResult
            {
                IsCompliant = isCompliant,
                Framework = Framework,
                Status = status,
                Violations = violations,
                Recommendations = recommendations,
                Metadata = new Dictionary<string, object>
                {
                    ["TotalTransfers"] = _transfers.Count,
                    ["ActiveTias"] = _tias.Values.Count(t => t.Status == TiaStatus.Approved),
                    ["RegisteredMechanisms"] = _mechanisms.Count,
                    ["AssessedCountries"] = _countryAssessments.Count
                }
            });
        }

        private bool IsEeaCountry(string countryCode)
        {
            var eeaCountries = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "AT", "BE", "BG", "HR", "CY", "CZ", "DK", "EE", "FI", "FR",
                "DE", "GR", "HU", "IE", "IT", "LV", "LT", "LU", "MT", "NL",
                "PL", "PT", "RO", "SK", "SI", "ES", "SE",
                "IS", "LI", "NO" // EEA non-EU
            };
            return eeaCountries.Contains(countryCode);
        }

        private TransferCheckDetail CheckAdequacy(string country)
        {
            if (_countryAssessments.TryGetValue(country.ToUpperInvariant(), out var assessment) &&
                assessment.HasAdequacyDecision)
            {
                return new TransferCheckDetail
                {
                    CheckName = "Adequacy Decision",
                    Passed = true,
                    Details = $"Adequacy decision: {assessment.AdequacyDecisionReference}"
                };
            }

            return new TransferCheckDetail
            {
                CheckName = "Adequacy Decision",
                Passed = false,
                Details = "No adequacy decision for destination country"
            };
        }

        private TransferMechanism? FindApplicableMechanism(TransferCheckInput input)
        {
            foreach (var mechanism in _mechanisms.Values.Where(m => m.IsActive))
            {
                // Check if mechanism applies to this destination
                if (mechanism.ApplicableCountries != null &&
                    !mechanism.ApplicableCountries.Contains(input.DestinationCountry, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Check validity
                if (mechanism.ValidUntil.HasValue && mechanism.ValidUntil.Value < DateTime.UtcNow)
                {
                    continue;
                }

                return mechanism;
            }

            return null;
        }

        private TransferCheckDetail CheckDerogation(TransferCheckInput input)
        {
            // GDPR Article 49 derogations
            if (input.HasExplicitConsent)
            {
                return new TransferCheckDetail
                {
                    CheckName = "Derogation",
                    Passed = true,
                    Details = "Article 49(1)(a) - Explicit consent for specific transfer"
                };
            }

            if (input.IsNecessaryForContract)
            {
                return new TransferCheckDetail
                {
                    CheckName = "Derogation",
                    Passed = true,
                    Details = "Article 49(1)(b) - Necessary for contract performance"
                };
            }

            if (input.IsForPublicInterest)
            {
                return new TransferCheckDetail
                {
                    CheckName = "Derogation",
                    Passed = true,
                    Details = "Article 49(1)(d) - Important public interest"
                };
            }

            if (input.IsForLegalClaims)
            {
                return new TransferCheckDetail
                {
                    CheckName = "Derogation",
                    Passed = true,
                    Details = "Article 49(1)(e) - Establishment or defense of legal claims"
                };
            }

            if (input.IsForVitalInterests)
            {
                return new TransferCheckDetail
                {
                    CheckName = "Derogation",
                    Passed = true,
                    Details = "Article 49(1)(f) - Vital interests of data subject"
                };
            }

            return new TransferCheckDetail
            {
                CheckName = "Derogation",
                Passed = false,
                Details = "No applicable derogation"
            };
        }

        private void InitializeDefaultMechanisms()
        {
            RegisterMechanism(new TransferMechanism
            {
                MechanismId = "scc-controller-controller",
                Name = "Standard Contractual Clauses (Controller to Controller)",
                MechanismType = TransferMechanismType.StandardContractualClauses,
                LegalBasis = "GDPR Article 46(2)(c)",
                RequiresTia = true,
                RequiresSupplementaryMeasures = true,
                IsActive = true
            });

            RegisterMechanism(new TransferMechanism
            {
                MechanismId = "scc-controller-processor",
                Name = "Standard Contractual Clauses (Controller to Processor)",
                MechanismType = TransferMechanismType.StandardContractualClauses,
                LegalBasis = "GDPR Article 46(2)(c)",
                RequiresTia = true,
                RequiresSupplementaryMeasures = true,
                IsActive = true
            });

            RegisterMechanism(new TransferMechanism
            {
                MechanismId = "eu-us-dpf",
                Name = "EU-US Data Privacy Framework",
                MechanismType = TransferMechanismType.DataPrivacyFramework,
                LegalBasis = "Adequacy Decision",
                ApplicableCountries = new[] { "US" },
                RequiresTia = false,
                RequiresSupplementaryMeasures = false,
                IsActive = true
            });

            RegisterMechanism(new TransferMechanism
            {
                MechanismId = "bcr",
                Name = "Binding Corporate Rules",
                MechanismType = TransferMechanismType.BindingCorporateRules,
                LegalBasis = "GDPR Article 47",
                RequiresTia = true,
                RequiresSupplementaryMeasures = false,
                IsActive = true
            });
        }

        private void InitializeCountryAssessments()
        {
            // Adequacy countries
            var adequacyCountries = new[]
            {
                ("AD", "Andorra", "Decision 2010/625/EU"),
                ("AR", "Argentina", "Decision 2003/490/EC"),
                ("CA", "Canada", "Decision 2002/2/EC (commercial)"),
                ("FO", "Faroe Islands", "Decision 2010/146/EU"),
                ("GG", "Guernsey", "Decision 2003/821/EC"),
                ("IL", "Israel", "Decision 2011/61/EU"),
                ("IM", "Isle of Man", "Decision 2004/411/EC"),
                ("JP", "Japan", "Decision 2019/419"),
                ("JE", "Jersey", "Decision 2008/393/EC"),
                ("NZ", "New Zealand", "Decision 2013/65/EU"),
                ("KR", "South Korea", "Decision 2022/254"),
                ("CH", "Switzerland", "Decision 2000/518/EC"),
                ("GB", "United Kingdom", "Decision 2021/1772"),
                ("UY", "Uruguay", "Decision 2012/484/EU")
            };

            foreach (var (code, name, reference) in adequacyCountries)
            {
                _countryAssessments[code] = new CountryAssessment
                {
                    CountryCode = code,
                    CountryName = name,
                    HasAdequacyDecision = true,
                    AdequacyDecisionReference = reference,
                    RiskLevel = CountryRiskLevel.Low,
                    LastAssessedAt = DateTime.UtcNow
                };
            }

            // High-risk countries (examples)
            var highRiskCountries = new[]
            {
                ("CN", "China", "Extensive government surveillance"),
                ("RU", "Russia", "Data localization requirements"),
                ("HK", "Hong Kong", "Recent changes in legal framework")
            };

            foreach (var (code, name, reason) in highRiskCountries)
            {
                _countryAssessments[code] = new CountryAssessment
                {
                    CountryCode = code,
                    CountryName = name,
                    HasAdequacyDecision = false,
                    RiskLevel = CountryRiskLevel.High,
                    RiskFactors = new[] { reason },
                    RequiresSupplementaryMeasures = true,
                    LastAssessedAt = DateTime.UtcNow
                };
            }

            // US (special case - DPF)
            _countryAssessments["US"] = new CountryAssessment
            {
                CountryCode = "US",
                CountryName = "United States",
                HasAdequacyDecision = true,
                AdequacyDecisionReference = "EU-US Data Privacy Framework",
                RiskLevel = CountryRiskLevel.Medium,
                Notes = "Adequacy limited to DPF-certified organizations",
                LastAssessedAt = DateTime.UtcNow
            };
        }
    
    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("cross_border_data_transfer.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("cross_border_data_transfer.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}

    #region Types

    /// <summary>
    /// Transfer mechanism type.
    /// </summary>
    public enum TransferMechanismType
    {
        AdequacyDecision,
        StandardContractualClauses,
        BindingCorporateRules,
        DataPrivacyFramework,
        Derogation,
        AdHocContractualClauses
    }

    /// <summary>
    /// Country risk level.
    /// </summary>
    public enum CountryRiskLevel
    {
        Unknown = 0,
        Low = 1,
        Medium = 2,
        High = 3,
        Critical = 4
    }

    /// <summary>
    /// TIA status.
    /// </summary>
    public enum TiaStatus
    {
        Draft,
        UnderReview,
        Approved,
        Expired,
        Revoked
    }

    /// <summary>
    /// Transfer action types for audit.
    /// </summary>
    public enum TransferAction
    {
        TransferRecorded,
        TransferBlocked,
        TiaCreated,
        TiaApproved,
        TiaExpired,
        MechanismRegistered
    }

    /// <summary>
    /// Transfer mechanism.
    /// </summary>
    public sealed record TransferMechanism
    {
        public required string MechanismId { get; init; }
        public required string Name { get; init; }
        public required TransferMechanismType MechanismType { get; init; }
        public required string LegalBasis { get; init; }
        public string[]? ApplicableCountries { get; init; }
        public bool RequiresTia { get; init; }
        public bool RequiresSupplementaryMeasures { get; init; }
        public DateTime? ValidUntil { get; init; }
        public required bool IsActive { get; init; }
    }

    /// <summary>
    /// Country assessment.
    /// </summary>
    public sealed record CountryAssessment
    {
        public required string CountryCode { get; init; }
        public required string CountryName { get; init; }
        public required bool HasAdequacyDecision { get; init; }
        public string? AdequacyDecisionReference { get; init; }
        public required CountryRiskLevel RiskLevel { get; init; }
        public string[]? RiskFactors { get; init; }
        public bool RequiresSupplementaryMeasures { get; init; }
        public string? Notes { get; init; }
        public required DateTime LastAssessedAt { get; init; }
    }

    /// <summary>
    /// Data transfer record.
    /// </summary>
    public sealed record DataTransferRecord
    {
        public required string TransferId { get; init; }
        public required string SourceCountry { get; init; }
        public required string DestinationCountry { get; init; }
        public required string[] DataCategories { get; init; }
        public string? Purpose { get; init; }
        public string? RecipientName { get; init; }
        public string? RecipientType { get; init; }
        public string? MechanismUsed { get; init; }
        public required DateTime TransferredAt { get; init; }
        public string? RecordedBy { get; init; }
        public string? DataVolume { get; init; }
        public bool IsRecurring { get; init; }
        public string[]? Warnings { get; init; }
    }

    /// <summary>
    /// Transfer Impact Assessment.
    /// </summary>
    public sealed record TransferImpactAssessment
    {
        public required string TiaId { get; init; }
        public required string Name { get; init; }
        public required string SourceCountry { get; init; }
        public required string DestinationCountry { get; init; }
        public required string[] DataCategories { get; init; }
        public string? TransferPurpose { get; init; }
        public string? RecipientType { get; init; }
        public required TiaStatus Status { get; init; }
        public required DateTime CreatedAt { get; init; }
        public required string CreatedBy { get; init; }
        public DateTime? ApprovedAt { get; init; }
        public string? ApprovedBy { get; init; }
        public DateTime? ExpiresAt { get; init; }
        public required CountryRiskLevel CountryRiskLevel { get; init; }
        public string? LegalFrameworkAssessment { get; init; }
        public string? PublicAuthorityAccessRisk { get; init; }
        public required SupplementaryMeasure[] SupplementaryMeasures { get; init; }
    }

    /// <summary>
    /// Supplementary measure.
    /// </summary>
    public sealed record SupplementaryMeasure
    {
        public required string MeasureType { get; init; }
        public required string Description { get; init; }
        public required string ImplementationStatus { get; init; }
    }

    /// <summary>
    /// Transfer check input.
    /// </summary>
    public sealed record TransferCheckInput
    {
        public required string SourceCountry { get; init; }
        public required string DestinationCountry { get; init; }
        public string[]? DataCategories { get; init; }
        public string? Purpose { get; init; }
        public string[]? SupplementaryMeasures { get; init; }
        public bool HasExplicitConsent { get; init; }
        public bool IsNecessaryForContract { get; init; }
        public bool IsForPublicInterest { get; init; }
        public bool IsForLegalClaims { get; init; }
        public bool IsForVitalInterests { get; init; }
    }

    /// <summary>
    /// Transfer check result.
    /// </summary>
    public sealed record TransferCheckResult
    {
        public required bool IsPermitted { get; init; }
        public string? MechanismUsed { get; init; }
        public required string Reason { get; init; }
        public required IReadOnlyList<TransferCheckDetail> CheckDetails { get; init; }
        public IReadOnlyList<string>? Warnings { get; init; }
        public IReadOnlyList<string>? Blockers { get; init; }
    }

    /// <summary>
    /// Transfer check detail.
    /// </summary>
    public sealed record TransferCheckDetail
    {
        public required string CheckName { get; init; }
        public required bool Passed { get; init; }
        public required string Details { get; init; }
    }

    /// <summary>
    /// Record transfer input.
    /// </summary>
    public sealed record RecordTransferInput
    {
        public required string SourceCountry { get; init; }
        public required string DestinationCountry { get; init; }
        public string[]? DataCategories { get; init; }
        public string? Purpose { get; init; }
        public string? RecipientName { get; init; }
        public string? RecipientType { get; init; }
        public string? RecordedBy { get; init; }
        public string? DataVolume { get; init; }
        public bool IsRecurring { get; init; }
    }

    /// <summary>
    /// Record transfer result.
    /// </summary>
    public sealed record RecordTransferResult
    {
        public required bool Success { get; init; }
        public string? TransferId { get; init; }
        public string? MechanismUsed { get; init; }
        public IReadOnlyList<string>? Warnings { get; init; }
        public IReadOnlyList<string>? Blockers { get; init; }
        public string? ErrorMessage { get; init; }
    }

    /// <summary>
    /// Create TIA input.
    /// </summary>
    public sealed record CreateTiaInput
    {
        public required string Name { get; init; }
        public required string SourceCountry { get; init; }
        public required string DestinationCountry { get; init; }
        public string[]? DataCategories { get; init; }
        public string? TransferPurpose { get; init; }
        public string? RecipientType { get; init; }
        public required string CreatedBy { get; init; }
        public string? LegalFrameworkAssessment { get; init; }
        public string? PublicAuthorityAccessRisk { get; init; }
        public SupplementaryMeasure[]? SupplementaryMeasures { get; init; }
    }

    /// <summary>
    /// Create TIA result.
    /// </summary>
    public sealed record CreateTiaResult
    {
        public required bool Success { get; init; }
        public string? TiaId { get; init; }
        public TiaStatus Status { get; init; }
        public string? ErrorMessage { get; init; }
    }

    /// <summary>
    /// Approve TIA result.
    /// </summary>
    public sealed record ApproveTiaResult
    {
        public required bool Success { get; init; }
        public string? TiaId { get; init; }
        public DateTime ExpiresAt { get; init; }
        public string? ErrorMessage { get; init; }
    }

    /// <summary>
    /// Transfer audit entry.
    /// </summary>
    public sealed record TransferAuditEntry
    {
        public required string EntryId { get; init; }
        public string? TransferId { get; init; }
        public string? TiaId { get; init; }
        public required TransferAction Action { get; init; }
        public required DateTime Timestamp { get; init; }
        public string? PerformedBy { get; init; }
        public string? Details { get; init; }
    }

    #endregion
}
