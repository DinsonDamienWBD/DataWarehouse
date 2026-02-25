using System;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Resilience
{
    /// <summary>
    /// Focused circuit breaker contract (RESIL-01).
    /// Provides a dedicated circuit breaker interface with Open/Closed/HalfOpen states,
    /// complementing the existing <see cref="IResiliencePolicy"/> which combines
    /// circuit breaker, retry, and timeout in a single policy.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Resilience contracts")]
    public interface ICircuitBreaker
    {
        /// <summary>
        /// Gets the name of this circuit breaker instance.
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Gets the current circuit breaker state.
        /// Reuses the existing <see cref="CircuitState"/> enum from the SDK.
        /// </summary>
        CircuitState State { get; }

        /// <summary>
        /// Raised when the circuit breaker state changes.
        /// </summary>
        event Action<CircuitBreakerStateChanged>? OnStateChanged;

        /// <summary>
        /// Executes an action with circuit breaker protection.
        /// If the circuit is open, the action is rejected immediately.
        /// </summary>
        /// <typeparam name="T">The return type.</typeparam>
        /// <param name="action">The action to execute.</param>
        /// <param name="ct">Cancellation token for the operation.</param>
        /// <returns>The result of the action.</returns>
        Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct = default);

        /// <summary>
        /// Executes an action with circuit breaker protection (no return value).
        /// </summary>
        /// <param name="action">The action to execute.</param>
        /// <param name="ct">Cancellation token for the operation.</param>
        /// <returns>A task representing the operation.</returns>
        Task ExecuteAsync(Func<CancellationToken, Task> action, CancellationToken ct = default);

        /// <summary>
        /// Manually trips the circuit breaker to the Open state.
        /// </summary>
        /// <param name="reason">The reason for tripping the circuit breaker.</param>
        void Trip(string reason);

        /// <summary>
        /// Manually resets the circuit breaker to the Closed state.
        /// </summary>
        void Reset();

        /// <summary>
        /// Gets circuit breaker execution statistics.
        /// </summary>
        /// <returns>Current statistics.</returns>
        CircuitBreakerStatistics GetStatistics();
    }

    /// <summary>
    /// Event raised when a circuit breaker changes state.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Resilience contracts")]
    public record CircuitBreakerStateChanged
    {
        /// <summary>
        /// The name of the circuit breaker.
        /// </summary>
        public required string Name { get; init; }

        /// <summary>
        /// The previous state.
        /// </summary>
        public required CircuitState PreviousState { get; init; }

        /// <summary>
        /// The new state.
        /// </summary>
        public required CircuitState NewState { get; init; }

        /// <summary>
        /// Reason for the state change, if any.
        /// </summary>
        public string? Reason { get; init; }

        /// <summary>
        /// When the state change occurred.
        /// </summary>
        public required DateTimeOffset Timestamp { get; init; }
    }

    /// <summary>
    /// Statistics for a circuit breaker.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Resilience contracts")]
    public record CircuitBreakerStatistics
    {
        /// <summary>
        /// The name of the circuit breaker.
        /// </summary>
        public required string Name { get; init; }

        /// <summary>
        /// Current circuit state.
        /// </summary>
        public required CircuitState CurrentState { get; init; }

        /// <summary>
        /// Total number of requests processed.
        /// </summary>
        public required long TotalRequests { get; init; }

        /// <summary>
        /// Number of successful requests.
        /// </summary>
        public required long SuccessfulRequests { get; init; }

        /// <summary>
        /// Number of failed requests.
        /// </summary>
        public required long FailedRequests { get; init; }

        /// <summary>
        /// Number of requests rejected because the circuit was open.
        /// </summary>
        public required long RejectedRequests { get; init; }

        /// <summary>
        /// When the last failure occurred, if any.
        /// </summary>
        public DateTimeOffset? LastFailureAt { get; init; }

        /// <summary>
        /// When the last success occurred, if any.
        /// </summary>
        public DateTimeOffset? LastSuccessAt { get; init; }

        /// <summary>
        /// How long the circuit breaker has been in the current state.
        /// </summary>
        public TimeSpan? TimeInCurrentState { get; init; }
    }

    /// <summary>
    /// Configuration options for a circuit breaker.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: Resilience contracts")]
    public record CircuitBreakerOptions
    {
        /// <summary>
        /// Number of failures within the failure window that trips the circuit. Default: 5.
        /// </summary>
        public int FailureThreshold { get; init; } = 5;

        /// <summary>
        /// The time window in which failures are counted. Default: 60 seconds.
        /// </summary>
        public TimeSpan FailureWindow { get; init; } = TimeSpan.FromSeconds(60);

        /// <summary>
        /// How long the circuit stays open before transitioning to half-open. Default: 30 seconds.
        /// </summary>
        public TimeSpan BreakDuration { get; init; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Maximum number of attempts allowed in the half-open state. Default: 1.
        /// </summary>
        public int HalfOpenMaxAttempts { get; init; } = 1;

        /// <summary>
        /// Optional filter to determine which exceptions count as failures.
        /// If null, all exceptions count as failures.
        /// </summary>
        public Func<Exception, bool>? ShouldHandle { get; init; }
    }
}
