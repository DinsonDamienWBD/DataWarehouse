using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.SecurityFrameworks
{
    /// <summary>
    /// CSA STAR (Security, Trust, Assurance and Risk) cloud security compliance strategy.
    /// </summary>
    public sealed class CsaStarStrategy : ComplianceStrategyBase
    {
        public override string StrategyId => "csa-star";
        public override string StrategyName => "CSA STAR";
        public override string Framework => "CSA-STAR";

        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            if (!context.Attributes.TryGetValue("CcmControlsImplemented", out var ccmObj) || ccmObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "CSA-CCM",
                    Description = "Cloud Controls Matrix (CCM) controls not implemented",
                    Severity = ViolationSeverity.Critical,
                    Remediation = "Implement CSA CCM v4 controls across 17 domains",
                    RegulatoryReference = "CSA Cloud Controls Matrix"
                });
            }

            if (!context.Attributes.TryGetValue("StarMaturityLevel", out var maturityObj))
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "CSA-MATURITY",
                    Description = "CSA STAR maturity level not assessed",
                    Severity = ViolationSeverity.Medium,
                    Remediation = "Assess cloud security maturity using STAR Maturity Model",
                    RegulatoryReference = "CSA STAR Maturity Model"
                });
            }

            if (!context.Attributes.TryGetValue("CaraiPublished", out var caraiObj) || caraiObj is not true)
            {
                recommendations.Add("Publish Consensus Assessments Initiative Questionnaire (CAIQ) to STAR registry");
            }

            if (!context.Attributes.TryGetValue("SharedResponsibilityModel", out var srmObj) || srmObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "CSA-SRM",
                    Description = "Shared responsibility model not documented",
                    Severity = ViolationSeverity.High,
                    Remediation = "Document shared security responsibilities between provider and customer",
                    RegulatoryReference = "CSA Reference Architecture"
                });
            }

            recommendations.Add("Consider STAR Level 2 certification for independent assessment");

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
