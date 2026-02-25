using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.Geofencing
{
    /// <summary>
    /// T77.10: Cross-Border Exceptions Strategy
    /// Controlled exceptions with legal approval workflow for cross-border data transfers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Features:
    /// - Legal approval workflow for exceptions
    /// - Time-limited exception grants
    /// - Multi-level authorization
    /// - Audit trail for all exceptions
    /// - Automatic expiration and renewal
    /// - Exception templates for common scenarios
    /// </para>
    /// </remarks>
    public sealed class CrossBorderExceptionsStrategy : ComplianceStrategyBase
    {
        private readonly BoundedDictionary<string, ExceptionRequest> _pendingRequests = new BoundedDictionary<string, ExceptionRequest>(1000);
        private readonly BoundedDictionary<string, GrantedExcep> _activeExceptions = new BoundedDictionary<string, GrantedExcep>(1000);
        private readonly BoundedDictionary<string, ExceptionTemplate> _templates = new BoundedDictionary<string, ExceptionTemplate>(1000);
        private readonly ConcurrentBag<ExceptionAuditEntry> _auditLog = new();

        private int _requiredApprovals = 2;
        private TimeSpan _defaultExceptionDuration = TimeSpan.FromDays(90);
        private TimeSpan _maxExceptionDuration = TimeSpan.FromDays(365);

        /// <inheritdoc/>
        public override string StrategyId => "cross-border-exceptions";

        /// <inheritdoc/>
        public override string StrategyName => "Cross-Border Exceptions";

        /// <inheritdoc/>
        public override string Framework => "DataSovereignty";

        /// <inheritdoc/>
        public override Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken cancellationToken = default)
        {
            if (configuration.TryGetValue("RequiredApprovals", out var approvalsObj) && approvalsObj is int approvals)
            {
                _requiredApprovals = Math.Max(1, approvals);
            }

            if (configuration.TryGetValue("DefaultExceptionDays", out var daysObj) && daysObj is int days)
            {
                _defaultExceptionDuration = TimeSpan.FromDays(days);
            }

            if (configuration.TryGetValue("MaxExceptionDays", out var maxObj) && maxObj is int maxDays)
            {
                _maxExceptionDuration = TimeSpan.FromDays(maxDays);
            }

            InitializeDefaultTemplates();

            return base.InitializeAsync(configuration, cancellationToken);
        }

        /// <summary>
        /// Submits a cross-border exception request.
        /// </summary>
        public ExceptionRequestResult SubmitRequest(ExceptionRequest request)
        {
            ArgumentNullException.ThrowIfNull(request, nameof(request));

            // Validate request
            var validation = ValidateRequest(request);
            if (!validation.IsValid)
            {
                return new ExceptionRequestResult
                {
                    Success = false,
                    ErrorMessage = validation.ErrorMessage
                };
            }

            // Generate request ID
            var requestId = Guid.NewGuid().ToString();
            var submittedRequest = request with
            {
                RequestId = requestId,
                SubmittedAt = DateTime.UtcNow,
                Status = ExceptionStatus.PendingReview,
                RequiredApprovals = DetermineRequiredApprovals(request)
            };

            _pendingRequests[requestId] = submittedRequest;

            LogAudit(AuditAction.RequestSubmitted, requestId, request.RequesterId,
                $"Exception request for {request.SourceRegion} -> {request.DestinationRegion}");

            return new ExceptionRequestResult
            {
                Success = true,
                RequestId = requestId,
                RequiredApprovals = submittedRequest.RequiredApprovals,
                EstimatedReviewTime = TimeSpan.FromDays(5) // Typical legal review time
            };
        }

        /// <summary>
        /// Submits a request using a predefined template.
        /// </summary>
        public ExceptionRequestResult SubmitFromTemplate(string templateId, string sourceRegion,
            string destinationRegion, string requesterId, Dictionary<string, string>? parameters = null)
        {
            if (!_templates.TryGetValue(templateId, out var template))
            {
                return new ExceptionRequestResult
                {
                    Success = false,
                    ErrorMessage = $"Template not found: {templateId}"
                };
            }

            var request = new ExceptionRequest
            {
                SourceRegion = sourceRegion,
                DestinationRegion = destinationRegion,
                DataClassifications = template.AllowedClassifications.ToList(),
                LegalBasis = template.DefaultLegalBasis,
                Justification = parameters?.GetValueOrDefault("Justification") ?? template.TemplateDescription,
                RequestedDuration = template.DefaultDuration,
                RequesterId = requesterId,
                SafeguardsInPlace = template.RequiredSafeguards.ToList(),
                TemplateId = templateId
            };

            return SubmitRequest(request);
        }

        /// <summary>
        /// Reviews and approves/rejects an exception request.
        /// </summary>
        public ReviewResult ReviewRequest(string requestId, string reviewerId, ReviewDecision decision, string comments)
        {
            if (!_pendingRequests.TryGetValue(requestId, out var request))
            {
                return new ReviewResult
                {
                    Success = false,
                    ErrorMessage = "Request not found"
                };
            }

            if (request.Status != ExceptionStatus.PendingReview && request.Status != ExceptionStatus.PendingLegalReview)
            {
                return new ReviewResult
                {
                    Success = false,
                    ErrorMessage = $"Request in invalid state: {request.Status}"
                };
            }

            // Check for self-review
            if (reviewerId.Equals(request.RequesterId, StringComparison.OrdinalIgnoreCase))
            {
                return new ReviewResult
                {
                    Success = false,
                    ErrorMessage = "Self-review not permitted"
                };
            }

            // Check if already reviewed by this reviewer
            if (request.Approvals.Any(a => a.ReviewerId.Equals(reviewerId, StringComparison.OrdinalIgnoreCase)))
            {
                return new ReviewResult
                {
                    Success = false,
                    ErrorMessage = "Already reviewed by this reviewer"
                };
            }

            // Add review
            var approval = new ExceptionApproval
            {
                ReviewerId = reviewerId,
                Decision = decision,
                Comments = comments,
                ReviewedAt = DateTime.UtcNow
            };

            request.Approvals.Add(approval);

            LogAudit(
                decision == ReviewDecision.Approve ? AuditAction.RequestApproved : AuditAction.RequestRejected,
                requestId, reviewerId, comments);

            // Check if fully rejected
            if (decision == ReviewDecision.Reject)
            {
                request = request with { Status = ExceptionStatus.Rejected };
                _pendingRequests[requestId] = request;

                return new ReviewResult
                {
                    Success = true,
                    RequestId = requestId,
                    NewStatus = ExceptionStatus.Rejected,
                    Message = "Request rejected"
                };
            }

            // Check if needs more approvals
            var approvalCount = request.Approvals.Count(a => a.Decision == ReviewDecision.Approve);
            if (approvalCount < request.RequiredApprovals)
            {
                _pendingRequests[requestId] = request;

                return new ReviewResult
                {
                    Success = true,
                    RequestId = requestId,
                    CurrentApprovals = approvalCount,
                    RequiredApprovals = request.RequiredApprovals,
                    NewStatus = request.Status,
                    Message = $"Approval {approvalCount}/{request.RequiredApprovals}"
                };
            }

            // Check if needs legal review
            if (RequiresLegalReview(request) && !request.Approvals.Any(a => a.IsLegalReview))
            {
                request = request with { Status = ExceptionStatus.PendingLegalReview };
                _pendingRequests[requestId] = request;

                return new ReviewResult
                {
                    Success = true,
                    RequestId = requestId,
                    NewStatus = ExceptionStatus.PendingLegalReview,
                    Message = "Pending legal review"
                };
            }

            // Grant the exception
            return GrantException(request);
        }

        /// <summary>
        /// Performs legal review of an exception request.
        /// </summary>
        public ReviewResult LegalReview(string requestId, string legalReviewerId, ReviewDecision decision,
            string legalOpinion, List<string>? additionalConditions = null)
        {
            if (!_pendingRequests.TryGetValue(requestId, out var request))
            {
                return new ReviewResult
                {
                    Success = false,
                    ErrorMessage = "Request not found"
                };
            }

            if (request.Status != ExceptionStatus.PendingLegalReview)
            {
                return new ReviewResult
                {
                    Success = false,
                    ErrorMessage = "Request not pending legal review"
                };
            }

            var legalApproval = new ExceptionApproval
            {
                ReviewerId = legalReviewerId,
                Decision = decision,
                Comments = legalOpinion,
                ReviewedAt = DateTime.UtcNow,
                IsLegalReview = true,
                AdditionalConditions = additionalConditions ?? new List<string>()
            };

            request.Approvals.Add(legalApproval);

            LogAudit(
                decision == ReviewDecision.Approve ? AuditAction.LegalApproved : AuditAction.LegalRejected,
                requestId, legalReviewerId, legalOpinion);

            if (decision == ReviewDecision.Reject)
            {
                request = request with { Status = ExceptionStatus.Rejected };
                _pendingRequests[requestId] = request;

                return new ReviewResult
                {
                    Success = true,
                    RequestId = requestId,
                    NewStatus = ExceptionStatus.Rejected,
                    Message = "Rejected by legal review"
                };
            }

            // Add any additional conditions
            if (additionalConditions?.Count > 0)
            {
                request.Conditions.AddRange(additionalConditions);
            }

            // Grant the exception
            return GrantException(request);
        }

        /// <summary>
        /// Checks if a cross-border transfer is covered by an active exception.
        /// </summary>
        public ExceptionCheckResult CheckException(string sourceRegion, string destinationRegion,
            string dataClassification, string? resourceId = null)
        {
            // Find applicable exceptions
            var applicableExceptions = _activeExceptions.Values
                .Where(e => e.Status == ExceptionStatus.Active &&
                           e.ExpiresAt > DateTime.UtcNow &&
                           e.SourceRegion.Equals(sourceRegion, StringComparison.OrdinalIgnoreCase) &&
                           e.DestinationRegion.Equals(destinationRegion, StringComparison.OrdinalIgnoreCase) &&
                           (e.DataClassifications.Count == 0 ||
                            e.DataClassifications.Contains(dataClassification, StringComparer.OrdinalIgnoreCase)) &&
                           (string.IsNullOrEmpty(resourceId) ||
                            e.ApplicableResources.Count == 0 ||
                            e.ApplicableResources.Contains(resourceId, StringComparer.OrdinalIgnoreCase)))
                .ToList();

            if (applicableExceptions.Count == 0)
            {
                return new ExceptionCheckResult
                {
                    HasException = false,
                    Message = "No applicable exception found"
                };
            }

            var bestException = applicableExceptions
                .OrderByDescending(e => e.ExpiresAt)
                .First();

            // Log usage
            LogAudit(AuditAction.ExceptionUsed, bestException.ExceptionId, "SYSTEM",
                $"Used for {sourceRegion} -> {destinationRegion}, classification: {dataClassification}");

            return new ExceptionCheckResult
            {
                HasException = true,
                ExceptionId = bestException.ExceptionId,
                LegalBasis = bestException.LegalBasis,
                ExpiresAt = bestException.ExpiresAt,
                Conditions = bestException.Conditions,
                Message = "Transfer covered by exception"
            };
        }

        /// <summary>
        /// Revokes an active exception.
        /// </summary>
        public RevokeResult RevokeException(string exceptionId, string revokedBy, string reason)
        {
            if (!_activeExceptions.TryGetValue(exceptionId, out var exception))
            {
                return new RevokeResult
                {
                    Success = false,
                    ErrorMessage = "Exception not found"
                };
            }

            var revokedException = exception with
            {
                Status = ExceptionStatus.Revoked,
                RevokedAt = DateTime.UtcNow,
                RevokedBy = revokedBy,
                RevocationReason = reason
            };

            _activeExceptions[exceptionId] = revokedException;

            LogAudit(AuditAction.ExceptionRevoked, exceptionId, revokedBy, reason);

            return new RevokeResult
            {
                Success = true,
                ExceptionId = exceptionId,
                RevokedAt = revokedException.RevokedAt
            };
        }

        /// <summary>
        /// Renews an existing exception.
        /// </summary>
        public RenewalResult RenewException(string exceptionId, string requesterId, TimeSpan? newDuration = null)
        {
            if (!_activeExceptions.TryGetValue(exceptionId, out var exception))
            {
                return new RenewalResult
                {
                    Success = false,
                    ErrorMessage = "Exception not found"
                };
            }

            if (exception.Status != ExceptionStatus.Active)
            {
                return new RenewalResult
                {
                    Success = false,
                    ErrorMessage = "Cannot renew non-active exception"
                };
            }

            // Check if eligible for renewal
            if (exception.RenewalCount >= exception.MaxRenewals)
            {
                return new RenewalResult
                {
                    Success = false,
                    ErrorMessage = $"Maximum renewals ({exception.MaxRenewals}) reached. Submit new request."
                };
            }

            // Create renewal request
            var duration = newDuration ?? _defaultExceptionDuration;
            if (duration > _maxExceptionDuration)
            {
                duration = _maxExceptionDuration;
            }

            var renewedExpiry = exception.ExpiresAt.Add(duration);

            var renewedException = exception with
            {
                ExpiresAt = renewedExpiry,
                RenewalCount = exception.RenewalCount + 1,
                LastRenewedAt = DateTime.UtcNow,
                LastRenewedBy = requesterId
            };

            _activeExceptions[exceptionId] = renewedException;

            LogAudit(AuditAction.ExceptionRenewed, exceptionId, requesterId,
                $"Renewed until {renewedExpiry:d}, renewal {renewedException.RenewalCount}/{exception.MaxRenewals}");

            return new RenewalResult
            {
                Success = true,
                ExceptionId = exceptionId,
                NewExpiryDate = renewedExpiry,
                RenewalCount = renewedException.RenewalCount,
                RemainingRenewals = exception.MaxRenewals - renewedException.RenewalCount
            };
        }

        /// <summary>
        /// Gets all active exceptions for a region pair.
        /// </summary>
        public IReadOnlyList<GrantedExcep> GetActiveExceptions(string? sourceRegion = null, string? destinationRegion = null)
        {
            var query = _activeExceptions.Values
                .Where(e => e.Status == ExceptionStatus.Active && e.ExpiresAt > DateTime.UtcNow);

            if (!string.IsNullOrEmpty(sourceRegion))
                query = query.Where(e => e.SourceRegion.Equals(sourceRegion, StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrEmpty(destinationRegion))
                query = query.Where(e => e.DestinationRegion.Equals(destinationRegion, StringComparison.OrdinalIgnoreCase));

            return query.ToList();
        }

        /// <summary>
        /// Gets exceptions expiring soon.
        /// </summary>
        public IReadOnlyList<GrantedExcep> GetExpiringSoon(int daysAhead = 30)
        {
            var threshold = DateTime.UtcNow.AddDays(daysAhead);
            return _activeExceptions.Values
                .Where(e => e.Status == ExceptionStatus.Active &&
                           e.ExpiresAt > DateTime.UtcNow &&
                           e.ExpiresAt < threshold)
                .OrderBy(e => e.ExpiresAt)
                .ToList();
        }

        /// <summary>
        /// Gets pending requests.
        /// </summary>
        public IReadOnlyList<ExceptionRequest> GetPendingRequests()
        {
            return _pendingRequests.Values
                .Where(r => r.Status == ExceptionStatus.PendingReview || r.Status == ExceptionStatus.PendingLegalReview)
                .OrderBy(r => r.SubmittedAt)
                .ToList();
        }

        /// <summary>
        /// Gets audit log entries.
        /// </summary>
        public IReadOnlyList<ExceptionAuditEntry> GetAuditLog(string? exceptionId = null, int count = 100)
        {
            var query = _auditLog.AsEnumerable();

            if (!string.IsNullOrEmpty(exceptionId))
                query = query.Where(e => e.ExceptionId == exceptionId);

            return query
                .OrderByDescending(e => e.Timestamp)
                .Take(count)
                .ToList();
        }

        /// <inheritdoc/>
        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
        IncrementCounter("cross_border_exceptions.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            // Check for cross-border transfer
            if (!string.IsNullOrEmpty(context.SourceLocation) &&
                !string.IsNullOrEmpty(context.DestinationLocation) &&
                !context.SourceLocation.Equals(context.DestinationLocation, StringComparison.OrdinalIgnoreCase))
            {
                var exceptionCheck = CheckException(
                    context.SourceLocation,
                    context.DestinationLocation,
                    context.DataClassification,
                    context.ResourceId);

                if (!exceptionCheck.HasException)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "CBE-001",
                        Description = $"No exception for transfer {context.SourceLocation} -> {context.DestinationLocation}",
                        Severity = ViolationSeverity.High,
                        AffectedResource = context.ResourceId,
                        Remediation = "Submit cross-border exception request or use alternative transfer mechanism"
                    });
                }
                else
                {
                    // Check conditions
                    if (exceptionCheck.Conditions.Count > 0)
                    {
                        recommendations.Add($"Exception conditions: {string.Join("; ", exceptionCheck.Conditions)}");
                    }

                    // Warn if expiring soon
                    if (exceptionCheck.ExpiresAt.HasValue &&
                        exceptionCheck.ExpiresAt.Value < DateTime.UtcNow.AddDays(30))
                    {
                        recommendations.Add($"Exception expires on {exceptionCheck.ExpiresAt.Value:d}. Consider renewal.");
                    }
                }
            }

            // Check for expired exceptions
            var expiredCount = _activeExceptions.Values.Count(e => e.Status == ExceptionStatus.Active && e.ExpiresAt < DateTime.UtcNow);
            if (expiredCount > 0)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "CBE-002",
                    Description = $"{expiredCount} exceptions have expired but not been cleaned up",
                    Severity = ViolationSeverity.Medium,
                    Remediation = "Review and archive expired exceptions"
                });
            }

            // Check for pending requests
            var pendingCount = _pendingRequests.Values.Count(r =>
                (r.Status == ExceptionStatus.PendingReview || r.Status == ExceptionStatus.PendingLegalReview) &&
                r.SubmittedAt < DateTime.UtcNow.AddDays(-7));

            if (pendingCount > 0)
            {
                recommendations.Add($"{pendingCount} exception requests pending for over 7 days. Review backlog.");
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
                    ["ActiveExceptions"] = _activeExceptions.Values.Count(e => e.Status == ExceptionStatus.Active),
                    ["PendingRequests"] = _pendingRequests.Values.Count(r => r.Status == ExceptionStatus.PendingReview),
                    ["ExpiringSoon"] = GetExpiringSoon(30).Count,
                    ["RequiredApprovals"] = _requiredApprovals
                }
            });
        }

        private (bool IsValid, string? ErrorMessage) ValidateRequest(ExceptionRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.SourceRegion))
                return (false, "Source region is required");

            if (string.IsNullOrWhiteSpace(request.DestinationRegion))
                return (false, "Destination region is required");

            if (string.IsNullOrWhiteSpace(request.LegalBasis))
                return (false, "Legal basis is required");

            if (string.IsNullOrWhiteSpace(request.Justification))
                return (false, "Justification is required");

            if (request.RequestedDuration > _maxExceptionDuration)
                return (false, $"Requested duration exceeds maximum of {_maxExceptionDuration.TotalDays} days");

            return (true, null);
        }

        private int DetermineRequiredApprovals(ExceptionRequest request)
        {
            var baseApprovals = _requiredApprovals;

            // More sensitive classifications require more approvals
            var sensitiveClassifications = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "personal", "pii", "phi", "sensitive", "confidential"
            };

            if (request.DataClassifications.Any(c => sensitiveClassifications.Any(sc => c.Contains(sc, StringComparison.OrdinalIgnoreCase))))
            {
                baseApprovals++;
            }

            // Longer durations require more approvals
            if (request.RequestedDuration > TimeSpan.FromDays(180))
            {
                baseApprovals++;
            }

            return baseApprovals;
        }

        private bool RequiresLegalReview(ExceptionRequest request)
        {
            // Legal review required for:
            // - Cross-continental transfers
            // - Sensitive data classifications
            // - Long duration exceptions

            var euRegions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "EU", "EEA" };
            var crossContinental = euRegions.Contains(request.SourceRegion) != euRegions.Contains(request.DestinationRegion);

            var sensitiveData = request.DataClassifications.Any(c =>
                c.Contains("personal", StringComparison.OrdinalIgnoreCase) ||
                c.Contains("health", StringComparison.OrdinalIgnoreCase) ||
                c.Contains("financial", StringComparison.OrdinalIgnoreCase));

            var longDuration = request.RequestedDuration > TimeSpan.FromDays(180);

            return crossContinental || sensitiveData || longDuration;
        }

        private ReviewResult GrantException(ExceptionRequest request)
        {
            var exceptionId = Guid.NewGuid().ToString();
            var duration = request.RequestedDuration > TimeSpan.Zero
                ? request.RequestedDuration
                : _defaultExceptionDuration;

            var exception = new GrantedExcep
            {
                ExceptionId = exceptionId,
                RequestId = request.RequestId ?? "",
                SourceRegion = request.SourceRegion,
                DestinationRegion = request.DestinationRegion,
                DataClassifications = request.DataClassifications,
                LegalBasis = request.LegalBasis,
                Justification = request.Justification,
                Conditions = request.Conditions,
                SafeguardsRequired = request.SafeguardsInPlace,
                Status = ExceptionStatus.Active,
                GrantedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.Add(duration),
                MaxRenewals = 3,
                RenewalCount = 0,
                ApplicableResources = new List<string>()
            };

            _activeExceptions[exceptionId] = exception;

            // Update request status
            if (request.RequestId != null)
            {
                _pendingRequests[request.RequestId] = request with { Status = ExceptionStatus.Active };
            }

            LogAudit(AuditAction.ExceptionGranted, exceptionId, "SYSTEM",
                $"Exception granted for {request.SourceRegion} -> {request.DestinationRegion}, expires {exception.ExpiresAt:d}");

            return new ReviewResult
            {
                Success = true,
                RequestId = request.RequestId,
                ExceptionId = exceptionId,
                NewStatus = ExceptionStatus.Active,
                ExpiresAt = exception.ExpiresAt,
                Message = "Exception granted"
            };
        }

        private void LogAudit(AuditAction action, string exceptionId, string actorId, string details)
        {
            _auditLog.Add(new ExceptionAuditEntry
            {
                EntryId = Guid.NewGuid().ToString(),
                ExceptionId = exceptionId,
                Action = action,
                ActorId = actorId,
                Details = details,
                Timestamp = DateTime.UtcNow
            });
        }

        private void InitializeDefaultTemplates()
        {
            _templates["scc-eu-us"] = new ExceptionTemplate
            {
                TemplateId = "scc-eu-us",
                TemplateName = "EU to US (Standard Contractual Clauses)",
                TemplateDescription = "Transfer under EU Standard Contractual Clauses",
                DefaultLegalBasis = "GDPR Article 46(2)(c) - Standard Contractual Clauses",
                AllowedClassifications = new List<string> { "personal", "personal-eu" },
                RequiredSafeguards = new List<string>
                {
                    "Standard Contractual Clauses executed",
                    "Transfer Impact Assessment completed",
                    "Supplementary measures implemented"
                },
                DefaultDuration = TimeSpan.FromDays(365),
                RequiresLegalReview = true
            };

            _templates["dpf-eu-us"] = new ExceptionTemplate
            {
                TemplateId = "dpf-eu-us",
                TemplateName = "EU to US (Data Privacy Framework)",
                TemplateDescription = "Transfer to DPF-certified US organization",
                DefaultLegalBasis = "EU-US Data Privacy Framework",
                AllowedClassifications = new List<string> { "personal", "personal-eu" },
                RequiredSafeguards = new List<string>
                {
                    "Recipient is DPF-certified",
                    "DPF certification verified"
                },
                DefaultDuration = TimeSpan.FromDays(365),
                RequiresLegalReview = false
            };

            _templates["bcr"] = new ExceptionTemplate
            {
                TemplateId = "bcr",
                TemplateName = "Binding Corporate Rules",
                TemplateDescription = "Intra-group transfer under approved BCRs",
                DefaultLegalBasis = "GDPR Article 47 - Binding Corporate Rules",
                AllowedClassifications = new List<string> { "personal", "personal-eu", "employee" },
                RequiredSafeguards = new List<string>
                {
                    "BCR approved by lead supervisory authority",
                    "Entities bound by BCRs"
                },
                DefaultDuration = TimeSpan.FromDays(730), // 2 years
                RequiresLegalReview = false
            };

            _templates["derogation-consent"] = new ExceptionTemplate
            {
                TemplateId = "derogation-consent",
                TemplateName = "Derogation - Explicit Consent",
                TemplateDescription = "Transfer based on explicit data subject consent",
                DefaultLegalBasis = "GDPR Article 49(1)(a) - Explicit consent",
                AllowedClassifications = new List<string> { "personal" },
                RequiredSafeguards = new List<string>
                {
                    "Explicit consent obtained and documented",
                    "Data subject informed of risks",
                    "Consent specific to this transfer"
                },
                DefaultDuration = TimeSpan.FromDays(90),
                RequiresLegalReview = true
            };
        }
    
    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        IncrementCounter("cross_border_exceptions.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
        IncrementCounter("cross_border_exceptions.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}

    /// <summary>
    /// Cross-border exception request.
    /// </summary>
    public sealed record ExceptionRequest
    {
        public string? RequestId { get; init; }
        public required string SourceRegion { get; init; }
        public required string DestinationRegion { get; init; }
        public List<string> DataClassifications { get; init; } = new();
        public required string LegalBasis { get; init; }
        public required string Justification { get; init; }
        public TimeSpan RequestedDuration { get; init; }
        public required string RequesterId { get; init; }
        public List<string> SafeguardsInPlace { get; init; } = new();
        public List<string> Conditions { get; init; } = new();
        public string? TemplateId { get; init; }
        public DateTime SubmittedAt { get; init; }
        public ExceptionStatus Status { get; init; }
        public int RequiredApprovals { get; init; }
        public List<ExceptionApproval> Approvals { get; init; } = new();
    }

    /// <summary>
    /// Exception status.
    /// </summary>
    public enum ExceptionStatus
    {
        PendingReview,
        PendingLegalReview,
        Active,
        Expired,
        Rejected,
        Revoked
    }

    /// <summary>
    /// Exception approval record.
    /// </summary>
    public sealed record ExceptionApproval
    {
        public required string ReviewerId { get; init; }
        public required ReviewDecision Decision { get; init; }
        public required string Comments { get; init; }
        public required DateTime ReviewedAt { get; init; }
        public bool IsLegalReview { get; init; }
        public List<string> AdditionalConditions { get; init; } = new();
    }

    /// <summary>
    /// Review decision.
    /// </summary>
    public enum ReviewDecision
    {
        Approve,
        Reject,
        RequestChanges
    }

    /// <summary>
    /// Granted exception record.
    /// </summary>
    public sealed record GrantedExcep
    {
        public required string ExceptionId { get; init; }
        public required string RequestId { get; init; }
        public required string SourceRegion { get; init; }
        public required string DestinationRegion { get; init; }
        public List<string> DataClassifications { get; init; } = new();
        public required string LegalBasis { get; init; }
        public required string Justification { get; init; }
        public List<string> Conditions { get; init; } = new();
        public List<string> SafeguardsRequired { get; init; } = new();
        public List<string> ApplicableResources { get; init; } = new();
        public ExceptionStatus Status { get; init; }
        public DateTime GrantedAt { get; init; }
        public DateTime ExpiresAt { get; init; }
        public int MaxRenewals { get; init; }
        public int RenewalCount { get; init; }
        public DateTime? LastRenewedAt { get; init; }
        public string? LastRenewedBy { get; init; }
        public DateTime? RevokedAt { get; init; }
        public string? RevokedBy { get; init; }
        public string? RevocationReason { get; init; }
    }

    /// <summary>
    /// Exception template.
    /// </summary>
    public sealed record ExceptionTemplate
    {
        public required string TemplateId { get; init; }
        public required string TemplateName { get; init; }
        public required string TemplateDescription { get; init; }
        public required string DefaultLegalBasis { get; init; }
        public List<string> AllowedClassifications { get; init; } = new();
        public List<string> RequiredSafeguards { get; init; } = new();
        public TimeSpan DefaultDuration { get; init; }
        public bool RequiresLegalReview { get; init; }
    }

    /// <summary>
    /// Exception request result.
    /// </summary>
    public sealed record ExceptionRequestResult
    {
        public required bool Success { get; init; }
        public string? RequestId { get; init; }
        public string? ErrorMessage { get; init; }
        public int RequiredApprovals { get; init; }
        public TimeSpan? EstimatedReviewTime { get; init; }
    }

    /// <summary>
    /// Review result.
    /// </summary>
    public sealed record ReviewResult
    {
        public required bool Success { get; init; }
        public string? RequestId { get; init; }
        public string? ExceptionId { get; init; }
        public string? ErrorMessage { get; init; }
        public int CurrentApprovals { get; init; }
        public int RequiredApprovals { get; init; }
        public ExceptionStatus NewStatus { get; init; }
        public DateTime? ExpiresAt { get; init; }
        public string? Message { get; init; }
    }

    /// <summary>
    /// Exception check result.
    /// </summary>
    public sealed record ExceptionCheckResult
    {
        public required bool HasException { get; init; }
        public string? ExceptionId { get; init; }
        public string? LegalBasis { get; init; }
        public DateTime? ExpiresAt { get; init; }
        public List<string> Conditions { get; init; } = new();
        public required string Message { get; init; }
    }

    /// <summary>
    /// Revoke result.
    /// </summary>
    public sealed record RevokeResult
    {
        public required bool Success { get; init; }
        public string? ExceptionId { get; init; }
        public string? ErrorMessage { get; init; }
        public DateTime? RevokedAt { get; init; }
    }

    /// <summary>
    /// Renewal result.
    /// </summary>
    public sealed record RenewalResult
    {
        public required bool Success { get; init; }
        public string? ExceptionId { get; init; }
        public string? ErrorMessage { get; init; }
        public DateTime? NewExpiryDate { get; init; }
        public int RenewalCount { get; init; }
        public int RemainingRenewals { get; init; }
    }

    /// <summary>
    /// Audit action type.
    /// </summary>
    public enum AuditAction
    {
        RequestSubmitted,
        RequestApproved,
        RequestRejected,
        LegalApproved,
        LegalRejected,
        ExceptionGranted,
        ExceptionUsed,
        ExceptionRenewed,
        ExceptionRevoked,
        ExceptionExpired
    }

    /// <summary>
    /// Exception audit entry.
    /// </summary>
    public sealed record ExceptionAuditEntry
    {
        public required string EntryId { get; init; }
        public required string ExceptionId { get; init; }
        public required AuditAction Action { get; init; }
        public required string ActorId { get; init; }
        public required string Details { get; init; }
        public required DateTime Timestamp { get; init; }
    }
}
