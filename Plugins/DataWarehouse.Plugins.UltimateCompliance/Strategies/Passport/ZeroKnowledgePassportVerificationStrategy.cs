using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Compliance;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.Passport;

/// <summary>
/// Zero-knowledge passport verification strategy that generates and verifies
/// Schnorr-like proofs for compliance passport claims.
/// <para>
/// This strategy allows third parties to verify compliance status (e.g., "this object
/// is GDPR-compliant") without revealing the full passport contents. Proofs are
/// commitment-based using HMAC-SHA256 and can be cached for efficient repeat verification.
/// </para>
/// </summary>
public sealed class ZeroKnowledgePassportVerificationStrategy : ComplianceStrategyBase
{
    private readonly ConcurrentDictionary<string, ZkPassportProof> _proofCache = new();
    private static readonly TimeSpan ProofCacheDuration = TimeSpan.FromHours(1);
    private byte[] _signingKey = Array.Empty<byte>();

    /// <inheritdoc/>
    public override string StrategyId => "passport-zk-verification";

    /// <inheritdoc/>
    public override string StrategyName => "Zero-Knowledge Passport Verification";

    /// <inheritdoc/>
    public override string Framework => "CompliancePassport";

    /// <inheritdoc/>
    public override Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken cancellationToken = default)
    {
        base.InitializeAsync(configuration, cancellationToken);

        if (configuration.TryGetValue("SigningKey", out var keyObj) && keyObj is string keyStr && keyStr.Length > 0)
        {
            _signingKey = Convert.FromBase64String(keyStr);
        }
        else
        {
            _signingKey = RandomNumberGenerator.GetBytes(32);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Generates a zero-knowledge proof that a passport satisfies the specified claim.
    /// </summary>
    /// <param name="passport">The compliance passport to prove claims about.</param>
    /// <param name="claim">
    /// Claim to prove. Supported formats:
    /// <list type="bullet">
    /// <item><c>covers:{regulation}</c> - passport has entry for regulation</item>
    /// <item><c>status:{status}</c> - passport status matches</item>
    /// <item><c>score:&gt;={threshold}</c> - all entries have score >= threshold</item>
    /// <item><c>valid</c> - passport is currently valid</item>
    /// <item><c>zone:{zoneId}</c> - passport covers zone regulations (via metadata)</item>
    /// </list>
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A cryptographic proof that the claim is true.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the claim is false and cannot be proved.</exception>
    /// <exception cref="ArgumentException">Thrown when the claim format is not recognized.</exception>
    public Task<ZkPassportProof> GenerateProofAsync(CompliancePassport passport, string claim, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(passport);
        ArgumentException.ThrowIfNullOrWhiteSpace(claim);

        var cacheKey = $"{passport.PassportId}:{claim}";
        if (_proofCache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
        {
            return Task.FromResult(cached);
        }

        var claimIsTrue = EvaluateClaim(passport, claim);
        if (!claimIsTrue)
        {
            throw new InvalidOperationException(
                $"Cannot generate zero-knowledge proof: claim '{claim}' is false for passport '{passport.PassportId}'.");
        }

        var now = DateTimeOffset.UtcNow;

        // Step 1: Generate random nonce (32 bytes)
        var nonce = RandomNumberGenerator.GetBytes(32);

        // Step 2: Compute commitment = HMAC-SHA256(nonce, passportId + claim)
        var commitmentData = Encoding.UTF8.GetBytes(passport.PassportId + claim);
        byte[] commitment;
        using (var hmac = new HMACSHA256(nonce))
        {
            commitment = hmac.ComputeHash(commitmentData);
        }

        // Step 3: Generate challenge = SHA256(commitment + passportId + claim + timestamp)
        var timestampBytes = BitConverter.GetBytes(now.ToUnixTimeMilliseconds());
        byte[] challenge;
        using (var sha = SHA256.Create())
        {
            var challengeInput = CombineArrays(
                commitment,
                Encoding.UTF8.GetBytes(passport.PassportId),
                Encoding.UTF8.GetBytes(claim),
                timestampBytes);
            challenge = sha.ComputeHash(challengeInput);
        }

        // Step 4: Generate response = HMAC-SHA256(signingKey, nonce + challenge)
        byte[] response;
        using (var hmac = new HMACSHA256(_signingKey))
        {
            response = hmac.ComputeHash(CombineArrays(nonce, challenge));
        }

        var proof = new ZkPassportProof
        {
            ProofId = Guid.NewGuid().ToString("N"),
            PassportId = passport.PassportId,
            Claim = claim,
            Commitment = commitment,
            Challenge = challenge,
            Response = response,
            GeneratedAt = now,
            ExpiresAt = now.Add(ProofCacheDuration),
            Nonce = nonce,
            TimestampTicks = now.ToUnixTimeMilliseconds()
        };

        _proofCache[cacheKey] = proof;
        EvictExpiredProofs();

        return Task.FromResult(proof);
    }

    /// <summary>
    /// Verifies a zero-knowledge proof without accessing the original passport.
    /// </summary>
    /// <param name="proof">The proof to verify.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Verification result indicating whether the proof is valid.</returns>
    public Task<ZkVerificationResult> VerifyProofAsync(ZkPassportProof proof, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(proof);

        var now = DateTimeOffset.UtcNow;

        // Check expiration
        if (proof.ExpiresAt <= now)
        {
            return Task.FromResult(new ZkVerificationResult
            {
                IsValid = false,
                Claim = proof.Claim,
                FailureReason = "Proof has expired.",
                VerifiedAt = now
            });
        }

        // Recompute challenge from commitment + passportId + claim + timestamp
        var timestampBytes = BitConverter.GetBytes(proof.TimestampTicks);
        byte[] recomputedChallenge;
        using (var sha = SHA256.Create())
        {
            var challengeInput = CombineArrays(
                proof.Commitment,
                Encoding.UTF8.GetBytes(proof.PassportId),
                Encoding.UTF8.GetBytes(proof.Claim),
                timestampBytes);
            recomputedChallenge = sha.ComputeHash(challengeInput);
        }

        if (!CryptographicEquals(recomputedChallenge, proof.Challenge))
        {
            return Task.FromResult(new ZkVerificationResult
            {
                IsValid = false,
                Claim = proof.Claim,
                FailureReason = "Challenge verification failed: proof may have been tampered with.",
                VerifiedAt = now
            });
        }

        // Verify response = HMAC-SHA256(signingKey, nonce + challenge)
        byte[] expectedResponse;
        using (var hmac = new HMACSHA256(_signingKey))
        {
            expectedResponse = hmac.ComputeHash(CombineArrays(proof.Nonce, proof.Challenge));
        }

        if (!CryptographicEquals(expectedResponse, proof.Response))
        {
            return Task.FromResult(new ZkVerificationResult
            {
                IsValid = false,
                Claim = proof.Claim,
                FailureReason = "Response verification failed: signing key mismatch or proof tampering.",
                VerifiedAt = now
            });
        }

        return Task.FromResult(new ZkVerificationResult
        {
            IsValid = true,
            Claim = proof.Claim,
            FailureReason = null,
            VerifiedAt = now
        });
    }

    /// <summary>
    /// Generates proofs for multiple claims against a single passport.
    /// </summary>
    /// <param name="passport">The compliance passport to prove claims about.</param>
    /// <param name="claims">List of claims to prove.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of proofs, one per claim.</returns>
    public async Task<IReadOnlyList<ZkPassportProof>> GenerateBatchProofsAsync(
        CompliancePassport passport,
        IReadOnlyList<string> claims,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(passport);
        ArgumentNullException.ThrowIfNull(claims);

        var proofs = new List<ZkPassportProof>(claims.Count);
        foreach (var claim in claims)
        {
            ct.ThrowIfCancellationRequested();
            var proof = await GenerateProofAsync(passport, claim, ct).ConfigureAwait(false);
            proofs.Add(proof);
        }

        return proofs;
    }

    /// <inheritdoc/>
    protected override Task<ComplianceResult> CheckComplianceCoreAsync(
        ComplianceContext context,
        CancellationToken cancellationToken)
    {
        // Check if a valid proof exists for the resource in the proof cache
        var resourceId = context.ResourceId ?? context.OperationType;
        var hasValidProof = false;
        string? matchedClaim = null;

        foreach (var kvp in _proofCache)
        {
            if (kvp.Value.PassportId == resourceId && kvp.Value.ExpiresAt > DateTimeOffset.UtcNow)
            {
                hasValidProof = true;
                matchedClaim = kvp.Value.Claim;
                break;
            }
        }

        var result = new ComplianceResult
        {
            IsCompliant = hasValidProof,
            Framework = Framework,
            Status = hasValidProof ? ComplianceStatus.Compliant : ComplianceStatus.NonCompliant,
            Violations = hasValidProof
                ? Array.Empty<ComplianceViolation>()
                : new[]
                {
                    new ComplianceViolation
                    {
                        Code = "ZK-001",
                        Description = $"No valid zero-knowledge proof found for resource '{resourceId}'.",
                        Severity = ViolationSeverity.High,
                        AffectedResource = resourceId,
                        Remediation = "Generate a ZK proof for the resource's compliance passport."
                    }
                },
            Recommendations = hasValidProof
                ? new[] { $"Valid ZK proof exists for claim: {matchedClaim}" }
                : new[] { "Generate ZK proofs for all required compliance claims." },
            Metadata = new Dictionary<string, object>
            {
                ["CachedProofCount"] = _proofCache.Count,
                ["HasValidProof"] = hasValidProof
            }
        };

        return Task.FromResult(result);
    }

    /// <summary>
    /// Evaluates whether a claim is true for the given passport.
    /// </summary>
    private static bool EvaluateClaim(CompliancePassport passport, string claim)
    {
        if (string.Equals(claim, "valid", StringComparison.OrdinalIgnoreCase))
        {
            return passport.IsValid();
        }

        var colonIndex = claim.IndexOf(':');
        if (colonIndex < 0)
        {
            throw new ArgumentException($"Unrecognized claim format: '{claim}'. Expected 'valid' or 'type:value'.", nameof(claim));
        }

        var claimType = claim[..colonIndex].ToLowerInvariant();
        var claimValue = claim[(colonIndex + 1)..];

        return claimType switch
        {
            "covers" => passport.CoversRegulation(claimValue),
            "status" => Enum.TryParse<PassportStatus>(claimValue, ignoreCase: true, out var status)
                        && passport.Status == status,
            "score" when claimValue.StartsWith(">=", StringComparison.Ordinal) =>
                double.TryParse(claimValue[2..], NumberStyles.Float, CultureInfo.InvariantCulture, out var threshold)
                && passport.Entries.Count > 0
                && passport.Entries.All(e => e.ComplianceScore >= threshold),
            "zone" => passport.Metadata != null
                      && passport.Metadata.TryGetValue("CoveredZones", out var zones)
                      && zones is IEnumerable<string> zoneList
                      && zoneList.Any(z => string.Equals(z, claimValue, StringComparison.OrdinalIgnoreCase)),
            _ => throw new ArgumentException($"Unrecognized claim type: '{claimType}'.", nameof(claim))
        };
    }

    /// <summary>
    /// Combines multiple byte arrays into a single array.
    /// </summary>
    private static byte[] CombineArrays(params byte[][] arrays)
    {
        var totalLength = 0;
        foreach (var arr in arrays)
            totalLength += arr.Length;

        var result = new byte[totalLength];
        var offset = 0;
        foreach (var arr in arrays)
        {
            Buffer.BlockCopy(arr, 0, result, offset, arr.Length);
            offset += arr.Length;
        }

        return result;
    }

    /// <summary>
    /// Constant-time comparison of two byte arrays to prevent timing attacks.
    /// </summary>
    private static bool CryptographicEquals(byte[] a, byte[] b)
    {
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    /// <summary>
    /// Evicts expired proofs from the cache.
    /// </summary>
    private void EvictExpiredProofs()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var kvp in _proofCache)
        {
            if (kvp.Value.ExpiresAt <= now)
            {
                _proofCache.TryRemove(kvp.Key, out _);
            }
        }
    }
}

/// <summary>
/// A zero-knowledge proof that a compliance passport satisfies a specific claim.
/// The proof allows a verifier to confirm the claim is true without learning
/// any other information about the passport contents.
/// </summary>
public sealed record ZkPassportProof
{
    /// <summary>
    /// Unique identifier for this proof instance.
    /// </summary>
    public required string ProofId { get; init; }

    /// <summary>
    /// Identifier of the passport this proof is about.
    /// </summary>
    public required string PassportId { get; init; }

    /// <summary>
    /// The claim being proved (e.g., "covers:GDPR", "status:Active", "score:>=0.8").
    /// </summary>
    public required string Claim { get; init; }

    /// <summary>
    /// Cryptographic commitment binding the prover to the claim.
    /// </summary>
    public required byte[] Commitment { get; init; }

    /// <summary>
    /// Challenge derived from commitment, passport identity, claim, and timestamp.
    /// </summary>
    public required byte[] Challenge { get; init; }

    /// <summary>
    /// Response proving knowledge of the signing key without revealing it.
    /// </summary>
    public required byte[] Response { get; init; }

    /// <summary>
    /// Timestamp when this proof was generated.
    /// </summary>
    public DateTimeOffset GeneratedAt { get; init; }

    /// <summary>
    /// Timestamp when this proof expires and is no longer valid.
    /// </summary>
    public DateTimeOffset ExpiresAt { get; init; }

    /// <summary>
    /// Nonce used during proof generation, needed for verification.
    /// </summary>
    public required byte[] Nonce { get; init; }

    /// <summary>
    /// Unix millisecond timestamp used in challenge computation, needed for verification.
    /// </summary>
    public long TimestampTicks { get; init; }
}

/// <summary>
/// Result of verifying a <see cref="ZkPassportProof"/>.
/// </summary>
public sealed record ZkVerificationResult
{
    /// <summary>
    /// Whether the proof is valid and the claim is confirmed true.
    /// </summary>
    public required bool IsValid { get; init; }

    /// <summary>
    /// The claim that was verified.
    /// </summary>
    public required string Claim { get; init; }

    /// <summary>
    /// Reason for verification failure, if any.
    /// </summary>
    public string? FailureReason { get; init; }

    /// <summary>
    /// Timestamp when verification was performed.
    /// </summary>
    public DateTimeOffset VerifiedAt { get; init; }
}
