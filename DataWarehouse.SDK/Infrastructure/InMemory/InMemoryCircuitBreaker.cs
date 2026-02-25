using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Resilience;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Infrastructure.InMemory
{
    /// <summary>
    /// In-memory implementation of <see cref="ICircuitBreaker"/> with a real state machine.
    /// This is production-ready for single-node deployments -- not a stub.
    /// Tracks failures, trips to Open when threshold is exceeded, and probes in HalfOpen.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: In-memory implementation")]
    public sealed class InMemoryCircuitBreaker : ICircuitBreaker
    {
        private readonly CircuitBreakerOptions _options;
        private readonly object _lock = new();
        private CircuitState _state = CircuitState.Closed;
        private int _failureCount;
        private DateTimeOffset _lastFailureTime;
        private DateTimeOffset _stateChangedAt = DateTimeOffset.UtcNow;
        private long _totalRequests;
        private long _successfulRequests;
        private long _failedRequests;
        private long _rejectedRequests;
        private DateTimeOffset? _lastSuccessAt;

        /// <summary>
        /// Initializes a new circuit breaker.
        /// </summary>
        /// <param name="name">The name of this circuit breaker.</param>
        /// <param name="options">Circuit breaker options. Uses defaults if null.</param>
        public InMemoryCircuitBreaker(string name, CircuitBreakerOptions? options = null)
        {
            Name = name;
            _options = options ?? new CircuitBreakerOptions();
        }

        /// <inheritdoc />
        public string Name { get; }

        /// <inheritdoc />
        public CircuitState State
        {
            get { lock (_lock) { return _state; } }
        }

        /// <inheritdoc />
        public event Action<CircuitBreakerStateChanged>? OnStateChanged;

        /// <inheritdoc />
        public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            EnsureNotOpen();
            Interlocked.Increment(ref _totalRequests);

            try
            {
                var result = await action(ct);
                OnSuccess();
                return result;
            }
            catch (Exception ex) when (ShouldHandle(ex))
            {
                OnFailure();
                throw;
            }
        }

        /// <inheritdoc />
        public async Task ExecuteAsync(Func<CancellationToken, Task> action, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            EnsureNotOpen();
            Interlocked.Increment(ref _totalRequests);

            try
            {
                await action(ct);
                OnSuccess();
            }
            catch (Exception ex) when (ShouldHandle(ex))
            {
                OnFailure();
                throw;
            }
        }

        /// <inheritdoc />
        public void Trip(string reason)
        {
            lock (_lock)
            {
                TransitionTo(CircuitState.Open, reason);
            }
        }

        /// <inheritdoc />
        public void Reset()
        {
            lock (_lock)
            {
                _failureCount = 0;
                TransitionTo(CircuitState.Closed, "Manual reset");
            }
        }

        /// <inheritdoc />
        public CircuitBreakerStatistics GetStatistics() => new()
        {
            Name = Name,
            CurrentState = State,
            TotalRequests = Interlocked.Read(ref _totalRequests),
            SuccessfulRequests = Interlocked.Read(ref _successfulRequests),
            FailedRequests = Interlocked.Read(ref _failedRequests),
            RejectedRequests = Interlocked.Read(ref _rejectedRequests),
            LastFailureAt = _lastFailureTime == default ? null : _lastFailureTime,
            LastSuccessAt = _lastSuccessAt,
            TimeInCurrentState = DateTimeOffset.UtcNow - _stateChangedAt
        };

        private void EnsureNotOpen()
        {
            lock (_lock)
            {
                if (_state == CircuitState.Open)
                {
                    if (DateTimeOffset.UtcNow - _stateChangedAt >= _options.BreakDuration)
                    {
                        TransitionTo(CircuitState.HalfOpen, "Break duration elapsed");
                    }
                    else
                    {
                        Interlocked.Increment(ref _rejectedRequests);
                        throw new CircuitBreakerOpenException(Name);
                    }
                }
            }
        }

        private void OnSuccess()
        {
            Interlocked.Increment(ref _successfulRequests);
            _lastSuccessAt = DateTimeOffset.UtcNow;
            lock (_lock)
            {
                if (_state == CircuitState.HalfOpen)
                {
                    _failureCount = 0;
                    TransitionTo(CircuitState.Closed, "Successful probe in HalfOpen");
                }
            }
        }

        private void OnFailure()
        {
            Interlocked.Increment(ref _failedRequests);
            _lastFailureTime = DateTimeOffset.UtcNow;
            lock (_lock)
            {
                if (_state == CircuitState.HalfOpen)
                {
                    TransitionTo(CircuitState.Open, "Failed probe in HalfOpen");
                    return;
                }

                _failureCount++;
                if (_failureCount >= _options.FailureThreshold)
                {
                    TransitionTo(CircuitState.Open, $"Failure threshold ({_options.FailureThreshold}) exceeded");
                }
            }
        }

        private void TransitionTo(CircuitState newState, string reason)
        {
            var previous = _state;
            if (previous == newState) return;
            _state = newState;
            _stateChangedAt = DateTimeOffset.UtcNow;

            OnStateChanged?.Invoke(new CircuitBreakerStateChanged
            {
                Name = Name,
                PreviousState = previous,
                NewState = newState,
                Reason = reason,
                Timestamp = _stateChangedAt
            });
        }

        private bool ShouldHandle(Exception ex) =>
            _options.ShouldHandle?.Invoke(ex) ?? true;
    }

    /// <summary>
    /// Exception thrown when a circuit breaker is open and rejecting requests.
    /// </summary>
    [SdkCompatibility("2.0.0", Notes = "Phase 26: In-memory implementation")]
    public sealed class CircuitBreakerOpenException : InvalidOperationException
    {
        /// <summary>
        /// Gets the name of the circuit breaker that is open.
        /// </summary>
        public string CircuitBreakerName { get; }

        /// <summary>
        /// Initializes a new circuit breaker open exception.
        /// </summary>
        /// <param name="circuitBreakerName">The name of the open circuit breaker.</param>
        public CircuitBreakerOpenException(string circuitBreakerName)
            : base($"Circuit breaker '{circuitBreakerName}' is open and rejecting requests.")
        {
            CircuitBreakerName = circuitBreakerName;
        }
    }
}
