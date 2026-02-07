using System.Collections.Concurrent;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.Retention;

/// <summary>
/// Litigation hold strategy for managing legal holds on data.
/// Prevents deletion of data under legal hold regardless of other retention policies.
/// </summary>
/// <remarks>
/// Features:
/// - Create and manage legal holds
/// - Apply holds to specific objects, paths, or custodians
/// - Hold release management
/// - Audit trail for hold operations
/// - Integration with e-discovery workflows
/// </remarks>
public sealed class LegalHoldStrategy : RetentionStrategyBase
{
    private readonly ConcurrentDictionary<string, LegalHold> _holds = new();
    private readonly ConcurrentDictionary<string, HashSet<string>> _objectHolds = new();
    private readonly ConcurrentDictionary<string, HashSet<string>> _custodianHolds = new();
    private readonly List<HoldAuditEntry> _auditLog = new();
    private readonly object _auditLock = new();

    /// <inheritdoc/>
    public override string StrategyId => "retention.legalhold";

    /// <inheritdoc/>
    public override string DisplayName => "Legal Hold";

    /// <inheritdoc/>
    public override DataManagementCapabilities Capabilities { get; } = new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = true,
        SupportsTransactions = true,
        SupportsTTL = false,
        MaxThroughput = 100_000,
        TypicalLatencyMs = 0.5
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Litigation hold management for legal preservation requirements. " +
        "Prevents deletion of held data regardless of other retention policies. " +
        "Supports custodian-based holds and provides full audit trail for compliance.";

    /// <inheritdoc/>
    public override string[] Tags => ["retention", "legal-hold", "litigation", "e-discovery", "preservation"];

    /// <summary>
    /// Gets the count of active legal holds.
    /// </summary>
    public int ActiveHoldCount => _holds.Values.Count(h => h.IsActive);

    /// <summary>
    /// Gets the total count of held objects.
    /// </summary>
    public int HeldObjectCount => _objectHolds.Count;

    /// <summary>
    /// Creates a new legal hold.
    /// </summary>
    /// <param name="holdId">Unique hold identifier.</param>
    /// <param name="matterName">Legal matter name.</param>
    /// <param name="description">Hold description.</param>
    /// <param name="createdBy">User who created the hold.</param>
    /// <returns>The created legal hold.</returns>
    public LegalHold CreateHold(string holdId, string matterName, string description, string createdBy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(holdId);
        ArgumentException.ThrowIfNullOrWhiteSpace(matterName);
        ArgumentException.ThrowIfNullOrWhiteSpace(createdBy);

        if (_holds.ContainsKey(holdId))
        {
            throw new InvalidOperationException($"Hold '{holdId}' already exists");
        }

        var hold = new LegalHold
        {
            HoldId = holdId,
            MatterName = matterName,
            Description = description,
            CreatedAt = DateTime.UtcNow,
            CreatedBy = createdBy,
            IsActive = true,
            HeldObjects = new List<string>(),
            Custodians = new List<string>()
        };

        _holds[holdId] = hold;
        LogHoldAction(holdId, HoldAction.Created, createdBy, $"Hold created for matter: {matterName}");

        return hold;
    }

    /// <summary>
    /// Applies a hold to specific objects.
    /// </summary>
    /// <param name="holdId">Hold identifier.</param>
    /// <param name="objectIds">Object IDs to hold.</param>
    /// <param name="appliedBy">User applying the hold.</param>
    /// <returns>Number of objects placed on hold.</returns>
    public int ApplyHoldToObjects(string holdId, IEnumerable<string> objectIds, string appliedBy)
    {
        if (!_holds.TryGetValue(holdId, out var hold) || !hold.IsActive)
        {
            throw new InvalidOperationException($"Hold '{holdId}' not found or not active");
        }

        var count = 0;
        foreach (var objectId in objectIds)
        {
            var holds = _objectHolds.GetOrAdd(objectId, _ => new HashSet<string>());
            lock (holds)
            {
                if (holds.Add(holdId))
                {
                    hold.HeldObjects.Add(objectId);
                    count++;
                }
            }
        }

        if (count > 0)
        {
            LogHoldAction(holdId, HoldAction.ObjectsAdded, appliedBy, $"Added {count} objects to hold");
        }

        return count;
    }

    /// <summary>
    /// Applies a hold to a custodian (all their data).
    /// </summary>
    /// <param name="holdId">Hold identifier.</param>
    /// <param name="custodianId">Custodian identifier.</param>
    /// <param name="appliedBy">User applying the hold.</param>
    public void ApplyHoldToCustodian(string holdId, string custodianId, string appliedBy)
    {
        if (!_holds.TryGetValue(holdId, out var hold) || !hold.IsActive)
        {
            throw new InvalidOperationException($"Hold '{holdId}' not found or not active");
        }

        var holds = _custodianHolds.GetOrAdd(custodianId, _ => new HashSet<string>());
        lock (holds)
        {
            if (holds.Add(holdId))
            {
                hold.Custodians.Add(custodianId);
                LogHoldAction(holdId, HoldAction.CustodianAdded, appliedBy, $"Custodian '{custodianId}' added to hold");
            }
        }
    }

    /// <summary>
    /// Releases a hold, allowing held data to be deleted.
    /// </summary>
    /// <param name="holdId">Hold identifier.</param>
    /// <param name="releasedBy">User releasing the hold.</param>
    /// <param name="reason">Reason for release.</param>
    public void ReleaseHold(string holdId, string releasedBy, string reason)
    {
        if (!_holds.TryGetValue(holdId, out var hold))
        {
            throw new InvalidOperationException($"Hold '{holdId}' not found");
        }

        hold.IsActive = false;
        hold.ReleasedAt = DateTime.UtcNow;
        hold.ReleasedBy = releasedBy;
        hold.ReleaseReason = reason;

        // Remove from object holds
        foreach (var objectId in hold.HeldObjects)
        {
            if (_objectHolds.TryGetValue(objectId, out var holds))
            {
                lock (holds)
                {
                    holds.Remove(holdId);
                    if (holds.Count == 0)
                    {
                        _objectHolds.TryRemove(objectId, out _);
                    }
                }
            }
        }

        // Remove from custodian holds
        foreach (var custodianId in hold.Custodians)
        {
            if (_custodianHolds.TryGetValue(custodianId, out var holds))
            {
                lock (holds)
                {
                    holds.Remove(holdId);
                    if (holds.Count == 0)
                    {
                        _custodianHolds.TryRemove(custodianId, out _);
                    }
                }
            }
        }

        LogHoldAction(holdId, HoldAction.Released, releasedBy, reason);
    }

    /// <summary>
    /// Checks if an object is under legal hold.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <returns>True if under hold.</returns>
    public bool IsObjectUnderHold(string objectId)
    {
        return _objectHolds.TryGetValue(objectId, out var holds) && holds.Count > 0;
    }

    /// <summary>
    /// Gets all active holds for an object.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <returns>List of hold IDs.</returns>
    public IReadOnlyList<string> GetObjectHolds(string objectId)
    {
        if (_objectHolds.TryGetValue(objectId, out var holds))
        {
            lock (holds)
            {
                return holds.ToList().AsReadOnly();
            }
        }
        return Array.Empty<string>();
    }

    /// <summary>
    /// Checks if a custodian is under hold.
    /// </summary>
    /// <param name="custodianId">Custodian identifier.</param>
    /// <returns>True if under hold.</returns>
    public bool IsCustodianUnderHold(string custodianId)
    {
        return _custodianHolds.TryGetValue(custodianId, out var holds) && holds.Count > 0;
    }

    /// <inheritdoc/>
    protected override Task<RetentionDecision> EvaluateCoreAsync(DataObject data, CancellationToken ct)
    {
        // Check if object is directly under hold
        if (_objectHolds.TryGetValue(data.ObjectId, out var objectHolds) && objectHolds.Count > 0)
        {
            var holdIds = string.Join(", ", objectHolds);
            return Task.FromResult(new RetentionDecision
            {
                Action = RetentionAction.LegalHold,
                Reason = $"Object under legal hold: {holdIds}",
                Metadata = new Dictionary<string, object> { ["HoldIds"] = objectHolds.ToArray() }
            });
        }

        // Check if custodian/owner is under hold
        var custodianId = GetCustodian(data);
        if (!string.IsNullOrEmpty(custodianId) &&
            _custodianHolds.TryGetValue(custodianId, out var custodianHolds) &&
            custodianHolds.Count > 0)
        {
            var holdIds = string.Join(", ", custodianHolds);
            return Task.FromResult(new RetentionDecision
            {
                Action = RetentionAction.LegalHold,
                Reason = $"Custodian '{custodianId}' under legal hold: {holdIds}",
                Metadata = new Dictionary<string, object>
                {
                    ["HoldIds"] = custodianHolds.ToArray(),
                    ["CustodianId"] = custodianId
                }
            });
        }

        // No holds apply
        return Task.FromResult(RetentionDecision.Retain(
            "No legal holds apply",
            DateTime.UtcNow.AddDays(1)));
    }

    private static string? GetCustodian(DataObject data)
    {
        if (data.Metadata?.TryGetValue("owner", out var owner) == true)
        {
            return owner?.ToString();
        }

        if (data.Metadata?.TryGetValue("custodian", out var custodian) == true)
        {
            return custodian?.ToString();
        }

        if (data.Metadata?.TryGetValue("createdBy", out var createdBy) == true)
        {
            return createdBy?.ToString();
        }

        return null;
    }

    private void LogHoldAction(string holdId, HoldAction action, string user, string details)
    {
        lock (_auditLock)
        {
            _auditLog.Add(new HoldAuditEntry
            {
                Timestamp = DateTime.UtcNow,
                HoldId = holdId,
                Action = action,
                PerformedBy = user,
                Details = details
            });

            while (_auditLog.Count > 10000)
            {
                _auditLog.RemoveAt(0);
            }
        }
    }

    /// <summary>
    /// Gets the audit log for hold operations.
    /// </summary>
    /// <param name="holdId">Optional hold ID to filter by.</param>
    /// <param name="limit">Maximum entries to return.</param>
    /// <returns>Audit entries.</returns>
    public IReadOnlyList<HoldAuditEntry> GetAuditLog(string? holdId = null, int limit = 100)
    {
        lock (_auditLock)
        {
            var entries = holdId != null
                ? _auditLog.Where(e => e.HoldId == holdId)
                : _auditLog;

            return entries.TakeLast(limit).ToList().AsReadOnly();
        }
    }

    /// <summary>
    /// Gets a legal hold by ID.
    /// </summary>
    /// <param name="holdId">Hold identifier.</param>
    /// <returns>Legal hold or null.</returns>
    public LegalHold? GetHold(string holdId)
    {
        return _holds.TryGetValue(holdId, out var hold) ? hold : null;
    }

    /// <summary>
    /// Gets all active legal holds.
    /// </summary>
    /// <returns>List of active holds.</returns>
    public IReadOnlyList<LegalHold> GetActiveHolds()
    {
        return _holds.Values.Where(h => h.IsActive).ToList().AsReadOnly();
    }
}

/// <summary>
/// Represents a legal hold.
/// </summary>
public sealed class LegalHold
{
    /// <summary>
    /// Unique hold identifier.
    /// </summary>
    public required string HoldId { get; init; }

    /// <summary>
    /// Legal matter name.
    /// </summary>
    public required string MatterName { get; init; }

    /// <summary>
    /// Hold description.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// When the hold was created.
    /// </summary>
    public required DateTime CreatedAt { get; init; }

    /// <summary>
    /// Who created the hold.
    /// </summary>
    public required string CreatedBy { get; init; }

    /// <summary>
    /// Whether the hold is currently active.
    /// </summary>
    public bool IsActive { get; set; }

    /// <summary>
    /// When the hold was released.
    /// </summary>
    public DateTime? ReleasedAt { get; set; }

    /// <summary>
    /// Who released the hold.
    /// </summary>
    public string? ReleasedBy { get; set; }

    /// <summary>
    /// Reason for release.
    /// </summary>
    public string? ReleaseReason { get; set; }

    /// <summary>
    /// Objects under this hold.
    /// </summary>
    public required List<string> HeldObjects { get; init; }

    /// <summary>
    /// Custodians under this hold.
    /// </summary>
    public required List<string> Custodians { get; init; }
}

/// <summary>
/// Actions that can be performed on a legal hold.
/// </summary>
public enum HoldAction
{
    /// <summary>
    /// Hold was created.
    /// </summary>
    Created,

    /// <summary>
    /// Objects were added to hold.
    /// </summary>
    ObjectsAdded,

    /// <summary>
    /// Custodian was added to hold.
    /// </summary>
    CustodianAdded,

    /// <summary>
    /// Objects were removed from hold.
    /// </summary>
    ObjectsRemoved,

    /// <summary>
    /// Hold was released.
    /// </summary>
    Released
}

/// <summary>
/// Audit entry for hold operations.
/// </summary>
public sealed class HoldAuditEntry
{
    /// <summary>
    /// When the action occurred.
    /// </summary>
    public required DateTime Timestamp { get; init; }

    /// <summary>
    /// Hold affected.
    /// </summary>
    public required string HoldId { get; init; }

    /// <summary>
    /// Action performed.
    /// </summary>
    public required HoldAction Action { get; init; }

    /// <summary>
    /// User who performed the action.
    /// </summary>
    public required string PerformedBy { get; init; }

    /// <summary>
    /// Additional details.
    /// </summary>
    public required string Details { get; init; }
}
