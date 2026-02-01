using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Security;

namespace DataWarehouse.Plugins.MilitarySecurity;

/// <summary>
/// Production-ready Need-to-Know (NTK) enforcement plugin for military-grade security.
/// Implements compartmented information access control requiring both appropriate clearance
/// and legitimate need-to-know for job function.
/// <para>
/// Key Features:
/// - Access grants with optional time limits for temporary access requirements
/// - Purpose-based validation ensuring access aligns with approved justifications
/// - Complete audit trail for compliance review and security audits
/// - Periodic review support with configurable review intervals
/// - Information owner/security officer authorization verification
/// </para>
/// <para>
/// Reference: Executive Order 13526 Section 4.1, NIST SP 800-53 control AC-4.
/// </para>
/// </summary>
/// <remarks>
/// This plugin is thread-safe and designed for high-concurrency production environments.
/// All access decisions and modifications create immutable audit entries for compliance.
/// </remarks>
public sealed class NeedToKnowEnforcementPlugin : SecurityProviderPluginBase, INeedToKnowEnforcement
{
    #region Private Fields

    /// <summary>
    /// Thread-safe storage for access grants.
    /// Key format: "{subjectId}:{resourceId}"
    /// </summary>
    private readonly ConcurrentDictionary<string, NeedToKnowGrant> _grants = new();

    /// <summary>
    /// Thread-safe storage for audit entries by resource.
    /// Key: resourceId
    /// </summary>
    private readonly ConcurrentDictionary<string, ConcurrentBag<NeedToKnowAuditEntry>> _auditTrails = new();

    /// <summary>
    /// Authorized personnel who can grant/revoke access.
    /// Key: authorityId
    /// </summary>
    private readonly ConcurrentDictionary<string, AuthorizedAuthority> _authorities = new();

    /// <summary>
    /// Approved purposes for access validation.
    /// Key: resourceId, Value: set of approved purpose patterns
    /// </summary>
    private readonly ConcurrentDictionary<string, HashSet<string>> _approvedPurposes = new();

    /// <summary>
    /// Resources requiring periodic review.
    /// Key: resourceId
    /// </summary>
    private readonly ConcurrentDictionary<string, PeriodicReviewConfig> _periodicReviewConfigs = new();

    /// <summary>
    /// Lock object for thread-safe grant operations.
    /// </summary>
    private readonly object _grantLock = new();

    /// <summary>
    /// Default review interval for periodic reviews.
    /// </summary>
    private TimeSpan _defaultReviewInterval = TimeSpan.FromDays(90);

    /// <summary>
    /// Whether to require purpose validation for access checks.
    /// </summary>
    private bool _requirePurposeValidation = true;

    /// <summary>
    /// Whether to allow wildcard purposes.
    /// </summary>
    private bool _allowWildcardPurpose = false;

    #endregion

    #region Plugin Identity

    /// <inheritdoc />
    public override string Id => "datawarehouse.milsec.need-to-know";

    /// <inheritdoc />
    public override string Name => "Need-to-Know Enforcement";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    #endregion

    #region Configuration Methods

    /// <summary>
    /// Registers an authority who can grant or revoke need-to-know access.
    /// </summary>
    /// <param name="authorityId">Unique identifier of the authority.</param>
    /// <param name="name">Human-readable name of the authority.</param>
    /// <param name="role">Role of the authority (e.g., "InformationOwner", "SecurityOfficer").</param>
    /// <param name="authorizedResourcePatterns">Patterns for resources this authority can manage (supports wildcards).</param>
    /// <exception cref="ArgumentNullException">If any required parameter is null.</exception>
    public void RegisterAuthority(
        string authorityId,
        string name,
        AuthorityRole role,
        IEnumerable<string> authorizedResourcePatterns)
    {
        ArgumentNullException.ThrowIfNull(authorityId);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(authorizedResourcePatterns);

        _authorities[authorityId] = new AuthorizedAuthority
        {
            AuthorityId = authorityId,
            Name = name,
            Role = role,
            AuthorizedResourcePatterns = authorizedResourcePatterns.ToHashSet(),
            RegisteredAt = DateTimeOffset.UtcNow
        };

        RecordSystemAuditEntry(
            "AUTHORITY_REGISTERED",
            $"Authority '{name}' ({authorityId}) registered with role {role}",
            "SYSTEM");
    }

    /// <summary>
    /// Removes an authority from the system.
    /// </summary>
    /// <param name="authorityId">Identifier of the authority to remove.</param>
    /// <returns>True if the authority was removed; false if not found.</returns>
    public bool RemoveAuthority(string authorityId)
    {
        var removed = _authorities.TryRemove(authorityId, out var authority);

        if (removed && authority != null)
        {
            RecordSystemAuditEntry(
                "AUTHORITY_REMOVED",
                $"Authority '{authority.Name}' ({authorityId}) removed from system",
                "SYSTEM");
        }

        return removed;
    }

    /// <summary>
    /// Registers an approved purpose for a resource.
    /// </summary>
    /// <param name="resourceId">Resource identifier.</param>
    /// <param name="purposePattern">Purpose pattern to approve (can include wildcards).</param>
    public void RegisterApprovedPurpose(string resourceId, string purposePattern)
    {
        ArgumentNullException.ThrowIfNull(resourceId);
        ArgumentNullException.ThrowIfNull(purposePattern);

        _approvedPurposes.AddOrUpdate(
            resourceId,
            _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { purposePattern },
            (_, existing) =>
            {
                lock (existing)
                {
                    existing.Add(purposePattern);
                }
                return existing;
            });
    }

    /// <summary>
    /// Configures periodic review requirements for a resource.
    /// </summary>
    /// <param name="resourceId">Resource identifier.</param>
    /// <param name="reviewInterval">How often access should be reviewed.</param>
    /// <param name="requiresReauthorization">If true, access is suspended until reviewed.</param>
    public void ConfigurePeriodicReview(
        string resourceId,
        TimeSpan reviewInterval,
        bool requiresReauthorization = false)
    {
        ArgumentNullException.ThrowIfNull(resourceId);

        _periodicReviewConfigs[resourceId] = new PeriodicReviewConfig
        {
            ResourceId = resourceId,
            ReviewInterval = reviewInterval,
            RequiresReauthorization = requiresReauthorization
        };
    }

    /// <summary>
    /// Sets the default review interval for periodic reviews.
    /// </summary>
    /// <param name="interval">Default review interval.</param>
    public void SetDefaultReviewInterval(TimeSpan interval)
    {
        _defaultReviewInterval = interval;
    }

    /// <summary>
    /// Enables or disables purpose validation requirement.
    /// </summary>
    /// <param name="required">True to require purpose validation.</param>
    public void SetPurposeValidationRequired(bool required)
    {
        _requirePurposeValidation = required;
    }

    /// <summary>
    /// Enables or disables wildcard purpose acceptance.
    /// </summary>
    /// <param name="allowed">True to allow wildcard purposes.</param>
    public void SetWildcardPurposeAllowed(bool allowed)
    {
        _allowWildcardPurpose = allowed;
    }

    #endregion

    #region INeedToKnowEnforcement Implementation

    /// <inheritdoc />
    /// <remarks>
    /// This method performs the following checks:
    /// 1. Verifies an active grant exists for the subject/resource pair
    /// 2. Validates the grant has not expired
    /// 3. Checks periodic review requirements
    /// 4. Validates the stated purpose against approved purposes
    /// All access attempts are logged in the audit trail.
    /// </remarks>
    public Task<bool> HasNeedToKnowAsync(string subjectId, string resourceId, string purpose)
    {
        ArgumentNullException.ThrowIfNull(subjectId);
        ArgumentNullException.ThrowIfNull(resourceId);
        ArgumentNullException.ThrowIfNull(purpose);

        var grantKey = CreateGrantKey(subjectId, resourceId);
        var now = DateTimeOffset.UtcNow;

        // Check if grant exists
        if (!_grants.TryGetValue(grantKey, out var grant))
        {
            RecordAccessDenied(subjectId, resourceId, purpose, "No active grant found");
            return Task.FromResult(false);
        }

        // Check if grant has expired
        if (grant.ExpiresAt.HasValue && now > grant.ExpiresAt.Value)
        {
            RecordAccessDenied(subjectId, resourceId, purpose, "Grant has expired");
            return Task.FromResult(false);
        }

        // Check if grant is revoked
        if (grant.IsRevoked)
        {
            RecordAccessDenied(subjectId, resourceId, purpose, "Grant has been revoked");
            return Task.FromResult(false);
        }

        // Check periodic review requirements
        if (!CheckPeriodicReviewCompliance(grant, resourceId, now))
        {
            RecordAccessDenied(subjectId, resourceId, purpose, "Periodic review overdue - access suspended");
            return Task.FromResult(false);
        }

        // Validate purpose if required
        if (_requirePurposeValidation && !ValidatePurpose(resourceId, purpose))
        {
            RecordAccessDenied(subjectId, resourceId, purpose, "Purpose not in approved list");
            return Task.FromResult(false);
        }

        // Access granted - record audit entry
        RecordAccessGranted(subjectId, resourceId, purpose);

        // Update last access time on grant
        grant.LastAccessedAt = now;
        grant.AccessCount++;

        return Task.FromResult(true);
    }

    /// <inheritdoc />
    /// <remarks>
    /// This method:
    /// 1. Validates the granting authority is authorized
    /// 2. Creates a new access grant with optional expiration
    /// 3. Sets up periodic review tracking
    /// 4. Creates an immutable audit entry
    /// Thread-safe implementation using locks for grant creation.
    /// </remarks>
    public Task GrantNeedToKnowAsync(string subjectId, string resourceId, string grantedBy, TimeSpan? duration)
    {
        ArgumentNullException.ThrowIfNull(subjectId);
        ArgumentNullException.ThrowIfNull(resourceId);
        ArgumentNullException.ThrowIfNull(grantedBy);

        // Validate authority
        if (!IsAuthorizedToGrantAccess(grantedBy, resourceId))
        {
            throw new UnauthorizedAccessException(
                $"Authority '{grantedBy}' is not authorized to grant access to resource '{resourceId}'");
        }

        var grantKey = CreateGrantKey(subjectId, resourceId);
        var now = DateTimeOffset.UtcNow;

        lock (_grantLock)
        {
            // Check for existing active grant
            if (_grants.TryGetValue(grantKey, out var existingGrant) &&
                !existingGrant.IsRevoked &&
                (!existingGrant.ExpiresAt.HasValue || existingGrant.ExpiresAt.Value > now))
            {
                // Extend or update existing grant
                existingGrant.ExpiresAt = duration.HasValue ? now.Add(duration.Value) : null;
                existingGrant.LastModifiedAt = now;
                existingGrant.LastModifiedBy = grantedBy;

                RecordAuditEntry(
                    resourceId,
                    subjectId,
                    "GRANT_EXTENDED",
                    now,
                    grantedBy,
                    $"Grant extended. New expiration: {existingGrant.ExpiresAt?.ToString("O") ?? "Indefinite"}");

                return Task.CompletedTask;
            }

            // Create new grant
            var grant = new NeedToKnowGrant
            {
                GrantId = GenerateGrantId(),
                SubjectId = subjectId,
                ResourceId = resourceId,
                GrantedBy = grantedBy,
                GrantedAt = now,
                ExpiresAt = duration.HasValue ? now.Add(duration.Value) : null,
                NextReviewAt = now.Add(_defaultReviewInterval),
                IsRevoked = false,
                AccessCount = 0,
                LastModifiedAt = now,
                LastModifiedBy = grantedBy
            };

            // Apply periodic review config if exists
            if (_periodicReviewConfigs.TryGetValue(resourceId, out var reviewConfig))
            {
                grant.NextReviewAt = now.Add(reviewConfig.ReviewInterval);
            }

            _grants[grantKey] = grant;

            RecordAuditEntry(
                resourceId,
                subjectId,
                "GRANT",
                now,
                grantedBy,
                $"Need-to-know access granted. GrantId: {grant.GrantId}. " +
                $"Expires: {grant.ExpiresAt?.ToString("O") ?? "Indefinite"}. " +
                $"Next review: {grant.NextReviewAt:O}");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// This method:
    /// 1. Validates the revoking authority is authorized
    /// 2. Marks the grant as revoked (preserves for audit history)
    /// 3. Creates an immutable audit entry with revocation reason
    /// Revoked grants remain in storage for audit trail purposes.
    /// </remarks>
    public Task RevokeNeedToKnowAsync(string subjectId, string resourceId, string revokedBy, string reason)
    {
        ArgumentNullException.ThrowIfNull(subjectId);
        ArgumentNullException.ThrowIfNull(resourceId);
        ArgumentNullException.ThrowIfNull(revokedBy);
        ArgumentNullException.ThrowIfNull(reason);

        // Validate authority
        if (!IsAuthorizedToRevokeAccess(revokedBy, resourceId))
        {
            throw new UnauthorizedAccessException(
                $"Authority '{revokedBy}' is not authorized to revoke access to resource '{resourceId}'");
        }

        var grantKey = CreateGrantKey(subjectId, resourceId);
        var now = DateTimeOffset.UtcNow;

        if (!_grants.TryGetValue(grantKey, out var grant))
        {
            // No grant exists - record attempt but don't throw
            RecordAuditEntry(
                resourceId,
                subjectId,
                "REVOKE_NOT_FOUND",
                now,
                revokedBy,
                $"Attempted to revoke non-existent grant. Reason: {reason}");

            return Task.CompletedTask;
        }

        // Mark grant as revoked
        grant.IsRevoked = true;
        grant.RevokedAt = now;
        grant.RevokedBy = revokedBy;
        grant.RevocationReason = reason;
        grant.LastModifiedAt = now;
        grant.LastModifiedBy = revokedBy;

        RecordAuditEntry(
            resourceId,
            subjectId,
            "REVOKE",
            now,
            revokedBy,
            $"Need-to-know access revoked. GrantId: {grant.GrantId}. Reason: {reason}. " +
            $"Total accesses: {grant.AccessCount}. Last access: {grant.LastAccessedAt?.ToString("O") ?? "Never"}");

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Returns all audit entries for a resource in chronological order.
    /// Includes grants, revocations, access attempts (both successful and denied),
    /// and periodic review events.
    /// </remarks>
    public Task<IReadOnlyList<NeedToKnowAuditEntry>> GetAuditTrailAsync(string resourceId)
    {
        ArgumentNullException.ThrowIfNull(resourceId);

        if (_auditTrails.TryGetValue(resourceId, out var entries))
        {
            var sortedEntries = entries
                .OrderBy(e => e.Timestamp)
                .ToList();

            return Task.FromResult<IReadOnlyList<NeedToKnowAuditEntry>>(sortedEntries);
        }

        return Task.FromResult<IReadOnlyList<NeedToKnowAuditEntry>>(Array.Empty<NeedToKnowAuditEntry>());
    }

    #endregion

    #region Additional Public Methods

    /// <summary>
    /// Gets all active grants for a subject.
    /// </summary>
    /// <param name="subjectId">Subject identifier.</param>
    /// <returns>Read-only list of active grants.</returns>
    public IReadOnlyList<NeedToKnowGrantInfo> GetActiveGrantsForSubject(string subjectId)
    {
        ArgumentNullException.ThrowIfNull(subjectId);

        var now = DateTimeOffset.UtcNow;
        var activeGrants = _grants.Values
            .Where(g => g.SubjectId == subjectId &&
                       !g.IsRevoked &&
                       (!g.ExpiresAt.HasValue || g.ExpiresAt.Value > now))
            .Select(g => new NeedToKnowGrantInfo
            {
                GrantId = g.GrantId,
                SubjectId = g.SubjectId,
                ResourceId = g.ResourceId,
                GrantedBy = g.GrantedBy,
                GrantedAt = g.GrantedAt,
                ExpiresAt = g.ExpiresAt,
                NextReviewAt = g.NextReviewAt,
                AccessCount = g.AccessCount,
                LastAccessedAt = g.LastAccessedAt
            })
            .ToList();

        return activeGrants;
    }

    /// <summary>
    /// Gets all active grants for a resource.
    /// </summary>
    /// <param name="resourceId">Resource identifier.</param>
    /// <returns>Read-only list of active grants.</returns>
    public IReadOnlyList<NeedToKnowGrantInfo> GetActiveGrantsForResource(string resourceId)
    {
        ArgumentNullException.ThrowIfNull(resourceId);

        var now = DateTimeOffset.UtcNow;
        var activeGrants = _grants.Values
            .Where(g => g.ResourceId == resourceId &&
                       !g.IsRevoked &&
                       (!g.ExpiresAt.HasValue || g.ExpiresAt.Value > now))
            .Select(g => new NeedToKnowGrantInfo
            {
                GrantId = g.GrantId,
                SubjectId = g.SubjectId,
                ResourceId = g.ResourceId,
                GrantedBy = g.GrantedBy,
                GrantedAt = g.GrantedAt,
                ExpiresAt = g.ExpiresAt,
                NextReviewAt = g.NextReviewAt,
                AccessCount = g.AccessCount,
                LastAccessedAt = g.LastAccessedAt
            })
            .ToList();

        return activeGrants;
    }

    /// <summary>
    /// Gets grants that are due for periodic review.
    /// </summary>
    /// <returns>Read-only list of grants requiring review.</returns>
    public IReadOnlyList<NeedToKnowGrantInfo> GetGrantsRequiringReview()
    {
        var now = DateTimeOffset.UtcNow;
        var grantsNeedingReview = _grants.Values
            .Where(g => !g.IsRevoked &&
                       (!g.ExpiresAt.HasValue || g.ExpiresAt.Value > now) &&
                       g.NextReviewAt <= now)
            .Select(g => new NeedToKnowGrantInfo
            {
                GrantId = g.GrantId,
                SubjectId = g.SubjectId,
                ResourceId = g.ResourceId,
                GrantedBy = g.GrantedBy,
                GrantedAt = g.GrantedAt,
                ExpiresAt = g.ExpiresAt,
                NextReviewAt = g.NextReviewAt,
                AccessCount = g.AccessCount,
                LastAccessedAt = g.LastAccessedAt
            })
            .ToList();

        return grantsNeedingReview;
    }

    /// <summary>
    /// Completes a periodic review for a grant, extending its review date.
    /// </summary>
    /// <param name="subjectId">Subject identifier.</param>
    /// <param name="resourceId">Resource identifier.</param>
    /// <param name="reviewedBy">Authority performing the review.</param>
    /// <param name="approved">Whether access should continue.</param>
    /// <param name="notes">Review notes.</param>
    /// <returns>True if review was recorded; false if grant not found.</returns>
    public bool CompletePeriodicReview(
        string subjectId,
        string resourceId,
        string reviewedBy,
        bool approved,
        string? notes = null)
    {
        ArgumentNullException.ThrowIfNull(subjectId);
        ArgumentNullException.ThrowIfNull(resourceId);
        ArgumentNullException.ThrowIfNull(reviewedBy);

        var grantKey = CreateGrantKey(subjectId, resourceId);
        var now = DateTimeOffset.UtcNow;

        if (!_grants.TryGetValue(grantKey, out var grant))
        {
            return false;
        }

        if (approved)
        {
            // Extend review date
            var reviewInterval = _periodicReviewConfigs.TryGetValue(resourceId, out var config)
                ? config.ReviewInterval
                : _defaultReviewInterval;

            grant.NextReviewAt = now.Add(reviewInterval);
            grant.LastReviewAt = now;
            grant.LastReviewedBy = reviewedBy;
            grant.LastModifiedAt = now;
            grant.LastModifiedBy = reviewedBy;

            RecordAuditEntry(
                resourceId,
                subjectId,
                "REVIEW_APPROVED",
                now,
                reviewedBy,
                $"Periodic review completed. Access approved. Next review: {grant.NextReviewAt:O}. Notes: {notes ?? "None"}");
        }
        else
        {
            // Revoke on review denial
            grant.IsRevoked = true;
            grant.RevokedAt = now;
            grant.RevokedBy = reviewedBy;
            grant.RevocationReason = $"Periodic review denied. Notes: {notes ?? "None"}";
            grant.LastModifiedAt = now;
            grant.LastModifiedBy = reviewedBy;

            RecordAuditEntry(
                resourceId,
                subjectId,
                "REVIEW_DENIED",
                now,
                reviewedBy,
                $"Periodic review completed. Access denied and revoked. Notes: {notes ?? "None"}");
        }

        return true;
    }

    /// <summary>
    /// Gets statistics about the need-to-know system.
    /// </summary>
    /// <returns>Statistics object.</returns>
    public NeedToKnowStatistics GetStatistics()
    {
        var now = DateTimeOffset.UtcNow;
        var allGrants = _grants.Values.ToList();

        return new NeedToKnowStatistics
        {
            TotalGrants = allGrants.Count,
            ActiveGrants = allGrants.Count(g => !g.IsRevoked &&
                (!g.ExpiresAt.HasValue || g.ExpiresAt.Value > now)),
            RevokedGrants = allGrants.Count(g => g.IsRevoked),
            ExpiredGrants = allGrants.Count(g => !g.IsRevoked &&
                g.ExpiresAt.HasValue && g.ExpiresAt.Value <= now),
            GrantsPendingReview = allGrants.Count(g => !g.IsRevoked &&
                (!g.ExpiresAt.HasValue || g.ExpiresAt.Value > now) &&
                g.NextReviewAt <= now),
            TotalAuthorities = _authorities.Count,
            TotalAuditEntries = _auditTrails.Values.Sum(bag => bag.Count),
            TotalAccessAttempts = allGrants.Sum(g => g.AccessCount)
        };
    }

    #endregion

    #region Private Helper Methods

    /// <summary>
    /// Creates a composite key for grant storage.
    /// </summary>
    private static string CreateGrantKey(string subjectId, string resourceId)
    {
        return $"{subjectId}:{resourceId}";
    }

    /// <summary>
    /// Generates a unique grant ID.
    /// </summary>
    private static string GenerateGrantId()
    {
        return $"NTK-{Guid.NewGuid():N}";
    }

    /// <summary>
    /// Validates if an authority is authorized to grant access to a resource.
    /// </summary>
    private bool IsAuthorizedToGrantAccess(string authorityId, string resourceId)
    {
        if (!_authorities.TryGetValue(authorityId, out var authority))
        {
            return false;
        }

        // Check role allows granting
        if (authority.Role != AuthorityRole.InformationOwner &&
            authority.Role != AuthorityRole.SecurityOfficer &&
            authority.Role != AuthorityRole.DelegatedAuthority)
        {
            return false;
        }

        // Check resource pattern authorization
        return IsResourceAuthorized(authority.AuthorizedResourcePatterns, resourceId);
    }

    /// <summary>
    /// Validates if an authority is authorized to revoke access to a resource.
    /// </summary>
    private bool IsAuthorizedToRevokeAccess(string authorityId, string resourceId)
    {
        if (!_authorities.TryGetValue(authorityId, out var authority))
        {
            return false;
        }

        // Security officers can always revoke
        if (authority.Role == AuthorityRole.SecurityOfficer)
        {
            return true;
        }

        // Check role allows revoking
        if (authority.Role != AuthorityRole.InformationOwner &&
            authority.Role != AuthorityRole.DelegatedAuthority)
        {
            return false;
        }

        // Check resource pattern authorization
        return IsResourceAuthorized(authority.AuthorizedResourcePatterns, resourceId);
    }

    /// <summary>
    /// Checks if a resource matches any of the authorized patterns.
    /// </summary>
    private static bool IsResourceAuthorized(HashSet<string> patterns, string resourceId)
    {
        foreach (var pattern in patterns)
        {
            if (pattern == "*")
            {
                return true;
            }

            if (pattern.EndsWith("*"))
            {
                var prefix = pattern[..^1];
                if (resourceId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            else if (string.Equals(pattern, resourceId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Validates the stated purpose against approved purposes for the resource.
    /// </summary>
    private bool ValidatePurpose(string resourceId, string purpose)
    {
        // If wildcard is allowed and purpose is wildcard, accept
        if (_allowWildcardPurpose && purpose == "*")
        {
            return true;
        }

        // If no approved purposes registered, allow any (unless purpose validation is strict)
        if (!_approvedPurposes.TryGetValue(resourceId, out var approvedPurposes))
        {
            return !_requirePurposeValidation;
        }

        lock (approvedPurposes)
        {
            foreach (var approved in approvedPurposes)
            {
                if (approved == "*")
                {
                    return true;
                }

                if (approved.EndsWith("*"))
                {
                    var prefix = approved[..^1];
                    if (purpose.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                else if (string.Equals(approved, purpose, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Checks if a grant complies with periodic review requirements.
    /// </summary>
    private bool CheckPeriodicReviewCompliance(NeedToKnowGrant grant, string resourceId, DateTimeOffset now)
    {
        if (!_periodicReviewConfigs.TryGetValue(resourceId, out var config))
        {
            // No strict periodic review configured - just track in grant
            return true;
        }

        // If requires reauthorization and review is overdue, deny access
        if (config.RequiresReauthorization && grant.NextReviewAt <= now)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Records an audit entry for a resource.
    /// </summary>
    private void RecordAuditEntry(
        string resourceId,
        string subjectId,
        string action,
        DateTimeOffset timestamp,
        string performedBy,
        string? reason)
    {
        var entry = new NeedToKnowAuditEntry(
            subjectId,
            action,
            timestamp,
            performedBy,
            reason);

        _auditTrails.AddOrUpdate(
            resourceId,
            _ => new ConcurrentBag<NeedToKnowAuditEntry> { entry },
            (_, bag) =>
            {
                bag.Add(entry);
                return bag;
            });
    }

    /// <summary>
    /// Records an access granted event.
    /// </summary>
    private void RecordAccessGranted(string subjectId, string resourceId, string purpose)
    {
        RecordAuditEntry(
            resourceId,
            subjectId,
            "ACCESS",
            DateTimeOffset.UtcNow,
            subjectId,
            $"Access granted for purpose: {purpose}");
    }

    /// <summary>
    /// Records an access denied event.
    /// </summary>
    private void RecordAccessDenied(string subjectId, string resourceId, string purpose, string reason)
    {
        RecordAuditEntry(
            resourceId,
            subjectId,
            "DENY",
            DateTimeOffset.UtcNow,
            subjectId,
            $"Access denied. Purpose: {purpose}. Reason: {reason}");
    }

    /// <summary>
    /// Records a system-level audit entry.
    /// </summary>
    private void RecordSystemAuditEntry(string action, string details, string performedBy)
    {
        // Record to a special system audit resource
        RecordAuditEntry(
            "_SYSTEM",
            "_SYSTEM",
            action,
            DateTimeOffset.UtcNow,
            performedBy,
            details);
    }

    #endregion

    #region Plugin Lifecycle

    /// <inheritdoc />
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["SecurityType"] = "NeedToKnow";
        metadata["SupportsACL"] = true;
        metadata["SupportsAudit"] = true;
        metadata["SupportsPeriodicReview"] = true;
        metadata["ActiveGrants"] = _grants.Count(g => !g.Value.IsRevoked);
        metadata["TotalAuthorities"] = _authorities.Count;
        return metadata;
    }

    #endregion
}

#region Supporting Types

/// <summary>
/// Role of an authority in the need-to-know system.
/// </summary>
public enum AuthorityRole
{
    /// <summary>
    /// Owner of the information with full grant/revoke authority.
    /// </summary>
    InformationOwner,

    /// <summary>
    /// Security officer with elevated revocation authority.
    /// </summary>
    SecurityOfficer,

    /// <summary>
    /// Authority delegated by information owner or security officer.
    /// </summary>
    DelegatedAuthority,

    /// <summary>
    /// Read-only access to audit trails (cannot grant/revoke).
    /// </summary>
    Auditor
}

/// <summary>
/// Internal representation of an authorized authority.
/// </summary>
internal sealed class AuthorizedAuthority
{
    public string AuthorityId { get; set; } = "";
    public string Name { get; set; } = "";
    public AuthorityRole Role { get; set; }
    public HashSet<string> AuthorizedResourcePatterns { get; set; } = new();
    public DateTimeOffset RegisteredAt { get; set; }
}

/// <summary>
/// Internal representation of a need-to-know grant.
/// </summary>
internal sealed class NeedToKnowGrant
{
    public string GrantId { get; set; } = "";
    public string SubjectId { get; set; } = "";
    public string ResourceId { get; set; } = "";
    public string GrantedBy { get; set; } = "";
    public DateTimeOffset GrantedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset NextReviewAt { get; set; }
    public DateTimeOffset? LastReviewAt { get; set; }
    public string? LastReviewedBy { get; set; }
    public bool IsRevoked { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokedBy { get; set; }
    public string? RevocationReason { get; set; }
    public long AccessCount { get; set; }
    public DateTimeOffset? LastAccessedAt { get; set; }
    public DateTimeOffset LastModifiedAt { get; set; }
    public string LastModifiedBy { get; set; } = "";
}

/// <summary>
/// Configuration for periodic review requirements.
/// </summary>
internal sealed class PeriodicReviewConfig
{
    public string ResourceId { get; set; } = "";
    public TimeSpan ReviewInterval { get; set; }
    public bool RequiresReauthorization { get; set; }
}

/// <summary>
/// Public-facing grant information (read-only).
/// </summary>
public sealed class NeedToKnowGrantInfo
{
    /// <summary>
    /// Unique grant identifier.
    /// </summary>
    public string GrantId { get; init; } = "";

    /// <summary>
    /// Subject who was granted access.
    /// </summary>
    public string SubjectId { get; init; } = "";

    /// <summary>
    /// Resource for which access was granted.
    /// </summary>
    public string ResourceId { get; init; } = "";

    /// <summary>
    /// Authority who granted the access.
    /// </summary>
    public string GrantedBy { get; init; } = "";

    /// <summary>
    /// When the grant was created.
    /// </summary>
    public DateTimeOffset GrantedAt { get; init; }

    /// <summary>
    /// When the grant expires (null for indefinite).
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>
    /// When the next periodic review is due.
    /// </summary>
    public DateTimeOffset NextReviewAt { get; init; }

    /// <summary>
    /// Total number of access attempts using this grant.
    /// </summary>
    public long AccessCount { get; init; }

    /// <summary>
    /// When the grant was last used for access.
    /// </summary>
    public DateTimeOffset? LastAccessedAt { get; init; }
}

/// <summary>
/// Statistics about the need-to-know system.
/// </summary>
public sealed class NeedToKnowStatistics
{
    /// <summary>
    /// Total number of grants (including revoked and expired).
    /// </summary>
    public int TotalGrants { get; init; }

    /// <summary>
    /// Number of currently active grants.
    /// </summary>
    public int ActiveGrants { get; init; }

    /// <summary>
    /// Number of revoked grants.
    /// </summary>
    public int RevokedGrants { get; init; }

    /// <summary>
    /// Number of expired grants.
    /// </summary>
    public int ExpiredGrants { get; init; }

    /// <summary>
    /// Number of grants pending periodic review.
    /// </summary>
    public int GrantsPendingReview { get; init; }

    /// <summary>
    /// Total number of registered authorities.
    /// </summary>
    public int TotalAuthorities { get; init; }

    /// <summary>
    /// Total number of audit entries.
    /// </summary>
    public long TotalAuditEntries { get; init; }

    /// <summary>
    /// Total number of access attempts across all grants.
    /// </summary>
    public long TotalAccessAttempts { get; init; }
}

#endregion
