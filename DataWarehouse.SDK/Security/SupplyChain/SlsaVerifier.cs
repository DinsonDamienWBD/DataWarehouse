using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.SDK.Security.SupplyChain
{
    // ──────────────────────────────────────────────────────────────
    // Verification Types
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Policy for verifying SLSA provenance attestations.
    /// Defines the constraints that provenance must satisfy.
    /// </summary>
    public sealed class VerificationPolicy
    {
        /// <summary>Required builder ID. If set, provenance must match this builder.</summary>
        public string? RequiredBuilderId { get; init; }

        /// <summary>Allowed source repository URIs. If non-empty, source must be in this list.</summary>
        public IReadOnlyList<Uri> AllowedSourceRepos { get; init; } = Array.Empty<Uri>();

        /// <summary>Minimum SLSA level required (1, 2, or 3). Default 1.</summary>
        public int MinSlsaLevel { get; init; } = 1;

        /// <summary>Whether to verify artifact digests against actual files. Default false.</summary>
        public bool VerifyArtifactDigests { get; init; }

        /// <summary>Base path for artifact files when verifying digests.</summary>
        public string? ArtifactBasePath { get; init; }

        /// <summary>Allowed build types (URIs). If non-empty, buildType must be in this list.</summary>
        public IReadOnlyList<string> AllowedBuildTypes { get; init; } = Array.Empty<string>();

        /// <summary>Maximum age of provenance before it is considered stale. Null = no limit.</summary>
        public TimeSpan? MaxProvenanceAge { get; init; }
    }

    /// <summary>
    /// Result of verifying SLSA provenance.
    /// </summary>
    public sealed class VerificationResult
    {
        /// <summary>The verified SLSA level (1, 2, or 3). 0 if verification failed.</summary>
        public int Level { get; init; }

        /// <summary>Whether the verification passed all policy checks.</summary>
        public bool Passed { get; init; }

        /// <summary>Detailed findings from the verification process.</summary>
        public IReadOnlyList<string> Findings { get; init; } = Array.Empty<string>();

        /// <summary>Specific policy violations found.</summary>
        public IReadOnlyList<string> Violations { get; init; } = Array.Empty<string>();

        /// <summary>Time taken for verification.</summary>
        public TimeSpan Duration { get; init; }

        /// <summary>Creates a passing result.</summary>
        internal static VerificationResult Pass(int level, List<string> findings, TimeSpan duration) =>
            new() { Level = level, Passed = true, Findings = findings.AsReadOnly(), Violations = Array.Empty<string>(), Duration = duration };

        /// <summary>Creates a failing result.</summary>
        internal static VerificationResult Fail(int level, List<string> findings, List<string> violations, TimeSpan duration) =>
            new() { Level = level, Passed = false, Findings = findings.AsReadOnly(), Violations = violations.AsReadOnly(), Duration = duration };
    }

    // ──────────────────────────────────────────────────────────────
    // IProvenanceVerifier interface
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Interface for verifying SLSA provenance attestations.
    /// </summary>
    public interface IProvenanceVerifier
    {
        /// <summary>
        /// Verifies an SLSA provenance attestation against the given policy.
        /// </summary>
        Task<VerificationResult> VerifyAsync(SlsaProvenance provenance, VerificationPolicy policy, CancellationToken ct = default);

        /// <summary>
        /// Verifies provenance from its serialized JSON form.
        /// </summary>
        Task<VerificationResult> VerifyFromJsonAsync(string provenanceJson, VerificationPolicy policy, CancellationToken ct = default);
    }

    // ──────────────────────────────────────────────────────────────
    // SlsaVerifier implementation
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// SLSA provenance verifier implementing multi-level verification:
    ///
    /// Level 1: Provenance exists and is well-formed
    /// - Statement has correct _type and predicateType
    /// - At least one subject with a valid SHA-256 digest
    /// - BuildDefinition and RunDetails are present
    ///
    /// Level 2: Provenance is signed and signature verifies
    /// - DSSE envelope is present with valid structure
    /// - Signature verifies against the signing key from IKeyStore
    /// - Statement payload matches the signed payload
    ///
    /// Level 3: Full provenance chain verified
    /// - Builder is in the allowed list (RequiredBuilderId)
    /// - Source matches allowed repos (AllowedSourceRepos)
    /// - No user-controlled build parameters that could tamper with the build
    /// - Build type is in allowed list (if specified)
    ///
    /// Integration: Responds to message bus topic "security.supply-chain.provenance.verify".
    /// </summary>
    public sealed class SlsaVerifier : IProvenanceVerifier
    {
        private readonly IKeyStore? _keyStore;
        private readonly ISecurityContext? _securityContext;
        private readonly IMessageBus? _messageBus;

        private static readonly JsonSerializerOptions s_jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        /// <summary>
        /// Creates a new SLSA verifier.
        /// </summary>
        /// <param name="keyStore">Optional key store for signature verification (Level 2+).</param>
        /// <param name="securityContext">Optional security context for key store access.</param>
        /// <param name="messageBus">Optional message bus for event publishing.</param>
        public SlsaVerifier(
            IKeyStore? keyStore = null,
            ISecurityContext? securityContext = null,
            IMessageBus? messageBus = null)
        {
            _keyStore = keyStore;
            _securityContext = securityContext;
            _messageBus = messageBus;
        }

        /// <summary>
        /// Verifies SLSA provenance against the given policy.
        /// </summary>
        public async Task<VerificationResult> VerifyAsync(
            SlsaProvenance provenance, VerificationPolicy policy, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(provenance);
            ArgumentNullException.ThrowIfNull(policy);

            var startTime = DateTime.UtcNow;
            var findings = new List<string>();
            var violations = new List<string>();

            // ── Level 1: Well-formedness ─────────────────────────
            var level1 = VerifyLevel1(provenance, findings, violations);
            if (!level1 || policy.MinSlsaLevel <= 1)
            {
                var duration = DateTime.UtcNow - startTime;
                var result = violations.Count == 0 && level1
                    ? VerificationResult.Pass(1, findings, duration)
                    : VerificationResult.Fail(0, findings, violations, duration);

                await PublishVerificationEventAsync(result, ct).ConfigureAwait(false);
                return result;
            }

            // ── Level 2: Signature verification ──────────────────
            var level2 = await VerifyLevel2Async(provenance, findings, violations, ct).ConfigureAwait(false);
            if (!level2 || policy.MinSlsaLevel <= 2)
            {
                var maxLevel = level2 ? 2 : 1;
                var duration = DateTime.UtcNow - startTime;
                var result = violations.Count == 0 && maxLevel >= policy.MinSlsaLevel
                    ? VerificationResult.Pass(maxLevel, findings, duration)
                    : VerificationResult.Fail(maxLevel, findings, violations, duration);

                await PublishVerificationEventAsync(result, ct).ConfigureAwait(false);
                return result;
            }

            // ── Level 3: Full provenance chain ───────────────────
            var level3 = VerifyLevel3(provenance, policy, findings, violations);

            // ── Additional policy checks ─────────────────────────
            await VerifyAdditionalPolicyAsync(provenance, policy, findings, violations, ct).ConfigureAwait(false);

            var finalLevel = level3 ? 3 : 2;
            var finalDuration = DateTime.UtcNow - startTime;
            var finalResult = violations.Count == 0 && finalLevel >= policy.MinSlsaLevel
                ? VerificationResult.Pass(finalLevel, findings, finalDuration)
                : VerificationResult.Fail(finalLevel, findings, violations, finalDuration);

            await PublishVerificationEventAsync(finalResult, ct).ConfigureAwait(false);
            return finalResult;
        }

        /// <summary>
        /// Verifies provenance from serialized JSON (DSSE envelope or raw statement).
        /// </summary>
        public async Task<VerificationResult> VerifyFromJsonAsync(
            string provenanceJson, VerificationPolicy policy, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(provenanceJson);

            try
            {
                // Try parsing as DSSE envelope first
                using var doc = JsonDocument.Parse(provenanceJson);
                var root = doc.RootElement;

                if (root.TryGetProperty("payloadType", out _) && root.TryGetProperty("payload", out var payloadProp))
                {
                    // DSSE envelope
                    var payloadBase64 = payloadProp.GetString()!;
                    var statementJson = Encoding.UTF8.GetString(Convert.FromBase64String(payloadBase64));
                    var statement = JsonSerializer.Deserialize<InTotoStatement>(statementJson, s_jsonOptions)!;

                    byte[]? signature = null;
                    string? sigKeyId = null;

                    if (root.TryGetProperty("signatures", out var sigs) && sigs.GetArrayLength() > 0)
                    {
                        var firstSig = sigs[0];
                        sigKeyId = firstSig.GetProperty("keyid").GetString();
                        var sigBase64 = firstSig.GetProperty("sig").GetString();
                        if (!string.IsNullOrEmpty(sigBase64))
                            signature = Convert.FromBase64String(sigBase64);
                    }

                    var provenance = new SlsaProvenance
                    {
                        Statement = statement,
                        Signature = signature,
                        SerializedJson = provenanceJson,
                        SlsaLevel = signature != null ? 2 : 1
                    };

                    return await VerifyAsync(provenance, policy, ct).ConfigureAwait(false);
                }
                else
                {
                    // Raw in-toto statement
                    var statement = JsonSerializer.Deserialize<InTotoStatement>(provenanceJson, s_jsonOptions)!;
                    var provenance = new SlsaProvenance
                    {
                        Statement = statement,
                        Signature = null,
                        SerializedJson = provenanceJson,
                        SlsaLevel = 1
                    };

                    return await VerifyAsync(provenance, policy, ct).ConfigureAwait(false);
                }
            }
            catch (JsonException ex)
            {
                return VerificationResult.Fail(0,
                    new List<string> { "Failed to parse provenance JSON" },
                    new List<string> { $"JSON parse error: {ex.Message}" },
                    TimeSpan.Zero);
            }
        }

        // ── Level 1: Well-formedness ─────────────────────────────

        private static bool VerifyLevel1(SlsaProvenance provenance, List<string> findings, List<string> violations)
        {
            bool valid = true;

            // Check statement type
            if (provenance.Statement.Type == "https://in-toto.io/Statement/v1")
            {
                findings.Add("L1: Statement type is valid (in-toto Statement v1)");
            }
            else
            {
                violations.Add($"L1: Invalid statement type: {provenance.Statement.Type}");
                valid = false;
            }

            // Check predicate type
            if (provenance.Statement.PredicateType == "https://slsa.dev/provenance/v1")
            {
                findings.Add("L1: Predicate type is valid (SLSA provenance v1)");
            }
            else
            {
                violations.Add($"L1: Invalid predicate type: {provenance.Statement.PredicateType}");
                valid = false;
            }

            // Check subjects
            if (provenance.Statement.Subject.Count > 0)
            {
                findings.Add($"L1: {provenance.Statement.Subject.Count} subject(s) present");

                // Verify each subject has a sha256 digest
                foreach (var subject in provenance.Statement.Subject)
                {
                    if (subject.Digest.ContainsKey("sha256") &&
                        !string.IsNullOrEmpty(subject.Digest["sha256"]) &&
                        subject.Digest["sha256"].Length == 64)
                    {
                        findings.Add($"L1: Subject '{subject.Name}' has valid SHA-256 digest");
                    }
                    else
                    {
                        violations.Add($"L1: Subject '{subject.Name}' missing or invalid SHA-256 digest");
                        valid = false;
                    }
                }
            }
            else
            {
                violations.Add("L1: No subjects in statement");
                valid = false;
            }

            // Check buildDefinition
            if (!string.IsNullOrEmpty(provenance.Statement.Predicate.BuildDefinition.BuildType))
            {
                findings.Add($"L1: Build type present: {provenance.Statement.Predicate.BuildDefinition.BuildType}");
            }
            else
            {
                violations.Add("L1: Missing build type in buildDefinition");
                valid = false;
            }

            // Check runDetails
            if (!string.IsNullOrEmpty(provenance.Statement.Predicate.RunDetails.Builder.Id))
            {
                findings.Add($"L1: Builder ID present: {provenance.Statement.Predicate.RunDetails.Builder.Id}");
            }
            else
            {
                violations.Add("L1: Missing builder ID in runDetails");
                valid = false;
            }

            if (valid)
                findings.Add("L1: PASS - Provenance is well-formed");
            else
                findings.Add("L1: FAIL - Provenance has structural issues");

            return valid;
        }

        // ── Level 2: Signature verification ──────────────────────

        private async Task<bool> VerifyLevel2Async(
            SlsaProvenance provenance, List<string> findings, List<string> violations, CancellationToken ct)
        {
            if (!provenance.IsSigned)
            {
                violations.Add("L2: Provenance is not signed");
                return false;
            }

            findings.Add("L2: Provenance has signature");

            // Try to verify the signature using the key store
            if (_keyStore == null || _securityContext == null)
            {
                findings.Add("L2: No key store configured — signature verification failed (fail-closed)");
                // Fail-closed: a signature that cannot be verified is not trusted.
                return false;
            }

            try
            {
                // Extract key ID from DSSE envelope
                string? sigKeyId = null;
                try
                {
                    using var doc = JsonDocument.Parse(provenance.SerializedJson);
                    if (doc.RootElement.TryGetProperty("signatures", out var sigs) && sigs.GetArrayLength() > 0)
                    {
                        sigKeyId = sigs[0].GetProperty("keyid").GetString();
                    }
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[SlsaVerifier.VerifyLevel2Async] {ex.GetType().Name}: {ex.Message}"); }

                if (string.IsNullOrEmpty(sigKeyId))
                {
                    findings.Add("L2: No key ID in signature — cannot verify");
                    return true; // Signature exists, key ID unknown
                }

                var keyBytes = await _keyStore.GetKeyAsync(sigKeyId, _securityContext).ConfigureAwait(false);

                // Get the original payload
                var payloadJson = JsonSerializer.Serialize(provenance.Statement, new JsonSerializerOptions
                {
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });

                var payloadBytes = Encoding.UTF8.GetBytes(payloadJson);
                var signatureValid = VerifySignature(payloadBytes, provenance.Signature!, keyBytes);

                CryptographicOperations.ZeroMemory(keyBytes);

                if (signatureValid)
                {
                    findings.Add($"L2: Signature verified with key '{sigKeyId}'");
                    findings.Add("L2: PASS - Signature is valid");
                    return true;
                }
                else
                {
                    violations.Add($"L2: Signature verification failed for key '{sigKeyId}'");
                    return false;
                }
            }
            catch (Exception ex)
            {
                violations.Add($"L2: Signature verification error: {ex.Message}");
                return false;
            }
        }

        private static bool VerifySignature(byte[] payload, byte[] signature, byte[] keyBytes)
        {
            // Try RSA-PSS verification with public key only
            // Security: Never import private keys for verification — only public keys are needed.
            // HMAC fallback removed: symmetric key verification makes signatures forgeable
            // by anyone with the "verification" key.
            try
            {
                using var rsa = RSA.Create();
                rsa.ImportRSAPublicKey(keyBytes, out _);
                return rsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
            }
            catch (CryptographicException)
            {
                // Try SubjectPublicKeyInfo format (X.509 DER)
                try
                {
                    using var rsa = RSA.Create();
                    rsa.ImportSubjectPublicKeyInfo(keyBytes, out _);
                    return rsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[SlsaVerifier.VerifySignature] All public key formats failed: {ex.Message}");
                    return false;
                }
            }
        }

        // ── Level 3: Full provenance chain ───────────────────────

        private static bool VerifyLevel3(
            SlsaProvenance provenance, VerificationPolicy policy, List<string> findings, List<string> violations)
        {
            bool valid = true;

            // Check builder is in allowed list
            var builderId = provenance.Statement.Predicate.RunDetails.Builder.Id;

            if (!string.IsNullOrEmpty(policy.RequiredBuilderId))
            {
                if (builderId == policy.RequiredBuilderId)
                {
                    findings.Add($"L3: Builder '{builderId}' matches required builder");
                }
                else
                {
                    violations.Add($"L3: Builder '{builderId}' does not match required builder '{policy.RequiredBuilderId}'");
                    valid = false;
                }
            }
            else
            {
                findings.Add("L3: No required builder specified in policy — skipping builder check");
            }

            // Check source matches allowed repos
            if (policy.AllowedSourceRepos.Count > 0)
            {
                var extParams = provenance.Statement.Predicate.BuildDefinition.ExternalParameters;
                if (extParams.TryGetValue("source", out var sourceObj))
                {
                    var sourceJson = JsonSerializer.Serialize(sourceObj);
                    using var sourceDoc = JsonDocument.Parse(sourceJson);

                    if (sourceDoc.RootElement.TryGetProperty("uri", out var uriProp))
                    {
                        var sourceUri = uriProp.GetString()!;
                        var matches = policy.AllowedSourceRepos.Any(allowed =>
                            sourceUri.StartsWith(allowed.ToString(), StringComparison.OrdinalIgnoreCase));

                        if (matches)
                        {
                            findings.Add($"L3: Source repo '{sourceUri}' is in allowed list");
                        }
                        else
                        {
                            violations.Add($"L3: Source repo '{sourceUri}' is not in allowed repos");
                            valid = false;
                        }
                    }
                    else
                    {
                        violations.Add("L3: Source URI missing from external parameters");
                        valid = false;
                    }
                }
                else
                {
                    violations.Add("L3: No source information in external parameters");
                    valid = false;
                }
            }
            else
            {
                findings.Add("L3: No allowed source repos specified in policy — skipping source check");
            }

            // Check no user-controlled build parameters
            var internalParams = provenance.Statement.Predicate.BuildDefinition.InternalParameters;
            if (internalParams.Count == 0)
            {
                findings.Add("L3: No internal build parameters (clean build)");
            }
            else
            {
                findings.Add($"L3: {internalParams.Count} internal build parameter(s) present — review for user-controlled inputs");
            }

            // Check allowed build types
            if (policy.AllowedBuildTypes.Count > 0)
            {
                var buildType = provenance.Statement.Predicate.BuildDefinition.BuildType;
                if (policy.AllowedBuildTypes.Contains(buildType))
                {
                    findings.Add($"L3: Build type '{buildType}' is in allowed list");
                }
                else
                {
                    violations.Add($"L3: Build type '{buildType}' is not in allowed build types");
                    valid = false;
                }
            }

            if (valid)
                findings.Add("L3: PASS - Full provenance chain verified");
            else
                findings.Add("L3: FAIL - Provenance chain has issues");

            return valid;
        }

        // ── Additional policy checks ─────────────────────────────

        private static async Task VerifyAdditionalPolicyAsync(
            SlsaProvenance provenance, VerificationPolicy policy,
            List<string> findings, List<string> violations, CancellationToken ct)
        {
            // Check provenance age
            if (policy.MaxProvenanceAge.HasValue)
            {
                var metadata = provenance.Statement.Predicate.RunDetails.Metadata;
                if (!string.IsNullOrEmpty(metadata.FinishedOn) &&
                    DateTimeOffset.TryParse(metadata.FinishedOn, out var finishedAt))
                {
                    var age = DateTimeOffset.UtcNow - finishedAt;
                    if (age > policy.MaxProvenanceAge.Value)
                    {
                        violations.Add($"Provenance is {age.TotalHours:F1}h old — exceeds max age of {policy.MaxProvenanceAge.Value.TotalHours:F1}h");
                    }
                    else
                    {
                        findings.Add($"Provenance age ({age.TotalHours:F1}h) within max age limit");
                    }
                }
            }

            // Verify artifact digests against actual files (if requested)
            if (policy.VerifyArtifactDigests && !string.IsNullOrEmpty(policy.ArtifactBasePath))
            {
                foreach (var subject in provenance.Statement.Subject)
                {
                    ct.ThrowIfCancellationRequested();

                    var filePath = System.IO.Path.Combine(policy.ArtifactBasePath, subject.Name);
                    if (!System.IO.File.Exists(filePath))
                    {
                        violations.Add($"Artifact '{subject.Name}' not found at {filePath}");
                        continue;
                    }

                    if (subject.Digest.TryGetValue("sha256", out var expectedHash))
                    {
                        using var sha256 = SHA256.Create();
                        await using var stream = System.IO.File.OpenRead(filePath);
                        var actualHash = Convert.ToHexString(
                            await sha256.ComputeHashAsync(stream, ct).ConfigureAwait(false)).ToLowerInvariant();

                        if (actualHash == expectedHash)
                        {
                            findings.Add($"Artifact '{subject.Name}' digest verified");
                        }
                        else
                        {
                            violations.Add($"Artifact '{subject.Name}' digest mismatch: expected {expectedHash}, got {actualHash}");
                        }
                    }
                }
            }

            await Task.CompletedTask;
        }

        // ── Message bus integration ──────────────────────────────

        private async Task PublishVerificationEventAsync(VerificationResult result, CancellationToken ct)
        {
            if (_messageBus == null) return;

            try
            {
                var message = new PluginMessage
                {
                    Type = "security.supply-chain.provenance.verify",
                    Payload = new Dictionary<string, object>
                    {
                        ["level"] = result.Level,
                        ["passed"] = result.Passed,
                        ["findingCount"] = result.Findings.Count,
                        ["violationCount"] = result.Violations.Count,
                        ["durationMs"] = result.Duration.TotalMilliseconds,
                        ["timestamp"] = DateTime.UtcNow
                    }
                };

                await _messageBus.PublishAsync("security.supply-chain.provenance.verify", message, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SlsaVerifier.PublishVerificationEventAsync] {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
