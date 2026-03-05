using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.AsiaPacific
{
    /// <summary>
    /// India Digital Personal Data Protection Act (DPDP) compliance strategy.
    /// </summary>
    public sealed class PdpbStrategy : ComplianceStrategyBase
    {
        /// <inheritdoc/>
        public override string StrategyId => "pdpb-in";

        /// <inheritdoc/>
        public override string StrategyName => "India DPDP";

        /// <inheritdoc/>
        public override string Framework => "DPDP-IN";

        /// <inheritdoc/>
        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("pdpb.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            // Check consent (Section 6)
            if (!context.Attributes.TryGetValue("FreeInformedConsent", out var consentObj) || consentObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "DPDP-001",
                    Description = "Free, specific, informed, and unambiguous consent not obtained",
                    Severity = ViolationSeverity.Critical,
                    Remediation = "Obtain valid consent with clear affirmative action (Sec. 6)",
                    RegulatoryReference = "DPDP Act 2023 Section 6"
                });
            }

            // Check data fiduciary duties (Section 8)
            if (!context.Attributes.TryGetValue("DataFiduciaryDutiesComplied", out var dutiesObj) || dutiesObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "DPDP-002",
                    Description = "Data fiduciary duties not fulfilled",
                    Severity = ViolationSeverity.High,
                    Remediation = "Implement technical and organizational measures for data protection (Sec. 8)",
                    RegulatoryReference = "DPDP Act 2023 Section 8"
                });
            }

            // Check children's data (Section 9)
            if (context.DataSubjectCategories != null && context.DataSubjectCategories.Contains("children", StringComparer.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("VerifiableParentalConsent", out var parentalObj) || parentalObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "DPDP-003",
                        Description = "Verifiable parental consent not obtained for child's data",
                        Severity = ViolationSeverity.Critical,
                        Remediation = "Obtain verifiable parental consent before processing children's data (Sec. 9)",
                        RegulatoryReference = "DPDP Act 2023 Section 9"
                    });
                }
            }

            // Check cross-border transfer (Section 16)
            if (!string.IsNullOrEmpty(context.DestinationLocation) &&
                !context.DestinationLocation.StartsWith("IN", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("GovernmentApprovedCountry", out var approvedObj) || approvedObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "DPDP-004",
                        Description = "Cross-border transfer to non-approved country",
                        Severity = ViolationSeverity.High,
                        Remediation = "Transfer data only to government-notified countries (Sec. 16)",
                        RegulatoryReference = "DPDP Act 2023 Section 16"
                    });
                }
            }

            // Check data breach notification (Section 8(6))
            if (context.Attributes.TryGetValue("DataBreachOccurred", out var breachObj) && breachObj is true)
            {
                if (!context.Attributes.TryGetValue("BoardAndUsersNotified", out var notifyObj) || notifyObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "DPDP-005",
                        Description = "Data breach not notified to Board and affected users",
                        Severity = ViolationSeverity.Critical,
                        Remediation = "Notify Data Protection Board and users promptly after breach (Sec. 8(6))",
                        RegulatoryReference = "DPDP Act 2023 Section 8(6)"
                    });
                }
            }

            recommendations.Add("Appoint Data Protection Officer if processing significant volumes of personal data");

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
    
    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("pdpb.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("pdpb.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
