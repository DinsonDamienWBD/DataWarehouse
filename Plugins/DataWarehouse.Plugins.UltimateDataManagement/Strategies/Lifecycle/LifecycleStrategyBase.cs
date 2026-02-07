using System.Collections.Concurrent;
using System.Diagnostics;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.Lifecycle;

/// <summary>
/// Actions that can be taken on data objects during lifecycle management.
/// </summary>
public enum LifecycleAction
{
    /// <summary>
    /// No action required - data remains in current state.
    /// </summary>
    None,

    /// <summary>
    /// Archive data to cold storage.
    /// </summary>
    Archive,

    /// <summary>
    /// Delete data permanently.
    /// </summary>
    Delete,

    /// <summary>
    /// Move data to a different storage tier.
    /// </summary>
    Tier,

    /// <summary>
    /// Notify stakeholders about the data.
    /// </summary>
    Notify,

    /// <summary>
    /// Migrate data to another location.
    /// </summary>
    Migrate,

    /// <summary>
    /// Expire the data (mark as expired).
    /// </summary>
    Expire,

    /// <summary>
    /// Purge data securely.
    /// </summary>
    Purge,

    /// <summary>
    /// Classify or reclassify the data.
    /// </summary>
    Classify,

    /// <summary>
    /// Place data on legal hold.
    /// </summary>
    Hold,

    /// <summary>
    /// Compress the data.
    /// </summary>
    Compress,

    /// <summary>
    /// Encrypt the data.
    /// </summary>
    Encrypt,

    /// <summary>
    /// Restore data from archive.
    /// </summary>
    Restore
}

/// <summary>
/// Classification labels for data objects.
/// </summary>
public enum ClassificationLabel
{
    /// <summary>
    /// Unclassified data.
    /// </summary>
    Unclassified = 0,

    /// <summary>
    /// Public data - no restrictions.
    /// </summary>
    Public = 1,

    /// <summary>
    /// Internal use only.
    /// </summary>
    Internal = 2,

    /// <summary>
    /// Confidential data with restricted access.
    /// </summary>
    Confidential = 3,

    /// <summary>
    /// Personally Identifiable Information - highest protection.
    /// </summary>
    PII = 4,

    /// <summary>
    /// Protected Health Information (HIPAA).
    /// </summary>
    PHI = 5,

    /// <summary>
    /// Payment Card Industry data.
    /// </summary>
    PCI = 6,

    /// <summary>
    /// Sensitive data requiring special handling.
    /// </summary>
    Sensitive = 7
}

/// <summary>
/// Represents a lifecycle management decision for a data object.
/// </summary>
public sealed class LifecycleDecision
{
    /// <summary>
    /// The recommended action to take.
    /// </summary>
    public required LifecycleAction Action { get; init; }

    /// <summary>
    /// Reason for the decision.
    /// </summary>
    public required string Reason { get; init; }

    /// <summary>
    /// Priority of the action (0-1, higher = more urgent).
    /// </summary>
    public double Priority { get; init; } = 0.5;

    /// <summary>
    /// Confidence score for the decision (0-1).
    /// </summary>
    public double Confidence { get; init; } = 1.0;

    /// <summary>
    /// When the action should be executed (null = immediately).
    /// </summary>
    public DateTime? ScheduledAt { get; init; }

    /// <summary>
    /// When to re-evaluate the decision.
    /// </summary>
    public DateTime? NextEvaluationDate { get; init; }

    /// <summary>
    /// Target location for migration/tier actions.
    /// </summary>
    public string? TargetLocation { get; init; }

    /// <summary>
    /// Additional action parameters.
    /// </summary>
    public Dictionary<string, object>? Parameters { get; init; }

    /// <summary>
    /// Policy that triggered this decision.
    /// </summary>
    public string? PolicyName { get; init; }

    /// <summary>
    /// Creates a no-action decision.
    /// </summary>
    public static LifecycleDecision NoAction(string reason, DateTime? nextEvaluation = null) =>
        new() { Action = LifecycleAction.None, Reason = reason, NextEvaluationDate = nextEvaluation };

    /// <summary>
    /// Creates an archive decision.
    /// </summary>
    public static LifecycleDecision Archive(string reason, string? target = null, double priority = 0.5) =>
        new() { Action = LifecycleAction.Archive, Reason = reason, TargetLocation = target, Priority = priority };

    /// <summary>
    /// Creates a delete decision.
    /// </summary>
    public static LifecycleDecision Delete(string reason, double priority = 0.5) =>
        new() { Action = LifecycleAction.Delete, Reason = reason, Priority = priority };

    /// <summary>
    /// Creates a tier movement decision.
    /// </summary>
    public static LifecycleDecision Tier(string reason, string targetTier, double priority = 0.5) =>
        new() { Action = LifecycleAction.Tier, Reason = reason, TargetLocation = targetTier, Priority = priority };

    /// <summary>
    /// Creates a notification decision.
    /// </summary>
    public static LifecycleDecision Notify(string reason, Dictionary<string, object>? notifyParams = null) =>
        new() { Action = LifecycleAction.Notify, Reason = reason, Parameters = notifyParams };

    /// <summary>
    /// Creates a purge decision.
    /// </summary>
    public static LifecycleDecision Purge(string reason, string method, double priority = 0.5) =>
        new()
        {
            Action = LifecycleAction.Purge,
            Reason = reason,
            Priority = priority,
            Parameters = new Dictionary<string, object> { ["Method"] = method }
        };

    /// <summary>
    /// Creates an expiration decision.
    /// </summary>
    public static LifecycleDecision Expire(string reason, bool softDelete = true) =>
        new()
        {
            Action = LifecycleAction.Expire,
            Reason = reason,
            Parameters = new Dictionary<string, object> { ["SoftDelete"] = softDelete }
        };

    /// <summary>
    /// Creates a migration decision.
    /// </summary>
    public static LifecycleDecision Migrate(string reason, string targetLocation, double priority = 0.5) =>
        new() { Action = LifecycleAction.Migrate, Reason = reason, TargetLocation = targetLocation, Priority = priority };
}

/// <summary>
/// Represents a lifecycle policy with conditions and actions.
/// </summary>
public sealed class LifecyclePolicy
{
    /// <summary>
    /// Unique identifier for the policy.
    /// </summary>
    public required string PolicyId { get; init; }

    /// <summary>
    /// Display name for the policy.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Description of the policy.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Priority of the policy (higher = evaluated first).
    /// </summary>
    public int Priority { get; init; } = 100;

    /// <summary>
    /// Whether the policy is enabled.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Conditions that must be met for the policy to apply.
    /// </summary>
    public required List<PolicyCondition> Conditions { get; init; }

    /// <summary>
    /// Action to take when conditions are met.
    /// </summary>
    public required LifecycleAction Action { get; init; }

    /// <summary>
    /// Action parameters.
    /// </summary>
    public Dictionary<string, object>? ActionParameters { get; init; }

    /// <summary>
    /// Cron expression for scheduled evaluation (null = continuous).
    /// </summary>
    public string? CronSchedule { get; init; }

    /// <summary>
    /// Scope filter for the policy.
    /// </summary>
    public PolicyScope? Scope { get; init; }

    /// <summary>
    /// When the policy was created.
    /// </summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// When the policy was last modified.
    /// </summary>
    public DateTime? LastModifiedAt { get; init; }

    /// <summary>
    /// Policies that conflict with this one.
    /// </summary>
    public List<string>? ConflictsWith { get; init; }
}

/// <summary>
/// Represents a condition for policy evaluation.
/// </summary>
public sealed class PolicyCondition
{
    /// <summary>
    /// Field to evaluate.
    /// </summary>
    public required string Field { get; init; }

    /// <summary>
    /// Comparison operator.
    /// </summary>
    public required ConditionOperator Operator { get; init; }

    /// <summary>
    /// Value to compare against.
    /// </summary>
    public required object Value { get; init; }

    /// <summary>
    /// Logical operator for combining with next condition.
    /// </summary>
    public LogicalOperator LogicalOperator { get; init; } = LogicalOperator.And;
}

/// <summary>
/// Comparison operators for policy conditions.
/// </summary>
public enum ConditionOperator
{
    /// <summary>
    /// Equal to.
    /// </summary>
    Equals,

    /// <summary>
    /// Not equal to.
    /// </summary>
    NotEquals,

    /// <summary>
    /// Greater than.
    /// </summary>
    GreaterThan,

    /// <summary>
    /// Greater than or equal to.
    /// </summary>
    GreaterThanOrEqual,

    /// <summary>
    /// Less than.
    /// </summary>
    LessThan,

    /// <summary>
    /// Less than or equal to.
    /// </summary>
    LessThanOrEqual,

    /// <summary>
    /// Contains the value.
    /// </summary>
    Contains,

    /// <summary>
    /// Starts with the value.
    /// </summary>
    StartsWith,

    /// <summary>
    /// Ends with the value.
    /// </summary>
    EndsWith,

    /// <summary>
    /// Matches regex pattern.
    /// </summary>
    Matches,

    /// <summary>
    /// Value is in list.
    /// </summary>
    In,

    /// <summary>
    /// Value is not in list.
    /// </summary>
    NotIn,

    /// <summary>
    /// Value exists.
    /// </summary>
    Exists,

    /// <summary>
    /// Value is between range.
    /// </summary>
    Between
}

/// <summary>
/// Logical operators for combining conditions.
/// </summary>
public enum LogicalOperator
{
    /// <summary>
    /// Both conditions must be true.
    /// </summary>
    And,

    /// <summary>
    /// Either condition can be true.
    /// </summary>
    Or
}

/// <summary>
/// Scope filter for policies.
/// </summary>
public sealed class PolicyScope
{
    /// <summary>
    /// Tenant IDs to include.
    /// </summary>
    public string[]? TenantIds { get; init; }

    /// <summary>
    /// Path prefixes to include.
    /// </summary>
    public string[]? PathPrefixes { get; init; }

    /// <summary>
    /// Content types to include.
    /// </summary>
    public string[]? ContentTypes { get; init; }

    /// <summary>
    /// Classification labels to include.
    /// </summary>
    public ClassificationLabel[]? Classifications { get; init; }

    /// <summary>
    /// Tags to match.
    /// </summary>
    public string[]? Tags { get; init; }

    /// <summary>
    /// Minimum object size.
    /// </summary>
    public long? MinSize { get; init; }

    /// <summary>
    /// Maximum object size.
    /// </summary>
    public long? MaxSize { get; init; }
}

/// <summary>
/// Statistics for lifecycle operations.
/// </summary>
public sealed class LifecycleStats
{
    /// <summary>
    /// Total objects evaluated.
    /// </summary>
    public long TotalEvaluated { get; set; }

    /// <summary>
    /// Objects by action taken.
    /// </summary>
    public Dictionary<LifecycleAction, long> ActionCounts { get; set; } = new();

    /// <summary>
    /// Total bytes processed.
    /// </summary>
    public long TotalBytesProcessed { get; set; }

    /// <summary>
    /// Total bytes freed (deleted/purged).
    /// </summary>
    public long TotalBytesFreed { get; set; }

    /// <summary>
    /// Total bytes archived.
    /// </summary>
    public long TotalBytesArchived { get; set; }

    /// <summary>
    /// Total bytes migrated.
    /// </summary>
    public long TotalBytesMigrated { get; set; }

    /// <summary>
    /// Policies executed.
    /// </summary>
    public long PoliciesExecuted { get; set; }

    /// <summary>
    /// Failed operations.
    /// </summary>
    public long FailedOperations { get; set; }

    /// <summary>
    /// Last evaluation time.
    /// </summary>
    public DateTime? LastEvaluationTime { get; set; }

    /// <summary>
    /// Average evaluation time in milliseconds.
    /// </summary>
    public double AverageEvaluationTimeMs { get; set; }

    /// <summary>
    /// Objects currently pending action.
    /// </summary>
    public long PendingActions { get; set; }

    /// <summary>
    /// Objects on legal hold.
    /// </summary>
    public long ObjectsOnHold { get; set; }
}

/// <summary>
/// Extended data object for lifecycle management with additional properties.
/// </summary>
public sealed class LifecycleDataObject
{
    /// <summary>
    /// Object identifier.
    /// </summary>
    public required string ObjectId { get; init; }

    /// <summary>
    /// Object path or name.
    /// </summary>
    public string? Path { get; init; }

    /// <summary>
    /// Content type.
    /// </summary>
    public string? ContentType { get; init; }

    /// <summary>
    /// Size in bytes.
    /// </summary>
    public long Size { get; init; }

    /// <summary>
    /// When the object was created.
    /// </summary>
    public required DateTime CreatedAt { get; init; }

    /// <summary>
    /// When the object was last modified.
    /// </summary>
    public DateTime? LastModifiedAt { get; init; }

    /// <summary>
    /// When the object was last accessed.
    /// </summary>
    public DateTime? LastAccessedAt { get; init; }

    /// <summary>
    /// Version number.
    /// </summary>
    public int Version { get; init; } = 1;

    /// <summary>
    /// Whether this is the latest version.
    /// </summary>
    public bool IsLatestVersion { get; init; } = true;

    /// <summary>
    /// Tenant ID.
    /// </summary>
    public string? TenantId { get; init; }

    /// <summary>
    /// Object tags.
    /// </summary>
    public string[]? Tags { get; init; }

    /// <summary>
    /// Classification label.
    /// </summary>
    public ClassificationLabel Classification { get; init; } = ClassificationLabel.Unclassified;

    /// <summary>
    /// Current storage tier.
    /// </summary>
    public string? StorageTier { get; init; }

    /// <summary>
    /// Current storage location/region.
    /// </summary>
    public string? StorageLocation { get; init; }

    /// <summary>
    /// Expiration date if set.
    /// </summary>
    public DateTime? ExpiresAt { get; init; }

    /// <summary>
    /// Whether object is on legal hold.
    /// </summary>
    public bool IsOnHold { get; init; }

    /// <summary>
    /// Legal hold ID if on hold.
    /// </summary>
    public string? HoldId { get; init; }

    /// <summary>
    /// Whether object is archived.
    /// </summary>
    public bool IsArchived { get; init; }

    /// <summary>
    /// Whether object is soft deleted.
    /// </summary>
    public bool IsSoftDeleted { get; init; }

    /// <summary>
    /// When soft deleted if applicable.
    /// </summary>
    public DateTime? SoftDeletedAt { get; init; }

    /// <summary>
    /// Content hash for integrity.
    /// </summary>
    public string? ContentHash { get; init; }

    /// <summary>
    /// Whether content is encrypted.
    /// </summary>
    public bool IsEncrypted { get; init; }

    /// <summary>
    /// Encryption key ID if encrypted.
    /// </summary>
    public string? EncryptionKeyId { get; init; }

    /// <summary>
    /// Custom metadata.
    /// </summary>
    public Dictionary<string, object>? Metadata { get; init; }

    /// <summary>
    /// Related object IDs.
    /// </summary>
    public string[]? RelatedObjectIds { get; init; }

    /// <summary>
    /// Gets the age of the object.
    /// </summary>
    public TimeSpan Age => DateTime.UtcNow - CreatedAt;

    /// <summary>
    /// Gets the time since last access.
    /// </summary>
    public TimeSpan? TimeSinceLastAccess =>
        LastAccessedAt.HasValue ? DateTime.UtcNow - LastAccessedAt.Value : null;

    /// <summary>
    /// Gets whether the object is expired.
    /// </summary>
    public bool IsExpired => ExpiresAt.HasValue && DateTime.UtcNow > ExpiresAt.Value;

    /// <summary>
    /// Gets remaining time until expiration.
    /// </summary>
    public TimeSpan? TimeUntilExpiration =>
        ExpiresAt.HasValue ? ExpiresAt.Value - DateTime.UtcNow : null;
}

/// <summary>
/// Interface for lifecycle strategies.
/// </summary>
public interface ILifecycleStrategy : IDataManagementStrategy
{
    /// <summary>
    /// Evaluates lifecycle decision for a data object.
    /// </summary>
    /// <param name="data">The data object to evaluate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Lifecycle decision.</returns>
    Task<LifecycleDecision> EvaluateAsync(LifecycleDataObject data, CancellationToken ct);

    /// <summary>
    /// Executes a lifecycle policy.
    /// </summary>
    /// <param name="policy">The policy to execute.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of objects affected.</returns>
    Task<int> ExecutePolicyAsync(LifecyclePolicy policy, CancellationToken ct);

    /// <summary>
    /// Gets lifecycle statistics.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Current statistics.</returns>
    Task<LifecycleStats> GetStatsAsync(CancellationToken ct);
}

/// <summary>
/// Abstract base class for lifecycle strategies.
/// Provides common functionality for lifecycle management operations.
/// </summary>
public abstract class LifecycleStrategyBase : DataManagementStrategyBase, ILifecycleStrategy
{
    /// <summary>
    /// Thread-safe collection of tracked objects.
    /// </summary>
    protected readonly ConcurrentDictionary<string, LifecycleDataObject> TrackedObjects = new();

    /// <summary>
    /// Thread-safe collection of registered policies.
    /// </summary>
    protected readonly ConcurrentDictionary<string, LifecyclePolicy> Policies = new();

    /// <summary>
    /// Thread-safe collection of pending actions.
    /// </summary>
    protected readonly ConcurrentDictionary<string, LifecycleDecision> PendingActions = new();

    /// <summary>
    /// Lock for statistics updates.
    /// </summary>
    private readonly object _statsLock = new();

    /// <summary>
    /// Current lifecycle statistics.
    /// </summary>
    private readonly LifecycleStats _stats = new();

    private double _totalEvaluationTimeMs;
    private long _evaluationCount;

    /// <inheritdoc/>
    public override DataManagementCategory Category => DataManagementCategory.Lifecycle;

    /// <inheritdoc/>
    public async Task<LifecycleDecision> EvaluateAsync(LifecycleDataObject data, CancellationToken ct)
    {
        ThrowIfNotInitialized();
        ArgumentNullException.ThrowIfNull(data);

        var sw = Stopwatch.StartNew();
        try
        {
            var decision = await EvaluateCoreAsync(data, ct);
            sw.Stop();

            UpdateEvaluationStats(decision, sw.Elapsed.TotalMilliseconds);
            RecordRead(0, sw.Elapsed.TotalMilliseconds);

            if (decision.Action != LifecycleAction.None)
            {
                PendingActions[data.ObjectId] = decision;
            }

            return decision;
        }
        catch (Exception ex)
        {
            sw.Stop();
            RecordFailure();
            return LifecycleDecision.NoAction($"Evaluation failed: {ex.Message}");
        }
    }

    /// <inheritdoc/>
    public async Task<int> ExecutePolicyAsync(LifecyclePolicy policy, CancellationToken ct)
    {
        ThrowIfNotInitialized();
        ArgumentNullException.ThrowIfNull(policy);

        if (!policy.Enabled)
        {
            return 0;
        }

        var sw = Stopwatch.StartNew();
        try
        {
            var affected = await ExecutePolicyCoreAsync(policy, ct);
            sw.Stop();

            lock (_statsLock)
            {
                _stats.PoliciesExecuted++;
                _stats.LastEvaluationTime = DateTime.UtcNow;
            }

            RecordWrite(0, sw.Elapsed.TotalMilliseconds);
            return affected;
        }
        catch
        {
            sw.Stop();
            lock (_statsLock)
            {
                _stats.FailedOperations++;
            }
            RecordFailure();
            throw;
        }
    }

    /// <inheritdoc/>
    public Task<LifecycleStats> GetStatsAsync(CancellationToken ct)
    {
        ThrowIfNotInitialized();

        lock (_statsLock)
        {
            return Task.FromResult(new LifecycleStats
            {
                TotalEvaluated = _stats.TotalEvaluated,
                ActionCounts = new Dictionary<LifecycleAction, long>(_stats.ActionCounts),
                TotalBytesProcessed = _stats.TotalBytesProcessed,
                TotalBytesFreed = _stats.TotalBytesFreed,
                TotalBytesArchived = _stats.TotalBytesArchived,
                TotalBytesMigrated = _stats.TotalBytesMigrated,
                PoliciesExecuted = _stats.PoliciesExecuted,
                FailedOperations = _stats.FailedOperations,
                LastEvaluationTime = _stats.LastEvaluationTime,
                AverageEvaluationTimeMs = _evaluationCount > 0 ? _totalEvaluationTimeMs / _evaluationCount : 0,
                PendingActions = PendingActions.Count,
                ObjectsOnHold = TrackedObjects.Values.Count(o => o.IsOnHold)
            });
        }
    }

    /// <summary>
    /// Core evaluation logic. Must be overridden.
    /// </summary>
    protected abstract Task<LifecycleDecision> EvaluateCoreAsync(LifecycleDataObject data, CancellationToken ct);

    /// <summary>
    /// Core policy execution logic. Default implementation evaluates all tracked objects.
    /// </summary>
    protected virtual async Task<int> ExecutePolicyCoreAsync(LifecyclePolicy policy, CancellationToken ct)
    {
        var affected = 0;
        var objects = GetObjectsInScope(policy.Scope);

        foreach (var obj in objects)
        {
            ct.ThrowIfCancellationRequested();

            if (EvaluatePolicyConditions(obj, policy.Conditions))
            {
                var decision = new LifecycleDecision
                {
                    Action = policy.Action,
                    Reason = $"Policy: {policy.Name}",
                    PolicyName = policy.PolicyId,
                    Parameters = policy.ActionParameters
                };

                affected += await ApplyDecisionAsync(obj, decision, ct);
            }
        }

        return affected;
    }

    /// <summary>
    /// Gets objects within the specified scope.
    /// </summary>
    protected virtual IEnumerable<LifecycleDataObject> GetObjectsInScope(PolicyScope? scope)
    {
        var objects = TrackedObjects.Values.AsEnumerable();

        if (scope == null)
        {
            return objects;
        }

        if (scope.TenantIds?.Length > 0)
        {
            objects = objects.Where(o => o.TenantId != null && scope.TenantIds.Contains(o.TenantId));
        }

        if (scope.PathPrefixes?.Length > 0)
        {
            objects = objects.Where(o => o.Path != null &&
                scope.PathPrefixes.Any(p => o.Path.StartsWith(p, StringComparison.OrdinalIgnoreCase)));
        }

        if (scope.ContentTypes?.Length > 0)
        {
            objects = objects.Where(o => o.ContentType != null && scope.ContentTypes.Contains(o.ContentType));
        }

        if (scope.Classifications?.Length > 0)
        {
            objects = objects.Where(o => scope.Classifications.Contains(o.Classification));
        }

        if (scope.Tags?.Length > 0)
        {
            objects = objects.Where(o => o.Tags?.Intersect(scope.Tags).Any() == true);
        }

        if (scope.MinSize.HasValue)
        {
            objects = objects.Where(o => o.Size >= scope.MinSize.Value);
        }

        if (scope.MaxSize.HasValue)
        {
            objects = objects.Where(o => o.Size <= scope.MaxSize.Value);
        }

        return objects;
    }

    /// <summary>
    /// Evaluates policy conditions against a data object.
    /// </summary>
    protected virtual bool EvaluatePolicyConditions(LifecycleDataObject obj, List<PolicyCondition> conditions)
    {
        if (conditions.Count == 0)
        {
            return true;
        }

        bool result = true;
        var currentLogicalOp = LogicalOperator.And;

        foreach (var condition in conditions)
        {
            var conditionResult = EvaluateSingleCondition(obj, condition);

            result = currentLogicalOp switch
            {
                LogicalOperator.And => result && conditionResult,
                LogicalOperator.Or => result || conditionResult,
                _ => result && conditionResult
            };

            currentLogicalOp = condition.LogicalOperator;
        }

        return result;
    }

    /// <summary>
    /// Evaluates a single condition against a data object.
    /// </summary>
    protected virtual bool EvaluateSingleCondition(LifecycleDataObject obj, PolicyCondition condition)
    {
        var fieldValue = GetFieldValue(obj, condition.Field);

        return condition.Operator switch
        {
            ConditionOperator.Equals => Equals(fieldValue, condition.Value),
            ConditionOperator.NotEquals => !Equals(fieldValue, condition.Value),
            ConditionOperator.GreaterThan => CompareValues(fieldValue, condition.Value) > 0,
            ConditionOperator.GreaterThanOrEqual => CompareValues(fieldValue, condition.Value) >= 0,
            ConditionOperator.LessThan => CompareValues(fieldValue, condition.Value) < 0,
            ConditionOperator.LessThanOrEqual => CompareValues(fieldValue, condition.Value) <= 0,
            ConditionOperator.Contains => fieldValue?.ToString()?.Contains(condition.Value.ToString() ?? "", StringComparison.OrdinalIgnoreCase) == true,
            ConditionOperator.StartsWith => fieldValue?.ToString()?.StartsWith(condition.Value.ToString() ?? "", StringComparison.OrdinalIgnoreCase) == true,
            ConditionOperator.EndsWith => fieldValue?.ToString()?.EndsWith(condition.Value.ToString() ?? "", StringComparison.OrdinalIgnoreCase) == true,
            ConditionOperator.Exists => fieldValue != null,
            ConditionOperator.In => condition.Value is IEnumerable<object> list && list.Contains(fieldValue),
            ConditionOperator.NotIn => condition.Value is IEnumerable<object> notList && !notList.Contains(fieldValue),
            _ => false
        };
    }

    /// <summary>
    /// Gets a field value from a data object.
    /// </summary>
    protected virtual object? GetFieldValue(LifecycleDataObject obj, string field)
    {
        return field.ToLowerInvariant() switch
        {
            "objectid" => obj.ObjectId,
            "path" => obj.Path,
            "contenttype" => obj.ContentType,
            "size" => obj.Size,
            "createdat" => obj.CreatedAt,
            "lastmodifiedat" => obj.LastModifiedAt,
            "lastaccessedat" => obj.LastAccessedAt,
            "age" => obj.Age,
            "agedays" => obj.Age.TotalDays,
            "classification" => obj.Classification,
            "storagetier" => obj.StorageTier,
            "isonhold" => obj.IsOnHold,
            "isarchived" => obj.IsArchived,
            "issoftdeleted" => obj.IsSoftDeleted,
            "isexpired" => obj.IsExpired,
            "tenantid" => obj.TenantId,
            "isencrypted" => obj.IsEncrypted,
            _ => obj.Metadata?.TryGetValue(field, out var v) == true ? v : null
        };
    }

    /// <summary>
    /// Compares two values.
    /// </summary>
    protected static int CompareValues(object? a, object? b)
    {
        if (a == null && b == null) return 0;
        if (a == null) return -1;
        if (b == null) return 1;

        if (a is IComparable comparableA && b is IComparable comparableB)
        {
            try
            {
                if (a.GetType() != b.GetType())
                {
                    b = Convert.ChangeType(b, a.GetType());
                }
                return comparableA.CompareTo(b);
            }
            catch
            {
                return string.Compare(a.ToString(), b.ToString(), StringComparison.OrdinalIgnoreCase);
            }
        }

        return string.Compare(a.ToString(), b.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Applies a lifecycle decision to a data object.
    /// </summary>
    protected virtual Task<int> ApplyDecisionAsync(LifecycleDataObject obj, LifecycleDecision decision, CancellationToken ct)
    {
        lock (_statsLock)
        {
            if (!_stats.ActionCounts.TryGetValue(decision.Action, out var count))
            {
                count = 0;
            }
            _stats.ActionCounts[decision.Action] = count + 1;
            _stats.TotalBytesProcessed += obj.Size;

            switch (decision.Action)
            {
                case LifecycleAction.Delete:
                case LifecycleAction.Purge:
                    _stats.TotalBytesFreed += obj.Size;
                    TrackedObjects.TryRemove(obj.ObjectId, out _);
                    break;
                case LifecycleAction.Archive:
                    _stats.TotalBytesArchived += obj.Size;
                    break;
                case LifecycleAction.Migrate:
                    _stats.TotalBytesMigrated += obj.Size;
                    break;
            }
        }

        PendingActions.TryRemove(obj.ObjectId, out _);
        return Task.FromResult(1);
    }

    /// <summary>
    /// Updates evaluation statistics.
    /// </summary>
    protected void UpdateEvaluationStats(LifecycleDecision decision, double timeMs)
    {
        lock (_statsLock)
        {
            _stats.TotalEvaluated++;
            _stats.LastEvaluationTime = DateTime.UtcNow;
            _totalEvaluationTimeMs += timeMs;
            _evaluationCount++;
        }
    }

    /// <summary>
    /// Tracks an object for lifecycle management.
    /// </summary>
    public void TrackObject(LifecycleDataObject obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        TrackedObjects[obj.ObjectId] = obj;
    }

    /// <summary>
    /// Removes an object from tracking.
    /// </summary>
    public bool UntrackObject(string objectId)
    {
        PendingActions.TryRemove(objectId, out _);
        return TrackedObjects.TryRemove(objectId, out _);
    }

    /// <summary>
    /// Registers a lifecycle policy.
    /// </summary>
    public void RegisterPolicy(LifecyclePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        Policies[policy.PolicyId] = policy;
    }

    /// <summary>
    /// Unregisters a lifecycle policy.
    /// </summary>
    public bool UnregisterPolicy(string policyId)
    {
        return Policies.TryRemove(policyId, out _);
    }

    /// <summary>
    /// Gets all registered policies ordered by priority.
    /// </summary>
    public IEnumerable<LifecyclePolicy> GetPolicies()
    {
        return Policies.Values.Where(p => p.Enabled).OrderByDescending(p => p.Priority);
    }
}
