using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.WORM
{
    /// <summary>
    /// Software WORM (Write Once Read Many) compliance strategy for immutable storage.
    /// Validates write-once guarantees, retention enforcement, and immutability proofs.
    /// </summary>
    public sealed class WormStorageStrategy : ComplianceStrategyBase
    {
        public override string StrategyId => "worm-storage-compliance";
        public override string StrategyName => "WORM Storage Compliance";
        public override string Framework => "WORM";

        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("worm_storage.check");
            var violations = new List<ComplianceViolation>();

            // Check 1: Verify write-once constraint
            if (context.OperationType.Contains("update", StringComparison.OrdinalIgnoreCase) ||
                context.OperationType.Contains("modify", StringComparison.OrdinalIgnoreCase))
            {
                if (context.Attributes.TryGetValue("IsWormProtected", out var wormProtected) &&
                    wormProtected is bool isProtected && isProtected)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "WORM-001",
                        Description = "Attempted modification of WORM-protected data",
                        Severity = ViolationSeverity.Critical,
                        AffectedResource = context.ResourceId,
                        Remediation = "WORM data cannot be modified after initial write. Create new version instead.",
                        RegulatoryReference = "Software WORM Specification: Write-Once Requirement"
                    });
                }
            }

            // Check 2: Verify immutability marker
            if (context.OperationType.Contains("write", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.ContainsKey("ImmutabilityMarker") ||
                    !context.Attributes.ContainsKey("WriteTimestamp"))
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "WORM-002",
                        Description = "WORM data missing required immutability metadata",
                        Severity = ViolationSeverity.High,
                        AffectedResource = context.ResourceId,
                        Remediation = "Add ImmutabilityMarker and WriteTimestamp to WORM data",
                        RegulatoryReference = "Software WORM: Immutability Proof Requirements"
                    });
                }
            }

            // Check 3: Verify deletion protection during retention period
            if (context.OperationType.Contains("delete", StringComparison.OrdinalIgnoreCase))
            {
                if (context.Attributes.TryGetValue("RetentionExpiry", out var expiry) &&
                    expiry is DateTime expiryDate && expiryDate > DateTime.UtcNow)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "WORM-003",
                        Description = "Attempted deletion of data within retention period",
                        Severity = ViolationSeverity.Critical,
                        AffectedResource = context.ResourceId,
                        Remediation = $"Data cannot be deleted until retention expiry: {expiryDate:yyyy-MM-dd}",
                        RegulatoryReference = "WORM Retention Policy"
                    });
                }
            }

            // Check 4: Verify storage integrity hash
            if (context.Attributes.ContainsKey("IsWormStorage"))
            {
                if (!context.Attributes.ContainsKey("IntegrityHash") ||
                    !context.Attributes.ContainsKey("HashAlgorithm"))
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "WORM-004",
                        Description = "WORM storage missing integrity verification metadata",
                        Severity = ViolationSeverity.High,
                        AffectedResource = context.ResourceId,
                        Remediation = "Add cryptographic hash for integrity verification",
                        RegulatoryReference = "WORM: Data Integrity Requirements"
                    });
                }
            }

            // Check 5: Verify access audit trail
            if (context.OperationType.Contains("read", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.ContainsKey("AuditEnabled") ||
                    (context.Attributes["AuditEnabled"] is bool auditEnabled && !auditEnabled))
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "WORM-005",
                        Description = "WORM data access not being audited",
                        Severity = ViolationSeverity.Medium,
                        AffectedResource = context.ResourceId,
                        Remediation = "Enable audit logging for all WORM data access",
                        RegulatoryReference = "WORM: Audit Trail Requirements"
                    });
                }
            }

            // Check 6: Verify tamper-evident logging
            if (context.Attributes.TryGetValue("IsWormStorage", out var isWorm) &&
                isWorm is bool wormStorage && wormStorage)
            {
                if (!context.Attributes.ContainsKey("TamperProofLog"))
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "WORM-006",
                        Description = "WORM storage lacks tamper-evident logging",
                        Severity = ViolationSeverity.High,
                        AffectedResource = context.ResourceId,
                        Remediation = "Implement hash-chain or blockchain-based tamper-evident logging",
                        RegulatoryReference = "WORM: Tamper Detection Standards"
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

        private static List<string> GenerateRecommendations(List<ComplianceViolation> violations)
        {
            var recommendations = new List<string>();

            if (violations.Any(v => v.Code == "WORM-001"))
                recommendations.Add("Implement write-once protection at storage layer");

            if (violations.Any(v => v.Code == "WORM-002" || v.Code == "WORM-004"))
                recommendations.Add("Add cryptographic integrity verification to WORM data");

            if (violations.Any(v => v.Code == "WORM-003"))
                recommendations.Add("Implement retention policy enforcement at storage level");

            if (violations.Any(v => v.Code == "WORM-005"))
                recommendations.Add("Enable comprehensive audit logging for WORM access");

            if (violations.Any(v => v.Code == "WORM-006"))
                recommendations.Add("Deploy tamper-evident logging infrastructure");

            return recommendations;
        }
    
    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("worm_storage.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("worm_storage.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
