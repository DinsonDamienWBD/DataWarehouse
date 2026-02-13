using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Distributed;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Infrastructure.InMemory
{
    /// <summary>
    /// In-memory single-node implementation of <see cref="IAutoGovernance"/>.
    /// Supports policy registration and performs basic evaluation.
    /// When no policies are registered, all operations are allowed.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: In-memory implementation")]
    public sealed class InMemoryAutoGovernance : IAutoGovernance
    {
        private readonly ConcurrentDictionary<string, GovernancePolicy> _policies = new();

        /// <inheritdoc />
        public event Action<GovernanceEvent>? OnGovernanceEvent;

        /// <inheritdoc />
        public Task<PolicyEvaluationResult> EvaluateAsync(GovernanceContext context, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            var violations = new List<PolicyViolation>();
            var requiredActions = new List<GovernanceAction>();

            foreach (var policy in _policies.Values.Where(p => p.IsEnabled).OrderBy(p => p.Priority))
            {
                if (string.Equals(policy.Expression, context.OperationType, StringComparison.OrdinalIgnoreCase) &&
                    policy.Action == GovernanceAction.Deny)
                {
                    violations.Add(new PolicyViolation
                    {
                        PolicyId = policy.PolicyId,
                        PolicyName = policy.Name,
                        Description = $"Operation '{context.OperationType}' denied by policy '{policy.Name}'",
                        Action = policy.Action
                    });
                    requiredActions.Add(policy.Action);
                }
            }

            OnGovernanceEvent?.Invoke(new GovernanceEvent
            {
                EventType = violations.Count > 0 ? GovernanceEventType.PolicyViolated : GovernanceEventType.PolicyEvaluated,
                PolicyId = violations.FirstOrDefault()?.PolicyId ?? "none",
                ResourceId = context.ResourceId,
                Timestamp = DateTimeOffset.UtcNow
            });

            if (violations.Count > 0)
            {
                return Task.FromResult(PolicyEvaluationResult.Denied(violations, requiredActions));
            }

            return Task.FromResult(PolicyEvaluationResult.Allowed());
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<GovernancePolicy>> GetActivePoliciesAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<GovernancePolicy>>(
                _policies.Values.Where(p => p.IsEnabled).ToList());
        }

        /// <inheritdoc />
        public Task RegisterPolicyAsync(GovernancePolicy policy, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _policies[policy.PolicyId] = policy;

            OnGovernanceEvent?.Invoke(new GovernanceEvent
            {
                EventType = GovernanceEventType.PolicyRegistered,
                PolicyId = policy.PolicyId,
                ResourceId = string.Empty,
                Timestamp = DateTimeOffset.UtcNow
            });

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task DeregisterPolicyAsync(string policyId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _policies.TryRemove(policyId, out _);

            OnGovernanceEvent?.Invoke(new GovernanceEvent
            {
                EventType = GovernanceEventType.PolicyDeregistered,
                PolicyId = policyId,
                ResourceId = string.Empty,
                Timestamp = DateTimeOffset.UtcNow
            });

            return Task.CompletedTask;
        }
    }
}
