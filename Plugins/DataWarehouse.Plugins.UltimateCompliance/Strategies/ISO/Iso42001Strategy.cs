using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.ISO
{
    /// <summary>
    /// ISO/IEC 42001 AI Management System compliance strategy.
    /// </summary>
    public sealed class Iso42001Strategy : ComplianceStrategyBase
    {
        /// <inheritdoc/>
        public override string StrategyId => "iso42001";

        /// <inheritdoc/>
        public override string StrategyName => "ISO/IEC 42001";

        /// <inheritdoc/>
        public override string Framework => "ISO42001";

        /// <inheritdoc/>
        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("iso42001.check");
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
                    Recommendations = new List<string> { "ISO 42001 applies only to AI system operations" }
                });
            }

            // Check AI lifecycle management (6.2.5)
            if (!context.Attributes.TryGetValue("AiLifecycleManaged", out var lifecycleObj) || lifecycleObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "ISO42001-001",
                    Description = "AI lifecycle management not established",
                    Severity = ViolationSeverity.High,
                    Remediation = "Implement AI lifecycle governance covering development, deployment, and decommissioning",
                    RegulatoryReference = "ISO/IEC 42001:2023 6.2.5"
                });
            }

            // Check AI risk assessment (6.1.2)
            if (!context.Attributes.TryGetValue("AiRiskAssessed", out var riskObj) || riskObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "ISO42001-002",
                    Description = "AI-specific risk assessment not conducted",
                    Severity = ViolationSeverity.Critical,
                    Remediation = "Conduct AI risk assessment including bias, fairness, and safety concerns",
                    RegulatoryReference = "ISO/IEC 42001:2023 6.1.2"
                });
            }

            // Check transparency and explainability (6.2.8)
            if (!string.IsNullOrEmpty(context.OperationType) && context.OperationType.Equals("automated-decision", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("AiExplainabilityProvided", out var explainObj) || explainObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "ISO42001-003",
                        Description = "AI decision explainability not provided",
                        Severity = ViolationSeverity.High,
                        Remediation = "Implement explainability mechanisms for AI-driven decisions",
                        RegulatoryReference = "ISO/IEC 42001:2023 6.2.8"
                    });
                }
            }

            // Check AI governance structure (5.1)
            if (!context.Attributes.TryGetValue("AiGovernanceEstablished", out var governanceObj) || governanceObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "ISO42001-004",
                    Description = "AI governance structure not established",
                    Severity = ViolationSeverity.Medium,
                    Remediation = "Establish AI governance with defined roles, responsibilities, and oversight",
                    RegulatoryReference = "ISO/IEC 42001:2023 5.1"
                });
            }

            // Check data quality for AI (6.2.3)
            if (!context.Attributes.TryGetValue("AiDataQualityVerified", out var qualityObj) || qualityObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "ISO42001-005",
                    Description = "AI training/inference data quality not verified",
                    Severity = ViolationSeverity.High,
                    Remediation = "Implement data quality controls for AI model training and inference",
                    RegulatoryReference = "ISO/IEC 42001:2023 6.2.3"
                });
            }

            recommendations.Add("Monitor AI system performance and retrain models when drift is detected");

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
            IncrementCounter("iso42001.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("iso42001.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
