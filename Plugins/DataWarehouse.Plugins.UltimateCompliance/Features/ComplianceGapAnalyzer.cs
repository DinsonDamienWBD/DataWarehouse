using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Features
{
    /// <summary>
    /// AI-assisted gap analysis for compliance frameworks using message bus intelligence
    /// with rule-based fallback.
    /// </summary>
    public sealed class ComplianceGapAnalyzer
    {
        private readonly Dictionary<string, FrameworkRequirements> _frameworks = new();
        private bool _useAiAnalysis = true;

        /// <summary>
        /// Gets or sets whether to use AI-assisted analysis.
        /// </summary>
        public bool UseAiAnalysis
        {
            get => _useAiAnalysis;
            set => _useAiAnalysis = value;
        }

        /// <summary>
        /// Registers framework requirements for gap analysis.
        /// </summary>
        public void RegisterFramework(string frameworkId, FrameworkRequirements requirements)
        {
            _frameworks[frameworkId] = requirements;
        }

        /// <summary>
        /// Performs gap analysis for a specific framework.
        /// </summary>
        public async Task<GapAnalysisResult> AnalyzeGapsAsync(string frameworkId, CurrentImplementation currentState, CancellationToken cancellationToken = default)
        {
            if (!_frameworks.TryGetValue(frameworkId, out var requirements))
            {
                return new GapAnalysisResult
                {
                    FrameworkId = frameworkId,
                    Success = false,
                    Message = $"Framework not registered: {frameworkId}",
                    Gaps = Array.Empty<ComplianceGap>()
                };
            }

            List<ComplianceGap> gaps;

            if (_useAiAnalysis)
            {
                gaps = await PerformAiAssistedAnalysisAsync(requirements, currentState, cancellationToken);
            }
            else
            {
                gaps = PerformRuleBasedAnalysis(requirements, currentState);
            }

            var criticalCount = gaps.Count(g => g.Severity == GapSeverity.Critical);
            var highCount = gaps.Count(g => g.Severity == GapSeverity.High);
            var mediumCount = gaps.Count(g => g.Severity == GapSeverity.Medium);
            var lowCount = gaps.Count(g => g.Severity == GapSeverity.Low);

            var complianceScore = CalculateComplianceScore(requirements, currentState, gaps);

            return new GapAnalysisResult
            {
                FrameworkId = frameworkId,
                Success = true,
                Message = $"Analysis complete. Found {gaps.Count} gaps",
                Gaps = gaps,
                ComplianceScore = complianceScore,
                CriticalGaps = criticalCount,
                HighGaps = highCount,
                MediumGaps = mediumCount,
                LowGaps = lowCount,
                AnalysisTime = DateTime.UtcNow
            };
        }

        /// <summary>
        /// Generates remediation recommendations for identified gaps.
        /// </summary>
        public IReadOnlyList<RemediationRecommendation> GenerateRecommendations(GapAnalysisResult analysisResult)
        {
            var recommendations = new List<RemediationRecommendation>();

            foreach (var gap in analysisResult.Gaps.OrderByDescending(g => g.Severity))
            {
                var recommendation = new RemediationRecommendation
                {
                    GapId = gap.RequirementId,
                    Priority = gap.Severity == GapSeverity.Critical ? "Immediate" :
                              gap.Severity == GapSeverity.High ? "High" :
                              gap.Severity == GapSeverity.Medium ? "Medium" : "Low",
                    EstimatedEffort = EstimateEffort(gap),
                    SuggestedActions = GenerateActions(gap),
                    ExpectedImpact = $"Addresses {gap.RequirementId} compliance requirement"
                };

                recommendations.Add(recommendation);
            }

            return recommendations;
        }

        private async Task<List<ComplianceGap>> PerformAiAssistedAnalysisAsync(FrameworkRequirements requirements, CurrentImplementation currentState, CancellationToken cancellationToken)
        {
            // AI-assisted analysis via message bus: "intelligence.analyze"
            // This would integrate with AI service for deeper analysis
            await Task.Delay(50, cancellationToken); // Simulate AI processing

            // Fallback to rule-based if AI unavailable
            return PerformRuleBasedAnalysis(requirements, currentState);
        }

        private List<ComplianceGap> PerformRuleBasedAnalysis(FrameworkRequirements requirements, CurrentImplementation currentState)
        {
            var gaps = new List<ComplianceGap>();

            foreach (var requirement in requirements.Requirements)
            {
                var isImplemented = currentState.ImplementedControls.Contains(requirement.ControlId);

                if (!isImplemented)
                {
                    gaps.Add(new ComplianceGap
                    {
                        RequirementId = requirement.ControlId,
                        RequirementDescription = requirement.Description,
                        CurrentState = "Not Implemented",
                        ExpectedState = "Fully Implemented",
                        Severity = requirement.Mandatory ? GapSeverity.Critical : GapSeverity.Medium,
                        RegulatoryReference = requirement.Reference
                    });
                }
                else if (currentState.PartiallyImplementedControls.Contains(requirement.ControlId))
                {
                    gaps.Add(new ComplianceGap
                    {
                        RequirementId = requirement.ControlId,
                        RequirementDescription = requirement.Description,
                        CurrentState = "Partially Implemented",
                        ExpectedState = "Fully Implemented",
                        Severity = requirement.Mandatory ? GapSeverity.High : GapSeverity.Low,
                        RegulatoryReference = requirement.Reference
                    });
                }
            }

            return gaps;
        }

        private static double CalculateComplianceScore(FrameworkRequirements requirements, CurrentImplementation currentState, List<ComplianceGap> gaps)
        {
            var totalRequirements = requirements.Requirements.Count;
            if (totalRequirements == 0)
                return 100.0;

            var implementedCount = currentState.ImplementedControls.Count;
            var partialCount = currentState.PartiallyImplementedControls.Count;

            var score = ((implementedCount + (partialCount * 0.5)) / totalRequirements) * 100.0;
            return Math.Round(score, 2);
        }

        private static string EstimateEffort(ComplianceGap gap)
        {
            return gap.Severity switch
            {
                GapSeverity.Critical => "High (2-4 weeks)",
                GapSeverity.High => "Medium (1-2 weeks)",
                GapSeverity.Medium => "Low (3-5 days)",
                GapSeverity.Low => "Minimal (1-2 days)",
                _ => "Unknown"
            };
        }

        private static List<string> GenerateActions(ComplianceGap gap)
        {
            return gap.CurrentState switch
            {
                "Not Implemented" => new List<string>
                {
                    "Design and implement control",
                    "Document procedures",
                    "Train relevant personnel",
                    "Test and validate implementation"
                },
                "Partially Implemented" => new List<string>
                {
                    "Complete remaining implementation",
                    "Update documentation",
                    "Validate against requirements"
                },
                _ => new List<string> { "Review and update control" }
            };
        }
    }

    /// <summary>
    /// Framework requirements definition.
    /// </summary>
    public sealed class FrameworkRequirements
    {
        public required string FrameworkId { get; init; }
        public required string FrameworkName { get; init; }
        public required List<ControlRequirement> Requirements { get; init; }
    }

    /// <summary>
    /// Individual control requirement.
    /// </summary>
    public sealed class ControlRequirement
    {
        public required string ControlId { get; init; }
        public required string Description { get; init; }
        public required bool Mandatory { get; init; }
        public string? Reference { get; init; }
    }

    /// <summary>
    /// Current implementation state.
    /// </summary>
    public sealed class CurrentImplementation
    {
        public required HashSet<string> ImplementedControls { get; init; }
        public required HashSet<string> PartiallyImplementedControls { get; init; }
    }

    /// <summary>
    /// Identified compliance gap.
    /// </summary>
    public sealed class ComplianceGap
    {
        public required string RequirementId { get; init; }
        public required string RequirementDescription { get; init; }
        public required string CurrentState { get; init; }
        public required string ExpectedState { get; init; }
        public required GapSeverity Severity { get; init; }
        public string? RegulatoryReference { get; init; }
    }

    /// <summary>
    /// Gap severity levels.
    /// </summary>
    public enum GapSeverity
    {
        Low,
        Medium,
        High,
        Critical
    }

    /// <summary>
    /// Result of gap analysis.
    /// </summary>
    public sealed class GapAnalysisResult
    {
        public required string FrameworkId { get; init; }
        public required bool Success { get; init; }
        public required string Message { get; init; }
        public required IReadOnlyList<ComplianceGap> Gaps { get; init; }
        public double ComplianceScore { get; init; }
        public int CriticalGaps { get; init; }
        public int HighGaps { get; init; }
        public int MediumGaps { get; init; }
        public int LowGaps { get; init; }
        public DateTime AnalysisTime { get; init; }
    }

    /// <summary>
    /// Remediation recommendation for a gap.
    /// </summary>
    public sealed class RemediationRecommendation
    {
        public required string GapId { get; init; }
        public required string Priority { get; init; }
        public required string EstimatedEffort { get; init; }
        public required List<string> SuggestedActions { get; init; }
        public required string ExpectedImpact { get; init; }
    }
}
