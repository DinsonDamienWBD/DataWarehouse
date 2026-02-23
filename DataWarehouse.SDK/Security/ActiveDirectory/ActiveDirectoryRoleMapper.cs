using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataWarehouse.SDK.Security.ActiveDirectory
{
    /// <summary>
    /// Defines a mapping from an Active Directory group to a DataWarehouse role.
    /// Groups can be identified by SID (e.g., "S-1-5-21-*-512") or distinguished
    /// name (e.g., "CN=Domain Admins,CN=Users,DC=corp,DC=example,DC=com").
    /// </summary>
    /// <param name="AdGroupIdentifier">
    /// The AD group identifier -- either a SID string (e.g., "S-1-5-21-xxx-512")
    /// or a distinguished name / common name (e.g., "Domain Admins").
    /// </param>
    /// <param name="DwRole">
    /// The DataWarehouse role to assign (e.g., "admin", "viewer", "operator").
    /// </param>
    /// <param name="Priority">
    /// Priority for conflict resolution. Higher values take precedence when a user
    /// belongs to multiple groups with different role assignments.
    /// </param>
    /// <param name="IsWildcard">
    /// When true, the <paramref name="AdGroupIdentifier"/> is treated as a prefix match
    /// (e.g., "Domain Admins" matches any group containing that name).
    /// </param>
    public sealed record AdGroupRoleMapping(
        string AdGroupIdentifier,
        string DwRole,
        int Priority = 0,
        bool IsWildcard = false);

    /// <summary>
    /// Configuration for Active Directory group-to-role mapping.
    /// Includes explicit mapping rules and well-known AD group defaults.
    /// </summary>
    /// <param name="Mappings">
    /// Explicit group-to-role mappings. These are evaluated in addition to
    /// well-known mappings when enabled.
    /// </param>
    /// <param name="DefaultRole">
    /// Role assigned when a user's groups match no explicit or well-known mappings.
    /// Defaults to "viewer".
    /// </param>
    /// <param name="MapDomainAdminsToAdmin">
    /// When true (default), "Domain Admins" (SID ending -512) automatically maps to "admin".
    /// </param>
    /// <param name="MapEnterpriseAdminsToSuperAdmin">
    /// When true (default), "Enterprise Admins" (SID ending -519) automatically maps to "super_admin".
    /// </param>
    public sealed record ActiveDirectoryRoleMappingConfig(
        IReadOnlyList<AdGroupRoleMapping> Mappings,
        string DefaultRole = "viewer",
        bool MapDomainAdminsToAdmin = true,
        bool MapEnterpriseAdminsToSuperAdmin = true);

    /// <summary>
    /// Maps Active Directory group memberships to DataWarehouse roles.
    /// Supports configurable explicit mappings, well-known AD group defaults
    /// (Domain Admins, Enterprise Admins, Domain Users), and priority-based
    /// conflict resolution.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Thread safety:</b> This class is thread-safe. The configuration is immutable
    /// after construction and the internal lookup uses <see cref="FrozenDictionary{TKey, TValue}"/>
    /// for lock-free concurrent reads.
    /// </para>
    /// <para>
    /// <b>Role precedence:</b> When <see cref="GetEffectiveRole"/> is called, the
    /// highest-privilege role wins. The built-in precedence order is:
    /// super_admin &gt; admin &gt; operator &gt; editor &gt; viewer.
    /// </para>
    /// <para>
    /// <b>Well-known mappings:</b> When enabled via configuration, the following
    /// AD groups are mapped automatically:
    /// <list type="bullet">
    /// <item><description>"Domain Admins" / SID *-512 -> "admin" (priority 100)</description></item>
    /// <item><description>"Enterprise Admins" / SID *-519 -> "super_admin" (priority 200)</description></item>
    /// <item><description>"Domain Users" / SID *-513 -> "viewer" (priority 0)</description></item>
    /// </list>
    /// </para>
    /// </remarks>
    public sealed class ActiveDirectoryRoleMapper
    {
        /// <summary>
        /// Well-known AD group RID suffixes.
        /// </summary>
        private const string DomainAdminsSidSuffix = "-512";
        private const string DomainUsersSidSuffix = "-513";
        private const string EnterpriseAdminsSidSuffix = "-519";

        /// <summary>
        /// Built-in role precedence (higher index = higher privilege).
        /// </summary>
        private static readonly FrozenDictionary<string, int> RolePrecedence =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["viewer"] = 0,
                ["editor"] = 1,
                ["operator"] = 2,
                ["admin"] = 3,
                ["super_admin"] = 4
            }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

        private readonly ActiveDirectoryRoleMappingConfig _config;
        private readonly FrozenDictionary<string, AdGroupRoleMapping> _exactMappings;
        private readonly IReadOnlyList<AdGroupRoleMapping> _wildcardMappings;
        private readonly IReadOnlyList<AdGroupRoleMapping> _wellKnownMappings;
        private readonly ILogger _logger;

        /// <summary>
        /// Creates a new Active Directory role mapper with the specified configuration.
        /// </summary>
        /// <param name="config">Mapping configuration. Must not be null.</param>
        /// <param name="logger">Optional logger for diagnostic output.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="config"/> is null.</exception>
        public ActiveDirectoryRoleMapper(ActiveDirectoryRoleMappingConfig config, ILogger? logger = null)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _logger = logger ?? NullLogger.Instance;

            ValidateConfig();

            // Build well-known mappings
            var wellKnown = new List<AdGroupRoleMapping>();
            if (config.MapDomainAdminsToAdmin)
            {
                wellKnown.Add(new AdGroupRoleMapping("Domain Admins", "admin", Priority: 100));
            }
            if (config.MapEnterpriseAdminsToSuperAdmin)
            {
                wellKnown.Add(new AdGroupRoleMapping("Enterprise Admins", "super_admin", Priority: 200));
            }
            // Domain Users always maps to default role at lowest priority
            wellKnown.Add(new AdGroupRoleMapping("Domain Users", config.DefaultRole, Priority: -1));
            _wellKnownMappings = wellKnown;

            // Separate exact and wildcard mappings for efficient lookup
            var exact = new Dictionary<string, AdGroupRoleMapping>(StringComparer.OrdinalIgnoreCase);
            var wildcards = new List<AdGroupRoleMapping>();

            foreach (var mapping in config.Mappings)
            {
                if (mapping.IsWildcard)
                {
                    wildcards.Add(mapping);
                }
                else
                {
                    // Last-wins for exact duplicates (validated with warning above)
                    exact[mapping.AdGroupIdentifier] = mapping;
                }
            }

            _exactMappings = exact.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
            _wildcardMappings = wildcards;
        }

        /// <summary>
        /// Maps a list of AD group memberships to DataWarehouse roles.
        /// Each group is resolved independently; the result includes all matched roles
        /// ordered by their mapping priority (highest first).
        /// </summary>
        /// <param name="adGroups">
        /// List of AD group names or SIDs from the user's Kerberos PAC data.
        /// </param>
        /// <returns>
        /// Distinct DataWarehouse roles matched from the group list, ordered by
        /// mapping priority (highest first). Returns a list containing only the
        /// default role if no groups match any mapping.
        /// </returns>
        public IReadOnlyList<string> MapGroupsToRoles(IReadOnlyList<string> adGroups)
        {
            ArgumentNullException.ThrowIfNull(adGroups);

            if (adGroups.Count == 0)
            {
                return [_config.DefaultRole];
            }

            var matchedMappings = new List<(string Role, int Priority)>();

            foreach (var group in adGroups)
            {
                if (string.IsNullOrWhiteSpace(group))
                    continue;

                var mapping = ResolveMapping(group);
                if (mapping is not null)
                {
                    matchedMappings.Add((mapping.DwRole, mapping.Priority));
                }
            }

            if (matchedMappings.Count == 0)
            {
                return [_config.DefaultRole];
            }

            return matchedMappings
                .OrderByDescending(m => m.Priority)
                .Select(m => m.Role)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Returns the single highest-privilege role from the user's AD group memberships.
        /// Uses the built-in role precedence: super_admin &gt; admin &gt; operator &gt; editor &gt; viewer.
        /// </summary>
        /// <param name="adGroups">
        /// List of AD group names or SIDs from the user's Kerberos PAC data.
        /// </param>
        /// <returns>
        /// The highest-privilege role the user qualifies for, or the configured
        /// default role if no groups match.
        /// </returns>
        public string GetEffectiveRole(IReadOnlyList<string> adGroups)
        {
            var roles = MapGroupsToRoles(adGroups);

            if (roles.Count == 0)
                return _config.DefaultRole;

            if (roles.Count == 1)
                return roles[0];

            // Return the role with the highest precedence
            var bestRole = _config.DefaultRole;
            var bestPrecedence = GetRolePrecedence(bestRole);

            foreach (var role in roles)
            {
                var precedence = GetRolePrecedence(role);
                if (precedence > bestPrecedence)
                {
                    bestRole = role;
                    bestPrecedence = precedence;
                }
            }

            return bestRole;
        }

        /// <summary>
        /// Validates the mapping configuration, logging warnings for duplicate
        /// group identifiers or conflicting priority assignments.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Thrown if critical configuration errors are detected (currently none;
        /// duplicates and conflicts produce warnings only).
        /// </exception>
        public void ValidateConfig()
        {
            var seen = new Dictionary<string, AdGroupRoleMapping>(StringComparer.OrdinalIgnoreCase);

            foreach (var mapping in _config.Mappings)
            {
                if (string.IsNullOrWhiteSpace(mapping.AdGroupIdentifier))
                {
                    _logger.LogWarning("AD role mapping has empty group identifier, skipping");
                    continue;
                }

                if (seen.TryGetValue(mapping.AdGroupIdentifier, out var existing))
                {
                    if (existing.Priority == mapping.Priority && !string.Equals(existing.DwRole, mapping.DwRole, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning(
                            "Conflicting AD role mappings for group '{Group}': '{Role1}' (priority {P1}) vs '{Role2}' (priority {P2}). Last mapping wins.",
                            mapping.AdGroupIdentifier, existing.DwRole, existing.Priority, mapping.DwRole, mapping.Priority);
                    }
                    else if (!string.Equals(existing.DwRole, mapping.DwRole, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning(
                            "Duplicate AD role mapping for group '{Group}': '{Role1}' (priority {P1}) and '{Role2}' (priority {P2})",
                            mapping.AdGroupIdentifier, existing.DwRole, existing.Priority, mapping.DwRole, mapping.Priority);
                    }
                }

                seen[mapping.AdGroupIdentifier] = mapping;
            }
        }

        /// <summary>
        /// Resolves the best mapping for a given AD group identifier.
        /// Checks exact matches first, then well-known SID patterns, then wildcards.
        /// </summary>
        private AdGroupRoleMapping? ResolveMapping(string group)
        {
            // 1. Exact match from explicit mappings (fastest path via FrozenDictionary)
            if (_exactMappings.TryGetValue(group, out var exactMapping))
                return exactMapping;

            // 2. Well-known SID-based mappings (check SID suffix)
            if (group.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase))
            {
                if (_config.MapEnterpriseAdminsToSuperAdmin && group.EndsWith(EnterpriseAdminsSidSuffix, StringComparison.Ordinal))
                    return new AdGroupRoleMapping(group, "super_admin", Priority: 200);

                if (_config.MapDomainAdminsToAdmin && group.EndsWith(DomainAdminsSidSuffix, StringComparison.Ordinal))
                    return new AdGroupRoleMapping(group, "admin", Priority: 100);

                if (group.EndsWith(DomainUsersSidSuffix, StringComparison.Ordinal))
                    return new AdGroupRoleMapping(group, _config.DefaultRole, Priority: -1);
            }

            // 3. Well-known name-based mappings
            foreach (var wk in _wellKnownMappings)
            {
                if (string.Equals(group, wk.AdGroupIdentifier, StringComparison.OrdinalIgnoreCase))
                    return wk;
            }

            // 4. Wildcard mappings (prefix match)
            AdGroupRoleMapping? bestWildcard = null;
            foreach (var wc in _wildcardMappings)
            {
                if (group.Contains(wc.AdGroupIdentifier, StringComparison.OrdinalIgnoreCase))
                {
                    if (bestWildcard is null || wc.Priority > bestWildcard.Priority)
                        bestWildcard = wc;
                }
            }

            return bestWildcard;
        }

        /// <summary>
        /// Gets the numeric precedence for a role name.
        /// Unknown roles receive precedence -1 (below viewer).
        /// </summary>
        private static int GetRolePrecedence(string role)
        {
            return RolePrecedence.TryGetValue(role, out var precedence) ? precedence : -1;
        }
    }
}
