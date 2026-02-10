using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.USFederal
{
    /// <summary>
    /// DFARS 252.204-7012 compliance strategy.
    /// Validates defense contractor cybersecurity requirements for CUI protection.
    /// </summary>
    public sealed class Dfars252Strategy : ComplianceStrategyBase
    {
        /// <inheritdoc/>
        public override string StrategyId => "dfars-252-204-7012";

        /// <inheritdoc/>
        public override string StrategyName => "DFARS 252.204-7012 Compliance";

        /// <inheritdoc/>
        public override string Framework => "DFARS-252";

        /// <inheritdoc/>
        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            CheckNist800171Compliance(context, violations, recommendations);
            CheckCyberIncidentReporting(context, violations, recommendations);
            CheckCloudRequirements(context, violations, recommendations);
            CheckSubcontractorFlow(context, violations, recommendations);

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

        private void CheckNist800171Compliance(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (context.DataClassification.Equals("cui", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("Nist800171Implemented", out var nistObj) || nistObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "DFARS252-001",
                        Description = "NIST 800-171 security requirements not implemented for CUI",
                        Severity = ViolationSeverity.Critical,
                        Remediation = "Implement all applicable NIST 800-171 Rev 2 security requirements",
                        RegulatoryReference = "DFARS 252.204-7012(b)"
                    });
                }

                if (context.Attributes.TryGetValue("PoamExists", out var poamObj) && poamObj is true)
                {
                    if (!context.Attributes.TryGetValue("PoamSubmitted", out var submittedObj) || submittedObj is not true)
                    {
                        violations.Add(new ComplianceViolation
                        {
                            Code = "DFARS252-002",
                            Description = "Plan of Action and Milestones (POA&M) for NIST 800-171 gaps not submitted",
                            Severity = ViolationSeverity.High,
                            Remediation = "Submit POA&M to Supplier Performance Risk System (SPRS)",
                            RegulatoryReference = "DFARS 252.204-7012(c)"
                        });
                    }
                }

                if (!context.Attributes.TryGetValue("SprsScorePosted", out var sprsObj) || sprsObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "DFARS252-003",
                        Description = "NIST 800-171 assessment score not posted in SPRS",
                        Severity = ViolationSeverity.High,
                        Remediation = "Post current assessment score in Supplier Performance Risk System",
                        RegulatoryReference = "DFARS 252.204-7012(d)"
                    });
                }
            }
        }

        private void CheckCyberIncidentReporting(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (context.OperationType.Equals("security-incident", StringComparison.OrdinalIgnoreCase) ||
                context.OperationType.Equals("cyber-incident", StringComparison.OrdinalIgnoreCase))
            {
                if (context.DataClassification.Equals("cui", StringComparison.OrdinalIgnoreCase))
                {
                    if (!context.Attributes.TryGetValue("IncidentReportedWithin72Hours", out var reportedObj) || reportedObj is not true)
                    {
                        violations.Add(new ComplianceViolation
                        {
                            Code = "DFARS252-004",
                            Description = "Cyber incident affecting CUI not reported within 72 hours",
                            Severity = ViolationSeverity.Critical,
                            Remediation = "Report cyber incidents to DoD at https://dibnet.dod.mil within 72 hours",
                            RegulatoryReference = "DFARS 252.204-7012(c)(1)"
                        });
                    }

                    if (!context.Attributes.TryGetValue("MediaPreserved", out var mediaObj) || mediaObj is not true)
                    {
                        violations.Add(new ComplianceViolation
                        {
                            Code = "DFARS252-005",
                            Description = "Affected media not preserved for incident review",
                            Severity = ViolationSeverity.High,
                            Remediation = "Preserve and protect images of affected systems for 90 days",
                            RegulatoryReference = "DFARS 252.204-7012(c)(1)(ii)(B)"
                        });
                    }

                    if (context.Attributes.TryGetValue("MalwareDetected", out var malwareObj) && malwareObj is true)
                    {
                        if (!context.Attributes.TryGetValue("MalwareSubmitted", out var submittedObj) || submittedObj is not true)
                        {
                            recommendations.Add("Submit malicious software to DoD Cyber Crime Center");
                        }
                    }
                }
            }
        }

        private void CheckCloudRequirements(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (context.Attributes.TryGetValue("CloudStorage", out var cloudObj) && cloudObj is true)
            {
                if (context.DataClassification.Equals("cui", StringComparison.OrdinalIgnoreCase))
                {
                    if (!context.Attributes.TryGetValue("FedRampModerate", out var fedrampObj) || fedrampObj is not true)
                    {
                        violations.Add(new ComplianceViolation
                        {
                            Code = "DFARS252-006",
                            Description = "CUI stored in cloud without FedRAMP Moderate authorization",
                            Severity = ViolationSeverity.Critical,
                            Remediation = "Use only FedRAMP Moderate or DoD IL4+ authorized cloud providers",
                            RegulatoryReference = "DFARS 252.204-7012(b)(2)"
                        });
                    }

                    if (!string.IsNullOrEmpty(context.DestinationLocation) &&
                        !context.DestinationLocation.Equals("US", StringComparison.OrdinalIgnoreCase))
                    {
                        violations.Add(new ComplianceViolation
                        {
                            Code = "DFARS252-007",
                            Description = $"CUI processed/stored outside United States: {context.DestinationLocation}",
                            Severity = ViolationSeverity.Critical,
                            Remediation = "CUI must be processed and stored within the United States",
                            RegulatoryReference = "DFARS 252.204-7012(b)(2)(ii)(C)"
                        });
                    }
                }
            }
        }

        private void CheckSubcontractorFlow(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (context.Attributes.TryGetValue("SubcontractorInvolved", out var subObj) && subObj is true)
            {
                if (!context.Attributes.TryGetValue("DfarsFlowedDown", out var flowObj) || flowObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "DFARS252-008",
                        Description = "DFARS 252.204-7012 not flowed down to subcontractor",
                        Severity = ViolationSeverity.High,
                        Remediation = "Flow down DFARS clause to all subcontractors handling CUI",
                        RegulatoryReference = "DFARS 252.204-7012(m)"
                    });
                }
            }
        }
    }
}
