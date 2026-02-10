using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.SecurityFrameworks
{
    /// <summary>
    /// CIS Controls v8 compliance strategy.
    /// </summary>
    public sealed class CisControlsStrategy : ComplianceStrategyBase
    {
        public override string StrategyId => "cis-controls";
        public override string StrategyName => "CIS Controls v8";
        public override string Framework => "CIS-CONTROLS";

        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            if (!context.Attributes.TryGetValue("AssetInventory", out var invObj) || invObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "CIS-01",
                    Description = "Enterprise asset inventory not maintained",
                    Severity = ViolationSeverity.High,
                    Remediation = "Maintain accurate inventory of enterprise assets (CIS Control 1)",
                    RegulatoryReference = "CIS Controls v8 - Control 1"
                });
            }

            if (!context.Attributes.TryGetValue("DataProtectionImplemented", out var dataObj) || dataObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "CIS-03",
                    Description = "Data protection controls not implemented",
                    Severity = ViolationSeverity.Critical,
                    Remediation = "Implement data classification, encryption, and DLP (CIS Control 3)",
                    RegulatoryReference = "CIS Controls v8 - Control 3"
                });
            }

            if (!context.Attributes.TryGetValue("SecureConfiguration", out var configObj) || configObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "CIS-04",
                    Description = "Secure configuration not established",
                    Severity = ViolationSeverity.High,
                    Remediation = "Implement secure configuration baselines for enterprise assets (CIS Control 4)",
                    RegulatoryReference = "CIS Controls v8 - Control 4"
                });
            }

            if (!context.Attributes.TryGetValue("AccessControlManagement", out var accessObj) || accessObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "CIS-06",
                    Description = "Access control management not implemented",
                    Severity = ViolationSeverity.High,
                    Remediation = "Implement account management and access control policies (CIS Control 6)",
                    RegulatoryReference = "CIS Controls v8 - Control 6"
                });
            }

            recommendations.Add("Align implementation with CIS Implementation Group based on organization size");

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
