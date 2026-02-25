using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.ChaosVaccination
{
    /// <summary>
    /// Core engine contract for fault injection in the chaos vaccination system.
    /// Manages experiment lifecycle: validation, execution, monitoring, and abort.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 61: Chaos vaccination types")]
    public interface IChaosInjectionEngine
    {
        /// <summary>
        /// Executes a chaos experiment, injecting the specified fault and collecting results.
        /// Safety checks are run before injection begins.
        /// </summary>
        /// <param name="experiment">The experiment definition to execute.</param>
        /// <param name="ct">Cancellation token for the operation.</param>
        /// <returns>The result of the experiment execution.</returns>
        Task<ChaosExperimentResult> ExecuteExperimentAsync(ChaosExperiment experiment, CancellationToken ct = default);

        /// <summary>
        /// Aborts a currently running experiment.
        /// </summary>
        /// <param name="experimentId">The ID of the experiment to abort.</param>
        /// <param name="reason">The reason for aborting the experiment.</param>
        /// <param name="ct">Cancellation token for the operation.</param>
        Task AbortExperimentAsync(string experimentId, string reason, CancellationToken ct = default);

        /// <summary>
        /// Gets all currently running experiments.
        /// </summary>
        /// <param name="ct">Cancellation token for the operation.</param>
        /// <returns>A list of results for all running experiments.</returns>
        Task<IReadOnlyList<ChaosExperimentResult>> GetRunningExperimentsAsync(CancellationToken ct = default);

        /// <summary>
        /// Validates all safety checks for an experiment without executing it.
        /// </summary>
        /// <param name="experiment">The experiment to validate.</param>
        /// <param name="ct">Cancellation token for the operation.</param>
        /// <returns>True if all safety checks pass; false otherwise.</returns>
        Task<bool> ValidateExperimentSafetyAsync(ChaosExperiment experiment, CancellationToken ct = default);

        /// <summary>
        /// Raised when a significant experiment lifecycle event occurs.
        /// </summary>
        event Action<ChaosExperimentEvent>? OnExperimentEvent;
    }

    /// <summary>
    /// Types of events that occur during a chaos experiment lifecycle.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 61: Chaos vaccination types")]
    public enum ChaosExperimentEventType
    {
        /// <summary>The experiment has started.</summary>
        Started,

        /// <summary>The fault has been injected.</summary>
        FaultInjected,

        /// <summary>A safety check failed during execution.</summary>
        SafetyCheckFailed,

        /// <summary>The blast radius limit was exceeded.</summary>
        BlastRadiusExceeded,

        /// <summary>The experiment completed (successfully or not).</summary>
        Completed,

        /// <summary>The experiment was aborted.</summary>
        Aborted
    }

    /// <summary>
    /// Event raised during chaos experiment lifecycle transitions.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 61: Chaos vaccination types")]
    public record ChaosExperimentEvent
    {
        /// <summary>
        /// The ID of the experiment that generated this event.
        /// </summary>
        public required string ExperimentId { get; init; }

        /// <summary>
        /// The type of lifecycle event.
        /// </summary>
        public required ChaosExperimentEventType EventType { get; init; }

        /// <summary>
        /// When the event occurred.
        /// </summary>
        public required DateTimeOffset Timestamp { get; init; }

        /// <summary>
        /// Additional detail about the event.
        /// </summary>
        public string? Detail { get; init; }
    }
}
