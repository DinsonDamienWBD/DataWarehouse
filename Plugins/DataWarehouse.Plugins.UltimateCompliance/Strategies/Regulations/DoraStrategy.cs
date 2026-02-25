using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.Regulations
{
    /// <summary>
    /// DORA (Digital Operational Resilience Act) compliance strategy.
    /// Implements EU financial services operational resilience requirements.
    /// </summary>
    /// <remarks>
    /// Regulation (EU) 2022/2554 establishes requirements for digital operational resilience
    /// of financial entities including ICT risk management, incident reporting, and third-party risk management.
    /// </remarks>
    public sealed class DoraStrategy : ComplianceStrategyBase
    {
        /// <inheritdoc/>
        public override string StrategyId => "dora";

        /// <inheritdoc/>
        public override string StrategyName => "DORA Compliance";

        /// <inheritdoc/>
        public override string Framework => "DORA";

        /// <inheritdoc/>
        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("dora.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            // Check ICT risk management framework
            CheckIctRiskManagement(context, violations, recommendations);

            // Check incident reporting
            CheckIncidentReporting(context, violations, recommendations);

            // Check digital operational resilience testing
            CheckResilienceTesting(context, violations, recommendations);

            // Check third-party risk management
            CheckThirdPartyRiskManagement(context, violations, recommendations);

            // Check information sharing arrangements
            CheckInformationSharing(context, violations, recommendations);

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

        private void CheckIctRiskManagement(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (!context.Attributes.TryGetValue("IctRiskFramework", out var riskObj) || riskObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "DORA-001",
                    Description = "No ICT risk management framework established",
                    Severity = ViolationSeverity.Critical,
                    Remediation = "Implement comprehensive ICT risk management framework covering identification, protection, detection, response and recovery",
                    RegulatoryReference = "DORA Article 6"
                });
            }

            if (!context.Attributes.TryGetValue("BusinessContinuityPolicy", out var bcpObj) || bcpObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "DORA-002",
                    Description = "No business continuity policy and disaster recovery plans",
                    Severity = ViolationSeverity.High,
                    Remediation = "Establish ICT business continuity policy and disaster recovery plans",
                    RegulatoryReference = "DORA Article 11"
                });
            }
        }

        private void CheckIncidentReporting(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (!context.Attributes.TryGetValue("IncidentReportingProcess", out var incidentObj) || incidentObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "DORA-003",
                    Description = "No ICT incident reporting process",
                    Severity = ViolationSeverity.High,
                    Remediation = "Establish incident classification, reporting and response procedures within required timeframes",
                    RegulatoryReference = "DORA Article 17"
                });
            }

            if (!context.Attributes.TryGetValue("IncidentRegister", out var registerObj) || registerObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "DORA-004",
                    Description = "No incident register maintained",
                    Severity = ViolationSeverity.Medium,
                    Remediation = "Maintain comprehensive register of all ICT-related incidents",
                    RegulatoryReference = "DORA Article 19"
                });
            }
        }

        private void CheckResilienceTesting(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (!context.Attributes.TryGetValue("ResilienceTesting", out var testingObj) || testingObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "DORA-005",
                    Description = "No digital operational resilience testing program",
                    Severity = ViolationSeverity.High,
                    Remediation = "Establish testing program including scenario-based testing and threat-led penetration testing",
                    RegulatoryReference = "DORA Article 24"
                });
            }

            if (context.Attributes.TryGetValue("LastPenetrationTest", out var lastTestObj) &&
                lastTestObj is DateTime lastTest &&
                (DateTime.UtcNow - lastTest).TotalDays > 365)
            {
                recommendations.Add("Penetration testing should be conducted at least annually for significant entities");
            }
        }

        private void CheckThirdPartyRiskManagement(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (!context.Attributes.TryGetValue("ThirdPartyRiskManagement", out var thirdPartyObj) || thirdPartyObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "DORA-006",
                    Description = "No third-party ICT service provider risk management",
                    Severity = ViolationSeverity.Critical,
                    Remediation = "Implement comprehensive third-party ICT risk management framework including due diligence and contractual arrangements",
                    RegulatoryReference = "DORA Article 28"
                });
            }

            if (!context.Attributes.TryGetValue("ContractualArrangements", out var contractsObj) || contractsObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "DORA-007",
                    Description = "Contractual arrangements with ICT providers do not meet DORA requirements",
                    Severity = ViolationSeverity.High,
                    Remediation = "Ensure contracts include full description of services, locations, security requirements, and exit strategies",
                    RegulatoryReference = "DORA Article 30"
                });
            }
        }

        private void CheckInformationSharing(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (context.Attributes.TryGetValue("ParticipatesInInformationSharing", out var sharingObj) && sharingObj is false)
            {
                recommendations.Add("Consider participating in information sharing arrangements on cyber threats and vulnerabilities");
            }
        }
    
    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("dora.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("dora.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
