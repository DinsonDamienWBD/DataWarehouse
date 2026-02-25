using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Policy
{
    /// <summary>
    /// Represents the current state of an emergency escalation within its lifecycle.
    /// Transitions follow the state machine: Pending -> Active -> Confirmed|Reverted|TimedOut.
    /// Terminal states (Confirmed, Reverted, TimedOut) cannot transition further.
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 75: Emergency Escalation (AUTH-01, AUTH-02, AUTH-03)")]
    public enum EscalationState
    {
        /// <summary>
        /// Escalation has been requested but not yet activated. Awaiting activation with policy snapshots.
        /// </summary>
        [Description("Escalation requested, awaiting activation")]
        Pending = 0,

        /// <summary>
        /// AI override is active with a running countdown timer. The override window is in effect.
        /// </summary>
        [Description("AI override is active, countdown running")]
        Active = 1,

        /// <summary>
        /// An administrator confirmed the escalation, making the override permanent. Terminal state.
        /// </summary>
        [Description("Admin confirmed, override is permanent")]
        Confirmed = 2,

        /// <summary>
        /// An administrator reverted the escalation, restoring the prior policy state. Terminal state.
        /// </summary>
        [Description("Admin reverted, prior state restored")]
        Reverted = 3,

        /// <summary>
        /// The override window expired without admin action, triggering automatic reversion. Terminal state.
        /// </summary>
        [Description("Countdown expired, auto-reverted")]
        TimedOut = 4
    }

    /// <summary>
    /// Immutable record representing a single state transition in an emergency escalation's lifecycle.
    /// Each record contains a SHA-256 tamper-proof hash computed from all other fields.
    /// Any modification to the record invalidates the hash, enabling tamper detection.
    /// <para>
    /// Records are created at each state transition (Pending, Active, Confirmed, Reverted, TimedOut)
    /// and stored in the EscalationRecordStore.
    /// </para>
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 75: Emergency Escalation (AUTH-01, AUTH-02, AUTH-03)")]
    public sealed record EscalationRecord
    {
        /// <summary>
        /// Unique identifier for this escalation (GUID string). Shared across all state transition
        /// records for the same escalation.
        /// </summary>
        public required string EscalationId { get; init; }

        /// <summary>
        /// Identity of the actor who originally requested the escalation.
        /// </summary>
        public required string RequestedBy { get; init; }

        /// <summary>
        /// Human-readable justification for why the escalation is needed.
        /// </summary>
        public required string Reason { get; init; }

        /// <summary>
        /// Identifier of the feature or policy being overridden by this escalation.
        /// </summary>
        public required string AffectedFeatureId { get; init; }

        /// <summary>
        /// Current state of the escalation at the time this record was created.
        /// </summary>
        public required EscalationState State { get; init; }

        /// <summary>
        /// UTC timestamp when the escalation was originally requested.
        /// </summary>
        public required DateTimeOffset RequestedAt { get; init; }

        /// <summary>
        /// UTC timestamp when the escalation was activated (state transitioned to Active).
        /// Null for Pending records.
        /// </summary>
        public DateTimeOffset? ActivatedAt { get; init; }

        /// <summary>
        /// UTC timestamp when the escalation reached a terminal state (Confirmed, Reverted, or TimedOut).
        /// Null for Pending and Active records.
        /// </summary>
        public DateTimeOffset? ResolvedAt { get; init; }

        /// <summary>
        /// Identity of the actor who confirmed or reverted the escalation. Null for Pending,
        /// Active, and TimedOut records (timeout is system-initiated).
        /// </summary>
        public string? ResolvedBy { get; init; }

        /// <summary>
        /// Duration of the AI override window. After activation, the escalation auto-reverts
        /// if not confirmed or reverted within this period. Default: 15 minutes.
        /// </summary>
        public required TimeSpan OverrideWindow { get; init; }

        /// <summary>
        /// JSON snapshot of the policy state before the override was applied.
        /// Used to restore the previous state on revert or timeout. Null for Pending records.
        /// </summary>
        public string? PreviousPolicySnapshot { get; init; }

        /// <summary>
        /// JSON snapshot of the override policy applied during the escalation.
        /// Null for Pending records.
        /// </summary>
        public string? OverridePolicySnapshot { get; init; }

        /// <summary>
        /// SHA-256 tamper-proof hash computed from all other fields in this record.
        /// Used by <see cref="VerifyIntegrity"/> to detect any unauthorized modifications.
        /// </summary>
        public required string RecordHash { get; init; }

        /// <summary>
        /// Computes a SHA-256 hash from all record fields (excluding RecordHash itself) using a
        /// canonical string representation with sorted field names and UTC timestamps.
        /// </summary>
        /// <param name="escalationId">The escalation identifier.</param>
        /// <param name="requestedBy">The requesting actor identity.</param>
        /// <param name="reason">The escalation reason.</param>
        /// <param name="affectedFeatureId">The affected feature identifier.</param>
        /// <param name="state">The escalation state.</param>
        /// <param name="requestedAt">When the escalation was requested.</param>
        /// <param name="activatedAt">When the escalation was activated, or null.</param>
        /// <param name="resolvedAt">When the escalation was resolved, or null.</param>
        /// <param name="resolvedBy">Who resolved the escalation, or null.</param>
        /// <param name="overrideWindow">The override window duration.</param>
        /// <param name="previousPolicySnapshot">Previous policy JSON, or null.</param>
        /// <param name="overridePolicySnapshot">Override policy JSON, or null.</param>
        /// <returns>Lowercase hex-encoded SHA-256 hash string.</returns>
        public static string ComputeHash(
            string escalationId,
            string requestedBy,
            string reason,
            string affectedFeatureId,
            EscalationState state,
            DateTimeOffset requestedAt,
            DateTimeOffset? activatedAt,
            DateTimeOffset? resolvedAt,
            string? resolvedBy,
            TimeSpan overrideWindow,
            string? previousPolicySnapshot,
            string? overridePolicySnapshot)
        {
            // Canonical string with sorted field names and UTC timestamps for deterministic hashing
            var sb = new StringBuilder(1024);
            sb.Append("AffectedFeatureId=").Append(affectedFeatureId ?? string.Empty).Append('|');
            sb.Append("ActivatedAt=").Append(FormatTimestamp(activatedAt)).Append('|');
            sb.Append("EscalationId=").Append(escalationId ?? string.Empty).Append('|');
            sb.Append("OverridePolicySnapshot=").Append(overridePolicySnapshot ?? string.Empty).Append('|');
            sb.Append("OverrideWindow=").Append(overrideWindow.TotalMilliseconds.ToString(CultureInfo.InvariantCulture)).Append('|');
            sb.Append("PreviousPolicySnapshot=").Append(previousPolicySnapshot ?? string.Empty).Append('|');
            sb.Append("Reason=").Append(reason ?? string.Empty).Append('|');
            sb.Append("RequestedAt=").Append(FormatTimestamp(requestedAt)).Append('|');
            sb.Append("RequestedBy=").Append(requestedBy ?? string.Empty).Append('|');
            sb.Append("ResolvedAt=").Append(FormatTimestamp(resolvedAt)).Append('|');
            sb.Append("ResolvedBy=").Append(resolvedBy ?? string.Empty).Append('|');
            sb.Append("State=").Append(((int)state).ToString(CultureInfo.InvariantCulture));

            var bytes = Encoding.UTF8.GetBytes(sb.ToString());

            using var sha256 = SHA256.Create();
            var hashBytes = sha256.ComputeHash(bytes);
            return ConvertToHex(hashBytes);
        }

        /// <summary>
        /// Verifies the integrity of this record by recomputing the SHA-256 hash from its fields
        /// and comparing it to the stored <see cref="RecordHash"/>.
        /// </summary>
        /// <returns>
        /// <c>true</c> if the recomputed hash matches <see cref="RecordHash"/>, indicating the
        /// record has not been tampered with; <c>false</c> otherwise.
        /// </returns>
        public bool VerifyIntegrity()
        {
            var computed = ComputeHash(
                EscalationId,
                RequestedBy,
                Reason,
                AffectedFeatureId,
                State,
                RequestedAt,
                ActivatedAt,
                ResolvedAt,
                ResolvedBy,
                OverrideWindow,
                PreviousPolicySnapshot,
                OverridePolicySnapshot);

            return string.Equals(computed, RecordHash, StringComparison.Ordinal);
        }

        private static string FormatTimestamp(DateTimeOffset? timestamp)
        {
            return timestamp?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty;
        }

        private static string FormatTimestamp(DateTimeOffset timestamp)
        {
            return timestamp.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        }

        private static string ConvertToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            for (var i = 0; i < bytes.Length; i++)
            {
                sb.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// Configuration for the emergency escalation system, controlling override windows,
    /// concurrency limits, and validation requirements.
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 75: Emergency Escalation (AUTH-01, AUTH-02, AUTH-03)")]
    public sealed record EscalationConfiguration
    {
        /// <summary>
        /// Default duration for the AI override window when no explicit window is specified.
        /// After activation, the escalation auto-reverts if not confirmed or reverted within this period.
        /// Default: 15 minutes.
        /// </summary>
        public TimeSpan DefaultOverrideWindow { get; init; } = TimeSpan.FromMinutes(15);

        /// <summary>
        /// Maximum allowed override window duration. Requests exceeding this are rejected.
        /// Default: 4 hours.
        /// </summary>
        public TimeSpan MaxOverrideWindow { get; init; } = TimeSpan.FromHours(4);

        /// <summary>
        /// Maximum number of Active escalations allowed simultaneously. Prevents abuse by
        /// limiting concurrent AI overrides. Default: 3.
        /// </summary>
        public int MaxConcurrentEscalations { get; init; } = 3;

        /// <summary>
        /// When true, the escalation reason must be non-empty for RequestEscalationAsync to succeed.
        /// Default: true.
        /// </summary>
        public bool RequireReason { get; init; } = true;
    }

    /// <summary>
    /// Defines the contract for the emergency escalation service, which manages the lifecycle
    /// of time-bounded AI overrides through a strict state machine (Pending -> Active -> Confirmed|Reverted|TimedOut).
    /// <para>
    /// Implementations must ensure:
    /// <list type="bullet">
    /// <item>All <see cref="EscalationRecord"/> instances are immutable with valid SHA-256 hashes.</item>
    /// <item>State transitions follow the allowed paths (no skipping states, no re-entering terminal states).</item>
    /// <item>Timeout checking auto-reverts expired Active escalations.</item>
    /// <item>Concurrent escalation limits are enforced.</item>
    /// </list>
    /// </para>
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 75: Emergency Escalation (AUTH-01, AUTH-02, AUTH-03)")]
    public interface IEscalationService
    {
        /// <summary>
        /// Requests a new emergency escalation, creating a record in the Pending state.
        /// Validates the reason (if required), override window bounds, and concurrent escalation limits.
        /// </summary>
        /// <param name="requestedBy">Identity of the actor requesting the escalation.</param>
        /// <param name="reason">Justification for the escalation.</param>
        /// <param name="featureId">Identifier of the feature/policy to override.</param>
        /// <param name="overrideWindow">
        /// Duration of the override window, or null to use <see cref="EscalationConfiguration.DefaultOverrideWindow"/>.
        /// Must not exceed <see cref="EscalationConfiguration.MaxOverrideWindow"/>.
        /// </param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The newly created <see cref="EscalationRecord"/> in Pending state.</returns>
        /// <exception cref="ArgumentException">Thrown when validation fails (empty reason, excessive window).</exception>
        /// <exception cref="InvalidOperationException">Thrown when concurrent escalation limit is reached.</exception>
        Task<EscalationRecord> RequestEscalationAsync(
            string requestedBy,
            string reason,
            string featureId,
            TimeSpan? overrideWindow = null,
            CancellationToken ct = default);

        /// <summary>
        /// Activates a Pending escalation, transitioning it to Active state with policy snapshots.
        /// Records an AiEmergency authority decision via <see cref="IAuthorityResolver"/>.
        /// </summary>
        /// <param name="escalationId">The escalation to activate.</param>
        /// <param name="policySnapshotJson">JSON snapshot of the current policy state (for revert).</param>
        /// <param name="overridePolicyJson">JSON snapshot of the override policy to apply.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The <see cref="EscalationRecord"/> in Active state.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the escalation is not in Pending state.</exception>
        Task<EscalationRecord> ActivateEscalationAsync(
            string escalationId,
            string policySnapshotJson,
            string overridePolicyJson,
            CancellationToken ct = default);

        /// <summary>
        /// Confirms an Active escalation, making the override permanent (terminal state).
        /// </summary>
        /// <param name="escalationId">The escalation to confirm.</param>
        /// <param name="confirmedBy">Identity of the administrator confirming the escalation.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The <see cref="EscalationRecord"/> in Confirmed state.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the escalation is not in Active state.</exception>
        Task<EscalationRecord> ConfirmEscalationAsync(
            string escalationId,
            string confirmedBy,
            CancellationToken ct = default);

        /// <summary>
        /// Reverts an Active escalation, restoring the prior policy state (terminal state).
        /// </summary>
        /// <param name="escalationId">The escalation to revert.</param>
        /// <param name="revertedBy">Identity of the administrator reverting the escalation.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The <see cref="EscalationRecord"/> in Reverted state.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the escalation is not in Active state.</exception>
        Task<EscalationRecord> RevertEscalationAsync(
            string escalationId,
            string revertedBy,
            CancellationToken ct = default);

        /// <summary>
        /// Retrieves the most recent state record for the specified escalation.
        /// </summary>
        /// <param name="escalationId">The escalation identifier.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The most recent <see cref="EscalationRecord"/>, or throws if not found.</returns>
        /// <exception cref="KeyNotFoundException">Thrown when no escalation exists with the specified ID.</exception>
        Task<EscalationRecord> GetEscalationAsync(
            string escalationId,
            CancellationToken ct = default);

        /// <summary>
        /// Returns all escalations currently in the Active state.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Read-only list of Active escalation records.</returns>
        Task<IReadOnlyList<EscalationRecord>> GetActiveEscalationsAsync(CancellationToken ct = default);

        /// <summary>
        /// Checks all Active escalations for timeout expiry. Any escalation whose
        /// ActivatedAt + OverrideWindow has elapsed is transitioned to TimedOut state.
        /// Should be called periodically (e.g., by a background timer).
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        Task CheckTimeoutsAsync(CancellationToken ct = default);
    }
}
