using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Policy;

namespace DataWarehouse.SDK.Infrastructure.Authority
{
    /// <summary>
    /// Unified entry point integrating all authority chain subsystems: authority resolution,
    /// emergency escalation, N-of-M quorum approval, hardware token validation, and the dead man's switch.
    /// Satisfies AUTH-01 through AUTH-09 requirements.
    /// <para>
    /// Provides high-level convenience methods that orchestrate multiple subsystems for common
    /// operations (authority resolution, protected actions, hardware-token-backed approval,
    /// emergency override, and periodic maintenance).
    /// </para>
    /// <para>
    /// Use <see cref="CreateDefault"/> to wire up all components with default configurations,
    /// or inject individual subsystems via the constructor for custom configurations.
    /// </para>
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 75: Authority Chain Facade (AUTH-01 through AUTH-09)")]
    public sealed class AuthorityChainFacade
    {
        /// <summary>Authority resolution engine for resolving competing decisions by priority ordering.</summary>
        public IAuthorityResolver Authority { get; }

        /// <summary>Emergency escalation service for time-bounded AI overrides with countdown timers.</summary>
        public IEscalationService Escalation { get; }

        /// <summary>Quorum service for N-of-M super admin approval of protected actions.</summary>
        public IQuorumService Quorum { get; }

        /// <summary>Hardware token validator for YubiKey (OTP/FIDO2), smart card (PIV/CAC), and TPM attestation.</summary>
        public IHardwareTokenValidator HardwareTokens { get; }

        /// <summary>Dead man's switch that auto-locks the system after prolonged inactivity.</summary>
        public DeadManSwitch DeadManSwitch { get; }

        /// <summary>
        /// Initializes a new instance of <see cref="AuthorityChainFacade"/> with all authority subsystems.
        /// </summary>
        /// <param name="authorityResolver">Authority resolution engine.</param>
        /// <param name="escalationService">Emergency escalation service.</param>
        /// <param name="quorumService">Quorum approval service.</param>
        /// <param name="hardwareTokenValidator">Hardware token validator.</param>
        /// <param name="deadManSwitch">Dead man's switch for inactivity monitoring.</param>
        /// <exception cref="ArgumentNullException">Thrown when any parameter is null.</exception>
        public AuthorityChainFacade(
            IAuthorityResolver authorityResolver,
            IEscalationService escalationService,
            IQuorumService quorumService,
            IHardwareTokenValidator hardwareTokenValidator,
            DeadManSwitch deadManSwitch)
        {
            Authority = authorityResolver ?? throw new ArgumentNullException(nameof(authorityResolver));
            Escalation = escalationService ?? throw new ArgumentNullException(nameof(escalationService));
            Quorum = quorumService ?? throw new ArgumentNullException(nameof(quorumService));
            HardwareTokens = hardwareTokenValidator ?? throw new ArgumentNullException(nameof(hardwareTokenValidator));
            DeadManSwitch = deadManSwitch ?? throw new ArgumentNullException(nameof(deadManSwitch));
        }

        /// <summary>
        /// Resolves authority for the specified action, considering the dead man's switch state.
        /// If the system is locked by the dead man's switch, only Quorum-level decisions can proceed.
        /// </summary>
        /// <param name="action">The action to resolve authority for (e.g., "OverrideEncryptionPolicy").</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>
        /// An <see cref="AuthorityResolution"/> containing the winning decision and overridden decisions.
        /// Returns a SystemDefaults-level resolution if no decisions exist for the action.
        /// </returns>
        public async Task<AuthorityResolution> ResolveAuthorityAsync(string action, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            // Check dead man's switch status -- if locked, only Quorum-level decisions proceed
            var switchStatus = await DeadManSwitch.CheckStatusAsync(ct).ConfigureAwait(false);

            // Gather all active decisions for this action from the authority resolver
            var decisions = new List<AuthorityDecision>();
            if (Authority is AuthorityResolutionEngine engine)
            {
                var history = engine.GetDecisionHistory(action);
                foreach (var decision in history)
                {
                    if (!decision.IsActive)
                        continue;

                    // If system is locked, only allow Quorum-level decisions (priority 0)
                    if (switchStatus.IsLocked && decision.AuthorityPriority > 0)
                        continue;

                    decisions.Add(decision);
                }
            }
            else
            {
                // Non-engine resolver: log that decision history is unavailable;
                // will fall through to SystemDefaults below rather than silently using wrong authority.
                System.Diagnostics.Debug.WriteLine(
                    $"AuthorityChainFacade: IAuthorityResolver implementation '{Authority.GetType().Name}' " +
                    $"does not support GetDecisionHistory for action '{action}'. Falling back to SystemDefaults.");
            }

            // If no decisions found, create a SystemDefaults decision as baseline
            if (decisions.Count == 0)
            {
                var defaultDecision = await Authority.RecordDecisionAsync(
                    "SystemDefaults",
                    action,
                    actorId: null,
                    reason: switchStatus.IsLocked
                        ? "System locked by dead man's switch; default policy applied"
                        : "No explicit authority decision; system defaults apply",
                    ct: ct).ConfigureAwait(false);

                decisions.Add(defaultDecision);
            }

            // Resolve using authority chain ordering
            return await Authority.ResolveAsync(action, decisions, chain: null, ct: ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Initiates a quorum request for a protected action. Records activity on the dead man's switch
        /// to indicate super admin engagement.
        /// </summary>
        /// <param name="action">The protected action requiring quorum approval.</param>
        /// <param name="requestedBy">ID of the user requesting the action.</param>
        /// <param name="justification">Human-readable justification for the request.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The newly created <see cref="QuorumRequest"/> in Collecting state.</returns>
        public async Task<QuorumRequest> RequestProtectedActionAsync(
            QuorumAction action,
            string requestedBy,
            string justification,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            // Record activity on the dead man's switch
            DeadManSwitch.RecordActivity("QuorumApproval", requestedBy);

            // Delegate to quorum service
            return await Quorum.InitiateQuorumAsync(action, requestedBy, justification, parameters: null, ct: ct)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Approves a quorum request using a hardware security token for strong authentication.
        /// Creates a challenge, validates the token response, and records the approval if valid.
        /// </summary>
        /// <param name="requestId">ID of the quorum request to approve.</param>
        /// <param name="approverId">ID of the approving super admin.</param>
        /// <param name="approverName">Display name of the approving super admin.</param>
        /// <param name="tokenType">Type of hardware token used for authentication.</param>
        /// <param name="tokenResponse">The hardware token's response data.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The updated <see cref="QuorumRequest"/> with the new approval recorded.</returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when hardware token validation fails (includes the failure reason from the token validator).
        /// </exception>
        public async Task<QuorumRequest> ApproveWithHardwareTokenAsync(
            string requestId,
            string approverId,
            string approverName,
            HardwareTokenType tokenType,
            string tokenResponse,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            // Create challenge and validate token
            var challenge = await HardwareTokens.CreateChallengeAsync(tokenType, ct).ConfigureAwait(false);
            var validationResult = await HardwareTokens.ValidateResponseAsync(challenge.ChallengeId, tokenResponse, ct)
                .ConfigureAwait(false);

            if (!validationResult.IsValid)
                throw new InvalidOperationException($"Hardware token validation failed: {validationResult.FailureReason}");

            // Token is valid -- record the approval with the token type as the authentication method
            var updatedRequest = await Quorum.ApproveAsync(
                requestId,
                approverId,
                approverName,
                method: tokenType.ToString(),
                comment: validationResult.TokenSerialNumber != null
                    ? $"Token serial: {validationResult.TokenSerialNumber}"
                    : null,
                ct: ct).ConfigureAwait(false);

            // Record activity on the dead man's switch
            DeadManSwitch.RecordActivity("QuorumApproval", approverId);

            return updatedRequest;
        }

        /// <summary>
        /// Initiates an emergency override via the escalation service. Records activity on the
        /// dead man's switch and delegates to the escalation service for Pending state creation.
        /// </summary>
        /// <param name="requestedBy">Identity of the actor requesting the emergency override.</param>
        /// <param name="reason">Justification for the emergency override.</param>
        /// <param name="featureId">Identifier of the feature or policy to override.</param>
        /// <param name="window">Optional override window duration (defaults to escalation service default).</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The newly created <see cref="EscalationRecord"/> in Pending state.</returns>
        public async Task<EscalationRecord> EmergencyOverrideAsync(
            string requestedBy,
            string reason,
            string featureId,
            TimeSpan? window = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            // Record activity on the dead man's switch
            DeadManSwitch.RecordActivity("EscalationConfirm", requestedBy);

            // Delegate to escalation service
            return await Escalation.RequestEscalationAsync(requestedBy, reason, featureId, window, ct)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Runs periodic maintenance across all authority subsystems:
        /// escalation timeout checking, quorum expiration checking, and dead man's switch enforcement.
        /// Should be called periodically by a background timer (e.g., every minute).
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        public async Task RunMaintenanceAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            // Check escalation timeouts (auto-revert expired Active escalations)
            await Escalation.CheckTimeoutsAsync(ct).ConfigureAwait(false);

            // Check quorum expirations (expire requests past approval window, execute after cooling-off)
            await Quorum.CheckExpirationsAsync(ct).ConfigureAwait(false);

            // Check and enforce the dead man's switch (auto-lock after inactivity threshold)
            await DeadManSwitch.CheckAndEnforceAsync(ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Creates a fully wired <see cref="AuthorityChainFacade"/> with default configurations
        /// for all subsystems. Suitable for standalone operation with no external dependencies.
        /// </summary>
        /// <param name="authorityConfig">Authority resolution configuration, or null for defaults.</param>
        /// <param name="escalationConfig">Escalation service configuration, or null for defaults.</param>
        /// <param name="quorumConfig">Quorum service configuration, or null for defaults (3-of-5 quorum).</param>
        /// <param name="deadManConfig">Dead man's switch configuration, or null for defaults (30-day threshold).</param>
        /// <returns>A fully wired <see cref="AuthorityChainFacade"/> with all subsystems connected.</returns>
        public static AuthorityChainFacade CreateDefault(
            AuthorityConfiguration? authorityConfig = null,
            EscalationConfiguration? escalationConfig = null,
            QuorumConfiguration? quorumConfig = null,
            DeadManSwitchConfiguration? deadManConfig = null)
        {
            // Default quorum config: 3-of-5 with standard member placeholders
            var effectiveQuorumConfig = quorumConfig ?? new QuorumConfiguration
            {
                RequiredApprovals = 3,
                TotalMembers = 5,
                MemberIds = new[] { "admin-1", "admin-2", "admin-3", "admin-4", "admin-5" }
            };

            // Wire up authority resolution
            var authorityResolver = new AuthorityResolutionEngine(authorityConfig);

            // Wire up escalation subsystem
            var escalationStore = new EscalationRecordStore();
            var escalationStateMachine = new EscalationStateMachine(authorityResolver, escalationStore, escalationConfig);

            // Wire up quorum subsystem
            var vetoHandler = new QuorumVetoHandler(effectiveQuorumConfig);
            var quorumEngine = new QuorumEngine(authorityResolver, effectiveQuorumConfig, vetoHandler);

            // Wire up hardware token validator
            var hardwareTokenValidator = new HardwareTokenValidator();

            // Wire up dead man's switch
            var deadManSwitch = new DeadManSwitch(authorityResolver, deadManConfig);

            return new AuthorityChainFacade(
                authorityResolver,
                escalationStateMachine,
                quorumEngine,
                hardwareTokenValidator,
                deadManSwitch);
        }
    }
}
