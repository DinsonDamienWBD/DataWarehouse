using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.Innovation
{
    /// <summary>
    /// Smart contract and blockchain-based compliance strategy.
    /// </summary>
    public sealed class SmartContractComplianceStrategy : ComplianceStrategyBase
    {
        public override string StrategyId => "smart-contract";
        public override string StrategyName => "Smart Contract Compliance";
        public override string Framework => "SMART-CONTRACT";

        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("smart_contract_compliance.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            bool usesBlockchain = context.Attributes.TryGetValue("UsesBlockchain", out var bcObj) && bcObj is true;

            if (!usesBlockchain)
            {
                return Task.FromResult(new ComplianceResult
                {
                    IsCompliant = true,
                    Framework = Framework,
                    Status = ComplianceStatus.NotApplicable,
                    Violations = violations,
                    Recommendations = new List<string> { "Blockchain/smart contract not in use" }
                });
            }

            if (!context.Attributes.TryGetValue("SmartContractAudited", out var auditObj) || auditObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "SC-001",
                    Description = "Smart contract not audited for compliance logic",
                    Severity = ViolationSeverity.Critical,
                    Remediation = "Audit smart contracts to ensure compliance rules are correctly encoded",
                    RegulatoryReference = "Smart Contract Security Standards"
                });
            }

            if (!context.Attributes.TryGetValue("ImmutableAuditLog", out var logObj) || logObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "SC-002",
                    Description = "Immutable audit log not maintained on blockchain",
                    Severity = ViolationSeverity.Medium,
                    Remediation = "Store compliance events on blockchain for tamper-proof audit trail",
                    RegulatoryReference = "Blockchain Audit Standards"
                });
            }

            if (!context.Attributes.TryGetValue("RightToErasure", out var erasureObj) || erasureObj is not true)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "SC-003",
                    Description = "Right to erasure mechanism not implemented for blockchain data",
                    Severity = ViolationSeverity.High,
                    Remediation = "Implement off-chain storage or cryptographic erasure for GDPR right to erasure",
                    RegulatoryReference = "GDPR Art 17, Blockchain Privacy"
                });
            }

            recommendations.Add("Consider private/permissioned blockchains for regulated data");

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
            IncrementCounter("smart_contract_compliance.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("smart_contract_compliance.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
