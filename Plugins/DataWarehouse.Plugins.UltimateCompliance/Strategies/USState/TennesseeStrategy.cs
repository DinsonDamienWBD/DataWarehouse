using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.USState
{
    /// <summary>
    /// Tennessee Information Protection Act compliance strategy.
    /// </summary>
    public sealed class TennesseeStrategy : ComplianceStrategyBase
    {
        public override string StrategyId => "tennessee-privacy";
        public override string StrategyName => "Tennessee Information Protection Act Compliance";
        public override string Framework => "TENNESSEE-PRIVACY";

        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            if (!context.Attributes.TryGetValue("DataProtectionAssessment", out var dpaObj) || dpaObj is not true)
            {
                violations.Add(new ComplianceViolation { Code = "TENN-001", Description = "Data protection assessment not conducted", Severity = ViolationSeverity.Medium, Remediation = "Conduct data protection assessment", RegulatoryReference = "Tennessee HB 1181" });
            }

            if (context.DataClassification.Equals("sensitive", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("ConsentObtained", out var consentObj) || consentObj is not true)
                {
                    violations.Add(new ComplianceViolation { Code = "TENN-002", Description = "Sensitive data without consent", Severity = ViolationSeverity.Critical, Remediation = "Obtain consent for sensitive data", RegulatoryReference = "Tennessee HB 1181" });
                }
            }

            if (context.Attributes.TryGetValue("DeidentificationRequired", out var deidentObj) && deidentObj is true)
            {
                if (!context.Attributes.TryGetValue("DeidentificationCompleted", out var completedObj) || completedObj is not true)
                {
                    violations.Add(new ComplianceViolation { Code = "TENN-003", Description = "Data not deidentified as required", Severity = ViolationSeverity.High, Remediation = "Deidentify data per requirements", RegulatoryReference = "Tennessee HB 1181" });
                }
            }

            var isCompliant = !violations.Any(v => v.Severity >= ViolationSeverity.High);
            var status = violations.Count == 0 ? ComplianceStatus.Compliant : violations.Any(v => v.Severity >= ViolationSeverity.High) ? ComplianceStatus.NonCompliant : ComplianceStatus.PartiallyCompliant;
            return Task.FromResult(new ComplianceResult { IsCompliant = isCompliant, Framework = Framework, Status = status, Violations = violations, Recommendations = recommendations });
        }
    }
}
