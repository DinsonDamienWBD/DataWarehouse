using System;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Contracts.Scaling
{
    /// <summary>
    /// Backpressure mitigation strategy that determines how a subsystem
    /// responds when load exceeds configured capacity thresholds.
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 88: Backpressure strategy enumeration")]
    public enum BackpressureStrategy
    {
        /// <summary>Evict the oldest queued items to make room for new ones.</summary>
        DropOldest,

        /// <summary>Block the producer until capacity is available (cooperative flow control).</summary>
        BlockProducer,

        /// <summary>Reject new work entirely, returning errors to callers.</summary>
        ShedLoad,

        /// <summary>Accept work at reduced fidelity (e.g., skip secondary indexes, lower compression).</summary>
        DegradeQuality,

        /// <summary>Dynamically select a strategy based on current load, latency, and queue depth.</summary>
        Adaptive
    }

    /// <summary>
    /// Current backpressure state of a subsystem, progressing from normal
    /// through warning and critical to active load shedding.
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 88: Backpressure state enumeration")]
    public enum BackpressureState
    {
        /// <summary>Load is within normal operating parameters.</summary>
        Normal,

        /// <summary>Load is approaching configured limits; proactive measures may activate.</summary>
        Warning,

        /// <summary>Load has exceeded safe thresholds; active mitigation is required.</summary>
        Critical,

        /// <summary>Subsystem is actively shedding load to recover stability.</summary>
        Shedding
    }

    /// <summary>
    /// Contract for subsystems that participate in backpressure signaling.
    /// Implementations report their current pressure state and respond to
    /// backpressure directives from the scaling infrastructure.
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 88: Backpressure-aware subsystem contract")]
    public interface IBackpressureAware
    {
        /// <summary>
        /// Gets or sets the active backpressure mitigation strategy.
        /// Can be changed at runtime to adapt to workload characteristics.
        /// </summary>
        BackpressureStrategy Strategy { get; set; }

        /// <summary>
        /// Gets the current backpressure state of this subsystem.
        /// </summary>
        BackpressureState CurrentState { get; }

        /// <summary>
        /// Raised when the backpressure state transitions between levels.
        /// Subscribers can use this to coordinate cross-subsystem responses.
        /// </summary>
        event Action<BackpressureStateChangedEventArgs>? OnBackpressureChanged;

        /// <summary>
        /// Applies a backpressure directive to this subsystem based on the provided context.
        /// The implementation should evaluate the context and transition state accordingly.
        /// </summary>
        /// <param name="context">Current load metrics driving the backpressure decision.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>A task that completes when the backpressure action has been applied.</returns>
        Task ApplyBackpressureAsync(BackpressureContext context, CancellationToken ct = default);
    }

    /// <summary>
    /// Event arguments raised when a subsystem's backpressure state changes.
    /// </summary>
    /// <param name="PreviousState">The backpressure state before the transition.</param>
    /// <param name="CurrentState">The backpressure state after the transition.</param>
    /// <param name="SubsystemName">Name of the subsystem that transitioned.</param>
    /// <param name="Timestamp">UTC timestamp when the transition occurred.</param>
    [SdkCompatibility("6.0.0", Notes = "Phase 88: Backpressure state change event")]
    public record BackpressureStateChangedEventArgs(
        BackpressureState PreviousState,
        BackpressureState CurrentState,
        string SubsystemName,
        DateTime Timestamp);

    /// <summary>
    /// Load metrics provided to <see cref="IBackpressureAware.ApplyBackpressureAsync"/>
    /// to drive backpressure decisions.
    /// </summary>
    /// <param name="CurrentLoad">Current number of in-flight operations or items.</param>
    /// <param name="MaxCapacity">Configured maximum capacity for this subsystem.</param>
    /// <param name="QueueDepth">Number of items waiting in the backpressure queue.</param>
    /// <param name="LatencyP99">P99 operation latency in milliseconds.</param>
    [SdkCompatibility("6.0.0", Notes = "Phase 88: Backpressure decision context")]
    public record BackpressureContext(
        long CurrentLoad,
        long MaxCapacity,
        int QueueDepth,
        double LatencyP99);
}
