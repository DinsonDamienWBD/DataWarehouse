using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.Innovation
{
    /// <summary>
    /// Blockchain-based audit trail compliance strategy for immutable
    /// and verifiable compliance event logging.
    /// </summary>
    public sealed class BlockchainAuditTrailStrategy : ComplianceStrategyBase
    {
        public override string StrategyId => "blockchain-audit-trail";
        public override string StrategyName => "Blockchain Audit Trail";
        public override string Framework => "Innovation-Blockchain";

        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("blockchain_audit_trail.check");
            var violations = new List<ComplianceViolation>();

            // Check 1: Verify blockchain anchor
            if (!context.Attributes.TryGetValue("BlockchainAnchor", out var anchor) ||
                string.IsNullOrEmpty(anchor?.ToString()))
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "BCAUDIT-001",
                    Description = "Compliance event not anchored to blockchain",
                    Severity = ViolationSeverity.High,
                    AffectedResource = context.ResourceId,
                    Remediation = "Anchor compliance events to blockchain for immutable verification",
                    RegulatoryReference = "Blockchain Audit: Anchoring Requirements"
                });
            }

            // Check 2: Verify transaction hash
            if (!context.Attributes.ContainsKey("TransactionHash") ||
                !context.Attributes.ContainsKey("BlockNumber"))
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "BCAUDIT-002",
                    Description = "Missing blockchain transaction details",
                    Severity = ViolationSeverity.High,
                    AffectedResource = context.ResourceId,
                    Remediation = "Record transaction hash and block number for each audit entry",
                    RegulatoryReference = "Blockchain Audit: Transaction Tracking"
                });
            }

            // Check 3: Verify merkle proof capability
            if (!context.Attributes.ContainsKey("MerkleProof") &&
                !context.Attributes.ContainsKey("MerkleRoot"))
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "BCAUDIT-003",
                    Description = "Merkle proof verification not available",
                    Severity = ViolationSeverity.Medium,
                    AffectedResource = context.ResourceId,
                    Remediation = "Generate Merkle proofs for efficient batch verification",
                    RegulatoryReference = "Blockchain Audit: Merkle Tree Requirements"
                });
            }

            // Check 4: Verify smart contract compliance
            var usesSmartContract = GetConfigValue<bool>("UseSmartContract", false);
            if (usesSmartContract)
            {
                if (!context.Attributes.ContainsKey("ContractAddress") ||
                    !context.Attributes.ContainsKey("ContractAuditReport"))
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "BCAUDIT-004",
                        Description = "Smart contract not properly documented or audited",
                        Severity = ViolationSeverity.High,
                        AffectedResource = context.ResourceId,
                        Remediation = "Use audited smart contracts with documented addresses",
                        RegulatoryReference = "Blockchain Audit: Smart Contract Security"
                    });
                }
            }

            // Check 5: Verify consensus mechanism
            if (!context.Attributes.TryGetValue("ConsensusType", out var consensus))
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "BCAUDIT-005",
                    Description = "Blockchain consensus mechanism not specified",
                    Severity = ViolationSeverity.Medium,
                    AffectedResource = context.ResourceId,
                    Remediation = "Document blockchain network and consensus mechanism (PoW, PoS, etc.)",
                    RegulatoryReference = "Blockchain Audit: Network Specification"
                });
            }

            // Check 6: Verify timestamping service
            if (!context.Attributes.ContainsKey("BlockTimestamp") ||
                !context.Attributes.ContainsKey("TimestampProof"))
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "BCAUDIT-006",
                    Description = "Trusted timestamp proof not available",
                    Severity = ViolationSeverity.High,
                    AffectedResource = context.ResourceId,
                    Remediation = "Use blockchain timestamp as trusted time source with proof",
                    RegulatoryReference = "Blockchain Audit: Timestamping"
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

        private T GetConfigValue<T>(string key, T defaultValue)
        {
            if (Configuration.TryGetValue(key, out var value) && value is T typedValue)
                return typedValue;
            return defaultValue;
        }

        private static List<string> GenerateRecommendations(List<ComplianceViolation> violations)
        {
            var recommendations = new List<string>();

            if (violations.Any(v => v.Code == "BCAUDIT-001" || v.Code == "BCAUDIT-002"))
                recommendations.Add("Integrate with blockchain network for audit trail anchoring");

            if (violations.Any(v => v.Code == "BCAUDIT-003"))
                recommendations.Add("Implement Merkle tree construction for efficient verification");

            if (violations.Any(v => v.Code == "BCAUDIT-004"))
                recommendations.Add("Deploy audited smart contracts for automated compliance");

            if (violations.Any(v => v.Code == "BCAUDIT-005" || v.Code == "BCAUDIT-006"))
                recommendations.Add("Document blockchain architecture and timestamping service");

            return recommendations;
        }
    
    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("blockchain_audit_trail.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("blockchain_audit_trail.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
