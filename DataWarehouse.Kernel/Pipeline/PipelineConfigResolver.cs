using DataWarehouse.SDK.Contracts.Pipeline;
using System.Collections.Concurrent;

namespace DataWarehouse.Kernel.Pipeline;

/// <summary>
/// Default implementation of IPipelineConfigProvider that resolves effective pipeline policies
/// by walking the hierarchy: Instance → UserGroup → User → Operation.
///
/// Merge logic:
/// - For each stage and field, walk from Operation → User → Group → Instance until a non-null value is found
/// - Enforce AllowChildOverride: if a parent level sets this to false, child levels cannot override that stage
/// - Thread-safe using ConcurrentDictionary for in-memory storage
/// </summary>
public sealed class PipelineConfigResolver : IPipelineConfigProvider
{
    private readonly ConcurrentDictionary<(PolicyLevel, string), PipelinePolicy> _policies = new();

    /// <summary>
    /// Resolves the effective pipeline policy for a given user/group/operation context.
    /// Walks the policy hierarchy and merges policies from Instance → UserGroup → User → Operation.
    /// </summary>
    /// <param name="userId">User ID for User-level policy resolution. Null to skip User level.</param>
    /// <param name="groupId">Group ID for UserGroup-level policy resolution. Null to skip UserGroup level.</param>
    /// <param name="operationId">Operation ID for Operation-level policy resolution. Null to skip Operation level.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The merged effective pipeline policy.</returns>
    public Task<PipelinePolicy> ResolveEffectivePolicyAsync(
        string? userId = null,
        string? groupId = null,
        string? operationId = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Build policy chain: Instance → UserGroup? → User? → Operation?
        var policyChain = new List<PipelinePolicy?>();

        // 1. Instance level (required, fallback to empty if not set)
        var instancePolicy = GetPolicyAsync(PolicyLevel.Instance, "default", ct).Result;
        policyChain.Add(instancePolicy ?? CreateDefaultInstancePolicy());

        // 2. UserGroup level (optional)
        if (!string.IsNullOrEmpty(groupId))
        {
            var groupPolicy = GetPolicyAsync(PolicyLevel.UserGroup, groupId, ct).Result;
            policyChain.Add(groupPolicy);
        }
        else
        {
            policyChain.Add(null);
        }

        // 3. User level (optional)
        if (!string.IsNullOrEmpty(userId))
        {
            var userPolicy = GetPolicyAsync(PolicyLevel.User, userId, ct).Result;
            policyChain.Add(userPolicy);
        }
        else
        {
            policyChain.Add(null);
        }

        // 4. Operation level (optional)
        if (!string.IsNullOrEmpty(operationId))
        {
            var operationPolicy = GetPolicyAsync(PolicyLevel.Operation, operationId, ct).Result;
            policyChain.Add(operationPolicy);
        }
        else
        {
            policyChain.Add(null);
        }

        // Merge policies: per-stage, first non-null wins (with AllowChildOverride enforcement)
        var effectivePolicy = MergePolicies(policyChain);

        return Task.FromResult(effectivePolicy);
    }

    /// <summary>
    /// Gets a policy at a specific level and scope.
    /// </summary>
    /// <param name="level">The policy level (Instance, UserGroup, User, Operation).</param>
    /// <param name="scopeId">The scope identifier (instance ID, group ID, user ID, or operation ID).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The policy if found, otherwise null.</returns>
    public Task<PipelinePolicy?> GetPolicyAsync(PolicyLevel level, string scopeId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        _policies.TryGetValue((level, scopeId), out var policy);
        return Task.FromResult(policy);
    }

    /// <summary>
    /// Sets or updates a policy at a specific level and scope.
    /// Validates immutability before updating.
    /// </summary>
    /// <param name="policy">The policy to set or update.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task SetPolicyAsync(PipelinePolicy policy, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ct.ThrowIfCancellationRequested();

        var key = (policy.Level, policy.ScopeId);

        // Check if existing policy is immutable
        if (_policies.TryGetValue(key, out var existing) && existing.IsImmutable)
        {
            throw new InvalidOperationException(
                $"Cannot update immutable policy at level {policy.Level}, scope {policy.ScopeId}. " +
                "Immutable policies can only be deleted and recreated.");
        }

        // Increment version if updating existing policy
        var newPolicy = policy;
        if (existing != null)
        {
            // PipelinePolicy is a class, so create a new instance with updated version
            newPolicy = new PipelinePolicy
            {
                PolicyId = policy.PolicyId,
                Name = policy.Name,
                Level = policy.Level,
                ScopeId = policy.ScopeId,
                Stages = policy.Stages,
                StageOrder = policy.StageOrder,
                Version = existing.Version + 1,
                UpdatedAt = DateTimeOffset.UtcNow,
                UpdatedBy = policy.UpdatedBy,
                MigrationBehavior = policy.MigrationBehavior,
                IsImmutable = policy.IsImmutable,
                Description = policy.Description
            };
        }

        _policies[key] = newPolicy;

        return Task.CompletedTask;
    }

    /// <summary>
    /// Deletes a policy at a specific level and scope.
    /// </summary>
    /// <param name="level">The policy level to delete.</param>
    /// <param name="scopeId">The scope identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the policy was deleted, false if it didn't exist.</returns>
    public Task<bool> DeletePolicyAsync(PolicyLevel level, string scopeId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var removed = _policies.TryRemove((level, scopeId), out _);
        return Task.FromResult(removed);
    }

    /// <summary>
    /// Lists all policies at a given level.
    /// </summary>
    /// <param name="level">The policy level to list.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A read-only list of policies at the specified level.</returns>
    public Task<IReadOnlyList<PipelinePolicy>> ListPoliciesAsync(PolicyLevel level, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var policies = _policies
            .Where(kvp => kvp.Key.Item1 == level)
            .Select(kvp => kvp.Value)
            .ToList();

        return Task.FromResult<IReadOnlyList<PipelinePolicy>>(policies);
    }

    /// <summary>
    /// Visualizes the resolved pipeline for a given context, showing which level
    /// each setting was inherited from.
    /// </summary>
    /// <param name="userId">User ID for User-level policy resolution.</param>
    /// <param name="groupId">Group ID for UserGroup-level policy resolution.</param>
    /// <param name="operationId">Operation ID for Operation-level policy resolution.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Visualization showing which level provided each setting.</returns>
    public Task<EffectivePolicyVisualization> VisualizeEffectivePolicyAsync(
        string? userId = null,
        string? groupId = null,
        string? operationId = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Build policy chain
        var policyChain = new List<PipelinePolicy?>();

        var instancePolicy = GetPolicyAsync(PolicyLevel.Instance, "default", ct).Result;
        policyChain.Add(instancePolicy ?? CreateDefaultInstancePolicy());

        if (!string.IsNullOrEmpty(groupId))
        {
            var groupPolicy = GetPolicyAsync(PolicyLevel.UserGroup, groupId, ct).Result;
            policyChain.Add(groupPolicy);
        }
        else
        {
            policyChain.Add(null);
        }

        if (!string.IsNullOrEmpty(userId))
        {
            var userPolicy = GetPolicyAsync(PolicyLevel.User, userId, ct).Result;
            policyChain.Add(userPolicy);
        }
        else
        {
            policyChain.Add(null);
        }

        if (!string.IsNullOrEmpty(operationId))
        {
            var operationPolicy = GetPolicyAsync(PolicyLevel.Operation, operationId, ct).Result;
            policyChain.Add(operationPolicy);
        }
        else
        {
            policyChain.Add(null);
        }

        // Merge with attribution tracking
        var (effectivePolicy, attributions) = MergePoliciesWithAttribution(policyChain);

        return Task.FromResult(new EffectivePolicyVisualization
        {
            ResolvedPolicy = effectivePolicy,
            StageAttributions = attributions
        });
    }

    /// <summary>
    /// Merges a policy chain from Instance → UserGroup → User → Operation.
    /// Per-stage field: first non-null wins (walking from Operation → Instance),
    /// but respects AllowChildOverride enforcement.
    /// </summary>
    private PipelinePolicy MergePolicies(List<PipelinePolicy?> policyChain)
    {
        // Get all unique stage types across all policies
        var allStageTypes = policyChain
            .Where(p => p != null)
            .SelectMany(p => p!.Stages.Select(s => s.StageType))
            .Distinct()
            .ToList();

        // Build merged stages
        var mergedStages = new List<PipelineStagePolicy>();

        foreach (var stageType in allStageTypes)
        {
            var mergedStage = MergeStage(stageType, policyChain);
            if (mergedStage != null)
            {
                mergedStages.Add(mergedStage);
            }
        }

        // Merge StageOrder: first non-null wins from Operation → Instance
        List<string>? stageOrder = null;
        for (int i = policyChain.Count - 1; i >= 0; i--)
        {
            if (policyChain[i]?.StageOrder != null)
            {
                stageOrder = policyChain[i]!.StageOrder;
                break;
            }
        }

        // Use the highest level policy as the base
        var basePolicy = policyChain.FirstOrDefault(p => p != null)!;

        return new PipelinePolicy
        {
            PolicyId = Guid.NewGuid().ToString("N"),
            Name = "Effective Policy",
            Level = PolicyLevel.Operation, // Effective policy is at the most specific level
            ScopeId = "merged",
            Stages = mergedStages,
            StageOrder = stageOrder,
            Version = basePolicy.Version,
            UpdatedAt = DateTimeOffset.UtcNow,
            UpdatedBy = "PipelineConfigResolver",
            MigrationBehavior = basePolicy.MigrationBehavior,
            IsImmutable = false,
            Description = "Merged effective policy from hierarchy"
        };
    }

    /// <summary>
    /// Merges a single stage across the policy hierarchy.
    /// Walks from Operation → User → UserGroup → Instance, taking first non-null value for each field.
    /// Enforces AllowChildOverride: if a parent locks a stage, child levels cannot override it.
    /// </summary>
    private PipelineStagePolicy? MergeStage(string stageType, List<PipelinePolicy?> policyChain)
    {
        // Check if any parent level has locked this stage (AllowChildOverride = false)
        // Walk from Instance → Operation to find lock points
        var lockedAtLevel = -1;
        for (int i = 0; i < policyChain.Count; i++)
        {
            var policy = policyChain[i];
            if (policy == null) continue;

            var stage = policy.Stages.FirstOrDefault(s => s.StageType == stageType);
            if (stage != null && !stage.AllowChildOverride)
            {
                lockedAtLevel = i;
                break;
            }
        }

        // If locked, only consider policies up to and including the lock level
        var effectivePolicyChain = lockedAtLevel >= 0
            ? policyChain.Take(lockedAtLevel + 1).ToList()
            : policyChain;

        // Now merge fields from Operation → Instance (reverse walk)
        bool? enabled = null;
        string? pluginId = null;
        string? strategyName = null;
        int? order = null;
        Dictionary<string, object>? parameters = null;
        bool allowChildOverride = true;

        for (int i = effectivePolicyChain.Count - 1; i >= 0; i--)
        {
            var policy = effectivePolicyChain[i];
            if (policy == null) continue;

            var stage = policy.Stages.FirstOrDefault(s => s.StageType == stageType);
            if (stage == null) continue;

            // First non-null wins for each field
            enabled ??= stage.Enabled;
            pluginId ??= stage.PluginId;
            strategyName ??= stage.StrategyName;
            order ??= stage.Order;

            // Parameters: merge dictionaries (child overrides parent keys)
            if (stage.Parameters != null)
            {
                parameters ??= new Dictionary<string, object>();
                foreach (var kvp in stage.Parameters)
                {
                    if (!parameters.ContainsKey(kvp.Key))
                    {
                        parameters[kvp.Key] = kvp.Value;
                    }
                }
            }

            // AllowChildOverride from the highest level that defines this stage
            if (i == 0 || effectivePolicyChain[i] != null)
            {
                allowChildOverride = stage.AllowChildOverride;
            }
        }

        // If no fields were found, stage doesn't exist in hierarchy
        if (enabled == null && pluginId == null && strategyName == null && order == null)
        {
            return null;
        }

        return new PipelineStagePolicy
        {
            StageType = stageType,
            Enabled = enabled ?? true, // Default to enabled if not specified
            PluginId = pluginId,
            StrategyName = strategyName,
            Order = order ?? 100, // Default order
            Parameters = parameters,
            AllowChildOverride = allowChildOverride
        };
    }

    /// <summary>
    /// Merges policies with attribution tracking for visualization.
    /// Returns both the merged policy and attributions showing which level provided each value.
    /// </summary>
    private (PipelinePolicy effectivePolicy, List<EffectivePolicyVisualization.StageAttribution> attributions)
        MergePoliciesWithAttribution(List<PipelinePolicy?> policyChain)
    {
        var attributions = new List<EffectivePolicyVisualization.StageAttribution>();

        // Get all unique stage types
        var allStageTypes = policyChain
            .Where(p => p != null)
            .SelectMany(p => p!.Stages.Select(s => s.StageType))
            .Distinct()
            .ToList();

        var mergedStages = new List<PipelineStagePolicy>();

        foreach (var stageType in allStageTypes)
        {
            var (mergedStage, stageAttributions) = MergeStageWithAttribution(stageType, policyChain);
            if (mergedStage != null)
            {
                mergedStages.Add(mergedStage);
                attributions.AddRange(stageAttributions);
            }
        }

        // Merge StageOrder with attribution
        List<string>? stageOrder = null;
        for (int i = policyChain.Count - 1; i >= 0; i--)
        {
            if (policyChain[i]?.StageOrder != null)
            {
                stageOrder = policyChain[i]!.StageOrder;
                attributions.Add(new EffectivePolicyVisualization.StageAttribution
                {
                    StageType = "Global",
                    FieldName = "StageOrder",
                    SourceLevel = policyChain[i]!.Level,
                    SourcePolicyId = policyChain[i]!.PolicyId,
                    Value = stageOrder
                });
                break;
            }
        }

        var basePolicy = policyChain.FirstOrDefault(p => p != null)!;

        var effectivePolicy = new PipelinePolicy
        {
            PolicyId = Guid.NewGuid().ToString("N"),
            Name = "Effective Policy",
            Level = PolicyLevel.Operation,
            ScopeId = "merged",
            Stages = mergedStages,
            StageOrder = stageOrder,
            Version = basePolicy.Version,
            UpdatedAt = DateTimeOffset.UtcNow,
            UpdatedBy = "PipelineConfigResolver",
            MigrationBehavior = basePolicy.MigrationBehavior,
            IsImmutable = false,
            Description = "Merged effective policy from hierarchy"
        };

        return (effectivePolicy, attributions);
    }

    /// <summary>
    /// Merges a single stage with attribution tracking.
    /// </summary>
    private (PipelineStagePolicy? stage, List<EffectivePolicyVisualization.StageAttribution> attributions)
        MergeStageWithAttribution(string stageType, List<PipelinePolicy?> policyChain)
    {
        var attributions = new List<EffectivePolicyVisualization.StageAttribution>();

        // Check for lock
        var lockedAtLevel = -1;
        for (int i = 0; i < policyChain.Count; i++)
        {
            var policy = policyChain[i];
            if (policy == null) continue;

            var stage = policy.Stages.FirstOrDefault(s => s.StageType == stageType);
            if (stage != null && !stage.AllowChildOverride)
            {
                lockedAtLevel = i;
                break;
            }
        }

        var effectivePolicyChain = lockedAtLevel >= 0
            ? policyChain.Take(lockedAtLevel + 1).ToList()
            : policyChain;

        bool? enabled = null;
        string? pluginId = null;
        string? strategyName = null;
        int? order = null;
        Dictionary<string, object>? parameters = null;
        bool allowChildOverride = true;

        // Walk from Operation → Instance and track first non-null for each field
        for (int i = effectivePolicyChain.Count - 1; i >= 0; i--)
        {
            var policy = effectivePolicyChain[i];
            if (policy == null) continue;

            var stage = policy.Stages.FirstOrDefault(s => s.StageType == stageType);
            if (stage == null) continue;

            if (enabled == null && stage.Enabled != null)
            {
                enabled = stage.Enabled;
                attributions.Add(new EffectivePolicyVisualization.StageAttribution
                {
                    StageType = stageType,
                    FieldName = "Enabled",
                    SourceLevel = policy.Level,
                    SourcePolicyId = policy.PolicyId,
                    Value = enabled
                });
            }

            if (pluginId == null && stage.PluginId != null)
            {
                pluginId = stage.PluginId;
                attributions.Add(new EffectivePolicyVisualization.StageAttribution
                {
                    StageType = stageType,
                    FieldName = "PluginId",
                    SourceLevel = policy.Level,
                    SourcePolicyId = policy.PolicyId,
                    Value = pluginId
                });
            }

            if (strategyName == null && stage.StrategyName != null)
            {
                strategyName = stage.StrategyName;
                attributions.Add(new EffectivePolicyVisualization.StageAttribution
                {
                    StageType = stageType,
                    FieldName = "StrategyName",
                    SourceLevel = policy.Level,
                    SourcePolicyId = policy.PolicyId,
                    Value = strategyName
                });
            }

            if (order == null && stage.Order != null)
            {
                order = stage.Order;
                attributions.Add(new EffectivePolicyVisualization.StageAttribution
                {
                    StageType = stageType,
                    FieldName = "Order",
                    SourceLevel = policy.Level,
                    SourcePolicyId = policy.PolicyId,
                    Value = order
                });
            }

            if (stage.Parameters != null)
            {
                parameters ??= new Dictionary<string, object>();
                foreach (var kvp in stage.Parameters)
                {
                    if (!parameters.ContainsKey(kvp.Key))
                    {
                        parameters[kvp.Key] = kvp.Value;
                        attributions.Add(new EffectivePolicyVisualization.StageAttribution
                        {
                            StageType = stageType,
                            FieldName = $"Parameters.{kvp.Key}",
                            SourceLevel = policy.Level,
                            SourcePolicyId = policy.PolicyId,
                            Value = kvp.Value
                        });
                    }
                }
            }
        }

        if (enabled == null && pluginId == null && strategyName == null && order == null)
        {
            return (null, attributions);
        }

        var mergedStage = new PipelineStagePolicy
        {
            StageType = stageType,
            Enabled = enabled ?? true,
            PluginId = pluginId,
            StrategyName = strategyName,
            Order = order ?? 100,
            Parameters = parameters,
            AllowChildOverride = allowChildOverride
        };

        return (mergedStage, attributions);
    }

    /// <summary>
    /// Creates a default instance-level policy with Compression → Encryption.
    /// </summary>
    private static PipelinePolicy CreateDefaultInstancePolicy()
    {
        return new PipelinePolicy
        {
            PolicyId = "default-instance",
            Name = "Default Instance Policy",
            Level = PolicyLevel.Instance,
            ScopeId = "default",
            Stages = new List<PipelineStagePolicy>
            {
                new()
                {
                    StageType = "Compression",
                    Enabled = true,
                    Order = 100,
                    AllowChildOverride = true
                },
                new()
                {
                    StageType = "Encryption",
                    Enabled = true,
                    Order = 200,
                    AllowChildOverride = true
                }
            },
            StageOrder = new List<string> { "Compression", "Encryption" },
            Version = 1,
            UpdatedAt = DateTimeOffset.UtcNow,
            UpdatedBy = "System",
            MigrationBehavior = MigrationBehavior.KeepExisting,
            IsImmutable = false,
            Description = "Default system pipeline policy"
        };
    }
}
