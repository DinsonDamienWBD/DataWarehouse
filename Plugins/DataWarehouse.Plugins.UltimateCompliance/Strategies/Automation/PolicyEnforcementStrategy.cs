using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.Automation
{
    /// <summary>
    /// Policy enforcement automation strategy that automatically blocks non-compliant
    /// operations and enforces compliance policies in real-time.
    /// Supports deny/allow lists, attribute-based policies, and automated remediation.
    /// </summary>
    public sealed class PolicyEnforcementStrategy : ComplianceStrategyBase
    {
        private readonly BoundedDictionary<string, CompliancePolicy> _policies = new BoundedDictionary<string, CompliancePolicy>(1000);
        private readonly BoundedDictionary<string, PolicyViolationLog> _violationLog = new BoundedDictionary<string, PolicyViolationLog>(1000);
        private EnforcementMode _enforcementMode = EnforcementMode.Enforce;

        /// <inheritdoc/>
        public override string StrategyId => "policy-enforcement";

        /// <inheritdoc/>
        public override string StrategyName => "Policy Enforcement Automation";

        /// <inheritdoc/>
        public override string Framework => "Policy-Based";

        /// <inheritdoc/>
        public override Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken cancellationToken = default)
        {
            base.InitializeAsync(configuration, cancellationToken);

            // Load enforcement mode
            if (configuration.TryGetValue("EnforcementMode", out var modeObj) && modeObj is string mode)
            {
                _enforcementMode = Enum.TryParse<EnforcementMode>(mode, true, out var parsedMode) ? parsedMode : EnforcementMode.Enforce;
            }

            // Load policies from configuration
            if (configuration.TryGetValue("Policies", out var policiesObj) && policiesObj is List<CompliancePolicy> policies)
            {
                foreach (var policy in policies)
                {
                    _policies[policy.PolicyId] = policy;
                }
            }

            // Load default policies if none provided
            if (_policies.IsEmpty)
            {
                LoadDefaultPolicies();
            }

            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        protected override async Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
        IncrementCounter("policy_enforcement.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();
            var enforcedPolicies = new List<string>();

            // Evaluate all applicable policies
            foreach (var policy in _policies.Values)
            {
                if (!IsPolicyApplicable(policy, context))
                    continue;

                var policyResult = await EvaluatePolicyAsync(policy, context, cancellationToken);

                if (!policyResult.IsCompliant)
                {
                    var violation = new ComplianceViolation
                    {
                        Code = policy.PolicyId,
                        Description = $"Policy violation: {policy.Description}",
                        Severity = MapSeverity(policy.Severity),
                        Remediation = policy.AutoRemediationAction ?? "Manual review required",
                        RegulatoryReference = policy.RegulatoryBasis,
                        AffectedResource = context.ResourceId
                    };

                    violations.Add(violation);

                    // Log violation
                    LogViolation(policy, context, violation);

                    // Auto-remediate if configured and mode allows
                    if (_enforcementMode == EnforcementMode.Enforce && !string.IsNullOrEmpty(policy.AutoRemediationAction))
                    {
                        await ExecuteRemediationAsync(policy, context, cancellationToken);
                    }

                    // Block operation if policy is blocking
                    if (policy.BlockOnViolation && _enforcementMode == EnforcementMode.Enforce)
                    {
                        recommendations.Add($"OPERATION BLOCKED: {policy.Description}");
                    }
                }

                enforcedPolicies.Add(policy.PolicyId);
            }

            // In audit-only mode, never block
            var isCompliant = _enforcementMode == EnforcementMode.AuditOnly || !violations.Any(v => v.Severity >= ViolationSeverity.High);
            var status = _enforcementMode == EnforcementMode.AuditOnly ? ComplianceStatus.RequiresReview :
                        violations.Count == 0 ? ComplianceStatus.Compliant :
                        violations.Any(v => v.Severity >= ViolationSeverity.High) ? ComplianceStatus.NonCompliant :
                        ComplianceStatus.PartiallyCompliant;

            return new ComplianceResult
            {
                IsCompliant = isCompliant,
                Framework = Framework,
                Status = status,
                Violations = violations,
                Recommendations = recommendations,
                Metadata = new Dictionary<string, object>
                {
                    ["EnforcementMode"] = _enforcementMode.ToString(),
                    ["PoliciesEvaluated"] = enforcedPolicies,
                    ["BlockedOperations"] = violations.Count(v => _policies.TryGetValue(v.Code, out var p) && p.BlockOnViolation),
                    ["TotalViolationsLogged"] = _violationLog.Count
                }
            };
        }

        private bool IsPolicyApplicable(CompliancePolicy policy, ComplianceContext context)
        {
            // Check scope filters
            if (policy.Scope != null)
            {
                if (policy.Scope.OperationTypes != null && !policy.Scope.OperationTypes.Contains(context.OperationType, StringComparer.OrdinalIgnoreCase))
                    return false;

                if (policy.Scope.DataClassifications != null && !policy.Scope.DataClassifications.Contains(context.DataClassification, StringComparer.OrdinalIgnoreCase))
                    return false;

                if (policy.Scope.Locations != null && !string.IsNullOrEmpty(context.SourceLocation) &&
                    !policy.Scope.Locations.Any(l => context.SourceLocation.Contains(l, StringComparison.OrdinalIgnoreCase)))
                    return false;
            }

            return true;
        }

        private async Task<PolicyEvaluationResult> EvaluatePolicyAsync(CompliancePolicy policy, ComplianceContext context, CancellationToken cancellationToken)
        {
            // Evaluate policy conditions
            foreach (var condition in policy.Conditions)
            {
                var conditionMet = condition.Type switch
                {
                    PolicyConditionType.RequireAttribute => context.Attributes.ContainsKey(condition.AttributeName ?? ""),
                    PolicyConditionType.AttributeEquals => context.Attributes.TryGetValue(condition.AttributeName ?? "", out var val) &&
                                                           val?.ToString() == condition.ExpectedValue?.ToString(),
                    PolicyConditionType.DenyAttribute => !context.Attributes.ContainsKey(condition.AttributeName ?? ""),
                    PolicyConditionType.AllowList => condition.AllowedValues?.Contains(
                        context.Attributes.TryGetValue(condition.AttributeName ?? "", out var v) ? v?.ToString() : null,
                        StringComparer.OrdinalIgnoreCase) ?? false,
                    PolicyConditionType.DenyList => !(condition.DeniedValues?.Contains(
                        context.Attributes.TryGetValue(condition.AttributeName ?? "", out var v) ? v?.ToString() : null,
                        StringComparer.OrdinalIgnoreCase) ?? false),
                    PolicyConditionType.Custom => condition.CustomEvaluator?.Invoke(context) ?? true,
                    _ => true
                };

                if (!conditionMet)
                {
                    return new PolicyEvaluationResult
                    {
                        IsCompliant = false,
                        ViolatedCondition = condition.Type.ToString()
                    };
                }
            }

            return new PolicyEvaluationResult { IsCompliant = true };
        }

        private async Task ExecuteRemediationAsync(CompliancePolicy policy, ComplianceContext context, CancellationToken cancellationToken)
        {
            // Execute auto-remediation action
            // In a production system, this would integrate with actual remediation systems
            await Task.CompletedTask;
        }

        private void LogViolation(CompliancePolicy policy, ComplianceContext context, ComplianceViolation violation)
        {
            var logId = $"{policy.PolicyId}:{context.OperationType}:{DateTime.UtcNow.Ticks}";
            _violationLog[logId] = new PolicyViolationLog
            {
                LogId = logId,
                PolicyId = policy.PolicyId,
                Context = context,
                Violation = violation,
                Timestamp = DateTime.UtcNow,
                EnforcementMode = _enforcementMode
            };

            // Clean up old logs (keep last 10,000 entries)
            if (_violationLog.Count > 10000)
            {
                var oldestEntries = _violationLog.OrderBy(kvp => kvp.Value.Timestamp).Take(1000).Select(kvp => kvp.Key).ToList();
                foreach (var key in oldestEntries)
                {
                    _violationLog.TryRemove(key, out _);
                }
            }
        }

        private void LoadDefaultPolicies()
        {
            // Personal data protection policy
            _policies["POL-001"] = new CompliancePolicy
            {
                PolicyId = "POL-001",
                Name = "Personal Data Encryption",
                Description = "All personal data must be encrypted at rest and in transit",
                Severity = "Critical",
                RegulatoryBasis = "GDPR Article 32, HIPAA Security Rule",
                BlockOnViolation = true,
                AutoRemediationAction = "Enable encryption",
                Scope = new PolicyScope
                {
                    DataClassifications = new[] { "personal", "sensitive", "health", "pii" }
                },
                Conditions = new[]
                {
                    new PolicyCondition
                    {
                        Type = PolicyConditionType.RequireAttribute,
                        AttributeName = "Encrypted"
                    },
                    new PolicyCondition
                    {
                        Type = PolicyConditionType.AttributeEquals,
                        AttributeName = "Encrypted",
                        ExpectedValue = true
                    }
                }
            };

            // Cross-border transfer policy
            _policies["POL-002"] = new CompliancePolicy
            {
                PolicyId = "POL-002",
                Name = "Cross-Border Transfer Controls",
                Description = "Cross-border data transfers require approved transfer mechanism",
                Severity = "High",
                RegulatoryBasis = "GDPR Chapter V",
                BlockOnViolation = true,
                Scope = new PolicyScope
                {
                    OperationTypes = new[] { "transfer", "export", "replicate" }
                },
                Conditions = new[]
                {
                    new PolicyCondition
                    {
                        Type = PolicyConditionType.Custom,
                        CustomEvaluator = ctx => string.IsNullOrEmpty(ctx.SourceLocation) ||
                                                string.IsNullOrEmpty(ctx.DestinationLocation) ||
                                                ctx.SourceLocation == ctx.DestinationLocation ||
                                                ctx.Attributes.ContainsKey("TransferMechanism")
                    }
                }
            };

            // Audit trail requirement
            _policies["POL-003"] = new CompliancePolicy
            {
                PolicyId = "POL-003",
                Name = "Mandatory Audit Logging",
                Description = "All operations on sensitive data must be audited",
                Severity = "High",
                RegulatoryBasis = "SOX Section 404, HIPAA Security Rule",
                BlockOnViolation = false,
                Scope = new PolicyScope
                {
                    DataClassifications = new[] { "financial", "health", "sensitive" }
                },
                Conditions = new[]
                {
                    new PolicyCondition
                    {
                        Type = PolicyConditionType.RequireAttribute,
                        AttributeName = "AuditEnabled"
                    }
                }
            };

            // Consent requirement policy
            _policies["POL-004"] = new CompliancePolicy
            {
                PolicyId = "POL-004",
                Name = "Consent Required",
                Description = "Processing personal data requires valid consent or lawful basis",
                Severity = "Critical",
                RegulatoryBasis = "GDPR Article 6",
                BlockOnViolation = true,
                Scope = new PolicyScope
                {
                    DataClassifications = new[] { "personal", "pii" },
                    OperationTypes = new[] { "process", "analyze", "profile" }
                },
                Conditions = new[]
                {
                    new PolicyCondition
                    {
                        Type = PolicyConditionType.RequireAttribute,
                        AttributeName = "LawfulBasis"
                    }
                }
            };
        }

        private ViolationSeverity MapSeverity(string severity)
        {
            return severity?.ToLowerInvariant() switch
            {
                "critical" => ViolationSeverity.Critical,
                "high" => ViolationSeverity.High,
                "medium" => ViolationSeverity.Medium,
                "low" => ViolationSeverity.Low,
                _ => ViolationSeverity.Medium
            };
        }

        private sealed class PolicyEvaluationResult
        {
            public required bool IsCompliant { get; init; }
            public string? ViolatedCondition { get; init; }
        }

        private sealed class PolicyViolationLog
        {
            public required string LogId { get; init; }
            public required string PolicyId { get; init; }
            public required ComplianceContext Context { get; init; }
            public required ComplianceViolation Violation { get; init; }
            public required DateTime Timestamp { get; init; }
            public required EnforcementMode EnforcementMode { get; init; }
        }
    
    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        IncrementCounter("policy_enforcement.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
        IncrementCounter("policy_enforcement.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}

    /// <summary>
    /// Enforcement mode for policy automation.
    /// </summary>
    public enum EnforcementMode
    {
        /// <summary>Policies are actively enforced and can block operations</summary>
        Enforce,
        /// <summary>Policy violations are logged but operations are not blocked</summary>
        AuditOnly,
        /// <summary>Enforcement is disabled</summary>
        Disabled
    }

    /// <summary>
    /// Represents a compliance policy for enforcement.
    /// </summary>
    public sealed class CompliancePolicy
    {
        public required string PolicyId { get; init; }
        public required string Name { get; init; }
        public required string Description { get; init; }
        public required string Severity { get; init; }
        public string? RegulatoryBasis { get; init; }
        public required bool BlockOnViolation { get; init; }
        public string? AutoRemediationAction { get; init; }
        public PolicyScope? Scope { get; init; }
        public required PolicyCondition[] Conditions { get; init; }
    }

    /// <summary>
    /// Defines the scope where a policy applies.
    /// </summary>
    public sealed class PolicyScope
    {
        public string[]? OperationTypes { get; init; }
        public string[]? DataClassifications { get; init; }
        public string[]? Locations { get; init; }
        public string[]? UserRoles { get; init; }
    }

    /// <summary>
    /// Represents a policy condition that must be met.
    /// </summary>
    public sealed class PolicyCondition
    {
        public required PolicyConditionType Type { get; init; }
        public string? AttributeName { get; init; }
        public object? ExpectedValue { get; init; }
        public string[]? AllowedValues { get; init; }
        public string[]? DeniedValues { get; init; }
        public Func<ComplianceContext, bool>? CustomEvaluator { get; init; }
    }

    /// <summary>
    /// Types of policy conditions.
    /// </summary>
    public enum PolicyConditionType
    {
        RequireAttribute,
        AttributeEquals,
        DenyAttribute,
        AllowList,
        DenyList,
        Custom
    }
}
