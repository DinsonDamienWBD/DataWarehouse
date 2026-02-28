using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.USState
{
    /// <summary>
    /// Oregon Consumer Privacy Act compliance strategy.
    /// </summary>
    public sealed class OregonStrategy : ComplianceStrategyBase
    {
        public override string StrategyId => "oregon-privacy";
        public override string StrategyName => "Oregon Consumer Privacy Act Compliance";
        public override string Framework => "OREGON-PRIVACY";

        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("oregon.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            if (!context.Attributes.TryGetValue("DataProtectionPractices", out var practicesObj) || practicesObj is not true)
            {
                violations.Add(new ComplianceViolation { Code = "OR-001", Description = "Data protection practices not documented", Severity = ViolationSeverity.Medium, Remediation = "Document data protection practices", RegulatoryReference = "Oregon SB 619" });
            }

            if (!string.IsNullOrEmpty(context.DataClassification) && context.DataClassification.Equals("sensitive", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("ConsentObtained", out var consentObj) || consentObj is not true)
                {
                    violations.Add(new ComplianceViolation { Code = "OR-002", Description = "Sensitive data without consent", Severity = ViolationSeverity.Critical, Remediation = "Obtain consent", RegulatoryReference = "Oregon SB 619" });
                }
            }

            if (!string.IsNullOrEmpty(context.OperationType) && context.OperationType.Equals("access-request", StringComparison.OrdinalIgnoreCase))
            {
                if (context.Attributes.TryGetValue("ResponseDays", out var daysObj) && daysObj is int days && days > 45)
                {
                    violations.Add(new ComplianceViolation { Code = "OR-003", Description = $"Request exceeded 45 days ({days} days)", Severity = ViolationSeverity.High, Remediation = "Respond within 45 days", RegulatoryReference = "Oregon SB 619" });
                }
            }

            var isCompliant = !violations.Any(v => v.Severity >= ViolationSeverity.High);
            var status = violations.Count == 0 ? ComplianceStatus.Compliant : violations.Any(v => v.Severity >= ViolationSeverity.High) ? ComplianceStatus.NonCompliant : ComplianceStatus.PartiallyCompliant;
            return Task.FromResult(new ComplianceResult { IsCompliant = isCompliant, Framework = Framework, Status = status, Violations = violations, Recommendations = recommendations });
        }
    
    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("oregon.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("oregon.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
