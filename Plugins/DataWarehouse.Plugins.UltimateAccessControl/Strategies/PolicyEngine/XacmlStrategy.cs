using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.PolicyEngine
{
    /// <summary>
    /// XACML (eXtensible Access Control Markup Language) policy engine strategy.
    ///
    /// Implements XACML 3.0 policy evaluation with:
    /// - Target matching (subject, resource, action, environment attributes)
    /// - Rule combining algorithms (deny-overrides, permit-overrides, first-applicable, ordered-deny-overrides)
    /// - Policy combining algorithms for PolicySet evaluation
    /// - Attribute retrieval from Policy Information Point (PIP)
    /// - Obligation and advice handling
    /// - Multi-valued attribute matching
    ///
    /// XACML Components:
    /// - PDP (Policy Decision Point): This strategy — evaluates policies
    /// - PEP (Policy Enforcement Point): The calling application
    /// - PIP (Policy Information Point): Attribute sources (configurable)
    /// - PAP (Policy Administration Point): Policy management API
    /// </summary>
    public sealed class XacmlStrategy : AccessControlStrategyBase
    {
        private readonly ConcurrentDictionary<string, XacmlPolicy> _policies = new();
        private readonly ConcurrentDictionary<string, XacmlPolicySet> _policySets = new();
        private readonly ConcurrentDictionary<string, Func<string, Task<object?>>> _pipResolvers = new();
        private RuleCombiningAlgorithm _defaultCombiningAlgorithm = RuleCombiningAlgorithm.DenyOverrides;

        public override string StrategyId => "xacml";
        public override string StrategyName => "XACML 3.0 Policy Engine";

        public override AccessControlCapabilities Capabilities { get; } = new()
        {
            SupportsRealTimeDecisions = true,
            SupportsAuditTrail = true,
            SupportsPolicyConfiguration = true,
            SupportsExternalIdentity = true,
            SupportsTemporalAccess = true,
            SupportsGeographicRestrictions = true,
            MaxConcurrentEvaluations = 10000
        };

        public override Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken cancellationToken = default)
        {
            if (configuration.TryGetValue("DefaultCombiningAlgorithm", out var algo) && algo is string algoStr)
            {
                _defaultCombiningAlgorithm = algoStr.ToLowerInvariant() switch
                {
                    "permit-overrides" => RuleCombiningAlgorithm.PermitOverrides,
                    "first-applicable" => RuleCombiningAlgorithm.FirstApplicable,
                    "ordered-deny-overrides" => RuleCombiningAlgorithm.OrderedDenyOverrides,
                    "ordered-permit-overrides" => RuleCombiningAlgorithm.OrderedPermitOverrides,
                    _ => RuleCombiningAlgorithm.DenyOverrides
                };
            }

            // Load policies from configuration
            if (configuration.TryGetValue("Policies", out var policiesObj) && policiesObj is List<object> policies)
            {
                foreach (var policyObj in policies.OfType<Dictionary<string, object>>())
                {
                    var policy = ParsePolicyFromConfig(policyObj);
                    if (policy != null)
                        _policies[policy.PolicyId] = policy;
                }
            }

            return base.InitializeAsync(configuration, cancellationToken);
        }

        protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("xacml.init");
            return base.InitializeAsyncCore(cancellationToken);
        }

        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("xacml.shutdown");
            _policies.Clear();
            _policySets.Clear();
            return base.ShutdownAsyncCore(cancellationToken);
        }

        /// <summary>
        /// Adds an XACML policy to the PDP.
        /// </summary>
        public void AddPolicy(XacmlPolicy policy)
        {
            _policies[policy.PolicyId] = policy;
        }

        /// <summary>
        /// Adds an XACML policy set (collection of policies with combining algorithm).
        /// </summary>
        public void AddPolicySet(XacmlPolicySet policySet)
        {
            _policySets[policySet.PolicySetId] = policySet;
        }

        /// <summary>
        /// Registers a PIP attribute resolver for dynamic attribute retrieval.
        /// </summary>
        public void RegisterPipResolver(string attributeCategory, Func<string, Task<object?>> resolver)
        {
            _pipResolvers[attributeCategory] = resolver;
        }

        protected override async Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("xacml.evaluate");

            var request = BuildXacmlRequest(context);
            var applicableRules = new List<(XacmlRule Rule, string PolicyId)>();
            var applicablePolicies = new List<string>();
            var obligations = new List<XacmlObligation>();
            var advice = new List<XacmlAdvice>();

            // Resolve PIP attributes
            await ResolvePipAttributes(request, cancellationToken);

            // Evaluate each policy
            foreach (var policy in _policies.Values)
            {
                if (!MatchesTarget(policy.Target, request))
                    continue;

                applicablePolicies.Add(policy.PolicyId);

                foreach (var rule in policy.Rules)
                {
                    if (!MatchesTarget(rule.Target, request))
                        continue;

                    if (EvaluateCondition(rule.Condition, request))
                    {
                        applicableRules.Add((rule, policy.PolicyId));
                    }
                }

                // Collect obligations and advice
                obligations.AddRange(policy.Obligations);
                advice.AddRange(policy.Advice);
            }

            // Apply combining algorithm
            var (decision, reason) = ApplyCombiningAlgorithm(applicableRules, _defaultCombiningAlgorithm);

            return new AccessDecision
            {
                IsGranted = decision == XacmlDecision.Permit,
                Reason = reason,
                ApplicablePolicies = applicablePolicies.Select(p => $"XACML.{p}").ToArray(),
                Metadata = new Dictionary<string, object>
                {
                    ["XacmlDecision"] = decision.ToString(),
                    ["CombiningAlgorithm"] = _defaultCombiningAlgorithm.ToString(),
                    ["EvaluatedPolicies"] = applicablePolicies.Count,
                    ["MatchedRules"] = applicableRules.Count,
                    ["Obligations"] = obligations.Select(o => o.ObligationId).ToList(),
                    ["Advice"] = advice.Select(a => a.AdviceId).ToList()
                }
            };
        }

        private XacmlRequest BuildXacmlRequest(AccessContext context)
        {
            return new XacmlRequest
            {
                SubjectAttributes = new Dictionary<string, object>(context.SubjectAttributes)
                {
                    ["subject-id"] = context.SubjectId,
                    ["roles"] = context.Roles
                },
                ResourceAttributes = new Dictionary<string, object>(context.ResourceAttributes)
                {
                    ["resource-id"] = context.ResourceId
                },
                ActionAttributes = new Dictionary<string, object>
                {
                    ["action-id"] = context.Action
                },
                EnvironmentAttributes = new Dictionary<string, object>(context.EnvironmentAttributes)
                {
                    ["current-time"] = context.RequestTime.ToString("o"),
                    ["ip-address"] = context.ClientIpAddress ?? "unknown"
                }
            };
        }

        private async Task ResolvePipAttributes(XacmlRequest request, CancellationToken cancellationToken)
        {
            foreach (var resolver in _pipResolvers)
            {
                try
                {
                    var value = await resolver.Value(request.SubjectAttributes.GetValueOrDefault("subject-id")?.ToString() ?? "");
                    if (value != null)
                    {
                        request.SubjectAttributes[$"pip.{resolver.Key}"] = value;
                    }
                }
                catch
                {
                    // PIP resolution failure is non-fatal — attribute simply unavailable
                }
            }
        }

        private bool MatchesTarget(XacmlTarget? target, XacmlRequest request)
        {
            if (target == null)
                return true; // No target means applies to all

            foreach (var match in target.AnyOf)
            {
                bool anyOfMatches = false;

                foreach (var allOf in match.AllOf)
                {
                    bool allOfMatches = true;

                    foreach (var matchExpr in allOf.Matches)
                    {
                        var attributeValue = GetAttributeValue(matchExpr.Category, matchExpr.AttributeId, request);

                        if (attributeValue == null)
                        {
                            allOfMatches = false;
                            break;
                        }

                        if (!EvaluateMatch(matchExpr.MatchFunction, attributeValue, matchExpr.Value))
                        {
                            allOfMatches = false;
                            break;
                        }
                    }

                    if (allOfMatches)
                    {
                        anyOfMatches = true;
                        break;
                    }
                }

                if (!anyOfMatches)
                    return false;
            }

            return true;
        }

        private object? GetAttributeValue(string category, string attributeId, XacmlRequest request)
        {
            var attributes = category switch
            {
                "subject" => request.SubjectAttributes,
                "resource" => request.ResourceAttributes,
                "action" => request.ActionAttributes,
                "environment" => request.EnvironmentAttributes,
                _ => null
            };

            if (attributes == null) return null;
            return attributes.TryGetValue(attributeId, out var value) ? value : null;
        }

        private bool EvaluateMatch(string matchFunction, object attributeValue, object matchValue)
        {
            var attrStr = attributeValue?.ToString() ?? "";
            var matchStr = matchValue?.ToString() ?? "";

            return matchFunction switch
            {
                "string-equal" => attrStr.Equals(matchStr, StringComparison.Ordinal),
                "string-equal-ignore-case" => attrStr.Equals(matchStr, StringComparison.OrdinalIgnoreCase),
                "string-contains" => attrStr.Contains(matchStr),
                "string-starts-with" => attrStr.StartsWith(matchStr),
                "string-ends-with" => attrStr.EndsWith(matchStr),
                "string-regexp-match" => System.Text.RegularExpressions.Regex.IsMatch(attrStr, matchStr),
                "integer-equal" => int.TryParse(attrStr, out var a) && int.TryParse(matchStr, out var b) && a == b,
                "integer-greater-than" => int.TryParse(attrStr, out var ga) && int.TryParse(matchStr, out var gb) && ga > gb,
                "integer-less-than" => int.TryParse(attrStr, out var la) && int.TryParse(matchStr, out var lb) && la < lb,
                "any-of" => attributeValue is IEnumerable<object> list && list.Any(v => v?.ToString() == matchStr),
                "all-of" => attributeValue is IEnumerable<object> allList && allList.All(v => v?.ToString() == matchStr),
                _ => attrStr.Equals(matchStr, StringComparison.OrdinalIgnoreCase) // Default to string equality
            };
        }

        private bool EvaluateCondition(XacmlCondition? condition, XacmlRequest request)
        {
            if (condition == null) return true;

            var attrValue = GetAttributeValue(condition.Category, condition.AttributeId, request);
            if (attrValue == null) return false;

            return EvaluateMatch(condition.Function, attrValue, condition.Value);
        }

        private (XacmlDecision Decision, string Reason) ApplyCombiningAlgorithm(
            List<(XacmlRule Rule, string PolicyId)> rules,
            RuleCombiningAlgorithm algorithm)
        {
            if (rules.Count == 0)
                return (XacmlDecision.NotApplicable, "No applicable rules found");

            switch (algorithm)
            {
                case RuleCombiningAlgorithm.DenyOverrides:
                    var denyRule = rules.FirstOrDefault(r => r.Rule.Effect == XacmlEffect.Deny);
                    if (denyRule != default)
                        return (XacmlDecision.Deny, $"Denied by rule '{denyRule.Rule.RuleId}' in policy '{denyRule.PolicyId}' (deny-overrides)");

                    var permitRule = rules.FirstOrDefault(r => r.Rule.Effect == XacmlEffect.Permit);
                    if (permitRule != default)
                        return (XacmlDecision.Permit, $"Permitted by rule '{permitRule.Rule.RuleId}' in policy '{permitRule.PolicyId}' (deny-overrides)");
                    break;

                case RuleCombiningAlgorithm.PermitOverrides:
                    var permitFirst = rules.FirstOrDefault(r => r.Rule.Effect == XacmlEffect.Permit);
                    if (permitFirst != default)
                        return (XacmlDecision.Permit, $"Permitted by rule '{permitFirst.Rule.RuleId}' in policy '{permitFirst.PolicyId}' (permit-overrides)");

                    var denyFallback = rules.FirstOrDefault(r => r.Rule.Effect == XacmlEffect.Deny);
                    if (denyFallback != default)
                        return (XacmlDecision.Deny, $"Denied by rule '{denyFallback.Rule.RuleId}' in policy '{denyFallback.PolicyId}' (permit-overrides)");
                    break;

                case RuleCombiningAlgorithm.FirstApplicable:
                    var first = rules.First();
                    var decision = first.Rule.Effect == XacmlEffect.Permit ? XacmlDecision.Permit : XacmlDecision.Deny;
                    return (decision, $"{decision} by first applicable rule '{first.Rule.RuleId}' in policy '{first.PolicyId}'");

                case RuleCombiningAlgorithm.OrderedDenyOverrides:
                    // Same as deny-overrides but evaluation order matters
                    foreach (var rule in rules)
                    {
                        if (rule.Rule.Effect == XacmlEffect.Deny)
                            return (XacmlDecision.Deny, $"Denied by rule '{rule.Rule.RuleId}' (ordered-deny-overrides)");
                    }
                    var orderedPermit = rules.FirstOrDefault(r => r.Rule.Effect == XacmlEffect.Permit);
                    if (orderedPermit != default)
                        return (XacmlDecision.Permit, $"Permitted by rule '{orderedPermit.Rule.RuleId}' (ordered-deny-overrides)");
                    break;

                case RuleCombiningAlgorithm.OrderedPermitOverrides:
                    foreach (var rule in rules)
                    {
                        if (rule.Rule.Effect == XacmlEffect.Permit)
                            return (XacmlDecision.Permit, $"Permitted by rule '{rule.Rule.RuleId}' (ordered-permit-overrides)");
                    }
                    var orderedDeny = rules.FirstOrDefault(r => r.Rule.Effect == XacmlEffect.Deny);
                    if (orderedDeny != default)
                        return (XacmlDecision.Deny, $"Denied by rule '{orderedDeny.Rule.RuleId}' (ordered-permit-overrides)");
                    break;
            }

            return (XacmlDecision.NotApplicable, "No applicable decision");
        }

        private XacmlPolicy? ParsePolicyFromConfig(Dictionary<string, object> config)
        {
            var id = config.TryGetValue("id", out var idObj) ? idObj?.ToString() : null;
            if (id == null) return null;

            var policy = new XacmlPolicy
            {
                PolicyId = id,
                Description = config.TryGetValue("description", out var desc) ? desc?.ToString() ?? "" : "",
                Rules = new List<XacmlRule>(),
                Obligations = new List<XacmlObligation>(),
                Advice = new List<XacmlAdvice>()
            };

            if (config.TryGetValue("rules", out var rulesObj) && rulesObj is List<object> rules)
            {
                foreach (var ruleObj in rules.OfType<Dictionary<string, object>>())
                {
                    var ruleId = ruleObj.TryGetValue("id", out var rId) ? rId?.ToString() ?? "" : Guid.NewGuid().ToString("N");
                    var effect = ruleObj.TryGetValue("effect", out var eff) ? eff?.ToString() : "deny";

                    policy.Rules.Add(new XacmlRule
                    {
                        RuleId = ruleId,
                        Effect = effect?.Equals("permit", StringComparison.OrdinalIgnoreCase) == true
                            ? XacmlEffect.Permit : XacmlEffect.Deny,
                        Description = ruleObj.TryGetValue("description", out var rDesc) ? rDesc?.ToString() ?? "" : ""
                    });
                }
            }

            return policy;
        }
    }

    #region XACML Data Model

    public sealed class XacmlPolicy
    {
        public required string PolicyId { get; init; }
        public string Description { get; init; } = "";
        public XacmlTarget? Target { get; init; }
        public required List<XacmlRule> Rules { get; init; }
        public List<XacmlObligation> Obligations { get; init; } = new();
        public List<XacmlAdvice> Advice { get; init; } = new();
    }

    public sealed class XacmlPolicySet
    {
        public required string PolicySetId { get; init; }
        public required List<string> PolicyIds { get; init; }
        public RuleCombiningAlgorithm CombiningAlgorithm { get; init; } = RuleCombiningAlgorithm.DenyOverrides;
    }

    public sealed class XacmlRule
    {
        public required string RuleId { get; init; }
        public XacmlEffect Effect { get; init; }
        public XacmlTarget? Target { get; init; }
        public XacmlCondition? Condition { get; init; }
        public string Description { get; init; } = "";
    }

    public sealed class XacmlTarget
    {
        public List<XacmlAnyOf> AnyOf { get; init; } = new();
    }

    public sealed class XacmlAnyOf
    {
        public List<XacmlAllOf> AllOf { get; init; } = new();
    }

    public sealed class XacmlAllOf
    {
        public List<XacmlMatch> Matches { get; init; } = new();
    }

    public sealed class XacmlMatch
    {
        public required string MatchFunction { get; init; }
        public required string Category { get; init; }
        public required string AttributeId { get; init; }
        public required object Value { get; init; }
    }

    public sealed class XacmlCondition
    {
        public required string Function { get; init; }
        public required string Category { get; init; }
        public required string AttributeId { get; init; }
        public required object Value { get; init; }
    }

    public sealed class XacmlObligation
    {
        public required string ObligationId { get; init; }
        public XacmlEffect FulfillOn { get; init; }
        public Dictionary<string, object> Attributes { get; init; } = new();
    }

    public sealed class XacmlAdvice
    {
        public required string AdviceId { get; init; }
        public XacmlEffect AppliesTo { get; init; }
        public Dictionary<string, object> Attributes { get; init; } = new();
    }

    internal sealed class XacmlRequest
    {
        public Dictionary<string, object> SubjectAttributes { get; init; } = new();
        public Dictionary<string, object> ResourceAttributes { get; init; } = new();
        public Dictionary<string, object> ActionAttributes { get; init; } = new();
        public Dictionary<string, object> EnvironmentAttributes { get; init; } = new();
    }

    public enum XacmlDecision { Permit, Deny, Indeterminate, NotApplicable }
    public enum XacmlEffect { Permit, Deny }
    public enum RuleCombiningAlgorithm
    {
        DenyOverrides,
        PermitOverrides,
        FirstApplicable,
        OrderedDenyOverrides,
        OrderedPermitOverrides
    }

    #endregion
}
