using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.Innovation
{
    /// <summary>
    /// Quantum-resistant audit trail compliance strategy using post-quantum
    /// cryptographic algorithms for long-term security.
    /// </summary>
    public sealed class QuantumProofAuditStrategy : ComplianceStrategyBase
    {
        public override string StrategyId => "quantum-proof-audit";
        public override string StrategyName => "Quantum-Proof Audit Trail";
        public override string Framework => "Innovation-QuantumProof";

        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
        IncrementCounter("quantum_proof_audit.check");
            var violations = new List<ComplianceViolation>();

            // Check 1: Verify post-quantum signature algorithm
            if (!context.Attributes.TryGetValue("SignatureAlgorithm", out var sigAlg) ||
                !IsPostQuantumSignature(sigAlg?.ToString()))
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "QPROOF-001",
                    Description = "Not using post-quantum signature algorithm",
                    Severity = ViolationSeverity.High,
                    AffectedResource = context.ResourceId,
                    Remediation = "Use NIST-approved post-quantum signatures: CRYSTALS-Dilithium, Falcon, or SPHINCS+",
                    RegulatoryReference = "NIST PQC: Post-Quantum Signature Standards"
                });
            }

            // Check 2: Verify post-quantum key encapsulation
            if (!context.Attributes.TryGetValue("KeyEncapsulation", out var kem) ||
                !IsPostQuantumKEM(kem?.ToString()))
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "QPROOF-002",
                    Description = "Not using post-quantum key encapsulation mechanism",
                    Severity = ViolationSeverity.High,
                    AffectedResource = context.ResourceId,
                    Remediation = "Use CRYSTALS-Kyber or other NIST-approved PQC KEM",
                    RegulatoryReference = "NIST PQC: Key Encapsulation Standards"
                });
            }

            // Check 3: Verify hash function quantum resistance
            if (!context.Attributes.TryGetValue("HashAlgorithm", out var hashAlg) ||
                !IsQuantumResistantHash(hashAlg?.ToString()))
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "QPROOF-003",
                    Description = "Hash algorithm may be vulnerable to quantum attacks",
                    Severity = ViolationSeverity.Medium,
                    AffectedResource = context.ResourceId,
                    Remediation = "Use SHA-3 or increase hash output size (SHA-512) for quantum resistance",
                    RegulatoryReference = "NIST: Quantum-Resistant Hash Functions"
                });
            }

            // Check 4: Verify hybrid cryptography support
            var hybridMode = GetConfigValue<bool>("HybridCryptography", false);
            if (!hybridMode && !context.Attributes.ContainsKey("LegacyAlgorithmSupport"))
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "QPROOF-004",
                    Description = "No hybrid cryptography for transition period",
                    Severity = ViolationSeverity.Medium,
                    AffectedResource = context.ResourceId,
                    Remediation = "Implement hybrid mode combining classical and post-quantum algorithms",
                    RegulatoryReference = "NIST PQC: Migration Guidelines"
                });
            }

            // Check 5: Verify long-term key rotation policy
            if (!context.Attributes.ContainsKey("KeyRotationPolicy") ||
                !context.Attributes.ContainsKey("KeyExpirationDate"))
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "QPROOF-005",
                    Description = "Missing key rotation policy for quantum-resistant keys",
                    Severity = ViolationSeverity.Medium,
                    AffectedResource = context.ResourceId,
                    Remediation = "Define key rotation schedule considering quantum computing advances",
                    RegulatoryReference = "PQC: Key Management Best Practices"
                });
            }

            // Check 6: Verify cryptographic agility
            if (!context.Attributes.ContainsKey("CryptoAgility") ||
                !context.Attributes.ContainsKey("AlgorithmUpgradePath"))
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "QPROOF-006",
                    Description = "System lacks cryptographic agility for future algorithm updates",
                    Severity = ViolationSeverity.High,
                    AffectedResource = context.ResourceId,
                    Remediation = "Design systems for easy cryptographic algorithm replacement",
                    RegulatoryReference = "PQC: Cryptographic Agility Requirements"
                });
            }

            var isCompliant = violations.Count == 0;
            var status = isCompliant ? ComplianceStatus.Compliant :
                        violations.Any(v => v.Severity >= ViolationSeverity.High) ? ComplianceStatus.PartiallyCompliant :
                        ComplianceStatus.RequiresReview;

            return Task.FromResult(new ComplianceResult
            {
                IsCompliant = isCompliant,
                Framework = Framework,
                Status = status,
                Violations = violations,
                Recommendations = GenerateRecommendations(violations)
            });
        }

        private static bool IsPostQuantumSignature(string? algorithm)
        {
            if (string.IsNullOrEmpty(algorithm))
                return false;

            var pqSignatures = new[] { "Dilithium", "CRYSTALS-Dilithium", "Falcon", "SPHINCS+", "SPHINCS" };
            return pqSignatures.Any(a => algorithm.Contains(a, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsPostQuantumKEM(string? algorithm)
        {
            if (string.IsNullOrEmpty(algorithm))
                return false;

            var pqKEMs = new[] { "Kyber", "CRYSTALS-Kyber", "NTRU", "SABER" };
            return pqKEMs.Any(a => algorithm.Contains(a, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsQuantumResistantHash(string? algorithm)
        {
            if (string.IsNullOrEmpty(algorithm))
                return false;

            return algorithm.Contains("SHA-3", StringComparison.OrdinalIgnoreCase) ||
                   algorithm.Contains("SHA3", StringComparison.OrdinalIgnoreCase) ||
                   algorithm.Contains("SHA512", StringComparison.OrdinalIgnoreCase) ||
                   algorithm.Contains("SHA-512", StringComparison.OrdinalIgnoreCase);
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

            if (violations.Any(v => v.Code == "QPROOF-001" || v.Code == "QPROOF-002"))
                recommendations.Add("Migrate to NIST-approved post-quantum cryptographic algorithms");

            if (violations.Any(v => v.Code == "QPROOF-003"))
                recommendations.Add("Upgrade hash functions to quantum-resistant variants");

            if (violations.Any(v => v.Code == "QPROOF-004"))
                recommendations.Add("Implement hybrid cryptography during transition period");

            if (violations.Any(v => v.Code == "QPROOF-005"))
                recommendations.Add("Establish key rotation policy with quantum threat considerations");

            if (violations.Any(v => v.Code == "QPROOF-006"))
                recommendations.Add("Design for cryptographic agility to adapt to evolving standards");

            return recommendations;
        }
    
    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        IncrementCounter("quantum_proof_audit.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
        IncrementCounter("quantum_proof_audit.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
