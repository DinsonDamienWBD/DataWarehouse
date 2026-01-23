using System.Collections.Concurrent;

namespace DataWarehouse.SDK.Services;

/// <summary>
/// Permission cascade service implementing hierarchical permission inheritance.
/// Permissions cascade from parent to child resources automatically.
/// </summary>
public sealed class PermissionCascadeService : IDisposable
{
    private readonly ConcurrentDictionary<string, ResourcePermissions> _permissions = new();
    private readonly ConcurrentDictionary<string, string> _parentMap = new();
    private readonly ConcurrentDictionary<string, HashSet<string>> _childrenMap = new();
    private readonly ReaderWriterLockSlim _hierarchyLock = new(LockRecursionPolicy.SupportsRecursion);
    private bool _disposed;

    /// <summary>
    /// Event raised when permissions change.
    /// </summary>
    public event EventHandler<PermissionChangedEventArgs>? PermissionChanged;

    /// <summary>
    /// Sets the parent-child relationship for permission inheritance.
    /// </summary>
    public void SetParent(string resourceId, string? parentId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _hierarchyLock.EnterWriteLock();
        try
        {
            // Remove from old parent's children
            if (_parentMap.TryGetValue(resourceId, out var oldParent) && oldParent != null)
            {
                if (_childrenMap.TryGetValue(oldParent, out var siblings))
                {
                    siblings.Remove(resourceId);
                }
            }

            // Set new parent
            if (parentId != null)
            {
                _parentMap[resourceId] = parentId;

                if (!_childrenMap.TryGetValue(parentId, out var children))
                {
                    children = new HashSet<string>();
                    _childrenMap[parentId] = children;
                }
                children.Add(resourceId);
            }
            else
            {
                _parentMap.TryRemove(resourceId, out _);
            }
        }
        finally
        {
            _hierarchyLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Gets the parent of a resource.
    /// </summary>
    public string? GetParent(string resourceId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _parentMap.TryGetValue(resourceId, out var parent) ? parent : null;
    }

    /// <summary>
    /// Gets all children of a resource.
    /// </summary>
    public IEnumerable<string> GetChildren(string resourceId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _hierarchyLock.EnterReadLock();
        try
        {
            return _childrenMap.TryGetValue(resourceId, out var children)
                ? children.ToList()
                : Enumerable.Empty<string>();
        }
        finally
        {
            _hierarchyLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Grants permissions to a principal for a resource.
    /// </summary>
    public void Grant(string resourceId, string principalId, Permission permission, bool cascade = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var perms = _permissions.GetOrAdd(resourceId, _ => new ResourcePermissions { ResourceId = resourceId });

        var entry = perms.Entries.FirstOrDefault(e => e.PrincipalId == principalId);
        if (entry == null)
        {
            entry = new PermissionEntry { PrincipalId = principalId };
            perms.Entries.Add(entry);
        }

        entry.Permissions |= permission;
        entry.UpdatedAt = DateTime.UtcNow;

        PermissionChanged?.Invoke(this, new PermissionChangedEventArgs
        {
            ResourceId = resourceId,
            PrincipalId = principalId,
            Permission = permission,
            Action = PermissionAction.Granted,
            Cascaded = false
        });

        // Cascade to children if requested
        if (cascade)
        {
            CascadePermissions(resourceId, principalId, permission, PermissionAction.Granted);
        }
    }

    /// <summary>
    /// Revokes permissions from a principal for a resource.
    /// </summary>
    public void Revoke(string resourceId, string principalId, Permission permission, bool cascade = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_permissions.TryGetValue(resourceId, out var perms))
            return;

        var entry = perms.Entries.FirstOrDefault(e => e.PrincipalId == principalId);
        if (entry == null)
            return;

        entry.Permissions &= ~permission;
        entry.UpdatedAt = DateTime.UtcNow;

        PermissionChanged?.Invoke(this, new PermissionChangedEventArgs
        {
            ResourceId = resourceId,
            PrincipalId = principalId,
            Permission = permission,
            Action = PermissionAction.Revoked,
            Cascaded = false
        });

        // Cascade to children if requested
        if (cascade)
        {
            CascadePermissions(resourceId, principalId, permission, PermissionAction.Revoked);
        }
    }

    /// <summary>
    /// Checks if a principal has a specific permission on a resource.
    /// Includes inherited permissions from parent resources.
    /// </summary>
    public bool HasPermission(string resourceId, string principalId, Permission permission)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Check direct permissions
        if (_permissions.TryGetValue(resourceId, out var perms))
        {
            var entry = perms.Entries.FirstOrDefault(e => e.PrincipalId == principalId);
            if (entry != null)
            {
                // Explicit deny always wins
                if ((entry.DeniedPermissions & permission) != 0)
                    return false;

                if ((entry.Permissions & permission) == permission)
                    return true;
            }
        }

        // Check inherited permissions from parents
        _hierarchyLock.EnterReadLock();
        try
        {
            var current = resourceId;
            while (_parentMap.TryGetValue(current, out var parent))
            {
                if (_permissions.TryGetValue(parent, out var parentPerms))
                {
                    var parentEntry = parentPerms.Entries.FirstOrDefault(e => e.PrincipalId == principalId);
                    if (parentEntry != null)
                    {
                        // Check for explicit deny
                        if ((parentEntry.DeniedPermissions & permission) != 0)
                            return false;

                        // Check if permission is inheritable
                        if (parentEntry.Inheritable && (parentEntry.Permissions & permission) == permission)
                            return true;
                    }
                }
                current = parent;
            }
        }
        finally
        {
            _hierarchyLock.ExitReadLock();
        }

        return false;
    }

    /// <summary>
    /// Gets the effective permissions for a principal on a resource.
    /// Includes all inherited permissions.
    /// </summary>
    public Permission GetEffectivePermissions(string resourceId, string principalId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var effective = Permission.None;
        var denied = Permission.None;

        // Collect permissions from ancestors first (lower priority)
        var ancestors = new List<string>();
        _hierarchyLock.EnterReadLock();
        try
        {
            var current = resourceId;
            while (_parentMap.TryGetValue(current, out var parent))
            {
                ancestors.Add(parent);
                current = parent;
            }
        }
        finally
        {
            _hierarchyLock.ExitReadLock();
        }

        // Process from root to leaf for correct precedence
        ancestors.Reverse();
        foreach (var ancestor in ancestors)
        {
            if (_permissions.TryGetValue(ancestor, out var ancestorPerms))
            {
                var entry = ancestorPerms.Entries.FirstOrDefault(e => e.PrincipalId == principalId);
                if (entry != null && entry.Inheritable)
                {
                    effective |= entry.Permissions;
                    denied |= entry.DeniedPermissions;
                }
            }
        }

        // Apply direct permissions (highest priority)
        if (_permissions.TryGetValue(resourceId, out var perms))
        {
            var entry = perms.Entries.FirstOrDefault(e => e.PrincipalId == principalId);
            if (entry != null)
            {
                effective |= entry.Permissions;
                denied |= entry.DeniedPermissions;
            }
        }

        // Apply denies
        return effective & ~denied;
    }

    /// <summary>
    /// Sets explicit deny for permissions.
    /// </summary>
    public void Deny(string resourceId, string principalId, Permission permission, bool cascade = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var perms = _permissions.GetOrAdd(resourceId, _ => new ResourcePermissions { ResourceId = resourceId });

        var entry = perms.Entries.FirstOrDefault(e => e.PrincipalId == principalId);
        if (entry == null)
        {
            entry = new PermissionEntry { PrincipalId = principalId };
            perms.Entries.Add(entry);
        }

        entry.DeniedPermissions |= permission;
        entry.UpdatedAt = DateTime.UtcNow;

        PermissionChanged?.Invoke(this, new PermissionChangedEventArgs
        {
            ResourceId = resourceId,
            PrincipalId = principalId,
            Permission = permission,
            Action = PermissionAction.Denied,
            Cascaded = false
        });

        if (cascade)
        {
            CascadePermissions(resourceId, principalId, permission, PermissionAction.Denied);
        }
    }

    /// <summary>
    /// Gets all permission entries for a resource.
    /// </summary>
    public IEnumerable<PermissionEntry> GetPermissionEntries(string resourceId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return _permissions.TryGetValue(resourceId, out var perms)
            ? perms.Entries.ToList()
            : Enumerable.Empty<PermissionEntry>();
    }

    private void CascadePermissions(string resourceId, string principalId, Permission permission, PermissionAction action)
    {
        _hierarchyLock.EnterReadLock();
        try
        {
            if (!_childrenMap.TryGetValue(resourceId, out var children))
                return;

            foreach (var child in children.ToList())
            {
                switch (action)
                {
                    case PermissionAction.Granted:
                        Grant(child, principalId, permission, cascade: true);
                        break;
                    case PermissionAction.Revoked:
                        Revoke(child, principalId, permission, cascade: true);
                        break;
                    case PermissionAction.Denied:
                        Deny(child, principalId, permission, cascade: true);
                        break;
                }

                PermissionChanged?.Invoke(this, new PermissionChangedEventArgs
                {
                    ResourceId = child,
                    PrincipalId = principalId,
                    Permission = permission,
                    Action = action,
                    Cascaded = true
                });
            }
        }
        finally
        {
            _hierarchyLock.ExitReadLock();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _hierarchyLock.Dispose();
        _disposed = true;
    }
}

/// <summary>
/// Permission flags for resources.
/// </summary>
[Flags]
public enum Permission
{
    None = 0,
    Read = 1,
    Write = 2,
    Delete = 4,
    Execute = 8,
    Share = 16,
    Admin = 32,
    All = Read | Write | Delete | Execute | Share | Admin
}

/// <summary>
/// Actions that can be performed on permissions.
/// </summary>
public enum PermissionAction
{
    Granted,
    Revoked,
    Denied
}

/// <summary>
/// Permission entry for a principal.
/// </summary>
public class PermissionEntry
{
    public string PrincipalId { get; set; } = string.Empty;
    public Permission Permissions { get; set; } = Permission.None;
    public Permission DeniedPermissions { get; set; } = Permission.None;
    public bool Inheritable { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Event args for permission changes.
/// </summary>
public class PermissionChangedEventArgs : EventArgs
{
    public string ResourceId { get; set; } = string.Empty;
    public string PrincipalId { get; set; } = string.Empty;
    public Permission Permission { get; set; }
    public PermissionAction Action { get; set; }
    public bool Cascaded { get; set; }
}

internal class ResourcePermissions
{
    public string ResourceId { get; set; } = string.Empty;
    public List<PermissionEntry> Entries { get; set; } = new();
}
