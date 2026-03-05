using System.Diagnostics;
using System.Text.RegularExpressions;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.Lifecycle;

/// <summary>
/// Policy validation result.
/// </summary>
public sealed class PolicyValidationResult
{
    /// <summary>
    /// Whether the policy is valid.
    /// </summary>
    public required bool IsValid { get; init; }

    /// <summary>
    /// Validation errors if any.
    /// </summary>
    public List<string> Errors { get; init; } = new();

    /// <summary>
    /// Validation warnings if any.
    /// </summary>
    public List<string> Warnings { get; init; } = new();

    /// <summary>
    /// Conflicting policy IDs if any.
    /// </summary>
    public List<string> ConflictingPolicies { get; init; } = new();

    /// <summary>
    /// Creates a valid result.
    /// </summary>
    public static PolicyValidationResult Valid() => new() { IsValid = true };

    /// <summary>
    /// Creates an invalid result with errors.
    /// </summary>
    public static PolicyValidationResult Invalid(params string[] errors) =>
        new() { IsValid = false, Errors = errors.ToList() };
}

/// <summary>
/// Policy execution result.
/// </summary>
public sealed class PolicyExecutionResult
{
    /// <summary>
    /// Policy ID that was executed.
    /// </summary>
    public required string PolicyId { get; init; }

    /// <summary>
    /// Whether execution succeeded.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// Number of objects affected.
    /// </summary>
    public int ObjectsAffected { get; init; }

    /// <summary>
    /// Execution duration.
    /// </summary>
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// When the policy was executed.
    /// </summary>
    public DateTime ExecutedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Error message if failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Next scheduled execution if applicable.
    /// </summary>
    public DateTime? NextScheduledExecution { get; init; }
}

/// <summary>
/// Schedule entry for policy execution.
/// </summary>
public sealed class PolicyScheduleEntry
{
    /// <summary>
    /// Policy ID.
    /// </summary>
    public required string PolicyId { get; init; }

    /// <summary>
    /// Cron expression.
    /// </summary>
    public required string CronExpression { get; init; }

    /// <summary>
    /// Next execution time.
    /// </summary>
    public DateTime NextExecution { get; set; }

    /// <summary>
    /// Last execution time.
    /// </summary>
    public DateTime? LastExecution { get; set; }

    /// <summary>
    /// Whether the schedule is active.
    /// </summary>
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// Lifecycle policy engine strategy that provides comprehensive policy management and execution.
/// Supports priority-based rule ordering, scheduled evaluation, and conflict detection.
/// </summary>
public sealed class LifecyclePolicyEngineStrategy : LifecycleStrategyBase
{
    private readonly BoundedDictionary<string, PolicyScheduleEntry> _schedules = new BoundedDictionary<string, PolicyScheduleEntry>(1000);
    private readonly BoundedDictionary<string, PolicyExecutionResult> _lastResults = new BoundedDictionary<string, PolicyExecutionResult>(1000);
    private readonly BoundedDictionary<string, List<string>> _conflictGraph = new BoundedDictionary<string, List<string>>(1000);
    private readonly SemaphoreSlim _executionLock = new(1, 1);
    private CancellationTokenSource? _schedulerCts;
    private Task? _schedulerTask;

    /// <inheritdoc/>
    public override string StrategyId => "lifecycle-policy-engine";

    /// <inheritdoc/>
    public override string DisplayName => "Lifecycle Policy Engine";

    /// <inheritdoc/>
    public override DataManagementCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = true,
        SupportsTransactions = false,
        SupportsTTL = true,
        MaxThroughput = 5000,
        TypicalLatencyMs = 2.0
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Policy execution engine that defines and executes lifecycle rules with conditions and actions. " +
        "Supports priority-based ordering, scheduled evaluation using cron expressions, and automatic conflict detection.";

    /// <inheritdoc/>
    public override string[] Tags => new[]
    {
        "lifecycle", "policy", "engine", "rules", "scheduling", "cron", "automation", "governance"
    };

    /// <inheritdoc/>
    protected override Task InitializeCoreAsync(CancellationToken ct)
    {
        _schedulerCts = new CancellationTokenSource();
        _schedulerTask = RunSchedulerAsync(_schedulerCts.Token);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override async Task DisposeCoreAsync()
    {
        if (_schedulerCts != null)
        {
            await _schedulerCts.CancelAsync();
            _schedulerCts.Dispose();
        }

        if (_schedulerTask != null)
        {
            try
            {
                await _schedulerTask;
            }
            catch (OperationCanceledException ex)
            {

                // Expected
                System.Diagnostics.Debug.WriteLine($"[Warning] caught {ex.GetType().Name}: {ex.Message}");
            }
        }

        _executionLock.Dispose();
    }

    /// <inheritdoc/>
    protected override async Task<LifecycleDecision> EvaluateCoreAsync(LifecycleDataObject data, CancellationToken ct)
    {
        // Get policies ordered by priority
        var policies = GetPolicies().ToList();

        foreach (var policy in policies)
        {
            ct.ThrowIfCancellationRequested();

            // Check if object is in scope
            if (!IsInScope(data, policy.Scope))
            {
                continue;
            }

            // Evaluate conditions
            if (EvaluatePolicyConditions(data, policy.Conditions))
            {
                return new LifecycleDecision
                {
                    Action = policy.Action,
                    Reason = $"Matched policy: {policy.Name}",
                    PolicyName = policy.PolicyId,
                    Priority = policy.Priority / 1000.0, // Normalize to 0-1
                    Parameters = policy.ActionParameters,
                    Confidence = 1.0
                };
            }
        }

        return LifecycleDecision.NoAction("No matching policy found", DateTime.UtcNow.AddHours(1));
    }

    /// <inheritdoc/>
    protected override async Task<int> ExecutePolicyCoreAsync(LifecyclePolicy policy, CancellationToken ct)
    {
        await _executionLock.WaitAsync(ct);
        try
        {
            var sw = Stopwatch.StartNew();
            var affected = 0;

            // Check for conflicts
            if (_conflictGraph.TryGetValue(policy.PolicyId, out var conflicts))
            {
                foreach (var conflictId in conflicts)
                {
                    if (_lastResults.TryGetValue(conflictId, out var lastResult) &&
                        lastResult.Success &&
                        DateTime.UtcNow - lastResult.ExecutedAt < TimeSpan.FromMinutes(5))
                    {
                        // Skip due to recent conflicting policy execution
                        _lastResults[policy.PolicyId] = new PolicyExecutionResult
                        {
                            PolicyId = policy.PolicyId,
                            Success = false,
                            ErrorMessage = $"Skipped due to conflict with recently executed policy: {conflictId}",
                            Duration = sw.Elapsed
                        };
                        return 0;
                    }
                }
            }

            // Get objects in scope
            var objects = GetObjectsInScope(policy.Scope).ToList();

            foreach (var obj in objects)
            {
                ct.ThrowIfCancellationRequested();

                // Skip objects on hold for destructive actions
                if (obj.IsOnHold && IsDestructiveAction(policy.Action))
                {
                    continue;
                }

                if (EvaluatePolicyConditions(obj, policy.Conditions))
                {
                    var decision = new LifecycleDecision
                    {
                        Action = policy.Action,
                        Reason = $"Policy: {policy.Name}",
                        PolicyName = policy.PolicyId,
                        Parameters = policy.ActionParameters
                    };

                    await ExecuteActionAsync(obj, decision, ct);
                    affected++;
                }
            }

            sw.Stop();

            _lastResults[policy.PolicyId] = new PolicyExecutionResult
            {
                PolicyId = policy.PolicyId,
                Success = true,
                ObjectsAffected = affected,
                Duration = sw.Elapsed,
                NextScheduledExecution = GetNextScheduledExecution(policy.PolicyId)
            };

            return affected;
        }
        finally
        {
            _executionLock.Release();
        }
    }

    /// <summary>
    /// Validates a policy before registration.
    /// </summary>
    /// <param name="policy">Policy to validate.</param>
    /// <returns>Validation result.</returns>
    public PolicyValidationResult ValidatePolicy(LifecyclePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        var errors = new List<string>();
        var warnings = new List<string>();
        var conflicts = new List<string>();

        // Validate required fields
        if (string.IsNullOrWhiteSpace(policy.PolicyId))
        {
            errors.Add("Policy ID is required.");
        }

        if (string.IsNullOrWhiteSpace(policy.Name))
        {
            errors.Add("Policy name is required.");
        }

        // Validate conditions
        if (policy.Conditions.Count == 0)
        {
            warnings.Add("Policy has no conditions - will match all objects in scope.");
        }

        foreach (var condition in policy.Conditions)
        {
            if (string.IsNullOrWhiteSpace(condition.Field))
            {
                errors.Add($"Condition field is required.");
            }

            if (condition.Value == null)
            {
                errors.Add($"Condition value is required for field '{condition.Field}'.");
            }
        }

        // Validate cron expression
        if (!string.IsNullOrEmpty(policy.CronSchedule))
        {
            if (!IsValidCronExpression(policy.CronSchedule))
            {
                errors.Add($"Invalid cron expression: {policy.CronSchedule}");
            }
        }

        // Check for conflicts with existing policies
        foreach (var existingPolicy in Policies.Values)
        {
            if (existingPolicy.PolicyId == policy.PolicyId)
            {
                continue;
            }

            if (HasPotentialConflict(policy, existingPolicy))
            {
                conflicts.Add(existingPolicy.PolicyId);
                warnings.Add($"Potential conflict with policy: {existingPolicy.Name}");
            }
        }

        // Check explicitly declared conflicts
        if (policy.ConflictsWith != null)
        {
            foreach (var conflictId in policy.ConflictsWith)
            {
                if (Policies.ContainsKey(conflictId) && !conflicts.Contains(conflictId))
                {
                    conflicts.Add(conflictId);
                }
            }
        }

        return new PolicyValidationResult
        {
            IsValid = errors.Count == 0,
            Errors = errors,
            Warnings = warnings,
            ConflictingPolicies = conflicts
        };
    }

    /// <summary>
    /// Registers a policy with validation and conflict detection.
    /// </summary>
    /// <param name="policy">Policy to register.</param>
    /// <param name="force">Force registration even with warnings.</param>
    /// <returns>Validation result.</returns>
    public PolicyValidationResult RegisterPolicyWithValidation(LifecyclePolicy policy, bool force = false)
    {
        var validation = ValidatePolicy(policy);

        if (!validation.IsValid)
        {
            return validation;
        }

        if (validation.Warnings.Count > 0 && !force)
        {
            return validation;
        }

        // Register the policy
        RegisterPolicy(policy);

        // Update conflict graph
        if (validation.ConflictingPolicies.Count > 0)
        {
            _conflictGraph[policy.PolicyId] = validation.ConflictingPolicies;

            foreach (var conflictId in validation.ConflictingPolicies)
            {
                _conflictGraph.AddOrUpdate(
                    conflictId,
                    new List<string> { policy.PolicyId },
                    (_, existing) =>
                    {
                        if (!existing.Contains(policy.PolicyId))
                        {
                            existing.Add(policy.PolicyId);
                        }
                        return existing;
                    });
            }
        }

        // Set up schedule if applicable
        if (!string.IsNullOrEmpty(policy.CronSchedule))
        {
            _schedules[policy.PolicyId] = new PolicyScheduleEntry
            {
                PolicyId = policy.PolicyId,
                CronExpression = policy.CronSchedule,
                NextExecution = CalculateNextExecution(policy.CronSchedule)
            };
        }

        return validation;
    }

    /// <summary>
    /// Gets the last execution result for a policy.
    /// </summary>
    /// <param name="policyId">Policy ID.</param>
    /// <returns>Last execution result or null.</returns>
    public PolicyExecutionResult? GetLastExecutionResult(string policyId)
    {
        return _lastResults.TryGetValue(policyId, out var result) ? result : null;
    }

    /// <summary>
    /// Gets all scheduled policies.
    /// </summary>
    /// <returns>Collection of schedule entries.</returns>
    public IEnumerable<PolicyScheduleEntry> GetScheduledPolicies()
    {
        return _schedules.Values.Where(s => s.IsActive).OrderBy(s => s.NextExecution);
    }

    /// <summary>
    /// Executes all policies in priority order.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Total objects affected.</returns>
    public async Task<int> ExecuteAllPoliciesAsync(CancellationToken ct = default)
    {
        var totalAffected = 0;
        var policies = GetPolicies().ToList();

        foreach (var policy in policies)
        {
            ct.ThrowIfCancellationRequested();
            totalAffected += await ExecutePolicyAsync(policy, ct);
        }

        return totalAffected;
    }

    /// <summary>
    /// Gets conflicting policies for a given policy.
    /// </summary>
    /// <param name="policyId">Policy ID.</param>
    /// <returns>List of conflicting policy IDs.</returns>
    public IReadOnlyList<string> GetConflictingPolicies(string policyId)
    {
        return _conflictGraph.TryGetValue(policyId, out var conflicts)
            ? conflicts.AsReadOnly()
            : Array.Empty<string>();
    }

    private async Task RunSchedulerAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);

                var now = DateTime.UtcNow;
                var dueSchedules = _schedules.Values
                    .Where(s => s.IsActive && s.NextExecution <= now)
                    .ToList();

                foreach (var schedule in dueSchedules)
                {
                    if (ct.IsCancellationRequested) break;

                    if (Policies.TryGetValue(schedule.PolicyId, out var policy))
                    {
                        try
                        {
                            await ExecutePolicyAsync(policy, ct);
                        }
                        catch
                        {

                            // Log and continue
                            System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
                        }

                        schedule.LastExecution = now;
                        schedule.NextExecution = CalculateNextExecution(schedule.CronExpression);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ExecuteActionAsync(LifecycleDataObject obj, LifecycleDecision decision, CancellationToken ct)
    {
        switch (decision.Action)
        {
            case LifecycleAction.Archive:
                await ArchiveObjectAsync(obj, decision.TargetLocation, ct);
                break;

            case LifecycleAction.Delete:
            case LifecycleAction.Purge:
                await DeleteObjectAsync(obj, ct);
                break;

            case LifecycleAction.Tier:
                await TierObjectAsync(obj, decision.TargetLocation, ct);
                break;

            case LifecycleAction.Notify:
                await NotifyAsync(obj, decision.Parameters, ct);
                break;

            case LifecycleAction.Migrate:
                await MigrateObjectAsync(obj, decision.TargetLocation, ct);
                break;

            default:
                await ApplyDecisionAsync(obj, decision, ct);
                break;
        }
    }

    private Task ArchiveObjectAsync(LifecycleDataObject obj, string? target, CancellationToken ct)
    {
        // Archive implementation - update object state
        var updated = new LifecycleDataObject
        {
            ObjectId = obj.ObjectId,
            Path = obj.Path,
            ContentType = obj.ContentType,
            Size = obj.Size,
            CreatedAt = obj.CreatedAt,
            LastModifiedAt = obj.LastModifiedAt,
            LastAccessedAt = obj.LastAccessedAt,
            TenantId = obj.TenantId,
            Tags = obj.Tags,
            Classification = obj.Classification,
            StorageTier = target ?? "archive",
            IsArchived = true,
            Metadata = obj.Metadata
        };
        TrackedObjects[obj.ObjectId] = updated;
        return Task.CompletedTask;
    }

    private Task DeleteObjectAsync(LifecycleDataObject obj, CancellationToken ct)
    {
        TrackedObjects.TryRemove(obj.ObjectId, out _);
        PendingActions.TryRemove(obj.ObjectId, out _);
        return Task.CompletedTask;
    }

    private Task TierObjectAsync(LifecycleDataObject obj, string? targetTier, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(targetTier)) return Task.CompletedTask;

        var updated = new LifecycleDataObject
        {
            ObjectId = obj.ObjectId,
            Path = obj.Path,
            ContentType = obj.ContentType,
            Size = obj.Size,
            CreatedAt = obj.CreatedAt,
            LastModifiedAt = DateTime.UtcNow,
            LastAccessedAt = obj.LastAccessedAt,
            TenantId = obj.TenantId,
            Tags = obj.Tags,
            Classification = obj.Classification,
            StorageTier = targetTier,
            IsArchived = obj.IsArchived,
            Metadata = obj.Metadata
        };
        TrackedObjects[obj.ObjectId] = updated;
        return Task.CompletedTask;
    }

    private Task NotifyAsync(LifecycleDataObject obj, Dictionary<string, object>? parameters, CancellationToken ct)
    {
        // Notification would be sent to message bus (T90) in production
        // For now, just record the notification event
        return Task.CompletedTask;
    }

    private Task MigrateObjectAsync(LifecycleDataObject obj, string? targetLocation, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(targetLocation)) return Task.CompletedTask;

        var updated = new LifecycleDataObject
        {
            ObjectId = obj.ObjectId,
            Path = obj.Path,
            ContentType = obj.ContentType,
            Size = obj.Size,
            CreatedAt = obj.CreatedAt,
            LastModifiedAt = DateTime.UtcNow,
            LastAccessedAt = obj.LastAccessedAt,
            TenantId = obj.TenantId,
            Tags = obj.Tags,
            Classification = obj.Classification,
            StorageLocation = targetLocation,
            StorageTier = obj.StorageTier,
            IsArchived = obj.IsArchived,
            Metadata = obj.Metadata
        };
        TrackedObjects[obj.ObjectId] = updated;
        return Task.CompletedTask;
    }

    private bool IsInScope(LifecycleDataObject obj, PolicyScope? scope)
    {
        if (scope == null) return true;

        if (scope.TenantIds?.Length > 0 && (obj.TenantId == null || !scope.TenantIds.Contains(obj.TenantId)))
        {
            return false;
        }

        if (scope.PathPrefixes?.Length > 0)
        {
            if (obj.Path == null || !scope.PathPrefixes.Any(p => obj.Path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }

        if (scope.ContentTypes?.Length > 0 && (obj.ContentType == null || !scope.ContentTypes.Contains(obj.ContentType)))
        {
            return false;
        }

        if (scope.Classifications?.Length > 0 && !scope.Classifications.Contains(obj.Classification))
        {
            return false;
        }

        if (scope.MinSize.HasValue && obj.Size < scope.MinSize.Value)
        {
            return false;
        }

        if (scope.MaxSize.HasValue && obj.Size > scope.MaxSize.Value)
        {
            return false;
        }

        return true;
    }

    private static bool IsDestructiveAction(LifecycleAction action)
    {
        return action is LifecycleAction.Delete or LifecycleAction.Purge or LifecycleAction.Expire;
    }

    private bool HasPotentialConflict(LifecyclePolicy policy1, LifecyclePolicy policy2)
    {
        // Same action with same priority on overlapping scope
        if (policy1.Action == policy2.Action && Math.Abs(policy1.Priority - policy2.Priority) < 10)
        {
            return ScopesOverlap(policy1.Scope, policy2.Scope);
        }

        // Conflicting actions (e.g., delete vs archive) on overlapping scope
        if (AreConflictingActions(policy1.Action, policy2.Action))
        {
            return ScopesOverlap(policy1.Scope, policy2.Scope);
        }

        return false;
    }

    private static bool ScopesOverlap(PolicyScope? scope1, PolicyScope? scope2)
    {
        if (scope1 == null || scope2 == null) return true;

        // Check tenant overlap
        if (scope1.TenantIds?.Length > 0 && scope2.TenantIds?.Length > 0)
        {
            if (!scope1.TenantIds.Intersect(scope2.TenantIds).Any())
            {
                return false;
            }
        }

        // Check path overlap
        if (scope1.PathPrefixes?.Length > 0 && scope2.PathPrefixes?.Length > 0)
        {
            var hasOverlap = scope1.PathPrefixes.Any(p1 =>
                scope2.PathPrefixes.Any(p2 =>
                    p1.StartsWith(p2, StringComparison.OrdinalIgnoreCase) ||
                    p2.StartsWith(p1, StringComparison.OrdinalIgnoreCase)));

            if (!hasOverlap) return false;
        }

        // Check content type overlap
        if (scope1.ContentTypes?.Length > 0 && scope2.ContentTypes?.Length > 0)
        {
            if (!scope1.ContentTypes.Intersect(scope2.ContentTypes).Any())
            {
                return false;
            }
        }

        return true;
    }

    private static bool AreConflictingActions(LifecycleAction action1, LifecycleAction action2)
    {
        var destructive = new[] { LifecycleAction.Delete, LifecycleAction.Purge, LifecycleAction.Expire };
        var preserving = new[] { LifecycleAction.Archive, LifecycleAction.Hold };

        return (destructive.Contains(action1) && preserving.Contains(action2)) ||
               (destructive.Contains(action2) && preserving.Contains(action1));
    }

    private static bool IsValidCronExpression(string cron)
    {
        // Simple validation for cron format: minute hour day month weekday
        var parts = cron.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 5 || parts.Length > 6) return false;

        var pattern = @"^(\*|[0-9,\-\/]+)$";
        return parts.Take(5).All(p => Regex.IsMatch(p, pattern));
    }

    private static DateTime CalculateNextExecution(string cronExpression)
    {
        // P2-2464: Simplified cron calculation. In production, replace with a proper library (NCrontab).
        // Original code reused now.Minute for "*", then added 1 day when next <= now, turning
        // "* * * * *" (every minute) into "24 hours from now". Fix: for wildcard fields use the
        // smallest valid next value (minute=0 for hour, +1 minute for minute field).
        var parts = cronExpression.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var now = DateTime.UtcNow;

        bool minuteWild = parts[0] == "*";
        bool hourWild = parts.Length > 1 && parts[1] == "*";

        // For wildcard minute: we want "next minute", so start from now+1min truncated.
        // For specific minute: use that minute value.
        int minute = minuteWild ? 0 : int.TryParse(parts[0], out var m) ? m : 0;
        int hour = hourWild ? now.Hour : parts.Length > 1 && int.TryParse(parts[1], out var h) ? h : 0;

        var next = new DateTime(now.Year, now.Month, now.Day, hour, minute, 0, DateTimeKind.Utc);

        if (minuteWild)
        {
            // Wildcard minute: schedule every minute — next execution is now + 1 minute.
            next = now.AddSeconds(60 - now.Second);
        }
        else if (next <= now)
        {
            // Specific time already passed today: schedule for tomorrow (or next hour if hourWild).
            next = hourWild ? next.AddHours(1) : next.AddDays(1);
        }

        return next;
    }

    private DateTime? GetNextScheduledExecution(string policyId)
    {
        return _schedules.TryGetValue(policyId, out var schedule) ? schedule.NextExecution : null;
    }
}
