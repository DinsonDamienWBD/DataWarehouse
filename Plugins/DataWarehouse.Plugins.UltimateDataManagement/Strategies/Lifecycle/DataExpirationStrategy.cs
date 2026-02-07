using System.Collections.Concurrent;
using System.Diagnostics;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.Lifecycle;

/// <summary>
/// Expiration event type.
/// </summary>
public enum ExpirationEventType
{
    /// <summary>
    /// Object is about to expire (warning).
    /// </summary>
    ExpirationWarning,

    /// <summary>
    /// Object has expired.
    /// </summary>
    Expired,

    /// <summary>
    /// Object entered grace period.
    /// </summary>
    GracePeriodStarted,

    /// <summary>
    /// Grace period ended.
    /// </summary>
    GracePeriodEnded,

    /// <summary>
    /// Object was soft deleted.
    /// </summary>
    SoftDeleted,

    /// <summary>
    /// Object was hard deleted.
    /// </summary>
    HardDeleted,

    /// <summary>
    /// Expiration was extended.
    /// </summary>
    ExpirationExtended,

    /// <summary>
    /// Expiration was cancelled.
    /// </summary>
    ExpirationCancelled
}

/// <summary>
/// Expiration notification event.
/// </summary>
public sealed class ExpirationEvent
{
    /// <summary>
    /// Event ID.
    /// </summary>
    public required string EventId { get; init; }

    /// <summary>
    /// Object ID.
    /// </summary>
    public required string ObjectId { get; init; }

    /// <summary>
    /// Event type.
    /// </summary>
    public required ExpirationEventType EventType { get; init; }

    /// <summary>
    /// When the event occurred.
    /// </summary>
    public DateTime OccurredAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Original expiration date.
    /// </summary>
    public DateTime? OriginalExpirationDate { get; init; }

    /// <summary>
    /// New expiration date (if extended).
    /// </summary>
    public DateTime? NewExpirationDate { get; init; }

    /// <summary>
    /// Days until expiration (for warnings).
    /// </summary>
    public int? DaysUntilExpiration { get; init; }

    /// <summary>
    /// Additional event data.
    /// </summary>
    public Dictionary<string, object>? Data { get; init; }
}

/// <summary>
/// Expiration policy configuration.
/// </summary>
public sealed class ExpirationPolicy
{
    /// <summary>
    /// Policy ID.
    /// </summary>
    public required string PolicyId { get; init; }

    /// <summary>
    /// Policy name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Default TTL for objects.
    /// </summary>
    public required TimeSpan DefaultTtl { get; init; }

    /// <summary>
    /// Grace period after expiration.
    /// </summary>
    public TimeSpan GracePeriod { get; init; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Whether to soft delete before hard delete.
    /// </summary>
    public bool SoftDeleteFirst { get; init; } = true;

    /// <summary>
    /// Warning thresholds (days before expiration).
    /// </summary>
    public int[] WarningThresholds { get; init; } = new[] { 30, 7, 1 };

    /// <summary>
    /// Maximum extensions allowed.
    /// </summary>
    public int MaxExtensions { get; init; } = 3;

    /// <summary>
    /// Extension duration.
    /// </summary>
    public TimeSpan ExtensionDuration { get; init; } = TimeSpan.FromDays(30);

    /// <summary>
    /// Scope for the policy.
    /// </summary>
    public PolicyScope? Scope { get; init; }

    /// <summary>
    /// Whether the policy is enabled.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Batch size for bulk expiration.
    /// </summary>
    public int BatchSize { get; init; } = 100;

    /// <summary>
    /// Notification channels.
    /// </summary>
    public List<string>? NotificationChannels { get; init; }
}

/// <summary>
/// Expiration tracking for an object.
/// </summary>
public sealed class ExpirationTracker
{
    /// <summary>
    /// Object ID.
    /// </summary>
    public required string ObjectId { get; init; }

    /// <summary>
    /// Expiration date.
    /// </summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// Original expiration date.
    /// </summary>
    public DateTime OriginalExpiresAt { get; init; }

    /// <summary>
    /// Number of extensions granted.
    /// </summary>
    public int ExtensionsGranted { get; set; }

    /// <summary>
    /// Whether in grace period.
    /// </summary>
    public bool InGracePeriod { get; set; }

    /// <summary>
    /// When grace period started.
    /// </summary>
    public DateTime? GracePeriodStartedAt { get; set; }

    /// <summary>
    /// Whether soft deleted.
    /// </summary>
    public bool IsSoftDeleted { get; set; }

    /// <summary>
    /// When soft deleted.
    /// </summary>
    public DateTime? SoftDeletedAt { get; set; }

    /// <summary>
    /// Warnings sent (days threshold).
    /// </summary>
    public HashSet<int> WarningsSent { get; init; } = new();

    /// <summary>
    /// Policy ID governing expiration.
    /// </summary>
    public string? PolicyId { get; init; }

    /// <summary>
    /// Whether the object is expired.
    /// </summary>
    public bool IsExpired => DateTime.UtcNow > ExpiresAt;

    /// <summary>
    /// Time until expiration.
    /// </summary>
    public TimeSpan? TimeUntilExpiration =>
        IsExpired ? null : ExpiresAt - DateTime.UtcNow;

    /// <summary>
    /// Days until expiration.
    /// </summary>
    public int? DaysUntilExpiration =>
        IsExpired ? null : (int)Math.Ceiling((ExpiresAt - DateTime.UtcNow).TotalDays);
}

/// <summary>
/// Result of expiration processing.
/// </summary>
public sealed class ExpirationResult
{
    /// <summary>
    /// Object ID.
    /// </summary>
    public required string ObjectId { get; init; }

    /// <summary>
    /// Action taken.
    /// </summary>
    public required ExpirationEventType ActionTaken { get; init; }

    /// <summary>
    /// Whether action succeeded.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// Error message if failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Next expiration date if applicable.
    /// </summary>
    public DateTime? NextExpirationDate { get; init; }
}

/// <summary>
/// Bulk expiration processing result.
/// </summary>
public sealed class BulkExpirationResult
{
    /// <summary>
    /// Total objects processed.
    /// </summary>
    public int TotalProcessed { get; set; }

    /// <summary>
    /// Objects soft deleted.
    /// </summary>
    public int SoftDeleted { get; set; }

    /// <summary>
    /// Objects hard deleted.
    /// </summary>
    public int HardDeleted { get; set; }

    /// <summary>
    /// Objects that entered grace period.
    /// </summary>
    public int EnteredGracePeriod { get; set; }

    /// <summary>
    /// Warnings sent.
    /// </summary>
    public int WarningsSent { get; set; }

    /// <summary>
    /// Failures.
    /// </summary>
    public int Failures { get; set; }

    /// <summary>
    /// Skipped (on hold, etc.).
    /// </summary>
    public int Skipped { get; set; }

    /// <summary>
    /// Processing duration.
    /// </summary>
    public TimeSpan Duration { get; set; }

    /// <summary>
    /// Individual results.
    /// </summary>
    public List<ExpirationResult> Results { get; init; } = new();
}

/// <summary>
/// Data expiration strategy for TTL-based auto-expiration.
/// Features grace periods, soft delete, expiration notifications, and bulk processing.
/// </summary>
public sealed class DataExpirationStrategy : LifecycleStrategyBase
{
    private readonly ConcurrentDictionary<string, ExpirationPolicy> _policies = new();
    private readonly ConcurrentDictionary<string, ExpirationTracker> _trackers = new();
    private readonly ConcurrentQueue<ExpirationEvent> _eventQueue = new();
    private readonly List<Action<ExpirationEvent>> _eventHandlers = new();
    private readonly SemaphoreSlim _processLock = new(1, 1);
    private CancellationTokenSource? _processorCts;
    private Task? _processorTask;
    private long _totalExpired;
    private long _totalSoftDeleted;
    private long _totalHardDeleted;

    /// <summary>
    /// Default expiration policy.
    /// </summary>
    public ExpirationPolicy? DefaultPolicy { get; set; }

    /// <summary>
    /// How often to run expiration processing.
    /// </summary>
    public TimeSpan ProcessingInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <inheritdoc/>
    public override string StrategyId => "data-expiration";

    /// <inheritdoc/>
    public override string DisplayName => "Data Expiration Strategy";

    /// <inheritdoc/>
    public override DataManagementCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = true,
        SupportsTransactions = false,
        SupportsTTL = true,
        MaxThroughput = 1000,
        TypicalLatencyMs = 10.0
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "TTL-based auto-expiration strategy with grace period handling. " +
        "Features expiration notifications, soft delete before hard delete, and bulk expiration processing.";

    /// <inheritdoc/>
    public override string[] Tags => new[]
    {
        "lifecycle", "expiration", "ttl", "auto-delete", "grace-period", "notifications", "bulk"
    };

    /// <inheritdoc/>
    protected override Task InitializeCoreAsync(CancellationToken ct)
    {
        _processorCts = new CancellationTokenSource();
        _processorTask = RunExpirationProcessorAsync(_processorCts.Token);

        // Initialize default policy
        DefaultPolicy ??= new ExpirationPolicy
        {
            PolicyId = "default",
            Name = "Default Expiration Policy",
            DefaultTtl = TimeSpan.FromDays(365),
            GracePeriod = TimeSpan.FromDays(7),
            SoftDeleteFirst = true,
            WarningThresholds = new[] { 30, 7, 1 }
        };

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override async Task DisposeCoreAsync()
    {
        if (_processorCts != null)
        {
            await _processorCts.CancelAsync();
            _processorCts.Dispose();
        }

        if (_processorTask != null)
        {
            try
            {
                await _processorTask;
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
        }

        _processLock.Dispose();
    }

    /// <inheritdoc/>
    protected override async Task<LifecycleDecision> EvaluateCoreAsync(LifecycleDataObject data, CancellationToken ct)
    {
        // Get or create tracker
        if (!_trackers.TryGetValue(data.ObjectId, out var tracker))
        {
            if (data.ExpiresAt.HasValue)
            {
                tracker = new ExpirationTracker
                {
                    ObjectId = data.ObjectId,
                    ExpiresAt = data.ExpiresAt.Value,
                    OriginalExpiresAt = data.ExpiresAt.Value
                };
                _trackers[data.ObjectId] = tracker;
            }
            else
            {
                return LifecycleDecision.NoAction("No expiration date set");
            }
        }

        // Check expiration state
        if (tracker.IsSoftDeleted)
        {
            var policy = GetPolicyForObject(data);
            var gracePeriodEnd = tracker.SoftDeletedAt!.Value + (policy?.GracePeriod ?? TimeSpan.FromDays(7));

            if (DateTime.UtcNow > gracePeriodEnd)
            {
                return LifecycleDecision.Delete(
                    "Grace period ended - ready for hard delete",
                    priority: 0.9);
            }

            return LifecycleDecision.NoAction(
                $"In grace period until {gracePeriodEnd:g}",
                gracePeriodEnd);
        }

        if (tracker.IsExpired)
        {
            var policy = GetPolicyForObject(data);
            if (policy?.SoftDeleteFirst == true)
            {
                return LifecycleDecision.Expire(
                    "Object expired - initiating soft delete",
                    softDelete: true);
            }

            return LifecycleDecision.Delete(
                "Object expired - ready for deletion",
                priority: 0.8);
        }

        // Check for warnings
        var daysUntil = tracker.DaysUntilExpiration;
        if (daysUntil.HasValue)
        {
            var policy = GetPolicyForObject(data);
            if (policy != null)
            {
                foreach (var threshold in policy.WarningThresholds.OrderByDescending(t => t))
                {
                    if (daysUntil.Value <= threshold && !tracker.WarningsSent.Contains(threshold))
                    {
                        return new LifecycleDecision
                        {
                            Action = LifecycleAction.Notify,
                            Reason = $"Expiration warning: {daysUntil.Value} days remaining",
                            Parameters = new Dictionary<string, object>
                            {
                                ["WarningThreshold"] = threshold,
                                ["DaysRemaining"] = daysUntil.Value,
                                ["ExpiresAt"] = tracker.ExpiresAt
                            }
                        };
                    }
                }
            }
        }

        return LifecycleDecision.NoAction(
            $"Not expired - expires in {daysUntil} days",
            tracker.ExpiresAt.AddDays(-1));
    }

    /// <summary>
    /// Sets expiration for an object.
    /// </summary>
    /// <param name="objectId">Object ID.</param>
    /// <param name="expiresAt">Expiration date.</param>
    /// <param name="policyId">Policy ID to apply.</param>
    public void SetExpiration(string objectId, DateTime expiresAt, string? policyId = null)
    {
        var tracker = new ExpirationTracker
        {
            ObjectId = objectId,
            ExpiresAt = expiresAt,
            OriginalExpiresAt = expiresAt,
            PolicyId = policyId ?? DefaultPolicy?.PolicyId
        };

        _trackers[objectId] = tracker;

        // Update tracked object
        if (TrackedObjects.TryGetValue(objectId, out var obj))
        {
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
                StorageTier = obj.StorageTier,
                ExpiresAt = expiresAt,
                Metadata = obj.Metadata
            };
            TrackedObjects[objectId] = updated;
        }
    }

    /// <summary>
    /// Sets TTL for an object.
    /// </summary>
    /// <param name="objectId">Object ID.</param>
    /// <param name="ttl">Time to live.</param>
    /// <param name="policyId">Policy ID to apply.</param>
    public void SetTtl(string objectId, TimeSpan ttl, string? policyId = null)
    {
        SetExpiration(objectId, DateTime.UtcNow.Add(ttl), policyId);
    }

    /// <summary>
    /// Extends expiration for an object.
    /// </summary>
    /// <param name="objectId">Object ID.</param>
    /// <param name="extension">Extension duration (null = use policy default).</param>
    /// <returns>True if extended.</returns>
    public bool ExtendExpiration(string objectId, TimeSpan? extension = null)
    {
        if (!_trackers.TryGetValue(objectId, out var tracker))
        {
            return false;
        }

        var policy = _policies.TryGetValue(tracker.PolicyId ?? "", out var p) ? p : DefaultPolicy;

        // Check extension limit
        if (policy != null && tracker.ExtensionsGranted >= policy.MaxExtensions)
        {
            return false;
        }

        var extensionDuration = extension ?? policy?.ExtensionDuration ?? TimeSpan.FromDays(30);
        var newExpiration = (tracker.IsExpired ? DateTime.UtcNow : tracker.ExpiresAt).Add(extensionDuration);

        tracker.ExpiresAt = newExpiration;
        tracker.ExtensionsGranted++;
        tracker.InGracePeriod = false;
        tracker.IsSoftDeleted = false;
        tracker.SoftDeletedAt = null;
        tracker.GracePeriodStartedAt = null;

        // Raise event
        RaiseEvent(new ExpirationEvent
        {
            EventId = Guid.NewGuid().ToString(),
            ObjectId = objectId,
            EventType = ExpirationEventType.ExpirationExtended,
            OriginalExpirationDate = tracker.OriginalExpiresAt,
            NewExpirationDate = newExpiration
        });

        // Update tracked object
        if (TrackedObjects.TryGetValue(objectId, out var obj))
        {
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
                StorageTier = obj.StorageTier,
                ExpiresAt = newExpiration,
                IsSoftDeleted = false,
                SoftDeletedAt = null,
                Metadata = obj.Metadata
            };
            TrackedObjects[objectId] = updated;
        }

        return true;
    }

    /// <summary>
    /// Cancels expiration for an object.
    /// </summary>
    /// <param name="objectId">Object ID.</param>
    /// <returns>True if cancelled.</returns>
    public bool CancelExpiration(string objectId)
    {
        if (!_trackers.TryRemove(objectId, out var tracker))
        {
            return false;
        }

        RaiseEvent(new ExpirationEvent
        {
            EventId = Guid.NewGuid().ToString(),
            ObjectId = objectId,
            EventType = ExpirationEventType.ExpirationCancelled,
            OriginalExpirationDate = tracker.OriginalExpiresAt
        });

        // Update tracked object
        if (TrackedObjects.TryGetValue(objectId, out var obj))
        {
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
                StorageTier = obj.StorageTier,
                ExpiresAt = null,
                IsSoftDeleted = false,
                Metadata = obj.Metadata
            };
            TrackedObjects[objectId] = updated;
        }

        return true;
    }

    /// <summary>
    /// Processes all expired objects.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Bulk expiration result.</returns>
    public async Task<BulkExpirationResult> ProcessExpiredObjectsAsync(CancellationToken ct = default)
    {
        await _processLock.WaitAsync(ct);
        try
        {
            var sw = Stopwatch.StartNew();
            var result = new BulkExpirationResult();

            var expiredTrackers = _trackers.Values
                .Where(t => t.IsExpired || ShouldProcess(t))
                .OrderBy(t => t.ExpiresAt)
                .ToList();

            foreach (var tracker in expiredTrackers)
            {
                ct.ThrowIfCancellationRequested();

                // Skip if on hold
                if (TrackedObjects.TryGetValue(tracker.ObjectId, out var obj) && obj.IsOnHold)
                {
                    result.Skipped++;
                    continue;
                }

                var itemResult = await ProcessSingleExpirationAsync(tracker, ct);
                result.Results.Add(itemResult);
                result.TotalProcessed++;

                switch (itemResult.ActionTaken)
                {
                    case ExpirationEventType.SoftDeleted:
                        result.SoftDeleted++;
                        Interlocked.Increment(ref _totalSoftDeleted);
                        break;
                    case ExpirationEventType.HardDeleted:
                        result.HardDeleted++;
                        Interlocked.Increment(ref _totalHardDeleted);
                        Interlocked.Increment(ref _totalExpired);
                        break;
                    case ExpirationEventType.GracePeriodStarted:
                        result.EnteredGracePeriod++;
                        break;
                    case ExpirationEventType.ExpirationWarning:
                        result.WarningsSent++;
                        break;
                }

                if (!itemResult.Success)
                {
                    result.Failures++;
                }
            }

            sw.Stop();
            result.Duration = sw.Elapsed;

            return result;
        }
        finally
        {
            _processLock.Release();
        }
    }

    /// <summary>
    /// Registers an expiration policy.
    /// </summary>
    /// <param name="policy">Policy to register.</param>
    public void RegisterPolicy(ExpirationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        _policies[policy.PolicyId] = policy;
    }

    /// <summary>
    /// Subscribes to expiration events.
    /// </summary>
    /// <param name="handler">Event handler.</param>
    public void SubscribeToEvents(Action<ExpirationEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _eventHandlers.Add(handler);
    }

    /// <summary>
    /// Gets expiration tracker for an object.
    /// </summary>
    /// <param name="objectId">Object ID.</param>
    /// <returns>Tracker or null.</returns>
    public ExpirationTracker? GetTracker(string objectId)
    {
        return _trackers.TryGetValue(objectId, out var tracker) ? tracker : null;
    }

    /// <summary>
    /// Gets all objects expiring within a time period.
    /// </summary>
    /// <param name="within">Time period.</param>
    /// <returns>Collection of trackers.</returns>
    public IEnumerable<ExpirationTracker> GetExpiringWithin(TimeSpan within)
    {
        var threshold = DateTime.UtcNow.Add(within);
        return _trackers.Values
            .Where(t => !t.IsExpired && t.ExpiresAt <= threshold)
            .OrderBy(t => t.ExpiresAt);
    }

    /// <summary>
    /// Gets all expired objects.
    /// </summary>
    /// <returns>Collection of trackers.</returns>
    public IEnumerable<ExpirationTracker> GetExpiredObjects()
    {
        return _trackers.Values.Where(t => t.IsExpired).OrderBy(t => t.ExpiresAt);
    }

    /// <summary>
    /// Gets expiration statistics.
    /// </summary>
    /// <returns>Statistics dictionary.</returns>
    public Dictionary<string, object> GetExpirationStats()
    {
        var now = DateTime.UtcNow;
        return new Dictionary<string, object>
        {
            ["TotalTracked"] = _trackers.Count,
            ["TotalExpired"] = Interlocked.Read(ref _totalExpired),
            ["TotalSoftDeleted"] = Interlocked.Read(ref _totalSoftDeleted),
            ["TotalHardDeleted"] = Interlocked.Read(ref _totalHardDeleted),
            ["CurrentlyExpired"] = _trackers.Values.Count(t => t.IsExpired),
            ["InGracePeriod"] = _trackers.Values.Count(t => t.InGracePeriod),
            ["ExpiringIn24Hours"] = _trackers.Values.Count(t => !t.IsExpired && t.ExpiresAt <= now.AddHours(24)),
            ["ExpiringIn7Days"] = _trackers.Values.Count(t => !t.IsExpired && t.ExpiresAt <= now.AddDays(7)),
            ["ExpiringIn30Days"] = _trackers.Values.Count(t => !t.IsExpired && t.ExpiresAt <= now.AddDays(30)),
            ["PendingEvents"] = _eventQueue.Count
        };
    }

    private async Task RunExpirationProcessorAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(ProcessingInterval, ct);
                await ProcessExpiredObjectsAsync(ct);

                // Process event queue
                while (_eventQueue.TryDequeue(out var evt))
                {
                    foreach (var handler in _eventHandlers)
                    {
                        try
                        {
                            handler(evt);
                        }
                        catch
                        {
                            // Log and continue
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Log and continue
            }
        }
    }

    private async Task<ExpirationResult> ProcessSingleExpirationAsync(
        ExpirationTracker tracker,
        CancellationToken ct)
    {
        var policy = GetPolicyForTracker(tracker);

        try
        {
            // Check for warnings first
            if (!tracker.IsExpired)
            {
                var daysUntil = tracker.DaysUntilExpiration ?? 0;
                foreach (var threshold in policy?.WarningThresholds ?? Array.Empty<int>())
                {
                    if (daysUntil <= threshold && !tracker.WarningsSent.Contains(threshold))
                    {
                        tracker.WarningsSent.Add(threshold);

                        RaiseEvent(new ExpirationEvent
                        {
                            EventId = Guid.NewGuid().ToString(),
                            ObjectId = tracker.ObjectId,
                            EventType = ExpirationEventType.ExpirationWarning,
                            DaysUntilExpiration = daysUntil,
                            OriginalExpirationDate = tracker.ExpiresAt
                        });

                        return new ExpirationResult
                        {
                            ObjectId = tracker.ObjectId,
                            ActionTaken = ExpirationEventType.ExpirationWarning,
                            Success = true,
                            NextExpirationDate = tracker.ExpiresAt
                        };
                    }
                }

                return new ExpirationResult
                {
                    ObjectId = tracker.ObjectId,
                    ActionTaken = ExpirationEventType.ExpirationWarning,
                    Success = true,
                    NextExpirationDate = tracker.ExpiresAt
                };
            }

            // Object is expired
            if (!tracker.IsSoftDeleted && (policy?.SoftDeleteFirst ?? true))
            {
                // Soft delete
                tracker.IsSoftDeleted = true;
                tracker.SoftDeletedAt = DateTime.UtcNow;
                tracker.InGracePeriod = true;
                tracker.GracePeriodStartedAt = DateTime.UtcNow;

                // Update tracked object
                if (TrackedObjects.TryGetValue(tracker.ObjectId, out var obj))
                {
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
                        StorageTier = obj.StorageTier,
                        ExpiresAt = obj.ExpiresAt,
                        IsSoftDeleted = true,
                        SoftDeletedAt = DateTime.UtcNow,
                        Metadata = obj.Metadata
                    };
                    TrackedObjects[tracker.ObjectId] = updated;
                }

                RaiseEvent(new ExpirationEvent
                {
                    EventId = Guid.NewGuid().ToString(),
                    ObjectId = tracker.ObjectId,
                    EventType = ExpirationEventType.SoftDeleted,
                    OriginalExpirationDate = tracker.ExpiresAt
                });

                return new ExpirationResult
                {
                    ObjectId = tracker.ObjectId,
                    ActionTaken = ExpirationEventType.SoftDeleted,
                    Success = true
                };
            }

            // Check if grace period ended
            if (tracker.InGracePeriod)
            {
                var gracePeriodEnd = tracker.SoftDeletedAt!.Value.Add(policy?.GracePeriod ?? TimeSpan.FromDays(7));

                if (DateTime.UtcNow < gracePeriodEnd)
                {
                    return new ExpirationResult
                    {
                        ObjectId = tracker.ObjectId,
                        ActionTaken = ExpirationEventType.GracePeriodStarted,
                        Success = true,
                        NextExpirationDate = gracePeriodEnd
                    };
                }

                tracker.InGracePeriod = false;

                RaiseEvent(new ExpirationEvent
                {
                    EventId = Guid.NewGuid().ToString(),
                    ObjectId = tracker.ObjectId,
                    EventType = ExpirationEventType.GracePeriodEnded,
                    OriginalExpirationDate = tracker.ExpiresAt
                });
            }

            // Hard delete
            TrackedObjects.TryRemove(tracker.ObjectId, out _);
            _trackers.TryRemove(tracker.ObjectId, out _);
            PendingActions.TryRemove(tracker.ObjectId, out _);

            RaiseEvent(new ExpirationEvent
            {
                EventId = Guid.NewGuid().ToString(),
                ObjectId = tracker.ObjectId,
                EventType = ExpirationEventType.HardDeleted,
                OriginalExpirationDate = tracker.ExpiresAt
            });

            return new ExpirationResult
            {
                ObjectId = tracker.ObjectId,
                ActionTaken = ExpirationEventType.HardDeleted,
                Success = true
            };
        }
        catch (Exception ex)
        {
            return new ExpirationResult
            {
                ObjectId = tracker.ObjectId,
                ActionTaken = ExpirationEventType.Expired,
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }

    private bool ShouldProcess(ExpirationTracker tracker)
    {
        if (tracker.IsExpired) return true;

        var policy = GetPolicyForTracker(tracker);
        if (policy == null) return false;

        var daysUntil = tracker.DaysUntilExpiration ?? int.MaxValue;
        return policy.WarningThresholds.Any(t => daysUntil <= t && !tracker.WarningsSent.Contains(t));
    }

    private ExpirationPolicy? GetPolicyForObject(LifecycleDataObject data)
    {
        if (_trackers.TryGetValue(data.ObjectId, out var tracker) &&
            !string.IsNullOrEmpty(tracker.PolicyId) &&
            _policies.TryGetValue(tracker.PolicyId, out var policy))
        {
            return policy;
        }

        return DefaultPolicy;
    }

    private ExpirationPolicy? GetPolicyForTracker(ExpirationTracker tracker)
    {
        if (!string.IsNullOrEmpty(tracker.PolicyId) &&
            _policies.TryGetValue(tracker.PolicyId, out var policy))
        {
            return policy;
        }

        return DefaultPolicy;
    }

    private void RaiseEvent(ExpirationEvent evt)
    {
        _eventQueue.Enqueue(evt);
    }
}
