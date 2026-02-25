using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.NIST
{
    /// <summary>
    /// NIST AI Risk Management Framework compliance strategy.
    /// </summary>
    public sealed class NistAiRmfStrategy : ComplianceStrategyBase
    {
        /// <inheritdoc/>
        public override string StrategyId => "nist-ai-rmf";

        /// <inheritdoc/>
        public override string StrategyName => "NIST AI RMF";

        /// <inheritdoc/>
        public override string Framework => "NIST-AI-RMF";

        /// <inheritdoc/>
        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("nist_ai_rmf.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            bool isAiOperation = context.Attributes.TryGetValue("UsesAI", out var aiObj) && aiObj is true;

            if (!isAiOperation)
            {
                return Task.FromResult(new ComplianceResult
                {
                    IsCompliant = true,
                    Framework = Framework,
                    Status = ComplianceStatus.NotApplicable,
                    Violations = violations,
                    Recommendations = new List<string> { "NIST AI RMF applies only to AI system operations" }
                });
            }

            // Check GOVERN function
            if (!context.Attributes.TryGetValue("AiGovernanceEstablished", out var governObj) || governObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "NIST-AI-RMF-GOVERN",
                    Description = "AI governance structure not established",
                    Severity = ViolationSeverity.High,
                    Remediation = "Establish AI governance with policies, roles, and accountability (GOVERN)",
                    RegulatoryReference = "NIST AI RMF GOVERN function"
                });
            }

            // Check MAP function - risk identification
            if (!context.Attributes.TryGetValue("AiRisksMapped", out var mapObj) || mapObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "NIST-AI-RMF-MAP",
                    Description = "AI risks not systematically identified and mapped",
                    Severity = ViolationSeverity.Critical,
                    Remediation = "Map context, categorize AI system, and identify risks (MAP)",
                    RegulatoryReference = "NIST AI RMF MAP function"
                });
            }

            // Check MEASURE function - performance metrics
            if (!context.Attributes.TryGetValue("AiMetricsDefined", out var measureObj) || measureObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "NIST-AI-RMF-MEASURE",
                    Description = "AI risk metrics and measurement not defined",
                    Severity = ViolationSeverity.High,
                    Remediation = "Define and track trustworthiness metrics (MEASURE)",
                    RegulatoryReference = "NIST AI RMF MEASURE function"
                });
            }

            // Check MANAGE function - risk mitigation
            if (!context.Attributes.TryGetValue("AiRiskManagementPlan", out var manageObj) || manageObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "NIST-AI-RMF-MANAGE",
                    Description = "AI risk management plan not implemented",
                    Severity = ViolationSeverity.High,
                    Remediation = "Implement continuous risk management with mitigation strategies (MANAGE)",
                    RegulatoryReference = "NIST AI RMF MANAGE function"
                });
            }

            // Check trustworthy characteristics
            if (!context.Attributes.TryGetValue("AiFairnessAssessed", out var fairnessObj) || fairnessObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "NIST-AI-RMF-FAIRNESS",
                    Description = "AI system fairness and bias not assessed",
                    Severity = ViolationSeverity.High,
                    Remediation = "Assess and mitigate algorithmic bias and discrimination",
                    RegulatoryReference = "NIST AI RMF - Fairness characteristic"
                });
            }

            recommendations.Add("Align AI risk management with organizational ERM framework");

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
            IncrementCounter("nist_ai_rmf.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("nist_ai_rmf.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
