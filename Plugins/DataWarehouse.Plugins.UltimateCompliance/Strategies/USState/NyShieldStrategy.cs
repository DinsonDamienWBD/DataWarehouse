using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.USState
{
    /// <summary>
    /// NY SHIELD Act (Stop Hacks and Improve Electronic Data Security) compliance strategy.
    /// </summary>
    public sealed class NyShieldStrategy : ComplianceStrategyBase
    {
        public override string StrategyId => "ny-shield";
        public override string StrategyName => "NY SHIELD Act Compliance";
        public override string Framework => "NY-SHIELD";

        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            if (!context.Attributes.TryGetValue("DataSecurityProgram", out var programObj) || programObj is not true)
            {
                violations.Add(new ComplianceViolation { Code = "NYSHIELD-001", Description = "Data security program not implemented", Severity = ViolationSeverity.Critical, Remediation = "Implement reasonable data security safeguards", RegulatoryReference = "NY GBL § 899-bb" });
            }

            if (context.OperationType.Equals("data-breach", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("BreachNotificationSent", out var notifyObj) || notifyObj is not true)
                {
                    violations.Add(new ComplianceViolation { Code = "NYSHIELD-002", Description = "Breach notification not sent", Severity = ViolationSeverity.Critical, Remediation = "Notify affected individuals without unreasonable delay", RegulatoryReference = "NY GBL § 899-aa" });
                }
            }

            if (!context.Attributes.TryGetValue("EncryptionImplemented", out var encryptObj) || encryptObj is not true)
            {
                violations.Add(new ComplianceViolation { Code = "NYSHIELD-003", Description = "Private information not encrypted", Severity = ViolationSeverity.High, Remediation = "Encrypt private information", RegulatoryReference = "NY GBL § 899-bb" });
            }

            var isCompliant = !violations.Any(v => v.Severity >= ViolationSeverity.High);
            var status = violations.Count == 0 ? ComplianceStatus.Compliant : violations.Any(v => v.Severity >= ViolationSeverity.High) ? ComplianceStatus.NonCompliant : ComplianceStatus.PartiallyCompliant;
            return Task.FromResult(new ComplianceResult { IsCompliant = isCompliant, Framework = Framework, Status = status, Violations = violations, Recommendations = recommendations });
        }
    }
}
