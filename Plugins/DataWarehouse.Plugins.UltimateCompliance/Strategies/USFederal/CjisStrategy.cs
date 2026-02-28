using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.USFederal
{
    /// <summary>
    /// CJIS (Criminal Justice Information Services) compliance strategy.
    /// Validates criminal justice information security requirements including access control and encryption.
    /// </summary>
    public sealed class CjisStrategy : ComplianceStrategyBase
    {
        /// <inheritdoc/>
        public override string StrategyId => "cjis";

        /// <inheritdoc/>
        public override string StrategyName => "CJIS Security Policy Compliance";

        /// <inheritdoc/>
        public override string Framework => "CJIS";

        /// <inheritdoc/>
        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("cjis.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            CheckAccessControl(context, violations, recommendations);
            CheckEncryptionRequirements(context, violations, recommendations);
            CheckBackgroundScreening(context, violations, recommendations);
            CheckMediaProtection(context, violations, recommendations);
            CheckAuditRequirements(context, violations, recommendations);

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

        private void CheckAccessControl(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (!string.IsNullOrEmpty(context.DataClassification) && context.DataClassification.Equals("cjis", StringComparison.OrdinalIgnoreCase) ||
                (context.DataSubjectCategories != null && context.DataSubjectCategories.Contains("criminal-justice", StringComparer.OrdinalIgnoreCase)))
            {
                if (!context.Attributes.TryGetValue("AdvancedAuthentication", out var authObj) || authObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "CJIS-001",
                        Description = "Advanced authentication not implemented for CJIS data access",
                        Severity = ViolationSeverity.Critical,
                        Remediation = "Implement multi-factor authentication or biometric authentication",
                        RegulatoryReference = "CJIS Security Policy 5.6"
                    });
                }

                if (!context.Attributes.TryGetValue("NeedToKnowVerified", out var needObj) || needObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "CJIS-002",
                        Description = "Need-to-know access not verified for CJIS data",
                        Severity = ViolationSeverity.High,
                        Remediation = "Verify and document need-to-know justification for access",
                        RegulatoryReference = "CJIS Security Policy 5.2"
                    });
                }
            }
        }

        private void CheckEncryptionRequirements(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (!string.IsNullOrEmpty(context.DataClassification) && context.DataClassification.Equals("cjis", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("Fips140Encryption", out var fipsObj) || fipsObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "CJIS-003",
                        Description = "FIPS 140-2 validated encryption not used for CJIS data",
                        Severity = ViolationSeverity.Critical,
                        Remediation = "Use FIPS 140-2 validated cryptographic modules",
                        RegulatoryReference = "CJIS Security Policy 5.10.1"
                    });
                }

                if (!string.IsNullOrEmpty(context.OperationType) && context.OperationType.Equals("transmission", StringComparison.OrdinalIgnoreCase))
                {
                    if (!context.Attributes.TryGetValue("EncryptionInTransit", out var transitObj) || transitObj is not true)
                    {
                        violations.Add(new ComplianceViolation
                        {
                            Code = "CJIS-004",
                            Description = "CJIS data transmitted without encryption",
                            Severity = ViolationSeverity.Critical,
                            Remediation = "Encrypt all CJIS data transmissions using FIPS 140-2 approved methods",
                            RegulatoryReference = "CJIS Security Policy 5.10.1.2"
                        });
                    }
                }
            }
        }

        private void CheckBackgroundScreening(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (context.Attributes.TryGetValue("UserCjisAccess", out var accessObj) && accessObj is true)
            {
                if (!context.Attributes.TryGetValue("BackgroundCheckCompleted", out var bgObj) || bgObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "CJIS-005",
                        Description = "User with CJIS access has not completed fingerprint-based background check",
                        Severity = ViolationSeverity.Critical,
                        Remediation = "Complete FBI fingerprint-based background check before granting access",
                        RegulatoryReference = "CJIS Security Policy 5.3"
                    });
                }

                if (context.Attributes.TryGetValue("BackgroundCheckDate", out var dateObj) &&
                    dateObj is DateTime checkDate)
                {
                    var daysSinceCheck = (DateTime.UtcNow - checkDate).TotalDays;
                    if (daysSinceCheck > 3650) // 10 years
                    {
                        recommendations.Add("Background check older than 10 years - consider renewal");
                    }
                }
            }
        }

        private void CheckMediaProtection(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (!string.IsNullOrEmpty(context.OperationType) && context.OperationType.Equals("media-disposal", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(context.OperationType, "media-sanitization", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("SanitizationMethod", out var methodObj) ||
                    methodObj is not string method ||
                    string.IsNullOrEmpty(method))
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "CJIS-006",
                        Description = "Media sanitization method not documented",
                        Severity = ViolationSeverity.High,
                        Remediation = "Document media sanitization using approved methods (degaussing, physical destruction, cryptographic erasure)",
                        RegulatoryReference = "CJIS Security Policy 5.9.3"
                    });
                }
            }
        }

        private void CheckAuditRequirements(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (!context.Attributes.TryGetValue("AuditLoggingEnabled", out var auditObj) || auditObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "CJIS-007",
                    Description = "Audit logging not enabled for CJIS system",
                    Severity = ViolationSeverity.High,
                    Remediation = "Enable comprehensive audit logging of all access and operations",
                    RegulatoryReference = "CJIS Security Policy 5.4"
                });
            }

            if (context.Attributes.TryGetValue("AuditRetentionDays", out var retentionObj) &&
                retentionObj is int retentionDays && retentionDays < 365)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "CJIS-008",
                    Description = $"Audit log retention ({retentionDays} days) below minimum requirement",
                    Severity = ViolationSeverity.Medium,
                    Remediation = "Retain audit logs for minimum 1 year",
                    RegulatoryReference = "CJIS Security Policy 5.4"
                });
            }
        }
    
    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("cjis.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("cjis.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
