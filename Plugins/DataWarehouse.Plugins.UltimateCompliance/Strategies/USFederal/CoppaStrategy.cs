using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.USFederal
{
    /// <summary>
    /// COPPA (Children's Online Privacy Protection Act) compliance strategy.
    /// Validates children's privacy requirements including parental consent and data collection.
    /// </summary>
    public sealed class CoppaStrategy : ComplianceStrategyBase
    {
        /// <inheritdoc/>
        public override string StrategyId => "coppa";

        /// <inheritdoc/>
        public override string StrategyName => "COPPA Compliance";

        /// <inheritdoc/>
        public override string Framework => "COPPA";

        /// <inheritdoc/>
        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
        IncrementCounter("coppa.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            CheckParentalConsent(context, violations, recommendations);
            CheckDataCollection(context, violations, recommendations);
            CheckPrivacyNotice(context, violations, recommendations);
            CheckDataRetention(context, violations, recommendations);

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
                Recommendations = recommendations
            });
        }

        private void CheckParentalConsent(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (context.Attributes.TryGetValue("UserAge", out var ageObj) &&
                ageObj is int age && age < 13)
            {
                if (!context.Attributes.TryGetValue("VerifiableParentalConsent", out var consentObj) || consentObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "COPPA-001",
                        Description = "Personal information collected from child under 13 without verifiable parental consent",
                        Severity = ViolationSeverity.Critical,
                        Remediation = "Obtain verifiable parental consent before collecting child data",
                        RegulatoryReference = "COPPA 16 CFR 312.5"
                    });
                }

                if (context.Attributes.TryGetValue("ConsentMethod", out var methodObj) &&
                    methodObj is string method &&
                    method.Equals("email-only", StringComparison.OrdinalIgnoreCase))
                {
                    if (context.DataClassification.Equals("sensitive", StringComparison.OrdinalIgnoreCase))
                    {
                        violations.Add(new ComplianceViolation
                        {
                            Code = "COPPA-002",
                            Description = "Email-only consent insufficient for sensitive child data collection",
                            Severity = ViolationSeverity.High,
                            Remediation = "Use heightened consent method for sensitive data (credit card, video chat, etc.)",
                            RegulatoryReference = "COPPA 16 CFR 312.5(b)(2)"
                        });
                    }
                }
            }

            if (context.Attributes.TryGetValue("AgeVerification", out var verifyObj) && verifyObj is false)
            {
                recommendations.Add("Implement age screening mechanism to identify users under 13");
            }
        }

        private void CheckDataCollection(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (context.Attributes.TryGetValue("ChildDirectedService", out var childServiceObj) && childServiceObj is true)
            {
                if (!context.Attributes.TryGetValue("ActualKnowledgeCheck", out var knowledgeObj) || knowledgeObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "COPPA-003",
                        Description = "Child-directed service not checking for actual knowledge of child users",
                        Severity = ViolationSeverity.High,
                        Remediation = "Implement checks to avoid actual knowledge of child users without parental consent",
                        RegulatoryReference = "COPPA 16 CFR 312.2"
                    });
                }

                if (context.Attributes.TryGetValue("PersistentIdentifierUsed", out var persistentObj) && persistentObj is true)
                {
                    if (!context.Attributes.TryGetValue("ParentalConsent", out var consentObj) || consentObj is not true)
                    {
                        violations.Add(new ComplianceViolation
                        {
                            Code = "COPPA-004",
                            Description = "Persistent identifier used without parental consent",
                            Severity = ViolationSeverity.High,
                            Remediation = "Obtain parental consent before using cookies or tracking technologies",
                            RegulatoryReference = "COPPA 16 CFR 312.5"
                        });
                    }
                }
            }
        }

        private void CheckPrivacyNotice(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (context.Attributes.TryGetValue("ChildDirectedService", out var childServiceObj) && childServiceObj is true)
            {
                if (!context.Attributes.TryGetValue("PrivacyPolicyPosted", out var policyObj) || policyObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "COPPA-005",
                        Description = "COPPA-compliant privacy policy not posted",
                        Severity = ViolationSeverity.High,
                        Remediation = "Post clear privacy policy describing information practices for children",
                        RegulatoryReference = "COPPA 16 CFR 312.4"
                    });
                }

                if (!context.Attributes.TryGetValue("DirectNoticeToParent", out var directObj) || directObj is not true)
                {
                    recommendations.Add("Provide direct notice to parents before collecting child information");
                }
            }
        }

        private void CheckDataRetention(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (context.Attributes.TryGetValue("UserAge", out var ageObj) &&
                ageObj is int age && age < 13)
            {
                if (!context.Attributes.TryGetValue("RetentionPolicy", out var retentionObj) || retentionObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "COPPA-006",
                        Description = "No data retention policy for child information",
                        Severity = ViolationSeverity.Medium,
                        Remediation = "Delete child information when no longer needed for purpose collected",
                        RegulatoryReference = "COPPA 16 CFR 312.10"
                    });
                }

                if (context.OperationType.Equals("parent-deletion-request", StringComparison.OrdinalIgnoreCase))
                {
                    if (!context.Attributes.TryGetValue("RequestHonored", out var honoredObj) || honoredObj is not true)
                    {
                        violations.Add(new ComplianceViolation
                        {
                            Code = "COPPA-007",
                            Description = "Parental deletion request not honored",
                            Severity = ViolationSeverity.Critical,
                            Remediation = "Honor parent requests to delete child's personal information",
                            RegulatoryReference = "COPPA 16 CFR 312.6"
                        });
                    }
                }
            }
        }
    
    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        IncrementCounter("coppa.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
        IncrementCounter("coppa.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
