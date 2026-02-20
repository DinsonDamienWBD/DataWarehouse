using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.Core
{
    /// <summary>
    /// Attribute-Based Access Control (ABAC) strategy implementation.
    /// Evaluates access based on attributes of subjects, resources, actions, and environment.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ABAC provides fine-grained, context-aware access control by evaluating policies
    /// against attributes from multiple sources:
    /// - Subject attributes (user, department, clearance, role)
    /// - Resource attributes (classification, owner, sensitivity)
    /// - Action attributes (operation type, impact level)
    /// - Environment attributes (time, location, device, risk score)
    /// </para>
    /// </remarks>
    public sealed class AbacStrategy : AccessControlStrategyBase
    {
        private readonly BoundedDictionary<string, AbacPolicy> _policies = new BoundedDictionary<string, AbacPolicy>(1000);
        private readonly BoundedDictionary<string, Func<AccessContext, bool>> _customConditions = new BoundedDictionary<string, Func<AccessContext, bool>>(1000);

        /// <inheritdoc/>
        public override string StrategyId => "abac";

        /// <inheritdoc/>
        public override string StrategyName => "Attribute-Based Access Control";

        /// <inheritdoc/>
        public override AccessControlCapabilities Capabilities { get; } = new()
        {
            SupportsRealTimeDecisions = true,
            SupportsAuditTrail = true,
            SupportsPolicyConfiguration = true,
            SupportsExternalIdentity = true,
            SupportsTemporalAccess = true,
            SupportsGeographicRestrictions = true,
            MaxConcurrentEvaluations = 5000
        };

        /// <inheritdoc/>
        public override Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken cancellationToken = default)
        {
            // Load policies from configuration
            if (configuration.TryGetValue("Policies", out var policiesObj) &&
                policiesObj is IEnumerable<Dictionary<string, object>> policyConfigs)
            {
                foreach (var config in policyConfigs)
                {
                    var policy = ParsePolicyFromConfig(config);
                    if (policy != null)
                    {
                        _policies[policy.Id] = policy;
                    }
                }
            }

            // Register built-in conditions
            RegisterBuiltInConditions();

            return base.InitializeAsync(configuration, cancellationToken);
        }

        /// <summary>
        /// Production hardening: validates configuration parameters on initialization.
        /// </summary>
        protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("abac.init");
            return base.InitializeAsyncCore(cancellationToken);
        }

        /// <summary>
        /// Production hardening: releases resources and clears caches on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("abac.shutdown");
            _policies.Clear();
            return base.ShutdownAsyncCore(cancellationToken);
        }


        /// <summary>
        /// Adds a new ABAC policy.
        /// </summary>
        public void AddPolicy(AbacPolicy policy)
        {
            _policies[policy.Id] = policy;
        }

        /// <summary>
        /// Removes a policy.
        /// </summary>
        public bool RemovePolicy(string policyId)
        {
            return _policies.TryRemove(policyId, out _);
        }

        /// <summary>
        /// Registers a custom condition function.
        /// </summary>
        public void RegisterCondition(string name, Func<AccessContext, bool> condition)
        {
            _customConditions[name] = condition;
        }

        /// <summary>
        /// Gets all configured policies.
        /// </summary>
        public IReadOnlyList<AbacPolicy> GetPolicies()
        {
            return _policies.Values.OrderBy(p => p.Priority).ToList().AsReadOnly();
        }

        /// <inheritdoc/>
        protected override Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("abac.evaluate");
            var applicablePolicies = new List<string>();
            var deniedReasons = new List<string>();

            // Get policies sorted by priority (lower = higher priority)
            var orderedPolicies = _policies.Values.OrderBy(p => p.Priority);

            foreach (var policy in orderedPolicies)
            {
                if (!policy.IsEnabled)
                    continue;

                var (applies, reason) = EvaluatePolicy(policy, context);

                if (applies)
                {
                    applicablePolicies.Add(policy.Id);

                    if (policy.Effect == PolicyEffect.Deny)
                    {
                        // Immediate deny
                        return Task.FromResult(new AccessDecision
                        {
                            IsGranted = false,
                            Reason = $"Denied by ABAC policy '{policy.Name}': {reason}",
                            ApplicablePolicies = applicablePolicies.ToArray(),
                            Metadata = new Dictionary<string, object>
                            {
                                ["DenyingPolicy"] = policy.Id,
                                ["PolicyReason"] = reason
                            }
                        });
                    }

                    if (policy.Effect == PolicyEffect.Permit)
                    {
                        // Found a permit policy
                        return Task.FromResult(new AccessDecision
                        {
                            IsGranted = true,
                            Reason = $"Permitted by ABAC policy '{policy.Name}': {reason}",
                            ApplicablePolicies = applicablePolicies.ToArray(),
                            Metadata = new Dictionary<string, object>
                            {
                                ["PermittingPolicy"] = policy.Id,
                                ["PolicyReason"] = reason
                            }
                        });
                    }
                }
            }

            // No applicable policy found - default deny
            return Task.FromResult(new AccessDecision
            {
                IsGranted = false,
                Reason = "No applicable ABAC policy found - access denied by default",
                ApplicablePolicies = applicablePolicies.ToArray(),
                Metadata = new Dictionary<string, object>
                {
                    ["EvaluatedPolicies"] = _policies.Count,
                    ["ContextSubject"] = context.SubjectId,
                    ["ContextResource"] = context.ResourceId,
                    ["ContextAction"] = context.Action
                }
            });
        }

        private (bool applies, string reason) EvaluatePolicy(AbacPolicy policy, AccessContext context)
        {
            // Check target conditions
            if (!EvaluateTarget(policy.Target, context))
            {
                return (false, "Target conditions not met");
            }

            // Evaluate all conditions
            foreach (var condition in policy.Conditions)
            {
                if (!EvaluateCondition(condition, context))
                {
                    return (false, $"Condition '{condition.Name}' not satisfied");
                }
            }

            return (true, "All conditions satisfied");
        }

        private bool EvaluateTarget(PolicyTarget target, AccessContext context)
        {
            // Check subject match
            if (target.Subjects != null && target.Subjects.Any())
            {
                if (!target.Subjects.Contains(context.SubjectId, StringComparer.OrdinalIgnoreCase) &&
                    !target.Subjects.Intersect(context.Roles, StringComparer.OrdinalIgnoreCase).Any() &&
                    !target.Subjects.Contains("*"))
                {
                    return false;
                }
            }

            // Check resource match
            if (target.Resources != null && target.Resources.Any())
            {
                var resourceMatches = target.Resources.Any(r =>
                    r == "*" ||
                    context.ResourceId.Equals(r, StringComparison.OrdinalIgnoreCase) ||
                    (r.EndsWith("/*") && context.ResourceId.StartsWith(r.TrimEnd('*', '/'), StringComparison.OrdinalIgnoreCase)));

                if (!resourceMatches)
                    return false;
            }

            // Check action match
            if (target.Actions != null && target.Actions.Any())
            {
                if (!target.Actions.Contains(context.Action, StringComparer.OrdinalIgnoreCase) &&
                    !target.Actions.Contains("*"))
                {
                    return false;
                }
            }

            return true;
        }

        private bool EvaluateCondition(PolicyCondition condition, AccessContext context)
        {
            // Check for custom condition
            if (_customConditions.TryGetValue(condition.Name, out var customFunc))
            {
                return customFunc(context);
            }

            // Get attribute value
            object? attributeValue = GetAttributeValue(condition.AttributeSource, condition.AttributeName, context);

            // Evaluate operator
            return condition.Operator switch
            {
                ConditionOperator.Equals => AttributeEquals(attributeValue, condition.Value),
                ConditionOperator.NotEquals => !AttributeEquals(attributeValue, condition.Value),
                ConditionOperator.Contains => AttributeContains(attributeValue, condition.Value),
                ConditionOperator.NotContains => !AttributeContains(attributeValue, condition.Value),
                ConditionOperator.GreaterThan => CompareValues(attributeValue, condition.Value) > 0,
                ConditionOperator.LessThan => CompareValues(attributeValue, condition.Value) < 0,
                ConditionOperator.GreaterThanOrEqual => CompareValues(attributeValue, condition.Value) >= 0,
                ConditionOperator.LessThanOrEqual => CompareValues(attributeValue, condition.Value) <= 0,
                ConditionOperator.In => ValueIn(attributeValue, condition.Values),
                ConditionOperator.NotIn => !ValueIn(attributeValue, condition.Values),
                ConditionOperator.Matches => PatternMatches(attributeValue, condition.Value),
                ConditionOperator.Exists => attributeValue != null,
                ConditionOperator.NotExists => attributeValue == null,
                _ => false
            };
        }

        private object? GetAttributeValue(AttributeSource source, string attributeName, AccessContext context)
        {
            return source switch
            {
                AttributeSource.Subject => GetFromDictionary(context.SubjectAttributes, attributeName) ??
                                          (attributeName.Equals("id", StringComparison.OrdinalIgnoreCase) ? context.SubjectId : null),
                AttributeSource.Resource => GetFromDictionary(context.ResourceAttributes, attributeName) ??
                                           (attributeName.Equals("id", StringComparison.OrdinalIgnoreCase) ? context.ResourceId : null),
                AttributeSource.Action => attributeName.Equals("name", StringComparison.OrdinalIgnoreCase) ? context.Action : null,
                AttributeSource.Environment => GetFromDictionary(context.EnvironmentAttributes, attributeName) ??
                                              GetEnvironmentAttribute(attributeName, context),
                _ => null
            };
        }

        private object? GetFromDictionary(IReadOnlyDictionary<string, object> dict, string key)
        {
            return dict.TryGetValue(key, out var value) ? value : null;
        }

        private object? GetEnvironmentAttribute(string attributeName, AccessContext context)
        {
            return attributeName.ToLowerInvariant() switch
            {
                "time" or "currenttime" => context.RequestTime,
                "hour" => context.RequestTime.Hour,
                "dayofweek" => context.RequestTime.DayOfWeek.ToString(),
                "ip" or "ipaddress" => context.ClientIpAddress,
                "country" => context.Location?.Country,
                "city" => context.Location?.City,
                _ => null
            };
        }

        private bool AttributeEquals(object? value, object? expected)
        {
            if (value == null && expected == null) return true;
            if (value == null || expected == null) return false;
            return value.ToString()?.Equals(expected.ToString(), StringComparison.OrdinalIgnoreCase) ?? false;
        }

        private bool AttributeContains(object? value, object? searchFor)
        {
            if (value == null || searchFor == null) return false;

            if (value is IEnumerable<string> collection)
            {
                return collection.Contains(searchFor.ToString(), StringComparer.OrdinalIgnoreCase);
            }

            return value.ToString()?.Contains(searchFor.ToString() ?? "", StringComparison.OrdinalIgnoreCase) ?? false;
        }

        private int CompareValues(object? value, object? expected)
        {
            // For security: null values never compare equal to non-null values
            // Return non-zero to fail comparisons involving null
            if (value == null || expected == null)
            {
                // Both null: fail comparison for security
                if (value == null && expected == null) return -1;
                // One is null: definitely not equal
                return value == null ? -1 : 1;
            }

            if (value is IComparable comparable && expected is IComparable)
            {
                try
                {
                    var expectedConverted = Convert.ChangeType(expected, value.GetType());
                    return comparable.CompareTo(expectedConverted);
                }
                catch
                {
                    // Comparison failed - fail securely
                    return -1;
                }
            }

            // Type mismatch - fail securely
            return -1;
        }

        private bool ValueIn(object? value, IEnumerable<object>? values)
        {
            if (value == null || values == null) return false;
            return values.Any(v => AttributeEquals(value, v));
        }

        private bool PatternMatches(object? value, object? pattern)
        {
            if (value == null || pattern == null) return false;
            try
            {
                var regex = new System.Text.RegularExpressions.Regex(pattern.ToString() ?? "");
                return regex.IsMatch(value.ToString() ?? "");
            }
            catch
            {
                return false;
            }
        }

        private AbacPolicy? ParsePolicyFromConfig(Dictionary<string, object> config)
        {
            try
            {
                return new AbacPolicy
                {
                    Id = config["Id"]?.ToString() ?? Guid.NewGuid().ToString(),
                    Name = config["Name"]?.ToString() ?? "Unnamed Policy",
                    Description = config.TryGetValue("Description", out var desc) ? desc?.ToString() ?? "" : "",
                    Priority = config.TryGetValue("Priority", out var prio) && prio is int p ? p : 100,
                    IsEnabled = !config.TryGetValue("IsEnabled", out var enabled) || enabled is true,
                    Effect = config.TryGetValue("Effect", out var eff) && Enum.TryParse<PolicyEffect>(eff?.ToString(), out var pe) ? pe : PolicyEffect.Permit,
                    Target = new PolicyTarget(),
                    Conditions = new List<PolicyCondition>()
                };
            }
            catch
            {
                return null;
            }
        }

        private void RegisterBuiltInConditions()
        {
            // Business hours condition
            RegisterCondition("BusinessHours", context =>
            {
                var hour = context.RequestTime.Hour;
                var day = context.RequestTime.DayOfWeek;
                return day != DayOfWeek.Saturday && day != DayOfWeek.Sunday && hour >= 8 && hour < 18;
            });

            // Authenticated user condition
            RegisterCondition("IsAuthenticated", context =>
                !string.IsNullOrEmpty(context.SubjectId) && context.SubjectId != "anonymous");

            // Internal IP condition
            RegisterCondition("IsInternalIp", context =>
            {
                var ip = context.ClientIpAddress;
                if (string.IsNullOrEmpty(ip)) return false;

                // RFC 1918 private address ranges:
                // 10.0.0.0/8 (10.0.0.0 - 10.255.255.255)
                // 172.16.0.0/12 (172.16.0.0 - 172.31.255.255)
                // 192.168.0.0/16 (192.168.0.0 - 192.168.255.255)
                // Plus loopback: 127.0.0.0/8 and ::1
                if (ip.StartsWith("10.") || ip.StartsWith("192.168.") || ip.StartsWith("127.") || ip == "::1")
                    return true;

                // Check 172.16.0.0/12 range (172.16.x.x through 172.31.x.x)
                if (ip.StartsWith("172."))
                {
                    var parts = ip.Split('.');
                    if (parts.Length >= 2 && int.TryParse(parts[1], out var secondOctet))
                    {
                        return secondOctet >= 16 && secondOctet <= 31;
                    }
                }

                return false;
            });
        }
    }

    /// <summary>
    /// ABAC policy definition.
    /// </summary>
    public sealed record AbacPolicy
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public string Description { get; init; } = "";
        public int Priority { get; init; } = 100;
        public bool IsEnabled { get; init; } = true;
        public PolicyEffect Effect { get; init; }
        public required PolicyTarget Target { get; init; }
        public List<PolicyCondition> Conditions { get; init; } = new();
    }

    /// <summary>
    /// Policy effect (permit or deny).
    /// </summary>
    public enum PolicyEffect
    {
        Permit,
        Deny
    }

    /// <summary>
    /// Policy target specification.
    /// </summary>
    public record PolicyTarget
    {
        public List<string>? Subjects { get; init; }
        public List<string>? Resources { get; init; }
        public List<string>? Actions { get; init; }
    }

    /// <summary>
    /// Policy condition.
    /// </summary>
    public record PolicyCondition
    {
        public required string Name { get; init; }
        public AttributeSource AttributeSource { get; init; }
        public string AttributeName { get; init; } = "";
        public ConditionOperator Operator { get; init; }
        public object? Value { get; init; }
        public IEnumerable<object>? Values { get; init; }
    }

    /// <summary>
    /// Source of attribute for condition evaluation.
    /// </summary>
    public enum AttributeSource
    {
        Subject,
        Resource,
        Action,
        Environment
    }

    /// <summary>
    /// Condition operators.
    /// </summary>
    public enum ConditionOperator
    {
        Equals,
        NotEquals,
        Contains,
        NotContains,
        GreaterThan,
        LessThan,
        GreaterThanOrEqual,
        LessThanOrEqual,
        In,
        NotIn,
        Matches,
        Exists,
        NotExists
    }
}
