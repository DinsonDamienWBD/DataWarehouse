using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.Core
{
    /// <summary>
    /// Role-Based Access Control (RBAC) strategy implementation.
    /// Grants access based on user roles and role-permission mappings.
    /// </summary>
    /// <remarks>
    /// <para>
    /// RBAC features:
    /// - Role hierarchy with inheritance
    /// - Fine-grained permissions
    /// - Role assignment management
    /// - Permission caching for performance
    /// </para>
    /// </remarks>
    public sealed class RbacStrategy : AccessControlStrategyBase
    {
        private readonly ConcurrentDictionary<string, Role> _roles = new();
        private readonly ConcurrentDictionary<string, HashSet<string>> _rolePermissions = new();
        private readonly ConcurrentDictionary<string, HashSet<string>> _roleHierarchy = new();
        private readonly ConcurrentDictionary<string, HashSet<string>> _effectivePermissionsCache = new();

        /// <inheritdoc/>
        public override string StrategyId => "rbac";

        /// <inheritdoc/>
        public override string StrategyName => "Role-Based Access Control";

        /// <inheritdoc/>
        public override AccessControlCapabilities Capabilities { get; } = new()
        {
            SupportsRealTimeDecisions = true,
            SupportsAuditTrail = true,
            SupportsPolicyConfiguration = true,
            SupportsExternalIdentity = true,
            SupportsTemporalAccess = false,
            SupportsGeographicRestrictions = false,
            MaxConcurrentEvaluations = 10000
        };

        /// <inheritdoc/>
        public override Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken cancellationToken = default)
        {
            // Load roles from configuration
            if (configuration.TryGetValue("Roles", out var rolesObj) &&
                rolesObj is IEnumerable<Dictionary<string, object>> roleConfigs)
            {
                foreach (var config in roleConfigs)
                {
                    var roleName = config["Name"]?.ToString() ?? "";
                    var permissions = (config.TryGetValue("Permissions", out var permsObj) &&
                                      permsObj is IEnumerable<string> perms)
                        ? perms.ToList()
                        : new List<string>();

                    var parentRoles = (config.TryGetValue("ParentRoles", out var parentsObj) &&
                                      parentsObj is IEnumerable<string> parents)
                        ? parents.ToList()
                        : new List<string>();

                    CreateRole(roleName, permissions, parentRoles);
                }
            }

            return base.InitializeAsync(configuration, cancellationToken);
        }

        /// <summary>
        /// Creates a new role.
        /// </summary>
        public void CreateRole(string roleName, IEnumerable<string>? permissions = null, IEnumerable<string>? parentRoles = null)
        {
            var role = new Role
            {
                Name = roleName,
                Description = $"Role: {roleName}",
                CreatedAt = DateTime.UtcNow
            };

            _roles[roleName] = role;
            _rolePermissions[roleName] = new HashSet<string>(permissions ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);

            if (parentRoles != null && parentRoles.Any())
            {
                _roleHierarchy[roleName] = new HashSet<string>(parentRoles, StringComparer.OrdinalIgnoreCase);
            }

            // Invalidate cache
            InvalidateCache();
        }

        /// <summary>
        /// Grants a permission to a role.
        /// </summary>
        public void GrantPermission(string roleName, string permission)
        {
            if (!_rolePermissions.TryGetValue(roleName, out var permissions))
            {
                permissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _rolePermissions[roleName] = permissions;
            }

            permissions.Add(permission);
            InvalidateCache();
        }

        /// <summary>
        /// Revokes a permission from a role.
        /// </summary>
        public void RevokePermission(string roleName, string permission)
        {
            if (_rolePermissions.TryGetValue(roleName, out var permissions))
            {
                permissions.Remove(permission);
                InvalidateCache();
            }
        }

        /// <summary>
        /// Gets effective permissions for a role (including inherited).
        /// </summary>
        public HashSet<string> GetEffectivePermissions(string roleName)
        {
            if (_effectivePermissionsCache.TryGetValue(roleName, out var cached))
            {
                return new HashSet<string>(cached, StringComparer.OrdinalIgnoreCase);
            }

            var effective = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectPermissions(roleName, effective, new HashSet<string>());

            _effectivePermissionsCache[roleName] = effective;
            return new HashSet<string>(effective, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Gets effective permissions for multiple roles.
        /// </summary>
        public HashSet<string> GetEffectivePermissions(IEnumerable<string> roleNames)
        {
            var allPermissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var roleName in roleNames)
            {
                var rolePerms = GetEffectivePermissions(roleName);
                allPermissions.UnionWith(rolePerms);
            }

            return allPermissions;
        }

        /// <inheritdoc/>
        protected override Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            // Get user's roles
            var userRoles = context.Roles;

            if (!userRoles.Any())
            {
                return Task.FromResult(new AccessDecision
                {
                    IsGranted = false,
                    Reason = "User has no assigned roles",
                    ApplicablePolicies = new[] { "RBAC.NoRoles" }
                });
            }

            // Get effective permissions
            var effectivePermissions = GetEffectivePermissions(userRoles);

            // Build permission patterns to check
            var permissionsToCheck = new List<string>
            {
                // Exact permission: "resource:action"
                $"{context.ResourceId}:{context.Action}",
                // Wildcard resource: "*:action"
                $"*:{context.Action}",
                // Wildcard action: "resource:*"
                $"{context.ResourceId}:*",
                // Full wildcard
                "*:*"
            };

            // Check for resource prefix patterns
            var resourceParts = context.ResourceId.Split('/');
            for (int i = 1; i < resourceParts.Length; i++)
            {
                var prefix = string.Join("/", resourceParts.Take(i));
                permissionsToCheck.Add($"{prefix}/*:{context.Action}");
                permissionsToCheck.Add($"{prefix}/*:*");
            }

            // Check if any permission matches
            var matchedPermissions = permissionsToCheck
                .Where(p => effectivePermissions.Contains(p))
                .ToList();

            if (matchedPermissions.Any())
            {
                return Task.FromResult(new AccessDecision
                {
                    IsGranted = true,
                    Reason = $"Access granted by RBAC permission: {matchedPermissions.First()}",
                    ApplicablePolicies = userRoles.Select(r => $"RBAC.Role.{r}").ToArray(),
                    Metadata = new Dictionary<string, object>
                    {
                        ["MatchedPermissions"] = matchedPermissions,
                        ["UserRoles"] = userRoles.ToList()
                    }
                });
            }

            return Task.FromResult(new AccessDecision
            {
                IsGranted = false,
                Reason = $"No RBAC permission found for action '{context.Action}' on resource '{context.ResourceId}'",
                ApplicablePolicies = new[] { "RBAC.PermissionDenied" },
                Metadata = new Dictionary<string, object>
                {
                    ["RequiredPermission"] = $"{context.ResourceId}:{context.Action}",
                    ["UserRoles"] = userRoles.ToList(),
                    ["EffectivePermissions"] = effectivePermissions.Take(20).ToList()
                }
            });
        }

        private void CollectPermissions(string roleName, HashSet<string> permissions, HashSet<string> visited)
        {
            if (!visited.Add(roleName))
                return; // Avoid circular inheritance

            // Add direct permissions
            if (_rolePermissions.TryGetValue(roleName, out var directPerms))
            {
                permissions.UnionWith(directPerms);
            }

            // Add inherited permissions
            if (_roleHierarchy.TryGetValue(roleName, out var parents))
            {
                foreach (var parent in parents)
                {
                    CollectPermissions(parent, permissions, visited);
                }
            }
        }

        private void InvalidateCache()
        {
            _effectivePermissionsCache.Clear();
        }
    }

    /// <summary>
    /// Represents an RBAC role.
    /// </summary>
    public sealed record Role
    {
        public required string Name { get; init; }
        public string Description { get; init; } = "";
        public DateTime CreatedAt { get; init; }
        public DateTime? ModifiedAt { get; init; }
    }
}
