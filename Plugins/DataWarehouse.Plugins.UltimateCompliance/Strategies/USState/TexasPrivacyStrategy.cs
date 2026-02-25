using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.USState
{
    /// <summary>
    /// Texas Data Privacy and Security Act compliance strategy.
    /// </summary>
    public sealed class TexasPrivacyStrategy : ComplianceStrategyBase
    {
        public override string StrategyId => "texas-privacy";
        public override string StrategyName => "Texas Data Privacy and Security Act Compliance";
        public override string Framework => "TEXAS-PRIVACY";

        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("texas_privacy.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            if (!context.Attributes.TryGetValue("PrivacyNoticeProvided", out var noticeObj) || noticeObj is not true)
            {
                violations.Add(new ComplianceViolation { Code = "TX-001", Description = "Privacy notice not provided", Severity = ViolationSeverity.High, Remediation = "Provide privacy notice", RegulatoryReference = "Texas HB 4" });
            }

            if (context.DataClassification.Equals("sensitive", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("ConsentObtained", out var consentObj) || consentObj is not true)
                {
                    violations.Add(new ComplianceViolation { Code = "TX-002", Description = "Sensitive data without consent", Severity = ViolationSeverity.Critical, Remediation = "Obtain consent", RegulatoryReference = "Texas HB 4" });
                }
            }

            if (context.OperationType.Equals("deletion-request", StringComparison.OrdinalIgnoreCase))
            {
                if (context.Attributes.TryGetValue("ResponseDays", out var daysObj) && daysObj is int days && days > 45)
                {
                    violations.Add(new ComplianceViolation { Code = "TX-003", Description = $"Deletion request exceeded 45 days ({days} days)", Severity = ViolationSeverity.High, Remediation = "Respond within 45 days", RegulatoryReference = "Texas HB 4" });
                }
            }

            var isCompliant = !violations.Any(v => v.Severity >= ViolationSeverity.High);
            var status = violations.Count == 0 ? ComplianceStatus.Compliant : violations.Any(v => v.Severity >= ViolationSeverity.High) ? ComplianceStatus.NonCompliant : ComplianceStatus.PartiallyCompliant;
            return Task.FromResult(new ComplianceResult { IsCompliant = isCompliant, Framework = Framework, Status = status, Violations = violations, Recommendations = recommendations });
        }
    
    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("texas_privacy.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("texas_privacy.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
