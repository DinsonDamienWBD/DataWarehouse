using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.AsiaPacific
{
    /// <summary>
    /// Japan Act on the Protection of Personal Information (APPI) compliance strategy.
    /// </summary>
    public sealed class AppiStrategy : ComplianceStrategyBase
    {
        /// <inheritdoc/>
        public override string StrategyId => "appi";

        /// <inheritdoc/>
        public override string StrategyName => "Japan APPI";

        /// <inheritdoc/>
        public override string Framework => "APPI";

        /// <inheritdoc/>
        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            // Check purpose specification (Article 17)
            if (!context.ProcessingPurposes.Any())
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "APPI-001",
                    Description = "Purpose of use not specified",
                    Severity = ViolationSeverity.High,
                    Remediation = "Specify purposes of use as specifically as possible (Art. 17)",
                    RegulatoryReference = "APPI Article 17"
                });
            }

            // Check notice and consent (Article 21)
            if (!context.Attributes.TryGetValue("PurposeNotified", out var notifiedObj) || notifiedObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "APPI-002",
                    Description = "Purpose of use not notified to individual",
                    Severity = ViolationSeverity.High,
                    Remediation = "Notify or publicize purpose of use when obtaining personal information (Art. 21)",
                    RegulatoryReference = "APPI Article 21"
                });
            }

            // Check opt-out for third-party provision (Article 27)
            if (context.OperationType.Equals("transfer", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("OptOutOffered", out var optOutObj) || optOutObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "APPI-003",
                        Description = "Opt-out opportunity not provided for third-party data provision",
                        Severity = ViolationSeverity.Medium,
                        Remediation = "Provide opt-out mechanism for data sharing with third parties (Art. 27)",
                        RegulatoryReference = "APPI Article 27"
                    });
                }
            }

            // Check cross-border transfer (Article 28)
            if (!string.IsNullOrEmpty(context.DestinationLocation) &&
                !context.DestinationLocation.StartsWith("JP", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("CrossBorderConsent", out var cbConsentObj) || cbConsentObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "APPI-004",
                        Description = "Consent not obtained for cross-border personal data transfer",
                        Severity = ViolationSeverity.High,
                        Remediation = "Obtain consent or ensure adequate protection in destination country (Art. 28)",
                        RegulatoryReference = "APPI Article 28"
                    });
                }
            }

            // Check individual rights (Article 33-37)
            if (context.OperationType.Contains("request", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("RequestMechanismEstablished", out var mechanismObj) || mechanismObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "APPI-005",
                        Description = "Mechanism for individual rights requests not established",
                        Severity = ViolationSeverity.Medium,
                        Remediation = "Establish procedures for disclosure, correction, and suspension requests (Art. 33-37)",
                        RegulatoryReference = "APPI Articles 33-37"
                    });
                }
            }

            recommendations.Add("Appoint representative if handling over 5,000 records without Japan office");

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
