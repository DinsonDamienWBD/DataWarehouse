using DataWarehouse.SDK.Contracts.Storage;
using IStorageStrategy = DataWarehouse.SDK.Contracts.Storage.IStorageStrategy;
using StorageTier = DataWarehouse.SDK.Contracts.Storage.StorageTier;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateStorage.Features
{
    /// <summary>
    /// Lifecycle Management Feature (C4) - Unified lifecycle management across all storage backends.
    ///
    /// Features:
    /// - Retention policies per object/prefix/backend
    /// - Automatic expiration and deletion based on age
    /// - Transition rules (e.g., after 30 days move to cold storage)
    /// - Legal hold support (prevent deletion during legal proceedings)
    /// - WORM (Write Once Read Many) compliance integration
    /// - Policy inheritance (object inherits from prefix, prefix from global)
    /// - Audit trail for lifecycle events
    /// - Scheduled lifecycle evaluation
    /// </summary>
    public sealed class LifecycleManagementFeature : IDisposable
    {
        private readonly StrategyRegistry<IStorageStrategy> _registry;
        private readonly BoundedDictionary<string, LifecyclePolicy> _policies = new BoundedDictionary<string, LifecyclePolicy>(1000);
        private readonly BoundedDictionary<string, ObjectLifecycleState> _objectStates = new BoundedDictionary<string, ObjectLifecycleState>(1000);
        private readonly BoundedDictionary<string, LegalHold> _legalHolds = new BoundedDictionary<string, LegalHold>(1000);
        private readonly List<LifecycleEvent> _auditTrail = new();
        private readonly Timer _lifecycleTimer;
        private readonly object _auditLock = new();
        private bool _disposed;

        // Configuration
        private TimeSpan _evaluationInterval = TimeSpan.FromHours(6);
        private bool _enableLifecycleManagement = true;
        private int _maxAuditTrailSize = 10000;

        // Statistics
        private long _totalExpiredObjects;
        private long _totalDeletedObjects;
        private long _totalTransitions;
        private long _totalLegalHolds;

        /// <summary>
        /// Initializes a new instance of the LifecycleManagementFeature.
        /// </summary>
        /// <param name="registry">The storage strategy registry.</param>
        public LifecycleManagementFeature(StrategyRegistry<IStorageStrategy> registry)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));

            // Initialize default policies
            InitializeDefaultPolicies();

            // Start background lifecycle evaluation
            _lifecycleTimer = new Timer(
                callback: async _ => { try { await EvaluateLifecyclePoliciesAsync(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Timer callback failed: {ex.Message}"); } },
                state: null,
                dueTime: _evaluationInterval,
                period: _evaluationInterval);
        }

        /// <summary>
        /// Gets the total number of expired objects.
        /// </summary>
        public long TotalExpiredObjects => Interlocked.Read(ref _totalExpiredObjects);

        /// <summary>
        /// Gets the total number of deleted objects due to lifecycle policies.
        /// </summary>
        public long TotalDeletedObjects => Interlocked.Read(ref _totalDeletedObjects);

        /// <summary>
        /// Gets the total number of tier transitions.
        /// </summary>
        public long TotalTransitions => Interlocked.Read(ref _totalTransitions);

        /// <summary>
        /// Gets or sets whether lifecycle management is enabled.
        /// </summary>
        public bool EnableLifecycleManagement
        {
            get => _enableLifecycleManagement;
            set => _enableLifecycleManagement = value;
        }

        /// <summary>
        /// Adds or updates a lifecycle policy.
        /// </summary>
        /// <param name="policyId">Unique policy identifier.</param>
        /// <param name="policy">Lifecycle policy configuration.</param>
        public void SetLifecyclePolicy(string policyId, LifecyclePolicy policy)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(policyId);
            ArgumentNullException.ThrowIfNull(policy);

            policy.PolicyId = policyId;
            _policies[policyId] = policy;

            LogLifecycleEvent(new LifecycleEvent
            {
                EventType = LifecycleEventType.PolicyCreated,
                Timestamp = DateTime.UtcNow,
                Details = $"Policy '{policyId}' created"
            });
        }

        /// <summary>
        /// Gets a lifecycle policy by ID.
        /// </summary>
        /// <param name="policyId">Policy ID.</param>
        /// <returns>Lifecycle policy or null if not found.</returns>
        public LifecyclePolicy? GetLifecyclePolicy(string policyId)
        {
            return _policies.TryGetValue(policyId, out var policy) ? policy : null;
        }

        /// <summary>
        /// Gets all lifecycle policies.
        /// </summary>
        /// <returns>Dictionary of policy IDs to policies.</returns>
        public IReadOnlyDictionary<string, LifecyclePolicy> GetAllPolicies()
        {
            return _policies.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        }

        /// <summary>
        /// Removes a lifecycle policy.
        /// </summary>
        /// <param name="policyId">Policy ID to remove.</param>
        /// <returns>True if policy was removed, false if not found.</returns>
        public bool RemoveLifecyclePolicy(string policyId)
        {
            var removed = _policies.TryRemove(policyId, out _);
            if (removed)
            {
                LogLifecycleEvent(new LifecycleEvent
                {
                    EventType = LifecycleEventType.PolicyDeleted,
                    Timestamp = DateTime.UtcNow,
                    Details = $"Policy '{policyId}' deleted"
                });
            }
            return removed;
        }

        /// <summary>
        /// Places a legal hold on an object, preventing deletion.
        /// </summary>
        /// <param name="key">Object key.</param>
        /// <param name="reason">Reason for legal hold.</param>
        /// <param name="expiresAt">Optional expiration date for the hold.</param>
        public void PlaceLegalHold(string key, string reason, DateTime? expiresAt = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            ArgumentException.ThrowIfNullOrWhiteSpace(reason);

            var hold = new LegalHold
            {
                Key = key,
                Reason = reason,
                PlacedAt = DateTime.UtcNow,
                ExpiresAt = expiresAt
            };

            _legalHolds[key] = hold;
            Interlocked.Increment(ref _totalLegalHolds);

            LogLifecycleEvent(new LifecycleEvent
            {
                EventType = LifecycleEventType.LegalHoldPlaced,
                ObjectKey = key,
                Timestamp = DateTime.UtcNow,
                Details = $"Legal hold placed: {reason}"
            });
        }

        /// <summary>
        /// Removes a legal hold from an object.
        /// </summary>
        /// <param name="key">Object key.</param>
        /// <returns>True if hold was removed, false if not found.</returns>
        public bool RemoveLegalHold(string key)
        {
            var removed = _legalHolds.TryRemove(key, out _);
            if (removed)
            {
                LogLifecycleEvent(new LifecycleEvent
                {
                    EventType = LifecycleEventType.LegalHoldRemoved,
                    ObjectKey = key,
                    Timestamp = DateTime.UtcNow,
                    Details = "Legal hold removed"
                });
            }
            return removed;
        }

        /// <summary>
        /// Checks if an object has an active legal hold.
        /// </summary>
        /// <param name="key">Object key.</param>
        /// <returns>True if object has an active legal hold.</returns>
        public bool HasLegalHold(string key)
        {
            if (!_legalHolds.TryGetValue(key, out var hold))
            {
                return false;
            }

            // Check if hold has expired
            if (hold.ExpiresAt.HasValue && hold.ExpiresAt.Value < DateTime.UtcNow)
            {
                _legalHolds.TryRemove(key, out _);
                return false;
            }

            return true;
        }

        /// <summary>
        /// Tracks an object's lifecycle state.
        /// </summary>
        /// <param name="key">Object key.</param>
        /// <param name="backendId">Backend where object is stored.</param>
        /// <param name="createdAt">Object creation timestamp.</param>
        /// <param name="size">Object size in bytes.</param>
        public void TrackObject(string key, string backendId, DateTime createdAt, long size)
        {
            var state = _objectStates.GetOrAdd(key, _ => new ObjectLifecycleState
            {
                Key = key,
                BackendId = backendId,
                CreatedAt = createdAt,
                Size = size,
                LastAccessed = DateTime.UtcNow
            });

            state.BackendId = backendId;
            state.LastAccessed = DateTime.UtcNow;
        }

        /// <summary>
        /// Gets lifecycle state for an object.
        /// </summary>
        /// <param name="key">Object key.</param>
        /// <returns>Lifecycle state or null if not tracked.</returns>
        public ObjectLifecycleState? GetObjectState(string key)
        {
            return _objectStates.TryGetValue(key, out var state) ? state : null;
        }

        /// <summary>
        /// Gets recent lifecycle events from the audit trail.
        /// </summary>
        /// <param name="count">Number of events to retrieve.</param>
        /// <returns>List of recent lifecycle events.</returns>
        public IReadOnlyList<LifecycleEvent> GetAuditTrail(int count = 100)
        {
            lock (_auditLock)
            {
                return _auditTrail.TakeLast(count).ToList();
            }
        }

        /// <summary>
        /// Manually evaluates lifecycle policies for all tracked objects.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Number of actions taken.</returns>
        public async Task<int> EvaluateLifecyclePoliciesAsync(CancellationToken ct = default)
        {
            if (!_enableLifecycleManagement || _disposed)
            {
                return 0;
            }

            var actionsTaken = 0;

            foreach (var state in _objectStates.Values.ToList())
            {
                ct.ThrowIfCancellationRequested();

                // Skip objects with legal holds
                if (HasLegalHold(state.Key))
                {
                    continue;
                }

                // Find applicable policies
                var applicablePolicies = FindApplicablePolicies(state);

                foreach (var policy in applicablePolicies)
                {
                    if (await EvaluatePolicyForObjectAsync(state, policy, ct))
                    {
                        actionsTaken++;
                    }
                }
            }

            return actionsTaken;
        }

        #region Private Methods

        private void InitializeDefaultPolicies()
        {
            // Default policy: Delete objects older than 1 year
            SetLifecyclePolicy("DeleteOldObjects", new LifecyclePolicy
            {
                PolicyId = "DeleteOldObjects",
                Name = "Delete Old Objects",
                Enabled = false, // Disabled by default for safety
                Scope = PolicyScope.Global,
                Rules = new List<LifecycleRule>
                {
                    new()
                    {
                        Type = RuleType.Expiration,
                        AgeDays = 365,
                        Action = LifecycleAction.Delete
                    }
                }
            });

            // Default policy: Transition to cold storage after 90 days
            SetLifecyclePolicy("TransitionToCold", new LifecyclePolicy
            {
                PolicyId = "TransitionToCold",
                Name = "Transition to Cold Storage",
                Enabled = false,
                Scope = PolicyScope.Global,
                Rules = new List<LifecycleRule>
                {
                    new()
                    {
                        Type = RuleType.Transition,
                        AgeDays = 90,
                        Action = LifecycleAction.TransitionToTier,
                        TargetTier = StorageTier.Cold
                    }
                }
            });
        }

        private List<LifecyclePolicy> FindApplicablePolicies(ObjectLifecycleState state)
        {
            var applicable = new List<LifecyclePolicy>();

            foreach (var policy in _policies.Values.Where(p => p.Enabled))
            {
                switch (policy.Scope)
                {
                    case PolicyScope.Global:
                        applicable.Add(policy);
                        break;

                    case PolicyScope.Prefix:
                        if (!string.IsNullOrEmpty(policy.Prefix) &&
                            state.Key.StartsWith(policy.Prefix, StringComparison.OrdinalIgnoreCase))
                        {
                            applicable.Add(policy);
                        }
                        break;

                    case PolicyScope.Backend:
                        if (state.BackendId == policy.BackendId)
                        {
                            applicable.Add(policy);
                        }
                        break;

                    case PolicyScope.Object:
                        if (state.Key == policy.ObjectKey)
                        {
                            applicable.Add(policy);
                        }
                        break;
                }
            }

            return applicable;
        }

        private async Task<bool> EvaluatePolicyForObjectAsync(
            ObjectLifecycleState state,
            LifecyclePolicy policy,
            CancellationToken ct)
        {
            foreach (var rule in policy.Rules)
            {
                if (ShouldApplyRule(state, rule))
                {
                    return await ExecuteRuleActionAsync(state, rule, ct);
                }
            }

            return false;
        }

        private bool ShouldApplyRule(ObjectLifecycleState state, LifecycleRule rule)
        {
            // Check age condition
            if (rule.AgeDays.HasValue)
            {
                var age = DateTime.UtcNow - state.CreatedAt;
                if (age.TotalDays < rule.AgeDays.Value)
                {
                    return false;
                }
            }

            // Check size condition
            if (rule.MinSizeBytes.HasValue && state.Size < rule.MinSizeBytes.Value)
            {
                return false;
            }

            if (rule.MaxSizeBytes.HasValue && state.Size > rule.MaxSizeBytes.Value)
            {
                return false;
            }

            // Check last accessed condition
            if (rule.DaysSinceLastAccess.HasValue)
            {
                var daysSinceAccess = (DateTime.UtcNow - state.LastAccessed).TotalDays;
                if (daysSinceAccess < rule.DaysSinceLastAccess.Value)
                {
                    return false;
                }
            }

            return true;
        }

        private async Task<bool> ExecuteRuleActionAsync(
            ObjectLifecycleState state,
            LifecycleRule rule,
            CancellationToken ct)
        {
            try
            {
                switch (rule.Action)
                {
                    case LifecycleAction.Delete:
                        return await DeleteObjectAsync(state, ct);

                    case LifecycleAction.TransitionToTier:
                        if (rule.TargetTier.HasValue)
                        {
                            return await TransitionObjectAsync(state, rule.TargetTier.Value, ct);
                        }
                        break;

                    case LifecycleAction.Archive:
                        return await TransitionObjectAsync(state, StorageTier.Archive, ct);

                    case LifecycleAction.MarkForDeletion:
                        state.MarkedForDeletion = true;
                        state.DeletionScheduledAt = DateTime.UtcNow.AddDays(rule.DeletionGracePeriodDays ?? 30);
                        LogLifecycleEvent(new LifecycleEvent
                        {
                            EventType = LifecycleEventType.MarkedForDeletion,
                            ObjectKey = state.Key,
                            Timestamp = DateTime.UtcNow,
                            Details = $"Scheduled for deletion at {state.DeletionScheduledAt}"
                        });
                        return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LifecycleManagementFeature] Action execution failed for {state.Key}: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> DeleteObjectAsync(ObjectLifecycleState state, CancellationToken ct)
        {
            var backend = _registry.Get(state.BackendId);
            if (backend == null)
            {
                return false;
            }

            try
            {
                await backend.DeleteAsync(state.Key, ct);

                _objectStates.TryRemove(state.Key, out _);
                Interlocked.Increment(ref _totalExpiredObjects);
                Interlocked.Increment(ref _totalDeletedObjects);

                LogLifecycleEvent(new LifecycleEvent
                {
                    EventType = LifecycleEventType.ObjectDeleted,
                    ObjectKey = state.Key,
                    Timestamp = DateTime.UtcNow,
                    Details = "Object deleted due to lifecycle policy"
                });

                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LifecycleManagementFeature] Object deletion failed for {state.Key}: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> TransitionObjectAsync(
            ObjectLifecycleState state,
            StorageTier targetTier,
            CancellationToken ct)
        {
            var sourceBackend = _registry.Get(state.BackendId);
            if (sourceBackend == null)
            {
                return false;
            }

            var targetBackend = _registry.GetByPredicate(s => s.Tier == targetTier).FirstOrDefault();
            if (targetBackend == null)
            {
                return false;
            }

            try
            {
                // Read from source
                var data = await sourceBackend.RetrieveAsync(state.Key, ct);
                var metadata = await sourceBackend.GetMetadataAsync(state.Key, ct);

                // Convert readonly dictionary to mutable
                var metadataDict = metadata.CustomMetadata != null
                    ? new Dictionary<string, string>(metadata.CustomMetadata)
                    : null;

                // Write to target
                await targetBackend.StoreAsync(state.Key, data, metadataDict, ct);

                // Delete from source — if this fails, roll back the target write to avoid duplication
                try
                {
                    await sourceBackend.DeleteAsync(state.Key, ct);
                }
                catch (Exception deleteEx)
                {
                    System.Diagnostics.Debug.WriteLine($"[LifecycleManagementFeature] Source delete failed for {state.Key}, rolling back target write: {deleteEx.Message}");
                    try { await targetBackend.DeleteAsync(state.Key, ct); } catch { /* best-effort rollback */ }
                    throw new InvalidOperationException($"Transition aborted: source delete failed for '{state.Key}'", deleteEx);
                }

                // Update state
                state.BackendId = targetBackend.StrategyId;
                Interlocked.Increment(ref _totalTransitions);

                LogLifecycleEvent(new LifecycleEvent
                {
                    EventType = LifecycleEventType.ObjectTransitioned,
                    ObjectKey = state.Key,
                    Timestamp = DateTime.UtcNow,
                    Details = $"Transitioned to {targetTier} tier"
                });

                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LifecycleManagementFeature] Object transition failed: {ex.Message}");
                return false;
            }
        }

        private void LogLifecycleEvent(LifecycleEvent lifecycleEvent)
        {
            lock (_auditLock)
            {
                _auditTrail.Add(lifecycleEvent);

                // Trim audit trail if it exceeds max size
                if (_auditTrail.Count > _maxAuditTrailSize)
                {
                    _auditTrail.RemoveRange(0, _auditTrail.Count - _maxAuditTrailSize);
                }
            }
        }

        #endregion

        /// <summary>
        /// Disposes resources.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _lifecycleTimer.Dispose();
            _policies.Clear();
            _objectStates.Clear();
            _legalHolds.Clear();
            lock (_auditLock)
            {
                _auditTrail.Clear();
            }
        }
    }

    #region Supporting Types

    /// <summary>
    /// Lifecycle policy configuration.
    /// </summary>
    public sealed class LifecyclePolicy
    {
        /// <summary>Unique policy identifier.</summary>
        public string PolicyId { get; set; } = string.Empty;

        /// <summary>Policy name.</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>Whether the policy is enabled.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Policy scope (global, prefix, backend, object).</summary>
        public PolicyScope Scope { get; init; } = PolicyScope.Global;

        /// <summary>Prefix for prefix-scoped policies.</summary>
        public string? Prefix { get; init; }

        /// <summary>Backend ID for backend-scoped policies.</summary>
        public string? BackendId { get; init; }

        /// <summary>Object key for object-scoped policies.</summary>
        public string? ObjectKey { get; init; }

        /// <summary>List of lifecycle rules.</summary>
        public List<LifecycleRule> Rules { get; init; } = new();
    }

    /// <summary>
    /// Policy scope enumeration.
    /// </summary>
    public enum PolicyScope
    {
        /// <summary>Applies to all objects.</summary>
        Global,

        /// <summary>Applies to objects with specific prefix.</summary>
        Prefix,

        /// <summary>Applies to objects in specific backend.</summary>
        Backend,

        /// <summary>Applies to a specific object.</summary>
        Object
    }

    /// <summary>
    /// Lifecycle rule.
    /// </summary>
    public sealed class LifecycleRule
    {
        /// <summary>Rule type.</summary>
        public RuleType Type { get; init; }

        /// <summary>Minimum object age in days.</summary>
        public int? AgeDays { get; init; }

        /// <summary>Days since last access.</summary>
        public int? DaysSinceLastAccess { get; init; }

        /// <summary>Minimum object size in bytes.</summary>
        public long? MinSizeBytes { get; init; }

        /// <summary>Maximum object size in bytes.</summary>
        public long? MaxSizeBytes { get; init; }

        /// <summary>Action to take when rule conditions are met.</summary>
        public LifecycleAction Action { get; init; }

        /// <summary>Target tier for transition actions.</summary>
        public StorageTier? TargetTier { get; init; }

        /// <summary>Grace period in days before deletion.</summary>
        public int? DeletionGracePeriodDays { get; init; }
    }

    /// <summary>
    /// Rule type enumeration.
    /// </summary>
    public enum RuleType
    {
        /// <summary>Expiration rule (triggers deletion).</summary>
        Expiration,

        /// <summary>Transition rule (moves to different tier).</summary>
        Transition,

        /// <summary>Archive rule (moves to archive tier).</summary>
        Archive
    }

    /// <summary>
    /// Lifecycle action enumeration.
    /// </summary>
    public enum LifecycleAction
    {
        /// <summary>Delete the object.</summary>
        Delete,

        /// <summary>Transition to a different tier.</summary>
        TransitionToTier,

        /// <summary>Archive the object.</summary>
        Archive,

        /// <summary>Mark for deletion (soft delete).</summary>
        MarkForDeletion
    }

    /// <summary>
    /// Object lifecycle state.
    /// </summary>
    public sealed class ObjectLifecycleState
    {
        /// <summary>Object key.</summary>
        public string Key { get; init; } = string.Empty;

        /// <summary>Backend ID where object is stored.</summary>
        public string BackendId { get; set; } = string.Empty;

        /// <summary>Object creation timestamp.</summary>
        public DateTime CreatedAt { get; init; }

        /// <summary>Last access timestamp.</summary>
        public DateTime LastAccessed { get; set; }

        /// <summary>Object size in bytes.</summary>
        public long Size { get; init; }

        /// <summary>Whether object is marked for deletion.</summary>
        public bool MarkedForDeletion { get; set; }

        /// <summary>Scheduled deletion timestamp.</summary>
        public DateTime? DeletionScheduledAt { get; set; }
    }

    /// <summary>
    /// Legal hold information.
    /// </summary>
    public sealed class LegalHold
    {
        /// <summary>Object key.</summary>
        public string Key { get; init; } = string.Empty;

        /// <summary>Reason for legal hold.</summary>
        public string Reason { get; init; } = string.Empty;

        /// <summary>When hold was placed.</summary>
        public DateTime PlacedAt { get; init; }

        /// <summary>When hold expires (null = indefinite).</summary>
        public DateTime? ExpiresAt { get; init; }
    }

    /// <summary>
    /// Lifecycle event for audit trail.
    /// </summary>
    public sealed class LifecycleEvent
    {
        /// <summary>Event type.</summary>
        public LifecycleEventType EventType { get; init; }

        /// <summary>Object key (if applicable).</summary>
        public string? ObjectKey { get; init; }

        /// <summary>Event timestamp.</summary>
        public DateTime Timestamp { get; init; }

        /// <summary>Event details.</summary>
        public string Details { get; init; } = string.Empty;
    }

    /// <summary>
    /// Lifecycle event type enumeration.
    /// </summary>
    public enum LifecycleEventType
    {
        /// <summary>Policy created.</summary>
        PolicyCreated,

        /// <summary>Policy updated.</summary>
        PolicyUpdated,

        /// <summary>Policy deleted.</summary>
        PolicyDeleted,

        /// <summary>Object expired.</summary>
        ObjectExpired,

        /// <summary>Object deleted.</summary>
        ObjectDeleted,

        /// <summary>Object transitioned to different tier.</summary>
        ObjectTransitioned,

        /// <summary>Object marked for deletion.</summary>
        MarkedForDeletion,

        /// <summary>Legal hold placed.</summary>
        LegalHoldPlaced,

        /// <summary>Legal hold removed.</summary>
        LegalHoldRemoved
    }

    #endregion
}
