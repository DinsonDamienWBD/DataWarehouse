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
    /// T124.4: Consent Management Strategy
    /// Comprehensive consent lifecycle management for GDPR, CCPA, and other privacy regulations.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Consent Management Features:
    /// - Granular consent collection and recording
    /// - Consent versioning and history tracking
    /// - Purpose-based consent management
    /// - Consent expiration and renewal
    /// - Withdrawal processing with cascading effects
    /// - Proof of consent for regulatory audits
    /// </para>
    /// <para>
    /// Regulatory Support:
    /// - GDPR Article 7: Conditions for consent
    /// - GDPR Article 8: Child's consent
    /// - CCPA: Right to opt-out
    /// - LGPD: Brazilian consent requirements
    /// - PIPEDA: Canadian consent rules
    /// </para>
    /// </remarks>
    public sealed class ConsentManagementStrategy : ComplianceStrategyBase
    {
        private readonly BoundedDictionary<string, ConsentRecord> _consents = new BoundedDictionary<string, ConsentRecord>(1000);
        private readonly BoundedDictionary<string, ConsentPurpose> _purposes = new BoundedDictionary<string, ConsentPurpose>(1000);
        private readonly BoundedDictionary<string, ConsentPreference> _preferences = new BoundedDictionary<string, ConsentPreference>(1000);
        private readonly ConcurrentBag<ConsentAuditEntry> _auditLog = new();
        private readonly BoundedDictionary<string, ConsentVersion> _versions = new BoundedDictionary<string, ConsentVersion>(1000);

        private TimeSpan _defaultConsentExpiry = TimeSpan.FromDays(365);
        private int _ageOfMajority = 16; // GDPR default
        private bool _requireDoubleOptIn = true;

        /// <inheritdoc/>
        public override string StrategyId => "consent-management";

        /// <inheritdoc/>
        public override string StrategyName => "Consent Management";

        /// <inheritdoc/>
        public override string Framework => "DataPrivacy";

        /// <inheritdoc/>
        public override Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken cancellationToken = default)
        {
            if (configuration.TryGetValue("DefaultConsentExpiryDays", out var expiryObj) && expiryObj is int days)
                _defaultConsentExpiry = TimeSpan.FromDays(days);

            if (configuration.TryGetValue("AgeOfMajority", out var ageObj) && ageObj is int age)
                _ageOfMajority = Math.Max(13, Math.Min(21, age));

            if (configuration.TryGetValue("RequireDoubleOptIn", out var optInObj) && optInObj is bool optIn)
                _requireDoubleOptIn = optIn;

            InitializeDefaultPurposes();
            return base.InitializeAsync(configuration, cancellationToken);
        }

        /// <summary>
        /// Registers a consent purpose.
        /// </summary>
        public void RegisterPurpose(ConsentPurpose purpose)
        {
            ArgumentNullException.ThrowIfNull(purpose);
            _purposes[purpose.PurposeId] = purpose;
        }

        /// <summary>
        /// Records consent from a data subject.
        /// </summary>
        public ConsentRecordResult RecordConsent(ConsentRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);

            // Validate purpose exists
            if (!_purposes.TryGetValue(request.PurposeId, out var purpose))
            {
                return new ConsentRecordResult
                {
                    Success = false,
                    ErrorMessage = $"Unknown purpose: {request.PurposeId}"
                };
            }

            // Check for child consent requirements
            if (request.SubjectAge.HasValue && request.SubjectAge.Value < _ageOfMajority)
            {
                if (!request.ParentalConsentProvided)
                {
                    return new ConsentRecordResult
                    {
                        Success = false,
                        ErrorMessage = $"Parental consent required for subjects under {_ageOfMajority}",
                        RequiresParentalConsent = true
                    };
                }
            }

            // Check double opt-in requirement
            if (_requireDoubleOptIn && purpose.RequiresDoubleOptIn)
            {
                if (!request.DoubleOptInConfirmed)
                {
                    var pendingId = Guid.NewGuid().ToString();
                    _consents[pendingId] = new ConsentRecord
                    {
                        ConsentId = pendingId,
                        SubjectId = request.SubjectId,
                        PurposeId = request.PurposeId,
                        Status = ConsentStatus.PendingConfirmation,
                        CreatedAt = DateTime.UtcNow
                    };

                    return new ConsentRecordResult
                    {
                        Success = false,
                        RequiresDoubleOptIn = true,
                        PendingConsentId = pendingId,
                        ErrorMessage = "Double opt-in confirmation required"
                    };
                }
            }

            // Create consent record
            var consentId = Guid.NewGuid().ToString();
            var expiryDate = request.CustomExpiry ?? DateTime.UtcNow.Add(_defaultConsentExpiry);

            var consent = new ConsentRecord
            {
                ConsentId = consentId,
                SubjectId = request.SubjectId,
                PurposeId = request.PurposeId,
                Status = ConsentStatus.Active,
                ConsentGiven = request.ConsentGiven,
                VersionId = purpose.CurrentVersionId,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = expiryDate,
                CollectionMethod = request.CollectionMethod,
                CollectionContext = request.CollectionContext,
                IpAddress = request.IpAddress,
                UserAgent = request.UserAgent,
                ParentalConsentId = request.ParentalConsentId,
                ProofOfConsent = GenerateProofOfConsent(request)
            };

            _consents[consentId] = consent;

            // Update preferences
            UpdatePreferences(request.SubjectId, request.PurposeId, request.ConsentGiven);

            // Audit log
            _auditLog.Add(new ConsentAuditEntry
            {
                EntryId = Guid.NewGuid().ToString(),
                ConsentId = consentId,
                SubjectId = request.SubjectId,
                PurposeId = request.PurposeId,
                Action = ConsentAction.Granted,
                Timestamp = DateTime.UtcNow,
                Details = $"Consent granted via {request.CollectionMethod}"
            });

            return new ConsentRecordResult
            {
                Success = true,
                ConsentId = consentId,
                ExpiresAt = expiryDate,
                ProofOfConsent = consent.ProofOfConsent
            };
        }

        /// <summary>
        /// Confirms double opt-in for pending consent.
        /// </summary>
        public ConsentRecordResult ConfirmDoubleOptIn(string pendingConsentId, string confirmationToken)
        {
            if (!_consents.TryGetValue(pendingConsentId, out var consent))
            {
                return new ConsentRecordResult
                {
                    Success = false,
                    ErrorMessage = "Pending consent not found"
                };
            }

            if (consent.Status != ConsentStatus.PendingConfirmation)
            {
                return new ConsentRecordResult
                {
                    Success = false,
                    ErrorMessage = "Consent is not pending confirmation"
                };
            }

            // Verify token (simplified - in production would validate cryptographically)
            if (string.IsNullOrEmpty(confirmationToken))
            {
                return new ConsentRecordResult
                {
                    Success = false,
                    ErrorMessage = "Invalid confirmation token"
                };
            }

            // Update consent status
            var confirmedConsent = consent with
            {
                Status = ConsentStatus.Active,
                ConsentGiven = true,
                ConfirmedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.Add(_defaultConsentExpiry)
            };

            _consents[pendingConsentId] = confirmedConsent;

            // Update preferences
            UpdatePreferences(consent.SubjectId, consent.PurposeId, true);

            // Audit log
            _auditLog.Add(new ConsentAuditEntry
            {
                EntryId = Guid.NewGuid().ToString(),
                ConsentId = pendingConsentId,
                SubjectId = consent.SubjectId,
                PurposeId = consent.PurposeId,
                Action = ConsentAction.DoubleOptInConfirmed,
                Timestamp = DateTime.UtcNow
            });

            return new ConsentRecordResult
            {
                Success = true,
                ConsentId = pendingConsentId,
                ExpiresAt = confirmedConsent.ExpiresAt
            };
        }

        /// <summary>
        /// Withdraws consent for a data subject.
        /// </summary>
        public WithdrawConsentResult WithdrawConsent(string subjectId, string purposeId, string? reason = null)
        {
            // Snapshot the values to find the target consent, then use AddOrUpdate for atomic update
            var consent = _consents.Values
                .FirstOrDefault(c => c.SubjectId == subjectId &&
                                    c.PurposeId == purposeId &&
                                    c.Status == ConsentStatus.Active);

            if (consent == null)
            {
                return new WithdrawConsentResult
                {
                    Success = false,
                    ErrorMessage = "Active consent not found"
                };
            }

            // Atomically replace: if concurrent withdrawal already changed status, the factory won't re-withdraw
            ConsentRecord? withdrawnConsent = null;
            _consents.AddOrUpdate(
                consent.ConsentId,
                _ => consent, // should not happen; key already exists
                (_, existing) =>
                {
                    if (existing.Status != ConsentStatus.Active)
                        return existing; // Already withdrawn concurrently; no-op
                    withdrawnConsent = existing with
                    {
                        Status = ConsentStatus.Withdrawn,
                        WithdrawnAt = DateTime.UtcNow,
                        WithdrawalReason = reason
                    };
                    return withdrawnConsent;
                });

            if (withdrawnConsent == null)
            {
                return new WithdrawConsentResult { Success = false, ErrorMessage = "Consent already withdrawn" };
            }

            // Update preferences
            UpdatePreferences(subjectId, purposeId, false);

            // Audit log
            _auditLog.Add(new ConsentAuditEntry
            {
                EntryId = Guid.NewGuid().ToString(),
                ConsentId = consent.ConsentId,
                SubjectId = subjectId,
                PurposeId = purposeId,
                Action = ConsentAction.Withdrawn,
                Timestamp = DateTime.UtcNow,
                Details = reason
            });

            // Determine cascading effects
            var affectedPurposes = new List<string>();
            if (_purposes.TryGetValue(purposeId, out var purpose))
            {
                // Find dependent purposes
                affectedPurposes = _purposes.Values
                    .Where(p => p.DependsOnPurposes?.Contains(purposeId) == true)
                    .Select(p => p.PurposeId)
                    .ToList();

                // Cascade withdrawal to dependent purposes
                foreach (var depPurposeId in affectedPurposes)
                {
                    var depConsent = _consents.Values
                        .FirstOrDefault(c => c.SubjectId == subjectId &&
                                            c.PurposeId == depPurposeId &&
                                            c.Status == ConsentStatus.Active);

                    if (depConsent != null)
                    {
                        _consents[depConsent.ConsentId] = depConsent with
                        {
                            Status = ConsentStatus.Withdrawn,
                            WithdrawnAt = DateTime.UtcNow,
                            WithdrawalReason = $"Cascaded from withdrawal of {purposeId}"
                        };

                        UpdatePreferences(subjectId, depPurposeId, false);
                    }
                }
            }

            return new WithdrawConsentResult
            {
                Success = true,
                ConsentId = consent.ConsentId,
                WithdrawnAt = withdrawnConsent.WithdrawnAt!.Value,
                AffectedPurposes = affectedPurposes
            };
        }

        /// <summary>
        /// Checks if consent exists for a subject and purpose.
        /// </summary>
        public ConsentCheckResult CheckConsent(string subjectId, string purposeId)
        {
            var consent = _consents.Values
                .Where(c => c.SubjectId == subjectId && c.PurposeId == purposeId)
                .OrderByDescending(c => c.CreatedAt)
                .FirstOrDefault();

            if (consent == null)
            {
                return new ConsentCheckResult
                {
                    HasConsent = false,
                    Status = ConsentStatus.None,
                    Reason = "No consent record found"
                };
            }

            // Check expiry
            if (consent.Status == ConsentStatus.Active && consent.ExpiresAt.HasValue && consent.ExpiresAt.Value < DateTime.UtcNow)
            {
                // Mark as expired
                _consents[consent.ConsentId] = consent with { Status = ConsentStatus.Expired };

                return new ConsentCheckResult
                {
                    HasConsent = false,
                    ConsentId = consent.ConsentId,
                    Status = ConsentStatus.Expired,
                    Reason = "Consent has expired"
                };
            }

            return new ConsentCheckResult
            {
                HasConsent = consent.Status == ConsentStatus.Active && consent.ConsentGiven,
                ConsentId = consent.ConsentId,
                Status = consent.Status,
                VersionId = consent.VersionId,
                ExpiresAt = consent.ExpiresAt,
                Reason = consent.Status switch
                {
                    ConsentStatus.Active => "Valid consent exists",
                    ConsentStatus.Withdrawn => "Consent was withdrawn",
                    ConsentStatus.Expired => "Consent has expired",
                    ConsentStatus.PendingConfirmation => "Awaiting double opt-in confirmation",
                    _ => "Consent not granted"
                }
            };
        }

        /// <summary>
        /// Gets consent history for a data subject.
        /// </summary>
        public IReadOnlyList<ConsentRecord> GetConsentHistory(string subjectId, string? purposeId = null)
        {
            var query = _consents.Values.Where(c => c.SubjectId == subjectId);

            if (!string.IsNullOrEmpty(purposeId))
                query = query.Where(c => c.PurposeId == purposeId);

            return query.OrderByDescending(c => c.CreatedAt).ToList();
        }

        /// <summary>
        /// Gets consent preferences for a data subject.
        /// </summary>
        public ConsentPreference? GetPreferences(string subjectId)
        {
            return _preferences.TryGetValue(subjectId, out var prefs) ? prefs : null;
        }

        /// <summary>
        /// Gets all consents expiring soon.
        /// </summary>
        public IReadOnlyList<ConsentRecord> GetExpiringSoon(int daysAhead = 30)
        {
            var threshold = DateTime.UtcNow.AddDays(daysAhead);
            return _consents.Values
                .Where(c => c.Status == ConsentStatus.Active &&
                           c.ExpiresAt.HasValue &&
                           c.ExpiresAt.Value < threshold)
                .OrderBy(c => c.ExpiresAt)
                .ToList();
        }

        /// <summary>
        /// Exports consent proof for regulatory audit.
        /// </summary>
        public ConsentExport ExportConsentProof(string subjectId)
        {
            var consents = _consents.Values.Where(c => c.SubjectId == subjectId).ToList();
            var auditEntries = _auditLog.Where(e => e.SubjectId == subjectId).ToList();

            return new ConsentExport
            {
                SubjectId = subjectId,
                ExportedAt = DateTime.UtcNow,
                Consents = consents,
                AuditTrail = auditEntries,
                Purposes = _purposes.Values.ToList(),
                Summary = new ConsentSummary
                {
                    TotalConsents = consents.Count,
                    ActiveConsents = consents.Count(c => c.Status == ConsentStatus.Active),
                    WithdrawnConsents = consents.Count(c => c.Status == ConsentStatus.Withdrawn),
                    ExpiredConsents = consents.Count(c => c.Status == ConsentStatus.Expired)
                }
            };
        }

        /// <summary>
        /// Gets all registered purposes.
        /// </summary>
        public IReadOnlyList<ConsentPurpose> GetPurposes()
        {
            return _purposes.Values.ToList();
        }

        /// <summary>
        /// Gets audit log entries.
        /// </summary>
        public IReadOnlyList<ConsentAuditEntry> GetAuditLog(string? subjectId = null, int count = 100)
        {
            var query = _auditLog.AsEnumerable();
            if (!string.IsNullOrEmpty(subjectId))
                query = query.Where(e => e.SubjectId == subjectId);

            return query.OrderByDescending(e => e.Timestamp).Take(count).ToList();
        }

        /// <inheritdoc/>
        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("consent_management.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            // Check if consent is required and obtained
            if (context.Attributes.TryGetValue("RequiresConsent", out var reqObj) && reqObj is true)
            {
                var subjectId = context.Attributes.TryGetValue("SubjectId", out var sidObj) ? sidObj?.ToString() : null;
                var purposeId = context.Attributes.TryGetValue("PurposeId", out var pidObj) ? pidObj?.ToString() : null;

                if (string.IsNullOrEmpty(subjectId) || string.IsNullOrEmpty(purposeId))
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "CONSENT-001",
                        Description = "Subject ID and Purpose ID required for consent check",
                        Severity = ViolationSeverity.High,
                        Remediation = "Provide SubjectId and PurposeId attributes"
                    });
                }
                else
                {
                    var check = CheckConsent(subjectId, purposeId);
                    if (!check.HasConsent)
                    {
                        violations.Add(new ComplianceViolation
                        {
                            Code = "CONSENT-002",
                            Description = $"No valid consent for purpose '{purposeId}': {check.Reason}",
                            Severity = ViolationSeverity.Critical,
                            Remediation = "Obtain valid consent before processing",
                            RegulatoryReference = "GDPR Article 6(1)(a)"
                        });
                    }
                    else if (check.ExpiresAt.HasValue && check.ExpiresAt.Value < DateTime.UtcNow.AddDays(30))
                    {
                        recommendations.Add($"Consent for purpose '{purposeId}' expires on {check.ExpiresAt.Value:d}. Consider renewal.");
                    }
                }
            }

            // Check consent version currency
            if (context.Attributes.TryGetValue("ConsentVersionCheck", out var verObj) && verObj is true)
            {
                var subjectId = context.Attributes["SubjectId"]?.ToString();
                if (!string.IsNullOrEmpty(subjectId))
                {
                    var outdatedConsents = _consents.Values
                        .Where(c => c.SubjectId == subjectId && c.Status == ConsentStatus.Active)
                        .Where(c => _purposes.TryGetValue(c.PurposeId, out var p) && p.CurrentVersionId != c.VersionId)
                        .ToList();

                    if (outdatedConsents.Count > 0)
                    {
                        recommendations.Add($"Subject has {outdatedConsents.Count} consent(s) on outdated policy versions. Consider reconsent.");
                    }
                }
            }

            var isCompliant = !violations.Any(v => v.Severity >= ViolationSeverity.High);
            var status = violations.Count == 0 ? ComplianceStatus.Compliant :
                        violations.Any(v => v.Severity >= ViolationSeverity.High) ? ComplianceStatus.NonCompliant :
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
                    ["TotalConsents"] = _consents.Count,
                    ["ActiveConsents"] = _consents.Values.Count(c => c.Status == ConsentStatus.Active),
                    ["RegisteredPurposes"] = _purposes.Count
                }
            });
        }

        private void UpdatePreferences(string subjectId, string purposeId, bool consented)
        {
            _preferences.AddOrUpdate(
                subjectId,
                _ => new ConsentPreference
                {
                    SubjectId = subjectId,
                    PurposeConsents = new Dictionary<string, bool> { [purposeId] = consented },
                    LastUpdated = DateTime.UtcNow
                },
                (_, existing) =>
                {
                    existing.PurposeConsents[purposeId] = consented;
                    existing.LastUpdated = DateTime.UtcNow;
                    return existing;
                });
        }

        private string GenerateProofOfConsent(ConsentRequest request)
        {
            var proofData = $"{request.SubjectId}|{request.PurposeId}|{DateTime.UtcNow:O}|{request.CollectionMethod}";
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            var hash = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(proofData));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private void InitializeDefaultPurposes()
        {
            RegisterPurpose(new ConsentPurpose
            {
                PurposeId = "essential-services",
                Name = "Essential Services",
                Description = "Processing necessary for providing the core service",
                LegalBasis = "Contract performance",
                IsEssential = true,
                RequiresExplicitConsent = false,
                CurrentVersionId = "v1.0"
            });

            RegisterPurpose(new ConsentPurpose
            {
                PurposeId = "marketing-communications",
                Name = "Marketing Communications",
                Description = "Sending promotional emails and newsletters",
                LegalBasis = "Consent",
                IsEssential = false,
                RequiresExplicitConsent = true,
                RequiresDoubleOptIn = true,
                CurrentVersionId = "v1.0"
            });

            RegisterPurpose(new ConsentPurpose
            {
                PurposeId = "analytics",
                Name = "Analytics and Improvement",
                Description = "Analyzing usage patterns to improve our services",
                LegalBasis = "Legitimate interest",
                IsEssential = false,
                RequiresExplicitConsent = false,
                AllowsOptOut = true,
                CurrentVersionId = "v1.0"
            });

            RegisterPurpose(new ConsentPurpose
            {
                PurposeId = "third-party-sharing",
                Name = "Third-Party Data Sharing",
                Description = "Sharing data with selected partners",
                LegalBasis = "Consent",
                IsEssential = false,
                RequiresExplicitConsent = true,
                RequiresDoubleOptIn = true,
                CurrentVersionId = "v1.0"
            });

            RegisterPurpose(new ConsentPurpose
            {
                PurposeId = "profiling",
                Name = "Automated Profiling",
                Description = "Creating profiles for personalized experiences",
                LegalBasis = "Consent",
                IsEssential = false,
                RequiresExplicitConsent = true,
                CurrentVersionId = "v1.0"
            });
        }
    
    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("consent_management.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("consent_management.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}

    #region Types

    /// <summary>
    /// Consent status.
    /// </summary>
    public enum ConsentStatus
    {
        None,
        Active,
        Withdrawn,
        Expired,
        PendingConfirmation
    }

    /// <summary>
    /// Consent action types for audit.
    /// </summary>
    public enum ConsentAction
    {
        Granted,
        Withdrawn,
        Expired,
        Renewed,
        DoubleOptInConfirmed,
        VersionUpdated
    }

    /// <summary>
    /// Consent purpose definition.
    /// </summary>
    public sealed record ConsentPurpose
    {
        public required string PurposeId { get; init; }
        public required string Name { get; init; }
        public required string Description { get; init; }
        public required string LegalBasis { get; init; }
        public bool IsEssential { get; init; }
        public bool RequiresExplicitConsent { get; init; }
        public bool RequiresDoubleOptIn { get; init; }
        public bool AllowsOptOut { get; init; }
        public string? CurrentVersionId { get; init; }
        public string[]? DependsOnPurposes { get; init; }
    }

    /// <summary>
    /// Consent record.
    /// </summary>
    public sealed record ConsentRecord
    {
        public required string ConsentId { get; init; }
        public required string SubjectId { get; init; }
        public required string PurposeId { get; init; }
        public required ConsentStatus Status { get; init; }
        public bool ConsentGiven { get; init; }
        public string? VersionId { get; init; }
        public required DateTime CreatedAt { get; init; }
        public DateTime? ConfirmedAt { get; init; }
        public DateTime? ExpiresAt { get; init; }
        public DateTime? WithdrawnAt { get; init; }
        public string? WithdrawalReason { get; init; }
        public string? CollectionMethod { get; init; }
        public string? CollectionContext { get; init; }
        public string? IpAddress { get; init; }
        public string? UserAgent { get; init; }
        public string? ParentalConsentId { get; init; }
        public string? ProofOfConsent { get; init; }
    }

    /// <summary>
    /// Consent request.
    /// </summary>
    public sealed record ConsentRequest
    {
        public required string SubjectId { get; init; }
        public required string PurposeId { get; init; }
        public required bool ConsentGiven { get; init; }
        public required string CollectionMethod { get; init; }
        public string? CollectionContext { get; init; }
        public string? IpAddress { get; init; }
        public string? UserAgent { get; init; }
        public int? SubjectAge { get; init; }
        public bool ParentalConsentProvided { get; init; }
        public string? ParentalConsentId { get; init; }
        public bool DoubleOptInConfirmed { get; init; }
        public DateTime? CustomExpiry { get; init; }
    }

    /// <summary>
    /// Consent record result.
    /// </summary>
    public sealed record ConsentRecordResult
    {
        public required bool Success { get; init; }
        public string? ConsentId { get; init; }
        public DateTime? ExpiresAt { get; init; }
        public string? ProofOfConsent { get; init; }
        public string? ErrorMessage { get; init; }
        public bool RequiresParentalConsent { get; init; }
        public bool RequiresDoubleOptIn { get; init; }
        public string? PendingConsentId { get; init; }
    }

    /// <summary>
    /// Withdraw consent result.
    /// </summary>
    public sealed record WithdrawConsentResult
    {
        public required bool Success { get; init; }
        public string? ConsentId { get; init; }
        public DateTime WithdrawnAt { get; init; }
        public IReadOnlyList<string>? AffectedPurposes { get; init; }
        public string? ErrorMessage { get; init; }
    }

    /// <summary>
    /// Consent check result.
    /// </summary>
    public sealed record ConsentCheckResult
    {
        public required bool HasConsent { get; init; }
        public string? ConsentId { get; init; }
        public required ConsentStatus Status { get; init; }
        public string? VersionId { get; init; }
        public DateTime? ExpiresAt { get; init; }
        public string? Reason { get; init; }
    }

    /// <summary>
    /// Consent preference for a subject.
    /// </summary>
    public sealed class ConsentPreference
    {
        public required string SubjectId { get; init; }
        public Dictionary<string, bool> PurposeConsents { get; init; } = new();
        public DateTime LastUpdated { get; set; }
    }

    /// <summary>
    /// Consent version tracking.
    /// </summary>
    public sealed record ConsentVersion
    {
        public required string VersionId { get; init; }
        public required string PurposeId { get; init; }
        public required string PolicyText { get; init; }
        public required DateTime EffectiveDate { get; init; }
        public string? PreviousVersionId { get; init; }
    }

    /// <summary>
    /// Consent audit entry.
    /// </summary>
    public sealed record ConsentAuditEntry
    {
        public required string EntryId { get; init; }
        public required string ConsentId { get; init; }
        public required string SubjectId { get; init; }
        public required string PurposeId { get; init; }
        public required ConsentAction Action { get; init; }
        public required DateTime Timestamp { get; init; }
        public string? Details { get; init; }
    }

    /// <summary>
    /// Consent export for regulatory proof.
    /// </summary>
    public sealed record ConsentExport
    {
        public required string SubjectId { get; init; }
        public required DateTime ExportedAt { get; init; }
        public required IReadOnlyList<ConsentRecord> Consents { get; init; }
        public required IReadOnlyList<ConsentAuditEntry> AuditTrail { get; init; }
        public required IReadOnlyList<ConsentPurpose> Purposes { get; init; }
        public required ConsentSummary Summary { get; init; }
    }

    /// <summary>
    /// Consent summary statistics.
    /// </summary>
    public sealed record ConsentSummary
    {
        public int TotalConsents { get; init; }
        public int ActiveConsents { get; init; }
        public int WithdrawnConsents { get; init; }
        public int ExpiredConsents { get; init; }
    }

    #endregion
}
