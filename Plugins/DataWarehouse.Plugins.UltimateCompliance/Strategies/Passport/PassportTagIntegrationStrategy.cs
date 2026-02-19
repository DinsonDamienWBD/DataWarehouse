using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Compliance;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.Passport;

/// <summary>
/// Bridges <see cref="CompliancePassport"/> and the Universal Tag System (Phase 55).
/// <para>
/// Compliance passports ARE tag sets. This strategy converts passports to/from a flat
/// tag dictionary keyed under the <c>compliance.passport.*</c> namespace, caches tags
/// per object, and provides query helpers for tag-based compliance object discovery.
/// The internal cache is production-ready; Phase 55 adds the persistence/indexing layer.
/// </para>
/// </summary>
public sealed class PassportTagIntegrationStrategy : ComplianceStrategyBase
{
    // ==================================================================================
    // Constants
    // ==================================================================================

    /// <summary>Tag namespace prefix for all passport-related tags.</summary>
    public const string TagPrefix = "compliance.passport.";

    private const string TagId = TagPrefix + "id";
    private const string TagStatus = TagPrefix + "status";
    private const string TagScope = TagPrefix + "scope";
    private const string TagIssuedAt = TagPrefix + "issued_at";
    private const string TagExpiresAt = TagPrefix + "expires_at";
    private const string TagIssuer = TagPrefix + "issuer";
    private const string TagObjectId = TagPrefix + "object_id";
    private const string TagValid = TagPrefix + "valid";
    private const string TagEvidenceCount = TagPrefix + "evidence_count";
    private const string TagSignaturePresent = TagPrefix + "signature_present";
    private const string TagTenant = TagPrefix + "tenant_id";
    private const string TagRegulationPrefix = TagPrefix + "regulation.";

    // ==================================================================================
    // State
    // ==================================================================================

    /// <summary>
    /// Per-object tag cache. Key = objectId, Value = tag dictionary.
    /// In production with Phase 55, this becomes the read-through cache backed by tag storage.
    /// </summary>
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, object>> _objectTags = new();

    // ==================================================================================
    // Strategy identity
    // ==================================================================================

    /// <inheritdoc/>
    public override string StrategyId => "passport-tag-integration";

    /// <inheritdoc/>
    public override string StrategyName => "Passport Tag Integration";

    /// <inheritdoc/>
    public override string Framework => "CompliancePassport";

    // ==================================================================================
    // Passport <-> Tag conversion
    // ==================================================================================

    /// <summary>
    /// Converts a <see cref="CompliancePassport"/> to a flat tag dictionary.
    /// All tags are prefixed with <c>compliance.passport.</c>.
    /// </summary>
    /// <param name="passport">The passport to convert.</param>
    /// <returns>A read-only dictionary of tag key-value pairs representing the passport.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="passport"/> is null.</exception>
    public IReadOnlyDictionary<string, object> PassportToTags(CompliancePassport passport)
    {
        ArgumentNullException.ThrowIfNull(passport);
        IncrementCounter("passport_tag.to_tags");

        var tags = new Dictionary<string, object>
        {
            [TagId] = passport.PassportId,
            [TagStatus] = passport.Status.ToString(),
            [TagScope] = passport.Scope.ToString(),
            [TagIssuedAt] = passport.IssuedAt.ToString("O"),
            [TagExpiresAt] = passport.ExpiresAt.ToString("O"),
            [TagIssuer] = passport.IssuerId,
            [TagObjectId] = passport.ObjectId,
            [TagValid] = passport.IsValid().ToString(),
            [TagEvidenceCount] = passport.EvidenceChain.Count.ToString(CultureInfo.InvariantCulture),
            [TagSignaturePresent] = (passport.DigitalSignature != null).ToString()
        };

        if (passport.TenantId != null)
        {
            tags[TagTenant] = passport.TenantId;
        }

        // Per-regulation tags
        foreach (var entry in passport.Entries)
        {
            var regKey = NormalizeRegulationKey(entry.RegulationId);
            tags[$"{TagRegulationPrefix}{regKey}.status"] = entry.Status.ToString();
            tags[$"{TagRegulationPrefix}{regKey}.score"] = entry.ComplianceScore.ToString("F4", CultureInfo.InvariantCulture);
            tags[$"{TagRegulationPrefix}{regKey}.assessed_at"] = entry.AssessedAt.ToString("O");
            tags[$"{TagRegulationPrefix}{regKey}.control_id"] = entry.ControlId;
        }

        return tags;
    }

    /// <summary>
    /// Reconstructs a <see cref="CompliancePassport"/> from a tag dictionary.
    /// Parses all <c>compliance.passport.*</c> tags and reconstructs the passport entries.
    /// </summary>
    /// <param name="tags">The tag dictionary, typically from <see cref="PassportToTags"/>.</param>
    /// <returns>The reconstructed passport, or <c>null</c> if required tags are missing.</returns>
    public CompliancePassport? TagsToPassport(IReadOnlyDictionary<string, object> tags)
    {
        if (tags == null || tags.Count == 0)
            return null;

        IncrementCounter("passport_tag.from_tags");

        // Extract required scalar fields
        if (!TryGetTagString(tags, TagId, out var passportId) ||
            !TryGetTagString(tags, TagStatus, out var statusStr) ||
            !TryGetTagString(tags, TagScope, out var scopeStr) ||
            !TryGetTagString(tags, TagIssuedAt, out var issuedAtStr) ||
            !TryGetTagString(tags, TagExpiresAt, out var expiresAtStr) ||
            !TryGetTagString(tags, TagIssuer, out var issuerId) ||
            !TryGetTagString(tags, TagObjectId, out var objectId))
        {
            return null;
        }

        if (!Enum.TryParse<PassportStatus>(statusStr, ignoreCase: true, out var status) ||
            !Enum.TryParse<PassportScope>(scopeStr, ignoreCase: true, out var scope) ||
            !DateTimeOffset.TryParse(issuedAtStr, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var issuedAt) ||
            !DateTimeOffset.TryParse(expiresAtStr, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var expiresAt))
        {
            return null;
        }

        // Reconstruct regulation entries from regulation.* tags
        var entries = ReconstructEntries(tags);

        // Optional tenant
        TryGetTagString(tags, TagTenant, out var tenantId);

        return new CompliancePassport
        {
            PassportId = passportId!,
            ObjectId = objectId!,
            Status = status,
            Scope = scope,
            Entries = entries,
            EvidenceChain = Array.Empty<EvidenceLink>(), // Evidence is not round-tripped via tags
            IssuedAt = issuedAt,
            ExpiresAt = expiresAt,
            IssuerId = issuerId!,
            TenantId = tenantId
        };
    }

    // ==================================================================================
    // Per-object tag cache
    // ==================================================================================

    /// <summary>
    /// Retrieves passport tags for an object from the internal cache.
    /// </summary>
    /// <param name="objectId">The object identifier.</param>
    /// <returns>A read-only dictionary of passport tags, or empty if no passport exists.</returns>
    public IReadOnlyDictionary<string, object> GetPassportTagsForObject(string objectId)
    {
        ArgumentNullException.ThrowIfNull(objectId);
        IncrementCounter("passport_tag.get_tags");

        if (_objectTags.TryGetValue(objectId, out var tags))
        {
            return new Dictionary<string, object>(tags);
        }

        return new Dictionary<string, object>();
    }

    /// <summary>
    /// Converts a passport to tags and stores them in the internal cache.
    /// This is the write-through cache; actual persistence comes from Phase 55's tag storage.
    /// </summary>
    /// <param name="objectId">The object identifier to tag.</param>
    /// <param name="passport">The compliance passport to store as tags.</param>
    public void SetPassportTagsForObject(string objectId, CompliancePassport passport)
    {
        ArgumentNullException.ThrowIfNull(objectId);
        ArgumentNullException.ThrowIfNull(passport);
        IncrementCounter("passport_tag.set_tags");

        var tagDict = new ConcurrentDictionary<string, object>(PassportToTags(passport));
        _objectTags.AddOrUpdate(objectId, tagDict, (_, _) => tagDict);
    }

    /// <summary>
    /// Removes all passport tags for the specified object from the cache.
    /// </summary>
    /// <param name="objectId">The object identifier.</param>
    /// <returns><c>true</c> if tags were removed; <c>false</c> if no tags existed.</returns>
    public bool RemovePassportTagsForObject(string objectId)
    {
        ArgumentNullException.ThrowIfNull(objectId);
        IncrementCounter("passport_tag.remove_tags");
        return _objectTags.TryRemove(objectId, out _);
    }

    // ==================================================================================
    // Query helpers
    // ==================================================================================

    /// <summary>
    /// Finds all object IDs that have a passport entry for the specified regulation.
    /// </summary>
    /// <param name="regulationId">The regulation identifier (e.g., "GDPR", "HIPAA").</param>
    /// <returns>List of object IDs with matching regulation entries.</returns>
    public IReadOnlyList<string> FindObjectsByRegulation(string regulationId)
    {
        ArgumentNullException.ThrowIfNull(regulationId);
        IncrementCounter("passport_tag.query_regulation");

        var regKey = NormalizeRegulationKey(regulationId);
        var statusTag = $"{TagRegulationPrefix}{regKey}.status";

        return _objectTags
            .Where(kvp => kvp.Value.ContainsKey(statusTag))
            .Select(kvp => kvp.Key)
            .ToList();
    }

    /// <summary>
    /// Finds all object IDs whose passport has the specified status.
    /// </summary>
    /// <param name="status">The passport status to match.</param>
    /// <returns>List of matching object IDs.</returns>
    public IReadOnlyList<string> FindObjectsByStatus(PassportStatus status)
    {
        IncrementCounter("passport_tag.query_status");
        var statusStr = status.ToString();

        return _objectTags
            .Where(kvp => kvp.Value.TryGetValue(TagStatus, out var val) &&
                          string.Equals(val?.ToString(), statusStr, StringComparison.OrdinalIgnoreCase))
            .Select(kvp => kvp.Key)
            .ToList();
    }

    /// <summary>
    /// Finds all object IDs whose passport expires within the specified time window.
    /// </summary>
    /// <param name="within">The time window from now to check for expiration.</param>
    /// <returns>List of object IDs with passports expiring soon.</returns>
    public IReadOnlyList<string> FindObjectsExpiringSoon(TimeSpan within)
    {
        IncrementCounter("passport_tag.query_expiring");
        var cutoff = DateTimeOffset.UtcNow.Add(within);

        return _objectTags
            .Where(kvp =>
            {
                if (!kvp.Value.TryGetValue(TagExpiresAt, out var val))
                    return false;

                return DateTimeOffset.TryParse(
                    val?.ToString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var expiresAt) && expiresAt <= cutoff;
            })
            .Select(kvp => kvp.Key)
            .ToList();
    }

    /// <summary>
    /// Finds all object IDs that have at least one regulation entry with a compliance score below 0.8.
    /// </summary>
    /// <returns>List of non-compliant object IDs.</returns>
    public IReadOnlyList<string> FindNonCompliantObjects()
    {
        IncrementCounter("passport_tag.query_noncompliant");
        const double threshold = 0.8;

        return _objectTags
            .Where(kvp => HasLowScoreEntry(kvp.Value, threshold))
            .Select(kvp => kvp.Key)
            .ToList();
    }

    /// <summary>
    /// Returns the total number of objects with cached passport tags.
    /// </summary>
    public int CachedObjectCount => _objectTags.Count;

    // ==================================================================================
    // ComplianceStrategyBase override
    // ==================================================================================

    /// <inheritdoc/>
    protected override Task<ComplianceResult> CheckComplianceCoreAsync(
        ComplianceContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IncrementCounter("passport_tag.check");

        var objectId = context.ResourceId;
        if (string.IsNullOrWhiteSpace(objectId))
        {
            return Task.FromResult(new ComplianceResult
            {
                IsCompliant = false,
                Framework = Framework,
                Status = ComplianceStatus.NonCompliant,
                Violations = new[]
                {
                    new ComplianceViolation
                    {
                        Code = "PTI-001",
                        Description = "No object ID specified; cannot check passport tags",
                        Severity = ViolationSeverity.Medium
                    }
                }
            });
        }

        if (!_objectTags.TryGetValue(objectId, out var tags) || tags.Count == 0)
        {
            return Task.FromResult(new ComplianceResult
            {
                IsCompliant = false,
                Framework = Framework,
                Status = ComplianceStatus.NonCompliant,
                Violations = new[]
                {
                    new ComplianceViolation
                    {
                        Code = "PTI-002",
                        Description = $"No passport tags found for object '{objectId}'",
                        Severity = ViolationSeverity.High,
                        AffectedResource = objectId,
                        Remediation = "Issue a compliance passport for this object"
                    }
                }
            });
        }

        // Check passport validity from tags
        var isValid = tags.TryGetValue(TagValid, out var validVal) &&
                      string.Equals(validVal?.ToString(), "True", StringComparison.OrdinalIgnoreCase);

        var violations = new List<ComplianceViolation>();
        var recommendations = new List<string>();

        if (!isValid)
        {
            violations.Add(new ComplianceViolation
            {
                Code = "PTI-003",
                Description = $"Passport for object '{objectId}' is not valid (expired or inactive)",
                Severity = ViolationSeverity.High,
                AffectedResource = objectId,
                Remediation = "Renew or reactivate the compliance passport"
            });
        }

        // Check if any regulation scores are below threshold
        if (HasLowScoreEntry(tags, 0.8))
        {
            recommendations.Add("One or more regulation entries have a compliance score below 0.8; review and remediate");
        }

        return Task.FromResult(new ComplianceResult
        {
            IsCompliant = violations.Count == 0,
            Framework = Framework,
            Status = violations.Count == 0
                ? ComplianceStatus.Compliant
                : ComplianceStatus.NonCompliant,
            Violations = violations,
            Recommendations = recommendations,
            Metadata = new Dictionary<string, object>
            {
                ["ObjectId"] = objectId,
                ["TagCount"] = tags.Count,
                ["PassportValid"] = isValid
            }
        });
    }

    // ==================================================================================
    // Private helpers
    // ==================================================================================

    /// <summary>
    /// Normalizes a regulation ID for use as a tag key segment (lowercase, dots replaced with dashes).
    /// </summary>
    private static string NormalizeRegulationKey(string regulationId)
        => regulationId.ToLowerInvariant().Replace('.', '-');

    /// <summary>
    /// Tries to extract a string value from a tag dictionary.
    /// </summary>
    private static bool TryGetTagString(IReadOnlyDictionary<string, object> tags, string key, out string? value)
    {
        if (tags.TryGetValue(key, out var obj) && obj != null)
        {
            value = obj.ToString();
            return !string.IsNullOrEmpty(value);
        }

        value = null;
        return false;
    }

    /// <summary>
    /// Reconstructs <see cref="PassportEntry"/> records from regulation tags.
    /// </summary>
    private static IReadOnlyList<PassportEntry> ReconstructEntries(IReadOnlyDictionary<string, object> tags)
    {
        // Find all distinct regulation keys from tags like compliance.passport.regulation.{regKey}.status
        var regKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in tags.Keys)
        {
            if (!key.StartsWith(TagRegulationPrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var suffix = key[TagRegulationPrefix.Length..];
            var dotIndex = suffix.IndexOf('.');
            if (dotIndex > 0)
            {
                regKeys.Add(suffix[..dotIndex]);
            }
        }

        var entries = new List<PassportEntry>(regKeys.Count);

        foreach (var regKey in regKeys)
        {
            var statusKey = $"{TagRegulationPrefix}{regKey}.status";
            var scoreKey = $"{TagRegulationPrefix}{regKey}.score";
            var assessedKey = $"{TagRegulationPrefix}{regKey}.assessed_at";
            var controlKey = $"{TagRegulationPrefix}{regKey}.control_id";

            TryGetTagString(tags, statusKey, out var entryStatusStr);
            TryGetTagString(tags, scoreKey, out var scoreStr);
            TryGetTagString(tags, assessedKey, out var assessedStr);
            TryGetTagString(tags, controlKey, out var controlId);

            if (!Enum.TryParse<PassportStatus>(entryStatusStr, ignoreCase: true, out var entryStatus))
                entryStatus = PassportStatus.PendingReview;

            if (!double.TryParse(scoreStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var score))
                score = 0.0;

            if (!DateTimeOffset.TryParse(assessedStr, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var assessedAt))
                assessedAt = DateTimeOffset.UtcNow;

            entries.Add(new PassportEntry
            {
                RegulationId = regKey.ToUpperInvariant(),
                ControlId = controlId ?? regKey,
                Status = entryStatus,
                ComplianceScore = score,
                AssessedAt = assessedAt
            });
        }

        return entries;
    }

    /// <summary>
    /// Checks whether any regulation entry in the tag dictionary has a score below the threshold.
    /// </summary>
    private static bool HasLowScoreEntry(IReadOnlyDictionary<string, object> tags, double threshold)
    {
        foreach (var kvp in tags)
        {
            if (!kvp.Key.EndsWith(".score", StringComparison.OrdinalIgnoreCase) ||
                !kvp.Key.StartsWith(TagRegulationPrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            if (double.TryParse(kvp.Value?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var score) &&
                score < threshold)
            {
                return true;
            }
        }

        return false;
    }
}
