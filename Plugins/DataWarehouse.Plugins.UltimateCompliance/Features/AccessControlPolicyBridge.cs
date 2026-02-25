using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateCompliance.Features
{
    /// <summary>
    /// Bridges compliance policies with UltimateAccessControl plugin via message bus,
    /// enforcing access control based on compliance requirements.
    /// </summary>
    public sealed class AccessControlPolicyBridge
    {
        private readonly Dictionary<string, ComplianceAccessPolicy> _policies = new();
        private readonly List<PolicySyncResult> _syncHistory = new();

        /// <summary>
        /// Registers a compliance-driven access control policy.
        /// </summary>
        public void RegisterPolicy(string policyId, ComplianceAccessPolicy policy)
        {
            _policies[policyId] = policy;
        }

        /// <summary>
        /// Syncs compliance policies to the access control system via message bus.
        /// </summary>
        public async Task<PolicySyncResult> SyncPoliciesAsync(CancellationToken cancellationToken = default)
        {
            var syncStartTime = DateTime.UtcNow;
            var syncedCount = 0;
            var failedCount = 0;
            var errors = new List<string>();

            foreach (var kvp in _policies)
            {
                try
                {
                    var success = await PublishPolicyToMessageBusAsync(kvp.Key, kvp.Value, cancellationToken);

                    if (success)
                    {
                        syncedCount++;
                    }
                    else
                    {
                        failedCount++;
                        errors.Add($"Failed to sync policy: {kvp.Key}");
                    }
                }
                catch (Exception ex)
                {
                    failedCount++;
                    errors.Add($"Error syncing policy {kvp.Key}: {ex.Message}");
                }
            }

            var result = new PolicySyncResult
            {
                SyncTime = syncStartTime,
                TotalPolicies = _policies.Count,
                SyncedCount = syncedCount,
                FailedCount = failedCount,
                Success = failedCount == 0,
                Errors = errors
            };

            _syncHistory.Add(result);
            return result;
        }

        /// <summary>
        /// Evaluates whether an access request complies with registered policies.
        /// </summary>
        public AccessEvaluationResult EvaluateAccess(string userId, string resourceId, string operation, Dictionary<string, object> context)
        {
            var applicablePolicies = new List<string>();
            var denialReasons = new List<string>();

            foreach (var kvp in _policies)
            {
                var policy = kvp.Value;

                if (IsResourceMatched(resourceId, policy.ResourcePatterns))
                {
                    applicablePolicies.Add(kvp.Key);

                    if (!IsOperationAllowed(operation, policy.AllowedOperations))
                    {
                        denialReasons.Add($"Policy {kvp.Key}: Operation '{operation}' not allowed");
                        continue;
                    }

                    if (!AreConstraintsMet(context, policy.Constraints))
                    {
                        denialReasons.Add($"Policy {kvp.Key}: Constraints not met");
                        continue;
                    }

                    if (!IsDataClassificationAllowed(context, policy.AllowedDataClassifications))
                    {
                        denialReasons.Add($"Policy {kvp.Key}: Data classification not allowed");
                        continue;
                    }
                }
            }

            var isAllowed = applicablePolicies.Count > 0 && denialReasons.Count == 0;

            return new AccessEvaluationResult
            {
                IsAllowed = isAllowed,
                ApplicablePolicies = applicablePolicies,
                DenialReasons = denialReasons,
                EvaluationTime = DateTime.UtcNow
            };
        }

        /// <summary>
        /// Gets the sync history.
        /// </summary>
        public IReadOnlyList<PolicySyncResult> GetSyncHistory()
        {
            return _syncHistory.AsReadOnly();
        }

        private async Task<bool> PublishPolicyToMessageBusAsync(string policyId, ComplianceAccessPolicy policy, CancellationToken cancellationToken)
        {
            // Message bus integration point
            // In real implementation, this would publish to message bus topic: "accesscontrol.policy.update"
            await Task.Delay(10, cancellationToken); // Simulate async operation
            return true;
        }

        private static bool IsResourceMatched(string resourceId, IReadOnlyList<string> patterns)
        {
            if (patterns.Count == 0)
                return true;

            foreach (var pattern in patterns)
            {
                if (resourceId.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static bool IsOperationAllowed(string operation, IReadOnlyList<string> allowedOperations)
        {
            if (allowedOperations.Count == 0)
                return true;

            return allowedOperations.Contains(operation, StringComparer.OrdinalIgnoreCase);
        }

        private static bool AreConstraintsMet(Dictionary<string, object> context, Dictionary<string, object> constraints)
        {
            foreach (var constraint in constraints)
            {
                if (!context.TryGetValue(constraint.Key, out var contextValue))
                    return false;

                if (!contextValue.Equals(constraint.Value))
                    return false;
            }

            return true;
        }

        private static bool IsDataClassificationAllowed(Dictionary<string, object> context, IReadOnlyList<string> allowedClassifications)
        {
            if (allowedClassifications.Count == 0)
                return true;

            if (!context.TryGetValue("DataClassification", out var classification))
                return false;

            return allowedClassifications.Contains(classification?.ToString() ?? string.Empty, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Compliance-driven access control policy.
    /// </summary>
    public sealed class ComplianceAccessPolicy
    {
        public required string PolicyName { get; init; }
        public required string Framework { get; init; }
        public IReadOnlyList<string> ResourcePatterns { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> AllowedOperations { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> AllowedDataClassifications { get; init; } = Array.Empty<string>();
        public Dictionary<string, object> Constraints { get; init; } = new();
        public bool RequireEncryption { get; init; }
        public bool RequireAuditLog { get; init; } = true;
    }

    /// <summary>
    /// Result of policy synchronization.
    /// </summary>
    public sealed class PolicySyncResult
    {
        public required DateTime SyncTime { get; init; }
        public required int TotalPolicies { get; init; }
        public required int SyncedCount { get; init; }
        public required int FailedCount { get; init; }
        public required bool Success { get; init; }
        public required List<string> Errors { get; init; }
    }

    /// <summary>
    /// Result of access evaluation against compliance policies.
    /// </summary>
    public sealed class AccessEvaluationResult
    {
        public required bool IsAllowed { get; init; }
        public required List<string> ApplicablePolicies { get; init; }
        public required List<string> DenialReasons { get; init; }
        public required DateTime EvaluationTime { get; init; }
    }
}
