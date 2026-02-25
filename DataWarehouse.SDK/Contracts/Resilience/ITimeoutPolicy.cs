using System;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Resilience
{
    /// <summary>
    /// Contract for unified timeout configuration for async operations (RESIL-03).
    /// Wraps operations with a configurable timeout, supporting both optimistic
    /// (CancellationToken-based) and pessimistic (task-racing) strategies.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Resilience contracts")]
    public interface ITimeoutPolicy
    {
        /// <summary>
        /// Gets the default timeout for operations.
        /// </summary>
        TimeSpan DefaultTimeout { get; }

        /// <summary>
        /// Executes an action with the default timeout.
        /// </summary>
        /// <typeparam name="T">The return type.</typeparam>
        /// <param name="action">The action to execute.</param>
        /// <param name="ct">Cancellation token for the operation.</param>
        /// <returns>The result of the action.</returns>
        Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct = default);

        /// <summary>
        /// Executes an action with an explicit timeout override.
        /// </summary>
        /// <typeparam name="T">The return type.</typeparam>
        /// <param name="action">The action to execute.</param>
        /// <param name="timeout">The timeout for this specific operation.</param>
        /// <param name="ct">Cancellation token for the operation.</param>
        /// <returns>The result of the action.</returns>
        Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, TimeSpan timeout, CancellationToken ct = default);

        /// <summary>
        /// Executes an action with the default timeout (no return value).
        /// </summary>
        /// <param name="action">The action to execute.</param>
        /// <param name="ct">Cancellation token for the operation.</param>
        /// <returns>A task representing the operation.</returns>
        Task ExecuteAsync(Func<CancellationToken, Task> action, CancellationToken ct = default);

        /// <summary>
        /// Executes an action with an explicit timeout override (no return value).
        /// </summary>
        /// <param name="action">The action to execute.</param>
        /// <param name="timeout">The timeout for this specific operation.</param>
        /// <param name="ct">Cancellation token for the operation.</param>
        /// <returns>A task representing the operation.</returns>
        Task ExecuteAsync(Func<CancellationToken, Task> action, TimeSpan timeout, CancellationToken ct = default);

        /// <summary>
        /// Raised when an operation times out.
        /// </summary>
        event Action<TimeoutEvent>? OnTimeout;
    }

    /// <summary>
    /// Configuration options for a timeout policy.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Resilience contracts")]
    public record TimeoutOptions
    {
        /// <summary>
        /// Default timeout for operations. Default: 30 seconds.
        /// </summary>
        public TimeSpan DefaultTimeout { get; init; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// The timeout strategy to use. Default: Optimistic.
        /// </summary>
        public TimeoutStrategy Strategy { get; init; } = TimeoutStrategy.Optimistic;
    }

    /// <summary>
    /// Timeout strategies.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Resilience contracts")]
    public enum TimeoutStrategy
    {
        /// <summary>
        /// Optimistic timeout: relies on CancellationToken propagation.
        /// The action must observe the CancellationToken for the timeout to be effective.
        /// More efficient but requires cooperative cancellation.
        /// </summary>
        Optimistic,

        /// <summary>
        /// Pessimistic timeout: races the action against a timer.
        /// The action will be abandoned (not cancelled) if the timeout expires.
        /// Less efficient but works with non-cooperative operations.
        /// </summary>
        Pessimistic
    }

    /// <summary>
    /// Event raised when an operation times out.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Resilience contracts")]
    public record TimeoutEvent
    {
        /// <summary>
        /// The name of the operation that timed out.
        /// </summary>
        public required string OperationName { get; init; }

        /// <summary>
        /// How long the operation ran before timing out.
        /// </summary>
        public required TimeSpan ElapsedTime { get; init; }

        /// <summary>
        /// The configured timeout that was exceeded.
        /// </summary>
        public required TimeSpan ConfiguredTimeout { get; init; }

        /// <summary>
        /// When the timeout occurred.
        /// </summary>
        public required DateTimeOffset Timestamp { get; init; }
    }
}
