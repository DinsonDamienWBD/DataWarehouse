using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Policy;

namespace DataWarehouse.SDK.Infrastructure.Intelligence;

/// <summary>
/// Policy record controlling whether AI systems can modify their own autonomy configuration.
/// </summary>
/// <param name="AllowSelfModification">
/// Whether AI-originated requests (requester IDs starting with "ai:" or "system:ai") can
/// modify autonomy settings. Default: false (blocked).
/// </param>
/// <param name="ModificationRequiresQuorum">
/// Whether all autonomy modifications require N-of-M quorum approval via <see cref="IQuorumService"/>.
/// Default: true.
/// </param>
/// <param name="MinimumQuorumSize">
/// Minimum number of quorum members required for AI configuration changes. Default: 3.
/// </param>
[SdkCompatibility("6.0.0", Notes = "Phase 77: AI Policy Intelligence (AIPI-11)")]
public sealed record AiSelfModificationPolicy(
    bool AllowSelfModification = false,
    bool ModificationRequiresQuorum = true,
    int MinimumQuorumSize = 3
);

/// <summary>
/// Wraps <see cref="AiAutonomyConfiguration"/> with safety enforcement that prevents AI systems
/// from modifying their own autonomy levels. Optionally requires quorum approval via
/// <see cref="IQuorumService"/> for any autonomy change.
/// </summary>
/// <remarks>
/// Implements AIPI-11. The guard enforces two independent safety constraints:
/// <list type="number">
///   <item><description>
///     Self-modification block: requests from AI-originated identifiers (prefixed with "ai:" or
///     "system:ai") are rejected immediately when <see cref="AiSelfModificationPolicy.AllowSelfModification"/>
///     is false.
///   </description></item>
///   <item><description>
///     Quorum enforcement: when <see cref="AiSelfModificationPolicy.ModificationRequiresQuorum"/> is true,
///     all modification requests are submitted to <see cref="IQuorumService"/> for N-of-M approval before
///     the change is applied to the inner configuration.
///   </description></item>
/// </list>
/// Read operations (GetAutonomy) are always allowed without guard intervention.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 77: AI Policy Intelligence (AIPI-11)")]
public sealed class AiSelfModificationGuard
{
    private readonly AiAutonomyConfiguration _innerConfig;
    private readonly IQuorumService? _quorumService;
    private readonly AiSelfModificationPolicy _policy;

    private long _blockedSelfModificationAttempts;
    private long _quorumApprovedChanges;
    private long _quorumDeniedChanges;

    /// <summary>
    /// Number of AI self-modification attempts that were blocked by the guard.
    /// </summary>
    public long BlockedSelfModificationAttempts => Interlocked.Read(ref _blockedSelfModificationAttempts);

    /// <summary>
    /// Number of configuration changes that were approved through quorum.
    /// </summary>
    public long QuorumApprovedChanges => Interlocked.Read(ref _quorumApprovedChanges);

    /// <summary>
    /// Number of configuration changes that were denied by quorum.
    /// </summary>
    public long QuorumDeniedChanges => Interlocked.Read(ref _quorumDeniedChanges);

    /// <summary>
    /// Creates a new self-modification guard wrapping the specified autonomy configuration.
    /// </summary>
    /// <param name="innerConfig">The underlying autonomy configuration to protect.</param>
    /// <param name="quorumService">
    /// Optional quorum service for N-of-M approval. If null and quorum is required, modifications
    /// are denied (fail-closed).
    /// </param>
    /// <param name="policy">The self-modification policy controlling guard behavior.</param>
    public AiSelfModificationGuard(
        AiAutonomyConfiguration innerConfig,
        IQuorumService? quorumService,
        AiSelfModificationPolicy policy)
    {
        _innerConfig = innerConfig ?? throw new ArgumentNullException(nameof(innerConfig));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _quorumService = quorumService;
    }

    /// <summary>
    /// Gets the configured autonomy level for a specific feature at a specific policy level.
    /// Read operations are always allowed without guard intervention.
    /// </summary>
    /// <param name="featureId">The feature identifier.</param>
    /// <param name="level">The policy level.</param>
    /// <returns>The configured <see cref="AiAutonomyLevel"/>.</returns>
    public AiAutonomyLevel GetAutonomy(string featureId, PolicyLevel level)
    {
        return _innerConfig.GetAutonomy(featureId, level);
    }

    /// <summary>
    /// Attempts to modify the autonomy level for a specific feature and policy level.
    /// Enforces self-modification block and quorum requirements.
    /// </summary>
    /// <param name="requesterId">
    /// Identifier of the requester. AI-originated requests use "ai:" or "system:ai" prefixes.
    /// </param>
    /// <param name="featureId">The feature identifier to modify.</param>
    /// <param name="level">The policy level to modify.</param>
    /// <param name="newLevel">The new autonomy level to set.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the modification was applied; false if rejected.</returns>
    public async Task<bool> TryModifyAutonomyAsync(
        string requesterId,
        string featureId,
        PolicyLevel level,
        AiAutonomyLevel newLevel,
        CancellationToken ct = default)
    {
        if (requesterId is null) throw new ArgumentNullException(nameof(requesterId));
        if (featureId is null) throw new ArgumentNullException(nameof(featureId));

        // Step 1: Block AI self-modification
        if (IsAiOriginated(requesterId) && !_policy.AllowSelfModification)
        {
            Interlocked.Increment(ref _blockedSelfModificationAttempts);
            return false;
        }

        // Step 2: Quorum enforcement
        if (_policy.ModificationRequiresQuorum)
        {
            if (_quorumService is null)
            {
                // Fail-closed: no quorum service means no changes allowed
                Interlocked.Increment(ref _quorumDeniedChanges);
                return false;
            }

            var request = await _quorumService.InitiateQuorumAsync(
                QuorumAction.OverrideAi,
                requesterId,
                $"Modify AI autonomy for {featureId}:{level} to {newLevel}",
                new Dictionary<string, string>
                {
                    ["featureId"] = featureId,
                    ["level"] = level.ToString(),
                    ["newAutonomyLevel"] = newLevel.ToString()
                },
                ct).ConfigureAwait(false);

            // The quorum request is now in Collecting state.
            // In a real system, approvals would be gathered asynchronously.
            // Here we check the initial state -- if it was immediately approved
            // (e.g., single-member quorum), apply; otherwise deny for now.
            if (request.State == QuorumRequestState.Approved ||
                request.State == QuorumRequestState.Executed)
            {
                _innerConfig.SetAutonomy(featureId, level, newLevel);
                Interlocked.Increment(ref _quorumApprovedChanges);
                return true;
            }

            // Quorum is pending (Collecting/CoolingOff) -- treat as denied for this call
            Interlocked.Increment(ref _quorumDeniedChanges);
            return false;
        }

        // Step 3: No quorum required and not AI self-modification -- apply directly
        _innerConfig.SetAutonomy(featureId, level, newLevel);
        return true;
    }

    /// <summary>
    /// Determines whether a requester ID originates from an AI system.
    /// AI identifiers are prefixed with "ai:" or "system:ai".
    /// </summary>
    private static bool IsAiOriginated(string requesterId)
    {
        return requesterId.StartsWith("ai:", StringComparison.OrdinalIgnoreCase) ||
               requesterId.StartsWith("system:ai", StringComparison.OrdinalIgnoreCase);
    }
}
