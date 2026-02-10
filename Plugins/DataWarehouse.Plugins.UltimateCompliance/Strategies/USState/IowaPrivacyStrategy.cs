using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.USState
{
    /// <summary>
    /// Iowa Privacy Act compliance strategy.
    /// </summary>
    public sealed class IowaPrivacyStrategy : ComplianceStrategyBase
    {
        public override string StrategyId => "iowa-privacy";
        public override string StrategyName => "Iowa Privacy Act Compliance";
        public override string Framework => "IOWA-PRIVACY";

        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            if (!context.Attributes.TryGetValue("PrivacyNoticeProvided", out var noticeObj) || noticeObj is not true)
            {
                violations.Add(new ComplianceViolation { Code = "IOWA-001", Description = "Privacy notice not provided", Severity = ViolationSeverity.High, Remediation = "Provide privacy notice to consumers", RegulatoryReference = "Iowa SF 262" });
            }

            if (context.OperationType.Equals("data-sale", StringComparison.OrdinalIgnoreCase))
            {
                if (context.Attributes.TryGetValue("OptOutRequested", out var optOutObj) && optOutObj is true)
                {
                    violations.Add(new ComplianceViolation { Code = "IOWA-002", Description = "Data sold despite opt-out", Severity = ViolationSeverity.Critical, Remediation = "Honor consumer opt-out", RegulatoryReference = "Iowa SF 262" });
                }
            }

            if (context.DataClassification.Equals("sensitive", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("ConsentObtained", out var consentObj) || consentObj is not true)
                {
                    violations.Add(new ComplianceViolation { Code = "IOWA-003", Description = "Sensitive data processing without consent", Severity = ViolationSeverity.Critical, Remediation = "Obtain consent for sensitive data", RegulatoryReference = "Iowa SF 262" });
                }
            }

            var isCompliant = !violations.Any(v => v.Severity >= ViolationSeverity.High);
            var status = violations.Count == 0 ? ComplianceStatus.Compliant : violations.Any(v => v.Severity >= ViolationSeverity.High) ? ComplianceStatus.NonCompliant : ComplianceStatus.PartiallyCompliant;
            return Task.FromResult(new ComplianceResult { IsCompliant = isCompliant, Framework = Framework, Status = status, Violations = violations, Recommendations = recommendations });
        }
    }
}
