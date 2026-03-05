using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.Industry
{
    /// <summary>
    /// SOC 1 Type II (Internal Control over Financial Reporting) compliance strategy.
    /// </summary>
    public sealed class Soc1Strategy : ComplianceStrategyBase
    {
        public override string StrategyId => "soc1";
        public override string StrategyName => "SOC 1 Type II";
        public override string Framework => "SOC1";

        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("soc1.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            if (!context.Attributes.TryGetValue("ControlActivitiesDefined", out var controlObj) || controlObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "SOC1-001",
                    Description = "Control activities over financial reporting not defined",
                    Severity = ViolationSeverity.High,
                    Remediation = "Define and document control activities relevant to ICFR",
                    RegulatoryReference = "AICPA SOC 1 - Control Activities"
                });
            }

            if (!context.Attributes.TryGetValue("ControlOperatingEffectiveness", out var effectiveObj) || effectiveObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "SOC1-002",
                    Description = "Operating effectiveness of controls not tested",
                    Severity = ViolationSeverity.Critical,
                    Remediation = "Test controls over a period (Type II) to demonstrate operating effectiveness",
                    RegulatoryReference = "AICPA SOC 1 Type II"
                });
            }

            if (!context.Attributes.TryGetValue("ChangeManagementControlled", out var changeObj) || changeObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "SOC1-003",
                    Description = "Change management controls not established",
                    Severity = ViolationSeverity.High,
                    Remediation = "Implement controls for changes affecting financial systems",
                    RegulatoryReference = "AICPA SOC 1 - Change Management"
                });
            }

            if (!context.Attributes.TryGetValue("MonitoringActivities", out var monitorObj) || monitorObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "SOC1-004",
                    Description = "Monitoring activities for control effectiveness not performed",
                    Severity = ViolationSeverity.Medium,
                    Remediation = "Implement ongoing monitoring and periodic evaluations of controls",
                    RegulatoryReference = "AICPA SOC 1 - Monitoring"
                });
            }

            recommendations.Add("Engage independent auditor for annual SOC 1 Type II examination");

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
            IncrementCounter("soc1.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("soc1.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
