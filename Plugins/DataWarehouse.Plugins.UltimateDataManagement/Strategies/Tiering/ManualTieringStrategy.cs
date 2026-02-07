using System.Collections.Concurrent;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.Tiering;

/// <summary>
/// Manual tiering strategy that allows administrators to explicitly control tier assignments.
/// Provides APIs for direct tier assignment, bulk operations, and tier override capabilities.
/// </summary>
/// <remarks>
/// Features:
/// - Explicit tier assignment via API
/// - Bulk tier operations for batch processing
/// - Tier override capabilities with priority
/// - Audit logging of all manual operations
/// - Support for temporary tier locks
/// </remarks>
public sealed class ManualTieringStrategy : TieringStrategyBase
{
    private readonly ConcurrentDictionary<string, TierAssignment> _manualAssignments = new();
    private readonly ConcurrentDictionary<string, TierLock> _tierLocks = new();
    private readonly ConcurrentQueue<TierAuditEntry> _auditLog = new();
    private readonly int _maxAuditEntries = 10000;
    private long _totalManualAssignments;
    private long _totalBulkOperations;

    /// <summary>
    /// Represents a manual tier assignment.
    /// </summary>
    private sealed class TierAssignment
    {
        public required string ObjectId { get; init; }
        public StorageTier TargetTier { get; init; }
        public string? AssignedBy { get; init; }
        public DateTime AssignedAt { get; init; }
        public string? Reason { get; init; }
        public int Priority { get; init; }
    }

    /// <summary>
    /// Represents a tier lock that prevents automatic tier changes.
    /// </summary>
    private sealed class TierLock
    {
        public required string ObjectId { get; init; }
        public StorageTier LockedTier { get; init; }
        public DateTime LockedUntil { get; init; }
        public string? LockedBy { get; init; }
        public string? Reason { get; init; }
    }

    /// <summary>
    /// Audit entry for manual tier operations.
    /// </summary>
    private sealed class TierAuditEntry
    {
        public DateTime Timestamp { get; init; }
        public required string Operation { get; init; }
        public required string ObjectId { get; init; }
        public StorageTier? FromTier { get; init; }
        public StorageTier? ToTier { get; init; }
        public string? PerformedBy { get; init; }
        public string? Reason { get; init; }
        public bool Success { get; init; }
    }

    /// <inheritdoc/>
    public override string StrategyId => "tiering.manual";

    /// <inheritdoc/>
    public override string DisplayName => "Manual Tiering";

    /// <inheritdoc/>
    public override DataManagementCapabilities Capabilities { get; } = new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = true,
        SupportsTransactions = false,
        SupportsTTL = true,
        MaxThroughput = 10000,
        TypicalLatencyMs = 1.0
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Manual tiering strategy for administrator-controlled tier assignments. " +
        "Provides explicit tier assignment API, bulk operations, tier overrides, " +
        "and comprehensive audit logging for compliance and governance.";

    /// <inheritdoc/>
    public override string[] Tags => ["tiering", "manual", "admin", "governance", "audit"];

    /// <summary>
    /// Assigns an object to a specific tier manually.
    /// </summary>
    /// <param name="objectId">The object identifier.</param>
    /// <param name="targetTier">The target storage tier.</param>
    /// <param name="assignedBy">The administrator performing the assignment.</param>
    /// <param name="reason">Optional reason for the assignment.</param>
    /// <param name="priority">Priority of this assignment (higher overrides lower).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the assignment was successful.</returns>
    public Task<bool> AssignTierAsync(
        string objectId,
        StorageTier targetTier,
        string? assignedBy = null,
        string? reason = null,
        int priority = 0,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);

        var assignment = new TierAssignment
        {
            ObjectId = objectId,
            TargetTier = targetTier,
            AssignedBy = assignedBy,
            AssignedAt = DateTime.UtcNow,
            Reason = reason,
            Priority = priority
        };

        var added = _manualAssignments.AddOrUpdate(objectId, assignment, (_, existing) =>
        {
            // Only override if new priority is higher or equal
            return priority >= existing.Priority ? assignment : existing;
        });

        var success = added == assignment;

        if (success)
        {
            Interlocked.Increment(ref _totalManualAssignments);
            AddAuditEntry("AssignTier", objectId, null, targetTier, assignedBy, reason, true);
        }

        return Task.FromResult(success);
    }

    /// <summary>
    /// Assigns multiple objects to tiers in a bulk operation.
    /// </summary>
    /// <param name="assignments">The tier assignments to perform.</param>
    /// <param name="assignedBy">The administrator performing the assignments.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of successful assignments.</returns>
    public Task<int> BulkAssignAsync(
        IEnumerable<(string ObjectId, StorageTier TargetTier, string? Reason)> assignments,
        string? assignedBy = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(assignments);

        var successCount = 0;

        foreach (var (objectId, targetTier, reason) in assignments)
        {
            ct.ThrowIfCancellationRequested();

            var assignment = new TierAssignment
            {
                ObjectId = objectId,
                TargetTier = targetTier,
                AssignedBy = assignedBy,
                AssignedAt = DateTime.UtcNow,
                Reason = reason,
                Priority = 0
            };

            _manualAssignments[objectId] = assignment;
            successCount++;
            AddAuditEntry("BulkAssign", objectId, null, targetTier, assignedBy, reason, true);
        }

        Interlocked.Increment(ref _totalBulkOperations);
        Interlocked.Add(ref _totalManualAssignments, successCount);

        return Task.FromResult(successCount);
    }

    /// <summary>
    /// Removes a manual tier assignment, allowing automatic tiering to resume.
    /// </summary>
    /// <param name="objectId">The object identifier.</param>
    /// <param name="removedBy">The administrator removing the assignment.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the assignment was removed.</returns>
    public Task<bool> RemoveAssignmentAsync(string objectId, string? removedBy = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);

        var removed = _manualAssignments.TryRemove(objectId, out var assignment);

        if (removed)
        {
            AddAuditEntry("RemoveAssignment", objectId, assignment?.TargetTier, null, removedBy, "Manual assignment removed", true);
        }

        return Task.FromResult(removed);
    }

    /// <summary>
    /// Locks an object to its current tier, preventing automatic tier changes.
    /// </summary>
    /// <param name="objectId">The object identifier.</param>
    /// <param name="lockedTier">The tier to lock the object to.</param>
    /// <param name="duration">How long to lock the tier.</param>
    /// <param name="lockedBy">The administrator locking the tier.</param>
    /// <param name="reason">Optional reason for the lock.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the lock was successful.</returns>
    public Task<bool> LockTierAsync(
        string objectId,
        StorageTier lockedTier,
        TimeSpan duration,
        string? lockedBy = null,
        string? reason = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);

        var tierLock = new TierLock
        {
            ObjectId = objectId,
            LockedTier = lockedTier,
            LockedUntil = DateTime.UtcNow.Add(duration),
            LockedBy = lockedBy,
            Reason = reason
        };

        _tierLocks[objectId] = tierLock;
        AddAuditEntry("LockTier", objectId, null, lockedTier, lockedBy, $"Locked until {tierLock.LockedUntil:u}: {reason}", true);

        return Task.FromResult(true);
    }

    /// <summary>
    /// Unlocks an object, allowing automatic tier changes.
    /// </summary>
    /// <param name="objectId">The object identifier.</param>
    /// <param name="unlockedBy">The administrator unlocking the tier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the unlock was successful.</returns>
    public Task<bool> UnlockTierAsync(string objectId, string? unlockedBy = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);

        var removed = _tierLocks.TryRemove(objectId, out var tierLock);

        if (removed)
        {
            AddAuditEntry("UnlockTier", objectId, tierLock?.LockedTier, null, unlockedBy, "Tier lock removed", true);
        }

        return Task.FromResult(removed);
    }

    /// <summary>
    /// Checks if an object has a tier lock.
    /// </summary>
    /// <param name="objectId">The object identifier.</param>
    /// <returns>True if the object is locked.</returns>
    public bool IsTierLocked(string objectId)
    {
        if (_tierLocks.TryGetValue(objectId, out var tierLock))
        {
            if (tierLock.LockedUntil > DateTime.UtcNow)
            {
                return true;
            }

            // Lock expired, remove it
            _tierLocks.TryRemove(objectId, out _);
        }

        return false;
    }

    /// <summary>
    /// Gets the manual assignment for an object.
    /// </summary>
    /// <param name="objectId">The object identifier.</param>
    /// <returns>The assigned tier if manually assigned, null otherwise.</returns>
    public StorageTier? GetManualAssignment(string objectId)
    {
        return _manualAssignments.TryGetValue(objectId, out var assignment) ? assignment.TargetTier : null;
    }

    /// <summary>
    /// Gets the audit log entries.
    /// </summary>
    /// <param name="limit">Maximum number of entries to return.</param>
    /// <returns>Recent audit log entries.</returns>
    public IReadOnlyList<(DateTime Timestamp, string Operation, string ObjectId, string? Details)> GetAuditLog(int limit = 100)
    {
        return _auditLog
            .OrderByDescending(e => e.Timestamp)
            .Take(limit)
            .Select(e => (e.Timestamp, e.Operation, e.ObjectId,
                (string?)$"{e.FromTier?.ToString() ?? "N/A"} -> {e.ToTier?.ToString() ?? "N/A"} by {e.PerformedBy ?? "system"}: {e.Reason}"))
            .ToList();
    }

    /// <inheritdoc/>
    protected override Task<TierRecommendation> EvaluateCoreAsync(DataObject data, CancellationToken ct)
    {
        // Check for tier lock first
        if (_tierLocks.TryGetValue(data.ObjectId, out var tierLock))
        {
            if (tierLock.LockedUntil > DateTime.UtcNow)
            {
                if (data.CurrentTier != tierLock.LockedTier)
                {
                    // Object should be at locked tier
                    return Task.FromResult(Demote(data, tierLock.LockedTier,
                        $"Tier locked to {tierLock.LockedTier} by {tierLock.LockedBy ?? "admin"}", 1.0, 1.0));
                }

                return Task.FromResult(NoChange(data, $"Tier locked to {tierLock.LockedTier}"));
            }

            // Lock expired, remove it
            _tierLocks.TryRemove(data.ObjectId, out _);
        }

        // Check for manual assignment
        if (_manualAssignments.TryGetValue(data.ObjectId, out var assignment))
        {
            if (data.CurrentTier != assignment.TargetTier)
            {
                var reason = $"Manual assignment to {assignment.TargetTier} by {assignment.AssignedBy ?? "admin"}";
                if (!string.IsNullOrEmpty(assignment.Reason))
                {
                    reason += $": {assignment.Reason}";
                }

                return Task.FromResult(assignment.TargetTier < data.CurrentTier
                    ? Promote(data, assignment.TargetTier, reason, 1.0, 1.0)
                    : Demote(data, assignment.TargetTier, reason, 1.0, 1.0));
            }

            return Task.FromResult(NoChange(data, "At manually assigned tier"));
        }

        // No manual assignment - recommend current tier
        return Task.FromResult(NoChange(data, "No manual assignment; use automatic tiering"));
    }

    private void AddAuditEntry(string operation, string objectId, StorageTier? fromTier, StorageTier? toTier,
        string? performedBy, string? reason, bool success)
    {
        var entry = new TierAuditEntry
        {
            Timestamp = DateTime.UtcNow,
            Operation = operation,
            ObjectId = objectId,
            FromTier = fromTier,
            ToTier = toTier,
            PerformedBy = performedBy,
            Reason = reason,
            Success = success
        };

        _auditLog.Enqueue(entry);

        // Trim audit log if too large
        while (_auditLog.Count > _maxAuditEntries)
        {
            _auditLog.TryDequeue(out _);
        }
    }
}
