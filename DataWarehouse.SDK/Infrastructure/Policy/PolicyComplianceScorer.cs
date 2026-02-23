using System;
using System.Collections.Generic;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Policy;

namespace DataWarehouse.SDK.Infrastructure.Policy
{
    /// <summary>
    /// Represents the evaluation result of a single regulatory requirement against the actual policy configuration.
    /// Contains pass/fail status, actual values found, identified gaps, and remediation guidance.
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 70: Compliance scoring (PADV-03)")]
    public sealed record ComplianceGapItem
    {
        /// <summary>
        /// The regulatory requirement that was evaluated.
        /// </summary>
        public required RegulatoryRequirement Requirement { get; init; }

        /// <summary>
        /// Whether the policy configuration satisfies this requirement.
        /// </summary>
        public bool Passed { get; init; }

        /// <summary>
        /// The actual intensity level found in the policy, or null if the feature was not configured.
        /// </summary>
        public int? ActualIntensity { get; init; }

        /// <summary>
        /// The actual cascade strategy found in the policy, or null if not evaluated or feature not found.
        /// </summary>
        public CascadeStrategy? ActualCascade { get; init; }

        /// <summary>
        /// The actual AI autonomy level found in the policy, or null if not evaluated or feature not found.
        /// </summary>
        public AiAutonomyLevel? ActualAiAutonomy { get; init; }

        /// <summary>
        /// Parameter keys that were required by the regulation but absent from the policy's custom parameters.
        /// Empty when all required parameters are present or no parameters were required.
        /// </summary>
        public IReadOnlyList<string> MissingParameters { get; init; } = Array.Empty<string>();

        /// <summary>
        /// Human-readable description of the compliance gap when <see cref="Passed"/> is false.
        /// Empty string when the requirement is satisfied.
        /// </summary>
        public string GapDescription { get; init; } = string.Empty;

        /// <summary>
        /// Actionable remediation guidance copied from the requirement. Present regardless of pass/fail
        /// to support proactive compliance improvement.
        /// </summary>
        public string Remediation { get; init; } = string.Empty;
    }

    /// <summary>
    /// Complete compliance score report for a single regulatory framework. Contains the numeric score,
    /// letter grade, requirement-level details, and a filtered list of gaps needing remediation.
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 70: Compliance scoring (PADV-03)")]
    public sealed record ComplianceScoreReport
    {
        /// <summary>
        /// Name of the regulatory framework that was evaluated (e.g., "HIPAA", "GDPR").
        /// </summary>
        public required string FrameworkName { get; init; }

        /// <summary>
        /// Version of the regulatory template used for evaluation.
        /// </summary>
        public required string FrameworkVersion { get; init; }

        /// <summary>
        /// Weighted compliance score from 0 (no compliance) to 100 (full compliance).
        /// Calculated as: sum(passed_requirement.Weight) / sum(all_requirement.Weight) * 100.
        /// </summary>
        public int Score { get; init; }

        /// <summary>
        /// Total number of requirements evaluated.
        /// </summary>
        public int TotalRequirements { get; init; }

        /// <summary>
        /// Number of requirements that passed evaluation.
        /// </summary>
        public int PassedRequirements { get; init; }

        /// <summary>
        /// Number of requirements that failed evaluation.
        /// </summary>
        public int FailedRequirements { get; init; }

        /// <summary>
        /// Only the failed items requiring remediation. Subset of <see cref="AllItems"/>.
        /// </summary>
        public required IReadOnlyList<ComplianceGapItem> Gaps { get; init; }

        /// <summary>
        /// All evaluated items including both passed and failed requirements.
        /// </summary>
        public required IReadOnlyList<ComplianceGapItem> AllItems { get; init; }

        /// <summary>
        /// Timestamp when this score was computed.
        /// </summary>
        public DateTimeOffset ScoredAt { get; init; }

        /// <summary>
        /// Letter grade derived from <see cref="Score"/>: A (90-100), B (80-89), C (70-79), D (60-69), F (0-59).
        /// </summary>
        public string Grade { get; init; } = "F";
    }

    /// <summary>
    /// Evaluates a set of feature policies against regulatory compliance templates and produces
    /// scored gap analysis reports with per-requirement pass/fail, weighted scoring, letter grades,
    /// and actionable remediation guidance.
    /// <para>
    /// The scorer reads policies but does not modify them -- it is a read-only analysis tool
    /// that gives operators visibility into how their policy configuration stacks up against
    /// HIPAA, GDPR, SOC2, FedRAMP, or custom regulatory requirements.
    /// </para>
    /// </summary>
    /// <remarks>
    /// <b>Usage example:</b>
    /// <code>
    /// var policies = OperationalProfile.Strict().FeaturePolicies;
    /// var scorer = new PolicyComplianceScorer(policies);
    ///
    /// // Score against a single framework
    /// var hipaaReport = scorer.ScoreAgainst(RegulatoryTemplate.Hipaa());
    /// Console.WriteLine($"HIPAA: {hipaaReport.Score}/100 ({hipaaReport.Grade})");
    ///
    /// // Score against all built-in frameworks
    /// var allReports = PolicyComplianceScorer.ScoreAll(policies);
    /// foreach (var report in allReports)
    ///     Console.WriteLine($"{report.FrameworkName}: {report.Score}/100 ({report.Grade})");
    /// </code>
    /// </remarks>
    [SdkCompatibility("6.0.0", Notes = "Phase 70: Compliance scoring (PADV-03)")]
    public sealed class PolicyComplianceScorer
    {
        private readonly IReadOnlyDictionary<string, FeaturePolicy> _policies;

        /// <summary>
        /// Initializes a new <see cref="PolicyComplianceScorer"/> that evaluates the specified policies.
        /// </summary>
        /// <param name="policies">
        /// The feature policies to score, keyed by feature identifier. Typically obtained from
        /// <see cref="OperationalProfile.FeaturePolicies"/> or resolved effective policies.
        /// Must not be null.
        /// </param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="policies"/> is null.</exception>
        public PolicyComplianceScorer(IReadOnlyDictionary<string, FeaturePolicy> policies)
        {
            _policies = policies ?? throw new ArgumentNullException(nameof(policies));
        }

        /// <summary>
        /// Scores the policies against a single regulatory template and produces a detailed gap analysis report.
        /// </summary>
        /// <param name="template">The regulatory template to evaluate against. Must not be null.</param>
        /// <returns>
        /// A <see cref="ComplianceScoreReport"/> containing the weighted score (0-100), letter grade,
        /// per-requirement pass/fail details, and remediation guidance for any gaps.
        /// </returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="template"/> is null.</exception>
        public ComplianceScoreReport ScoreAgainst(RegulatoryTemplate template)
        {
            if (template == null) throw new ArgumentNullException(nameof(template));

            var allItems = new List<ComplianceGapItem>();
            var gaps = new List<ComplianceGapItem>();
            double totalWeight = 0.0;
            double passedWeight = 0.0;
            int passedCount = 0;

            foreach (var requirement in template.Requirements)
            {
                totalWeight += requirement.Weight;
                var item = EvaluateRequirement(requirement);
                allItems.Add(item);

                if (item.Passed)
                {
                    passedWeight += requirement.Weight;
                    passedCount++;
                }
                else
                {
                    gaps.Add(item);
                }
            }

            int score = totalWeight > 0.0
                ? (int)Math.Round(passedWeight / totalWeight * 100.0)
                : 0;

            return new ComplianceScoreReport
            {
                FrameworkName = template.FrameworkName,
                FrameworkVersion = template.FrameworkVersion,
                Score = score,
                TotalRequirements = template.Requirements.Count,
                PassedRequirements = passedCount,
                FailedRequirements = template.Requirements.Count - passedCount,
                Gaps = gaps,
                AllItems = allItems,
                ScoredAt = DateTimeOffset.UtcNow,
                Grade = DeriveGrade(score)
            };
        }

        /// <summary>
        /// Scores the policies against multiple regulatory templates.
        /// </summary>
        /// <param name="templates">The regulatory templates to evaluate against.</param>
        /// <returns>A list of <see cref="ComplianceScoreReport"/> instances, one per template.</returns>
        public IReadOnlyList<ComplianceScoreReport> ScoreAll(params RegulatoryTemplate[] templates)
        {
            if (templates == null) throw new ArgumentNullException(nameof(templates));

            var reports = new List<ComplianceScoreReport>(templates.Length);
            foreach (var template in templates)
            {
                reports.Add(ScoreAgainst(template));
            }
            return reports;
        }

        /// <summary>
        /// Convenience method that scores the given policies against all four built-in regulatory templates
        /// (HIPAA, GDPR, SOC2, FedRAMP).
        /// </summary>
        /// <param name="policies">The feature policies to score. Must not be null.</param>
        /// <returns>A list of four <see cref="ComplianceScoreReport"/> instances.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="policies"/> is null.</exception>
        public static IReadOnlyList<ComplianceScoreReport> ScoreAll(IReadOnlyDictionary<string, FeaturePolicy> policies)
        {
            var scorer = new PolicyComplianceScorer(policies);
            return scorer.ScoreAll(RegulatoryTemplate.All());
        }

        /// <summary>
        /// Evaluates a single regulatory requirement against the loaded policies and returns
        /// a detailed gap item with pass/fail status, actual values, and remediation.
        /// </summary>
        private ComplianceGapItem EvaluateRequirement(RegulatoryRequirement requirement)
        {
            if (!_policies.TryGetValue(requirement.FeatureId, out var policy))
            {
                return new ComplianceGapItem
                {
                    Requirement = requirement,
                    Passed = false,
                    ActualIntensity = null,
                    ActualCascade = null,
                    ActualAiAutonomy = null,
                    MissingParameters = Array.Empty<string>(),
                    GapDescription = $"Feature '{requirement.FeatureId}' is not configured in the active policies.",
                    Remediation = requirement.Remediation
                };
            }

            var failReasons = new List<string>();
            var missingParams = new List<string>();

            // Check minimum intensity
            if (requirement.MinimumIntensity > 0 && policy.IntensityLevel < requirement.MinimumIntensity)
            {
                failReasons.Add($"Intensity {policy.IntensityLevel} is below minimum {requirement.MinimumIntensity}.");
            }

            // Check required cascade strategy
            if (requirement.RequiredCascade.HasValue && policy.Cascade != requirement.RequiredCascade.Value)
            {
                failReasons.Add($"Cascade is {policy.Cascade} but {requirement.RequiredCascade.Value} is required.");
            }

            // Check maximum AI autonomy
            if (requirement.MaxAiAutonomy.HasValue && (int)policy.AiAutonomy > (int)requirement.MaxAiAutonomy.Value)
            {
                failReasons.Add($"AI autonomy {policy.AiAutonomy} exceeds maximum {requirement.MaxAiAutonomy.Value}.");
            }

            // Check required parameters
            if (requirement.RequiredParameters != null && requirement.RequiredParameters.Count > 0)
            {
                foreach (var kvp in requirement.RequiredParameters)
                {
                    if (policy.CustomParameters == null || !policy.CustomParameters.ContainsKey(kvp.Key))
                    {
                        missingParams.Add(kvp.Key);
                        failReasons.Add($"Required parameter '{kvp.Key}' is missing.");
                    }
                    else if (!string.IsNullOrEmpty(kvp.Value) &&
                             !string.Equals(policy.CustomParameters[kvp.Key], kvp.Value, StringComparison.Ordinal))
                    {
                        failReasons.Add($"Parameter '{kvp.Key}' value '{policy.CustomParameters[kvp.Key]}' does not match required '{kvp.Value}'.");
                    }
                }
            }

            bool passed = failReasons.Count == 0;

            return new ComplianceGapItem
            {
                Requirement = requirement,
                Passed = passed,
                ActualIntensity = policy.IntensityLevel,
                ActualCascade = policy.Cascade,
                ActualAiAutonomy = policy.AiAutonomy,
                MissingParameters = missingParams,
                GapDescription = passed ? string.Empty : string.Join(" ", failReasons),
                Remediation = requirement.Remediation
            };
        }

        /// <summary>
        /// Derives a letter grade from a numeric score: A (90-100), B (80-89), C (70-79), D (60-69), F (0-59).
        /// </summary>
        private static string DeriveGrade(int score)
        {
            if (score >= 90) return "A";
            if (score >= 80) return "B";
            if (score >= 70) return "C";
            if (score >= 60) return "D";
            return "F";
        }
    }
}
