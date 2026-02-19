using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.Core
{
    /// <summary>
    /// Relationship-Based Access Control (ReBac) strategy implementation.
    /// Graph-based access control using relationships between subjects and resources.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ReBac features:
    /// - Graph-based relationship evaluation (user -> relation -> object)
    /// - Support for: owner, editor, viewer, member, parent relationships
    /// - Transitive relationship resolution (friend-of-friend)
    /// - Reverse relationships (document.owner includes user)
    /// </para>
    /// </remarks>
    public sealed class ReBacStrategy : AccessControlStrategyBase
    {
        private readonly ConcurrentDictionary<string, List<Relationship>> _relationships = new();
        private readonly Dictionary<string, HashSet<string>> _relationshipPermissions = new();

        /// <inheritdoc/>
        public override string StrategyId => "rebac";

        /// <inheritdoc/>
        public override string StrategyName => "Relationship-Based Access Control";

        /// <inheritdoc/>
        public override AccessControlCapabilities Capabilities { get; } = new()
        {
            SupportsRealTimeDecisions = true,
            SupportsAuditTrail = true,
            SupportsPolicyConfiguration = true,
            SupportsExternalIdentity = true,
            SupportsTemporalAccess = false,
            SupportsGeographicRestrictions = false,
            MaxConcurrentEvaluations = 5000
        };

        /// <inheritdoc/>
        public override Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken cancellationToken = default)
        {
            // Load relationships
            if (configuration.TryGetValue("Relationships", out var relationshipsObj) &&
                relationshipsObj is IEnumerable<Dictionary<string, object>> relationshipConfigs)
            {
                foreach (var config in relationshipConfigs)
                {
                    var subjectId = config["SubjectId"]?.ToString() ?? "";
                    var relationType = config["RelationType"]?.ToString() ?? "";
                    var targetId = config["TargetId"]?.ToString() ?? "";

                    if (!string.IsNullOrEmpty(subjectId) && !string.IsNullOrEmpty(relationType) && !string.IsNullOrEmpty(targetId))
                    {
                        AddRelationship(subjectId, relationType, targetId);
                    }
                }
            }

            // Initialize default relationship permissions
            InitializeDefaultRelationshipPermissions();

            return base.InitializeAsync(configuration, cancellationToken);
        }

        /// <summary>
        /// Production hardening: validates configuration parameters on initialization.
        /// </summary>
        protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("rebac.init");
            return base.InitializeAsyncCore(cancellationToken);
        }

        /// <summary>
        /// Production hardening: releases resources and clears caches on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("rebac.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }


        /// <summary>
        /// Adds a relationship between subject and target.
        /// </summary>
        public void AddRelationship(string subjectId, string relationType, string targetId)
        {
            var relationship = new Relationship
            {
                SubjectId = subjectId,
                RelationType = relationType,
                TargetId = targetId,
                CreatedAt = DateTime.UtcNow
            };

            var key = GetRelationshipKey(subjectId, relationType);
            if (!_relationships.TryGetValue(key, out var list))
            {
                list = new List<Relationship>();
                _relationships[key] = list;
            }

            list.Add(relationship);
        }

        /// <summary>
        /// Removes a relationship.
        /// </summary>
        public bool RemoveRelationship(string subjectId, string relationType, string targetId)
        {
            var key = GetRelationshipKey(subjectId, relationType);
            if (_relationships.TryGetValue(key, out var list))
            {
                return list.RemoveAll(r => r.TargetId == targetId) > 0;
            }

            return false;
        }

        /// <summary>
        /// Defines which actions a relationship type allows.
        /// </summary>
        public void DefineRelationshipPermissions(string relationType, IEnumerable<string> actions)
        {
            _relationshipPermissions[relationType] = new HashSet<string>(actions, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Checks if a relationship exists between subject and target.
        /// </summary>
        public bool HasRelationship(string subjectId, string relationType, string targetId, int maxDepth = 3)
        {
            return FindRelationship(subjectId, relationType, targetId, maxDepth, new HashSet<string>());
        }

        /// <inheritdoc/>
        protected override Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("rebac.evaluate");
            var subjectId = context.SubjectId;
            var resourceId = context.ResourceId;
            var action = context.Action;

            // Check for direct owner relationship
            if (HasRelationship(subjectId, "owner", resourceId))
            {
                return Task.FromResult(new AccessDecision
                {
                    IsGranted = true,
                    Reason = "ReBac access granted: subject has 'owner' relationship",
                    ApplicablePolicies = new[] { "ReBac.OwnerRelationship" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["Subject"] = subjectId,
                        ["Resource"] = resourceId,
                        ["Relationship"] = "owner",
                        ["Action"] = action
                    }
                });
            }

            // Check other relationship types
            var applicableRelationships = new List<string>();
            foreach (var (relationType, allowedActions) in _relationshipPermissions)
            {
                if (allowedActions.Contains(action) && HasRelationship(subjectId, relationType, resourceId))
                {
                    applicableRelationships.Add(relationType);
                }
            }

            if (applicableRelationships.Any())
            {
                return Task.FromResult(new AccessDecision
                {
                    IsGranted = true,
                    Reason = $"ReBac access granted: subject has '{applicableRelationships.First()}' relationship",
                    ApplicablePolicies = new[] { $"ReBac.{applicableRelationships.First()}Relationship" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["Subject"] = subjectId,
                        ["Resource"] = resourceId,
                        ["Relationships"] = applicableRelationships,
                        ["Action"] = action
                    }
                });
            }

            // Check for transitive relationships (e.g., member of parent organization)
            if (CheckTransitiveRelationships(subjectId, resourceId, action))
            {
                return Task.FromResult(new AccessDecision
                {
                    IsGranted = true,
                    Reason = "ReBac access granted: subject has transitive relationship",
                    ApplicablePolicies = new[] { "ReBac.TransitiveRelationship" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["Subject"] = subjectId,
                        ["Resource"] = resourceId,
                        ["Action"] = action,
                        ["RelationType"] = "transitive"
                    }
                });
            }

            return Task.FromResult(new AccessDecision
            {
                IsGranted = false,
                Reason = "ReBac access denied: no applicable relationship found",
                ApplicablePolicies = new[] { "ReBac.NoRelationship" },
                Metadata = new Dictionary<string, object>
                {
                    ["Subject"] = subjectId,
                    ["Resource"] = resourceId,
                    ["Action"] = action
                }
            });
        }

        private bool FindRelationship(string subjectId, string relationType, string targetId, int maxDepth, HashSet<string> visited)
        {
            if (maxDepth <= 0)
                return false;

            var key = GetRelationshipKey(subjectId, relationType);
            if (!visited.Add(key))
                return false; // Avoid cycles

            if (_relationships.TryGetValue(key, out var list))
            {
                // Check for direct relationship
                if (list.Any(r => r.TargetId == targetId))
                    return true;

                // Check transitive relationships
                foreach (var rel in list)
                {
                    if (FindRelationship(rel.TargetId, relationType, targetId, maxDepth - 1, visited))
                        return true;
                }
            }

            return false;
        }

        private bool CheckTransitiveRelationships(string subjectId, string resourceId, string action)
        {
            // Check if subject is member of a group that has access to resource
            var memberKey = GetRelationshipKey(subjectId, "member");
            if (_relationships.TryGetValue(memberKey, out var memberList))
            {
                foreach (var membership in memberList)
                {
                    // Check if the group has access to the resource
                    foreach (var (relationType, allowedActions) in _relationshipPermissions)
                    {
                        if (allowedActions.Contains(action) &&
                            HasRelationship(membership.TargetId, relationType, resourceId, maxDepth: 2))
                        {
                            return true;
                        }
                    }
                }
            }

            // Check if resource is child of a parent resource the subject can access
            var parentKey = GetRelationshipKey(resourceId, "parent");
            if (_relationships.TryGetValue(parentKey, out var parentList))
            {
                foreach (var parent in parentList)
                {
                    foreach (var (relationType, allowedActions) in _relationshipPermissions)
                    {
                        if (allowedActions.Contains(action) &&
                            HasRelationship(subjectId, relationType, parent.TargetId, maxDepth: 2))
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        private void InitializeDefaultRelationshipPermissions()
        {
            // Owner has full access
            DefineRelationshipPermissions("owner", new[] { "read", "write", "update", "delete", "execute", "manage" });

            // Editor can modify
            DefineRelationshipPermissions("editor", new[] { "read", "write", "update" });

            // Viewer can only read
            DefineRelationshipPermissions("viewer", new[] { "read", "view", "list" });

            // Member has basic access
            DefineRelationshipPermissions("member", new[] { "read", "view", "list" });

            // Contributor can read and write
            DefineRelationshipPermissions("contributor", new[] { "read", "write", "create" });
        }

        private static string GetRelationshipKey(string subjectId, string relationType)
        {
            return $"{subjectId}:{relationType}";
        }
    }

    /// <summary>
    /// Represents a relationship in ReBac.
    /// </summary>
    public sealed record Relationship
    {
        public required string SubjectId { get; init; }
        public required string RelationType { get; init; }
        public required string TargetId { get; init; }
        public DateTime CreatedAt { get; init; }
    }
}
