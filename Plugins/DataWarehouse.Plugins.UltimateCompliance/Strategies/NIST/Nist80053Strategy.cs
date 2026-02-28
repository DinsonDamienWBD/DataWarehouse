using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.NIST
{
    /// <summary>
    /// NIST SP 800-53 Security and Privacy Controls compliance strategy.
    /// </summary>
    public sealed class Nist80053Strategy : ComplianceStrategyBase
    {
        /// <inheritdoc/>
        public override string StrategyId => "nist80053";

        /// <inheritdoc/>
        public override string StrategyName => "NIST SP 800-53";

        /// <inheritdoc/>
        public override string Framework => "NIST80053";

        /// <inheritdoc/>
        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("nist80053.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            // Check Access Control (AC family)
            if (string.IsNullOrEmpty(context.UserId))
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "NIST80053-AC-001",
                    Description = "Access control not enforced - user identity required",
                    Severity = ViolationSeverity.High,
                    Remediation = "Implement account management and enforce least privilege (AC-2, AC-6)",
                    RegulatoryReference = "NIST SP 800-53 Rev. 5 AC-2, AC-6"
                });
            }

            // Check Audit and Accountability (AU family)
            if (!context.Attributes.TryGetValue("AuditLoggingEnabled", out var auditObj) || auditObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "NIST80053-AU-001",
                    Description = "Audit logging not enabled for security-relevant events",
                    Severity = ViolationSeverity.High,
                    Remediation = "Enable audit record generation and review (AU-2, AU-6)",
                    RegulatoryReference = "NIST SP 800-53 Rev. 5 AU-2, AU-6"
                });
            }

            // Check Configuration Management (CM family)
            if (!string.IsNullOrEmpty(context.OperationType) && context.OperationType.Equals("modify", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("ChangeControlled", out var cmObj) || cmObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "NIST80053-CM-001",
                        Description = "Configuration change not controlled through formal process",
                        Severity = ViolationSeverity.Medium,
                        Remediation = "Implement configuration change control and baseline management (CM-3, CM-2)",
                        RegulatoryReference = "NIST SP 800-53 Rev. 5 CM-2, CM-3"
                    });
                }
            }

            // Check Identification and Authentication (IA family)
            // Applies to confidential, secret, top-secret, and restricted classifications
            if (!context.Attributes.TryGetValue("AuthenticationStrength", out var authObj) ||
                authObj is not string authStrength ||
                authStrength.Equals("single-factor", StringComparison.OrdinalIgnoreCase))
            {
                var sensitiveClassifications = new[] { "confidential", "secret", "top-secret", "restricted" };
                if (!string.IsNullOrEmpty(context.DataClassification) && sensitiveClassifications.Any(c => context.DataClassification.Equals(c, StringComparison.OrdinalIgnoreCase)))
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "NIST80053-IA-001",
                        Description = $"Multi-factor authentication not used for {context.DataClassification} data access",
                        Severity = ViolationSeverity.High,
                        Remediation = "Implement multi-factor authentication for privileged access (IA-2(1))",
                        RegulatoryReference = "NIST SP 800-53 Rev. 5 IA-2(1)"
                    });
                }
            }

            // Check System and Communications Protection (SC family)
            if (!string.IsNullOrEmpty(context.DataClassification) && context.DataClassification.Equals("confidential", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(context.DataClassification, "secret", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("EncryptionInTransit", out var transitObj) || transitObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "NIST80053-SC-001",
                        Description = "Confidential data transmitted without cryptographic protection",
                        Severity = ViolationSeverity.Critical,
                        Remediation = "Implement transmission confidentiality and integrity (SC-8, SC-13)",
                        RegulatoryReference = "NIST SP 800-53 Rev. 5 SC-8, SC-13"
                    });
                }

                if (!context.Attributes.TryGetValue("EncryptionAtRest", out var restObj) || restObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "NIST80053-SC-002",
                        Description = "Confidential data stored without cryptographic protection",
                        Severity = ViolationSeverity.Critical,
                        Remediation = "Implement protection of information at rest (SC-28)",
                        RegulatoryReference = "NIST SP 800-53 Rev. 5 SC-28"
                    });
                }
            }

            recommendations.Add("Conduct regular control assessments as per NIST SP 800-53A");

            var hasHighViolations = violations.Any(v => v.Severity >= ViolationSeverity.High);
            var isCompliant = !hasHighViolations;
            var status = violations.Count == 0 ? ComplianceStatus.Compliant :
                        hasHighViolations ? ComplianceStatus.NonCompliant :
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
    
    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("nist80053.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("nist80053.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
