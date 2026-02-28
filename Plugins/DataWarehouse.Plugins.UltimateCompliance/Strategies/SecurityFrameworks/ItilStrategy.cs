using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.SecurityFrameworks
{
    /// <summary>
    /// ITIL v4 (IT Infrastructure Library) service management compliance strategy.
    /// </summary>
    public sealed class ItilStrategy : ComplianceStrategyBase
    {
        public override string StrategyId => "itil";
        public override string StrategyName => "ITIL v4";
        public override string Framework => "ITIL";

        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("itil.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            if (!context.Attributes.TryGetValue("ServiceValueSystem", out var svsObj) || svsObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "ITIL-SVS",
                    Description = "Service Value System not established",
                    Severity = ViolationSeverity.High,
                    Remediation = "Establish ITIL Service Value System with guiding principles",
                    RegulatoryReference = "ITIL v4 Service Value System"
                });
            }

            if (!context.Attributes.TryGetValue("ServiceLevelManagement", out var slmObj) || slmObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "ITIL-SLM",
                    Description = "Service Level Management not implemented",
                    Severity = ViolationSeverity.High,
                    Remediation = "Define and monitor SLAs for IT services",
                    RegulatoryReference = "ITIL v4 Service Level Management"
                });
            }

            if (!string.IsNullOrEmpty(context.OperationType) && context.OperationType.Equals("change", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("ChangeControlProcess", out var changeObj) || changeObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "ITIL-CHANGE",
                        Description = "Change enablement process not followed",
                        Severity = ViolationSeverity.Medium,
                        Remediation = "Follow ITIL change enablement practice for all changes",
                        RegulatoryReference = "ITIL v4 Change Enablement"
                    });
                }
            }

            if (!context.Attributes.TryGetValue("IncidentManagement", out var incObj) || incObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "ITIL-INCIDENT",
                    Description = "Incident management process not established",
                    Severity = ViolationSeverity.Medium,
                    Remediation = "Implement incident management with categorization and prioritization",
                    RegulatoryReference = "ITIL v4 Incident Management"
                });
            }

            recommendations.Add("Align service management practices with ITIL 4 guiding principles");

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
            IncrementCounter("itil.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("itil.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
