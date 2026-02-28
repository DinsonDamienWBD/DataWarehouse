using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.SecurityFrameworks
{
    /// <summary>
    /// COBIT (Control Objectives for Information and Related Technologies) compliance strategy.
    /// </summary>
    public sealed class CobitStrategy : ComplianceStrategyBase
    {
        public override string StrategyId => "cobit";
        public override string StrategyName => "COBIT";
        public override string Framework => "COBIT";

        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("cobit.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            if (!context.Attributes.TryGetValue("GovernanceFramework", out var govObj) || govObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "COBIT-EDM",
                    Description = "IT governance framework not established",
                    Severity = ViolationSeverity.High,
                    Remediation = "Establish governance processes (EDM domains)",
                    RegulatoryReference = "COBIT 2019 EDM"
                });
            }

            if (!context.Attributes.TryGetValue("ManagementObjectives", out var mgmtObj) || mgmtObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "COBIT-APO",
                    Description = "IT management objectives not defined",
                    Severity = ViolationSeverity.High,
                    Remediation = "Define management objectives across APO, BAI, DSS, MEA domains",
                    RegulatoryReference = "COBIT 2019 Management Objectives"
                });
            }

            if (!context.Attributes.TryGetValue("ProcessCapability", out var capObj))
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "COBIT-PCM",
                    Description = "Process capability not assessed",
                    Severity = ViolationSeverity.Medium,
                    Remediation = "Assess process capability using COBIT Process Capability Model",
                    RegulatoryReference = "COBIT PCM"
                });
            }

            if (!context.Attributes.TryGetValue("PerformanceMetrics", out var metricsObj) || metricsObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "COBIT-MEA01",
                    Description = "Performance and conformance monitoring not established",
                    Severity = ViolationSeverity.Medium,
                    Remediation = "Implement IT performance monitoring and measurement (MEA01)",
                    RegulatoryReference = "COBIT 2019 MEA01"
                });
            }

            recommendations.Add("Conduct COBIT maturity assessment for continuous improvement");

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
            IncrementCounter("cobit.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("cobit.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
