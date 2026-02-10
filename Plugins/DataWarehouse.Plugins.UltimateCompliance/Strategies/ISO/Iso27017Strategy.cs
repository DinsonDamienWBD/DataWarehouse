using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.ISO
{
    /// <summary>
    /// ISO/IEC 27017 Cloud Security Controls compliance strategy.
    /// </summary>
    public sealed class Iso27017Strategy : ComplianceStrategyBase
    {
        /// <inheritdoc/>
        public override string StrategyId => "iso27017";

        /// <inheritdoc/>
        public override string StrategyName => "ISO/IEC 27017";

        /// <inheritdoc/>
        public override string Framework => "ISO27017";

        /// <inheritdoc/>
        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            // Check cloud shared responsibility model (CLD.6.3.1)
            if (!context.Attributes.TryGetValue("SharedResponsibilityDefined", out var responsibilityObj) || responsibilityObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "ISO27017-001",
                    Description = "Shared responsibility model not defined for cloud service",
                    Severity = ViolationSeverity.High,
                    Remediation = "Document and communicate shared security responsibilities between provider and customer",
                    RegulatoryReference = "ISO/IEC 27017:2015 CLD.6.3.1"
                });
            }

            // Check virtual machine hardening (CLD.9.5.1)
            if (context.Attributes.TryGetValue("ResourceType", out var resourceObj) &&
                resourceObj is string resourceType &&
                resourceType.Contains("vm", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("HardenedConfiguration", out var hardenedObj) || hardenedObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "ISO27017-002",
                        Description = "Virtual machine not configured with hardened baseline",
                        Severity = ViolationSeverity.High,
                        Remediation = "Apply security hardening standards to virtual machine configurations",
                        RegulatoryReference = "ISO/IEC 27017:2015 CLD.9.5.1"
                    });
                }
            }

            // Check cloud service isolation (CLD.12.4.5)
            if (!context.Attributes.TryGetValue("TenantIsolation", out var isolationObj) || isolationObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "ISO27017-003",
                    Description = "Tenant isolation controls not verified",
                    Severity = ViolationSeverity.Critical,
                    Remediation = "Implement and verify multi-tenant isolation mechanisms",
                    RegulatoryReference = "ISO/IEC 27017:2015 CLD.12.4.5"
                });
            }

            // Check cloud data portability (CLD.8.1.4)
            if (context.OperationType.Equals("export", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("DataPortabilitySupported", out var portabilityObj) || portabilityObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "ISO27017-004",
                        Description = "Data portability mechanism not available for cloud data export",
                        Severity = ViolationSeverity.Medium,
                        Remediation = "Provide data export in standard, portable formats",
                        RegulatoryReference = "ISO/IEC 27017:2015 CLD.8.1.4"
                    });
                }
            }

            // Check monitoring and incident management (CLD.16.1.7)
            if (!context.Attributes.TryGetValue("CloudMonitoringEnabled", out var monitoringObj) || monitoringObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "ISO27017-005",
                    Description = "Cloud-specific security monitoring not enabled",
                    Severity = ViolationSeverity.Medium,
                    Remediation = "Enable continuous monitoring for cloud infrastructure and services",
                    RegulatoryReference = "ISO/IEC 27017:2015 CLD.16.1.7"
                });
            }

            recommendations.Add("Review cloud service provider's ISO 27017 compliance status and audit reports");

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
