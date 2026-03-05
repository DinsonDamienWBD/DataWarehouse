using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.WORM
{
    /// <summary>
    /// FINRA WORM compliance strategy for electronic storage media.
    /// Enforces index access, audit trail, and record retention requirements.
    /// </summary>
    public sealed class FinraWormStrategy : ComplianceStrategyBase
    {
        public override string StrategyId => "finra-worm-compliance";
        public override string StrategyName => "FINRA WORM Compliance";
        public override string Framework => "FINRA-4511";

        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("finra_worm.check");
            var violations = new List<ComplianceViolation>();

            // Check 1: Verify WORM media compliance
            if (!context.Attributes.TryGetValue("IsWormMedia", out var isWorm) ||
                !(isWorm is bool wormMedia && wormMedia))
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "FINRA-001",
                    Description = "Records not stored on FINRA-compliant WORM media",
                    Severity = ViolationSeverity.Critical,
                    AffectedResource = context.ResourceId,
                    Remediation = "Store records on write-once, read-many electronic storage media",
                    RegulatoryReference = "FINRA Rule 4511(c)"
                });
            }

            // Check 2: Verify retention period (3-6 years depending on record type)
            if (!context.Attributes.TryGetValue("RetentionPeriodYears", out var retention))
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "FINRA-002",
                    Description = "Retention period not defined for FINRA records",
                    Severity = ViolationSeverity.Critical,
                    AffectedResource = context.ResourceId,
                    Remediation = "Set retention period based on record type: 3 years minimum, 6 years for specific records",
                    RegulatoryReference = "FINRA Rule 4511(a)"
                });
            }
            // LOW-1566: Accept int, long, and double from JSON deserializer so the check is not silently skipped.
            else if (retention is int or long or double)
            {
                var years = Convert.ToDouble(retention);
                if (years < 3)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "FINRA-003",
                        Description = $"Retention period {years} years is below FINRA minimum of 3 years",
                        Severity = ViolationSeverity.Critical,
                        AffectedResource = context.ResourceId,
                        Remediation = "Increase retention period to meet FINRA requirements (3-6 years)",
                        RegulatoryReference = "FINRA Rule 4511(a)"
                    });
                }
            }

            // Check 3: Verify index and retrieval capability
            if (!context.Attributes.ContainsKey("IndexedRecord") ||
                !context.Attributes.ContainsKey("SearchableMetadata"))
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "FINRA-004",
                    Description = "Records lack required indexing and search capability",
                    Severity = ViolationSeverity.High,
                    AffectedResource = context.ResourceId,
                    Remediation = "Implement indexing system for prompt record retrieval by date, subject, and category",
                    RegulatoryReference = "FINRA Rule 4511(c)"
                });
            }

            // Check 4: Verify duplicate storage requirement
            if (!context.Attributes.TryGetValue("HasOffSiteCopy", out var offSiteCopy) ||
                !(offSiteCopy is bool hasOffSite && hasOffSite))
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "FINRA-005",
                    Description = "Missing required duplicate copy at separate location",
                    Severity = ViolationSeverity.High,
                    AffectedResource = context.ResourceId,
                    Remediation = "Maintain duplicate copy at separate geographical location for business continuity",
                    RegulatoryReference = "FINRA Rule 4511(c)"
                });
            }

            // Check 5: Verify prompt access capability
            if (context.OperationType.Contains("retrieve", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("PromptAccessible", out var promptAccess) ||
                    !(promptAccess is bool isPrompt && isPrompt))
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "FINRA-006",
                        Description = "Records not accessible for prompt production to FINRA",
                        Severity = ViolationSeverity.High,
                        AffectedResource = context.ResourceId,
                        Remediation = "Ensure records can be promptly produced upon FINRA request",
                        RegulatoryReference = "FINRA Rule 4511(c)"
                    });
                }
            }

            // Check 6: Verify audit trail for electronic records
            if (!context.Attributes.ContainsKey("ElectronicAuditTrail") ||
                !context.Attributes.ContainsKey("AccessLog"))
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "FINRA-007",
                    Description = "Missing audit trail for electronic record access",
                    Severity = ViolationSeverity.Medium,
                    AffectedResource = context.ResourceId,
                    Remediation = "Implement comprehensive audit logging for all record access and modifications",
                    RegulatoryReference = "FINRA Rule 4511(c) - Best Practices"
                });
            }

            // Check 7: Verify business continuity plan
            if (!context.Attributes.TryGetValue("BusinessContinuityPlan", out var bcpExists) ||
                !(bcpExists is bool hasBcp && hasBcp))
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "FINRA-008",
                    Description = "No business continuity plan documented for record preservation",
                    Severity = ViolationSeverity.Medium,
                    AffectedResource = context.ResourceId,
                    Remediation = "Document and test business continuity plan for record preservation and access",
                    RegulatoryReference = "FINRA Rule 4370 - Business Continuity"
                });
            }

            // P2-1562: Medium violations are advisory; only High+ violations fail compliance.
            var isCompliant = !violations.Any(v =>
                v.Severity == ViolationSeverity.Critical || v.Severity == ViolationSeverity.High);
            var status = violations.Count == 0 ? ComplianceStatus.Compliant :
                        violations.Any(v => v.Severity == ViolationSeverity.Critical) ? ComplianceStatus.NonCompliant :
                        !isCompliant ? ComplianceStatus.NonCompliant :
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
            // LOW-1567: Build HashSet<string> of violation codes once for O(1) lookups instead of O(N) .Any() per check.
            var codes = new HashSet<string>(violations.Select(v => v.Code), StringComparer.Ordinal);
            var recommendations = new List<string>();

            if (codes.Contains("FINRA-001"))
                recommendations.Add("Deploy FINRA-compliant WORM storage solution");

            if (codes.Contains("FINRA-002") || codes.Contains("FINRA-003"))
                recommendations.Add("Implement retention policy: 3 years for most records, 6 years for account records");

            if (codes.Contains("FINRA-004"))
                recommendations.Add("Build searchable index system with metadata tagging");

            if (codes.Contains("FINRA-005"))
                recommendations.Add("Establish off-site backup at separate geographical location");

            if (codes.Contains("FINRA-006"))
                recommendations.Add("Optimize retrieval system for prompt regulatory production");

            if (codes.Contains("FINRA-007"))
                recommendations.Add("Implement comprehensive audit trail for all electronic record operations");

            if (codes.Contains("FINRA-008"))
                recommendations.Add("Document and test business continuity plan for record systems");

            return recommendations;
        }
    
    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("finra_worm.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("finra_worm.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
