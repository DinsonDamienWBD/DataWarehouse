using System.Collections.Concurrent;

namespace DataWarehouse.Plugins.UniversalDashboards.Strategies;

/// <summary>
/// Dashboard-level access control service. Provides permission management for dashboards
/// with multi-tenant isolation, role-based access, and delegation to UltimateAccessControl via bus.
/// </summary>
public sealed class DashboardAccessControlService
{
    private readonly ConcurrentDictionary<string, DashboardPermissions> _permissions = new();
    private readonly ConcurrentDictionary<string, List<DashboardAccessGrant>> _grants = new();
    private readonly ConcurrentDictionary<string, List<DashboardAccessAuditEntry>> _auditLog = new();

    /// <summary>
    /// Sets permissions for a dashboard.
    /// </summary>
    public void SetPermissions(string tenantId, string dashboardId, string ownerId,
        DashboardVisibility visibility = DashboardVisibility.Private)
    {
        var key = BuildKey(tenantId, dashboardId);
        _permissions[key] = new DashboardPermissions
        {
            TenantId = tenantId,
            DashboardId = dashboardId,
            OwnerId = ownerId,
            Visibility = visibility,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>
    /// Grants access to a specific user or role.
    /// </summary>
    public DashboardAccessGrant GrantAccess(string tenantId, string dashboardId, string granteeId,
        DashboardRole role, string grantedBy)
    {
        var grant = new DashboardAccessGrant
        {
            GrantId = Guid.NewGuid().ToString("N"),
            TenantId = tenantId,
            DashboardId = dashboardId,
            GranteeId = granteeId,
            Role = role,
            GrantedBy = grantedBy,
            GrantedAt = DateTimeOffset.UtcNow
        };

        var key = BuildKey(tenantId, dashboardId);
        var grants = _grants.GetOrAdd(key, _ => new List<DashboardAccessGrant>());
        lock (grants) { grants.Add(grant); }

        RecordAudit(tenantId, dashboardId, grantedBy, "GrantAccess",
            $"Granted {role} to {granteeId}");

        return grant;
    }

    /// <summary>
    /// Revokes access from a specific user or role.
    /// </summary>
    public bool RevokeAccess(string tenantId, string dashboardId, string granteeId, string revokedBy)
    {
        var key = BuildKey(tenantId, dashboardId);
        if (!_grants.TryGetValue(key, out var grants)) return false;

        bool removed;
        lock (grants)
        {
            removed = grants.RemoveAll(g => g.GranteeId == granteeId) > 0;
        }

        if (removed)
        {
            RecordAudit(tenantId, dashboardId, revokedBy, "RevokeAccess",
                $"Revoked access from {granteeId}");
        }

        return removed;
    }

    /// <summary>
    /// Checks if a user has the required permission on a dashboard.
    /// </summary>
    public DashboardAccessCheck CheckAccess(string tenantId, string dashboardId, string userId,
        DashboardRole requiredRole = DashboardRole.Viewer)
    {
        var key = BuildKey(tenantId, dashboardId);

        // Check ownership
        if (_permissions.TryGetValue(key, out var perms) && perms.OwnerId == userId)
        {
            return new DashboardAccessCheck
            {
                Allowed = true,
                EffectiveRole = DashboardRole.Owner,
                Reason = "Dashboard owner"
            };
        }

        // Check visibility
        if (perms?.Visibility == DashboardVisibility.Public && requiredRole <= DashboardRole.Viewer)
        {
            return new DashboardAccessCheck
            {
                Allowed = true,
                EffectiveRole = DashboardRole.Viewer,
                Reason = "Public dashboard"
            };
        }

        if (perms?.Visibility == DashboardVisibility.TenantWide && requiredRole <= DashboardRole.Viewer)
        {
            return new DashboardAccessCheck
            {
                Allowed = true,
                EffectiveRole = DashboardRole.Viewer,
                Reason = "Tenant-wide dashboard"
            };
        }

        // Check explicit grants
        if (_grants.TryGetValue(key, out var grants))
        {
            DashboardAccessGrant? bestGrant;
            lock (grants)
            {
                bestGrant = grants
                    .Where(g => g.GranteeId == userId && (!g.ExpiresAt.HasValue || g.ExpiresAt > DateTimeOffset.UtcNow))
                    .OrderByDescending(g => g.Role)
                    .FirstOrDefault();
            }

            if (bestGrant != null && bestGrant.Role >= requiredRole)
            {
                return new DashboardAccessCheck
                {
                    Allowed = true,
                    EffectiveRole = bestGrant.Role,
                    Reason = $"Explicit grant (via {bestGrant.GrantedBy})"
                };
            }
        }

        RecordAudit(tenantId, dashboardId, userId, "AccessDenied",
            $"Required {requiredRole}, no sufficient grant found");

        return new DashboardAccessCheck
        {
            Allowed = false,
            EffectiveRole = DashboardRole.None,
            Reason = "No sufficient permission found"
        };
    }

    /// <summary>
    /// Gets the audit log for a dashboard.
    /// </summary>
    public IReadOnlyList<DashboardAccessAuditEntry> GetAuditLog(string tenantId, string dashboardId, int limit = 100)
    {
        var key = BuildKey(tenantId, dashboardId);
        if (!_auditLog.TryGetValue(key, out var entries)) return Array.Empty<DashboardAccessAuditEntry>();
        lock (entries) { return entries.TakeLast(limit).ToList().AsReadOnly(); }
    }

    /// <summary>
    /// Lists all grants for a dashboard.
    /// </summary>
    public IReadOnlyList<DashboardAccessGrant> ListGrants(string tenantId, string dashboardId)
    {
        var key = BuildKey(tenantId, dashboardId);
        if (!_grants.TryGetValue(key, out var grants)) return Array.Empty<DashboardAccessGrant>();
        lock (grants) { return grants.ToList().AsReadOnly(); }
    }

    private static string BuildKey(string tenantId, string dashboardId) => $"{tenantId}:{dashboardId}";

    private void RecordAudit(string tenantId, string dashboardId, string userId, string action, string details)
    {
        var key = BuildKey(tenantId, dashboardId);
        var entry = new DashboardAccessAuditEntry
        {
            TenantId = tenantId,
            DashboardId = dashboardId,
            UserId = userId,
            Action = action,
            Details = details,
            Timestamp = DateTimeOffset.UtcNow
        };

        var log = _auditLog.GetOrAdd(key, _ => new List<DashboardAccessAuditEntry>());
        lock (log)
        {
            log.Add(entry);
            while (log.Count > 5000) log.RemoveAt(0);
        }
    }
}

#region Models

public enum DashboardVisibility { Private, TenantWide, Public }

public enum DashboardRole
{
    None = 0,
    Viewer = 1,
    Editor = 2,
    Admin = 3,
    Owner = 4
}

public sealed record DashboardPermissions
{
    public required string TenantId { get; init; }
    public required string DashboardId { get; init; }
    public required string OwnerId { get; init; }
    public DashboardVisibility Visibility { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed record DashboardAccessGrant
{
    public required string GrantId { get; init; }
    public required string TenantId { get; init; }
    public required string DashboardId { get; init; }
    public required string GranteeId { get; init; }
    public DashboardRole Role { get; init; }
    public required string GrantedBy { get; init; }
    public DateTimeOffset GrantedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
}

public sealed record DashboardAccessCheck
{
    public bool Allowed { get; init; }
    public DashboardRole EffectiveRole { get; init; }
    public required string Reason { get; init; }
}

public sealed record DashboardAccessAuditEntry
{
    public required string TenantId { get; init; }
    public required string DashboardId { get; init; }
    public required string UserId { get; init; }
    public required string Action { get; init; }
    public required string Details { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}

#endregion
