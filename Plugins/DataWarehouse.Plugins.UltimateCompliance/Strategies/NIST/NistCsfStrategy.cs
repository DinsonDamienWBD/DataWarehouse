using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.NIST
{
    /// <summary>
    /// NIST Cybersecurity Framework 2.0 compliance strategy.
    /// Validates cybersecurity program against NIST CSF functions and categories.
    /// </summary>
    /// <remarks>
    /// NIST CSF 2.0 provides guidance for managing cybersecurity risk across
    /// six core functions: Govern, Identify, Protect, Detect, Respond, and Recover.
    /// </remarks>
    public sealed class NistCsfStrategy : ComplianceStrategyBase
    {
        private readonly HashSet<string> _coreFunctions = new(StringComparer.OrdinalIgnoreCase)
        {
            "Govern", "Identify", "Protect", "Detect", "Respond", "Recover"
        };

        /// <inheritdoc/>
        public override string StrategyId => "nist-csf";

        /// <inheritdoc/>
        public override string StrategyName => "NIST Cybersecurity Framework 2.0";

        /// <inheritdoc/>
        public override string Framework => "NIST CSF";

        /// <inheritdoc/>
        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            // Check Govern function
            CheckGovernFunction(context, violations, recommendations);

            // Check Identify function
            CheckIdentifyFunction(context, violations, recommendations);

            // Check Protect function
            CheckProtectFunction(context, violations, recommendations);

            // Check Detect function
            CheckDetectFunction(context, violations, recommendations);

            // Check Respond function
            CheckRespondFunction(context, violations, recommendations);

            // Check Recover function
            CheckRecoverFunction(context, violations, recommendations);

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

        private void CheckGovernFunction(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (!context.Attributes.TryGetValue("CybersecurityStrategy", out var strategyObj) || strategyObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "NIST-CSF-001",
                    Description = "No organizational cybersecurity strategy established",
                    Severity = ViolationSeverity.High,
                    Remediation = "Establish and document organizational cybersecurity strategy aligned with business objectives",
                    RegulatoryReference = "NIST CSF 2.0 - Govern"
                });
            }

            if (!context.Attributes.TryGetValue("RiskManagementStrategy", out var riskObj) || riskObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "NIST-CSF-002",
                    Description = "No cybersecurity risk management strategy",
                    Severity = ViolationSeverity.High,
                    Remediation = "Develop risk management strategy with risk tolerance and appetite",
                    RegulatoryReference = "NIST CSF 2.0 - GV.RM"
                });
            }

            if (!context.Attributes.TryGetValue("SupplyChainRiskManagement", out var scrmObj) || scrmObj is not true)
            {
                recommendations.Add("Implement supply chain risk management processes (GV.SC category)");
            }
        }

        private void CheckIdentifyFunction(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (!context.Attributes.TryGetValue("AssetInventory", out var assetObj) || assetObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "NIST-CSF-003",
                    Description = "No comprehensive asset inventory",
                    Severity = ViolationSeverity.High,
                    Remediation = "Maintain inventory of physical and logical assets including data, software, and hardware",
                    RegulatoryReference = "NIST CSF 2.0 - ID.AM"
                });
            }

            if (!context.Attributes.TryGetValue("RiskAssessment", out var riskAssessObj) || riskAssessObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "NIST-CSF-004",
                    Description = "Risk assessments not performed regularly",
                    Severity = ViolationSeverity.High,
                    Remediation = "Conduct regular risk assessments to identify threats and vulnerabilities",
                    RegulatoryReference = "NIST CSF 2.0 - ID.RA"
                });
            }
        }

        private void CheckProtectFunction(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (!context.Attributes.TryGetValue("AccessControl", out var acObj) || acObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "NIST-CSF-005",
                    Description = "Access control mechanisms not implemented",
                    Severity = ViolationSeverity.Critical,
                    Remediation = "Implement identity and access management with least privilege principle",
                    RegulatoryReference = "NIST CSF 2.0 - PR.AC"
                });
            }

            if (!context.Attributes.TryGetValue("DataSecurity", out var dataObj) || dataObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "NIST-CSF-006",
                    Description = "Data protection controls not implemented",
                    Severity = ViolationSeverity.Critical,
                    Remediation = "Implement data-at-rest and data-in-transit protection",
                    RegulatoryReference = "NIST CSF 2.0 - PR.DS"
                });
            }

            if (!context.Attributes.TryGetValue("SecurityAwareness", out var awarenessObj) || awarenessObj is not true)
            {
                recommendations.Add("Establish cybersecurity awareness and training program (PR.AT category)");
            }
        }

        private void CheckDetectFunction(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (!context.Attributes.TryGetValue("ContinuousMonitoring", out var monitoringObj) || monitoringObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "NIST-CSF-007",
                    Description = "Continuous security monitoring not implemented",
                    Severity = ViolationSeverity.High,
                    Remediation = "Implement continuous monitoring to detect cybersecurity events",
                    RegulatoryReference = "NIST CSF 2.0 - DE.CM"
                });
            }

            if (!context.Attributes.TryGetValue("AnomalyDetection", out var anomalyObj) || anomalyObj is not true)
            {
                recommendations.Add("Implement anomaly and event detection capabilities (DE.AE category)");
            }
        }

        private void CheckRespondFunction(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (!context.Attributes.TryGetValue("IncidentResponsePlan", out var responseObj) || responseObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "NIST-CSF-008",
                    Description = "No incident response plan",
                    Severity = ViolationSeverity.Critical,
                    Remediation = "Develop and maintain incident response plan with defined procedures",
                    RegulatoryReference = "NIST CSF 2.0 - RS.MA"
                });
            }

            if (!context.Attributes.TryGetValue("IncidentResponseTesting", out var testingObj) || testingObj is not true)
            {
                recommendations.Add("Test and update incident response procedures regularly (RS.MA category)");
            }
        }

        private void CheckRecoverFunction(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (!context.Attributes.TryGetValue("RecoveryPlanning", out var recoveryObj) || recoveryObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "NIST-CSF-009",
                    Description = "No recovery planning and processes",
                    Severity = ViolationSeverity.High,
                    Remediation = "Establish recovery planning processes to restore capabilities and services",
                    RegulatoryReference = "NIST CSF 2.0 - RC.RP"
                });
            }

            if (!context.Attributes.TryGetValue("BackupStrategy", out var backupObj) || backupObj is not true)
            {
                recommendations.Add("Implement comprehensive backup and restoration strategy (RC.CO category)");
            }
        }
    }
}
