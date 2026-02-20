using System.Diagnostics;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.Retention;

/// <summary>
/// Decision made by a retention strategy.
/// </summary>
public enum RetentionAction
{
    /// <summary>
    /// Keep the data - do not delete.
    /// </summary>
    Retain,

    /// <summary>
    /// Delete the data.
    /// </summary>
    Delete,

    /// <summary>
    /// Archive the data to cold storage.
    /// </summary>
    Archive,

    /// <summary>
    /// Data is under legal hold - cannot be deleted.
    /// </summary>
    LegalHold,

    /// <summary>
    /// Decision pending - needs more evaluation.
    /// </summary>
    Pending
}

/// <summary>
/// Result of a retention evaluation.
/// </summary>
public sealed class RetentionDecision
{
    /// <summary>
    /// The recommended action.
    /// </summary>
    public required RetentionAction Action { get; init; }

    /// <summary>
    /// Reason for the decision.
    /// </summary>
    public required string Reason { get; init; }

    /// <summary>
    /// When the data should be re-evaluated.
    /// </summary>
    public DateTime? NextEvaluationDate { get; init; }

    /// <summary>
    /// Applicable policy name.
    /// </summary>
    public string? PolicyName { get; init; }

    /// <summary>
    /// Confidence score (0-1) for ML-based decisions.
    /// </summary>
    public double? Confidence { get; init; }

    /// <summary>
    /// Additional metadata about the decision.
    /// </summary>
    public Dictionary<string, object>? Metadata { get; init; }

    /// <summary>
    /// Creates a retain decision.
    /// </summary>
    public static RetentionDecision Retain(string reason, DateTime? nextEvaluation = null) =>
        new() { Action = RetentionAction.Retain, Reason = reason, NextEvaluationDate = nextEvaluation };

    /// <summary>
    /// Creates a delete decision.
    /// </summary>
    public static RetentionDecision Delete(string reason) =>
        new() { Action = RetentionAction.Delete, Reason = reason };

    /// <summary>
    /// Creates an archive decision.
    /// </summary>
    public static RetentionDecision Archive(string reason) =>
        new() { Action = RetentionAction.Archive, Reason = reason };

    /// <summary>
    /// Creates a legal hold decision.
    /// </summary>
    public static RetentionDecision HoldForLegal(string reason, string holdId) =>
        new()
        {
            Action = RetentionAction.LegalHold,
            Reason = reason,
            Metadata = new Dictionary<string, object> { ["HoldId"] = holdId }
        };
}

/// <summary>
/// Scope for applying retention policies.
/// </summary>
public sealed class RetentionScope
{
    /// <summary>
    /// Tenant ID to scope the retention to.
    /// </summary>
    public string? TenantId { get; init; }

    /// <summary>
    /// Path prefix to scope to.
    /// </summary>
    public string? PathPrefix { get; init; }

    /// <summary>
    /// Content types to include.
    /// </summary>
    public string[]? ContentTypes { get; init; }

    /// <summary>
    /// Tags to match.
    /// </summary>
    public string[]? Tags { get; init; }

    /// <summary>
    /// Minimum age for consideration.
    /// </summary>
    public TimeSpan? MinAge { get; init; }

    /// <summary>
    /// Maximum items to process.
    /// </summary>
    public int? MaxItems { get; init; }

    /// <summary>
    /// Whether to perform a dry run (no actual deletions).
    /// </summary>
    public bool DryRun { get; init; } = false;
}

/// <summary>
/// Statistics for retention operations.
/// </summary>
public sealed class RetentionStats
{
    /// <summary>
    /// Total items evaluated.
    /// </summary>
    public long TotalEvaluated { get; set; }

    /// <summary>
    /// Items marked for retention.
    /// </summary>
    public long Retained { get; set; }

    /// <summary>
    /// Items deleted.
    /// </summary>
    public long Deleted { get; set; }

    /// <summary>
    /// Items archived.
    /// </summary>
    public long Archived { get; set; }

    /// <summary>
    /// Items under legal hold.
    /// </summary>
    public long LegalHold { get; set; }

    /// <summary>
    /// Bytes freed through deletion.
    /// </summary>
    public long BytesFreed { get; set; }

    /// <summary>
    /// Bytes moved to archive.
    /// </summary>
    public long BytesArchived { get; set; }

    /// <summary>
    /// Last evaluation time.
    /// </summary>
    public DateTime? LastEvaluationTime { get; set; }

    /// <summary>
    /// Last evaluation duration.
    /// </summary>
    public TimeSpan? LastEvaluationDuration { get; set; }
}

/// <summary>
/// Represents a data object for retention evaluation.
/// </summary>
public sealed class DataObject
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
    /// Custom metadata.
    /// </summary>
    public Dictionary<string, object>? Metadata { get; init; }

    /// <summary>
    /// Gets the age of the object.
    /// </summary>
    public TimeSpan Age => DateTime.UtcNow - CreatedAt;

    /// <summary>
    /// Gets the time since last access.
    /// </summary>
    public TimeSpan? TimeSinceLastAccess =>
        LastAccessedAt.HasValue ? DateTime.UtcNow - LastAccessedAt.Value : null;
}

/// <summary>
/// Interface for retention strategies.
/// </summary>
public interface IRetentionStrategy : IDataManagementStrategy
{
    /// <summary>
    /// Evaluates retention for a single data object.
    /// </summary>
    /// <param name="data">The data object to evaluate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Retention decision.</returns>
    Task<RetentionDecision> EvaluateAsync(DataObject data, CancellationToken ct);

    /// <summary>
    /// Applies retention policy to objects within scope.
    /// </summary>
    /// <param name="scope">Scope for retention application.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of objects affected.</returns>
    Task<int> ApplyRetentionAsync(RetentionScope scope, CancellationToken ct);

    /// <summary>
    /// Gets retention statistics.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Current statistics.</returns>
    Task<RetentionStats> GetStatsAsync(CancellationToken ct);
}

/// <summary>
/// Abstract base class for retention strategies.
/// </summary>
public abstract class RetentionStrategyBase : DataManagementStrategyBase, IRetentionStrategy
{
    /// <summary>
    /// Thread-safe collection of tracked objects.
    /// </summary>
    protected readonly BoundedDictionary<string, DataObject> TrackedObjects = new BoundedDictionary<string, DataObject>(1000);

    /// <summary>
    /// Lock for statistics updates.
    /// </summary>
    private readonly object _statsLock = new();

    /// <summary>
    /// Current retention statistics.
    /// </summary>
    private readonly RetentionStats _stats = new();

    /// <inheritdoc/>
    public override DataManagementCategory Category => DataManagementCategory.Lifecycle;

    /// <inheritdoc/>
    public async Task<RetentionDecision> EvaluateAsync(DataObject data, CancellationToken ct)
    {
        ThrowIfNotInitialized();
        ArgumentNullException.ThrowIfNull(data);

        var sw = Stopwatch.StartNew();
        try
        {
            var decision = await EvaluateCoreAsync(data, ct);
            sw.Stop();

            UpdateEvaluationStats(decision);
            RecordRead(0, sw.Elapsed.TotalMilliseconds);

            return decision;
        }
        catch (Exception ex)
        {
            sw.Stop();
            RecordFailure();
            return RetentionDecision.Retain($"Error during evaluation: {ex.Message}");
        }
    }

    /// <inheritdoc/>
    public async Task<int> ApplyRetentionAsync(RetentionScope scope, CancellationToken ct)
    {
        ThrowIfNotInitialized();
        ArgumentNullException.ThrowIfNull(scope);

        var sw = Stopwatch.StartNew();
        try
        {
            var affected = await ApplyRetentionCoreAsync(scope, ct);
            sw.Stop();

            lock (_statsLock)
            {
                _stats.LastEvaluationTime = DateTime.UtcNow;
                _stats.LastEvaluationDuration = sw.Elapsed;
            }

            RecordWrite(0, sw.Elapsed.TotalMilliseconds);
            return affected;
        }
        catch
        {
            sw.Stop();
            RecordFailure();
            throw;
        }
    }

    /// <inheritdoc/>
    public Task<RetentionStats> GetStatsAsync(CancellationToken ct)
    {
        ThrowIfNotInitialized();

        lock (_statsLock)
        {
            return Task.FromResult(new RetentionStats
            {
                TotalEvaluated = _stats.TotalEvaluated,
                Retained = _stats.Retained,
                Deleted = _stats.Deleted,
                Archived = _stats.Archived,
                LegalHold = _stats.LegalHold,
                BytesFreed = _stats.BytesFreed,
                BytesArchived = _stats.BytesArchived,
                LastEvaluationTime = _stats.LastEvaluationTime,
                LastEvaluationDuration = _stats.LastEvaluationDuration
            });
        }
    }

    /// <summary>
    /// Core evaluation logic. Must be overridden.
    /// </summary>
    protected abstract Task<RetentionDecision> EvaluateCoreAsync(DataObject data, CancellationToken ct);

    /// <summary>
    /// Core retention application logic. Default implementation evaluates all tracked objects.
    /// </summary>
    protected virtual async Task<int> ApplyRetentionCoreAsync(RetentionScope scope, CancellationToken ct)
    {
        var affected = 0;
        var objects = GetObjectsInScope(scope);
        var processed = 0;

        foreach (var obj in objects)
        {
            ct.ThrowIfCancellationRequested();

            if (scope.MaxItems.HasValue && processed >= scope.MaxItems.Value)
                break;

            var decision = await EvaluateCoreAsync(obj, ct);

            if (!scope.DryRun)
            {
                affected += ApplyDecision(obj, decision);
            }
            else if (decision.Action == RetentionAction.Delete || decision.Action == RetentionAction.Archive)
            {
                affected++;
            }

            processed++;
        }

        return affected;
    }

    /// <summary>
    /// Gets objects within the specified scope.
    /// </summary>
    protected virtual IEnumerable<DataObject> GetObjectsInScope(RetentionScope scope)
    {
        var objects = TrackedObjects.Values.AsEnumerable();

        if (!string.IsNullOrEmpty(scope.TenantId))
        {
            objects = objects.Where(o => o.TenantId == scope.TenantId);
        }

        if (!string.IsNullOrEmpty(scope.PathPrefix))
        {
            objects = objects.Where(o => o.Path?.StartsWith(scope.PathPrefix) == true);
        }

        if (scope.ContentTypes?.Length > 0)
        {
            objects = objects.Where(o => o.ContentType != null && scope.ContentTypes.Contains(o.ContentType));
        }

        if (scope.Tags?.Length > 0)
        {
            objects = objects.Where(o => o.Tags?.Intersect(scope.Tags).Any() == true);
        }

        if (scope.MinAge.HasValue)
        {
            var minDate = DateTime.UtcNow - scope.MinAge.Value;
            objects = objects.Where(o => o.CreatedAt < minDate);
        }

        return objects;
    }

    /// <summary>
    /// Applies a retention decision to an object.
    /// </summary>
    protected virtual int ApplyDecision(DataObject obj, RetentionDecision decision)
    {
        switch (decision.Action)
        {
            case RetentionAction.Delete:
                if (TrackedObjects.TryRemove(obj.ObjectId, out _))
                {
                    lock (_statsLock)
                    {
                        _stats.Deleted++;
                        _stats.BytesFreed += obj.Size;
                    }
                    return 1;
                }
                break;

            case RetentionAction.Archive:
                lock (_statsLock)
                {
                    _stats.Archived++;
                    _stats.BytesArchived += obj.Size;
                }
                return 1;

            case RetentionAction.LegalHold:
                lock (_statsLock)
                {
                    _stats.LegalHold++;
                }
                break;
        }

        return 0;
    }

    /// <summary>
    /// Updates evaluation statistics.
    /// </summary>
    protected void UpdateEvaluationStats(RetentionDecision decision)
    {
        lock (_statsLock)
        {
            _stats.TotalEvaluated++;

            switch (decision.Action)
            {
                case RetentionAction.Retain:
                    _stats.Retained++;
                    break;
                case RetentionAction.Delete:
                    break; // Counted when actually deleted
                case RetentionAction.Archive:
                    break; // Counted when actually archived
                case RetentionAction.LegalHold:
                    break; // Counted when hold applied
            }
        }
    }

    /// <summary>
    /// Tracks an object for retention management.
    /// </summary>
    /// <param name="obj">Object to track.</param>
    public void TrackObject(DataObject obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        TrackedObjects[obj.ObjectId] = obj;
    }

    /// <summary>
    /// Removes an object from tracking.
    /// </summary>
    /// <param name="objectId">Object ID to remove.</param>
    /// <returns>True if removed.</returns>
    public bool UntrackObject(string objectId)
    {
        return TrackedObjects.TryRemove(objectId, out _);
    }
}
