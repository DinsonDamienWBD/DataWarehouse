using System;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Policy;

namespace DataWarehouse.SDK.Infrastructure.Authority
{
    /// <summary>
    /// Handles the cooling-off period and veto mechanism for destructive quorum actions.
    /// Destructive actions (e.g., DeleteVde, ExportKeys, DisableAudit, DisableAi) enter a
    /// configurable cooling-off period (default 24 hours) after reaching quorum approval,
    /// during which any super admin can exercise a veto to terminate the request.
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 75: Quorum System (AUTH-04, AUTH-05, AUTH-07)")]
    public sealed class QuorumVetoHandler
    {
        private readonly QuorumConfiguration _config;

        /// <summary>
        /// Initializes a new instance of <see cref="QuorumVetoHandler"/>.
        /// </summary>
        /// <param name="config">Quorum configuration defining destructive actions and cooling-off duration.</param>
        public QuorumVetoHandler(QuorumConfiguration config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        /// <summary>
        /// Determines whether the specified action is classified as destructive,
        /// requiring a cooling-off period after quorum approval.
        /// </summary>
        /// <param name="action">The quorum action to check.</param>
        /// <returns>True if the action is in the configured <see cref="QuorumConfiguration.DestructiveActions"/>.</returns>
        public bool IsDestructiveAction(QuorumAction action)
        {
            var destructive = _config.DestructiveActions;
            for (var i = 0; i < destructive.Length; i++)
            {
                if (destructive[i] == action)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Transitions an approved quorum request into the cooling-off period.
        /// Sets <see cref="QuorumRequest.CoolingOffEndsAt"/> to now + configured cooling-off duration.
        /// </summary>
        /// <param name="approved">A quorum request in <see cref="QuorumRequestState.Approved"/> state.</param>
        /// <returns>A new <see cref="QuorumRequest"/> in <see cref="QuorumRequestState.CoolingOff"/> state.</returns>
        /// <exception cref="InvalidOperationException">Thrown if the request is not in Approved state.</exception>
        public QuorumRequest EnterCoolingOff(QuorumRequest approved)
        {
            if (approved is null)
                throw new ArgumentNullException(nameof(approved));
            if (approved.State != QuorumRequestState.Approved)
                throw new InvalidOperationException(
                    $"Cannot enter cooling-off from state '{approved.State}'. Request must be in Approved state.");

            return approved with
            {
                State = QuorumRequestState.CoolingOff,
                CoolingOffEndsAt = DateTimeOffset.UtcNow + _config.DestructiveCoolingOff
            };
        }

        /// <summary>
        /// Applies a veto to a quorum request in cooling-off, transitioning it to the terminal Vetoed state.
        /// </summary>
        /// <param name="request">A quorum request in <see cref="QuorumRequestState.CoolingOff"/> state.</param>
        /// <param name="vetoedBy">ID of the super admin exercising the veto.</param>
        /// <param name="reason">Mandatory reason for the veto.</param>
        /// <returns>A new <see cref="QuorumRequest"/> in <see cref="QuorumRequestState.Vetoed"/> state.</returns>
        /// <exception cref="InvalidOperationException">Thrown if the request is not in CoolingOff state.</exception>
        /// <exception cref="ArgumentException">Thrown if vetoedBy or reason is null or empty.</exception>
        public QuorumRequest ApplyVeto(QuorumRequest request, string vetoedBy, string reason)
        {
            if (request is null)
                throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrEmpty(vetoedBy))
                throw new ArgumentException("Veto requires a super admin ID.", nameof(vetoedBy));
            if (string.IsNullOrEmpty(reason))
                throw new ArgumentException("Veto requires a reason.", nameof(reason));
            if (request.State != QuorumRequestState.CoolingOff)
                throw new InvalidOperationException(
                    $"Cannot veto a request in state '{request.State}'. Request must be in CoolingOff state.");

            var veto = new QuorumVeto
            {
                VetoId = Guid.NewGuid().ToString("D"),
                VetoedBy = vetoedBy,
                VetoedAt = DateTimeOffset.UtcNow,
                Reason = reason
            };

            return request with
            {
                State = QuorumRequestState.Vetoed,
                Veto = veto
            };
        }

        /// <summary>
        /// Checks whether a request's cooling-off period has completed without veto.
        /// </summary>
        /// <param name="request">The quorum request to check.</param>
        /// <returns>True if the request is in CoolingOff state and <see cref="QuorumRequest.CoolingOffEndsAt"/> has passed.</returns>
        public bool IsCoolingOffComplete(QuorumRequest request)
        {
            if (request is null)
                throw new ArgumentNullException(nameof(request));

            return request.State == QuorumRequestState.CoolingOff
                && request.CoolingOffEndsAt.HasValue
                && request.CoolingOffEndsAt.Value <= DateTimeOffset.UtcNow;
        }
    }
}
