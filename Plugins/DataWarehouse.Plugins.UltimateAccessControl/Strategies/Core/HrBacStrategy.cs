using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.Core
{
    /// <summary>
    /// Hierarchical Role-Based Access Control (HrBAC) strategy implementation.
    /// Extends RBAC with role hierarchies and inheritance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// HrBAC features:
    /// - Role inheritance (child roles inherit parent permissions)
    /// - Role hierarchy traversal with depth limits
    /// - Separation of duties (SoD) constraints
    /// - Role dominance relationships
    /// </para>
    /// </remarks>
    public sealed class HrBacStrategy : AccessControlStrategyBase
    {
        private readonly ConcurrentDictionary<string, HierarchicalRole> _roles = new();
        private readonly ConcurrentDictionary<string, HashSet<string>> _rolePermissions = new();
        private readonly ConcurrentDictionary<string, HashSet<string>> _roleHierarchy = new(); // child -> parents
        private readonly ConcurrentDictionary<string, HashSet<string>> _effectivePermissionsCache = new();
        private readonly HashSet<(string, string)> _mutuallyExclusiveRoles = new();

        /// <inheritdoc/>
        public override string StrategyId => "hrbac";

        /// <inheritdoc/>
        public override string StrategyName => "Hierarchical Role-Based Access Control";

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
            // Load roles with hierarchy
            if (configuration.TryGetValue("Roles", out var rolesObj) &&
                rolesObj is IEnumerable<Dictionary<string, object>> roleConfigs)
            {
                foreach (var config in roleConfigs)
                {
                    var roleName = config["Name"]?.ToString() ?? "";
                    var level = config.TryGetValue("Level", out var levelObj) && levelObj is int l ? l : 0;
                    var permissions = (config.TryGetValue("Permissions", out var permsObj) &&
                                      permsObj is IEnumerable<string> perms)
                        ? perms.ToList()
                        : new List<string>();
                    var parentRoles = (config.TryGetValue("ParentRoles", out var parentsObj) &&
                                      parentsObj is IEnumerable<string> parents)
                        ? parents.ToList()
                        : new List<string>();

                    CreateRole(roleName, level, permissions, parentRoles);
                }
            }

            // Load SoD constraints
            if (configuration.TryGetValue("MutuallyExclusiveRoles", out var sodObj) &&
                sodObj is IEnumerable<Dictionary<string, object>> sodConfigs)
            {
                foreach (var config in sodConfigs)
                {
                    var role1 = config["Role1"]?.ToString() ?? "";
                    var role2 = config["Role2"]?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(role1) && !string.IsNullOrEmpty(role2))
                    {
                        DefineSeparationOfDuties(role1, role2);
                    }
                }
            }

            return base.InitializeAsync(configuration, cancellationToken);
        }

        /// <summary>
        /// Creates a role with hierarchy information.
        /// </summary>
        public void CreateRole(string roleName, int level, IEnumerable<string>? permissions = null, IEnumerable<string>? parentRoles = null)
        {
            var role = new HierarchicalRole
            {
                Name = roleName,
                Level = level,
                Description = $"Role: {roleName} (Level {level})",
                CreatedAt = DateTime.UtcNow
            };

            _roles[roleName] = role;
            _rolePermissions[roleName] = new HashSet<string>(permissions ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);

            if (parentRoles != null && parentRoles.Any())
            {
                _roleHierarchy[roleName] = new HashSet<string>(parentRoles, StringComparer.OrdinalIgnoreCase);
            }

            InvalidateCache();
        }

        /// <summary>
        /// Defines separation of duties constraint between two roles.
        /// </summary>
        public void DefineSeparationOfDuties(string role1, string role2)
        {
            _mutuallyExclusiveRoles.Add((role1, role2));
            _mutuallyExclusiveRoles.Add((role2, role1));
        }

        /// <summary>
        /// Checks if two roles are mutually exclusive.
        /// </summary>
        public bool AreRolesMutuallyExclusive(string role1, string role2)
        {
            return _mutuallyExclusiveRoles.Contains((role1, role2));
        }

        /// <summary>
        /// Validates that a set of roles doesn't violate SoD constraints.
        /// </summary>
        public (bool IsValid, List<string> Violations) ValidateSeparationOfDuties(IEnumerable<string> roles)
        {
            var violations = new List<string>();
            var roleList = roles.ToList();

            for (int i = 0; i < roleList.Count; i++)
            {
                for (int j = i + 1; j < roleList.Count; j++)
                {
                    if (AreRolesMutuallyExclusive(roleList[i], roleList[j]))
                    {
                        violations.Add($"Roles '{roleList[i]}' and '{roleList[j]}' are mutually exclusive");
                    }
                }
            }

            return (violations.Count == 0, violations);
        }

        /// <summary>
        /// Gets effective permissions for a role (including inherited).
        /// </summary>
        public HashSet<string> GetEffectivePermissions(string roleName, int maxDepth = 10)
        {
            var cacheKey = $"{roleName}:{maxDepth}";
            if (_effectivePermissionsCache.TryGetValue(cacheKey, out var cached))
            {
                return new HashSet<string>(cached, StringComparer.OrdinalIgnoreCase);
            }

            var effective = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectPermissions(roleName, effective, new HashSet<string>(), maxDepth);

            _effectivePermissionsCache[cacheKey] = effective;
            return new HashSet<string>(effective, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Gets effective permissions for multiple roles.
        /// </summary>
        public HashSet<string> GetEffectivePermissions(IEnumerable<string> roleNames, int maxDepth = 10)
        {
            var allPermissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var roleName in roleNames)
            {
                var rolePerms = GetEffectivePermissions(roleName, maxDepth);
                allPermissions.UnionWith(rolePerms);
            }

            return allPermissions;
        }

        /// <summary>
        /// Gets the role hierarchy path from a role to its ancestors.
        /// </summary>
        public List<string> GetRoleHierarchyPath(string roleName, int maxDepth = 10)
        {
            var path = new List<string> { roleName };
            var visited = new HashSet<string> { roleName };
            CollectHierarchyPath(roleName, path, visited, maxDepth);
            return path;
        }

        /// <inheritdoc/>
        protected override Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            var userRoles = context.Roles;

            if (!userRoles.Any())
            {
                return Task.FromResult(new AccessDecision
                {
                    IsGranted = false,
                    Reason = "HrBAC denied: user has no assigned roles",
                    ApplicablePolicies = new[] { "HrBAC.NoRoles" }
                });
            }

            // Validate SoD constraints
            var (isValid, violations) = ValidateSeparationOfDuties(userRoles);
            if (!isValid)
            {
                return Task.FromResult(new AccessDecision
                {
                    IsGranted = false,
                    Reason = $"HrBAC denied: Separation of Duties violation - {string.Join(", ", violations)}",
                    ApplicablePolicies = new[] { "HrBAC.SoDViolation" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["Violations"] = violations,
                        ["UserRoles"] = userRoles.ToList()
                    }
                });
            }

            // Get effective permissions with inheritance
            var effectivePermissions = GetEffectivePermissions(userRoles);

            // Build permission patterns to check
            var permissionsToCheck = new List<string>
            {
                $"{context.ResourceId}:{context.Action}",
                $"*:{context.Action}",
                $"{context.ResourceId}:*",
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
                var hierarchyPaths = userRoles.Select(r => string.Join(" -> ", GetRoleHierarchyPath(r))).ToList();

                return Task.FromResult(new AccessDecision
                {
                    IsGranted = true,
                    Reason = $"HrBAC granted: permission '{matchedPermissions.First()}' found via role hierarchy",
                    ApplicablePolicies = userRoles.Select(r => $"HrBAC.Role.{r}").ToArray(),
                    Metadata = new Dictionary<string, object>
                    {
                        ["MatchedPermissions"] = matchedPermissions,
                        ["UserRoles"] = userRoles.ToList(),
                        ["HierarchyPaths"] = hierarchyPaths,
                        ["EffectivePermissionCount"] = effectivePermissions.Count
                    }
                });
            }

            return Task.FromResult(new AccessDecision
            {
                IsGranted = false,
                Reason = $"HrBAC denied: no permission for action '{context.Action}' on resource '{context.ResourceId}'",
                ApplicablePolicies = new[] { "HrBAC.PermissionDenied" },
                Metadata = new Dictionary<string, object>
                {
                    ["RequiredPermission"] = $"{context.ResourceId}:{context.Action}",
                    ["UserRoles"] = userRoles.ToList(),
                    ["EffectivePermissions"] = effectivePermissions.Take(20).ToList()
                }
            });
        }

        private void CollectPermissions(string roleName, HashSet<string> permissions, HashSet<string> visited, int maxDepth)
        {
            if (maxDepth <= 0 || !visited.Add(roleName))
                return; // Avoid circular inheritance

            // Add direct permissions
            if (_rolePermissions.TryGetValue(roleName, out var directPerms))
            {
                permissions.UnionWith(directPerms);
            }

            // Add inherited permissions from parent roles
            if (_roleHierarchy.TryGetValue(roleName, out var parents))
            {
                foreach (var parent in parents)
                {
                    CollectPermissions(parent, permissions, visited, maxDepth - 1);
                }
            }
        }

        private void CollectHierarchyPath(string roleName, List<string> path, HashSet<string> visited, int maxDepth)
        {
            if (maxDepth <= 0 || !_roleHierarchy.TryGetValue(roleName, out var parents))
                return;

            foreach (var parent in parents)
            {
                if (visited.Add(parent))
                {
                    path.Add(parent);
                    CollectHierarchyPath(parent, path, visited, maxDepth - 1);
                }
            }
        }

        private void InvalidateCache()
        {
            _effectivePermissionsCache.Clear();
        }
    }

    /// <summary>
    /// Represents a hierarchical role.
    /// </summary>
    public sealed record HierarchicalRole
    {
        public required string Name { get; init; }
        public int Level { get; init; }
        public string Description { get; init; } = "";
        public DateTime CreatedAt { get; init; }
    }
}
