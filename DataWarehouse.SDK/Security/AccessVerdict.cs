// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace DataWarehouse.SDK.Security;

/// <summary>
/// The result of access verification through the multi-level hierarchy.
/// Contains the decision, which level made it, and full audit trace.
/// </summary>
public sealed record AccessVerdict
{
    public required bool Allowed { get; init; }
    public required string Reason { get; init; }
    public required HierarchyLevel DecidedAtLevel { get; init; }
    public required string RuleId { get; init; }
    public required CommandIdentity Identity { get; init; }
    public IReadOnlyList<HierarchyLevelResult> LevelResults { get; init; } = Array.Empty<HierarchyLevelResult>();
    public required DateTimeOffset EvaluatedAt { get; init; }
    public required string Resource { get; init; }
    public required string Action { get; init; }

    public static AccessVerdict Denied(
        CommandIdentity identity,
        string resource,
        string action,
        HierarchyLevel level,
        string ruleId,
        string reason,
        IReadOnlyList<HierarchyLevelResult>? levelResults = null) => new()
    {
        Allowed = false,
        Reason = reason,
        DecidedAtLevel = level,
        RuleId = ruleId,
        Identity = identity,
        Resource = resource,
        Action = action,
        EvaluatedAt = DateTimeOffset.UtcNow,
        LevelResults = levelResults ?? Array.Empty<HierarchyLevelResult>()
    };

    public static AccessVerdict Granted(
        CommandIdentity identity,
        string resource,
        string action,
        HierarchyLevel level,
        string ruleId,
        string reason,
        IReadOnlyList<HierarchyLevelResult>? levelResults = null) => new()
    {
        Allowed = true,
        Reason = reason,
        DecidedAtLevel = level,
        RuleId = ruleId,
        Identity = identity,
        Resource = resource,
        Action = action,
        EvaluatedAt = DateTimeOffset.UtcNow,
        LevelResults = levelResults ?? Array.Empty<HierarchyLevelResult>()
    };
}

/// <summary>
/// Result of access evaluation at a single hierarchy level.
/// </summary>
public sealed record HierarchyLevelResult
{
    public required HierarchyLevel Level { get; init; }
    public required LevelDecision Decision { get; init; }
    public string? RuleId { get; init; }
    public string? Reason { get; init; }
}

/// <summary>
/// The decision at a single hierarchy level.
/// </summary>
public enum LevelDecision
{
    NoRule,
    Allow,
    Deny
}

/// <summary>
/// A rule in the access control hierarchy.
/// Rules are stored per-level and evaluated during access verification.
/// </summary>
public sealed record HierarchyAccessRule
{
    public required string RuleId { get; init; }
    public required HierarchyLevel Level { get; init; }
    public required string ScopeId { get; init; }
    public required string Resource { get; init; }
    public required string Action { get; init; }
    public required LevelDecision Decision { get; init; }
    public string? PrincipalId { get; init; }
    public string? PrincipalPattern { get; init; }
    public string? Description { get; init; }
    public bool IsActive { get; init; } = true;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExpiresAt { get; init; }

    public bool IsExpired => ExpiresAt.HasValue && ExpiresAt.Value < DateTimeOffset.UtcNow;

    public bool MatchesPrincipal(string principalId)
    {
        if (PrincipalId is not null)
            return string.Equals(PrincipalId, principalId, StringComparison.OrdinalIgnoreCase);
        if (PrincipalPattern is not null)
        {
            // P2-584: Only do prefix match when pattern explicitly ends with '*'.
            // Without this check, "admin" (no wildcard) would match "adminevil" via StartsWith.
            if (PrincipalPattern.EndsWith('*'))
                return principalId.StartsWith(PrincipalPattern.TrimEnd('*'), StringComparison.OrdinalIgnoreCase);
            return string.Equals(PrincipalPattern, principalId, StringComparison.OrdinalIgnoreCase);
        }
        return true; // Rule applies to all principals at this level
    }

    public bool MatchesResource(string resource)
    {
        if (Resource == "*") return true;
        if (Resource.EndsWith("*"))
            return resource.StartsWith(Resource.TrimEnd('*'), StringComparison.OrdinalIgnoreCase);
        return string.Equals(Resource, resource, StringComparison.OrdinalIgnoreCase);
    }

    public bool MatchesAction(string action)
    {
        if (Action == "*") return true;
        return string.Equals(Action, action, StringComparison.OrdinalIgnoreCase);
    }
}
