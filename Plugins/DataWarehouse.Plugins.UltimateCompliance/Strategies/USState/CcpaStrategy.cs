using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.USState
{
    /// <summary>
    /// CCPA (California Consumer Privacy Act) compliance strategy.
    /// Validates consumer rights, sale opt-out, and disclosure requirements.
    /// </summary>
    public sealed class CcpaStrategy : ComplianceStrategyBase
    {
        /// <inheritdoc/>
        public override string StrategyId => "ccpa";

        /// <inheritdoc/>
        public override string StrategyName => "CCPA Compliance";

        /// <inheritdoc/>
        public override string Framework => "CCPA";

        /// <inheritdoc/>
        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("ccpa.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            CheckConsumerRights(context, violations, recommendations);
            CheckSaleOptOut(context, violations, recommendations);
            CheckPrivacyNotice(context, violations, recommendations);
            CheckDataMinimization(context, violations, recommendations);

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
                Recommendations = recommendations
            });
        }

        private void CheckConsumerRights(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (!string.IsNullOrEmpty(context.OperationType) && context.OperationType.Equals("access-request", StringComparison.OrdinalIgnoreCase))
            {
                if (context.Attributes.TryGetValue("ResponseDays", out var daysObj) &&
                    daysObj is int days && days > 45)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "CCPA-001",
                        Description = $"Access request not fulfilled within 45 days ({days} days elapsed)",
                        Severity = ViolationSeverity.High,
                        Remediation = "Respond to consumer requests within 45 days",
                        RegulatoryReference = "CCPA 1798.130(a)(2)"
                    });
                }
            }

            if (!string.IsNullOrEmpty(context.OperationType) && context.OperationType.Equals("deletion-request", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("ServiceProvidersNotified", out var notifyObj) || notifyObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "CCPA-002",
                        Description = "Service providers not notified of deletion request",
                        Severity = ViolationSeverity.High,
                        Remediation = "Direct service providers to delete consumer information",
                        RegulatoryReference = "CCPA 1798.105(c)"
                    });
                }
            }
        }

        private void CheckSaleOptOut(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (!string.IsNullOrEmpty(context.OperationType) && context.OperationType.Equals("data-sale", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("DoNotSellLinkProvided", out var linkObj) || linkObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "CCPA-003",
                        Description = "Do Not Sell My Personal Information link not provided",
                        Severity = ViolationSeverity.Critical,
                        Remediation = "Provide clear Do Not Sell link on homepage and privacy policy",
                        RegulatoryReference = "CCPA 1798.135(a)"
                    });
                }

                if (context.Attributes.TryGetValue("OptOutRequested", out var optOutObj) && optOutObj is true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "CCPA-004",
                        Description = "Personal information sold despite opt-out request",
                        Severity = ViolationSeverity.Critical,
                        Remediation = "Honor consumer opt-out for at least 12 months",
                        RegulatoryReference = "CCPA 1798.135(a)(5)"
                    });
                }
            }

            if (context.Attributes.TryGetValue("Under16", out var ageObj) && ageObj is true)
            {
                if (!context.Attributes.TryGetValue("AffirmativeConsent", out var consentObj) || consentObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "CCPA-005",
                        Description = "Sale of minor's information without affirmative consent",
                        Severity = ViolationSeverity.Critical,
                        Remediation = "Obtain opt-in consent before selling information of consumers under 16",
                        RegulatoryReference = "CCPA 1798.120(c)"
                    });
                }
            }
        }

        private void CheckPrivacyNotice(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (!context.Attributes.TryGetValue("PrivacyPolicyPosted", out var policyObj) || policyObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "CCPA-006",
                    Description = "Privacy policy not posted or accessible",
                    Severity = ViolationSeverity.High,
                    Remediation = "Post comprehensive privacy policy with CCPA disclosures",
                    RegulatoryReference = "CCPA 1798.130(a)(5)"
                });
            }

            if (!context.Attributes.TryGetValue("CategoriesDisclosed", out var categoriesObj) || categoriesObj is not true)
            {
                recommendations.Add("Disclose categories of personal information collected and purposes");
            }
        }

        private void CheckDataMinimization(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (context.Attributes.TryGetValue("RetentionPeriodExceeded", out var retentionObj) && retentionObj is true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "CCPA-007",
                    Description = "Personal information retained beyond disclosed purpose",
                    Severity = ViolationSeverity.Medium,
                    Remediation = "Delete or deidentify information no longer needed",
                    RegulatoryReference = "CCPA 1798.100(c)"
                });
            }
        }
    
    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("ccpa.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("ccpa.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
