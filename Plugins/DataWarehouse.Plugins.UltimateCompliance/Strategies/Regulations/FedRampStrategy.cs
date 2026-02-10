using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.Regulations
{
    /// <summary>
    /// FedRAMP (Federal Risk and Authorization Management Program) compliance strategy.
    /// Validates cloud services against federal security requirements.
    /// </summary>
    /// <remarks>
    /// FedRAMP provides standardized approach to security assessment, authorization,
    /// and continuous monitoring for cloud products and services used by US federal agencies.
    /// </remarks>
    public sealed class FedRampStrategy : ComplianceStrategyBase
    {
        private readonly HashSet<string> _validAuthorizationLevels = new(StringComparer.OrdinalIgnoreCase)
        {
            "Low", "Moderate", "High", "LI-SaaS"
        };

        /// <inheritdoc/>
        public override string StrategyId => "fedramp";

        /// <inheritdoc/>
        public override string StrategyName => "FedRAMP Compliance";

        /// <inheritdoc/>
        public override string Framework => "FedRAMP";

        /// <inheritdoc/>
        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            // Check authorization status
            CheckAuthorizationStatus(context, violations, recommendations);

            // Check NIST 800-53 controls
            CheckNist80053Controls(context, violations, recommendations);

            // Check continuous monitoring
            CheckContinuousMonitoring(context, violations, recommendations);

            // Check boundary documentation
            CheckBoundaryDocumentation(context, violations, recommendations);

            // Check security assessment
            CheckSecurityAssessment(context, violations, recommendations);

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

        private void CheckAuthorizationStatus(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (!context.Attributes.TryGetValue("FedRampAuthorization", out var authObj) ||
                authObj is not string authLevel ||
                !_validAuthorizationLevels.Contains(authLevel))
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "FEDRAMP-001",
                    Description = "No valid FedRAMP authorization level (Low, Moderate, High, or LI-SaaS)",
                    Severity = ViolationSeverity.Critical,
                    Remediation = "Obtain FedRAMP authorization at appropriate impact level through JAB or agency authorization",
                    RegulatoryReference = "FedRAMP Authorization Process"
                });
            }

            if (!context.Attributes.TryGetValue("AuthorizationDate", out var dateObj) ||
                dateObj is not DateTime authDate)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "FEDRAMP-002",
                    Description = "No documented authorization date",
                    Severity = ViolationSeverity.Medium,
                    Remediation = "Document FedRAMP authorization grant date",
                    RegulatoryReference = "FedRAMP Authorization Documentation"
                });
            }
            else if ((DateTime.UtcNow - authDate).TotalDays > 1095) // 3 years
            {
                recommendations.Add("FedRAMP authorization should be reassessed every 3 years");
            }
        }

        private void CheckNist80053Controls(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (!context.Attributes.TryGetValue("Nist80053Rev5", out var controlsObj) || controlsObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "FEDRAMP-003",
                    Description = "NIST 800-53 Rev 5 controls not fully implemented",
                    Severity = ViolationSeverity.Critical,
                    Remediation = "Implement all required NIST 800-53 Rev 5 security controls for your impact level",
                    RegulatoryReference = "NIST SP 800-53 Rev 5"
                });
            }

            if (context.Attributes.TryGetValue("FedRampAuthorization", out var authObj) && authObj is string authLevel)
            {
                int requiredControls = authLevel.ToLower() switch
                {
                    "low" => 125,
                    "moderate" => 325,
                    "high" => 421,
                    "li-saas" => 108,
                    _ => 0
                };

                if (context.Attributes.TryGetValue("ImplementedControls", out var implObj) &&
                    implObj is int implementedCount &&
                    implementedCount < requiredControls)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "FEDRAMP-004",
                        Description = $"Insufficient controls implemented: {implementedCount}/{requiredControls} for {authLevel} baseline",
                        Severity = ViolationSeverity.High,
                        Remediation = $"Implement all {requiredControls} required controls for FedRAMP {authLevel} baseline",
                        RegulatoryReference = $"FedRAMP {authLevel} Baseline"
                    });
                }
            }
        }

        private void CheckContinuousMonitoring(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (!context.Attributes.TryGetValue("ContinuousMonitoring", out var monitoringObj) || monitoringObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "FEDRAMP-005",
                    Description = "Continuous monitoring not implemented",
                    Severity = ViolationSeverity.Critical,
                    Remediation = "Implement continuous monitoring including monthly vulnerability scanning and annual assessments",
                    RegulatoryReference = "FedRAMP Continuous Monitoring Guide"
                });
            }

            if (!context.Attributes.TryGetValue("MonthlyVulnerabilityScan", out var scanObj) || scanObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "FEDRAMP-006",
                    Description = "Monthly vulnerability scanning not performed",
                    Severity = ViolationSeverity.High,
                    Remediation = "Conduct monthly authenticated vulnerability scans using approved scanning tools",
                    RegulatoryReference = "FedRAMP ConMon Requirements"
                });
            }
        }

        private void CheckBoundaryDocumentation(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (!context.Attributes.TryGetValue("AuthorizationBoundary", out var boundaryObj) || boundaryObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "FEDRAMP-007",
                    Description = "Authorization boundary not documented",
                    Severity = ViolationSeverity.High,
                    Remediation = "Document complete authorization boundary including all system components and connections",
                    RegulatoryReference = "FedRAMP System Security Plan"
                });
            }
        }

        private void CheckSecurityAssessment(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (!context.Attributes.TryGetValue("ThirdPartyAssessor", out var assessorObj) || assessorObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "FEDRAMP-008",
                    Description = "No FedRAMP-approved 3PAO assessment",
                    Severity = ViolationSeverity.Critical,
                    Remediation = "Engage FedRAMP-accredited Third Party Assessment Organization (3PAO) for security assessment",
                    RegulatoryReference = "FedRAMP 3PAO Requirements"
                });
            }

            if (!context.Attributes.TryGetValue("AnnualAssessment", out var annualObj) || annualObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "FEDRAMP-009",
                    Description = "Annual assessment not performed",
                    Severity = ViolationSeverity.High,
                    Remediation = "Conduct annual security assessment by 3PAO",
                    RegulatoryReference = "FedRAMP Annual Assessment"
                });
            }
        }
    }
}
