using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.Advanced
{
    public sealed class ZkProofAccessStrategy : AccessControlStrategyBase
    {
        private readonly ILogger _logger;

        public ZkProofAccessStrategy(ILogger? logger = null)
        {
            _logger = logger ?? NullLogger.Instance;
        }

        public override string StrategyId => "zk-proof-access";
        public override string StrategyName => "Zero-Knowledge Proof Access Strategy";

        public override AccessControlCapabilities Capabilities => new()
        {
            SupportsRealTimeDecisions = false,
            SupportsAuditTrail = true,
            SupportsPolicyConfiguration = true,
            SupportsExternalIdentity = false,
            SupportsTemporalAccess = true,
            SupportsGeographicRestrictions = false,
            MaxConcurrentEvaluations = 20
        };

        protected override async Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            var zkProof = context.SubjectAttributes.TryGetValue("zk_proof", out var proof) && proof is string proofStr ? proofStr : null;

            if (string.IsNullOrEmpty(zkProof))
            {
                return new AccessDecision
                {
                    IsGranted = false,
                    Reason = "No zero-knowledge proof provided"
                };
            }

            // Deserialize the Base64-encoded ZK proof
            ZkProofData proofData;
            try
            {
                var proofBytes = Convert.FromBase64String(zkProof);
                proofData = ZkProofData.Deserialize(proofBytes);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to deserialize ZK proof");
                return new AccessDecision
                {
                    IsGranted = false,
                    Reason = "Invalid ZK proof format"
                };
            }

            // Verify the zero-knowledge proof using Schnorr protocol
            var verificationResult = await ZkProofVerifier.VerifyAsync(proofData, cancellationToken);

            return new AccessDecision
            {
                IsGranted = verificationResult.IsValid,
                Reason = verificationResult.IsValid
                    ? "Access granted via zero-knowledge proof verification"
                    : $"ZK proof verification failed: {verificationResult.Reason}",
                ApplicablePolicies = new[] { "zk-proof-policy" },
                Metadata = new Dictionary<string, object>
                {
                    ["proof_type"] = "schnorr-ecdsa-p256",
                    ["identity_hidden"] = true,
                    ["verification_time_ms"] = verificationResult.VerificationTimeMs,
                    ["challenge_context"] = proofData.ChallengeContext
                }
            };
        }

        /// <summary>
        /// Generates a valid ZK proof for testing purposes.
        /// Returns the proof as a Base64 string and the public key bytes.
        /// </summary>
        internal static async Task<(string Base64Proof, byte[] PublicKey)> GenerateProofForTestingAsync(string challengeContext)
        {
            var (privateKey, publicKey) = ZkProofCrypto.GenerateKeyPair();
            var proof = await ZkProofGenerator.GenerateProofAsync(privateKey, challengeContext);
            var proofBytes = proof.Serialize();
            var base64Proof = Convert.ToBase64String(proofBytes);
            return (base64Proof, publicKey);
        }
    }
}
