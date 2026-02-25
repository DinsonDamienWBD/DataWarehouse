using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.NIST
{
    /// <summary>
    /// NIST Privacy Framework compliance strategy.
    /// </summary>
    public sealed class NistPrivacyStrategy : ComplianceStrategyBase
    {
        /// <inheritdoc/>
        public override string StrategyId => "nist-privacy";

        /// <inheritdoc/>
        public override string StrategyName => "NIST Privacy Framework";

        /// <inheritdoc/>
        public override string Framework => "NIST-Privacy";

        /// <inheritdoc/>
        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("nist_privacy.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            // Check IDENTIFY-P function
            if (!context.Attributes.TryGetValue("DataInventoryMaintained", out var inventoryObj) || inventoryObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "NIST-PRIVACY-IDENTIFY",
                    Description = "Data processing inventory not maintained",
                    Severity = ViolationSeverity.High,
                    Remediation = "Maintain inventory of personal data processing activities (IDENTIFY-P)",
                    RegulatoryReference = "NIST Privacy Framework IDENTIFY-P"
                });
            }

            // Check GOVERN-P function
            if (!context.Attributes.TryGetValue("PrivacyGovernance", out var governObj) || governObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "NIST-PRIVACY-GOVERN",
                    Description = "Privacy governance program not established",
                    Severity = ViolationSeverity.High,
                    Remediation = "Establish privacy governance with policy and accountability (GOVERN-P)",
                    RegulatoryReference = "NIST Privacy Framework GOVERN-P"
                });
            }

            // Check CONTROL-P function
            if (context.DataSubjectCategories.Any())
            {
                if (!context.Attributes.TryGetValue("DataSubjectRightsMechanism", out var rightsObj) || rightsObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "NIST-PRIVACY-CONTROL",
                        Description = "Data subject rights exercise mechanism not implemented",
                        Severity = ViolationSeverity.High,
                        Remediation = "Implement mechanisms for individuals to exercise privacy rights (CONTROL-P)",
                        RegulatoryReference = "NIST Privacy Framework CONTROL-P"
                    });
                }
            }

            // Check COMMUNICATE-P function
            if (!context.Attributes.TryGetValue("PrivacyNoticeProvided", out var noticeObj) || noticeObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "NIST-PRIVACY-COMMUNICATE",
                    Description = "Privacy notice not provided to individuals",
                    Severity = ViolationSeverity.Medium,
                    Remediation = "Provide clear, accessible privacy notices (COMMUNICATE-P)",
                    RegulatoryReference = "NIST Privacy Framework COMMUNICATE-P"
                });
            }

            // Check PROTECT-P function
            if (!context.Attributes.TryGetValue("DataProtectionControls", out var protectObj) || protectObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "NIST-PRIVACY-PROTECT",
                    Description = "Data protection controls not implemented",
                    Severity = ViolationSeverity.Critical,
                    Remediation = "Implement technical controls to protect personal data (PROTECT-P)",
                    RegulatoryReference = "NIST Privacy Framework PROTECT-P"
                });
            }

            recommendations.Add("Integrate privacy framework with existing cybersecurity framework");

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
            IncrementCounter("nist_privacy.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("nist_privacy.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
