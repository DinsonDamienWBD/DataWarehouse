using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.USFederal
{
    /// <summary>
    /// CMMC (Cybersecurity Maturity Model Certification) compliance strategy.
    /// Validates cybersecurity practices and processes for DoD contractors.
    /// </summary>
    public sealed class CmmcStrategy : ComplianceStrategyBase
    {
        /// <inheritdoc/>
        public override string StrategyId => "cmmc";

        /// <inheritdoc/>
        public override string StrategyName => "CMMC Compliance";

        /// <inheritdoc/>
        public override string Framework => "CMMC";

        /// <inheritdoc/>
        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("cmmc.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            CheckCmmcLevel(context, violations, recommendations);
            CheckAccessControl(context, violations, recommendations);
            CheckIncidentResponse(context, violations, recommendations);
            CheckAssessmentStatus(context, violations, recommendations);
            CheckCuiHandling(context, violations, recommendations);

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

        private void CheckCmmcLevel(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (!context.Attributes.TryGetValue("CmmcLevel", out var levelObj) ||
                levelObj is not int level ||
                level < 1)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "CMMC-001",
                    Description = "CMMC level not determined or documented",
                    Severity = ViolationSeverity.Critical,
                    Remediation = "Determine required CMMC level based on contract FCI/CUI requirements",
                    RegulatoryReference = "CMMC Model v2.0"
                });
                return;
            }

            if (level >= 2)
            {
                if (!context.Attributes.TryGetValue("Nist800171Implemented", out var nistObj) || nistObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "CMMC-002",
                        Description = "CMMC Level 2+ requires NIST 800-171 implementation",
                        Severity = ViolationSeverity.Critical,
                        Remediation = "Implement all NIST 800-171 controls for CUI protection",
                        RegulatoryReference = "CMMC Level 2 Requirements"
                    });
                }
            }

            if (level >= 3)
            {
                if (!context.Attributes.TryGetValue("AdvancedPractices", out var advObj) || advObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "CMMC-003",
                        Description = "CMMC Level 3 requires advanced/progressive practices",
                        Severity = ViolationSeverity.High,
                        Remediation = "Implement NIST 800-172 enhanced security requirements",
                        RegulatoryReference = "CMMC Level 3 Requirements"
                    });
                }
            }
        }

        private void CheckAccessControl(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (context.DataClassification.Equals("cui", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("MultiFactorAuth", out var mfaObj) || mfaObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "CMMC-004",
                        Description = "Multi-factor authentication not enforced for CUI access",
                        Severity = ViolationSeverity.Critical,
                        Remediation = "Implement MFA for all CUI system access",
                        RegulatoryReference = "CMMC AC.L2-3.1.12"
                    });
                }

                if (!context.Attributes.TryGetValue("LeastPrivilege", out var privilegeObj) || privilegeObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "CMMC-005",
                        Description = "Least privilege principle not enforced",
                        Severity = ViolationSeverity.High,
                        Remediation = "Implement least privilege access controls for CUI",
                        RegulatoryReference = "CMMC AC.L2-3.1.5"
                    });
                }
            }
        }

        private void CheckIncidentResponse(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (context.OperationType.Equals("security-incident", StringComparison.OrdinalIgnoreCase))
            {
                if (context.DataClassification.Equals("cui", StringComparison.OrdinalIgnoreCase))
                {
                    if (!context.Attributes.TryGetValue("DibcacReported", out var reportObj) || reportObj is not true)
                    {
                        violations.Add(new ComplianceViolation
                        {
                            Code = "CMMC-006",
                            Description = "CUI incident not reported to DIB CAC within 72 hours",
                            Severity = ViolationSeverity.Critical,
                            Remediation = "Report CUI incidents to Defense Industrial Base Cybersecurity Assessment Center",
                            RegulatoryReference = "DFARS 252.204-7012"
                        });
                    }
                }
            }

            if (!context.Attributes.TryGetValue("IncidentResponsePlan", out var planObj) || planObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "CMMC-007",
                    Description = "Incident response plan not documented",
                    Severity = ViolationSeverity.High,
                    Remediation = "Develop and maintain incident response plan",
                    RegulatoryReference = "CMMC IR.L2-3.6.1"
                });
            }
        }

        private void CheckAssessmentStatus(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (context.Attributes.TryGetValue("CmmcLevel", out var levelObj) && levelObj is int level && level >= 2)
            {
                if (!context.Attributes.TryGetValue("C3paoAssessment", out var assessObj) || assessObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "CMMC-008",
                        Description = "CMMC Level 2+ requires C3PAO assessment",
                        Severity = ViolationSeverity.Critical,
                        Remediation = "Complete assessment by CMMC Third Party Assessment Organization",
                        RegulatoryReference = "CMMC Assessment Guide"
                    });
                }
            }
        }

        private void CheckCuiHandling(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (context.DataClassification.Equals("cui", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("CuiMarked", out var markedObj) || markedObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "CMMC-009",
                        Description = "CUI not properly marked",
                        Severity = ViolationSeverity.Medium,
                        Remediation = "Mark all CUI with appropriate CUI markings",
                        RegulatoryReference = "CMMC MP.L2-3.8.2"
                    });
                }

                if (!context.Attributes.TryGetValue("EncryptionAtRest", out var encryptObj) || encryptObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "CMMC-010",
                        Description = "CUI not encrypted at rest",
                        Severity = ViolationSeverity.High,
                        Remediation = "Implement FIPS 140-2 validated encryption for CUI at rest",
                        RegulatoryReference = "CMMC SC.L2-3.13.11"
                    });
                }
            }
        }
    
    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("cmmc.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("cmmc.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
