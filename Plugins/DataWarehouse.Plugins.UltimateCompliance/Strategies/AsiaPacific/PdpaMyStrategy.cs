using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.AsiaPacific
{
    /// <summary>
    /// Malaysia Personal Data Protection Act compliance strategy.
    /// </summary>
    public sealed class PdpaMyStrategy : ComplianceStrategyBase
    {
        public override string StrategyId => "pdpa-my";
        public override string StrategyName => "Malaysia PDPA";
        public override string Framework => "PDPA-MY";

        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            if (!context.Attributes.TryGetValue("NoticeProvided", out var noticeObj) || noticeObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "PDPA-MY-007",
                    Description = "Personal data notice not provided to data subject",
                    Severity = ViolationSeverity.High,
                    Remediation = "Provide notice about processing purposes and data user identity (Sec. 7)",
                    RegulatoryReference = "PDPA Malaysia Section 7"
                });
            }

            if (!context.Attributes.TryGetValue("ConsentObtained", out var consentObj) || consentObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "PDPA-MY-006",
                    Description = "Consent not obtained for personal data processing",
                    Severity = ViolationSeverity.Critical,
                    Remediation = "Obtain consent unless exemption applies (Sec. 6)",
                    RegulatoryReference = "PDPA Malaysia Section 6"
                });
            }

            if (!context.Attributes.TryGetValue("DisclosureComplies", out var discObj) || discObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "PDPA-MY-008",
                    Description = "Personal data disclosed without lawful basis",
                    Severity = ViolationSeverity.High,
                    Remediation = "Disclose personal data only with consent or legal exemption (Sec. 8)",
                    RegulatoryReference = "PDPA Malaysia Section 8"
                });
            }

            if (!context.Attributes.TryGetValue("SecurityArrangements", out var secObj) || secObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "PDPA-MY-010",
                    Description = "Security arrangements not in place to protect personal data",
                    Severity = ViolationSeverity.High,
                    Remediation = "Implement security safeguards against loss, misuse, and unauthorized access (Sec. 10)",
                    RegulatoryReference = "PDPA Malaysia Section 10"
                });
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
                Recommendations = recommendations
            });
        }
    }
}
