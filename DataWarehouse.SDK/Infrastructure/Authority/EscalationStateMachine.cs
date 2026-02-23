using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Policy;

namespace DataWarehouse.SDK.Infrastructure.Authority
{
    /// <summary>
    /// State machine implementation of <see cref="IEscalationService"/> that manages the lifecycle
    /// of emergency AI overrides through strict state transitions: Pending -> Active -> Confirmed|Reverted|TimedOut.
    /// <para>
    /// Integrates with the authority chain via <see cref="IAuthorityResolver"/> to record AiEmergency
    /// authority decisions when escalations are activated, and with <see cref="AuthorityContextPropagator"/>
    /// to propagate the override context through async call chains.
    /// </para>
    /// <para>
    /// Thread-safe: uses a <see cref="SemaphoreSlim"/> to serialize state transitions and prevent
    /// race conditions when multiple actors attempt concurrent transitions on the same escalation.
    /// </para>
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 75: Emergency Escalation (AUTH-01, AUTH-02, AUTH-03)")]
    public sealed class EscalationStateMachine : IEscalationService, IDisposable
    {
        private readonly IAuthorityResolver _authorityResolver;
        private readonly EscalationRecordStore _store;
        private readonly EscalationConfiguration _config;
        private readonly SemaphoreSlim _transitionLock = new(1, 1);

        /// <summary>
        /// Initializes a new instance of the <see cref="EscalationStateMachine"/>.
        /// </summary>
        /// <param name="authorityResolver">
        /// The authority resolver used to record AiEmergency decisions when escalations are activated.
        /// </param>
        /// <param name="store">
        /// The immutable record store for persisting escalation state transitions.
        /// </param>
        /// <param name="config">
        /// Configuration controlling override windows, concurrency limits, and validation.
        /// If null, defaults to standard configuration (15-minute window, max 3 concurrent, reason required).
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="authorityResolver"/> or <paramref name="store"/> is null.
        /// </exception>
        public EscalationStateMachine(
            IAuthorityResolver authorityResolver,
            EscalationRecordStore store,
            EscalationConfiguration? config = null)
        {
            _authorityResolver = authorityResolver ?? throw new ArgumentNullException(nameof(authorityResolver));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _config = config ?? new EscalationConfiguration();
        }

        /// <inheritdoc />
        public async Task<EscalationRecord> RequestEscalationAsync(
            string requestedBy,
            string reason,
            string featureId,
            TimeSpan? overrideWindow = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(requestedBy))
                throw new ArgumentException("RequestedBy must not be null or empty.", nameof(requestedBy));
            if (string.IsNullOrEmpty(featureId))
                throw new ArgumentException("FeatureId must not be null or empty.", nameof(featureId));

            if (_config.RequireReason && string.IsNullOrWhiteSpace(reason))
                throw new ArgumentException("Reason is required by escalation configuration.", nameof(reason));

            var window = overrideWindow ?? _config.DefaultOverrideWindow;
            if (window > _config.MaxOverrideWindow)
            {
                throw new ArgumentException(
                    $"Override window ({window.TotalMinutes:F0} min) exceeds maximum allowed ({_config.MaxOverrideWindow.TotalMinutes:F0} min).",
                    nameof(overrideWindow));
            }

            await _transitionLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Check concurrent escalation limit (count Active escalations)
                var activeEscalations = _store.GetByState(EscalationState.Active);
                if (activeEscalations.Count >= _config.MaxConcurrentEscalations)
                {
                    throw new InvalidOperationException(
                        $"Maximum concurrent escalations ({_config.MaxConcurrentEscalations}) reached. " +
                        $"Confirm or revert an existing escalation before requesting a new one.");
                }

                var now = DateTimeOffset.UtcNow;
                var escalationId = Guid.NewGuid().ToString("D");

                var hash = EscalationRecord.ComputeHash(
                    escalationId, requestedBy, reason ?? string.Empty, featureId,
                    EscalationState.Pending, now, null, null, null, window, null, null);

                var record = new EscalationRecord
                {
                    EscalationId = escalationId,
                    RequestedBy = requestedBy,
                    Reason = reason ?? string.Empty,
                    AffectedFeatureId = featureId,
                    State = EscalationState.Pending,
                    RequestedAt = now,
                    OverrideWindow = window,
                    RecordHash = hash
                };

                _store.Store(record);
                return record;
            }
            finally
            {
                _transitionLock.Release();
            }
        }

        /// <inheritdoc />
        public async Task<EscalationRecord> ActivateEscalationAsync(
            string escalationId,
            string policySnapshotJson,
            string overridePolicyJson,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(escalationId))
                throw new ArgumentException("EscalationId must not be null or empty.", nameof(escalationId));

            await _transitionLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var current = _store.Get(escalationId);
                if (current is null)
                    throw new KeyNotFoundException($"Escalation '{escalationId}' not found.");

                ValidateTransition(current.State, EscalationState.Active);

                var now = DateTimeOffset.UtcNow;

                var hash = EscalationRecord.ComputeHash(
                    current.EscalationId, current.RequestedBy, current.Reason,
                    current.AffectedFeatureId, EscalationState.Active,
                    current.RequestedAt, now, null, null, current.OverrideWindow,
                    policySnapshotJson, overridePolicyJson);

                var record = new EscalationRecord
                {
                    EscalationId = current.EscalationId,
                    RequestedBy = current.RequestedBy,
                    Reason = current.Reason,
                    AffectedFeatureId = current.AffectedFeatureId,
                    State = EscalationState.Active,
                    RequestedAt = current.RequestedAt,
                    ActivatedAt = now,
                    OverrideWindow = current.OverrideWindow,
                    PreviousPolicySnapshot = policySnapshotJson,
                    OverridePolicySnapshot = overridePolicyJson,
                    RecordHash = hash
                };

                _store.Store(record);

                // Record AiEmergency authority decision
                await _authorityResolver.RecordDecisionAsync(
                    "AiEmergency",
                    current.AffectedFeatureId,
                    current.RequestedBy,
                    current.Reason,
                    ct).ConfigureAwait(false);

                // Propagate the AiEmergency authority context
                var authorityDecision = new AuthorityDecision
                {
                    DecisionId = Guid.NewGuid().ToString("D"),
                    AuthorityLevelName = "AiEmergency",
                    AuthorityPriority = 1,
                    Action = current.AffectedFeatureId,
                    Timestamp = now,
                    ActorId = current.RequestedBy,
                    DecisionReason = $"Emergency escalation: {current.Reason}",
                    IsActive = true
                };
                AuthorityContextPropagator.SetContext(authorityDecision);

                return record;
            }
            finally
            {
                _transitionLock.Release();
            }
        }

        /// <inheritdoc />
        public async Task<EscalationRecord> ConfirmEscalationAsync(
            string escalationId,
            string confirmedBy,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(escalationId))
                throw new ArgumentException("EscalationId must not be null or empty.", nameof(escalationId));
            if (string.IsNullOrEmpty(confirmedBy))
                throw new ArgumentException("ConfirmedBy must not be null or empty.", nameof(confirmedBy));

            await _transitionLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var current = _store.Get(escalationId);
                if (current is null)
                    throw new KeyNotFoundException($"Escalation '{escalationId}' not found.");

                ValidateTransition(current.State, EscalationState.Confirmed);

                var now = DateTimeOffset.UtcNow;

                var hash = EscalationRecord.ComputeHash(
                    current.EscalationId, current.RequestedBy, current.Reason,
                    current.AffectedFeatureId, EscalationState.Confirmed,
                    current.RequestedAt, current.ActivatedAt, now, confirmedBy,
                    current.OverrideWindow, current.PreviousPolicySnapshot,
                    current.OverridePolicySnapshot);

                var record = new EscalationRecord
                {
                    EscalationId = current.EscalationId,
                    RequestedBy = current.RequestedBy,
                    Reason = current.Reason,
                    AffectedFeatureId = current.AffectedFeatureId,
                    State = EscalationState.Confirmed,
                    RequestedAt = current.RequestedAt,
                    ActivatedAt = current.ActivatedAt,
                    ResolvedAt = now,
                    ResolvedBy = confirmedBy,
                    OverrideWindow = current.OverrideWindow,
                    PreviousPolicySnapshot = current.PreviousPolicySnapshot,
                    OverridePolicySnapshot = current.OverridePolicySnapshot,
                    RecordHash = hash
                };

                _store.Store(record);
                return record;
            }
            finally
            {
                _transitionLock.Release();
            }
        }

        /// <inheritdoc />
        public async Task<EscalationRecord> RevertEscalationAsync(
            string escalationId,
            string revertedBy,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(escalationId))
                throw new ArgumentException("EscalationId must not be null or empty.", nameof(escalationId));
            if (string.IsNullOrEmpty(revertedBy))
                throw new ArgumentException("RevertedBy must not be null or empty.", nameof(revertedBy));

            await _transitionLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var current = _store.Get(escalationId);
                if (current is null)
                    throw new KeyNotFoundException($"Escalation '{escalationId}' not found.");

                ValidateTransition(current.State, EscalationState.Reverted);

                var now = DateTimeOffset.UtcNow;

                var hash = EscalationRecord.ComputeHash(
                    current.EscalationId, current.RequestedBy, current.Reason,
                    current.AffectedFeatureId, EscalationState.Reverted,
                    current.RequestedAt, current.ActivatedAt, now, revertedBy,
                    current.OverrideWindow, current.PreviousPolicySnapshot,
                    current.OverridePolicySnapshot);

                var record = new EscalationRecord
                {
                    EscalationId = current.EscalationId,
                    RequestedBy = current.RequestedBy,
                    Reason = current.Reason,
                    AffectedFeatureId = current.AffectedFeatureId,
                    State = EscalationState.Reverted,
                    RequestedAt = current.RequestedAt,
                    ActivatedAt = current.ActivatedAt,
                    ResolvedAt = now,
                    ResolvedBy = revertedBy,
                    OverrideWindow = current.OverrideWindow,
                    PreviousPolicySnapshot = current.PreviousPolicySnapshot,
                    OverridePolicySnapshot = current.OverridePolicySnapshot,
                    RecordHash = hash
                };

                _store.Store(record);

                // Clear authority context if the reverted escalation is the current context
                var currentContext = AuthorityContextPropagator.Current;
                if (currentContext is not null &&
                    string.Equals(currentContext.Action, current.AffectedFeatureId, StringComparison.Ordinal) &&
                    string.Equals(currentContext.AuthorityLevelName, "AiEmergency", StringComparison.OrdinalIgnoreCase))
                {
                    AuthorityContextPropagator.Clear();
                }

                return record;
            }
            finally
            {
                _transitionLock.Release();
            }
        }

        /// <inheritdoc />
        public Task<EscalationRecord> GetEscalationAsync(
            string escalationId,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(escalationId))
                throw new ArgumentException("EscalationId must not be null or empty.", nameof(escalationId));

            var record = _store.Get(escalationId);
            if (record is null)
                throw new KeyNotFoundException($"Escalation '{escalationId}' not found.");

            return Task.FromResult(record);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<EscalationRecord>> GetActiveEscalationsAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_store.GetByState(EscalationState.Active));
        }

        /// <inheritdoc />
        public async Task CheckTimeoutsAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            var activeEscalations = _store.GetByState(EscalationState.Active);
            var now = DateTimeOffset.UtcNow;

            foreach (var active in activeEscalations)
            {
                ct.ThrowIfCancellationRequested();

                if (active.ActivatedAt is null)
                    continue;

                var expiry = active.ActivatedAt.Value + active.OverrideWindow;
                if (expiry >= now)
                    continue;

                // Escalation has expired -- transition to TimedOut
                await _transitionLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    // Re-check state under lock in case it was confirmed/reverted between read and lock
                    var current = _store.Get(active.EscalationId);
                    if (current is null || current.State != EscalationState.Active)
                        continue;

                    if (current.ActivatedAt is null)
                        continue;

                    var currentExpiry = current.ActivatedAt.Value + current.OverrideWindow;
                    if (currentExpiry >= now)
                        continue;

                    var hash = EscalationRecord.ComputeHash(
                        current.EscalationId, current.RequestedBy, current.Reason,
                        current.AffectedFeatureId, EscalationState.TimedOut,
                        current.RequestedAt, current.ActivatedAt, now, null,
                        current.OverrideWindow, current.PreviousPolicySnapshot,
                        current.OverridePolicySnapshot);

                    var timedOutRecord = new EscalationRecord
                    {
                        EscalationId = current.EscalationId,
                        RequestedBy = current.RequestedBy,
                        Reason = current.Reason,
                        AffectedFeatureId = current.AffectedFeatureId,
                        State = EscalationState.TimedOut,
                        RequestedAt = current.RequestedAt,
                        ActivatedAt = current.ActivatedAt,
                        ResolvedAt = now,
                        ResolvedBy = null, // System auto-revert
                        OverrideWindow = current.OverrideWindow,
                        PreviousPolicySnapshot = current.PreviousPolicySnapshot,
                        OverridePolicySnapshot = current.OverridePolicySnapshot,
                        RecordHash = hash
                    };

                    _store.Store(timedOutRecord);

                    // Clear authority context if this timed-out escalation is the current context
                    var currentContext = AuthorityContextPropagator.Current;
                    if (currentContext is not null &&
                        string.Equals(currentContext.Action, current.AffectedFeatureId, StringComparison.Ordinal) &&
                        string.Equals(currentContext.AuthorityLevelName, "AiEmergency", StringComparison.OrdinalIgnoreCase))
                    {
                        AuthorityContextPropagator.Clear();
                    }
                }
                finally
                {
                    _transitionLock.Release();
                }
            }
        }

        /// <summary>
        /// Validates that the proposed state transition is allowed by the state machine rules.
        /// </summary>
        /// <param name="currentState">The current state of the escalation.</param>
        /// <param name="targetState">The proposed target state.</param>
        /// <exception cref="InvalidOperationException">Thrown when the transition is not allowed.</exception>
        private static void ValidateTransition(EscalationState currentState, EscalationState targetState)
        {
            var valid = (currentState, targetState) switch
            {
                (EscalationState.Pending, EscalationState.Active) => true,
                (EscalationState.Active, EscalationState.Confirmed) => true,
                (EscalationState.Active, EscalationState.Reverted) => true,
                (EscalationState.Active, EscalationState.TimedOut) => true,
                _ => false
            };

            if (!valid)
            {
                throw new InvalidOperationException(
                    $"Invalid escalation state transition: {currentState} -> {targetState}. " +
                    $"Allowed transitions: Pending->Active, Active->Confirmed, Active->Reverted, Active->TimedOut.");
            }
        }

        /// <summary>
        /// Releases resources used by the state machine, including the internal semaphore.
        /// </summary>
        public void Dispose()
        {
            _transitionLock.Dispose();
        }
    }
}
