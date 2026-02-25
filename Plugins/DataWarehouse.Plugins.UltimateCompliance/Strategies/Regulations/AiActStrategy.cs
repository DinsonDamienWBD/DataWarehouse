using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.Regulations
{
    /// <summary>
    /// EU AI Act compliance strategy.
    /// Validates AI systems against risk classification, transparency, and governance requirements.
    /// </summary>
    public sealed class AiActStrategy : ComplianceStrategyBase
    {
        private readonly HashSet<string> _prohibitedPractices = new(StringComparer.OrdinalIgnoreCase)
        {
            "subliminal-manipulation", "social-scoring", "real-time-biometric-public",
            "emotion-recognition-workplace", "predictive-policing-individuals"
        };

        private readonly HashSet<string> _highRiskSystems = new(StringComparer.OrdinalIgnoreCase)
        {
            "biometric-identification", "critical-infrastructure", "educational-assessment",
            "employment-management", "access-to-services", "law-enforcement", "migration-control"
        };

        /// <inheritdoc/>
        public override string StrategyId => "eu-ai-act";

        /// <inheritdoc/>
        public override string StrategyName => "EU AI Act Compliance";

        /// <inheritdoc/>
        public override string Framework => "EU-AI-ACT";

        /// <inheritdoc/>
        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("ai_act.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            CheckProhibitedPractices(context, violations, recommendations);
            CheckRiskClassification(context, violations, recommendations);
            CheckTransparencyRequirements(context, violations, recommendations);
            CheckHumanOversight(context, violations, recommendations);
            CheckDataGovernance(context, violations, recommendations);

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

        private void CheckProhibitedPractices(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (context.Attributes.TryGetValue("AiPracticeType", out var practiceObj) &&
                practiceObj is string practice &&
                _prohibitedPractices.Contains(practice))
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "AIACT-001",
                    Description = $"Prohibited AI practice detected: {practice}",
                    Severity = ViolationSeverity.Critical,
                    Remediation = "Immediately cease prohibited AI practice",
                    RegulatoryReference = "AI Act Article 5"
                });
            }
        }

        private void CheckRiskClassification(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (!context.Attributes.TryGetValue("RiskClassification", out var riskObj) ||
                riskObj is not string riskClass ||
                string.IsNullOrEmpty(riskClass))
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "AIACT-002",
                    Description = "No risk classification assigned to AI system",
                    Severity = ViolationSeverity.High,
                    Remediation = "Classify system as unacceptable, high, limited, or minimal risk",
                    RegulatoryReference = "AI Act Article 6-7"
                });
                return;
            }

            if (riskClass.Equals("high-risk", StringComparison.OrdinalIgnoreCase) ||
                context.DataSubjectCategories.Any(c => _highRiskSystems.Contains(c)))
            {
                if (!context.Attributes.TryGetValue("ConformityAssessment", out var assessmentObj) || assessmentObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "AIACT-003",
                        Description = "High-risk AI system requires conformity assessment",
                        Severity = ViolationSeverity.Critical,
                        Remediation = "Complete conformity assessment and obtain CE marking",
                        RegulatoryReference = "AI Act Article 43"
                    });
                }

                if (!context.Attributes.TryGetValue("RiskManagementSystem", out var rmsObj) || rmsObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "AIACT-004",
                        Description = "High-risk AI system requires risk management system",
                        Severity = ViolationSeverity.High,
                        Remediation = "Establish continuous risk management throughout AI system lifecycle",
                        RegulatoryReference = "AI Act Article 9"
                    });
                }
            }
        }

        private void CheckTransparencyRequirements(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (context.Attributes.TryGetValue("UserFacingAi", out var userFacingObj) && userFacingObj is true)
            {
                if (!context.Attributes.TryGetValue("AiDisclosure", out var disclosureObj) || disclosureObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "AIACT-005",
                        Description = "User-facing AI system must disclose AI interaction",
                        Severity = ViolationSeverity.Medium,
                        Remediation = "Inform users they are interacting with an AI system",
                        RegulatoryReference = "AI Act Article 52"
                    });
                }
            }

            if (context.Attributes.TryGetValue("GeneratedContent", out var generatedObj) && generatedObj is true)
            {
                if (!context.Attributes.TryGetValue("ContentLabeling", out var labelObj) || labelObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "AIACT-006",
                        Description = "AI-generated content must be labeled",
                        Severity = ViolationSeverity.Medium,
                        Remediation = "Mark content as artificially generated or manipulated",
                        RegulatoryReference = "AI Act Article 52"
                    });
                }
            }
        }

        private void CheckHumanOversight(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (context.Attributes.TryGetValue("RiskClassification", out var riskObj) &&
                riskObj is string riskClass &&
                riskClass.Equals("high-risk", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("HumanOversightMeasures", out var oversightObj) || oversightObj is not true)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "AIACT-007",
                        Description = "High-risk AI system requires human oversight measures",
                        Severity = ViolationSeverity.High,
                        Remediation = "Implement human oversight with stop/override capabilities",
                        RegulatoryReference = "AI Act Article 14"
                    });
                }
            }
        }

        private void CheckDataGovernance(ComplianceContext context, List<ComplianceViolation> violations, List<string> recommendations)
        {
            if (context.Attributes.TryGetValue("TrainingDataQuality", out var qualityObj) && qualityObj is false)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "AIACT-008",
                    Description = "AI training data quality assessment not completed",
                    Severity = ViolationSeverity.Medium,
                    Remediation = "Assess training data for relevance, representativeness, and bias",
                    RegulatoryReference = "AI Act Article 10"
                });
            }

            if (context.DataClassification.Equals("sensitive", StringComparison.OrdinalIgnoreCase))
            {
                recommendations.Add("Ensure AI system respects fundamental rights when processing sensitive data");
            }
        }
    
    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("ai_act.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("ai_act.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
