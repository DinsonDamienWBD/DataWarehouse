using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.Industry
{
    /// <summary>
    /// SWIFT Customer Security Controls Framework (CSCF) compliance strategy.
    /// </summary>
    public sealed class SwiftCscfStrategy : ComplianceStrategyBase
    {
        public override string StrategyId => "swift-cscf";
        public override string StrategyName => "SWIFT CSCF";
        public override string Framework => "SWIFT-CSCF";

        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("swift_cscf.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            if (!context.Attributes.TryGetValue("SecureEnvironment", out var secEnvObj) || secEnvObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "CSCF-1.1",
                    Description = "Secure operating environment not established",
                    Severity = ViolationSeverity.Critical,
                    Remediation = "Implement network segmentation and isolation for SWIFT infrastructure (1.1)",
                    RegulatoryReference = "SWIFT CSCF Control 1.1"
                });
            }

            if (!context.Attributes.TryGetValue("RestrictAccess", out var accessObj) || accessObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "CSCF-2.1",
                    Description = "Access controls not restricted appropriately",
                    Severity = ViolationSeverity.Critical,
                    Remediation = "Implement multi-factor authentication and least privilege access (2.1-2.9)",
                    RegulatoryReference = "SWIFT CSCF Controls 2.1-2.9"
                });
            }

            if (!context.Attributes.TryGetValue("DetectAnomalousActivity", out var detectObj) || detectObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "CSCF-6.1",
                    Description = "Detection of anomalous activity not implemented",
                    Severity = ViolationSeverity.High,
                    Remediation = "Implement SIEM and monitoring for suspicious SWIFT transactions (6.1-6.4)",
                    RegulatoryReference = "SWIFT CSCF Controls 6.1-6.4"
                });
            }

            if (!context.Attributes.TryGetValue("VulnerabilityManagement", out var vulnObj) || vulnObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "CSCF-5.1",
                    Description = "Vulnerability and patch management not established",
                    Severity = ViolationSeverity.High,
                    Remediation = "Implement regular patching and vulnerability scanning (5.1)",
                    RegulatoryReference = "SWIFT CSCF Control 5.1"
                });
            }

            recommendations.Add("Complete annual SWIFT CSCF self-attestation and independent assessment");

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
            IncrementCounter("swift_cscf.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("swift_cscf.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
