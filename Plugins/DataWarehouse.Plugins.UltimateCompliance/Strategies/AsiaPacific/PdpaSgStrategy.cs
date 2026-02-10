using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.AsiaPacific
{
    /// <summary>
    /// Singapore Personal Data Protection Act (PDPA) compliance strategy.
    /// </summary>
    public sealed class PdpaSgStrategy : ComplianceStrategyBase
    {
        /// <inheritdoc/>
        public override string StrategyId => "pdpa-sg";

        /// <inheritdoc/>
        public override string StrategyName => "Singapore PDPA";

        /// <inheritdoc/>
        public override string Framework => "PDPA-SG";

        /// <inheritdoc/>
        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            // Check consent obligation (Section 13)
            if (!context.Attributes.TryGetValue("ConsentObtained", out var consentObj) || consentObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "PDPA-SG-001",
                    Description = "Consent not obtained for personal data collection, use, or disclosure",
                    Severity = ViolationSeverity.High,
                    Remediation = "Obtain consent before collecting, using, or disclosing personal data (Sec. 13)",
                    RegulatoryReference = "PDPA Singapore Section 13"
                });
            }

            // Check purpose limitation (Section 18)
            if (!context.ProcessingPurposes.Any())
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "PDPA-SG-002",
                    Description = "Purpose for data collection not specified",
                    Severity = ViolationSeverity.High,
                    Remediation = "Collect data only for reasonable purposes made known to individual (Sec. 18)",
                    RegulatoryReference = "PDPA Singapore Section 18"
                });
            }

            // Check access and correction (Section 21-22)
            if (context.OperationType.Contains("access-request", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("AccessGrantedWithin30Days", out var accessObj) || accessObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "PDPA-SG-003",
                        Description = "Access request not responded to within 30 days",
                        Severity = ViolationSeverity.Medium,
                        Remediation = "Provide access to personal data within 30 days (Sec. 21)",
                        RegulatoryReference = "PDPA Singapore Section 21"
                    });
                }
            }

            // Check data protection officer (Section 11)
            if (!context.Attributes.TryGetValue("DpoDesignated", out var dpoObj) || dpoObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "PDPA-SG-004",
                    Description = "Data Protection Officer not designated",
                    Severity = ViolationSeverity.Medium,
                    Remediation = "Appoint at least one individual to be responsible for PDPA compliance (Sec. 11)",
                    RegulatoryReference = "PDPA Singapore Section 11"
                });
            }

            // Check data breach notification (Section 26D)
            if (context.Attributes.TryGetValue("DataBreachOccurred", out var breachObj) && breachObj is true)
            {
                if (!context.Attributes.TryGetValue("BreachNotifiedWithin3Days", out var notifyObj) || notifyObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "PDPA-SG-005",
                        Description = "Data breach not notified to PDPC within 3 days",
                        Severity = ViolationSeverity.Critical,
                        Remediation = "Notify PDPC and affected individuals within 3 days of notifiable breach (Sec. 26D)",
                        RegulatoryReference = "PDPA Singapore Section 26D"
                    });
                }
            }

            recommendations.Add("Consider Singapore's Accountability Framework for enhanced compliance demonstration");

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
