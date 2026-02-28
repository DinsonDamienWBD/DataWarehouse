using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.NIST
{
    /// <summary>
    /// NIST SP 800-171 Controlled Unclassified Information (CUI) protection compliance strategy.
    /// </summary>
    public sealed class Nist800171Strategy : ComplianceStrategyBase
    {
        /// <inheritdoc/>
        public override string StrategyId => "nist800171";

        /// <inheritdoc/>
        public override string StrategyName => "NIST SP 800-171";

        /// <inheritdoc/>
        public override string Framework => "NIST800171";

        /// <inheritdoc/>
        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("nist800171.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            bool isCui = context.Attributes.TryGetValue("IsCUI", out var cuiObj) && cuiObj is true;

            if (!isCui)
            {
                return Task.FromResult(new ComplianceResult
                {
                    IsCompliant = true,
                    Framework = Framework,
                    Status = ComplianceStatus.NotApplicable,
                    Violations = violations,
                    Recommendations = new List<string> { "NIST 800-171 applies only to CUI" }
                });
            }

            // Check access control (3.1)
            if (!context.Attributes.TryGetValue("AccessAuthorized", out var accessObj) || accessObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "NIST800171-3.1.1",
                    Description = "CUI access not limited to authorized users",
                    Severity = ViolationSeverity.Critical,
                    Remediation = "Limit system access to authorized users and processes (3.1.1)",
                    RegulatoryReference = "NIST SP 800-171 3.1.1"
                });
            }

            // Check media protection (3.8)
            if (!string.IsNullOrEmpty(context.OperationType) && context.OperationType.Equals("export", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("MediaSanitized", out var sanitizeObj) || sanitizeObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "NIST800171-3.8.3",
                        Description = "CUI media not sanitized before disposal or reuse",
                        Severity = ViolationSeverity.High,
                        Remediation = "Sanitize or destroy media containing CUI (3.8.3)",
                        RegulatoryReference = "NIST SP 800-171 3.8.3"
                    });
                }
            }

            // Check encryption (3.13.11)
            if (!context.Attributes.TryGetValue("EncryptionEnabled", out var encryptObj) || encryptObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "NIST800171-3.13.11",
                    Description = "CUI not protected with FIPS-validated cryptography",
                    Severity = ViolationSeverity.Critical,
                    Remediation = "Employ FIPS-validated cryptography to protect CUI (3.13.11)",
                    RegulatoryReference = "NIST SP 800-171 3.13.11"
                });
            }

            // Check incident response (3.6)
            if (!context.Attributes.TryGetValue("IncidentResponseCapability", out var irObj) || irObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "NIST800171-3.6.1",
                    Description = "Incident handling capability not established for CUI systems",
                    Severity = ViolationSeverity.High,
                    Remediation = "Establish incident response capability with reporting procedures (3.6.1)",
                    RegulatoryReference = "NIST SP 800-171 3.6.1"
                });
            }

            // Check system monitoring (3.14)
            if (!context.Attributes.TryGetValue("SystemMonitored", out var monitorObj) || monitorObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "NIST800171-3.14.1",
                    Description = "System and user activities not monitored",
                    Severity = ViolationSeverity.Medium,
                    Remediation = "Identify and document anomalous activities (3.14.1)",
                    RegulatoryReference = "NIST SP 800-171 3.14.1"
                });
            }

            recommendations.Add("Consider CMMC certification if providing services to DoD");

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
            IncrementCounter("nist800171.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("nist800171.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
