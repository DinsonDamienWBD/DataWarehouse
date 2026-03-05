using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Compliance;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.Passport;

/// <summary>
/// Comprehensive passport verification API strategy that validates compliance passports
/// across six dimensions: signature, status, expiry, entry currency, evidence chain,
/// and field completeness.
/// <para>
/// Provides zone-specific verification to confirm regulation coverage for sovereignty zones,
/// and chain verification for passport succession (renewal/reissue tracking).
/// </para>
/// </summary>
public sealed class PassportVerificationApiStrategy : ComplianceStrategyBase
{
    private byte[] _signingKey = Array.Empty<byte>();
    private TimeSpan _defaultAssessmentPeriod = TimeSpan.FromDays(90);

    // Statistics
    private long _verificationsTotal;
    private long _verificationsValid;
    private long _verificationsInvalid;
    private readonly BoundedDictionary<string, long> _failureReasonCounts = new BoundedDictionary<string, long>(1000);

    /// <inheritdoc/>
    public override string StrategyId => "passport-verification-api";

    /// <inheritdoc/>
    public override string StrategyName => "Passport Verification API";

    /// <inheritdoc/>
    public override string Framework => "CompliancePassport";

    /// <inheritdoc/>
    public override async Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken cancellationToken = default)
    {
        await base.InitializeAsync(configuration, cancellationToken);

        if (configuration.TryGetValue("SigningKey", out var keyObj) && keyObj is string keyStr && keyStr.Length > 0)
        {
            _signingKey = Convert.FromBase64String(keyStr);
        }
        else
        {
            _signingKey = RandomNumberGenerator.GetBytes(32);
        }

        if (configuration.TryGetValue("DefaultAssessmentPeriodDays", out var periodObj)
            && periodObj is int days && days > 0)
        {
            _defaultAssessmentPeriod = TimeSpan.FromDays(days);
        }

    }

    /// <summary>
    /// Performs comprehensive verification of a compliance passport across all dimensions.
    /// </summary>
    /// <param name="passport">The passport to verify.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Verification result with detailed failure reasons if any.</returns>
    public Task<PassportVerificationResult> VerifyPassportAsync(CompliancePassport passport, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(passport);

        var failureReasons = new List<string>();
        var now = DateTimeOffset.UtcNow;

        // 1. Signature check
        var signatureValid = VerifySignature(passport);
        if (!signatureValid)
        {
            failureReasons.Add("Digital signature verification failed: passport may have been tampered with.");
        }

        // 2. Status check
        if (passport.Status != PassportStatus.Active)
        {
            failureReasons.Add($"Passport status is '{passport.Status}', expected 'Active'.");
        }

        // 3. Expiry check
        if (passport.ExpiresAt <= now)
        {
            failureReasons.Add($"Passport expired at {passport.ExpiresAt:O}.");
        }

        // 4. Entry currency check
        var allEntriesCurrent = true;
        foreach (var entry in passport.Entries)
        {
            var assessmentDeadline = entry.NextAssessmentDue ?? entry.AssessedAt.Add(_defaultAssessmentPeriod);
            if (assessmentDeadline <= now)
            {
                allEntriesCurrent = false;
                failureReasons.Add(
                    $"Entry for regulation '{entry.RegulationId}' control '{entry.ControlId}' " +
                    $"assessment is overdue (due by {assessmentDeadline:O}).");
            }
        }

        // 5. Evidence chain check
        if (passport.EvidenceChain.Count == 0)
        {
            failureReasons.Add("No evidence links found in passport evidence chain.");
        }
        else
        {
            foreach (var evidence in passport.EvidenceChain)
            {
                if (evidence.ContentHash == null || evidence.ContentHash.Length == 0)
                {
                    failureReasons.Add(
                        $"Evidence '{evidence.EvidenceId}' has no content hash for integrity verification.");
                }
            }
        }

        // 6. Completeness check
        if (string.IsNullOrWhiteSpace(passport.PassportId))
        {
            failureReasons.Add("PassportId is missing or empty.");
        }

        if (string.IsNullOrWhiteSpace(passport.ObjectId))
        {
            failureReasons.Add("ObjectId is missing or empty.");
        }

        if (string.IsNullOrWhiteSpace(passport.IssuerId))
        {
            failureReasons.Add("IssuerId is missing or empty.");
        }

        if (passport.Entries.Count == 0)
        {
            failureReasons.Add("Passport has no compliance entries.");
        }

        var isValid = failureReasons.Count == 0;

        // Update statistics
        Interlocked.Increment(ref _verificationsTotal);
        if (isValid)
        {
            Interlocked.Increment(ref _verificationsValid);
        }
        else
        {
            Interlocked.Increment(ref _verificationsInvalid);
            foreach (var reason in failureReasons)
            {
                var category = CategorizeFailureReason(reason);
                _failureReasonCounts.AddOrUpdate(category, 1, (_, count) => Interlocked.Increment(ref count));
            }
        }

        var result = new PassportVerificationResult
        {
            IsValid = isValid,
            Passport = passport,
            SignatureValid = signatureValid,
            AllEntriesCurrent = allEntriesCurrent,
            FailureReasons = failureReasons,
            VerifiedAt = now
        };

        return Task.FromResult(result);
    }

    /// <summary>
    /// Verifies a passport against zone-specific regulation requirements.
    /// Runs all standard checks plus confirms the passport covers every regulation required by the zone.
    /// </summary>
    /// <param name="passport">The passport to verify.</param>
    /// <param name="zoneId">Identifier of the sovereignty zone.</param>
    /// <param name="zoneRegulations">List of regulation identifiers required by the zone.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Verification result including zone-specific failures.</returns>
    public async Task<PassportVerificationResult> VerifyPassportForZoneAsync(
        CompliancePassport passport,
        string zoneId,
        IReadOnlyList<string> zoneRegulations,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(passport);
        ArgumentException.ThrowIfNullOrWhiteSpace(zoneId);
        ArgumentNullException.ThrowIfNull(zoneRegulations);

        // Run standard verification
        var baseResult = await VerifyPassportAsync(passport, ct).ConfigureAwait(false);
        var failureReasons = new List<string>(baseResult.FailureReasons);

        // Check zone regulation coverage
        foreach (var regulation in zoneRegulations)
        {
            if (!passport.CoversRegulation(regulation))
            {
                failureReasons.Add($"Missing regulation coverage: {regulation} (required by zone '{zoneId}').");
            }
        }

        var isValid = failureReasons.Count == 0;

        // Update statistics for zone check
        if (!isValid && failureReasons.Count > baseResult.FailureReasons.Count)
        {
            var zoneFailCount = failureReasons.Count - baseResult.FailureReasons.Count;
            _failureReasonCounts.AddOrUpdate("ZoneRegulationCoverage", zoneFailCount,
                (_, count) => Interlocked.Add(ref count, zoneFailCount));
        }

        return new PassportVerificationResult
        {
            IsValid = isValid,
            Passport = passport,
            SignatureValid = baseResult.SignatureValid,
            AllEntriesCurrent = baseResult.AllEntriesCurrent,
            FailureReasons = failureReasons,
            VerifiedAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>
    /// Verifies a chain of passports representing successive compliance certifications
    /// for the same object. Validates each passport individually, checks that all share
    /// the same ObjectId, and verifies temporal ordering.
    /// </summary>
    /// <param name="passportChain">Ordered list of passports from oldest to newest.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Aggregate verification result for the entire chain.</returns>
    public async Task<PassportVerificationResult> VerifyPassportChainAsync(
        IReadOnlyList<CompliancePassport> passportChain,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(passportChain);

        if (passportChain.Count == 0)
        {
            return new PassportVerificationResult
            {
                IsValid = false,
                Passport = null!,
                FailureReasons = new[] { "Passport chain is empty." },
                VerifiedAt = DateTimeOffset.UtcNow
            };
        }

        var failureReasons = new List<string>();
        var allSignaturesValid = true;
        var allEntriesCurrent = true;

        // Verify each passport individually
        for (var i = 0; i < passportChain.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var result = await VerifyPassportAsync(passportChain[i], ct).ConfigureAwait(false);

            // Only the latest passport needs to be fully valid; predecessors may be expired
            if (i == passportChain.Count - 1 && !result.IsValid)
            {
                foreach (var reason in result.FailureReasons)
                {
                    failureReasons.Add($"[Passport {i + 1}/{passportChain.Count}] {reason}");
                }
            }
            else if (i < passportChain.Count - 1)
            {
                // For predecessors, only check non-temporal issues
                if (!result.SignatureValid)
                {
                    failureReasons.Add(
                        $"[Passport {i + 1}/{passportChain.Count}] Signature verification failed.");
                }
            }

            if (!result.SignatureValid) allSignaturesValid = false;
            if (!result.AllEntriesCurrent) allEntriesCurrent = false;
        }

        // Verify chain integrity: all passports reference the same object
        var expectedObjectId = passportChain[0].ObjectId;
        for (var i = 1; i < passportChain.Count; i++)
        {
            if (!string.Equals(passportChain[i].ObjectId, expectedObjectId, StringComparison.Ordinal))
            {
                failureReasons.Add(
                    $"Chain integrity error: passport {i + 1} ObjectId '{passportChain[i].ObjectId}' " +
                    $"does not match expected '{expectedObjectId}'.");
            }
        }

        // Verify temporal ordering: each passport issued after the previous one
        for (var i = 1; i < passportChain.Count; i++)
        {
            if (passportChain[i].IssuedAt <= passportChain[i - 1].IssuedAt)
            {
                failureReasons.Add(
                    $"Temporal ordering error: passport {i + 1} issued at {passportChain[i].IssuedAt:O} " +
                    $"is not after passport {i} issued at {passportChain[i - 1].IssuedAt:O}.");
            }
        }

        return new PassportVerificationResult
        {
            IsValid = failureReasons.Count == 0,
            Passport = passportChain[^1],
            SignatureValid = allSignaturesValid,
            AllEntriesCurrent = allEntriesCurrent,
            FailureReasons = failureReasons,
            VerifiedAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>
    /// Gets verification statistics including total, valid, invalid counts and failure reason breakdown.
    /// </summary>
    public VerificationStatistics GetVerificationStatistics()
    {
        return new VerificationStatistics
        {
            TotalVerifications = Interlocked.Read(ref _verificationsTotal),
            ValidVerifications = Interlocked.Read(ref _verificationsValid),
            InvalidVerifications = Interlocked.Read(ref _verificationsInvalid),
            FailureReasonCounts = new Dictionary<string, long>(_failureReasonCounts)
        };
    }

    /// <inheritdoc/>
    protected override async Task<ComplianceResult> CheckComplianceCoreAsync(
        ComplianceContext context,
        CancellationToken cancellationToken)
    {
        // Attempt to extract a passport from context attributes
        CompliancePassport? passport = null;
        if (context.Attributes.TryGetValue("Passport", out var passportObj))
        {
            passport = passportObj as CompliancePassport;
            if (passport == null && passportObj is JsonElement jsonElement)
            {
                passport = JsonSerializer.Deserialize<CompliancePassport>(jsonElement.GetRawText());
            }
        }

        if (passport == null)
        {
            return new ComplianceResult
            {
                IsCompliant = false,
                Framework = Framework,
                Status = ComplianceStatus.NonCompliant,
                Violations = new[]
                {
                    new ComplianceViolation
                    {
                        Code = "PV-001",
                        Description = "No compliance passport found in context for verification.",
                        Severity = ViolationSeverity.High,
                        AffectedResource = context.ResourceId,
                        Remediation = "Provide a CompliancePassport in context attributes under key 'Passport'."
                    }
                }
            };
        }

        var verificationResult = await VerifyPassportAsync(passport, cancellationToken).ConfigureAwait(false);

        return new ComplianceResult
        {
            IsCompliant = verificationResult.IsValid,
            Framework = Framework,
            Status = verificationResult.IsValid ? ComplianceStatus.Compliant : ComplianceStatus.NonCompliant,
            Violations = verificationResult.IsValid
                ? Array.Empty<ComplianceViolation>()
                : verificationResult.FailureReasons.Select((reason, index) => new ComplianceViolation
                {
                    Code = $"PV-{index + 2:D3}",
                    Description = reason,
                    Severity = ViolationSeverity.High,
                    AffectedResource = passport.PassportId,
                    Remediation = "Address the specific verification failure."
                }).ToArray(),
            Recommendations = verificationResult.IsValid
                ? new[] { "Passport verification passed all checks." }
                : new[] { "Review and address all verification failures before relying on this passport." },
            Metadata = new Dictionary<string, object>
            {
                ["PassportId"] = passport.PassportId,
                ["SignatureValid"] = verificationResult.SignatureValid,
                ["AllEntriesCurrent"] = verificationResult.AllEntriesCurrent,
                ["FailureCount"] = verificationResult.FailureReasons.Count
            }
        };
    }

    /// <summary>
    /// Verifies the digital signature of a passport by recomputing the HMAC-SHA256
    /// over the passport content and comparing with the stored signature.
    /// </summary>
    private bool VerifySignature(CompliancePassport passport)
    {
        if (passport.DigitalSignature == null || passport.DigitalSignature.Length == 0)
        {
            return false;
        }

        if (_signingKey.Length == 0)
        {
            return false;
        }

        // Recompute HMAC-SHA256 using the same JSON serializer options as PassportIssuanceStrategy.SignPassport
        // (camelCase, no indentation, DigitalSignature nulled out) to ensure byte-for-byte match
        var signable = passport with { DigitalSignature = null };
        var json = JsonSerializer.Serialize(signable, _signerOptions);
        byte[] computed;
        using (var hmac = new HMACSHA256(_signingKey))
        {
            computed = hmac.ComputeHash(Encoding.UTF8.GetBytes(json));
        }

        return CryptographicOperations.FixedTimeEquals(computed, passport.DigitalSignature);
    }

    // Serializer options must exactly match PassportIssuanceStrategy._serializerOptions
    private static readonly JsonSerializerOptions _signerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    // Legacy canonical builder — retained for reference but no longer used for signature computation
    private static string BuildSignatureContent(CompliancePassport passport)
    {
        var sb = new StringBuilder();
        sb.Append(passport.PassportId);
        sb.Append('|');
        sb.Append(passport.ObjectId);
        sb.Append('|');
        sb.Append(passport.Status);
        sb.Append('|');
        sb.Append(passport.Scope);
        sb.Append('|');
        sb.Append(passport.IssuedAt.ToUnixTimeMilliseconds());
        sb.Append('|');
        sb.Append(passport.ExpiresAt.ToUnixTimeMilliseconds());
        sb.Append('|');
        sb.Append(passport.IssuerId);

        foreach (var entry in passport.Entries.OrderBy(e => e.RegulationId).ThenBy(e => e.ControlId))
        {
            sb.Append('|');
            sb.Append(entry.RegulationId);
            sb.Append(':');
            sb.Append(entry.ControlId);
            sb.Append(':');
            sb.Append(entry.Status);
            sb.Append(':');
            sb.Append(entry.ComplianceScore.ToString("F4", System.Globalization.CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Categorizes a failure reason string into a bucket for statistics tracking.
    /// </summary>
    private static string CategorizeFailureReason(string reason)
    {
        if (reason.Contains("signature", StringComparison.OrdinalIgnoreCase)) return "Signature";
        if (reason.Contains("status", StringComparison.OrdinalIgnoreCase)) return "Status";
        if (reason.Contains("expired", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("expir", StringComparison.OrdinalIgnoreCase)) return "Expiry";
        if (reason.Contains("assessment", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("overdue", StringComparison.OrdinalIgnoreCase)) return "EntryCurrency";
        if (reason.Contains("evidence", StringComparison.OrdinalIgnoreCase)) return "EvidenceChain";
        if (reason.Contains("missing", StringComparison.OrdinalIgnoreCase) &&
            reason.Contains("regulation", StringComparison.OrdinalIgnoreCase)) return "ZoneRegulationCoverage";
        if (reason.Contains("missing", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("empty", StringComparison.OrdinalIgnoreCase)) return "Completeness";
        return "Other";
    }
}

/// <summary>
/// Statistics for passport verification operations.
/// </summary>
public sealed record VerificationStatistics
{
    /// <summary>Total number of verifications performed.</summary>
    public long TotalVerifications { get; init; }

    /// <summary>Number of verifications that passed all checks.</summary>
    public long ValidVerifications { get; init; }

    /// <summary>Number of verifications that failed one or more checks.</summary>
    public long InvalidVerifications { get; init; }

    /// <summary>Breakdown of failure counts by reason category.</summary>
    public IReadOnlyDictionary<string, long> FailureReasonCounts { get; init; } = new Dictionary<string, long>();
}
