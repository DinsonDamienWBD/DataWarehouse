using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.SecurityFrameworks
{
    /// <summary>
    /// CIS Top 18 Critical Security Controls compliance strategy.
    /// </summary>
    public sealed class CisTop18Strategy : ComplianceStrategyBase
    {
        public override string StrategyId => "cis-top18";
        public override string StrategyName => "CIS Top 18";
        public override string Framework => "CIS-TOP18";

        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("cis_top18.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            if (!context.Attributes.TryGetValue("HardwareAssetInventory", out var hwObj) || hwObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "CIS18-01",
                    Description = "Hardware asset inventory not maintained",
                    Severity = ViolationSeverity.High,
                    Remediation = "Actively manage hardware assets with automated discovery",
                    RegulatoryReference = "CIS Top 18 Control 1"
                });
            }

            if (!context.Attributes.TryGetValue("SoftwareAssetInventory", out var swObj) || swObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "CIS18-02",
                    Description = "Software asset inventory not maintained",
                    Severity = ViolationSeverity.High,
                    Remediation = "Actively manage software assets with whitelisting",
                    RegulatoryReference = "CIS Top 18 Control 2"
                });
            }

            if (!context.Attributes.TryGetValue("ContinuousVulnerabilityManagement", out var vulnObj) || vulnObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "CIS18-03",
                    Description = "Continuous vulnerability management not implemented",
                    Severity = ViolationSeverity.Critical,
                    Remediation = "Implement automated vulnerability scanning and remediation",
                    RegulatoryReference = "CIS Top 18 Control 3"
                });
            }

            if (!context.Attributes.TryGetValue("ControlledUseOfAdminPrivileges", out var adminObj) || adminObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "CIS18-04",
                    Description = "Administrative privileges not controlled",
                    Severity = ViolationSeverity.High,
                    Remediation = "Implement least privilege and monitor privileged account usage",
                    RegulatoryReference = "CIS Top 18 Control 4"
                });
            }

            recommendations.Add("Prioritize implementation groups: IG1 (Basic), IG2 (Intermediate), IG3 (Advanced)");

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
            IncrementCounter("cis_top18.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("cis_top18.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
