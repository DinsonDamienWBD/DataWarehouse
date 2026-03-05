using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.USFederal
{
    /// <summary>
    /// GLBA (Gramm-Leach-Bliley Act) compliance strategy.
    /// Validates financial privacy safeguards and notice requirements.
    /// </summary>
    public sealed class GlbaStrategy : ComplianceStrategyBase
    {
        /// <inheritdoc/>
        public override string StrategyId => "glba";

        /// <inheritdoc/>
        public override string StrategyName => "GLBA Compliance";

        /// <inheritdoc/>
        public override string Framework => "GLBA";

        /// <inheritdoc/>
        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("glba.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            CheckSafeguardsRule(context, violations, recommendations);
            CheckPrivacyNotices(context, violations, recommendations);
            CheckPretextingProtection(context, violations, recommendations);
            CheckOptOutRights(context, violations, recommendations);

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

        private void CheckSafeguardsRule(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (!string.IsNullOrEmpty(context.DataClassification) && context.DataClassification.Equals("financial", StringComparison.OrdinalIgnoreCase) ||
                (context.DataSubjectCategories != null && context.DataSubjectCategories.Contains("customer-financial-info", StringComparer.OrdinalIgnoreCase)))
            {
                if (!context.Attributes.TryGetValue("InformationSecurityProgram", out var ispObj) || ispObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "GLBA-001",
                        Description = "No written information security program in place",
                        Severity = ViolationSeverity.Critical,
                        Remediation = "Develop comprehensive written information security program",
                        RegulatoryReference = "GLBA Safeguards Rule 16 CFR 314.4"
                    });
                }

                if (!context.Attributes.TryGetValue("RiskAssessmentConducted", out var riskObj) || riskObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "GLBA-002",
                        Description = "Risk assessment of customer information not conducted",
                        Severity = ViolationSeverity.High,
                        Remediation = "Conduct periodic risk assessments of customer information systems",
                        RegulatoryReference = "GLBA Safeguards Rule 16 CFR 314.4(b)"
                    });
                }

                if (!context.Attributes.TryGetValue("EncryptionImplemented", out var encryptObj) || encryptObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "GLBA-003",
                        Description = "Customer information not encrypted",
                        Severity = ViolationSeverity.High,
                        Remediation = "Encrypt customer information at rest and in transit",
                        RegulatoryReference = "GLBA Safeguards Rule 16 CFR 314.4(c)"
                    });
                }
            }
        }

        private void CheckPrivacyNotices(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (!string.IsNullOrEmpty(context.OperationType) && context.OperationType.Equals("customer-onboarding", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("InitialPrivacyNotice", out var initialObj) || initialObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "GLBA-004",
                        Description = "Initial privacy notice not provided to customer",
                        Severity = ViolationSeverity.High,
                        Remediation = "Provide clear privacy notice at customer relationship establishment",
                        RegulatoryReference = "GLBA Privacy Rule 16 CFR 313.4"
                    });
                }
            }

            if (context.Attributes.TryGetValue("PolicyChanged", out var changedObj) && changedObj is true)
            {
                if (!context.Attributes.TryGetValue("AnnualNoticeProvided", out var annualObj) || annualObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "GLBA-005",
                        Description = "Annual privacy notice not sent after policy change",
                        Severity = ViolationSeverity.Medium,
                        Remediation = "Send annual privacy notice when sharing policies change",
                        RegulatoryReference = "GLBA Privacy Rule 16 CFR 313.5"
                    });
                }
            }
        }

        private void CheckPretextingProtection(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (!context.Attributes.TryGetValue("PretextingControls", out var controlsObj) || controlsObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "GLBA-006",
                    Description = "Pretexting protection controls not implemented",
                    Severity = ViolationSeverity.Medium,
                    Remediation = "Implement controls to detect and prevent pretexting attempts",
                    RegulatoryReference = "GLBA Section 521"
                });
            }

            if (!string.IsNullOrEmpty(context.OperationType) && context.OperationType.Equals("information-request", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("CallerIdentityVerified", out var verifyObj) || verifyObj is not true)
                {
                    recommendations.Add("Verify caller identity before disclosing customer financial information");
                }
            }
        }

        private void CheckOptOutRights(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (!string.IsNullOrEmpty(context.OperationType) && context.OperationType.Equals("third-party-sharing", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("OptOutNotice", out var noticeObj) || noticeObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "GLBA-007",
                        Description = "Opt-out notice not provided before nonaffiliated third-party sharing",
                        Severity = ViolationSeverity.High,
                        Remediation = "Provide opt-out notice with reasonable opportunity to opt out",
                        RegulatoryReference = "GLBA Privacy Rule 16 CFR 313.7"
                    });
                }

                if (context.Attributes.TryGetValue("OptOutRequested", out var requestObj) && requestObj is true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "GLBA-008",
                        Description = "Information shared despite customer opt-out",
                        Severity = ViolationSeverity.Critical,
                        Remediation = "Honor all customer opt-out requests immediately",
                        RegulatoryReference = "GLBA Privacy Rule 16 CFR 313.7"
                    });
                }
            }
        }
    
    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("glba.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("glba.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
