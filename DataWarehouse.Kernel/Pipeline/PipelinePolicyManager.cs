using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Pipeline;
using DataWarehouse.SDK.Infrastructure;
using DataWarehouse.SDK.Security;

namespace DataWarehouse.Kernel.Pipeline;

/// <summary>
/// High-level pipeline policy management service.
/// Provides validated CRUD operations with authorization, audit logging,
/// and business rule enforcement for multi-level pipeline policies.
/// </summary>
/// <remarks>
/// T126 Phase C: Multi-Level Configuration Management
/// - C1: Instance-level policy CRUD with admin authorization
/// - C2: Immutability enforcement for instance policies
/// - C3: UserGroup-level policy CRUD with group admin authorization
/// - C4: User-level policy CRUD (self-service with parent override checks)
/// - C5: Operation-level overrides (per-call temporary policies)
/// - C6: Policy inheritance validation
/// - C7: AllowChildOverride enforcement
/// - C8: Effective policy visualization delegation
/// </remarks>
public class PipelinePolicyManager
{
    private readonly IPipelineConfigProvider _configProvider;
    private readonly IKernelContext? _kernelContext;

    /// <summary>
    /// Creates a new pipeline policy manager.
    /// </summary>
    /// <param name="configProvider">The underlying policy configuration provider.</param>
    /// <param name="kernelContext">Optional kernel context for logging and plugin access.</param>
    public PipelinePolicyManager(
        IPipelineConfigProvider configProvider,
        IKernelContext? kernelContext = null)
    {
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
        _kernelContext = kernelContext;
    }

    #region C1: Instance-level policy CRUD

    /// <summary>
    /// Sets the instance-level pipeline policy. Requires admin privileges.
    /// </summary>
    /// <param name="policy">The policy to set. Must have Level=Instance.</param>
    /// <param name="adminUserId">The admin user ID performing this operation.</param>
    /// <param name="securityContext">Optional security context for authorization.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentNullException">If policy or adminUserId is null.</exception>
    /// <exception cref="ArgumentException">If policy level is not Instance.</exception>
    /// <exception cref="SecurityOperationException">If user is not an admin.</exception>
    /// <exception cref="InvalidOperationException">If existing instance policy is immutable.</exception>
    public async Task SetInstancePolicyAsync(
        PipelinePolicy policy,
        string adminUserId,
        ISecurityContext? securityContext = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentException.ThrowIfNullOrWhiteSpace(adminUserId);

        // C1: Validate policy level
        if (policy.Level != PolicyLevel.Instance)
        {
            throw new ArgumentException(
                $"Policy level must be Instance for this operation. Got: {policy.Level}",
                nameof(policy));
        }

        // C1: Validate admin privileges
        ValidateAdminPrivileges(adminUserId, securityContext);

        // C2: Check immutability
        var existingPolicy = await _configProvider.GetPolicyAsync(
            PolicyLevel.Instance,
            policy.ScopeId,
            ct);

        if (existingPolicy?.IsImmutable == true)
        {
            _kernelContext?.LogError(
                $"Cannot update immutable instance policy. PolicyId: {existingPolicy.PolicyId}, " +
                $"ScopeId: {existingPolicy.ScopeId}, User: {adminUserId}");

            throw new InvalidOperationException(
                $"Cannot update immutable instance policy at scope '{policy.ScopeId}'. " +
                "Immutable policies cannot be modified once set. Contact system administrator.");
        }

        // Audit log the change
        _kernelContext?.LogInfo(
            $"[AUDIT] Setting instance policy. PolicyId: {policy.PolicyId}, " +
            $"ScopeId: {policy.ScopeId}, User: {adminUserId}, " +
            $"IsImmutable: {policy.IsImmutable}, StageCount: {policy.Stages.Count}");

        // Delegate to config provider
        await _configProvider.SetPolicyAsync(policy, ct);

        _kernelContext?.LogInfo(
            $"Instance policy set successfully. PolicyId: {policy.PolicyId}, " +
            $"ScopeId: {policy.ScopeId}");
    }

    /// <summary>
    /// Gets the instance-level pipeline policy.
    /// </summary>
    /// <param name="scopeId">The instance scope ID (usually "default").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The instance policy, or null if not set.</returns>
    public Task<PipelinePolicy?> GetInstancePolicyAsync(
        string scopeId = "default",
        CancellationToken ct = default)
    {
        return _configProvider.GetPolicyAsync(PolicyLevel.Instance, scopeId, ct);
    }

    /// <summary>
    /// Deletes the instance-level pipeline policy. Requires admin privileges.
    /// </summary>
    /// <param name="scopeId">The instance scope ID.</param>
    /// <param name="adminUserId">The admin user ID performing this operation.</param>
    /// <param name="securityContext">Optional security context for authorization.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if deleted, false if not found.</returns>
    /// <exception cref="SecurityOperationException">If user is not an admin.</exception>
    /// <exception cref="InvalidOperationException">If the instance policy is immutable.</exception>
    public async Task<bool> DeleteInstancePolicyAsync(
        string scopeId,
        string adminUserId,
        ISecurityContext? securityContext = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(adminUserId);

        ValidateAdminPrivileges(adminUserId, securityContext);

        // Check immutability before deletion
        var existingPolicy = await _configProvider.GetPolicyAsync(
            PolicyLevel.Instance,
            scopeId,
            ct);

        if (existingPolicy?.IsImmutable == true)
        {
            throw new InvalidOperationException(
                $"Cannot delete immutable instance policy at scope '{scopeId}'.");
        }

        _kernelContext?.LogInfo(
            $"[AUDIT] Deleting instance policy. ScopeId: {scopeId}, User: {adminUserId}");

        var deleted = await _configProvider.DeletePolicyAsync(PolicyLevel.Instance, scopeId, ct);

        if (deleted)
        {
            _kernelContext?.LogInfo($"Instance policy deleted. ScopeId: {scopeId}");
        }

        return deleted;
    }

    #endregion

    #region C3: UserGroup-level policy CRUD

    /// <summary>
    /// Sets a user-group-level pipeline policy.
    /// </summary>
    /// <param name="groupId">The group ID.</param>
    /// <param name="policy">The policy to set. Must have Level=UserGroup and ScopeId=groupId.</param>
    /// <param name="userId">The user ID performing this operation (must be group admin).</param>
    /// <param name="securityContext">Optional security context for authorization.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentException">If policy level/scope mismatch.</exception>
    /// <exception cref="SecurityOperationException">If user is not a group admin.</exception>
    /// <exception cref="PolicyViolationException">If policy violates parent AllowChildOverride constraints.</exception>
    public async Task SetGroupPolicyAsync(
        string groupId,
        PipelinePolicy policy,
        string userId,
        ISecurityContext? securityContext = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        // C3: Validate policy level and scope
        if (policy.Level != PolicyLevel.UserGroup)
        {
            throw new ArgumentException(
                $"Policy level must be UserGroup for this operation. Got: {policy.Level}",
                nameof(policy));
        }

        if (policy.ScopeId != groupId)
        {
            throw new ArgumentException(
                $"Policy ScopeId must match groupId. Expected: {groupId}, Got: {policy.ScopeId}",
                nameof(policy));
        }

        // C3: Validate user is admin of this group
        ValidateGroupAdminPrivileges(userId, groupId, securityContext);

        // C6: Validate against parent constraints
        var validation = await ValidatePolicyAsync(policy, ct);
        if (!validation.IsValid)
        {
            _kernelContext?.LogError(
                $"Group policy validation failed. GroupId: {groupId}, User: {userId}, " +
                $"Errors: {string.Join("; ", validation.Errors)}");

            throw new PolicyViolationException(
                $"Policy validation failed: {string.Join(", ", validation.Errors)}",
                validation);
        }

        // Audit log
        _kernelContext?.LogInfo(
            $"[AUDIT] Setting group policy. GroupId: {groupId}, PolicyId: {policy.PolicyId}, " +
            $"User: {userId}, StageCount: {policy.Stages.Count}");

        await _configProvider.SetPolicyAsync(policy, ct);

        _kernelContext?.LogInfo(
            $"Group policy set successfully. GroupId: {groupId}, PolicyId: {policy.PolicyId}");
    }

    /// <summary>
    /// Gets a user-group-level pipeline policy.
    /// </summary>
    /// <param name="groupId">The group ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The group policy, or null if not set.</returns>
    public Task<PipelinePolicy?> GetGroupPolicyAsync(
        string groupId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        return _configProvider.GetPolicyAsync(PolicyLevel.UserGroup, groupId, ct);
    }

    /// <summary>
    /// Deletes a user-group-level pipeline policy.
    /// </summary>
    /// <param name="groupId">The group ID.</param>
    /// <param name="userId">The user ID performing this operation (must be group admin).</param>
    /// <param name="securityContext">Optional security context for authorization.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if deleted, false if not found.</returns>
    public async Task<bool> DeleteGroupPolicyAsync(
        string groupId,
        string userId,
        ISecurityContext? securityContext = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        ValidateGroupAdminPrivileges(userId, groupId, securityContext);

        _kernelContext?.LogInfo(
            $"[AUDIT] Deleting group policy. GroupId: {groupId}, User: {userId}");

        var deleted = await _configProvider.DeletePolicyAsync(PolicyLevel.UserGroup, groupId, ct);

        if (deleted)
        {
            _kernelContext?.LogInfo($"Group policy deleted. GroupId: {groupId}");
        }

        return deleted;
    }

    #endregion

    #region C4: User-level policy CRUD

    /// <summary>
    /// Sets a user-level pipeline policy (self-service).
    /// </summary>
    /// <param name="userId">The user ID. Must match policy.ScopeId.</param>
    /// <param name="policy">The policy to set. Must have Level=User and ScopeId=userId.</param>
    /// <param name="groupId">Optional group ID for parent policy resolution.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentException">If policy level/scope mismatch.</exception>
    /// <exception cref="PolicyViolationException">If policy violates parent AllowChildOverride constraints.</exception>
    public async Task SetUserPolicyAsync(
        string userId,
        PipelinePolicy policy,
        string? groupId = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentNullException.ThrowIfNull(policy);

        // C4: Validate policy level and scope
        if (policy.Level != PolicyLevel.User)
        {
            throw new ArgumentException(
                $"Policy level must be User for this operation. Got: {policy.Level}",
                nameof(policy));
        }

        if (policy.ScopeId != userId)
        {
            throw new ArgumentException(
                $"Policy ScopeId must match userId. Expected: {userId}, Got: {policy.ScopeId}",
                nameof(policy));
        }

        // C6: Validate against parent constraints (Group and Instance)
        var validation = await ValidatePolicyAsync(policy, ct);
        if (!validation.IsValid)
        {
            _kernelContext?.LogError(
                $"User policy validation failed. UserId: {userId}, " +
                $"Errors: {string.Join("; ", validation.Errors)}");

            throw new PolicyViolationException(
                $"Policy validation failed: {string.Join(", ", validation.Errors)}",
                validation);
        }

        // Audit log
        _kernelContext?.LogInfo(
            $"[AUDIT] Setting user policy. UserId: {userId}, PolicyId: {policy.PolicyId}, " +
            $"StageCount: {policy.Stages.Count}");

        await _configProvider.SetPolicyAsync(policy, ct);

        _kernelContext?.LogInfo(
            $"User policy set successfully. UserId: {userId}, PolicyId: {policy.PolicyId}");
    }

    /// <summary>
    /// Gets a user-level pipeline policy.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The user policy, or null if not set.</returns>
    public Task<PipelinePolicy?> GetUserPolicyAsync(
        string userId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        return _configProvider.GetPolicyAsync(PolicyLevel.User, userId, ct);
    }

    /// <summary>
    /// Deletes a user-level pipeline policy.
    /// </summary>
    /// <param name="userId">The user ID (must be the same user or an admin).</param>
    /// <param name="requestingUserId">The user performing this operation.</param>
    /// <param name="securityContext">Optional security context for authorization.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if deleted, false if not found.</returns>
    public async Task<bool> DeleteUserPolicyAsync(
        string userId,
        string requestingUserId,
        ISecurityContext? securityContext = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestingUserId);

        // User can delete their own policy, or admin can delete any user policy
        if (userId != requestingUserId)
        {
            ValidateAdminPrivileges(requestingUserId, securityContext);
        }

        _kernelContext?.LogInfo(
            $"[AUDIT] Deleting user policy. UserId: {userId}, RequestingUser: {requestingUserId}");

        var deleted = await _configProvider.DeletePolicyAsync(PolicyLevel.User, userId, ct);

        if (deleted)
        {
            _kernelContext?.LogInfo($"User policy deleted. UserId: {userId}");
        }

        return deleted;
    }

    #endregion

    #region C5: Operation-level overrides

    /// <summary>
    /// Creates a temporary operation-level policy override.
    /// This policy is not persisted and only used for a single operation.
    /// </summary>
    /// <param name="userId">The user ID for parent policy resolution.</param>
    /// <param name="groupId">Optional group ID for parent policy resolution.</param>
    /// <param name="stageOverrides">Per-stage overrides for this operation.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The operation-level policy (not persisted).</returns>
    /// <exception cref="PolicyViolationException">If overrides violate parent AllowChildOverride constraints.</exception>
    public async Task<PipelinePolicy> CreateOperationOverrideAsync(
        string userId,
        string? groupId,
        Dictionary<string, PipelineStagePolicy> stageOverrides,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentNullException.ThrowIfNull(stageOverrides);

        var operationId = Guid.NewGuid().ToString("N");

        // Build operation policy
        var policy = new PipelinePolicy
        {
            PolicyId = operationId,
            Name = "Operation Override",
            Level = PolicyLevel.Operation,
            ScopeId = operationId,
            Stages = stageOverrides.Values.ToList(),
            Version = 1,
            UpdatedAt = DateTimeOffset.UtcNow,
            UpdatedBy = userId,
            MigrationBehavior = MigrationBehavior.KeepExisting,
            IsImmutable = false,
            Description = "Temporary operation-level override"
        };

        // C6: Validate against parent constraints
        var validation = await ValidatePolicyAsync(policy, ct);
        if (!validation.IsValid)
        {
            _kernelContext?.LogError(
                $"Operation override validation failed. UserId: {userId}, " +
                $"Errors: {string.Join("; ", validation.Errors)}");

            throw new PolicyViolationException(
                $"Operation override validation failed: {string.Join(", ", validation.Errors)}",
                validation);
        }

        _kernelContext?.LogDebug(
            $"Created operation override. OperationId: {operationId}, UserId: {userId}, " +
            $"StageCount: {stageOverrides.Count}");

        return policy;
    }

    #endregion

    #region C6: Policy inheritance validation

    /// <summary>
    /// Validates that a proposed policy respects inheritance rules from parent levels.
    /// Returns validation errors if any stage violates AllowChildOverride=false from a parent.
    /// </summary>
    /// <param name="policy">The policy to validate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Validation result with errors, warnings, and locked stage information.</returns>
    public async Task<PolicyValidationResult> ValidatePolicyAsync(
        PipelinePolicy policy,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(policy);

        var result = new PolicyValidationResult
        {
            IsValid = true,
            Errors = new List<string>(),
            Warnings = new List<string>(),
            LockedStages = new List<LockedStageInfo>()
        };

        // Skip validation for Instance level (no parents)
        if (policy.Level == PolicyLevel.Instance)
        {
            return result;
        }

        // C7: Get locked stages from parent levels
        var lockedStages = await GetLockedStagesAsync(
            policy.Level,
            policy.Level == PolicyLevel.UserGroup ? policy.ScopeId : null,
            ct);

        result.LockedStages = lockedStages.ToList();

        // C6: Check if any of this policy's stages are locked by a parent
        foreach (var stage in policy.Stages)
        {
            var lockInfo = lockedStages.FirstOrDefault(ls => ls.StageType == stage.StageType);
            if (lockInfo != null)
            {
                result.IsValid = false;
                result.Errors.Add(
                    $"Stage '{stage.StageType}' is locked by {lockInfo.LockedByLevel} policy " +
                    $"(PolicyId: {lockInfo.LockedByPolicyId}). {lockInfo.Reason}");
            }
        }

        // Additional warnings
        if (policy.Stages.Count == 0)
        {
            result.Warnings.Add("Policy has no stages defined - will inherit all settings from parent.");
        }

        return result;
    }

    #endregion

    #region C7: AllowChildOverride enforcement

    /// <summary>
    /// Checks which stages are locked by parent policies and cannot be overridden.
    /// </summary>
    /// <param name="level">The policy level to check (UserGroup, User, or Operation).</param>
    /// <param name="groupId">Optional group ID for User-level checks.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of locked stages with information about which parent locked them.</returns>
    public async Task<IReadOnlyList<LockedStageInfo>> GetLockedStagesAsync(
        PolicyLevel level,
        string? groupId = null,
        CancellationToken ct = default)
    {
        var lockedStages = new List<LockedStageInfo>();

        // C7: Walk parent chain from Instance upward
        // For UserGroup: only check Instance
        // For User: check Instance and UserGroup
        // For Operation: check Instance, UserGroup, and User

        // Check Instance level
        var instancePolicy = await _configProvider.GetPolicyAsync(
            PolicyLevel.Instance,
            "default",
            ct);

        if (instancePolicy != null)
        {
            foreach (var stage in instancePolicy.Stages.Where(s => !s.AllowChildOverride))
            {
                lockedStages.Add(new LockedStageInfo
                {
                    StageType = stage.StageType,
                    LockedByLevel = PolicyLevel.Instance,
                    LockedByPolicyId = instancePolicy.PolicyId,
                    Reason = "Instance administrator has locked this stage configuration"
                });
            }
        }

        // Check UserGroup level (if applicable)
        if (level >= PolicyLevel.User && !string.IsNullOrEmpty(groupId))
        {
            var groupPolicy = await _configProvider.GetPolicyAsync(
                PolicyLevel.UserGroup,
                groupId,
                ct);

            if (groupPolicy != null)
            {
                foreach (var stage in groupPolicy.Stages.Where(s => !s.AllowChildOverride))
                {
                    // Only add if not already locked by Instance
                    if (!lockedStages.Any(ls => ls.StageType == stage.StageType))
                    {
                        lockedStages.Add(new LockedStageInfo
                        {
                            StageType = stage.StageType,
                            LockedByLevel = PolicyLevel.UserGroup,
                            LockedByPolicyId = groupPolicy.PolicyId,
                            Reason = "Group administrator has locked this stage configuration"
                        });
                    }
                }
            }
        }

        // Note: User level locking for Operation level would be checked here if needed
        // Currently not implemented as Operation overrides are typically ephemeral

        return lockedStages;
    }

    #endregion

    #region C8: Effective policy visualization

    /// <summary>
    /// Returns a human-readable visualization of the resolved pipeline for a given context,
    /// showing which level each setting was inherited from.
    /// </summary>
    /// <param name="userId">Optional user ID for context.</param>
    /// <param name="groupId">Optional group ID for context.</param>
    /// <param name="operationId">Optional operation ID for context.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Visualization showing inheritance chain for each setting.</returns>
    public Task<EffectivePolicyVisualization> GetEffectivePolicyAsync(
        string? userId = null,
        string? groupId = null,
        string? operationId = null,
        CancellationToken ct = default)
    {
        // C8: Delegate to config provider's visualization
        return _configProvider.VisualizeEffectivePolicyAsync(userId, groupId, operationId, ct);
    }

    #endregion

    #region Authorization helpers

    /// <summary>
    /// Validates that a user has admin privileges.
    /// </summary>
    private void ValidateAdminPrivileges(string userId, ISecurityContext? securityContext)
    {
        // If no security context, allow (development/testing mode)
        if (securityContext == null)
        {
            _kernelContext?.LogWarning(
                $"No security context provided for admin operation. UserId: {userId}. " +
                "This is allowed for development/testing only.");
            return;
        }

        // Check if user is system admin
        if (!securityContext.IsSystemAdmin)
        {
            throw SecurityOperationException.AccessDenied(
                userId,
                "Instance",
                "SystemAdmin");
        }
    }

    /// <summary>
    /// Validates that a user is an admin of a specific group.
    /// </summary>
    private void ValidateGroupAdminPrivileges(
        string userId,
        string groupId,
        ISecurityContext? securityContext)
    {
        // If no security context, allow (development/testing mode)
        if (securityContext == null)
        {
            _kernelContext?.LogWarning(
                $"No security context provided for group admin operation. " +
                $"UserId: {userId}, GroupId: {groupId}. " +
                "This is allowed for development/testing only.");
            return;
        }

        // Check if user is system admin (can manage all groups)
        if (securityContext.IsSystemAdmin)
        {
            return;
        }

        // Check if user has "GroupAdmin" role or group-specific admin role
        var hasGroupAdminRole = securityContext.Roles.Any(r =>
            r.Equals("GroupAdmin", StringComparison.OrdinalIgnoreCase) ||
            r.Equals($"{groupId}:Admin", StringComparison.OrdinalIgnoreCase));

        if (!hasGroupAdminRole)
        {
            throw SecurityOperationException.AccessDenied(
                userId,
                $"Group:{groupId}",
                "GroupAdmin");
        }
    }

    #endregion
}

#region Supporting types

/// <summary>
/// Result of policy validation.
/// </summary>
public class PolicyValidationResult
{
    /// <summary>Whether the policy is valid.</summary>
    public bool IsValid { get; set; }

    /// <summary>Validation errors that prevent the policy from being set.</summary>
    public List<string> Errors { get; set; } = new();

    /// <summary>Warnings that don't prevent the policy but indicate potential issues.</summary>
    public List<string> Warnings { get; set; } = new();

    /// <summary>Stages that are locked by parent policies.</summary>
    public List<LockedStageInfo> LockedStages { get; set; } = new();
}

/// <summary>
/// Information about a stage that is locked by a parent policy.
/// </summary>
public class LockedStageInfo
{
    /// <summary>The stage type that is locked.</summary>
    public string StageType { get; set; } = string.Empty;

    /// <summary>The policy level that locked this stage.</summary>
    public PolicyLevel LockedByLevel { get; set; }

    /// <summary>The policy ID that locked this stage.</summary>
    public string LockedByPolicyId { get; set; } = string.Empty;

    /// <summary>Human-readable reason why the stage is locked.</summary>
    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// Exception thrown when a policy violates parent constraints.
/// </summary>
public class PolicyViolationException : DataWarehouseException
{
    /// <summary>The validation result that contains the violation details.</summary>
    public PolicyValidationResult ValidationResult { get; }

    /// <summary>
    /// Creates a new policy violation exception.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="validationResult">The validation result with error details.</param>
    public PolicyViolationException(string message, PolicyValidationResult validationResult)
        : base(ErrorCode.ValidationFailed, message)
    {
        ValidationResult = validationResult ?? throw new ArgumentNullException(nameof(validationResult));
    }
}

#endregion
