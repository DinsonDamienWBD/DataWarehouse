using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.Core
{
    /// <summary>
    /// Access Control List (ACL) strategy implementation.
    /// Per-resource ACL entries with allow/deny rules and inheritance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ACL features:
    /// - Per-resource ACL entries (allow/deny per principal)
    /// - ACL inheritance from parent resources
    /// - Deny-takes-precedence evaluation
    /// - Principal groups and wildcards
    /// </para>
    /// </remarks>
    public sealed class AclStrategy : AccessControlStrategyBase
    {
        private readonly ConcurrentDictionary<string, AccessControlList> _resourceAcls = new();

        /// <inheritdoc/>
        public override string StrategyId => "acl";

        /// <inheritdoc/>
        public override string StrategyName => "Access Control Lists";

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
            // Load ACLs from configuration
            if (configuration.TryGetValue("ResourceAcls", out var aclsObj) &&
                aclsObj is IEnumerable<Dictionary<string, object>> aclConfigs)
            {
                foreach (var config in aclConfigs)
                {
                    var resourceId = config["ResourceId"]?.ToString() ?? "";
                    if (string.IsNullOrEmpty(resourceId))
                        continue;

                    var acl = GetOrCreateAcl(resourceId);

                    if (config.TryGetValue("Entries", out var entriesObj) &&
                        entriesObj is IEnumerable<Dictionary<string, object>> entryConfigs)
                    {
                        foreach (var entryConfig in entryConfigs)
                        {
                            var principalId = entryConfig["PrincipalId"]?.ToString() ?? "";
                            var actionStr = entryConfig["Action"]?.ToString() ?? "";
                            var typeStr = entryConfig["Type"]?.ToString() ?? "Allow";

                            if (!string.IsNullOrEmpty(principalId) && !string.IsNullOrEmpty(actionStr))
                            {
                                var type = Enum.TryParse<AclEntryType>(typeStr, out var t) ? t : AclEntryType.Allow;
                                acl.AddEntry(new AclEntry
                                {
                                    PrincipalId = principalId,
                                    Action = actionStr,
                                    Type = type
                                });
                            }
                        }
                    }
                }
            }

            return base.InitializeAsync(configuration, cancellationToken);
        }

        /// <summary>
        /// Gets or creates an ACL for a resource.
        /// </summary>
        public AccessControlList GetOrCreateAcl(string resourceId)
        {
            return _resourceAcls.GetOrAdd(resourceId, _ => new AccessControlList { ResourceId = resourceId });
        }

        /// <summary>
        /// Adds an ACL entry for a resource.
        /// </summary>
        public void AddAclEntry(string resourceId, string principalId, string action, AclEntryType type)
        {
            var acl = GetOrCreateAcl(resourceId);
            acl.AddEntry(new AclEntry
            {
                PrincipalId = principalId,
                Action = action,
                Type = type
            });
        }

        /// <summary>
        /// Removes an ACL entry from a resource.
        /// </summary>
        public bool RemoveAclEntry(string resourceId, string principalId, string action)
        {
            if (_resourceAcls.TryGetValue(resourceId, out var acl))
            {
                return acl.RemoveEntry(principalId, action);
            }

            return false;
        }

        /// <summary>
        /// Sets whether ACL inheritance is enabled for a resource.
        /// </summary>
        public void SetAclInheritance(string resourceId, bool enabled)
        {
            var acl = GetOrCreateAcl(resourceId);
            acl.InheritFromParent = enabled;
        }

        /// <inheritdoc/>
        protected override Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            var resourceId = context.ResourceId;
            var subjectId = context.SubjectId;
            var action = context.Action;

            // Collect ACL entries (from current resource and inherited)
            var allEntries = new List<AclEntry>();
            CollectAclEntries(resourceId, allEntries);

            if (!allEntries.Any())
            {
                return Task.FromResult(new AccessDecision
                {
                    IsGranted = false,
                    Reason = "ACL denied: no ACL entries configured for resource",
                    ApplicablePolicies = new[] { "ACL.NoEntries" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["Subject"] = subjectId,
                        ["Resource"] = resourceId,
                        ["Action"] = action
                    }
                });
            }

            // Evaluate entries - deny takes precedence
            var denyEntries = new List<AclEntry>();
            var allowEntries = new List<AclEntry>();

            foreach (var entry in allEntries)
            {
                // Check if entry applies to this principal
                if (!IsPrincipalMatch(entry.PrincipalId, subjectId, context.Roles))
                    continue;

                // Check if entry applies to this action
                if (!IsActionMatch(entry.Action, action))
                    continue;

                if (entry.Type == AclEntryType.Deny)
                    denyEntries.Add(entry);
                else
                    allowEntries.Add(entry);
            }

            // Deny takes precedence
            if (denyEntries.Any())
            {
                return Task.FromResult(new AccessDecision
                {
                    IsGranted = false,
                    Reason = $"ACL denied: explicit deny entry for action '{action}'",
                    ApplicablePolicies = new[] { "ACL.ExplicitDeny" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["Subject"] = subjectId,
                        ["Resource"] = resourceId,
                        ["Action"] = action,
                        ["DenyEntries"] = denyEntries.Count
                    }
                });
            }

            // Check for allow entries
            if (allowEntries.Any())
            {
                return Task.FromResult(new AccessDecision
                {
                    IsGranted = true,
                    Reason = $"ACL granted: allow entry found for action '{action}'",
                    ApplicablePolicies = new[] { "ACL.Allow" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["Subject"] = subjectId,
                        ["Resource"] = resourceId,
                        ["Action"] = action,
                        ["AllowEntries"] = allowEntries.Count
                    }
                });
            }

            // No matching entries - default deny
            return Task.FromResult(new AccessDecision
            {
                IsGranted = false,
                Reason = "ACL denied: no matching allow entry found (default deny)",
                ApplicablePolicies = new[] { "ACL.DefaultDeny" },
                Metadata = new Dictionary<string, object>
                {
                    ["Subject"] = subjectId,
                    ["Resource"] = resourceId,
                    ["Action"] = action,
                    ["TotalEntries"] = allEntries.Count
                }
            });
        }

        private void CollectAclEntries(string resourceId, List<AclEntry> entries)
        {
            // Get direct ACL
            if (_resourceAcls.TryGetValue(resourceId, out var acl))
            {
                entries.AddRange(acl.Entries);

                // Check for inheritance
                if (acl.InheritFromParent)
                {
                    var parentPath = GetParentPath(resourceId);
                    if (parentPath != null)
                    {
                        CollectAclEntries(parentPath, entries);
                    }
                }
            }
            else
            {
                // Try to find parent ACL
                var parentPath = GetParentPath(resourceId);
                if (parentPath != null)
                {
                    CollectAclEntries(parentPath, entries);
                }
            }
        }

        private static string? GetParentPath(string path)
        {
            var lastSlash = path.LastIndexOf('/');
            if (lastSlash > 0)
            {
                return path.Substring(0, lastSlash);
            }

            return null;
        }

        private static bool IsPrincipalMatch(string aclPrincipal, string subjectId, IReadOnlyList<string> roles)
        {
            // Wildcard match
            if (aclPrincipal == "*")
                return true;

            // Exact subject match
            if (aclPrincipal.Equals(subjectId, StringComparison.OrdinalIgnoreCase))
                return true;

            // Role match
            if (roles.Any(r => r.Equals(aclPrincipal, StringComparison.OrdinalIgnoreCase)))
                return true;

            return false;
        }

        private static bool IsActionMatch(string aclAction, string requestedAction)
        {
            // Wildcard match
            if (aclAction == "*")
                return true;

            // Exact match
            if (aclAction.Equals(requestedAction, StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }
    }

    /// <summary>
    /// Access Control List for a resource.
    /// </summary>
    public sealed class AccessControlList
    {
        private readonly List<AclEntry> _entries = new();
        private readonly object _lock = new();

        public required string ResourceId { get; init; }
        public bool InheritFromParent { get; set; } = true;

        public IReadOnlyList<AclEntry> Entries
        {
            get
            {
                lock (_lock)
                {
                    return _entries.ToList().AsReadOnly();
                }
            }
        }

        public void AddEntry(AclEntry entry)
        {
            lock (_lock)
            {
                _entries.Add(entry);
            }
        }

        public bool RemoveEntry(string principalId, string action)
        {
            lock (_lock)
            {
                return _entries.RemoveAll(e =>
                    e.PrincipalId.Equals(principalId, StringComparison.OrdinalIgnoreCase) &&
                    e.Action.Equals(action, StringComparison.OrdinalIgnoreCase)) > 0;
            }
        }

        public void ClearEntries()
        {
            lock (_lock)
            {
                _entries.Clear();
            }
        }
    }

    /// <summary>
    /// ACL entry.
    /// </summary>
    public sealed record AclEntry
    {
        public required string PrincipalId { get; init; }
        public required string Action { get; init; }
        public required AclEntryType Type { get; init; }
        public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    }

    /// <summary>
    /// ACL entry type.
    /// </summary>
    public enum AclEntryType
    {
        Allow,
        Deny
    }
}
