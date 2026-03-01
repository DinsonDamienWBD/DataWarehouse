using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.WORM
{
    /// <summary>
    /// SEC 17a-4 WORM compliance strategy for broker-dealer record retention.
    /// Enforces non-rewritable and non-erasable electronic storage requirements.
    /// </summary>
    public sealed class Sec17a4WormStrategy : ComplianceStrategyBase
    {
        public override string StrategyId => "sec-17a-4-worm";
        public override string StrategyName => "SEC 17a-4 WORM Compliance";
        public override string Framework => "SEC-17a-4";

        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("sec17a4_worm.check");
            var violations = new List<ComplianceViolation>();

            // Check 1: Verify non-rewritable storage
            if (context.OperationType.Contains("write", StringComparison.OrdinalIgnoreCase) ||
                context.OperationType.Contains("create", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.TryGetValue("NonRewritable", out var nonRewritable) ||
                    !(nonRewritable is bool isNonRewritable && isNonRewritable))
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "SEC17A4-001",
                        Description = "Storage does not meet SEC 17a-4 non-rewritable requirement",
                        Severity = ViolationSeverity.Critical,
                        AffectedResource = context.ResourceId,
                        Remediation = "Implement write-once storage that prevents any modification after initial write",
                        RegulatoryReference = "17 CFR 240.17a-4(f)(2)(ii)(A)"
                    });
                }
            }

            // Check 2: Verify non-erasable storage
            if (!context.Attributes.TryGetValue("NonErasable", out var nonErasable) ||
                !(nonErasable is bool isNonErasable && isNonErasable))
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "SEC17A4-002",
                    Description = "Storage does not meet SEC 17a-4 non-erasable requirement",
                    Severity = ViolationSeverity.Critical,
                    AffectedResource = context.ResourceId,
                    Remediation = "Implement storage that prevents erasure during retention period",
                    RegulatoryReference = "17 CFR 240.17a-4(f)(2)(ii)(A)"
                });
            }

            // Check 3: Verify retention period (6 years for broker-dealer records)
            // LOW-1566: Accept int, long, and double from JSON deserializer to avoid silent check skip.
            if (!context.Attributes.TryGetValue("RetentionPeriodYears", out var retention) ||
                !(retention is int or long or double) ||
                Convert.ToDouble(retention) < 6)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "SEC17A4-003",
                    Description = "Retention period does not meet SEC 17a-4 minimum of 6 years",
                    Severity = ViolationSeverity.Critical,
                    AffectedResource = context.ResourceId,
                    Remediation = "Set retention period to minimum 6 years for broker-dealer records",
                    RegulatoryReference = "17 CFR 240.17a-4(b)"
                });
            }

            // Check 4: Verify immediate accessibility (2 years)
            if (context.Attributes.TryGetValue("RecordAge", out var age) &&
                age is int ageInYears && ageInYears <= 2)
            {
                if (!context.Attributes.TryGetValue("ImmediatelyAccessible", out var accessible) ||
                    !(accessible is bool isAccessible && isAccessible))
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "SEC17A4-004",
                        Description = "Records under 2 years old must be immediately accessible",
                        Severity = ViolationSeverity.High,
                        AffectedResource = context.ResourceId,
                        Remediation = "Ensure records are stored in immediately accessible format for first 2 years",
                        RegulatoryReference = "17 CFR 240.17a-4(f)(3)(iii)"
                    });
                }
            }

            // Check 5: Verify audit trail
            if (!context.Attributes.ContainsKey("AuditTrail") ||
                !context.Attributes.ContainsKey("SerializedTimestamp"))
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "SEC17A4-005",
                    Description = "Missing required audit trail and timestamp serialization",
                    Severity = ViolationSeverity.High,
                    AffectedResource = context.ResourceId,
                    Remediation = "Implement complete audit trail with serialized timestamps for all record access",
                    RegulatoryReference = "17 CFR 240.17a-4(f)(3)(v)"
                });
            }

            // Check 6: Verify third-party downloader capability
            if (!context.Attributes.TryGetValue("DownloadCapability", out var downloadable) ||
                !(downloadable is bool canDownload && canDownload))
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "SEC17A4-006",
                    Description = "Storage system lacks required third-party download capability",
                    Severity = ViolationSeverity.High,
                    AffectedResource = context.ResourceId,
                    Remediation = "Provide ability for SEC and third parties to download records",
                    RegulatoryReference = "17 CFR 240.17a-4(f)(3)(iv)"
                });
            }

            // Check 7: Verify duplicate copy requirement
            var requireDuplicate = GetConfigValue<bool>("RequireDuplicateCopy", true);
            if (requireDuplicate &&
                (!context.Attributes.TryGetValue("HasDuplicateCopy", out var hasDuplicate) ||
                 !(hasDuplicate is bool duplicate && duplicate)))
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "SEC17A4-007",
                    Description = "Missing required duplicate copy at separate location",
                    Severity = ViolationSeverity.High,
                    AffectedResource = context.ResourceId,
                    Remediation = "Maintain duplicate copy of records at geographically separate location",
                    RegulatoryReference = "17 CFR 240.17a-4(f)(3)(vi)"
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

        // LOW-1459: GetConfigValue<T> is now on ComplianceStrategyBase — removed duplicate.

        private static List<string> GenerateRecommendations(List<ComplianceViolation> violations)
        {
            // LOW-1567: Build HashSet<string> of violation codes once for O(1) lookups instead of O(N) .Any() per check.
            var codes = new HashSet<string>(violations.Select(v => v.Code), StringComparer.Ordinal);
            var recommendations = new List<string>();

            if (codes.Contains("SEC17A4-001") || codes.Contains("SEC17A4-002"))
                recommendations.Add("Deploy SEC 17a-4 compliant WORM storage solution (hardware or software)");

            if (codes.Contains("SEC17A4-003"))
                recommendations.Add("Configure 6-year retention policy for all broker-dealer records");

            if (codes.Contains("SEC17A4-004"))
                recommendations.Add("Implement tiered storage with immediate accessibility for recent records");

            if (codes.Contains("SEC17A4-005"))
                recommendations.Add("Deploy comprehensive audit and timestamp serialization system");

            if (codes.Contains("SEC17A4-006"))
                recommendations.Add("Implement third-party download interface for regulatory access");

            if (codes.Contains("SEC17A4-007"))
                recommendations.Add("Establish duplicate backup at geographically separate facility");

            return recommendations;
        }
    
    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("sec17a4_worm.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("sec17a4_worm.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
