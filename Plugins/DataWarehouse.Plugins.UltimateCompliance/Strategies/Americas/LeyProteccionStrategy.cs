using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.Americas
{
    /// <summary>
    /// Latin American general data protection compliance strategy.
    /// </summary>
    public sealed class LeyProteccionStrategy : ComplianceStrategyBase
    {
        public override string StrategyId => "ley-proteccion";
        public override string StrategyName => "Latin American Data Protection Compliance";
        public override string Framework => "LEY-PROTECCION";

        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            if (!context.Attributes.TryGetValue("InformedConsent", out var consentObj) || consentObj is not true)
            {
                violations.Add(new ComplianceViolation { Code = "LEYPROT-001", Description = "Informed consent not obtained", Severity = ViolationSeverity.High, Remediation = "Obtain informed consent", RegulatoryReference = "General Latin American Principles" });
            }

            if (!context.Attributes.TryGetValue("PurposeSpecified", out var purposeObj) || purposeObj is not true)
            {
                violations.Add(new ComplianceViolation { Code = "LEYPROT-002", Description = "Processing purpose not specified", Severity = ViolationSeverity.Medium, Remediation = "Specify processing purpose", RegulatoryReference = "Purpose Limitation Principle" });
            }

            if (!context.Attributes.TryGetValue("DataSecurityMeasures", out var securityObj) || securityObj is not true)
            {
                violations.Add(new ComplianceViolation { Code = "LEYPROT-003", Description = "Data security measures not implemented", Severity = ViolationSeverity.High, Remediation = "Implement appropriate security measures", RegulatoryReference = "Security Principle" });
            }

            var isCompliant = !violations.Any(v => v.Severity >= ViolationSeverity.High);
            var status = violations.Count == 0 ? ComplianceStatus.Compliant : violations.Any(v => v.Severity >= ViolationSeverity.High) ? ComplianceStatus.NonCompliant : ComplianceStatus.PartiallyCompliant;
            return Task.FromResult(new ComplianceResult { IsCompliant = isCompliant, Framework = Framework, Status = status, Violations = violations, Recommendations = recommendations });
        }
    }
}
