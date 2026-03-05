// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Collections.Concurrent;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.SDK.Security;

/// <summary>
/// Multi-level access verification engine. Evaluates permissions across the
/// System → Tenant → Instance → UserGroup → User hierarchy.
///
/// UNIVERSAL ENFORCEMENT: Every action in the system MUST pass through this matrix.
/// No exceptions — manual, automatic, user, AI, scheduler, webhook, internal service.
///
/// Deny at ANY level = absolute DENY. No exceptions.
/// Allow requires explicit grant at appropriate level with no deny at any level.
/// No rule at any level = default DENY.
/// </summary>
public sealed class AccessVerificationMatrix
{
    private readonly BoundedDictionary<HierarchyLevel, ConcurrentBag<HierarchyAccessRule>> _rules = new BoundedDictionary<HierarchyLevel, ConcurrentBag<HierarchyAccessRule>>(1000);
    private readonly object _evaluationLock = new();

    public AccessVerificationMatrix()
    {
        // Initialize rule stores for each level
        foreach (var level in Enum.GetValues<HierarchyLevel>())
        {
            _rules[level] = new ConcurrentBag<HierarchyAccessRule>();
        }
    }

    /// <summary>
    /// Evaluate whether the effective principal in the CommandIdentity
    /// is allowed to perform the specified action on the specified resource.
    ///
    /// Walk from System (level 0) to User (level 4):
    /// - If explicit DENY at any level → return DENY immediately (short-circuit)
    /// - If explicit ALLOW → mark "allow found", continue to check for denies
    /// - If no rule → continue
    /// After all levels: ALLOW only if at least one ALLOW found and no DENY found.
    /// No rules at any level → DENY (default deny).
    /// </summary>
    public AccessVerdict Evaluate(CommandIdentity identity, string resource, string action)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);

        var levelResults = new List<HierarchyLevelResult>();
        HierarchyLevel? allowLevel = null;
        string? allowRuleId = null;
        string? allowReason = null;

        // Step 1: System-level check (maintenance mode, global bans)
        var systemResult = EvaluateLevel(HierarchyLevel.System, "system", identity.EffectivePrincipalId, resource, action);
        levelResults.Add(systemResult);
        if (systemResult.Decision == LevelDecision.Deny)
        {
            return AccessVerdict.Denied(identity, resource, action, HierarchyLevel.System,
                systemResult.RuleId ?? "system-deny", systemResult.Reason ?? "Denied by system-level policy",
                levelResults.AsReadOnly());
        }
        if (systemResult.Decision == LevelDecision.Allow)
        {
            allowLevel = HierarchyLevel.System;
            allowRuleId = systemResult.RuleId;
            allowReason = systemResult.Reason;
        }

        // Step 2: Tenant-level check (tenant active, tenant policies)
        var tenantResult = EvaluateLevel(HierarchyLevel.Tenant, identity.TenantId, identity.EffectivePrincipalId, resource, action);
        levelResults.Add(tenantResult);
        if (tenantResult.Decision == LevelDecision.Deny)
        {
            return AccessVerdict.Denied(identity, resource, action, HierarchyLevel.Tenant,
                tenantResult.RuleId ?? "tenant-deny", tenantResult.Reason ?? "Denied by tenant-level policy",
                levelResults.AsReadOnly());
        }
        if (tenantResult.Decision == LevelDecision.Allow && allowLevel is null)
        {
            allowLevel = HierarchyLevel.Tenant;
            allowRuleId = tenantResult.RuleId;
            allowReason = tenantResult.Reason;
        }

        // Step 3: Instance-level check (instance policies, locked stages)
        var instanceResult = EvaluateLevel(HierarchyLevel.Instance, identity.InstanceId, identity.EffectivePrincipalId, resource, action);
        levelResults.Add(instanceResult);
        if (instanceResult.Decision == LevelDecision.Deny)
        {
            return AccessVerdict.Denied(identity, resource, action, HierarchyLevel.Instance,
                instanceResult.RuleId ?? "instance-deny", instanceResult.Reason ?? "Denied by instance-level policy",
                levelResults.AsReadOnly());
        }
        if (instanceResult.Decision == LevelDecision.Allow && allowLevel is null)
        {
            allowLevel = HierarchyLevel.Instance;
            allowRuleId = instanceResult.RuleId;
            allowReason = instanceResult.Reason;
        }

        // Step 4: UserGroup-level check (for each group in identity.GroupIds)
        var groupResult = EvaluateGroupLevel(identity.GroupIds, identity.EffectivePrincipalId, resource, action);
        levelResults.Add(groupResult);
        if (groupResult.Decision == LevelDecision.Deny)
        {
            return AccessVerdict.Denied(identity, resource, action, HierarchyLevel.UserGroup,
                groupResult.RuleId ?? "group-deny", groupResult.Reason ?? "Denied by user-group-level policy",
                levelResults.AsReadOnly());
        }
        if (groupResult.Decision == LevelDecision.Allow && allowLevel is null)
        {
            allowLevel = HierarchyLevel.UserGroup;
            allowRuleId = groupResult.RuleId;
            allowReason = groupResult.Reason;
        }

        // Step 5: User-level check (identity.EffectivePrincipalId)
        var userResult = EvaluateLevel(HierarchyLevel.User, identity.EffectivePrincipalId, identity.EffectivePrincipalId, resource, action);
        levelResults.Add(userResult);
        if (userResult.Decision == LevelDecision.Deny)
        {
            return AccessVerdict.Denied(identity, resource, action, HierarchyLevel.User,
                userResult.RuleId ?? "user-deny", userResult.Reason ?? "Denied by user-level policy",
                levelResults.AsReadOnly());
        }
        if (userResult.Decision == LevelDecision.Allow && allowLevel is null)
        {
            allowLevel = HierarchyLevel.User;
            allowRuleId = userResult.RuleId;
            allowReason = userResult.Reason;
        }

        // Final decision: ALLOW only if at least one allow found, otherwise DENY
        if (allowLevel.HasValue)
        {
            return AccessVerdict.Granted(identity, resource, action, allowLevel.Value,
                allowRuleId ?? "allow", allowReason ?? $"Allowed at {allowLevel.Value} level",
                levelResults.AsReadOnly());
        }

        // Default deny — no rules matched at any level
        return AccessVerdict.Denied(identity, resource, action, HierarchyLevel.User,
            "default-deny", "No access rules found at any hierarchy level — default DENY",
            levelResults.AsReadOnly());
    }

    /// <summary>
    /// Adds a rule to the hierarchy.
    /// </summary>
    public void AddRule(HierarchyAccessRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        lock (_evaluationLock)
        {
            _rules[rule.Level].Add(rule);
        }
    }

    /// <summary>
    /// Removes all rules matching a predicate.
    /// </summary>
    public int RemoveRules(Func<HierarchyAccessRule, bool> predicate)
    {
        // Lock prevents concurrent AddRule from being lost during the read-snapshot-replace cycle
        lock (_evaluationLock)
        {
            int removed = 0;
            foreach (var level in _rules.Keys)
            {
                var remaining = _rules[level].Where(r => !predicate(r)).ToArray();
                var removedCount = _rules[level].Count - remaining.Length;
                if (removedCount > 0)
                {
                    _rules[level] = new ConcurrentBag<HierarchyAccessRule>(remaining);
                    removed += removedCount;
                }
            }
            return removed;
        }
    }

    /// <summary>
    /// Gets all rules at a specific hierarchy level.
    /// </summary>
    public IReadOnlyList<HierarchyAccessRule> GetRules(HierarchyLevel level)
    {
        return _rules.TryGetValue(level, out var rules) ? rules.ToArray() : Array.Empty<HierarchyAccessRule>();
    }

    /// <summary>
    /// Gets all rules across all levels.
    /// </summary>
    public IReadOnlyList<HierarchyAccessRule> GetAllRules()
    {
        return _rules.Values.SelectMany(r => r).ToArray();
    }

    /// <summary>
    /// Clears all rules at a specific level.
    /// </summary>
    public void ClearLevel(HierarchyLevel level)
    {
        _rules[level] = new ConcurrentBag<HierarchyAccessRule>();
    }

    /// <summary>
    /// Clears all rules at all levels.
    /// </summary>
    public void ClearAll()
    {
        foreach (var level in _rules.Keys)
        {
            _rules[level] = new ConcurrentBag<HierarchyAccessRule>();
        }
    }

    private HierarchyLevelResult EvaluateLevel(
        HierarchyLevel level,
        string scopeId,
        string principalId,
        string resource,
        string action)
    {
        if (!_rules.TryGetValue(level, out var rules))
        {
            return new HierarchyLevelResult { Level = level, Decision = LevelDecision.NoRule };
        }

        HierarchyAccessRule? matchedDeny = null;
        HierarchyAccessRule? matchedAllow = null;

        foreach (var rule in rules)
        {
            if (!rule.IsActive || rule.IsExpired) continue;
            if (!string.Equals(rule.ScopeId, scopeId, StringComparison.OrdinalIgnoreCase) &&
                rule.ScopeId != "*") continue;
            if (!rule.MatchesResource(resource)) continue;
            if (!rule.MatchesAction(action)) continue;
            if (!rule.MatchesPrincipal(principalId)) continue;

            // Deny takes absolute precedence
            if (rule.Decision == LevelDecision.Deny)
            {
                matchedDeny = rule;
                break; // Short-circuit on first deny
            }

            matchedAllow ??= rule;
        }

        if (matchedDeny is not null)
        {
            return new HierarchyLevelResult
            {
                Level = level,
                Decision = LevelDecision.Deny,
                RuleId = matchedDeny.RuleId,
                Reason = matchedDeny.Description ?? $"Denied by rule {matchedDeny.RuleId} at {level}"
            };
        }

        if (matchedAllow is not null)
        {
            return new HierarchyLevelResult
            {
                Level = level,
                Decision = LevelDecision.Allow,
                RuleId = matchedAllow.RuleId,
                Reason = matchedAllow.Description ?? $"Allowed by rule {matchedAllow.RuleId} at {level}"
            };
        }

        return new HierarchyLevelResult { Level = level, Decision = LevelDecision.NoRule };
    }

    private HierarchyLevelResult EvaluateGroupLevel(
        IReadOnlyList<string> groupIds,
        string principalId,
        string resource,
        string action)
    {
        if (groupIds.Count == 0)
        {
            return new HierarchyLevelResult { Level = HierarchyLevel.UserGroup, Decision = LevelDecision.NoRule };
        }

        // Check all groups — if ANY group has a DENY, it's a DENY
        HierarchyAccessRule? anyAllow = null;

        foreach (var groupId in groupIds)
        {
            var result = EvaluateLevel(HierarchyLevel.UserGroup, groupId, principalId, resource, action);
            if (result.Decision == LevelDecision.Deny)
            {
                return result; // Deny from any group = absolute deny
            }
            if (result.Decision == LevelDecision.Allow && anyAllow is null)
            {
                anyAllow = new HierarchyAccessRule
                {
                    RuleId = result.RuleId ?? "group-allow",
                    Level = HierarchyLevel.UserGroup,
                    ScopeId = groupId,
                    Resource = resource,
                    Action = action,
                    Decision = LevelDecision.Allow,
                    Description = result.Reason
                };
            }
        }

        if (anyAllow is not null)
        {
            return new HierarchyLevelResult
            {
                Level = HierarchyLevel.UserGroup,
                Decision = LevelDecision.Allow,
                RuleId = anyAllow.RuleId,
                Reason = anyAllow.Description
            };
        }

        return new HierarchyLevelResult { Level = HierarchyLevel.UserGroup, Decision = LevelDecision.NoRule };
    }
}

/// <summary>
/// Contract for providing hierarchy access rules from external storage.
/// Plugins can implement this to load rules from database, config files, etc.
/// </summary>
public interface IHierarchyRuleProvider
{
    /// <summary>
    /// Loads rules for a specific hierarchy level and scope.
    /// </summary>
    Task<IReadOnlyList<HierarchyAccessRule>> GetRulesAsync(
        HierarchyLevel level,
        string scopeId,
        CancellationToken ct = default);

    /// <summary>
    /// Loads all rules across all levels.
    /// </summary>
    Task<IReadOnlyList<HierarchyAccessRule>> GetAllRulesAsync(CancellationToken ct = default);
}
