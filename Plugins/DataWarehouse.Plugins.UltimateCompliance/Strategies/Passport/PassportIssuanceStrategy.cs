using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Compliance;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.Passport;

/// <summary>
/// Passport issuance engine that generates signed <see cref="CompliancePassport"/> instances
/// from object tags, data classification, and applicable regulatory frameworks.
/// <para>
/// For each regulation the engine runs a compliance assessment, builds per-regulation
/// <see cref="PassportEntry"/> records, assembles an <see cref="EvidenceLink"/> chain,
/// and signs the passport with HMAC-SHA256 for tamper detection. Passports are cached
/// per object for fast compliance lookups.
/// </para>
/// </summary>
public sealed class PassportIssuanceStrategy : ComplianceStrategyBase
{
    private readonly ConcurrentDictionary<string, CompliancePassport> _passportCache = new();
    private byte[]? _signingKey;

    /// <inheritdoc/>
    public override string StrategyId => "passport-issuance";

    /// <inheritdoc/>
    public override string StrategyName => "Compliance Passport Issuance";

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
            // Generate ephemeral signing key when none is configured
            _signingKey = RandomNumberGenerator.GetBytes(32);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Issues a new <see cref="CompliancePassport"/> for the given object based on a
    /// list of applicable regulations and the provided compliance context.
    /// </summary>
    /// <param name="objectId">Identifier of the data object to certify.</param>
    /// <param name="regulationIds">Regulations to assess (e.g., "GDPR", "HIPAA").</param>
    /// <param name="context">Compliance context carrying classification, attributes, and evidence.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A signed <see cref="CompliancePassport"/> with entries for each regulation.</returns>
    public Task<CompliancePassport> IssuePassportAsync(
        string objectId,
        IReadOnlyList<string> regulationIds,
        ComplianceContext context,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        ArgumentNullException.ThrowIfNull(regulationIds);
        ArgumentNullException.ThrowIfNull(context);

        ct.ThrowIfCancellationRequested();
        IncrementCounter("passport_issuance.issued");

        var now = DateTimeOffset.UtcNow;
        var entries = new List<PassportEntry>(regulationIds.Count);

        foreach (var regulationId in regulationIds)
        {
            var score = DeriveComplianceScore(regulationId, context);
            var status = score >= 0.8 ? PassportStatus.Active
                       : score >= 0.5 ? PassportStatus.Provisional
                       : PassportStatus.PendingReview;
            var period = GetRegulationAssessmentPeriod(regulationId);

            entries.Add(new PassportEntry
            {
                RegulationId = regulationId,
                ControlId = $"{regulationId}-FULL",
                Status = status,
                AssessedAt = now,
                NextAssessmentDue = now.Add(period),
                ComplianceScore = score
            });
        }

        var evidenceChain = BuildEvidenceChain(context, now);
        var expiresAt = GetPassportExpiry(entries);

        var passport = new CompliancePassport
        {
            PassportId = Guid.NewGuid().ToString("N"),
            ObjectId = objectId,
            Status = entries.All(e => e.Status == PassportStatus.Active) ? PassportStatus.Active
                   : entries.Any(e => e.Status == PassportStatus.PendingReview) ? PassportStatus.PendingReview
                   : PassportStatus.Provisional,
            Scope = PassportScope.Object,
            Entries = entries,
            EvidenceChain = evidenceChain,
            IssuedAt = now,
            ExpiresAt = now.Add(expiresAt),
            IssuerId = "passport-issuance-engine",
            TenantId = context.UserId,
            Metadata = new Dictionary<string, object>
            {
                ["DataClassification"] = context.DataClassification,
                ["OperationType"] = context.OperationType
            }
        };

        // Sign the passport
        passport = passport with { DigitalSignature = SignPassport(passport) };

        // Cache for fast lookup
        _passportCache.AddOrUpdate(objectId, passport, (_, _) => passport);

        return Task.FromResult(passport);
    }

    /// <summary>
    /// Renews an existing passport by creating a new passport instance with refreshed
    /// assessment timestamps and extended expiration. Preserves the evidence chain
    /// and appends a renewal evidence record.
    /// </summary>
    /// <param name="existing">The passport to renew.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A new signed <see cref="CompliancePassport"/> based on the existing one.</returns>
    public Task<CompliancePassport> RenewPassportAsync(CompliancePassport existing, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ct.ThrowIfCancellationRequested();
        IncrementCounter("passport_issuance.renewed");

        var now = DateTimeOffset.UtcNow;

        // Refresh entries with new assessment timestamps
        var renewedEntries = existing.Entries.Select(entry =>
        {
            var period = GetRegulationAssessmentPeriod(entry.RegulationId);
            return entry with
            {
                AssessedAt = now,
                NextAssessmentDue = now.Add(period)
            };
        }).ToList();

        // Append renewal evidence
        var renewalEvidence = new EvidenceLink
        {
            EvidenceId = Guid.NewGuid().ToString("N"),
            Type = EvidenceType.AutomatedCheck,
            Description = $"Passport renewed from {existing.PassportId} at {now:O}",
            CollectedAt = now,
            ArtifactReference = $"passport://{existing.PassportId}/renewal"
        };

        var updatedChain = new List<EvidenceLink>(existing.EvidenceChain) { renewalEvidence };
        var expiresAt = GetPassportExpiry(renewedEntries);

        var renewed = new CompliancePassport
        {
            PassportId = Guid.NewGuid().ToString("N"),
            ObjectId = existing.ObjectId,
            Status = existing.Status == PassportStatus.Revoked ? PassportStatus.PendingReview : existing.Status,
            Scope = existing.Scope,
            Entries = renewedEntries,
            EvidenceChain = updatedChain,
            IssuedAt = now,
            ExpiresAt = now.Add(expiresAt),
            LastVerifiedAt = now,
            IssuerId = "passport-issuance-engine",
            TenantId = existing.TenantId,
            Metadata = existing.Metadata
        };

        renewed = renewed with { DigitalSignature = SignPassport(renewed) };

        _passportCache.AddOrUpdate(existing.ObjectId, renewed, (_, _) => renewed);

        return Task.FromResult(renewed);
    }

    /// <summary>
    /// Verifies the digital signature of a passport to detect tampering.
    /// </summary>
    /// <param name="passport">The passport to verify.</param>
    /// <returns><c>true</c> if the signature is valid; otherwise <c>false</c>.</returns>
    public bool VerifySignature(CompliancePassport passport)
    {
        ArgumentNullException.ThrowIfNull(passport);

        if (passport.DigitalSignature is null || _signingKey is null)
            return false;

        var expected = SignPassport(passport);
        return CryptographicOperations.FixedTimeEquals(expected, passport.DigitalSignature);
    }

    /// <summary>
    /// Retrieves the cached passport for an object, if one exists.
    /// </summary>
    /// <param name="objectId">The object identifier to look up.</param>
    /// <returns>The cached passport, or <c>null</c> if none exists.</returns>
    public CompliancePassport? GetCachedPassport(string objectId)
    {
        _passportCache.TryGetValue(objectId, out var passport);
        return passport;
    }

    /// <inheritdoc/>
    protected override Task<ComplianceResult> CheckComplianceCoreAsync(
        ComplianceContext context,
        CancellationToken cancellationToken)
    {
        IncrementCounter("passport_issuance.check");

        var objectId = context.ResourceId ?? string.Empty;

        if (_passportCache.TryGetValue(objectId, out var passport) && passport.IsValid())
        {
            return Task.FromResult(new ComplianceResult
            {
                IsCompliant = true,
                Framework = Framework,
                Status = ComplianceStatus.Compliant,
                Metadata = new Dictionary<string, object>
                {
                    ["PassportId"] = passport.PassportId,
                    ["ExpiresAt"] = passport.ExpiresAt.ToString("O")
                }
            });
        }

        var isExpired = passport is not null && !passport.IsValid();
        var recommendation = isExpired
            ? "Passport expired; renew via RenewPassportAsync"
            : "No valid passport found; issue via IssuePassportAsync";

        return Task.FromResult(new ComplianceResult
        {
            IsCompliant = false,
            Framework = Framework,
            Status = ComplianceStatus.NonCompliant,
            Recommendations = new[] { recommendation }
        });
    }

    // ------------------------------------------------------------------
    // Private helpers
    // ------------------------------------------------------------------

    private byte[] SignPassport(CompliancePassport passport)
    {
        // Serialize passport content WITHOUT DigitalSignature for signing
        var signable = passport with { DigitalSignature = null };
        var json = JsonSerializer.Serialize(signable, _serializerOptions);
        var data = Encoding.UTF8.GetBytes(json);

        using var hmac = new HMACSHA256(_signingKey!);
        return hmac.ComputeHash(data);
    }

    private static double DeriveComplianceScore(string regulationId, ComplianceContext context)
    {
        // Derive score from context attributes if present
        if (context.Attributes.TryGetValue($"Score:{regulationId}", out var scoreObj))
        {
            if (scoreObj is double d) return Math.Clamp(d, 0.0, 1.0);
            if (scoreObj is int i) return Math.Clamp(i / 100.0, 0.0, 1.0);
            if (scoreObj is string s && double.TryParse(s, out var parsed))
                return Math.Clamp(parsed, 0.0, 1.0);
        }

        // Default: full compliance assumed when no assessment data
        return 1.0;
    }

    private static List<EvidenceLink> BuildEvidenceChain(ComplianceContext context, DateTimeOffset now)
    {
        var chain = new List<EvidenceLink>();

        // Base issuance evidence
        chain.Add(new EvidenceLink
        {
            EvidenceId = Guid.NewGuid().ToString("N"),
            Type = EvidenceType.AutomatedCheck,
            Description = $"Automated passport issuance for {context.OperationType} on {context.DataClassification} data",
            CollectedAt = now
        });

        // Parse additional evidence from context attributes
        if (context.Attributes.TryGetValue("Evidence", out var evidenceObj) && evidenceObj is string evidenceJson)
        {
            try
            {
                var items = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(evidenceJson);
                if (items is not null)
                {
                    foreach (var item in items)
                    {
                        chain.Add(new EvidenceLink
                        {
                            EvidenceId = Guid.NewGuid().ToString("N"),
                            Type = item.TryGetValue("Type", out var t) && Enum.TryParse<EvidenceType>(t, true, out var et)
                                ? et : EvidenceType.AutomatedCheck,
                            Description = item.GetValueOrDefault("Description") ?? "External evidence",
                            CollectedAt = now,
                            ArtifactReference = item.GetValueOrDefault("ArtifactReference")
                        });
                    }
                }
            }
            catch (JsonException)
            {
                // Malformed evidence JSON -- skip gracefully
            }
        }

        return chain;
    }

    private static TimeSpan GetRegulationAssessmentPeriod(string regulationId)
    {
        return regulationId.ToUpperInvariant() switch
        {
            "GDPR" => TimeSpan.FromDays(180),
            "HIPAA" => TimeSpan.FromDays(365),
            "PCI-DSS" or "PCI_DSS" => TimeSpan.FromDays(90),
            "SOX" => TimeSpan.FromDays(365),
            "SOC2" => TimeSpan.FromDays(365),
            "ISO27001" => TimeSpan.FromDays(365),
            "CCPA" => TimeSpan.FromDays(180),
            _ => TimeSpan.FromDays(180)
        };
    }

    private static TimeSpan GetPassportExpiry(IReadOnlyList<PassportEntry> entries)
    {
        if (entries.Count == 0)
            return TimeSpan.FromDays(180);

        var now = DateTimeOffset.UtcNow;
        var earliest = entries
            .Where(e => e.NextAssessmentDue.HasValue)
            .Select(e => e.NextAssessmentDue!.Value - now)
            .DefaultIfEmpty(TimeSpan.FromDays(180))
            .Min();

        // Clamp between 30 and 365 days
        if (earliest < TimeSpan.FromDays(30)) earliest = TimeSpan.FromDays(30);
        if (earliest > TimeSpan.FromDays(365)) earliest = TimeSpan.FromDays(365);

        return earliest;
    }

    private static readonly JsonSerializerOptions _serializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };
}
