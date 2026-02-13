using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Resilience
{
    /// <summary>
    /// Contract for coordinated graceful shutdown propagation (RESIL-04).
    /// Manages the shutdown sequence from kernel through plugins to strategies,
    /// ensuring all components are shut down in the correct order with proper
    /// resource cleanup.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Resilience contracts")]
    public interface IGracefulShutdown
    {
        /// <summary>
        /// Raised when a shutdown event occurs.
        /// </summary>
        event Action<ShutdownEvent>? OnShutdownEvent;

        /// <summary>
        /// Initiates a coordinated shutdown sequence.
        /// All registered shutdown handlers are called in priority order.
        /// </summary>
        /// <param name="context">The shutdown context describing urgency and constraints.</param>
        /// <param name="ct">Cancellation token for the shutdown operation.</param>
        /// <returns>A task representing the shutdown operation.</returns>
        Task InitiateShutdownAsync(ShutdownContext context, CancellationToken ct = default);

        /// <summary>
        /// Registers a shutdown handler to be called during graceful shutdown.
        /// Handlers are called in ascending priority order (0 first, 100 last).
        /// </summary>
        /// <param name="handler">The shutdown handler to register.</param>
        /// <param name="ct">Cancellation token for the registration.</param>
        /// <returns>A task representing the registration operation.</returns>
        Task RegisterShutdownHandlerAsync(IShutdownHandler handler, CancellationToken ct = default);

        /// <summary>
        /// Deregisters a previously registered shutdown handler.
        /// </summary>
        /// <param name="handlerId">The handler identifier to deregister.</param>
        /// <param name="ct">Cancellation token for the deregistration.</param>
        /// <returns>A task representing the deregistration operation.</returns>
        Task DeregisterShutdownHandlerAsync(string handlerId, CancellationToken ct = default);

        /// <summary>
        /// Gets the current shutdown state.
        /// </summary>
        /// <returns>The current shutdown state.</returns>
        ShutdownState GetState();
    }

    /// <summary>
    /// Handler that participates in the graceful shutdown sequence.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Resilience contracts")]
    public interface IShutdownHandler
    {
        /// <summary>
        /// Unique identifier for this handler.
        /// </summary>
        string HandlerId { get; }

        /// <summary>
        /// Priority of this handler (lower values are called first).
        /// 0 = shutdown first, 100 = shutdown last.
        /// </summary>
        int Priority { get; }

        /// <summary>
        /// Called during the shutdown sequence.
        /// </summary>
        /// <param name="context">The shutdown context.</param>
        /// <param name="ct">Cancellation token for the shutdown operation.</param>
        /// <returns>A task representing the handler's shutdown work.</returns>
        Task OnShutdownAsync(ShutdownContext context, CancellationToken ct = default);
    }

    /// <summary>
    /// Context describing a shutdown operation.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Resilience contracts")]
    public record ShutdownContext
    {
        /// <summary>
        /// Reason for the shutdown.
        /// </summary>
        public required string Reason { get; init; }

        /// <summary>
        /// The urgency level of the shutdown.
        /// </summary>
        public required ShutdownUrgency Urgency { get; init; }

        /// <summary>
        /// Maximum time to wait for shutdown completion.
        /// </summary>
        public required TimeSpan MaxWaitTime { get; init; }

        /// <summary>
        /// When the shutdown was initiated.
        /// </summary>
        public required DateTimeOffset InitiatedAt { get; init; }
    }

    /// <summary>
    /// Urgency levels for shutdown operations.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Resilience contracts")]
    public enum ShutdownUrgency
    {
        /// <summary>Graceful shutdown: drain all pending work, flush buffers, close connections cleanly.</summary>
        Graceful,
        /// <summary>Urgent shutdown: abbreviated cleanup, skip non-critical work.</summary>
        Urgent,
        /// <summary>Immediate shutdown: minimal cleanup only, stop as fast as possible.</summary>
        Immediate
    }

    /// <summary>
    /// States of the shutdown process.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Resilience contracts")]
    public enum ShutdownState
    {
        /// <summary>System is running normally.</summary>
        Running,
        /// <summary>Shutdown is in progress.</summary>
        ShuttingDown,
        /// <summary>Shutdown completed successfully.</summary>
        Completed,
        /// <summary>Shutdown failed (some handlers failed).</summary>
        Failed
    }

    /// <summary>
    /// An event describing shutdown-related operations.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Resilience contracts")]
    public record ShutdownEvent
    {
        /// <summary>
        /// The type of shutdown event.
        /// </summary>
        public required ShutdownEventType EventType { get; init; }

        /// <summary>
        /// The handler involved in the event, if applicable.
        /// </summary>
        public string? HandlerId { get; init; }

        /// <summary>
        /// When the event occurred.
        /// </summary>
        public required DateTimeOffset Timestamp { get; init; }

        /// <summary>
        /// Optional detail about the event.
        /// </summary>
        public string? Detail { get; init; }
    }

    /// <summary>
    /// Types of shutdown events.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Resilience contracts")]
    public enum ShutdownEventType
    {
        /// <summary>Shutdown was initiated.</summary>
        ShutdownInitiated,
        /// <summary>A shutdown handler started executing.</summary>
        HandlerStarted,
        /// <summary>A shutdown handler completed successfully.</summary>
        HandlerCompleted,
        /// <summary>A shutdown handler failed.</summary>
        HandlerFailed,
        /// <summary>All shutdown handlers completed and shutdown is done.</summary>
        ShutdownCompleted,
        /// <summary>Shutdown failed (one or more handlers failed).</summary>
        ShutdownFailed
    }
}
