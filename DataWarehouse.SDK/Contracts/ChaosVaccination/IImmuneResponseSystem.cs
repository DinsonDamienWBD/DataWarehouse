using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.ChaosVaccination
{
    /// <summary>
    /// Contract for the immune response system that learns from chaos experiments
    /// and applies automated remediation when recognized fault patterns recur.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 61: Chaos vaccination types")]
    public interface IImmuneResponseSystem
    {
        /// <summary>
        /// Attempts to recognize a fault by its signature against immune memory.
        /// Returns the matching entry if the fault has been seen before, null otherwise.
        /// </summary>
        /// <param name="signature">The fault signature to look up.</param>
        /// <param name="ct">Cancellation token for the operation.</param>
        /// <returns>The matching immune memory entry, or null if unrecognized.</returns>
        Task<ImmuneMemoryEntry?> RecognizeFaultAsync(FaultSignature signature, CancellationToken ct = default);

        /// <summary>
        /// Applies the remediation actions from an immune memory entry to resolve a recognized fault.
        /// </summary>
        /// <param name="entry">The immune memory entry containing remediation actions.</param>
        /// <param name="ct">Cancellation token for the operation.</param>
        /// <returns>True if remediation was successful; false otherwise.</returns>
        Task<bool> ApplyRemediationAsync(ImmuneMemoryEntry entry, CancellationToken ct = default);

        /// <summary>
        /// Records new immune memory from a completed chaos experiment and its successful remediation actions.
        /// </summary>
        /// <param name="result">The chaos experiment result to learn from.</param>
        /// <param name="successfulActions">The remediation actions that successfully resolved the fault.</param>
        /// <param name="ct">Cancellation token for the operation.</param>
        Task LearnFromExperimentAsync(ChaosExperimentResult result, RemediationAction[] successfulActions, CancellationToken ct = default);

        /// <summary>
        /// Gets all entries in the immune memory database.
        /// </summary>
        /// <param name="ct">Cancellation token for the operation.</param>
        /// <returns>All immune memory entries.</returns>
        Task<IReadOnlyList<ImmuneMemoryEntry>> GetImmuneMemoryAsync(CancellationToken ct = default);

        /// <summary>
        /// Removes a stale or incorrect immune memory entry by its fault signature hash.
        /// </summary>
        /// <param name="signatureHash">The hash of the fault signature to forget.</param>
        /// <param name="ct">Cancellation token for the operation.</param>
        Task ForgetAsync(string signatureHash, CancellationToken ct = default);

        /// <summary>
        /// Raised when an immune response is triggered (remediation applied).
        /// </summary>
        event Action<ImmuneResponseEvent>? OnImmuneResponse;
    }

    /// <summary>
    /// Types of remediation actions the immune response system can take.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 61: Chaos vaccination types")]
    public enum ImmuneRemediationActionType
    {
        /// <summary>Restart the affected plugin.</summary>
        RestartPlugin,

        /// <summary>Trip the circuit breaker on the affected path.</summary>
        TripCircuitBreaker,

        /// <summary>Drain active connections from the affected component.</summary>
        DrainConnections,

        /// <summary>Scale down the affected component to reduce load.</summary>
        ScaleDown,

        /// <summary>Isolate the affected node from the cluster.</summary>
        IsolateNode,

        /// <summary>Reroute traffic away from the affected component.</summary>
        RerouteTraffic,

        /// <summary>Restore the affected component from its last checkpoint.</summary>
        RestoreFromCheckpoint,

        /// <summary>Notify the operator for manual intervention.</summary>
        NotifyOperator,

        /// <summary>User-defined custom remediation action.</summary>
        Custom
    }

    /// <summary>
    /// An immune memory entry representing learned knowledge about a fault pattern
    /// and the remediation actions that resolve it.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 61: Chaos vaccination types")]
    public record ImmuneMemoryEntry
    {
        /// <summary>
        /// The fault signature this entry responds to.
        /// </summary>
        public required FaultSignature Signature { get; init; }

        /// <summary>
        /// The ordered set of remediation actions to apply when this fault is recognized.
        /// </summary>
        public RemediationAction[] RemediationActions { get; init; } = Array.Empty<RemediationAction>();

        /// <summary>
        /// The success rate of this remediation across all applications (0.0 to 1.0).
        /// </summary>
        public required double SuccessRate { get; init; }

        /// <summary>
        /// When this remediation was last applied. Null if never applied outside of learning.
        /// </summary>
        public DateTimeOffset? LastApplied { get; init; }

        /// <summary>
        /// How many times this remediation has been applied.
        /// </summary>
        public required int TimesApplied { get; init; }

        /// <summary>
        /// Average recovery time in milliseconds across all applications of this remediation.
        /// </summary>
        public required double AverageRecoveryMs { get; init; }
    }

    /// <summary>
    /// A specific remediation action to execute as part of an immune response.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 61: Chaos vaccination types")]
    public record RemediationAction
    {
        /// <summary>
        /// The type of remediation action to perform.
        /// </summary>
        public required ImmuneRemediationActionType ActionType { get; init; }

        /// <summary>
        /// The ID of the target component (plugin, node, circuit breaker, etc.).
        /// </summary>
        public required string TargetId { get; init; }

        /// <summary>
        /// Additional parameters for the remediation action.
        /// </summary>
        public IReadOnlyDictionary<string, object> Parameters { get; init; } = new Dictionary<string, object>();

        /// <summary>
        /// Execution priority. Lower values execute first.
        /// </summary>
        public int Priority { get; init; } = 0;

        /// <summary>
        /// Maximum time in milliseconds to wait for this action to complete.
        /// </summary>
        public long TimeoutMs { get; init; } = 30000;
    }

    /// <summary>
    /// Event raised when the immune response system takes action.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 61: Chaos vaccination types")]
    public record ImmuneResponseEvent
    {
        /// <summary>
        /// The hash of the fault signature that triggered the immune response.
        /// </summary>
        public required string SignatureHash { get; init; }

        /// <summary>
        /// A description of the action that was taken.
        /// </summary>
        public required string ActionTaken { get; init; }

        /// <summary>
        /// Whether the immune response was successful.
        /// </summary>
        public required bool Success { get; init; }

        /// <summary>
        /// Recovery time in milliseconds. Null if recovery was not measured.
        /// </summary>
        public long? RecoveryTimeMs { get; init; }

        /// <summary>
        /// When the immune response occurred.
        /// </summary>
        public required DateTimeOffset Timestamp { get; init; }
    }
}
