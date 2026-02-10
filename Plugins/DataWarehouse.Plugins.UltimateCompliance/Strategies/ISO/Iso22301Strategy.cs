using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.ISO
{
    /// <summary>
    /// ISO 22301 Business Continuity Management System compliance strategy.
    /// </summary>
    public sealed class Iso22301Strategy : ComplianceStrategyBase
    {
        /// <inheritdoc/>
        public override string StrategyId => "iso22301";

        /// <inheritdoc/>
        public override string StrategyName => "ISO 22301";

        /// <inheritdoc/>
        public override string Framework => "ISO22301";

        /// <inheritdoc/>
        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            // Check Business Impact Analysis (8.2.2)
            if (!context.Attributes.TryGetValue("BiaCompleted", out var biaObj) || biaObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "ISO22301-001",
                    Description = "Business Impact Analysis not completed for critical resources",
                    Severity = ViolationSeverity.High,
                    Remediation = "Conduct BIA to identify recovery priorities and time objectives",
                    RegulatoryReference = "ISO 22301:2019 8.2.2"
                });
            }

            // Check BCM policy (5.3)
            if (!context.Attributes.TryGetValue("BcmPolicyDefined", out var policyObj) || policyObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "ISO22301-002",
                    Description = "Business Continuity Management policy not established",
                    Severity = ViolationSeverity.High,
                    Remediation = "Establish, document, and communicate BCM policy with management commitment",
                    RegulatoryReference = "ISO 22301:2019 5.3"
                });
            }

            // Check backup and recovery (8.4.4)
            if (context.DataClassification.Equals("critical", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("BackupConfigured", out var backupObj) || backupObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "ISO22301-003",
                        Description = "Backup procedures not configured for critical data",
                        Severity = ViolationSeverity.Critical,
                        Remediation = "Implement automated backup with defined RTO and RPO",
                        RegulatoryReference = "ISO 22301:2019 8.4.4"
                    });
                }
            }

            // Check exercise and testing (8.5)
            if (!context.Attributes.TryGetValue("BcpTestDate", out var testObj) ||
                testObj is not DateTime testDate ||
                (DateTime.UtcNow - testDate).TotalDays > 365)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "ISO22301-004",
                    Description = "Business continuity plan not tested within the last 12 months",
                    Severity = ViolationSeverity.Medium,
                    Remediation = "Conduct annual BCP exercises and document results",
                    RegulatoryReference = "ISO 22301:2019 8.5"
                });
            }

            // Check maintenance and review (10.2)
            if (!context.Attributes.TryGetValue("BcmsReviewDate", out var reviewObj) ||
                reviewObj is not DateTime reviewDate ||
                (DateTime.UtcNow - reviewDate).TotalDays > 180)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "ISO22301-005",
                    Description = "BCMS management review overdue",
                    Severity = ViolationSeverity.Medium,
                    Remediation = "Conduct semi-annual management review of BCMS effectiveness",
                    RegulatoryReference = "ISO 22301:2019 10.2"
                });
            }

            recommendations.Add("Ensure RTO and RPO metrics are defined and monitored for critical business processes");

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
