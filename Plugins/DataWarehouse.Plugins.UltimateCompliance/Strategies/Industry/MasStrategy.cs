using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.Industry
{
    /// <summary>
    /// MAS TRM (Monetary Authority of Singapore Technology Risk Management) compliance strategy.
    /// </summary>
    public sealed class MasStrategy : ComplianceStrategyBase
    {
        public override string StrategyId => "mas-trm";
        public override string StrategyName => "MAS TRM";
        public override string Framework => "MAS-TRM";

        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("mas.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            if (!context.Attributes.TryGetValue("TechnologyRiskGovernance", out var govObj) || govObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "MAS-TRM-3",
                    Description = "Technology risk management governance not established",
                    Severity = ViolationSeverity.High,
                    Remediation = "Establish board and senior management oversight of technology risks (TRM 3)",
                    RegulatoryReference = "MAS TRM Guidelines 3"
                });
            }

            if (!context.Attributes.TryGetValue("OutsourcingRiskManaged", out var outsourceObj) || outsourceObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "MAS-TRM-9",
                    Description = "Outsourcing risk management not implemented",
                    Severity = ViolationSeverity.High,
                    Remediation = "Implement due diligence and ongoing monitoring of service providers (TRM 9)",
                    RegulatoryReference = "MAS TRM Guidelines 9"
                });
            }

            if (!context.Attributes.TryGetValue("BusinessContinuityPlan", out var bcpObj) || bcpObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "MAS-TRM-15",
                    Description = "Business continuity plan not established",
                    Severity = ViolationSeverity.Critical,
                    Remediation = "Develop BCP with defined RTO/RPO and regular testing (TRM 15)",
                    RegulatoryReference = "MAS TRM Guidelines 15"
                });
            }

            if (!context.Attributes.TryGetValue("CyberHygieneAssessment", out var hygieneObj) || hygieneObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "MAS-TRM-CYBER",
                    Description = "Cyber hygiene assessment not conducted",
                    Severity = ViolationSeverity.Medium,
                    Remediation = "Conduct regular cyber hygiene assessments per MAS requirements",
                    RegulatoryReference = "MAS Cyber Hygiene Guidelines"
                });
            }

            recommendations.Add("Conduct annual independent technology risk audit");

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
            IncrementCounter("mas.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("mas.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
