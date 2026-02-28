using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.Regulations
{
    /// <summary>
    /// GDPR (General Data Protection Regulation) compliance strategy.
    /// Validates operations against EU data protection requirements.
    /// </summary>
    public sealed class GdprStrategy : ComplianceStrategyBase
    {
        private readonly HashSet<string> _lawfulBases = new(StringComparer.OrdinalIgnoreCase)
        {
            "consent", "contract", "legal-obligation", "vital-interests",
            "public-task", "legitimate-interests"
        };

        private readonly HashSet<string> _sensitiveCategories = new(StringComparer.OrdinalIgnoreCase)
        {
            "racial-ethnic-origin", "political-opinions", "religious-beliefs",
            "trade-union-membership", "genetic-data", "biometric-data",
            "health-data", "sexual-orientation", "criminal-data"
        };

        /// <inheritdoc/>
        public override string StrategyId => "gdpr";

        /// <inheritdoc/>
        public override string StrategyName => "GDPR Compliance";

        /// <inheritdoc/>
        public override string Framework => "GDPR";

        /// <inheritdoc/>
        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("gdpr.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            // Check lawful basis for processing
            CheckLawfulBasis(context, violations, recommendations);

            // Check data minimization
            CheckDataMinimization(context, violations, recommendations);

            // Check purpose limitation
            CheckPurposeLimitation(context, violations, recommendations);

            // Check storage limitation
            CheckStorageLimitation(context, violations, recommendations);

            // Check special categories (sensitive data)
            CheckSpecialCategories(context, violations, recommendations);

            // Check data subject rights
            CheckDataSubjectRights(context, violations, recommendations);

            // Check cross-border transfers
            CheckTransferRequirements(context, violations, recommendations);

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
                    ["DataClassification"] = context.DataClassification,
                    ["OperationType"] = context.OperationType,
                    ["DataSubjectCategories"] = context.DataSubjectCategories.ToList(),
                    ["ProcessingPurposes"] = context.ProcessingPurposes.ToList()
                }
            });
        }

        private void CheckLawfulBasis(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (!context.Attributes.TryGetValue("LawfulBasis", out var basisObj) ||
                basisObj is not string basis ||
                string.IsNullOrEmpty(basis))
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "GDPR-001",
                    Description = "No lawful basis specified for data processing",
                    Severity = ViolationSeverity.Critical,
                    Remediation = "Specify a lawful basis: consent, contract, legal obligation, vital interests, public task, or legitimate interests",
                    RegulatoryReference = "GDPR Article 6"
                });
                return;
            }

            if (!_lawfulBases.Contains(basis))
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "GDPR-002",
                    Description = $"Invalid lawful basis: {basis}",
                    Severity = ViolationSeverity.High,
                    Remediation = $"Use one of: {string.Join(", ", _lawfulBases)}",
                    RegulatoryReference = "GDPR Article 6"
                });
            }

            // Additional checks based on lawful basis
            if (basis.Equals("consent", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("ConsentRecorded", out var consentObj) || consentObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "GDPR-003",
                        Description = "Consent-based processing requires recorded consent",
                        Severity = ViolationSeverity.High,
                        Remediation = "Record consent with timestamp, scope, and mechanism",
                        RegulatoryReference = "GDPR Article 7"
                    });
                }
            }

            if (basis.Equals("legitimate-interests", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("LegitimateInterestsAssessment", out var liaObj) || liaObj is not true)
                {
                    recommendations.Add("Document Legitimate Interests Assessment (LIA) for processing");
                }
            }
        }

        private void CheckDataMinimization(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (context.Attributes.TryGetValue("DataFields", out var fieldsObj) &&
                fieldsObj is IEnumerable<string> fields)
            {
                var fieldList = fields.ToList();
                if (fieldList.Count > 50)
                {
                    recommendations.Add($"Large number of data fields ({fieldList.Count}). Review for data minimization compliance.");
                }
            }

            if (context.Attributes.TryGetValue("DataMinimizationReview", out var reviewObj) && reviewObj is false)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "GDPR-004",
                    Description = "Data minimization review not completed",
                    Severity = ViolationSeverity.Medium,
                    Remediation = "Review data collection to ensure only necessary data is processed",
                    RegulatoryReference = "GDPR Article 5(1)(c)"
                });
            }
        }

        private void CheckPurposeLimitation(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (!context.ProcessingPurposes.Any())
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "GDPR-005",
                    Description = "No processing purpose specified",
                    Severity = ViolationSeverity.High,
                    Remediation = "Specify explicit, legitimate purposes for processing",
                    RegulatoryReference = "GDPR Article 5(1)(b)"
                });
            }

            if (context.Attributes.TryGetValue("OriginalPurpose", out var originalObj) &&
                originalObj is string originalPurpose)
            {
                var currentPurposes = context.ProcessingPurposes;
                if (!currentPurposes.Contains(originalPurpose, StringComparer.OrdinalIgnoreCase))
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "GDPR-006",
                        Description = "Processing purpose differs from original collection purpose",
                        Severity = ViolationSeverity.High,
                        Remediation = "Ensure compatibility with original purpose or obtain fresh consent",
                        RegulatoryReference = "GDPR Article 5(1)(b)"
                    });
                }
            }
        }

        private void CheckStorageLimitation(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (context.Attributes.TryGetValue("RetentionPeriodDefined", out var retentionObj) && retentionObj is false)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "GDPR-007",
                    Description = "No retention period defined for personal data",
                    Severity = ViolationSeverity.Medium,
                    Remediation = "Define and document data retention periods",
                    RegulatoryReference = "GDPR Article 5(1)(e)"
                });
            }

            if (context.Attributes.TryGetValue("DataAge", out var ageObj) &&
                ageObj is TimeSpan age &&
                context.Attributes.TryGetValue("MaxRetentionPeriod", out var maxObj) &&
                maxObj is TimeSpan maxRetention)
            {
                if (age > maxRetention)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "GDPR-008",
                        Description = $"Data has exceeded retention period ({age.TotalDays:F0} days > {maxRetention.TotalDays:F0} days)",
                        Severity = ViolationSeverity.High,
                        Remediation = "Delete or anonymize data that has exceeded retention period",
                        RegulatoryReference = "GDPR Article 5(1)(e)"
                    });
                }
            }
        }

        private void CheckSpecialCategories(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            var sensitiveData = context.DataSubjectCategories
                .Where(c => _sensitiveCategories.Contains(c))
                .ToList();

            if (sensitiveData.Any())
            {
                // Check for explicit consent or other Article 9 exemption
                if (!context.Attributes.TryGetValue("SpecialCategoryExemption", out var exemptionObj) ||
                    exemptionObj is not string exemption ||
                    string.IsNullOrEmpty(exemption))
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "GDPR-009",
                        Description = $"Processing special category data ({string.Join(", ", sensitiveData)}) without documented exemption",
                        Severity = ViolationSeverity.Critical,
                        Remediation = "Document Article 9(2) exemption basis for special category data",
                        RegulatoryReference = "GDPR Article 9"
                    });
                }

                recommendations.Add($"Enhanced security measures recommended for special category data: {string.Join(", ", sensitiveData)}");
            }
        }

        private void CheckDataSubjectRights(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            // Check if data subject rights mechanisms are in place
            if (!string.IsNullOrEmpty(context.OperationType) &&
                (context.OperationType.Equals("access-request", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(context.OperationType, "deletion-request", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(context.OperationType, "portability-request", StringComparison.OrdinalIgnoreCase)))
            {
                if (!context.Attributes.TryGetValue("IdentityVerified", out var verifiedObj) || verifiedObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "GDPR-010",
                        Description = "Data subject identity not verified before processing request",
                        Severity = ViolationSeverity.High,
                        Remediation = "Verify identity of data subject before processing rights request",
                        RegulatoryReference = "GDPR Article 12(6)"
                    });
                }
            }

            if (!string.IsNullOrEmpty(context.OperationType) && context.OperationType.Equals("automated-decision", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("HumanReviewAvailable", out var reviewObj) || reviewObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "GDPR-011",
                        Description = "Automated decision-making without human review option",
                        Severity = ViolationSeverity.High,
                        Remediation = "Provide mechanism for human review of automated decisions",
                        RegulatoryReference = "GDPR Article 22"
                    });
                }
            }
        }

        private void CheckTransferRequirements(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (!string.IsNullOrEmpty(context.SourceLocation) &&
                !string.IsNullOrEmpty(context.DestinationLocation) &&
                !context.SourceLocation.Equals(context.DestinationLocation, StringComparison.OrdinalIgnoreCase))
            {
                // Check if transfer mechanism is documented
                if (!context.Attributes.TryGetValue("TransferMechanism", out var mechanismObj) ||
                    mechanismObj is not string mechanism ||
                    string.IsNullOrEmpty(mechanism))
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "GDPR-012",
                        Description = $"Cross-border transfer from {context.SourceLocation} to {context.DestinationLocation} without documented transfer mechanism",
                        Severity = ViolationSeverity.High,
                        Remediation = "Document transfer mechanism (adequacy decision, SCCs, BCRs, or derogation)",
                        RegulatoryReference = "GDPR Chapter V"
                    });
                }
            }
        }
    
    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("gdpr.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("gdpr.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
