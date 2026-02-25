using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.WORM
{
    /// <summary>
    /// WORM retention policy compliance strategy.
    /// Enforces minimum/maximum retention periods, legal hold overrides, and expiration handling.
    /// </summary>
    public sealed class WormRetentionStrategy : ComplianceStrategyBase
    {
        public override string StrategyId => "worm-retention-policy";
        public override string StrategyName => "WORM Retention Policy";
        public override string Framework => "WORM-Retention";

        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("worm_retention.check");
            var violations = new List<ComplianceViolation>();

            // Check 1: Verify retention period is defined
            if (!context.Attributes.TryGetValue("RetentionPeriodDays", out var retentionPeriod))
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "WORM-RET-001",
                    Description = "WORM data missing mandatory retention period",
                    Severity = ViolationSeverity.Critical,
                    AffectedResource = context.ResourceId,
                    Remediation = "Define retention period based on regulatory requirements",
                    RegulatoryReference = "WORM Retention: Mandatory Period Definition"
                });
            }
            else if (retentionPeriod is int days)
            {
                // Check 2: Verify minimum retention period
                var minRetention = GetConfigValue<int>("MinRetentionDays", 365);
                if (days < minRetention)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "WORM-RET-002",
                        Description = $"Retention period {days} days is below minimum {minRetention} days",
                        Severity = ViolationSeverity.High,
                        AffectedResource = context.ResourceId,
                        Remediation = $"Increase retention period to at least {minRetention} days",
                        RegulatoryReference = "WORM Retention: Minimum Period Requirements"
                    });
                }

                // Check 3: Verify maximum retention period
                var maxRetention = GetConfigValue<int>("MaxRetentionDays", 7300); // 20 years
                if (days > maxRetention)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "WORM-RET-003",
                        Description = $"Retention period {days} days exceeds maximum {maxRetention} days",
                        Severity = ViolationSeverity.Medium,
                        AffectedResource = context.ResourceId,
                        Remediation = $"Reduce retention period to maximum {maxRetention} days or justify extended retention",
                        RegulatoryReference = "WORM Retention: Maximum Period Limits"
                    });
                }
            }

            // Check 4: Verify legal hold handling
            if (context.Attributes.TryGetValue("LegalHold", out var legalHold) &&
                legalHold is bool isOnHold && isOnHold)
            {
                if (!context.Attributes.ContainsKey("LegalHoldId") ||
                    !context.Attributes.ContainsKey("LegalHoldDate"))
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "WORM-RET-004",
                        Description = "Legal hold missing required tracking metadata",
                        Severity = ViolationSeverity.High,
                        AffectedResource = context.ResourceId,
                        Remediation = "Add LegalHoldId and LegalHoldDate for legal hold tracking",
                        RegulatoryReference = "WORM Legal Hold: Metadata Requirements"
                    });
                }

                if (context.OperationType.Contains("delete", StringComparison.OrdinalIgnoreCase))
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "WORM-RET-005",
                        Description = "Attempted deletion of data under legal hold",
                        Severity = ViolationSeverity.Critical,
                        AffectedResource = context.ResourceId,
                        Remediation = "Remove legal hold before allowing deletion",
                        RegulatoryReference = "WORM Legal Hold: Deletion Protection"
                    });
                }
            }

            // Check 5: Verify expiration handling policy
            if (context.Attributes.TryGetValue("RetentionExpiry", out var expiry) &&
                expiry is DateTime expiryDate && expiryDate <= DateTime.UtcNow)
            {
                if (!context.Attributes.ContainsKey("ExpirationAction"))
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "WORM-RET-006",
                        Description = "Expired WORM data missing expiration action policy",
                        Severity = ViolationSeverity.Medium,
                        AffectedResource = context.ResourceId,
                        Remediation = "Define expiration action: Archive, Delete, or Extend",
                        RegulatoryReference = "WORM Retention: Expiration Management"
                    });
                }
            }

            // Check 6: Verify retention extension authorization
            if (context.OperationType.Contains("extend", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.ContainsKey("ExtensionAuthorization") ||
                    !context.Attributes.ContainsKey("ExtensionReason"))
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "WORM-RET-007",
                        Description = "Retention extension lacks proper authorization",
                        Severity = ViolationSeverity.High,
                        AffectedResource = context.ResourceId,
                        Remediation = "Provide authorized approval and documented reason for retention extension",
                        RegulatoryReference = "WORM Retention: Extension Authorization"
                    });
                }
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

        private T GetConfigValue<T>(string key, T defaultValue)
        {
            if (Configuration.TryGetValue(key, out var value) && value is T typedValue)
                return typedValue;
            return defaultValue;
        }

        private static List<string> GenerateRecommendations(List<ComplianceViolation> violations)
        {
            var recommendations = new List<string>();

            if (violations.Any(v => v.Code.StartsWith("WORM-RET-00") && (v.Code == "WORM-RET-001" || v.Code == "WORM-RET-002")))
                recommendations.Add("Establish retention policy aligned with regulatory requirements");

            if (violations.Any(v => v.Code == "WORM-RET-004" || v.Code == "WORM-RET-005"))
                recommendations.Add("Implement legal hold management system with proper tracking");

            if (violations.Any(v => v.Code == "WORM-RET-006"))
                recommendations.Add("Define expiration workflow for automated retention management");

            if (violations.Any(v => v.Code == "WORM-RET-007"))
                recommendations.Add("Implement authorization workflow for retention modifications");

            return recommendations;
        }
    
    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("worm_retention.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("worm_retention.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
