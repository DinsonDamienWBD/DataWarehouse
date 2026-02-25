using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.ISO
{
    /// <summary>
    /// ISO/IEC 27001:2022 Information Security Management System (ISMS) compliance strategy.
    /// Validates ISMS implementation against international standard for information security.
    /// </summary>
    /// <remarks>
    /// ISO/IEC 27001:2022 specifies requirements for establishing, implementing, maintaining
    /// and continually improving an information security management system.
    /// </remarks>
    public sealed class Iso27001Strategy : ComplianceStrategyBase
    {
        private readonly HashSet<string> _requiredClauses = new(StringComparer.OrdinalIgnoreCase)
        {
            "Context", "Leadership", "Planning", "Support", "Operation",
            "Performance Evaluation", "Improvement"
        };

        /// <inheritdoc/>
        public override string StrategyId => "iso27001";

        /// <inheritdoc/>
        public override string StrategyName => "ISO 27001:2022 ISMS";

        /// <inheritdoc/>
        public override string Framework => "ISO 27001";

        /// <inheritdoc/>
        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("iso27001.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            // Check ISMS establishment (Clause 4)
            CheckIsmsContext(context, violations, recommendations);

            // Check leadership and commitment (Clause 5)
            CheckLeadership(context, violations, recommendations);

            // Check risk assessment and treatment (Clause 6)
            CheckRiskManagement(context, violations, recommendations);

            // Check documented information (Clause 7)
            CheckDocumentation(context, violations, recommendations);

            // Check operational controls (Clause 8)
            CheckOperationalControls(context, violations, recommendations);

            // Check performance evaluation (Clause 9)
            CheckPerformanceEvaluation(context, violations, recommendations);

            // Check continual improvement (Clause 10)
            CheckContinualImprovement(context, violations, recommendations);

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

        private void CheckIsmsContext(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (!context.Attributes.TryGetValue("IsmsScope", out var scopeObj) || scopeObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "ISO27001-001",
                    Description = "ISMS scope not defined",
                    Severity = ViolationSeverity.Critical,
                    Remediation = "Define and document the scope and boundaries of the ISMS",
                    RegulatoryReference = "ISO 27001:2022 Clause 4.3"
                });
            }

            if (!context.Attributes.TryGetValue("StakeholderNeeds", out var stakeholderObj) || stakeholderObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "ISO27001-002",
                    Description = "Interested parties and their requirements not identified",
                    Severity = ViolationSeverity.High,
                    Remediation = "Identify interested parties and determine their information security requirements",
                    RegulatoryReference = "ISO 27001:2022 Clause 4.2"
                });
            }
        }

        private void CheckLeadership(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (!context.Attributes.TryGetValue("InformationSecurityPolicy", out var policyObj) || policyObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "ISO27001-003",
                    Description = "No documented information security policy",
                    Severity = ViolationSeverity.Critical,
                    Remediation = "Establish and document information security policy aligned with organizational objectives",
                    RegulatoryReference = "ISO 27001:2022 Clause 5.2"
                });
            }

            if (!context.Attributes.TryGetValue("RolesAndResponsibilities", out var rolesObj) || rolesObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "ISO27001-004",
                    Description = "Information security roles and responsibilities not assigned",
                    Severity = ViolationSeverity.High,
                    Remediation = "Assign and communicate information security roles and responsibilities",
                    RegulatoryReference = "ISO 27001:2022 Clause 5.3"
                });
            }
        }

        private void CheckRiskManagement(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (!context.Attributes.TryGetValue("RiskAssessmentProcess", out var riskAssessObj) || riskAssessObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "ISO27001-005",
                    Description = "No information security risk assessment process",
                    Severity = ViolationSeverity.Critical,
                    Remediation = "Establish and apply information security risk assessment process",
                    RegulatoryReference = "ISO 27001:2022 Clause 6.1.2"
                });
            }

            if (!context.Attributes.TryGetValue("RiskTreatmentPlan", out var treatmentObj) || treatmentObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "ISO27001-006",
                    Description = "No information security risk treatment plan",
                    Severity = ViolationSeverity.Critical,
                    Remediation = "Formulate information security risk treatment plan with controls from Annex A",
                    RegulatoryReference = "ISO 27001:2022 Clause 6.1.3"
                });
            }

            if (!context.Attributes.TryGetValue("StatementOfApplicability", out var soaObj) || soaObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "ISO27001-007",
                    Description = "No Statement of Applicability (SoA) for Annex A controls",
                    Severity = ViolationSeverity.Critical,
                    Remediation = "Prepare Statement of Applicability documenting necessary controls and justification for exclusions",
                    RegulatoryReference = "ISO 27001:2022 Clause 6.1.3(d)"
                });
            }
        }

        private void CheckDocumentation(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (!context.Attributes.TryGetValue("DocumentControl", out var docControlObj) || docControlObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "ISO27001-008",
                    Description = "No documented information control procedures",
                    Severity = ViolationSeverity.High,
                    Remediation = "Establish procedures for controlling documented information (creation, update, distribution)",
                    RegulatoryReference = "ISO 27001:2022 Clause 7.5"
                });
            }
        }

        private void CheckOperationalControls(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (context.Attributes.TryGetValue("AnnexAControls", out var controlsObj) &&
                controlsObj is IEnumerable<string> implementedControls)
            {
                var controlCount = implementedControls.Count();
                if (controlCount < 93) // ISO 27001:2022 has 93 controls in Annex A
                {
                    recommendations.Add($"Consider implementing additional Annex A controls (currently {controlCount}/93)");
                }
            }

            if (!context.Attributes.TryGetValue("ControlsOperational", out var operationalObj) || operationalObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "ISO27001-009",
                    Description = "Risk treatment controls not operational",
                    Severity = ViolationSeverity.High,
                    Remediation = "Ensure all selected risk treatment controls are implemented and operational",
                    RegulatoryReference = "ISO 27001:2022 Clause 8.1"
                });
            }
        }

        private void CheckPerformanceEvaluation(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (!context.Attributes.TryGetValue("PerformanceMonitoring", out var monitoringObj) || monitoringObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "ISO27001-010",
                    Description = "No ISMS performance monitoring and measurement",
                    Severity = ViolationSeverity.High,
                    Remediation = "Establish processes to monitor, measure, analyze and evaluate information security performance",
                    RegulatoryReference = "ISO 27001:2022 Clause 9.1"
                });
            }

            if (!context.Attributes.TryGetValue("InternalAudit", out var auditObj) || auditObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "ISO27001-011",
                    Description = "Internal ISMS audits not conducted",
                    Severity = ViolationSeverity.High,
                    Remediation = "Conduct internal audits at planned intervals to verify ISMS conformity",
                    RegulatoryReference = "ISO 27001:2022 Clause 9.2"
                });
            }

            if (!context.Attributes.TryGetValue("ManagementReview", out var reviewObj) || reviewObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "ISO27001-012",
                    Description = "Management review of ISMS not performed",
                    Severity = ViolationSeverity.High,
                    Remediation = "Conduct management review of ISMS at planned intervals",
                    RegulatoryReference = "ISO 27001:2022 Clause 9.3"
                });
            }
        }

        private void CheckContinualImprovement(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (!context.Attributes.TryGetValue("NonconformityProcess", out var ncObj) || ncObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "ISO27001-013",
                    Description = "No process for handling nonconformities and corrective actions",
                    Severity = ViolationSeverity.High,
                    Remediation = "Establish process to handle nonconformities and take corrective actions",
                    RegulatoryReference = "ISO 27001:2022 Clause 10.1"
                });
            }

            if (!context.Attributes.TryGetValue("ContinualImprovement", out var improvementObj) || improvementObj is not true)
            {
                recommendations.Add("Establish processes for continual improvement of ISMS suitability, adequacy and effectiveness");
            }
        }
    
    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("iso27001.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("iso27001.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
