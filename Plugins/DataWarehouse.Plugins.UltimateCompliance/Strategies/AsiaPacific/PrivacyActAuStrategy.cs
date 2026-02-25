using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.AsiaPacific
{
    /// <summary>
    /// Australia Privacy Act (Australian Privacy Principles) compliance strategy.
    /// </summary>
    public sealed class PrivacyActAuStrategy : ComplianceStrategyBase
    {
        /// <inheritdoc/>
        public override string StrategyId => "privacy-act-au";

        /// <inheritdoc/>
        public override string StrategyName => "Australia Privacy Act";

        /// <inheritdoc/>
        public override string Framework => "PRIVACY-ACT-AU";

        /// <inheritdoc/>
        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("privacy_act_au.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            // Check APP 5 - notification of collection
            if (!context.Attributes.TryGetValue("CollectionNoticeProvided", out var noticeObj) || noticeObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "APP-005",
                    Description = "Collection notice not provided to individual",
                    Severity = ViolationSeverity.High,
                    Remediation = "Provide notice about identity, purposes, and disclosure of personal information (APP 5)",
                    RegulatoryReference = "Privacy Act 1988 APP 5"
                });
            }

            // Check APP 6 - use or disclosure
            if (!context.ProcessingPurposes.Any())
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "APP-006",
                    Description = "Purpose for collection and use not specified",
                    Severity = ViolationSeverity.High,
                    Remediation = "Only use or disclose personal information for primary purpose or related secondary purposes (APP 6)",
                    RegulatoryReference = "Privacy Act 1988 APP 6"
                });
            }

            // Check APP 8 - cross-border disclosure
            if (!string.IsNullOrEmpty(context.DestinationLocation) &&
                !context.DestinationLocation.StartsWith("AU", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("OverseasRecipientCompliant", out var overseasObj) || overseasObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "APP-008",
                        Description = "Cross-border disclosure without ensuring APP compliance",
                        Severity = ViolationSeverity.Critical,
                        Remediation = "Ensure overseas recipient is bound by APPs or obtain consent (APP 8)",
                        RegulatoryReference = "Privacy Act 1988 APP 8"
                    });
                }
            }

            // Check APP 11 - security of personal information
            if (!context.Attributes.TryGetValue("SecurityMeasuresImplemented", out var securityObj) || securityObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "APP-011",
                    Description = "Reasonable security measures not implemented",
                    Severity = ViolationSeverity.High,
                    Remediation = "Implement reasonable steps to protect personal information from misuse, interference, and loss (APP 11)",
                    RegulatoryReference = "Privacy Act 1988 APP 11"
                });
            }

            // Check Notifiable Data Breach scheme
            if (context.Attributes.TryGetValue("DataBreachOccurred", out var breachObj) && breachObj is true)
            {
                if (!context.Attributes.TryGetValue("BreachNotified", out var notifiedObj) || notifiedObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "NDB-001",
                        Description = "Eligible data breach not notified",
                        Severity = ViolationSeverity.Critical,
                        Remediation = "Notify OAIC and affected individuals of eligible data breaches as soon as practicable",
                        RegulatoryReference = "Privacy Act 1988 Part IIIC"
                    });
                }
            }

            recommendations.Add("Conduct regular privacy impact assessments for high-risk projects");

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
            IncrementCounter("privacy_act_au.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("privacy_act_au.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
