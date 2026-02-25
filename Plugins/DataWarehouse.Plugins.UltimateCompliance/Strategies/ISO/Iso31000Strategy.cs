using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.ISO
{
    /// <summary>
    /// ISO 31000 Risk Management compliance strategy.
    /// </summary>
    public sealed class Iso31000Strategy : ComplianceStrategyBase
    {
        /// <inheritdoc/>
        public override string StrategyId => "iso31000";

        /// <inheritdoc/>
        public override string StrategyName => "ISO 31000";

        /// <inheritdoc/>
        public override string Framework => "ISO31000";

        /// <inheritdoc/>
        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
        IncrementCounter("iso31000.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            // Check risk assessment conducted (6.4)
            if (!context.Attributes.TryGetValue("RiskAssessmentCompleted", out var assessmentObj) || assessmentObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "ISO31000-001",
                    Description = "Risk assessment not conducted for operation",
                    Severity = ViolationSeverity.High,
                    Remediation = "Conduct systematic risk identification, analysis, and evaluation",
                    RegulatoryReference = "ISO 31000:2018 6.4"
                });
            }

            // Check risk treatment plan (6.5)
            if (context.Attributes.TryGetValue("RiskLevel", out var riskObj) &&
                riskObj is string riskLevel &&
                (riskLevel.Equals("high", StringComparison.OrdinalIgnoreCase) ||
                 riskLevel.Equals("critical", StringComparison.OrdinalIgnoreCase)))
            {
                if (!context.Attributes.TryGetValue("RiskTreatmentPlan", out var treatmentObj) || treatmentObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "ISO31000-002",
                        Description = "Risk treatment plan not defined for high/critical risk",
                        Severity = ViolationSeverity.High,
                        Remediation = "Develop and implement risk treatment plan with mitigation controls",
                        RegulatoryReference = "ISO 31000:2018 6.5"
                    });
                }
            }

            // Check risk monitoring and review (6.6)
            if (!context.Attributes.TryGetValue("RiskMonitoringEnabled", out var monitoringObj) || monitoringObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "ISO31000-003",
                    Description = "Risk monitoring and review process not established",
                    Severity = ViolationSeverity.Medium,
                    Remediation = "Implement continuous risk monitoring with periodic reviews",
                    RegulatoryReference = "ISO 31000:2018 6.6"
                });
            }

            // Check risk communication (6.2)
            if (context.Attributes.TryGetValue("RiskLevel", out var riskLevelObj) &&
                riskLevelObj is string level &&
                level.Equals("critical", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("StakeholderNotified", out var notifiedObj) || notifiedObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "ISO31000-004",
                        Description = "Critical risk not communicated to stakeholders",
                        Severity = ViolationSeverity.High,
                        Remediation = "Communicate critical risks to relevant stakeholders for decision-making",
                        RegulatoryReference = "ISO 31000:2018 6.2"
                    });
                }
            }

            // Check integration with decision-making (5.4.7)
            if (!context.Attributes.TryGetValue("RiskInformedDecision", out var decisionObj) || decisionObj is not true)
            {
                recommendations.Add("Integrate risk considerations into operational decision-making processes");
            }

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
        IncrementCounter("iso31000.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
        IncrementCounter("iso31000.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
