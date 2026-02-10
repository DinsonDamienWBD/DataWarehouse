using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.USState
{
    /// <summary>
    /// Delaware Personal Data Privacy Act compliance strategy.
    /// </summary>
    public sealed class DelawareStrategy : ComplianceStrategyBase
    {
        public override string StrategyId => "delaware-privacy";
        public override string StrategyName => "Delaware Personal Data Privacy Act Compliance";
        public override string Framework => "DELAWARE-PRIVACY";

        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            if (context.DataClassification.Equals("sensitive", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("ConsentObtained", out var consentObj) || consentObj is not true)
                {
                    violations.Add(new ComplianceViolation { Code = "DE-001", Description = "Sensitive data without consent", Severity = ViolationSeverity.Critical, Remediation = "Obtain consent", RegulatoryReference = "Delaware HB 154" });
                }
            }

            if (!context.Attributes.TryGetValue("PrivacyNoticeProvided", out var noticeObj) || noticeObj is not true)
            {
                violations.Add(new ComplianceViolation { Code = "DE-002", Description = "Privacy notice not provided", Severity = ViolationSeverity.High, Remediation = "Provide privacy notice", RegulatoryReference = "Delaware HB 154" });
            }

            if (context.OperationType.Equals("deletion-request", StringComparison.OrdinalIgnoreCase))
            {
                if (context.Attributes.TryGetValue("ResponseDays", out var daysObj) && daysObj is int days && days > 45)
                {
                    violations.Add(new ComplianceViolation { Code = "DE-003", Description = $"Request exceeded 45 days ({days} days)", Severity = ViolationSeverity.High, Remediation = "Respond within 45 days", RegulatoryReference = "Delaware HB 154" });
                }
            }

            var isCompliant = !violations.Any(v => v.Severity >= ViolationSeverity.High);
            var status = violations.Count == 0 ? ComplianceStatus.Compliant : violations.Any(v => v.Severity >= ViolationSeverity.High) ? ComplianceStatus.NonCompliant : ComplianceStatus.PartiallyCompliant;
            return Task.FromResult(new ComplianceResult { IsCompliant = isCompliant, Framework = Framework, Status = status, Violations = violations, Recommendations = recommendations });
        }
    }
}
