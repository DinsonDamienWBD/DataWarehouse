// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace DataWarehouse.SDK.Security;

/// <summary>
/// The type of actor physically executing an action.
/// Used for audit trail ONLY — never for access control decisions.
/// </summary>
public enum ActorType
{
    Human,
    AiAgent,
    SystemService,
    Scheduler,
    Webhook,
    ApiClient
}

/// <summary>
/// The type of principal on whose behalf the action is performed.
/// Access control evaluates permissions against this principal.
/// </summary>
public enum PrincipalType
{
    User,
    UserGroup,
    Tenant,
    System
}

/// <summary>
/// Hierarchy levels for multi-level access verification.
/// Deny at ANY level = absolute DENY, no exceptions.
/// </summary>
public enum HierarchyLevel
{
    System = 0,
    Tenant = 1,
    Instance = 2,
    UserGroup = 3,
    User = 4
}

/// <summary>
/// Immutable, read-only identity that travels with every command, message, and action.
/// Constructed once at the entry point (CLI/GUI/API/Scheduler), never modified.
///
/// KEY RULE: Access control ALWAYS evaluates OnBehalfOfPrincipalId, NEVER ActorId.
/// The AI's own access level is IRRELEVANT — only the originating principal matters.
/// </summary>
public sealed record CommandIdentity
{
    // WHO is physically executing (for audit trail ONLY — never used for access control)
    public required string ActorId { get; init; }
    public required ActorType ActorType { get; init; }

    // ON WHOSE BEHALF (this is what access control evaluates)
    public required string OnBehalfOfPrincipalId { get; init; }
    public required PrincipalType PrincipalType { get; init; }

    // Context for hierarchy resolution
    public required string TenantId { get; init; }
    public required string InstanceId { get; init; }
    public IReadOnlyList<string> GroupIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Roles { get; init; } = Array.Empty<string>();

    // Authentication provenance
    public required string AuthenticationMethod { get; init; }
    public required DateTimeOffset AuthenticatedAt { get; init; }
    public string? SessionId { get; init; }

    // Delegation chain (audit trail): who delegated to whom
    // e.g., ["user:alice", "ai:gemini-user", "ai:claude-system"]
    public IReadOnlyList<string> DelegationChain { get; init; } = Array.Empty<string>();

    // Enforcement: this is the principal whose permissions are checked
    // ALWAYS equals OnBehalfOfPrincipalId — never ActorId
    public string EffectivePrincipalId => OnBehalfOfPrincipalId;

    /// <summary>
    /// Creates a new CommandIdentity with an additional delegate appended to the chain.
    /// Used when an AI agent delegates to another AI agent.
    /// The OnBehalfOfPrincipalId remains the ORIGINAL user — it never changes.
    /// </summary>
    public CommandIdentity WithDelegation(string delegateActorId)
    {
        var newChain = new List<string>(DelegationChain) { delegateActorId };
        return this with { DelegationChain = newChain.AsReadOnly() };
    }

    /// <summary>
    /// Creates a system-level identity for kernel/scheduler operations.
    /// </summary>
    public static CommandIdentity System(string serviceId, string instanceId = "default") => new()
    {
        ActorId = $"svc:{serviceId}",
        ActorType = ActorType.SystemService,
        OnBehalfOfPrincipalId = "system:kernel",
        PrincipalType = PrincipalType.System,
        TenantId = "system",
        InstanceId = instanceId,
        AuthenticationMethod = "Internal",
        AuthenticatedAt = DateTimeOffset.UtcNow
    };

    /// <summary>
    /// Creates a scheduler-level identity for background jobs.
    /// </summary>
    public static CommandIdentity Scheduler(string jobId, string instanceId = "default") => new()
    {
        ActorId = $"svc:scheduler:{jobId}",
        ActorType = ActorType.Scheduler,
        OnBehalfOfPrincipalId = "system:scheduler",
        PrincipalType = PrincipalType.System,
        TenantId = "system",
        InstanceId = instanceId,
        AuthenticationMethod = "Internal",
        AuthenticatedAt = DateTimeOffset.UtcNow
    };

    /// <summary>
    /// Creates identity for a human user (CLI/GUI/API).
    /// </summary>
    public static CommandIdentity ForUser(
        string userId,
        string tenantId,
        string instanceId,
        IReadOnlyList<string> roles,
        IReadOnlyList<string>? groupIds = null,
        string authMethod = "Session",
        string? sessionId = null) => new()
    {
        ActorId = $"user:{userId}",
        ActorType = ActorType.Human,
        OnBehalfOfPrincipalId = $"user:{userId}",
        PrincipalType = PrincipalType.User,
        TenantId = tenantId,
        InstanceId = instanceId,
        GroupIds = groupIds ?? Array.Empty<string>(),
        Roles = roles,
        AuthenticationMethod = authMethod,
        AuthenticatedAt = DateTimeOffset.UtcNow,
        SessionId = sessionId
    };

    /// <summary>
    /// Creates identity for an AI agent acting on behalf of a user.
    /// The access control check will use the USER's permissions, not the AI's.
    /// </summary>
    public static CommandIdentity ForAiAgent(
        string agentId,
        CommandIdentity onBehalfOf) => new()
    {
        ActorId = $"ai:{agentId}",
        ActorType = ActorType.AiAgent,
        OnBehalfOfPrincipalId = onBehalfOf.OnBehalfOfPrincipalId,
        PrincipalType = onBehalfOf.PrincipalType,
        TenantId = onBehalfOf.TenantId,
        InstanceId = onBehalfOf.InstanceId,
        GroupIds = onBehalfOf.GroupIds,
        Roles = onBehalfOf.Roles,
        AuthenticationMethod = onBehalfOf.AuthenticationMethod,
        AuthenticatedAt = onBehalfOf.AuthenticatedAt,
        SessionId = onBehalfOf.SessionId,
        DelegationChain = new List<string>(onBehalfOf.DelegationChain) { $"ai:{agentId}" }.AsReadOnly()
    };
}
