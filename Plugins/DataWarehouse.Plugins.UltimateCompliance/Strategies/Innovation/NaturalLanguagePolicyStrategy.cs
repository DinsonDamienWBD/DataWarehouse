using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.Innovation
{
    /// <summary>
    /// Natural language policy compliance strategy that parses and enforces
    /// compliance policies written in human-readable language using AI/NLP.
    /// </summary>
    public sealed class NaturalLanguagePolicyStrategy : ComplianceStrategyBase
    {
        public override string StrategyId => "natural-language-policy";
        public override string StrategyName => "Natural Language Policy";
        public override string Framework => "Innovation-NLP";

        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            var violations = new List<ComplianceViolation>();

            // Check 1: Verify policy is in natural language format
            if (!context.Attributes.TryGetValue("PolicyFormat", out var format) ||
                format?.ToString() != "NaturalLanguage")
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "NLP-001",
                    Description = "Policy not in natural language format",
                    Severity = ViolationSeverity.Medium,
                    AffectedResource = context.ResourceId,
                    Remediation = "Convert policies to human-readable natural language",
                    RegulatoryReference = "NLP Policy: Format Requirements"
                });
            }

            // Check 2: Verify NLP parsing capability
            if (!context.Attributes.ContainsKey("NlpParserEnabled") ||
                !context.Attributes.ContainsKey("ParsedPolicyRules"))
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "NLP-002",
                    Description = "NLP parsing not configured for policy interpretation",
                    Severity = ViolationSeverity.High,
                    AffectedResource = context.ResourceId,
                    Remediation = "Enable NLP parsing via message bus intelligence.nlp.parse",
                    RegulatoryReference = "NLP Policy: Parsing Requirements"
                });
            }

            // Check 3: Verify policy intent extraction
            if (!context.Attributes.ContainsKey("PolicyIntent") ||
                !context.Attributes.ContainsKey("ExtractedRules"))
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "NLP-003",
                    Description = "Policy intent and rules not extracted",
                    Severity = ViolationSeverity.High,
                    AffectedResource = context.ResourceId,
                    Remediation = "Extract actionable rules from natural language policies",
                    RegulatoryReference = "NLP Policy: Intent Extraction"
                });
            }

            // Check 4: Verify human review of parsed policies
            if (!context.Attributes.ContainsKey("HumanReviewed") ||
                !context.Attributes.ContainsKey("ReviewTimestamp"))
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "NLP-004",
                    Description = "Parsed policies not reviewed by compliance officer",
                    Severity = ViolationSeverity.High,
                    AffectedResource = context.ResourceId,
                    Remediation = "Require human review and approval of NLP-parsed policies",
                    RegulatoryReference = "NLP Policy: Human Validation"
                });
            }

            // Check 5: Verify policy version tracking
            if (!context.Attributes.ContainsKey("PolicyVersion") ||
                !context.Attributes.ContainsKey("ChangeHistory"))
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "NLP-005",
                    Description = "Policy versioning and change tracking not implemented",
                    Severity = ViolationSeverity.Medium,
                    AffectedResource = context.ResourceId,
                    Remediation = "Track policy versions and maintain change history",
                    RegulatoryReference = "NLP Policy: Version Control"
                });
            }

            var isCompliant = violations.Count == 0;
            var status = isCompliant ? ComplianceStatus.Compliant :
                        violations.Any(v => v.Severity == ViolationSeverity.Critical) ? ComplianceStatus.NonCompliant :
                        ComplianceStatus.PartiallyCompliant;

            return Task.FromResult(new ComplianceResult
            {
                IsCompliant = isCompliant,
                Framework = Framework,
                Status = status,
                Violations = violations,
                Recommendations = GenerateRecommendations(violations)
            });
        }

        private static List<string> GenerateRecommendations(List<ComplianceViolation> violations)
        {
            var recommendations = new List<string>();

            if (violations.Any(v => v.Code == "NLP-001" || v.Code == "NLP-002"))
                recommendations.Add("Deploy NLP engine for natural language policy parsing");

            if (violations.Any(v => v.Code == "NLP-003"))
                recommendations.Add("Implement intent extraction and rule generation from policies");

            if (violations.Any(v => v.Code == "NLP-004"))
                recommendations.Add("Establish human-in-the-loop review workflow for parsed policies");

            if (violations.Any(v => v.Code == "NLP-005"))
                recommendations.Add("Implement version control system for policy management");

            return recommendations;
        }
    }
}
