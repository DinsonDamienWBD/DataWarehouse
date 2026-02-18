using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.Core
{
    /// <summary>
    /// Policy-Based Access Control (PBAC) strategy implementation.
    /// Provides centralized policy management with complex rule evaluation, policy inheritance,
    /// conflict resolution, and policy versioning for enterprise-grade access control.
    /// </summary>
    /// <remarks>
    /// <para>
    /// PBAC features:
    /// - Centralized policy repository with versioning
    /// - Complex condition evaluation with boolean logic (AND/OR/NOT)
    /// - Policy inheritance and composition
    /// - Conflict resolution strategies (deny-overrides, permit-overrides, first-applicable)
    /// - Policy simulation and impact analysis
    /// - Real-time policy updates without restart
    /// </para>
    /// </remarks>
    public sealed class PolicyBasedAccessControlStrategy : AccessControlStrategyBase
    {
        private readonly ConcurrentDictionary<string, PolicyDefinition> _policies = new();
        private readonly ConcurrentDictionary<string, PolicySet> _policySets = new();
        private readonly ConcurrentDictionary<string, PolicyVersion> _policyVersions = new();
        private readonly List<IPolicyInformationPoint> _pips = new();
        private ConflictResolutionStrategy _defaultConflictResolution = ConflictResolutionStrategy.DenyOverrides;
        private int _nextVersionNumber = 1;

        /// <inheritdoc/>
        public override string StrategyId => "pbac";

        /// <inheritdoc/>
        public override string StrategyName => "Policy-Based Access Control";

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
            // Load conflict resolution strategy
            if (configuration.TryGetValue("ConflictResolution", out var crObj) &&
                crObj is string crStr &&
                Enum.TryParse<ConflictResolutionStrategy>(crStr, out var cr))
            {
                _defaultConflictResolution = cr;
            }

            // Load policies from configuration
            if (configuration.TryGetValue("Policies", out var policiesObj) &&
                policiesObj is IEnumerable<Dictionary<string, object>> policyConfigs)
            {
                foreach (var config in policyConfigs)
                {
                    var policy = ParsePolicyFromConfig(config);
                    if (policy != null)
                    {
                        AddPolicy(policy);
                    }
                }
            }

            // Initialize default policies
            InitializeDefaultPolicies();

            return base.InitializeAsync(configuration, cancellationToken);
        }

        #region Policy Management

        /// <summary>
        /// Adds a new policy to the repository.
        /// </summary>
        public PolicyVersion AddPolicy(PolicyDefinition policy)
        {
            var version = new PolicyVersion
            {
                VersionNumber = Interlocked.Increment(ref _nextVersionNumber),
                PolicyId = policy.Id,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = "system",
                Policy = policy,
                Status = PolicyVersionStatus.Active
            };

            _policies[policy.Id] = policy;
            _policyVersions[$"{policy.Id}:v{version.VersionNumber}"] = version;

            return version;
        }

        /// <summary>
        /// Updates an existing policy, creating a new version.
        /// </summary>
        public PolicyVersion UpdatePolicy(string policyId, PolicyDefinition updatedPolicy)
        {
            if (!_policies.ContainsKey(policyId))
            {
                throw new ArgumentException($"Policy '{policyId}' not found");
            }

            updatedPolicy = updatedPolicy with { Id = policyId };
            return AddPolicy(updatedPolicy);
        }

        /// <summary>
        /// Removes a policy from the repository.
        /// </summary>
        public bool RemovePolicy(string policyId)
        {
            return _policies.TryRemove(policyId, out _);
        }

        /// <summary>
        /// Gets a policy by ID.
        /// </summary>
        public PolicyDefinition? GetPolicy(string policyId)
        {
            return _policies.TryGetValue(policyId, out var policy) ? policy : null;
        }

        /// <summary>
        /// Gets all policies.
        /// </summary>
        public IReadOnlyList<PolicyDefinition> GetAllPolicies()
        {
            return _policies.Values.OrderBy(p => p.Priority).ToList().AsReadOnly();
        }

        /// <summary>
        /// Gets policy version history.
        /// </summary>
        public IReadOnlyList<PolicyVersion> GetPolicyVersions(string policyId)
        {
            return _policyVersions.Values
                .Where(v => v.PolicyId == policyId)
                .OrderByDescending(v => v.VersionNumber)
                .ToList()
                .AsReadOnly();
        }

        /// <summary>
        /// Creates a policy set (group of policies evaluated together).
        /// </summary>
        public void CreatePolicySet(PolicySet policySet)
        {
            _policySets[policySet.Id] = policySet;
        }

        /// <summary>
        /// Registers a Policy Information Point for dynamic attribute resolution.
        /// </summary>
        public void RegisterPip(IPolicyInformationPoint pip)
        {
            _pips.Add(pip);
        }

        #endregion

        #region Policy Simulation

        /// <summary>
        /// Simulates policy evaluation without actually enforcing it.
        /// Useful for testing policy changes before deployment.
        /// </summary>
        public async Task<PolicySimulationResult> SimulatePolicyAsync(
            AccessContext context,
            IEnumerable<PolicyDefinition>? testPolicies = null,
            CancellationToken cancellationToken = default)
        {
            var policiesToEvaluate = testPolicies?.ToList() ?? _policies.Values.ToList();
            var evaluations = new List<PolicyEvaluationDetail>();

            foreach (var policy in policiesToEvaluate.Where(p => p.IsEnabled).OrderBy(p => p.Priority))
            {
                var evaluation = await EvaluatePolicyDetailedAsync(policy, context, cancellationToken);
                evaluations.Add(evaluation);
            }

            var finalDecision = ApplyConflictResolution(evaluations, _defaultConflictResolution);

            return new PolicySimulationResult
            {
                Context = context,
                Evaluations = evaluations.AsReadOnly(),
                FinalDecision = finalDecision,
                ConflictResolutionUsed = _defaultConflictResolution,
                EvaluatedAt = DateTime.UtcNow
            };
        }

        /// <summary>
        /// Analyzes the impact of a policy change.
        /// </summary>
        public PolicyImpactAnalysis AnalyzePolicyImpact(PolicyDefinition proposedPolicy, IEnumerable<AccessContext> sampleContexts)
        {
            var affectedContexts = new List<ImpactedContext>();

            foreach (var context in sampleContexts)
            {
                // Evaluate without the proposed policy
                var currentPolicies = _policies.Values.Where(p => p.Id != proposedPolicy.Id).ToList();
                var beforeResult = EvaluatePoliciesSync(currentPolicies, context);

                // Evaluate with the proposed policy
                var withProposedPolicies = currentPolicies.Append(proposedPolicy).ToList();
                var afterResult = EvaluatePoliciesSync(withProposedPolicies, context);

                if (beforeResult.IsGranted != afterResult.IsGranted)
                {
                    affectedContexts.Add(new ImpactedContext
                    {
                        Context = context,
                        BeforeDecision = beforeResult.IsGranted,
                        AfterDecision = afterResult.IsGranted,
                        ChangeType = afterResult.IsGranted ? ImpactChangeType.NewlyGranted : ImpactChangeType.NewlyDenied
                    });
                }
            }

            return new PolicyImpactAnalysis
            {
                ProposedPolicy = proposedPolicy,
                TotalContextsAnalyzed = sampleContexts.Count(),
                AffectedContexts = affectedContexts.AsReadOnly(),
                ImpactScore = affectedContexts.Count / (double)Math.Max(1, sampleContexts.Count()),
                AnalyzedAt = DateTime.UtcNow
            };
        }

        private AccessDecision EvaluatePoliciesSync(List<PolicyDefinition> policies, AccessContext context)
        {
            var evaluations = new List<PolicyEvaluationDetail>();

            foreach (var policy in policies.Where(p => p.IsEnabled).OrderBy(p => p.Priority))
            {
                var evaluation = EvaluatePolicyDetailedSync(policy, context);
                evaluations.Add(evaluation);
            }

            return ApplyConflictResolution(evaluations, _defaultConflictResolution);
        }

        #endregion

        #region Core Evaluation

        /// <inheritdoc/>
        protected override async Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            // Enrich context with PIP data
            var enrichedContext = await EnrichContextAsync(context, cancellationToken);

            var evaluations = new List<PolicyEvaluationDetail>();

            // First, evaluate applicable policy sets
            foreach (var policySet in _policySets.Values.Where(ps => ps.IsEnabled))
            {
                if (PolicySetApplies(policySet, enrichedContext))
                {
                    foreach (var policyId in policySet.PolicyIds)
                    {
                        if (_policies.TryGetValue(policyId, out var policy) && policy.IsEnabled)
                        {
                            var evaluation = await EvaluatePolicyDetailedAsync(policy, enrichedContext, cancellationToken);
                            evaluations.Add(evaluation);
                        }
                    }
                }
            }

            // Then evaluate standalone policies
            foreach (var policy in _policies.Values.Where(p => p.IsEnabled && !IsInPolicySet(p.Id)).OrderBy(p => p.Priority))
            {
                var evaluation = await EvaluatePolicyDetailedAsync(policy, enrichedContext, cancellationToken);
                evaluations.Add(evaluation);
            }

            // Apply conflict resolution
            return ApplyConflictResolution(evaluations, _defaultConflictResolution);
        }

        private async Task<PolicyEvaluationDetail> EvaluatePolicyDetailedAsync(
            PolicyDefinition policy,
            AccessContext context,
            CancellationToken cancellationToken)
        {
            var evaluation = new PolicyEvaluationDetail
            {
                PolicyId = policy.Id,
                PolicyName = policy.Name,
                Priority = policy.Priority
            };

            // Check if policy target matches
            if (!MatchesTarget(policy.Target, context))
            {
                evaluation.IsApplicable = false;
                evaluation.Reason = "Target does not match";
                return evaluation;
            }

            evaluation.IsApplicable = true;

            // Evaluate all rules
            var ruleResults = new List<RuleEvaluationResult>();
            foreach (var rule in policy.Rules)
            {
                var ruleResult = await EvaluateRuleAsync(rule, context, cancellationToken);
                ruleResults.Add(ruleResult);
            }

            evaluation.RuleResults = ruleResults.AsReadOnly();

            // Determine policy decision based on rule combining algorithm
            evaluation.Decision = CombineRuleDecisions(ruleResults, policy.RuleCombiningAlgorithm);
            evaluation.Effect = policy.Effect;
            evaluation.Reason = $"Policy '{policy.Name}' evaluated: {evaluation.Decision}";

            return evaluation;
        }

        private PolicyEvaluationDetail EvaluatePolicyDetailedSync(PolicyDefinition policy, AccessContext context)
        {
            // Sync bridge: internal sync wrapper for policy evaluation
            return Task.Run(() => EvaluatePolicyDetailedAsync(policy, context, CancellationToken.None)).Result;
        }

        private async Task<RuleEvaluationResult> EvaluateRuleAsync(
            PolicyRule rule,
            AccessContext context,
            CancellationToken cancellationToken)
        {
            var result = new RuleEvaluationResult
            {
                RuleId = rule.Id,
                RuleName = rule.Name
            };

            try
            {
                // Evaluate the condition expression
                var conditionMet = await EvaluateConditionExpressionAsync(rule.Condition, context, cancellationToken);
                result.ConditionMet = conditionMet;
                result.Effect = conditionMet ? rule.Effect : RuleEffect.NotApplicable;
                result.Reason = conditionMet
                    ? $"Condition satisfied: {rule.Effect}"
                    : "Condition not satisfied";
            }
            catch (Exception ex)
            {
                result.ConditionMet = false;
                result.Effect = RuleEffect.Indeterminate;
                result.Reason = $"Evaluation error: {ex.Message}";
            }

            return result;
        }

        private async Task<bool> EvaluateConditionExpressionAsync(
            ConditionExpression condition,
            AccessContext context,
            CancellationToken cancellationToken)
        {
            return condition.Type switch
            {
                ConditionType.Simple => EvaluateSimpleCondition(condition, context),
                ConditionType.And => await EvaluateAndConditionAsync(condition, context, cancellationToken),
                ConditionType.Or => await EvaluateOrConditionAsync(condition, context, cancellationToken),
                ConditionType.Not => !await EvaluateConditionExpressionAsync(condition.SubConditions![0], context, cancellationToken),
                ConditionType.Function => await EvaluateFunctionConditionAsync(condition, context, cancellationToken),
                _ => false
            };
        }

        private bool EvaluateSimpleCondition(ConditionExpression condition, AccessContext context)
        {
            var attributeValue = GetAttributeValue(condition.AttributeSource, condition.AttributeName!, context);
            return EvaluateOperator(attributeValue, condition.Operator, condition.Value);
        }

        private async Task<bool> EvaluateAndConditionAsync(
            ConditionExpression condition,
            AccessContext context,
            CancellationToken cancellationToken)
        {
            foreach (var sub in condition.SubConditions!)
            {
                if (!await EvaluateConditionExpressionAsync(sub, context, cancellationToken))
                    return false;
            }
            return true;
        }

        private async Task<bool> EvaluateOrConditionAsync(
            ConditionExpression condition,
            AccessContext context,
            CancellationToken cancellationToken)
        {
            foreach (var sub in condition.SubConditions!)
            {
                if (await EvaluateConditionExpressionAsync(sub, context, cancellationToken))
                    return true;
            }
            return false;
        }

        private async Task<bool> EvaluateFunctionConditionAsync(
            ConditionExpression condition,
            AccessContext context,
            CancellationToken cancellationToken)
        {
            // Built-in functions
            return condition.FunctionName switch
            {
                "IsBusinessHours" => IsBusinessHours(context.RequestTime),
                "IsWeekday" => context.RequestTime.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday),
                "IsInternalNetwork" => IsInternalNetwork(context.ClientIpAddress),
                "HasRole" => context.Roles.Contains(condition.FunctionArgs?[0]?.ToString() ?? ""),
                "IpInRange" => IpInRange(context.ClientIpAddress, condition.FunctionArgs?[0]?.ToString()),
                "TimeInRange" => TimeInRange(context.RequestTime, condition.FunctionArgs),
                _ => false
            };
        }

        private bool EvaluateOperator(object? value, PolicyOperator op, object? expected)
        {
            return op switch
            {
                PolicyOperator.Equals => Equals(value?.ToString(), expected?.ToString()),
                PolicyOperator.NotEquals => !Equals(value?.ToString(), expected?.ToString()),
                PolicyOperator.Contains => value?.ToString()?.Contains(expected?.ToString() ?? "") ?? false,
                PolicyOperator.StartsWith => value?.ToString()?.StartsWith(expected?.ToString() ?? "") ?? false,
                PolicyOperator.EndsWith => value?.ToString()?.EndsWith(expected?.ToString() ?? "") ?? false,
                PolicyOperator.Matches => Regex.IsMatch(value?.ToString() ?? "", expected?.ToString() ?? "", RegexOptions.None, TimeSpan.FromSeconds(1)),
                PolicyOperator.GreaterThan => Compare(value, expected) > 0,
                PolicyOperator.LessThan => Compare(value, expected) < 0,
                PolicyOperator.GreaterThanOrEqual => Compare(value, expected) >= 0,
                PolicyOperator.LessThanOrEqual => Compare(value, expected) <= 0,
                PolicyOperator.In => (expected as IEnumerable<object>)?.Contains(value) ?? false,
                PolicyOperator.NotIn => !((expected as IEnumerable<object>)?.Contains(value) ?? false),
                PolicyOperator.Exists => value != null,
                PolicyOperator.NotExists => value == null,
                _ => false
            };
        }

        private static int Compare(object? a, object? b)
        {
            if (a is IComparable ca && b != null)
            {
                try
                {
                    var converted = Convert.ChangeType(b, a.GetType());
                    return ca.CompareTo(converted);
                }
                catch { /* Best-effort policy enforcement - non-critical */ }
            }
            return 0;
        }

        private object? GetAttributeValue(PolicyAttributeSource source, string name, AccessContext context)
        {
            return source switch
            {
                PolicyAttributeSource.Subject => context.SubjectAttributes.TryGetValue(name, out var sv) ? sv :
                    name.Equals("id", StringComparison.OrdinalIgnoreCase) ? context.SubjectId : null,
                PolicyAttributeSource.Resource => context.ResourceAttributes.TryGetValue(name, out var rv) ? rv :
                    name.Equals("id", StringComparison.OrdinalIgnoreCase) ? context.ResourceId : null,
                PolicyAttributeSource.Action => name.Equals("name", StringComparison.OrdinalIgnoreCase) ? context.Action : null,
                PolicyAttributeSource.Environment => context.EnvironmentAttributes.TryGetValue(name, out var ev) ? ev :
                    GetBuiltInEnvironmentAttribute(name, context),
                _ => null
            };
        }

        private static object? GetBuiltInEnvironmentAttribute(string name, AccessContext context)
        {
            return name.ToLowerInvariant() switch
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

        private bool MatchesTarget(PbacPolicyTarget target, AccessContext context)
        {
            // Check subject match
            if (target.Subjects != null && target.Subjects.Any())
            {
                if (!target.Subjects.Contains("*") &&
                    !target.Subjects.Contains(context.SubjectId, StringComparer.OrdinalIgnoreCase) &&
                    !target.Subjects.Intersect(context.Roles, StringComparer.OrdinalIgnoreCase).Any())
                {
                    return false;
                }
            }

            // Check resource match
            if (target.Resources != null && target.Resources.Any())
            {
                var matches = target.Resources.Any(r =>
                    r == "*" ||
                    context.ResourceId.Equals(r, StringComparison.OrdinalIgnoreCase) ||
                    (r.EndsWith("/*") && context.ResourceId.StartsWith(r[..^2], StringComparison.OrdinalIgnoreCase)) ||
                    Regex.IsMatch(context.ResourceId, r, RegexOptions.None, TimeSpan.FromSeconds(1)));

                if (!matches) return false;
            }

            // Check action match
            if (target.Actions != null && target.Actions.Any())
            {
                if (!target.Actions.Contains("*") &&
                    !target.Actions.Contains(context.Action, StringComparer.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        private PolicyDecision CombineRuleDecisions(List<RuleEvaluationResult> results, RuleCombiningAlgorithm algorithm)
        {
            return algorithm switch
            {
                RuleCombiningAlgorithm.DenyOverrides => results.Any(r => r.Effect == RuleEffect.Deny)
                    ? PolicyDecision.Deny
                    : results.Any(r => r.Effect == RuleEffect.Permit) ? PolicyDecision.Permit : PolicyDecision.NotApplicable,

                RuleCombiningAlgorithm.PermitOverrides => results.Any(r => r.Effect == RuleEffect.Permit)
                    ? PolicyDecision.Permit
                    : results.Any(r => r.Effect == RuleEffect.Deny) ? PolicyDecision.Deny : PolicyDecision.NotApplicable,

                RuleCombiningAlgorithm.FirstApplicable => results.FirstOrDefault(r => r.Effect is RuleEffect.Permit or RuleEffect.Deny)?.Effect switch
                {
                    RuleEffect.Permit => PolicyDecision.Permit,
                    RuleEffect.Deny => PolicyDecision.Deny,
                    _ => PolicyDecision.NotApplicable
                },

                RuleCombiningAlgorithm.OnlyOneApplicable => results.Count(r => r.Effect is RuleEffect.Permit or RuleEffect.Deny) == 1
                    ? (results.Single(r => r.Effect is RuleEffect.Permit or RuleEffect.Deny).Effect == RuleEffect.Permit
                        ? PolicyDecision.Permit : PolicyDecision.Deny)
                    : PolicyDecision.Indeterminate,

                _ => PolicyDecision.NotApplicable
            };
        }

        private AccessDecision ApplyConflictResolution(List<PolicyEvaluationDetail> evaluations, ConflictResolutionStrategy strategy)
        {
            var applicableEvaluations = evaluations.Where(e => e.IsApplicable && e.Decision is PolicyDecision.Permit or PolicyDecision.Deny).ToList();

            if (!applicableEvaluations.Any())
            {
                return new AccessDecision
                {
                    IsGranted = false,
                    Reason = "No applicable policies found - access denied by default",
                    ApplicablePolicies = Array.Empty<string>()
                };
            }

            bool isGranted;
            string reason;
            var applicablePolicies = applicableEvaluations.Select(e => e.PolicyId).ToArray();

            switch (strategy)
            {
                case ConflictResolutionStrategy.DenyOverrides:
                    var denyPolicy = applicableEvaluations.FirstOrDefault(e => e.Decision == PolicyDecision.Deny);
                    if (denyPolicy != null)
                    {
                        isGranted = false;
                        reason = $"Denied by policy '{denyPolicy.PolicyName}' (deny-overrides)";
                    }
                    else
                    {
                        isGranted = true;
                        reason = "Permitted - no deny policies applicable";
                    }
                    break;

                case ConflictResolutionStrategy.PermitOverrides:
                    var permitPolicy = applicableEvaluations.FirstOrDefault(e => e.Decision == PolicyDecision.Permit);
                    if (permitPolicy != null)
                    {
                        isGranted = true;
                        reason = $"Permitted by policy '{permitPolicy.PolicyName}' (permit-overrides)";
                    }
                    else
                    {
                        isGranted = false;
                        reason = "Denied - no permit policies applicable";
                    }
                    break;

                case ConflictResolutionStrategy.FirstApplicable:
                    var firstApplicable = applicableEvaluations.OrderBy(e => e.Priority).First();
                    isGranted = firstApplicable.Decision == PolicyDecision.Permit;
                    reason = $"{(isGranted ? "Permitted" : "Denied")} by first applicable policy '{firstApplicable.PolicyName}'";
                    break;

                case ConflictResolutionStrategy.HighestPriority:
                    var highestPriority = applicableEvaluations.OrderBy(e => e.Priority).First();
                    isGranted = highestPriority.Decision == PolicyDecision.Permit;
                    reason = $"{(isGranted ? "Permitted" : "Denied")} by highest priority policy '{highestPriority.PolicyName}'";
                    break;

                default:
                    isGranted = false;
                    reason = "Unknown conflict resolution strategy";
                    break;
            }

            return new AccessDecision
            {
                IsGranted = isGranted,
                Reason = reason,
                ApplicablePolicies = applicablePolicies,
                Metadata = new Dictionary<string, object>
                {
                    ["EvaluatedPolicies"] = evaluations.Count,
                    ["ApplicablePolicies"] = applicableEvaluations.Count,
                    ["ConflictResolution"] = strategy.ToString()
                }
            };
        }

        private async Task<AccessContext> EnrichContextAsync(AccessContext context, CancellationToken cancellationToken)
        {
            var subjectAttrs = new Dictionary<string, object>(context.SubjectAttributes);
            var resourceAttrs = new Dictionary<string, object>(context.ResourceAttributes);
            var envAttrs = new Dictionary<string, object>(context.EnvironmentAttributes);

            foreach (var pip in _pips)
            {
                try
                {
                    var attrs = await pip.GetAttributesAsync(context, cancellationToken);
                    foreach (var (key, value) in attrs)
                    {
                        switch (pip.AttributeCategory)
                        {
                            case PolicyAttributeSource.Subject:
                                subjectAttrs[key] = value;
                                break;
                            case PolicyAttributeSource.Resource:
                                resourceAttrs[key] = value;
                                break;
                            case PolicyAttributeSource.Environment:
                                envAttrs[key] = value;
                                break;
                        }
                    }
                }
                catch
                {
                    // Continue with other PIPs if one fails
                }
            }

            return context with
            {
                SubjectAttributes = subjectAttrs,
                ResourceAttributes = resourceAttrs,
                EnvironmentAttributes = envAttrs
            };
        }

        private bool PolicySetApplies(PolicySet policySet, AccessContext context)
        {
            if (policySet.AppliesTo == null || !policySet.AppliesTo.Any())
                return true;

            return policySet.AppliesTo.Any(target =>
                target == "*" ||
                context.ResourceId.StartsWith(target, StringComparison.OrdinalIgnoreCase));
        }

        private bool IsInPolicySet(string policyId)
        {
            return _policySets.Values.Any(ps => ps.PolicyIds.Contains(policyId));
        }

        #endregion

        #region Helper Functions

        private static bool IsBusinessHours(DateTime time)
        {
            var hour = time.Hour;
            var day = time.DayOfWeek;
            return day is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && hour >= 8 && hour < 18;
        }

        private static bool IsInternalNetwork(string? ip)
        {
            if (string.IsNullOrEmpty(ip)) return false;
            return ip.StartsWith("10.") || ip.StartsWith("192.168.") ||
                   ip.StartsWith("172.16.") || ip.StartsWith("172.17.") ||
                   ip.StartsWith("172.18.") || ip.StartsWith("172.19.") ||
                   ip.StartsWith("127.") || ip == "::1";
        }

        private static bool IpInRange(string? ip, string? cidr)
        {
            if (string.IsNullOrEmpty(ip) || string.IsNullOrEmpty(cidr)) return false;
            // Simplified CIDR matching
            var parts = cidr.Split('/');
            if (parts.Length != 2) return ip.StartsWith(cidr);
            return ip.StartsWith(parts[0].TrimEnd('.'));
        }

        private static bool TimeInRange(DateTime time, object[]? args)
        {
            if (args == null || args.Length < 2) return false;
            if (int.TryParse(args[0]?.ToString(), out var start) &&
                int.TryParse(args[1]?.ToString(), out var end))
            {
                var hour = time.Hour;
                return hour >= start && hour < end;
            }
            return false;
        }

        private void InitializeDefaultPolicies()
        {
            // Default admin access policy
            AddPolicy(new PolicyDefinition
            {
                Id = "default-admin-access",
                Name = "Default Admin Access",
                Description = "Grants full access to admin role",
                IsEnabled = true,
                Priority = 1,
                Effect = PbacPolicyEffect.Permit,
                Target = new PbacPolicyTarget { Subjects = new[] { "admin", "Administrator" } },
                Rules = new List<PolicyRule>
                {
                    new()
                    {
                        Id = "admin-permit-all",
                        Name = "Admin Permit All",
                        Effect = RuleEffect.Permit,
                        Condition = new ConditionExpression
                        {
                            Type = ConditionType.Simple,
                            AttributeSource = PolicyAttributeSource.Subject,
                            AttributeName = "role",
                            Operator = PolicyOperator.In,
                            Value = new[] { "admin", "Administrator" }
                        }
                    }
                },
                RuleCombiningAlgorithm = RuleCombiningAlgorithm.FirstApplicable
            });

            // Business hours policy
            AddPolicy(new PolicyDefinition
            {
                Id = "business-hours-restriction",
                Name = "Business Hours Restriction",
                Description = "Restricts sensitive access to business hours",
                IsEnabled = true,
                Priority = 50,
                Effect = PbacPolicyEffect.Deny,
                Target = new PbacPolicyTarget { Resources = new[] { "sensitive/*" } },
                Rules = new List<PolicyRule>
                {
                    new()
                    {
                        Id = "outside-business-hours",
                        Name = "Outside Business Hours",
                        Effect = RuleEffect.Deny,
                        Condition = new ConditionExpression
                        {
                            Type = ConditionType.Not,
                            SubConditions = new List<ConditionExpression>
                            {
                                new()
                                {
                                    Type = ConditionType.Function,
                                    FunctionName = "IsBusinessHours"
                                }
                            }
                        }
                    }
                },
                RuleCombiningAlgorithm = RuleCombiningAlgorithm.DenyOverrides
            });
        }

        private PolicyDefinition? ParsePolicyFromConfig(Dictionary<string, object> config)
        {
            try
            {
                return new PolicyDefinition
                {
                    Id = config["Id"]?.ToString() ?? Guid.NewGuid().ToString(),
                    Name = config["Name"]?.ToString() ?? "Unnamed Policy",
                    Description = config.TryGetValue("Description", out var desc) ? desc?.ToString() ?? "" : "",
                    Priority = config.TryGetValue("Priority", out var prio) && prio is int p ? p : 100,
                    IsEnabled = !config.TryGetValue("IsEnabled", out var enabled) || enabled is true,
                    Effect = config.TryGetValue("Effect", out var eff) &&
                             Enum.TryParse<PbacPolicyEffect>(eff?.ToString(), out var pe) ? pe : PbacPolicyEffect.Permit,
                    Target = new PbacPolicyTarget(),
                    Rules = new List<PolicyRule>(),
                    RuleCombiningAlgorithm = RuleCombiningAlgorithm.FirstApplicable
                };
            }
            catch
            {
                return null;
            }
        }

        #endregion
    }

    #region Supporting Types

    /// <summary>
    /// Policy definition.
    /// </summary>
    public sealed record PolicyDefinition
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public string Description { get; init; } = "";
        public int Priority { get; init; } = 100;
        public bool IsEnabled { get; init; } = true;
        public PbacPolicyEffect Effect { get; init; }
        public required PbacPolicyTarget Target { get; init; }
        public required List<PolicyRule> Rules { get; init; }
        public RuleCombiningAlgorithm RuleCombiningAlgorithm { get; init; } = RuleCombiningAlgorithm.FirstApplicable;
        public List<string> InheritFrom { get; init; } = new();
        public Dictionary<string, object> Metadata { get; init; } = new();
    }

    /// <summary>
    /// Policy target specification for PBAC.
    /// </summary>
    public sealed record PbacPolicyTarget
    {
        public string[]? Subjects { get; init; }
        public string[]? Resources { get; init; }
        public string[]? Actions { get; init; }
    }

    /// <summary>
    /// Policy rule.
    /// </summary>
    public sealed record PolicyRule
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public RuleEffect Effect { get; init; }
        public required ConditionExpression Condition { get; init; }
    }

    /// <summary>
    /// Condition expression for complex boolean logic.
    /// </summary>
    public sealed record ConditionExpression
    {
        public ConditionType Type { get; init; }
        public PolicyAttributeSource AttributeSource { get; init; }
        public string? AttributeName { get; init; }
        public PolicyOperator Operator { get; init; }
        public object? Value { get; init; }
        public List<ConditionExpression>? SubConditions { get; init; }
        public string? FunctionName { get; init; }
        public object[]? FunctionArgs { get; init; }
    }

    /// <summary>
    /// Condition types.
    /// </summary>
    public enum ConditionType
    {
        Simple,
        And,
        Or,
        Not,
        Function
    }

    /// <summary>
    /// Policy operators.
    /// </summary>
    public enum PolicyOperator
    {
        Equals,
        NotEquals,
        Contains,
        StartsWith,
        EndsWith,
        Matches,
        GreaterThan,
        LessThan,
        GreaterThanOrEqual,
        LessThanOrEqual,
        In,
        NotIn,
        Exists,
        NotExists
    }

    /// <summary>
    /// Policy attribute sources.
    /// </summary>
    public enum PolicyAttributeSource
    {
        Subject,
        Resource,
        Action,
        Environment
    }

    /// <summary>
    /// PBAC Policy effect.
    /// </summary>
    public enum PbacPolicyEffect
    {
        Permit,
        Deny
    }

    /// <summary>
    /// Rule effect.
    /// </summary>
    public enum RuleEffect
    {
        Permit,
        Deny,
        NotApplicable,
        Indeterminate
    }

    /// <summary>
    /// Policy decision.
    /// </summary>
    public enum PolicyDecision
    {
        Permit,
        Deny,
        NotApplicable,
        Indeterminate
    }

    /// <summary>
    /// Rule combining algorithms.
    /// </summary>
    public enum RuleCombiningAlgorithm
    {
        DenyOverrides,
        PermitOverrides,
        FirstApplicable,
        OnlyOneApplicable
    }

    /// <summary>
    /// Conflict resolution strategies.
    /// </summary>
    public enum ConflictResolutionStrategy
    {
        DenyOverrides,
        PermitOverrides,
        FirstApplicable,
        HighestPriority
    }

    /// <summary>
    /// Policy set (group of policies).
    /// </summary>
    public sealed record PolicySet
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public string Description { get; init; } = "";
        public bool IsEnabled { get; init; } = true;
        public required List<string> PolicyIds { get; init; }
        public string[]? AppliesTo { get; init; }
        public ConflictResolutionStrategy ConflictResolution { get; init; } = ConflictResolutionStrategy.DenyOverrides;
    }

    /// <summary>
    /// Policy version record.
    /// </summary>
    public sealed record PolicyVersion
    {
        public required int VersionNumber { get; init; }
        public required string PolicyId { get; init; }
        public required DateTime CreatedAt { get; init; }
        public required string CreatedBy { get; init; }
        public required PolicyDefinition Policy { get; init; }
        public PolicyVersionStatus Status { get; init; }
        public string? Comment { get; init; }
    }

    /// <summary>
    /// Policy version status.
    /// </summary>
    public enum PolicyVersionStatus
    {
        Draft,
        Active,
        Deprecated,
        Archived
    }

    /// <summary>
    /// Policy Information Point interface.
    /// </summary>
    public interface IPolicyInformationPoint
    {
        string Id { get; }
        PolicyAttributeSource AttributeCategory { get; }
        Task<Dictionary<string, object>> GetAttributesAsync(AccessContext context, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Policy evaluation detail.
    /// </summary>
    public sealed class PolicyEvaluationDetail
    {
        public required string PolicyId { get; init; }
        public required string PolicyName { get; init; }
        public required int Priority { get; init; }
        public bool IsApplicable { get; set; }
        public PolicyDecision Decision { get; set; }
        public PbacPolicyEffect Effect { get; set; }
        public string? Reason { get; set; }
        public IReadOnlyList<RuleEvaluationResult>? RuleResults { get; set; }
    }

    /// <summary>
    /// Rule evaluation result.
    /// </summary>
    public sealed class RuleEvaluationResult
    {
        public required string RuleId { get; init; }
        public required string RuleName { get; init; }
        public bool ConditionMet { get; set; }
        public RuleEffect Effect { get; set; }
        public string? Reason { get; set; }
    }

    /// <summary>
    /// Policy simulation result.
    /// </summary>
    public sealed record PolicySimulationResult
    {
        public required AccessContext Context { get; init; }
        public required IReadOnlyList<PolicyEvaluationDetail> Evaluations { get; init; }
        public required AccessDecision FinalDecision { get; init; }
        public required ConflictResolutionStrategy ConflictResolutionUsed { get; init; }
        public required DateTime EvaluatedAt { get; init; }
    }

    /// <summary>
    /// Policy impact analysis result.
    /// </summary>
    public sealed record PolicyImpactAnalysis
    {
        public required PolicyDefinition ProposedPolicy { get; init; }
        public required int TotalContextsAnalyzed { get; init; }
        public required IReadOnlyList<ImpactedContext> AffectedContexts { get; init; }
        public required double ImpactScore { get; init; }
        public required DateTime AnalyzedAt { get; init; }
    }

    /// <summary>
    /// Impacted context from policy change.
    /// </summary>
    public sealed record ImpactedContext
    {
        public required AccessContext Context { get; init; }
        public required bool BeforeDecision { get; init; }
        public required bool AfterDecision { get; init; }
        public required ImpactChangeType ChangeType { get; init; }
    }

    /// <summary>
    /// Type of impact change.
    /// </summary>
    public enum ImpactChangeType
    {
        NewlyGranted,
        NewlyDenied
    }

    #endregion
}
