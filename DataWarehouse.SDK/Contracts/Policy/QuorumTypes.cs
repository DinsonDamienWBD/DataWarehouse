using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Policy
{
    /// <summary>
    /// Represents the lifecycle state of a quorum request.
    /// Terminal states: <see cref="Vetoed"/>, <see cref="Executed"/>, <see cref="Expired"/>, <see cref="Denied"/>.
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 75: Quorum System (AUTH-04, AUTH-05, AUTH-07)")]
    public enum QuorumRequestState
    {
        /// <summary>Gathering approvals from quorum members; not yet reached N-of-M threshold.</summary>
        [Description("Gathering approvals from quorum members")]
        Collecting = 0,

        /// <summary>N-of-M approvals reached; action is authorized (non-destructive actions execute immediately).</summary>
        [Description("N-of-M approvals reached; action authorized")]
        Approved = 1,

        /// <summary>Approved but in 24hr cooling-off period for destructive actions; any super admin can veto.</summary>
        [Description("Approved but in cooling-off period; any super admin can veto")]
        CoolingOff = 2,

        /// <summary>Any super admin vetoed during cooling-off period (terminal).</summary>
        [Description("Vetoed by super admin during cooling-off (terminal)")]
        Vetoed = 3,

        /// <summary>Action was executed after quorum approval and cooling-off (if applicable) completed (terminal).</summary>
        [Description("Action executed after quorum approval (terminal)")]
        Executed = 4,

        /// <summary>Approval window expired before reaching quorum (terminal).</summary>
        [Description("Approval window expired before reaching quorum (terminal)")]
        Expired = 5,

        /// <summary>Explicitly denied (terminal).</summary>
        [Description("Explicitly denied (terminal)")]
        Denied = 6
    }

    /// <summary>
    /// Records a single super admin approval within a quorum request.
    /// Each approval is immutable and timestamped for audit purposes.
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 75: Quorum System (AUTH-04, AUTH-05, AUTH-07)")]
    public sealed record QuorumApproval
    {
        /// <summary>Unique identifier for this approval (GUID).</summary>
        public required string ApprovalId { get; init; }

        /// <summary>Super admin ID who approved.</summary>
        public required string ApproverId { get; init; }

        /// <summary>Display name of the approving super admin.</summary>
        public required string ApproverDisplayName { get; init; }

        /// <summary>UTC timestamp when approval was granted.</summary>
        public required DateTimeOffset ApprovedAt { get; init; }

        /// <summary>Authentication method used for approval (e.g., "password", "yubikey", "smartcard"). Phase 75-04 will use this.</summary>
        public string? ApprovalMethod { get; init; }

        /// <summary>Optional comment from the approver.</summary>
        public string? Comment { get; init; }
    }

    /// <summary>
    /// Records a veto by a super admin during the cooling-off period for a destructive quorum action.
    /// A veto terminates the request immediately.
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 75: Quorum System (AUTH-04, AUTH-05, AUTH-07)")]
    public sealed record QuorumVeto
    {
        /// <summary>Unique identifier for this veto (GUID).</summary>
        public required string VetoId { get; init; }

        /// <summary>Super admin ID who exercised the veto.</summary>
        public required string VetoedBy { get; init; }

        /// <summary>UTC timestamp when the veto was exercised.</summary>
        public required DateTimeOffset VetoedAt { get; init; }

        /// <summary>Mandatory reason explaining why the action was vetoed.</summary>
        public required string Reason { get; init; }
    }

    /// <summary>
    /// Represents a quorum request tracking N-of-M super admin approval for a protected action.
    /// Manages its full lifecycle from <see cref="QuorumRequestState.Collecting"/> through
    /// terminal states (<see cref="QuorumRequestState.Executed"/>, <see cref="QuorumRequestState.Vetoed"/>,
    /// <see cref="QuorumRequestState.Expired"/>, <see cref="QuorumRequestState.Denied"/>).
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 75: Quorum System (AUTH-04, AUTH-05, AUTH-07)")]
    public sealed record QuorumRequest
    {
        /// <summary>Unique identifier for this quorum request (GUID).</summary>
        public required string RequestId { get; init; }

        /// <summary>The protected action requiring quorum approval (from <see cref="QuorumAction"/> enum).</summary>
        public required QuorumAction Action { get; init; }

        /// <summary>ID of the user who initiated this quorum request.</summary>
        public required string RequestedBy { get; init; }

        /// <summary>Human-readable justification for the requested action.</summary>
        public required string Justification { get; init; }

        /// <summary>UTC timestamp when the request was created.</summary>
        public required DateTimeOffset RequestedAt { get; init; }

        /// <summary>Current lifecycle state of the quorum request.</summary>
        public required QuorumRequestState State { get; init; }

        /// <summary>The N in N-of-M: number of approvals required to reach quorum.</summary>
        public required int RequiredApprovals { get; init; }

        /// <summary>The M in N-of-M: total number of quorum members eligible to approve.</summary>
        public required int TotalMembers { get; init; }

        /// <summary>List of approvals collected so far. Empty until approvals begin.</summary>
        public IReadOnlyList<QuorumApproval> Approvals { get; init; } = Array.Empty<QuorumApproval>();

        /// <summary>Non-null if a super admin vetoed this request during cooling-off.</summary>
        public QuorumVeto? Veto { get; init; }

        /// <summary>Time window within which all required approvals must be gathered.</summary>
        public required TimeSpan ApprovalWindow { get; init; }

        /// <summary>Cooling-off period for destructive actions (24hr default). Non-destructive actions skip cooling-off.</summary>
        public required TimeSpan CoolingOffPeriod { get; init; }

        /// <summary>UTC timestamp when cooling-off ends. Set when entering <see cref="QuorumRequestState.CoolingOff"/>.</summary>
        public DateTimeOffset? CoolingOffEndsAt { get; init; }

        /// <summary>UTC timestamp when the action was executed. Set when entering <see cref="QuorumRequestState.Executed"/>.</summary>
        public DateTimeOffset? ExecutedAt { get; init; }

        /// <summary>Action-specific parameters (e.g., VDE ID for DeleteVde, key identifiers for ExportKeys).</summary>
        public Dictionary<string, string>? ActionParameters { get; init; }
    }

    /// <summary>
    /// Configuration for the quorum system defining approval thresholds, membership,
    /// timing windows, and which actions are considered destructive (requiring cooling-off).
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 75: Quorum System (AUTH-04, AUTH-05, AUTH-07)")]
    public sealed record QuorumConfiguration
    {
        /// <summary>The N in N-of-M: number of approvals required (e.g., 3 for a 3-of-5 quorum).</summary>
        public required int RequiredApprovals { get; init; }

        /// <summary>The M in N-of-M: total quorum members (e.g., 5 for a 3-of-5 quorum).</summary>
        public required int TotalMembers { get; init; }

        /// <summary>Maximum time to collect all required approvals. Default: 24 hours.</summary>
        public TimeSpan ApprovalWindow { get; init; } = TimeSpan.FromHours(24);

        /// <summary>Cooling-off period for destructive actions. Default: 24 hours.</summary>
        public TimeSpan DestructiveCoolingOff { get; init; } = TimeSpan.FromHours(24);

        /// <summary>
        /// Actions classified as destructive that require a cooling-off period after approval.
        /// Default: DeleteVde, ExportKeys, DisableAudit, DisableAi.
        /// </summary>
        public QuorumAction[] DestructiveActions { get; init; } = new[]
        {
            QuorumAction.DeleteVde,
            QuorumAction.ExportKeys,
            QuorumAction.DisableAudit,
            QuorumAction.DisableAi
        };

        /// <summary>List of super admin IDs eligible to participate in quorum voting.</summary>
        public IReadOnlyList<string> MemberIds { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// Defines the contract for the quorum service managing N-of-M super admin approval
    /// for protected actions. Handles the full lifecycle: initiation, approval collection,
    /// cooling-off with veto capability, expiration, and execution.
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 75: Quorum System (AUTH-04, AUTH-05, AUTH-07)")]
    public interface IQuorumService
    {
        /// <summary>
        /// Initiates a new quorum request for a protected action.
        /// </summary>
        /// <param name="action">The protected action requiring quorum approval.</param>
        /// <param name="requestedBy">ID of the user requesting the action.</param>
        /// <param name="justification">Human-readable justification for the request.</param>
        /// <param name="parameters">Optional action-specific parameters.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The newly created <see cref="QuorumRequest"/> in <see cref="QuorumRequestState.Collecting"/> state.</returns>
        Task<QuorumRequest> InitiateQuorumAsync(
            QuorumAction action,
            string requestedBy,
            string justification,
            Dictionary<string, string>? parameters = null,
            CancellationToken ct = default);

        /// <summary>
        /// Records an approval from a super admin for a pending quorum request.
        /// Transitions to <see cref="QuorumRequestState.Approved"/> or <see cref="QuorumRequestState.CoolingOff"/>
        /// when the required number of approvals is reached.
        /// </summary>
        /// <param name="requestId">ID of the quorum request to approve.</param>
        /// <param name="approverId">ID of the approving super admin (must be in MemberIds, cannot be the requester).</param>
        /// <param name="approverName">Display name of the approving super admin.</param>
        /// <param name="method">Optional authentication method (e.g., "password", "yubikey").</param>
        /// <param name="comment">Optional comment from the approver.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The updated <see cref="QuorumRequest"/>.</returns>
        Task<QuorumRequest> ApproveAsync(
            string requestId,
            string approverId,
            string approverName,
            string? method = null,
            string? comment = null,
            CancellationToken ct = default);

        /// <summary>
        /// Exercises a veto during the cooling-off period, terminating the quorum request.
        /// </summary>
        /// <param name="requestId">ID of the quorum request to veto.</param>
        /// <param name="vetoedBy">ID of the super admin exercising the veto (must be in MemberIds).</param>
        /// <param name="reason">Mandatory reason for the veto.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The updated <see cref="QuorumRequest"/> in <see cref="QuorumRequestState.Vetoed"/> state.</returns>
        Task<QuorumRequest> VetoAsync(
            string requestId,
            string vetoedBy,
            string reason,
            CancellationToken ct = default);

        /// <summary>
        /// Retrieves a quorum request by its unique identifier.
        /// </summary>
        /// <param name="requestId">The request ID to look up.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The <see cref="QuorumRequest"/>, or throws if not found.</returns>
        Task<QuorumRequest> GetRequestAsync(string requestId, CancellationToken ct = default);

        /// <summary>
        /// Returns all non-terminal quorum requests (Collecting or CoolingOff).
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Read-only list of pending quorum requests.</returns>
        Task<IReadOnlyList<QuorumRequest>> GetPendingRequestsAsync(CancellationToken ct = default);

        /// <summary>
        /// Checks all pending requests for expirations: expires requests past their approval window,
        /// and executes requests whose cooling-off period has completed without veto.
        /// Should be called periodically by a background timer.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        Task CheckExpirationsAsync(CancellationToken ct = default);

        /// <summary>
        /// Checks whether a given action requires quorum approval based on configuration.
        /// </summary>
        /// <param name="action">The action to check.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>True if the action requires quorum approval.</returns>
        Task<bool> IsActionProtectedAsync(QuorumAction action, CancellationToken ct = default);
    }
}
