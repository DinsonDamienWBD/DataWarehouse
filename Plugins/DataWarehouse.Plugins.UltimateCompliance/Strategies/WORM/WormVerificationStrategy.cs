using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.WORM
{
    /// <summary>
    /// WORM integrity verification strategy.
    /// Validates hash verification, tamper detection, and chain of custody for WORM data.
    /// </summary>
    public sealed class WormVerificationStrategy : ComplianceStrategyBase
    {
        public override string StrategyId => "worm-integrity-verification";
        public override string StrategyName => "WORM Integrity Verification";
        public override string Framework => "WORM-Verification";

        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            var violations = new List<ComplianceViolation>();

            // Check 1: Verify cryptographic hash presence
            if (!context.Attributes.TryGetValue("IntegrityHash", out var hash) || string.IsNullOrEmpty(hash?.ToString()))
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "WORM-VER-001",
                    Description = "WORM data missing cryptographic integrity hash",
                    Severity = ViolationSeverity.Critical,
                    AffectedResource = context.ResourceId,
                    Remediation = "Generate and store SHA-256 or stronger hash for data integrity verification",
                    RegulatoryReference = "WORM Verification: Cryptographic Hash Requirement"
                });
            }
            else
            {
                // Check 2: Verify hash algorithm strength
                if (!context.Attributes.TryGetValue("HashAlgorithm", out var algorithm) ||
                    !IsStrongHashAlgorithm(algorithm?.ToString()))
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "WORM-VER-002",
                        Description = "WORM data using weak or unspecified hash algorithm",
                        Severity = ViolationSeverity.High,
                        AffectedResource = context.ResourceId,
                        Remediation = "Use SHA-256, SHA-384, SHA-512, or SHA-3 for integrity hashing",
                        RegulatoryReference = "WORM Verification: Cryptographic Standards"
                    });
                }
            }

            // Check 3: Verify hash verification timestamp
            if (context.OperationType.Contains("verify", StringComparison.OrdinalIgnoreCase) ||
                context.OperationType.Contains("read", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.Attributes.ContainsKey("LastVerificationTime"))
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "WORM-VER-003",
                        Description = "WORM data lacks verification timestamp tracking",
                        Severity = ViolationSeverity.Medium,
                        AffectedResource = context.ResourceId,
                        Remediation = "Track and record each integrity verification attempt",
                        RegulatoryReference = "WORM Verification: Audit Trail"
                    });
                }
            }

            // Check 4: Verify chain of custody
            if (!context.Attributes.ContainsKey("CustodyChain") || !context.Attributes.ContainsKey("CreationUserId"))
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "WORM-VER-004",
                    Description = "WORM data missing chain of custody information",
                    Severity = ViolationSeverity.High,
                    AffectedResource = context.ResourceId,
                    Remediation = "Maintain complete chain of custody with user tracking and timestamps",
                    RegulatoryReference = "WORM Verification: Chain of Custody Requirements"
                });
            }

            // Check 5: Verify tamper detection mechanism
            if (!context.Attributes.ContainsKey("TamperProofLog") &&
                !context.Attributes.ContainsKey("BlockchainAnchor"))
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "WORM-VER-005",
                    Description = "WORM data lacks tamper detection mechanism",
                    Severity = ViolationSeverity.High,
                    AffectedResource = context.ResourceId,
                    Remediation = "Implement tamper-evident logging or blockchain anchoring",
                    RegulatoryReference = "WORM Verification: Tamper Detection"
                });
            }

            // Check 6: Verify digital signature
            var requiresSignature = GetConfigValue<bool>("RequireDigitalSignature", true);
            if (requiresSignature)
            {
                if (!context.Attributes.ContainsKey("DigitalSignature") ||
                    !context.Attributes.ContainsKey("SignerCertificate"))
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "WORM-VER-006",
                        Description = "WORM data missing required digital signature",
                        Severity = ViolationSeverity.High,
                        AffectedResource = context.ResourceId,
                        Remediation = "Apply digital signature from trusted certificate authority",
                        RegulatoryReference = "WORM Verification: Digital Signature Requirements"
                    });
                }
            }

            // Check 7: Verify integrity check frequency
            if (context.Attributes.TryGetValue("LastVerificationTime", out var lastVerification) &&
                lastVerification is DateTime lastVerified)
            {
                var maxDaysSinceVerification = GetConfigValue<int>("MaxDaysSinceVerification", 90);
                var daysSinceVerification = (DateTime.UtcNow - lastVerified).Days;

                if (daysSinceVerification > maxDaysSinceVerification)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "WORM-VER-007",
                        Description = $"WORM data not verified in {daysSinceVerification} days (max: {maxDaysSinceVerification})",
                        Severity = ViolationSeverity.Medium,
                        AffectedResource = context.ResourceId,
                        Remediation = "Perform periodic integrity verification as per policy",
                        RegulatoryReference = "WORM Verification: Periodic Verification Policy"
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

        private static bool IsStrongHashAlgorithm(string? algorithm)
        {
            if (string.IsNullOrEmpty(algorithm))
                return false;

            var strongAlgorithms = new[] { "SHA256", "SHA384", "SHA512", "SHA3-256", "SHA3-384", "SHA3-512", "SHA-256", "SHA-384", "SHA-512" };
            return strongAlgorithms.Any(a => algorithm.Contains(a, StringComparison.OrdinalIgnoreCase));
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

            if (violations.Any(v => v.Code == "WORM-VER-001" || v.Code == "WORM-VER-002"))
                recommendations.Add("Implement strong cryptographic hashing (SHA-256 or stronger) for all WORM data");

            if (violations.Any(v => v.Code == "WORM-VER-003" || v.Code == "WORM-VER-007"))
                recommendations.Add("Establish periodic integrity verification schedule");

            if (violations.Any(v => v.Code == "WORM-VER-004"))
                recommendations.Add("Implement comprehensive chain of custody tracking system");

            if (violations.Any(v => v.Code == "WORM-VER-005"))
                recommendations.Add("Deploy tamper-evident logging infrastructure (hash chains or blockchain)");

            if (violations.Any(v => v.Code == "WORM-VER-006"))
                recommendations.Add("Integrate digital signature capability with PKI infrastructure");

            return recommendations;
        }
    }
}
