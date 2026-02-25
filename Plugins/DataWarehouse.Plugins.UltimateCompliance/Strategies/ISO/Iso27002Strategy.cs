using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.ISO
{
    /// <summary>
    /// ISO/IEC 27002 Code of Practice for Information Security Controls compliance strategy.
    /// </summary>
    public sealed class Iso27002Strategy : ComplianceStrategyBase
    {
        /// <inheritdoc/>
        public override string StrategyId => "iso27002";

        /// <inheritdoc/>
        public override string StrategyName => "ISO/IEC 27002";

        /// <inheritdoc/>
        public override string Framework => "ISO27002";

        /// <inheritdoc/>
        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("iso27002.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            // Check access control selection (A.5)
            if (!context.Attributes.TryGetValue("AccessControlPolicy", out var policyObj) || policyObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "ISO27002-001",
                    Description = "Access control policy not defined or enforced",
                    Severity = ViolationSeverity.High,
                    Remediation = "Establish and document access control policy based on business requirements",
                    RegulatoryReference = "ISO/IEC 27002:2022 A.5"
                });
            }

            // Check user access management (A.5.18)
            if (string.IsNullOrEmpty(context.UserId))
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "ISO27002-002",
                    Description = "User identity not provided for access operation",
                    Severity = ViolationSeverity.High,
                    Remediation = "Ensure all access operations include authenticated user identification",
                    RegulatoryReference = "ISO/IEC 27002:2022 A.5.18"
                });
            }

            // Check cryptographic controls (A.8.24)
            if (context.DataClassification.Equals("confidential", StringComparison.OrdinalIgnoreCase) ||
                context.DataClassification.Equals("secret", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("EncryptionEnabled", out var encryptObj) || encryptObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "ISO27002-003",
                        Description = "Cryptographic controls not applied to confidential data",
                        Severity = ViolationSeverity.Critical,
                        Remediation = "Apply encryption at rest and in transit for confidential information",
                        RegulatoryReference = "ISO/IEC 27002:2022 A.8.24"
                    });
                }
            }

            // Check change management (A.8.32)
            if (context.OperationType.Equals("modify", StringComparison.OrdinalIgnoreCase) ||
                context.OperationType.Equals("update", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("ChangeApproved", out var approvedObj) || approvedObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "ISO27002-004",
                        Description = "Change operation without documented approval",
                        Severity = ViolationSeverity.Medium,
                        Remediation = "Implement change management process with approval workflow",
                        RegulatoryReference = "ISO/IEC 27002:2022 A.8.32"
                    });
                }
            }

            // Check logging and monitoring (A.8.15)
            if (!context.Attributes.TryGetValue("AuditLoggingEnabled", out var loggingObj) || loggingObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "ISO27002-005",
                    Description = "Audit logging not enabled for operation",
                    Severity = ViolationSeverity.Medium,
                    Remediation = "Enable and protect audit logs for all security-relevant events",
                    RegulatoryReference = "ISO/IEC 27002:2022 A.8.15"
                });
            }

            // Check information classification (A.5.12)
            if (string.IsNullOrEmpty(context.DataClassification))
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "ISO27002-006",
                    Description = "Information classification not assigned",
                    Severity = ViolationSeverity.Medium,
                    Remediation = "Classify information according to organizational classification scheme",
                    RegulatoryReference = "ISO/IEC 27002:2022 A.5.12"
                });
            }

            recommendations.Add("Regularly review and update control implementation based on risk assessments");

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
    
    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("iso27002.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("iso27002.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
