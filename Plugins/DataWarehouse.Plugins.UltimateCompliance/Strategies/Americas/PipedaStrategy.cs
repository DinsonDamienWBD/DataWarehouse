using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.Americas
{
    /// <summary>
    /// PIPEDA (Personal Information Protection and Electronic Documents Act - Canada) compliance strategy.
    /// </summary>
    public sealed class PipedaStrategy : ComplianceStrategyBase
    {
        public override string StrategyId => "pipeda";
        public override string StrategyName => "PIPEDA Compliance";
        public override string Framework => "PIPEDA";

        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            if (!context.Attributes.TryGetValue("ConsentObtained", out var consentObj) || consentObj is not true)
            {
                violations.Add(new ComplianceViolation { Code = "PIPEDA-001", Description = "Consent not obtained for collection, use, or disclosure", Severity = ViolationSeverity.Critical, Remediation = "Obtain meaningful consent", RegulatoryReference = "PIPEDA Principle 4.3" });
            }

            if (!context.Attributes.TryGetValue("AccountabilityPolicy", out var accountObj) || accountObj is not true)
            {
                violations.Add(new ComplianceViolation { Code = "PIPEDA-002", Description = "Accountability policy not documented", Severity = ViolationSeverity.High, Remediation = "Implement accountability for personal information", RegulatoryReference = "PIPEDA Principle 4.1" });
            }

            if (!context.Attributes.TryGetValue("OpennessPolicy", out var openObj) || openObj is not true)
            {
                violations.Add(new ComplianceViolation { Code = "PIPEDA-003", Description = "Policies not made available to individuals", Severity = ViolationSeverity.Medium, Remediation = "Make policies and practices openly available", RegulatoryReference = "PIPEDA Principle 4.8" });
            }

            if (!context.Attributes.TryGetValue("AppropiateSafeguards", out var safeguardsObj) || safeguardsObj is not true)
            {
                violations.Add(new ComplianceViolation { Code = "PIPEDA-004", Description = "Safeguards not proportionate to sensitivity", Severity = ViolationSeverity.High, Remediation = "Implement appropriate security safeguards", RegulatoryReference = "PIPEDA Principle 4.7" });
            }

            var isCompliant = !violations.Any(v => v.Severity >= ViolationSeverity.High);
            var status = violations.Count == 0 ? ComplianceStatus.Compliant : violations.Any(v => v.Severity >= ViolationSeverity.High) ? ComplianceStatus.NonCompliant : ComplianceStatus.PartiallyCompliant;
            return Task.FromResult(new ComplianceResult { IsCompliant = isCompliant, Framework = Framework, Status = status, Violations = violations, Recommendations = recommendations });
        }
    }
}
