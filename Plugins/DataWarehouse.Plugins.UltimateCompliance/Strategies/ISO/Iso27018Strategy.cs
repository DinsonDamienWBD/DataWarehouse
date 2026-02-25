using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.ISO
{
    /// <summary>
    /// ISO/IEC 27018 Cloud Privacy compliance strategy for PII protection.
    /// </summary>
    public sealed class Iso27018Strategy : ComplianceStrategyBase
    {
        /// <inheritdoc/>
        public override string StrategyId => "iso27018";

        /// <inheritdoc/>
        public override string StrategyName => "ISO/IEC 27018";

        /// <inheritdoc/>
        public override string Framework => "ISO27018";

        /// <inheritdoc/>
        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
        IncrementCounter("iso27018.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            // Check consent for PII processing (A.6)
            if (context.DataSubjectCategories.Any())
            {
                if (!context.Attributes.TryGetValue("ConsentObtained", out var consentObj) || consentObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "ISO27018-001",
                        Description = "PII processing without documented consent or legal basis",
                        Severity = ViolationSeverity.Critical,
                        Remediation = "Obtain and document user consent for PII processing in cloud",
                        RegulatoryReference = "ISO/IEC 27018:2019 A.6"
                    });
                }
            }

            // Check PII disclosure controls (A.7)
            if (!context.Attributes.TryGetValue("PiiDisclosurePolicy", out var disclosureObj) || disclosureObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "ISO27018-002",
                    Description = "PII disclosure policy not defined or enforced",
                    Severity = ViolationSeverity.High,
                    Remediation = "Implement controls to prevent unauthorized PII disclosure to third parties",
                    RegulatoryReference = "ISO/IEC 27018:2019 A.7"
                });
            }

            // Check return and deletion of PII (A.9)
            if (context.OperationType.Equals("delete", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("SecureDeletionMethod", out var deletionObj) || deletionObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "ISO27018-003",
                        Description = "Secure deletion method not specified for PII removal",
                        Severity = ViolationSeverity.High,
                        Remediation = "Use cryptographic erasure or secure overwriting for PII deletion",
                        RegulatoryReference = "ISO/IEC 27018:2019 A.9"
                    });
                }
            }

            // Check notification of PII breach (A.9.1)
            if (context.Attributes.TryGetValue("DataBreachDetected", out var breachObj) && breachObj is true)
            {
                if (!context.Attributes.TryGetValue("BreachNotificationPlan", out var notificationObj) || notificationObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "ISO27018-004",
                        Description = "PII breach notification process not established",
                        Severity = ViolationSeverity.Critical,
                        Remediation = "Document and execute breach notification plan for affected data subjects",
                        RegulatoryReference = "ISO/IEC 27018:2019 A.9.1"
                    });
                }
            }

            // Check transparency about PII processing (A.4)
            if (!context.Attributes.TryGetValue("PrivacyNoticeProvided", out var noticeObj) || noticeObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "ISO27018-005",
                    Description = "Privacy notice not provided to data subjects",
                    Severity = ViolationSeverity.Medium,
                    Remediation = "Provide clear privacy notice about PII collection, use, and storage",
                    RegulatoryReference = "ISO/IEC 27018:2019 A.4"
                });
            }

            recommendations.Add("Ensure cloud service provider maintains ISO 27018 certification");

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
        IncrementCounter("iso27018.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
        IncrementCounter("iso27018.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
