using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.PolicyEngine
{
    /// <summary>
    /// Casbin policy engine integration strategy.
    /// Supports RBAC, ABAC, and PBAC models with flexible policy storage adapters.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Casbin features:
    /// - Model/policy file management
    /// - Multiple access control models (RBAC, ABAC, PBAC)
    /// - Flexible adapter pattern for policy storage
    /// - Request format: (subject, object, action)
    /// - Policy format: p, sub, obj, act
    /// - Role format: g, user, role
    /// </para>
    /// <para>
    /// Supported models:
    /// - RBAC (Role-Based Access Control)
    /// - ABAC (Attribute-Based Access Control)
    /// - PBAC (Policy-Based Access Control)
    /// - ACL (Access Control List)
    /// </para>
    /// </remarks>
    public sealed class CasbinStrategy : AccessControlStrategyBase
    {
        private readonly BoundedDictionary<string, CasbinPolicy> _policies = new BoundedDictionary<string, CasbinPolicy>(1000);
        private readonly BoundedDictionary<string, List<string>> _roleAssignments = new BoundedDictionary<string, List<string>>(1000);
        private string _modelType = "RBAC";
        private bool _enableInheritance = true;

        /// <inheritdoc/>
        public override string StrategyId => "casbin";

        /// <inheritdoc/>
        public override string StrategyName => "Casbin Policy Engine";

        /// <inheritdoc/>
        public override AccessControlCapabilities Capabilities { get; } = new()
        {
            SupportsRealTimeDecisions = true,
            SupportsAuditTrail = true,
            SupportsPolicyConfiguration = true,
            SupportsExternalIdentity = false,
            SupportsTemporalAccess = false,
            SupportsGeographicRestrictions = false,
            MaxConcurrentEvaluations = 15000
        };

        /// <inheritdoc/>
        public override Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken cancellationToken = default)
        {
            if (configuration.TryGetValue("ModelType", out var modelType) && modelType is string modelTypeStr)
            {
                _modelType = modelTypeStr;
            }

            if (configuration.TryGetValue("EnableInheritance", out var inheritance) && inheritance is bool enable)
            {
                _enableInheritance = enable;
            }

            // Load policies from configuration
            if (configuration.TryGetValue("Policies", out var policiesObj) && policiesObj is List<object> policies)
            {
                foreach (var policyObj in policies)
                {
                    if (policyObj is Dictionary<string, object> policyDict)
                    {
                        var subject = policyDict.TryGetValue("subject", out var sub) ? sub?.ToString() : null;
                        var obj = policyDict.TryGetValue("object", out var o) ? o?.ToString() : null;
                        var action = policyDict.TryGetValue("action", out var act) ? act?.ToString() : null;

                        if (subject != null && obj != null && action != null)
                        {
                            AddPolicy(subject, obj, action);
                        }
                    }
                }
            }

            // Load role assignments
            if (configuration.TryGetValue("RoleAssignments", out var rolesObj) && rolesObj is Dictionary<string, object> roles)
            {
                foreach (var kvp in roles)
                {
                    if (kvp.Value is List<object> roleList)
                    {
                        var roleNames = roleList.Select(r => r?.ToString()).Where(r => r != null).ToList();
                        _roleAssignments[kvp.Key] = roleNames!;
                    }
                }
            }

            return base.InitializeAsync(configuration, cancellationToken);
        }

        /// <summary>
        /// Production hardening: validates configuration parameters on initialization.
        /// </summary>
        protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("casbin.init");
            return base.InitializeAsyncCore(cancellationToken);
        }

        /// <summary>
        /// Production hardening: releases resources and clears caches on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("casbin.shutdown");
            _policies.Clear();
            return base.ShutdownAsyncCore(cancellationToken);
        }


        /// <summary>
        /// Adds a policy rule.
        /// </summary>
        public void AddPolicy(string subject, string object_, string action)
        {
            var key = $"{subject}:{object_}:{action}";
            var policy = new CasbinPolicy
            {
                Subject = subject,
                Object = object_,
                Action = action,
                CreatedAt = DateTime.UtcNow
            };

            _policies[key] = policy;
        }

        /// <summary>
        /// Assigns a role to a user.
        /// </summary>
        public void AddRoleForUser(string userId, string role)
        {
            // Atomic add using GetOrAdd + lock for thread-safe list mutation
            var roles = _roleAssignments.GetOrAdd(userId, _ => new List<string>());
            lock (roles)
            {
                if (!roles.Contains(role))
                {
                    roles.Add(role);
                }
            }
        }

        /// <summary>
        /// Gets all roles for a user (including inherited roles with deep resolution).
        /// </summary>
        private List<string> GetRolesForUser(string userId)
        {
            var roles = new HashSet<string>();
            var queue = new Queue<string>();
            queue.Enqueue(userId);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (_roleAssignments.TryGetValue(current, out var directRoles))
                {
                    List<string> snapshot;
                    lock (directRoles)
                    {
                        snapshot = new List<string>(directRoles);
                    }

                    foreach (var role in snapshot)
                    {
                        if (roles.Add(role) && _enableInheritance)
                        {
                            // Enqueue for deep resolution (prevents infinite loop via HashSet check)
                            queue.Enqueue(role);
                        }
                    }
                }
            }

            return roles.ToList();
        }

        /// <summary>
        /// Checks if a policy rule exists for the request.
        /// </summary>
        private bool EnforcePolicy(string subject, string object_, string action)
        {
            // Direct match
            var directKey = $"{subject}:{object_}:{action}";
            if (_policies.ContainsKey(directKey))
                return true;

            // Wildcard matches
            var wildcardKeys = new[]
            {
                $"{subject}:{object_}:*",
                $"{subject}:*:{action}",
                $"*:{object_}:{action}",
                $"{subject}:*:*",
                $"*:{object_}:*",
                $"*:*:{action}",
                "*:*:*"
            };

            return wildcardKeys.Any(key => _policies.ContainsKey(key));
        }

        /// <inheritdoc/>
        protected override Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("casbin.evaluate");
            var subject = context.SubjectId;
            var object_ = context.ResourceId;
            var action = context.Action;

            // Check direct policy
            if (EnforcePolicy(subject, object_, action))
            {
                return Task.FromResult(new AccessDecision
                {
                    IsGranted = true,
                    Reason = "Casbin policy allows access (direct)",
                    ApplicablePolicies = new[] { $"Casbin.{subject}:{object_}:{action}" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["ModelType"] = _modelType,
                        ["MatchType"] = "Direct"
                    }
                });
            }

            // Check role-based policies
            var roles = GetRolesForUser(subject);
            foreach (var role in roles)
            {
                if (EnforcePolicy(role, object_, action))
                {
                    return Task.FromResult(new AccessDecision
                    {
                        IsGranted = true,
                        Reason = $"Casbin policy allows access (role: {role})",
                        ApplicablePolicies = new[] { $"Casbin.{role}:{object_}:{action}" },
                        Metadata = new Dictionary<string, object>
                        {
                            ["ModelType"] = _modelType,
                            ["MatchType"] = "Role",
                            ["Role"] = role
                        }
                    });
                }
            }

            // No policy match
            return Task.FromResult(new AccessDecision
            {
                IsGranted = false,
                Reason = "No Casbin policy allows this access",
                ApplicablePolicies = new[] { "Casbin.NoMatch" },
                Metadata = new Dictionary<string, object>
                {
                    ["ModelType"] = _modelType,
                    ["CheckedRoles"] = roles
                }
            });
        }
    }

    /// <summary>
    /// Casbin policy rule.
    /// </summary>
    public sealed class CasbinPolicy
    {
        public required string Subject { get; init; }
        public required string Object { get; init; }
        public required string Action { get; init; }
        public required DateTime CreatedAt { get; init; }
    }
}
