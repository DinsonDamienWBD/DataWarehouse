using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Policy;

namespace DataWarehouse.SDK.Infrastructure.Authority
{
    /// <summary>
    /// Status of the dead man's switch, reflecting the current inactivity state
    /// and time remaining before auto-lock triggers.
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 75: Hardware Tokens & Dead Man's Switch (AUTH-06, AUTH-08)")]
    public sealed record DeadManSwitchStatus
    {
        /// <summary>True if the system has been auto-locked due to inactivity.</summary>
        public bool IsLocked { get; init; }

        /// <summary>True if the warning period has been reached but lock has not yet triggered.</summary>
        public bool IsWarning { get; init; }

        /// <summary>UTC timestamp of the most recent monitored activity, or null if no activity has been recorded.</summary>
        public DateTimeOffset? LastActivityAt { get; init; }

        /// <summary>
        /// Days remaining until auto-lock triggers. 0 if locked, negative if overdue
        /// (locked N days ago beyond threshold).
        /// </summary>
        public int DaysUntilLock { get; init; }

        /// <summary>Number of whole days since the last monitored activity was recorded.</summary>
        public int DaysSinceLastActivity { get; init; }
    }

    /// <summary>
    /// Monitors super admin activity and auto-locks the system to maximum security after a
    /// configurable period of inactivity. Integrates with the authority chain via
    /// <see cref="IAuthorityResolver"/> to record SystemDefaults auto-lock decisions.
    /// <para>
    /// The dead man's switch ensures that an abandoned system (no super admin activity for N days)
    /// cannot be exploited. When triggered, only quorum-level operations can proceed until a
    /// super admin authenticates and explicitly unlocks.
    /// </para>
    /// <para>
    /// Thread-safe: uses <see cref="ConcurrentDictionary{TKey,TValue}"/> for activity tracking
    /// and volatile fields for lock/warning state.
    /// </para>
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 75: Hardware Tokens & Dead Man's Switch (AUTH-06, AUTH-08)")]
    public sealed class DeadManSwitch
    {
        private readonly IAuthorityResolver _authorityResolver;
        private readonly DeadManSwitchConfiguration _config;
        private readonly ConcurrentDictionary<string, DateTimeOffset> _activityTimestamps =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly DateTimeOffset _createdAt;
        private volatile bool _isLocked;
        private volatile bool _warningIssued;

        /// <summary>
        /// Initializes a new instance of the <see cref="DeadManSwitch"/>.
        /// </summary>
        /// <param name="authorityResolver">
        /// The authority resolver used to record SystemDefaults auto-lock decisions when the switch triggers.
        /// </param>
        /// <param name="config">
        /// Configuration controlling inactivity threshold, warning period, and monitored activity types.
        /// If null, defaults to standard configuration (30-day threshold, 7-day warning).
        /// </param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="authorityResolver"/> is null.</exception>
        public DeadManSwitch(IAuthorityResolver authorityResolver, DeadManSwitchConfiguration? config = null)
        {
            _authorityResolver = authorityResolver ?? throw new ArgumentNullException(nameof(authorityResolver));
            _config = config ?? new DeadManSwitchConfiguration();
            _createdAt = DateTimeOffset.UtcNow;
        }

        /// <summary>
        /// Records a monitored activity, resetting the inactivity timer for the specified activity type.
        /// Only activities whose type is in <see cref="DeadManSwitchConfiguration.MonitoredActivityTypes"/>
        /// are recorded; other types are silently ignored.
        /// </summary>
        /// <param name="activityType">The type of activity being recorded (e.g., "QuorumApproval", "Login").</param>
        /// <param name="actorId">Identity of the actor performing the activity.</param>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the system is locked by the dead man's switch. Super admin must authenticate
        /// and call <see cref="UnlockAsync"/> before normal operations can resume.
        /// </exception>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="activityType"/> or <paramref name="actorId"/> is null.</exception>
        public void RecordActivity(string activityType, string actorId)
        {
            if (string.IsNullOrEmpty(activityType))
                throw new ArgumentNullException(nameof(activityType));
            if (string.IsNullOrEmpty(actorId))
                throw new ArgumentNullException(nameof(actorId));

            if (_isLocked)
                throw new InvalidOperationException("System is locked by dead man's switch; super admin must authenticate to unlock");

            // Only record monitored activity types
            var isMonitored = false;
            for (var i = 0; i < _config.MonitoredActivityTypes.Length; i++)
            {
                if (string.Equals(_config.MonitoredActivityTypes[i], activityType, StringComparison.OrdinalIgnoreCase))
                {
                    isMonitored = true;
                    break;
                }
            }

            if (!isMonitored)
                return;

            _activityTimestamps[activityType] = DateTimeOffset.UtcNow;
        }

        /// <summary>
        /// Checks the current status of the dead man's switch without enforcing any state changes.
        /// Returns the lock state, warning state, last activity timestamp, and time remaining.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>A <see cref="DeadManSwitchStatus"/> reflecting the current inactivity state.</returns>
        public Task<DeadManSwitchStatus> CheckStatusAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            var lastActivity = GetMostRecentActivity();
            var elapsed = DateTimeOffset.UtcNow - lastActivity;
            var daysUntilLock = (int)(_config.InactivityThreshold - elapsed).TotalDays;

            if (_isLocked)
                daysUntilLock = -(int)(elapsed - _config.InactivityThreshold).TotalDays;

            var status = new DeadManSwitchStatus
            {
                IsLocked = _isLocked,
                IsWarning = _warningIssued && !_isLocked,
                LastActivityAt = _activityTimestamps.IsEmpty ? null : lastActivity,
                DaysUntilLock = _isLocked ? daysUntilLock : Math.Max(0, daysUntilLock),
                DaysSinceLastActivity = (int)elapsed.TotalDays
            };

            return Task.FromResult(status);
        }

        /// <summary>
        /// Main enforcement method: checks inactivity and triggers auto-lock or warning as needed.
        /// <para>
        /// If elapsed time exceeds <see cref="DeadManSwitchConfiguration.InactivityThreshold"/>:
        /// locks the system and records a SystemDefaults authority decision via <see cref="IAuthorityResolver"/>.
        /// </para>
        /// <para>
        /// If elapsed time exceeds (InactivityThreshold - WarningPeriod): sets warning state.
        /// </para>
        /// <para>
        /// Should be called periodically by a background timer (e.g., every minute via
        /// AuthorityChainFacade.RunMaintenanceAsync).
        /// </para>
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        public async Task CheckAndEnforceAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            if (!_config.Enabled || _isLocked)
                return;

            var lastActivity = GetMostRecentActivity();
            var elapsed = DateTimeOffset.UtcNow - lastActivity;

            if (elapsed > _config.InactivityThreshold)
            {
                _isLocked = true;
                var daysSinceActivity = (int)elapsed.TotalDays;

                await _authorityResolver.RecordDecisionAsync(
                    "SystemDefaults",
                    "DeadManSwitch.AutoLock",
                    actorId: null,
                    reason: $"No super admin activity for {daysSinceActivity} days; system auto-locked to maximum security",
                    ct: ct).ConfigureAwait(false);
            }
            else if (elapsed > _config.InactivityThreshold - _config.WarningPeriod)
            {
                _warningIssued = true;
            }
        }

        /// <summary>
        /// Unlocks the system after a dead man's switch auto-lock. Records the unlock activity
        /// and an authority decision documenting the unlock event.
        /// </summary>
        /// <param name="superAdminId">Identity of the super admin authenticating to unlock.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="superAdminId"/> is null or empty.</exception>
        public async Task UnlockAsync(string superAdminId, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(superAdminId))
                throw new ArgumentNullException(nameof(superAdminId));

            ct.ThrowIfCancellationRequested();

            _isLocked = false;
            _warningIssued = false;

            // Record the unlock activity (bypass the locked check by writing directly)
            _activityTimestamps["Login"] = DateTimeOffset.UtcNow;

            await _authorityResolver.RecordDecisionAsync(
                "SystemDefaults",
                "DeadManSwitch.Unlock",
                actorId: superAdminId,
                reason: $"Super admin {superAdminId} authenticated and unlocked the system",
                ct: ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Finds the most recent activity timestamp across all monitored activity types.
        /// If no activity has been recorded, returns the switch creation time.
        /// </summary>
        private DateTimeOffset GetMostRecentActivity()
        {
            if (_activityTimestamps.IsEmpty)
                return _createdAt;

            var mostRecent = _createdAt;
            foreach (var kvp in _activityTimestamps)
            {
                if (kvp.Value > mostRecent)
                    mostRecent = kvp.Value;
            }

            return mostRecent;
        }
    }
}
