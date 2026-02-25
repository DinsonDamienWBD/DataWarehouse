using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.Industry
{
    /// <summary>
    /// NERC CIP (Critical Infrastructure Protection) compliance strategy for energy sector.
    /// </summary>
    public sealed class NercCipStrategy : ComplianceStrategyBase
    {
        public override string StrategyId => "nerc-cip";
        public override string StrategyName => "NERC CIP";
        public override string Framework => "NERC-CIP";

        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("nerc_cip.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            bool isBES = context.Attributes.TryGetValue("IsBulkElectricSystem", out var besObj) && besObj is true;

            if (!isBES)
            {
                return Task.FromResult(new ComplianceResult
                {
                    IsCompliant = true,
                    Framework = Framework,
                    Status = ComplianceStatus.NotApplicable,
                    Violations = violations,
                    Recommendations = new List<string> { "NERC CIP applies to Bulk Electric System critical assets" }
                });
            }

            if (!context.Attributes.TryGetValue("CyberSecurityPlan", out var planObj) || planObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "CIP-003",
                    Description = "Cyber security plan not documented",
                    Severity = ViolationSeverity.Critical,
                    Remediation = "Document cyber security plan with policies and procedures (CIP-003)",
                    RegulatoryReference = "NERC CIP-003"
                });
            }

            if (!context.Attributes.TryGetValue("ElectronicSecurityPerimeter", out var espObj) || espObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "CIP-005",
                    Description = "Electronic Security Perimeter not established",
                    Severity = ViolationSeverity.Critical,
                    Remediation = "Define and protect Electronic Security Perimeter (CIP-005)",
                    RegulatoryReference = "NERC CIP-005"
                });
            }

            if (!context.Attributes.TryGetValue("IncidentReporting", out var incObj) || incObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "CIP-008",
                    Description = "Cyber security incident response plan not implemented",
                    Severity = ViolationSeverity.High,
                    Remediation = "Develop incident response and reporting procedures (CIP-008)",
                    RegulatoryReference = "NERC CIP-008"
                });
            }

            if (!context.Attributes.TryGetValue("PersonnelRiskAssessment", out var praObj) || praObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "CIP-004",
                    Description = "Personnel risk assessment not conducted",
                    Severity = ViolationSeverity.Medium,
                    Remediation = "Conduct background checks and training for personnel with BES access (CIP-004)",
                    RegulatoryReference = "NERC CIP-004"
                });
            }

            recommendations.Add("Conduct regular CIP compliance audits and maintain evidence");

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
    
    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("nerc_cip.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("nerc_cip.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
