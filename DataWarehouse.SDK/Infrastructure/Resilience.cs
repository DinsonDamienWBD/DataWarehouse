using System.Diagnostics;

namespace DataWarehouse.SDK.Infrastructure
{
    /// <summary>
    /// Defines a resilience policy that wraps operations with fault tolerance patterns.
    /// </summary>
    public interface IResiliencePolicy
    {
        /// <summary>
        /// Executes an asynchronous operation with resilience handling.
        /// </summary>
        /// <typeparam name="T">The return type of the operation.</typeparam>
        /// <param name="operation">The operation to execute.</param>
        /// <param name="cancellationToken">Cancellation token for the operation.</param>
        /// <returns>The result of the operation.</returns>
        Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default);

        /// <summary>
        /// Executes an asynchronous operation with resilience handling (no return value).
        /// </summary>
        /// <param name="operation">The operation to execute.</param>
        /// <param name="cancellationToken">Cancellation token for the operation.</param>
        Task ExecuteAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Represents the state of a circuit breaker.
    /// </summary>
    public enum CircuitState
    {
        /// <summary>
        /// Circuit is closed - requests flow through normally.
        /// </summary>
        Closed,

        /// <summary>
        /// Circuit is open - requests are blocked to allow recovery.
        /// </summary>
        Open,

        /// <summary>
        /// Circuit is half-open - limited test requests are allowed through.
        /// </summary>
        HalfOpen
    }

    /// <summary>
    /// Exception thrown when the circuit breaker is open and rejecting requests.
    /// </summary>
    public class CircuitBreakerOpenException : Exception
    {
        /// <summary>
        /// Gets the time remaining until the circuit transitions to half-open.
        /// </summary>
        public TimeSpan RemainingOpenTime { get; }

        /// <summary>
        /// Creates a new CircuitBreakerOpenException.
        /// </summary>
        /// <param name="remainingOpenTime">Time remaining until the circuit can be tested.</param>
        public CircuitBreakerOpenException(TimeSpan remainingOpenTime)
            : base($"Circuit breaker is open. Remaining time: {remainingOpenTime.TotalSeconds:F1}s")
        {
            RemainingOpenTime = remainingOpenTime;
        }
    }

    /// <summary>
    /// A circuit breaker policy that prevents cascading failures by temporarily
    /// blocking operations when a failure threshold is reached.
    /// Thread-safe implementation using Interlocked operations.
    /// </summary>
    public class CircuitBreakerPolicy : IResiliencePolicy
    {
        private readonly int _failureThreshold;
        private readonly TimeSpan _openDuration;
        private readonly int _halfOpenTestCount;

        // Thread-safe state using Interlocked
        private int _failureCount;
        private int _state; // 0 = Closed, 1 = Open, 2 = HalfOpen
        private long _openedAtTicks;
        private int _halfOpenSuccessCount;
        private int _halfOpenAttemptCount;

        /// <summary>
        /// Gets the current state of the circuit breaker.
        /// </summary>
        public CircuitState State => (CircuitState)Volatile.Read(ref _state);

        /// <summary>
        /// Gets the current failure count.
        /// </summary>
        public int FailureCount => Volatile.Read(ref _failureCount);

        /// <summary>
        /// Event raised when the circuit state changes.
        /// </summary>
        public event Action<CircuitState, CircuitState>? StateChanged;

        /// <summary>
        /// Creates a new circuit breaker policy.
        /// </summary>
        /// <param name="failureThreshold">Number of failures before opening the circuit.</param>
        /// <param name="openDuration">How long the circuit stays open before transitioning to half-open.</param>
        /// <param name="halfOpenTestCount">Number of successful tests required in half-open state to close the circuit.</param>
        public CircuitBreakerPolicy(int failureThreshold, TimeSpan openDuration, int halfOpenTestCount = 1)
        {
            if (failureThreshold <= 0)
                throw new ArgumentOutOfRangeException(nameof(failureThreshold), "Failure threshold must be positive.");
            if (openDuration <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(openDuration), "Open duration must be positive.");
            if (halfOpenTestCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(halfOpenTestCount), "Half-open test count must be positive.");

            _failureThreshold = failureThreshold;
            _openDuration = openDuration;
            _halfOpenTestCount = halfOpenTestCount;
            _state = (int)CircuitState.Closed;
        }

        /// <inheritdoc />
        public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default)
        {
            if (operation == null)
                throw new ArgumentNullException(nameof(operation));

            EnsureCircuitAllowsExecution();

            try
            {
                var result = await operation(cancellationToken).ConfigureAwait(false);
                OnSuccess();
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Don't count cancellation as failure
                throw;
            }
            catch
            {
                OnFailure();
                throw;
            }
        }

        /// <inheritdoc />
        public async Task ExecuteAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default)
        {
            if (operation == null)
                throw new ArgumentNullException(nameof(operation));

            await ExecuteAsync(async ct =>
            {
                await operation(ct).ConfigureAwait(false);
                return true;
            }, cancellationToken).ConfigureAwait(false);
        }

        private void EnsureCircuitAllowsExecution()
        {
            var currentState = (CircuitState)Volatile.Read(ref _state);

            switch (currentState)
            {
                case CircuitState.Closed:
                    return;

                case CircuitState.Open:
                    var openedAt = new DateTime(Volatile.Read(ref _openedAtTicks), DateTimeKind.Utc);
                    var elapsed = DateTime.UtcNow - openedAt;

                    if (elapsed >= _openDuration)
                    {
                        // Attempt to transition to half-open
                        if (TryTransitionToHalfOpen())
                        {
                            return; // Allow this request as a test
                        }
                    }

                    // Still open - calculate remaining time and reject
                    var remaining = _openDuration - elapsed;
                    if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
                    throw new CircuitBreakerOpenException(remaining);

                case CircuitState.HalfOpen:
                    // In half-open, we allow limited requests through
                    var attemptCount = Interlocked.Increment(ref _halfOpenAttemptCount);
                    if (attemptCount > _halfOpenTestCount * 2) // Allow some buffer for concurrent requests
                    {
                        Interlocked.Decrement(ref _halfOpenAttemptCount);
                        throw new CircuitBreakerOpenException(TimeSpan.Zero);
                    }
                    return;
            }
        }

        private bool TryTransitionToHalfOpen()
        {
            var previousState = Interlocked.CompareExchange(ref _state, (int)CircuitState.HalfOpen, (int)CircuitState.Open);
            if (previousState == (int)CircuitState.Open)
            {
                // Successfully transitioned to half-open
                Interlocked.Exchange(ref _halfOpenSuccessCount, 0);
                Interlocked.Exchange(ref _halfOpenAttemptCount, 0);
                RaiseStateChanged(CircuitState.Open, CircuitState.HalfOpen);
                return true;
            }
            return previousState == (int)CircuitState.HalfOpen; // Already half-open is OK
        }

        private void OnSuccess()
        {
            var currentState = (CircuitState)Volatile.Read(ref _state);

            switch (currentState)
            {
                case CircuitState.Closed:
                    // Reset failure count on success
                    Interlocked.Exchange(ref _failureCount, 0);
                    break;

                case CircuitState.HalfOpen:
                    var successCount = Interlocked.Increment(ref _halfOpenSuccessCount);
                    if (successCount >= _halfOpenTestCount)
                    {
                        // Enough successful tests - close the circuit
                        TryTransitionToClosed();
                    }
                    break;
            }
        }

        private void OnFailure()
        {
            var currentState = (CircuitState)Volatile.Read(ref _state);

            switch (currentState)
            {
                case CircuitState.Closed:
                    var newCount = Interlocked.Increment(ref _failureCount);
                    if (newCount >= _failureThreshold)
                    {
                        TryTransitionToOpen();
                    }
                    break;

                case CircuitState.HalfOpen:
                    // Any failure in half-open state reopens the circuit
                    TryTransitionToOpen();
                    break;
            }
        }

        private void TryTransitionToOpen()
        {
            var previousState = Volatile.Read(ref _state);
            if (previousState == (int)CircuitState.Open) return;

            var oldState = Interlocked.CompareExchange(ref _state, (int)CircuitState.Open, previousState);
            if (oldState == previousState)
            {
                Interlocked.Exchange(ref _openedAtTicks, DateTime.UtcNow.Ticks);
                Interlocked.Exchange(ref _failureCount, 0);
                RaiseStateChanged((CircuitState)previousState, CircuitState.Open);
            }
        }

        private void TryTransitionToClosed()
        {
            var previousState = Interlocked.CompareExchange(ref _state, (int)CircuitState.Closed, (int)CircuitState.HalfOpen);
            if (previousState == (int)CircuitState.HalfOpen)
            {
                Interlocked.Exchange(ref _failureCount, 0);
                RaiseStateChanged(CircuitState.HalfOpen, CircuitState.Closed);
            }
        }

        private void RaiseStateChanged(CircuitState oldState, CircuitState newState)
        {
            StateChanged?.Invoke(oldState, newState);
        }

        /// <summary>
        /// Manually resets the circuit breaker to closed state.
        /// </summary>
        public void Reset()
        {
            var previousState = (CircuitState)Interlocked.Exchange(ref _state, (int)CircuitState.Closed);
            Interlocked.Exchange(ref _failureCount, 0);
            Interlocked.Exchange(ref _halfOpenSuccessCount, 0);
            Interlocked.Exchange(ref _halfOpenAttemptCount, 0);

            if (previousState != CircuitState.Closed)
            {
                RaiseStateChanged(previousState, CircuitState.Closed);
            }
        }
    }

    /// <summary>
    /// A retry policy that implements exponential backoff with optional jitter.
    /// </summary>
    public class RetryPolicy : IResiliencePolicy
    {
        private readonly int _maxRetries;
        private readonly TimeSpan _baseDelay;
        private readonly double _backoffMultiplier;
        private readonly TimeSpan _maxDelay;
        private readonly double _jitterFactor;
        private readonly Func<Exception, bool>? _shouldRetry;

        // Thread-local random for jitter calculation (thread-safe)
        private static readonly ThreadLocal<Random> _random = new(() => new Random(Guid.NewGuid().GetHashCode()));

        /// <summary>
        /// Creates a new retry policy.
        /// </summary>
        /// <param name="maxRetries">Maximum number of retry attempts.</param>
        /// <param name="baseDelay">Initial delay between retries.</param>
        /// <param name="backoffMultiplier">Multiplier for exponential backoff (default 2.0).</param>
        /// <param name="maxDelay">Maximum delay cap (default 30 seconds).</param>
        /// <param name="jitterFactor">Jitter factor (0.0-1.0) to randomize delays (default 0.25).</param>
        /// <param name="shouldRetry">Optional predicate to determine if an exception should trigger a retry.</param>
        public RetryPolicy(
            int maxRetries,
            TimeSpan baseDelay,
            double backoffMultiplier = 2.0,
            TimeSpan? maxDelay = null,
            double jitterFactor = 0.25,
            Func<Exception, bool>? shouldRetry = null)
        {
            if (maxRetries < 0)
                throw new ArgumentOutOfRangeException(nameof(maxRetries), "Max retries cannot be negative.");
            if (baseDelay < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(baseDelay), "Base delay cannot be negative.");
            if (backoffMultiplier < 1.0)
                throw new ArgumentOutOfRangeException(nameof(backoffMultiplier), "Backoff multiplier must be at least 1.0.");
            if (jitterFactor < 0.0 || jitterFactor > 1.0)
                throw new ArgumentOutOfRangeException(nameof(jitterFactor), "Jitter factor must be between 0.0 and 1.0.");

            _maxRetries = maxRetries;
            _baseDelay = baseDelay;
            _backoffMultiplier = backoffMultiplier;
            _maxDelay = maxDelay ?? TimeSpan.FromSeconds(30);
            _jitterFactor = jitterFactor;
            _shouldRetry = shouldRetry;
        }

        /// <inheritdoc />
        public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default)
        {
            if (operation == null)
                throw new ArgumentNullException(nameof(operation));

            var attempt = 0;
            var exceptions = new List<Exception>();

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    return await operation(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Don't retry on user-requested cancellation
                    throw;
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);

                    // Check if we should retry this exception type
                    if (_shouldRetry != null && !_shouldRetry(ex))
                    {
                        throw;
                    }

                    attempt++;
                    if (attempt > _maxRetries)
                    {
                        throw new AggregateException(
                            $"Operation failed after {attempt} attempts.",
                            exceptions);
                    }

                    var delay = CalculateDelay(attempt);
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        /// <inheritdoc />
        public async Task ExecuteAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default)
        {
            if (operation == null)
                throw new ArgumentNullException(nameof(operation));

            await ExecuteAsync(async ct =>
            {
                await operation(ct).ConfigureAwait(false);
                return true;
            }, cancellationToken).ConfigureAwait(false);
        }

        private TimeSpan CalculateDelay(int attempt)
        {
            // Calculate exponential delay: baseDelay * (multiplier ^ (attempt - 1))
            var exponentialDelay = _baseDelay.TotalMilliseconds * Math.Pow(_backoffMultiplier, attempt - 1);

            // Cap at max delay
            exponentialDelay = Math.Min(exponentialDelay, _maxDelay.TotalMilliseconds);

            // Apply jitter: delay * (1 - jitter + random * 2 * jitter)
            // This spreads the delay uniformly in the range [delay * (1-jitter), delay * (1+jitter)]
            if (_jitterFactor > 0 && _random.Value != null)
            {
                var jitterRange = exponentialDelay * _jitterFactor;
                var jitter = (_random.Value.NextDouble() * 2 - 1) * jitterRange;
                exponentialDelay += jitter;
            }

            // Ensure non-negative
            exponentialDelay = Math.Max(0, exponentialDelay);

            return TimeSpan.FromMilliseconds(exponentialDelay);
        }
    }

    /// <summary>
    /// Exception thrown when an operation exceeds its configured timeout.
    /// </summary>
    public class TimeoutPolicyException : TimeoutException
    {
        /// <summary>
        /// Gets the configured timeout that was exceeded.
        /// </summary>
        public TimeSpan ConfiguredTimeout { get; }

        /// <summary>
        /// Creates a new TimeoutPolicyException.
        /// </summary>
        /// <param name="timeout">The timeout that was exceeded.</param>
        public TimeoutPolicyException(TimeSpan timeout)
            : base($"Operation timed out after {timeout.TotalSeconds:F1} seconds.")
        {
            ConfiguredTimeout = timeout;
        }

        /// <summary>
        /// Creates a new TimeoutPolicyException with an inner exception.
        /// </summary>
        /// <param name="timeout">The timeout that was exceeded.</param>
        /// <param name="innerException">The inner exception.</param>
        public TimeoutPolicyException(TimeSpan timeout, Exception innerException)
            : base($"Operation timed out after {timeout.TotalSeconds:F1} seconds.", innerException)
        {
            ConfiguredTimeout = timeout;
        }
    }

    /// <summary>
    /// A timeout policy that enforces a maximum execution time for operations.
    /// </summary>
    public class TimeoutPolicy : IResiliencePolicy
    {
        private readonly TimeSpan _timeout;
        private readonly TimeoutStrategy _strategy;

        /// <summary>
        /// Defines how the timeout policy handles timeout situations.
        /// </summary>
        public enum TimeoutStrategy
        {
            /// <summary>
            /// Optimistic timeout - relies on the operation respecting the cancellation token.
            /// More efficient but requires cooperative cancellation.
            /// </summary>
            Optimistic,

            /// <summary>
            /// Pessimistic timeout - abandons the operation if it exceeds the timeout,
            /// even if it doesn't respect cancellation. May leave background work running.
            /// </summary>
            Pessimistic
        }

        /// <summary>
        /// Creates a new timeout policy.
        /// </summary>
        /// <param name="timeout">The maximum time to allow for an operation.</param>
        /// <param name="strategy">The timeout strategy to use (default: Optimistic).</param>
        public TimeoutPolicy(TimeSpan timeout, TimeoutStrategy strategy = TimeoutStrategy.Optimistic)
        {
            if (timeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be positive.");

            _timeout = timeout;
            _strategy = strategy;
        }

        /// <inheritdoc />
        public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default)
        {
            if (operation == null)
                throw new ArgumentNullException(nameof(operation));

            using var timeoutCts = new CancellationTokenSource();
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            timeoutCts.CancelAfter(_timeout);

            try
            {
                if (_strategy == TimeoutStrategy.Optimistic)
                {
                    return await operation(linkedCts.Token).ConfigureAwait(false);
                }
                else
                {
                    // Pessimistic strategy - race against a timeout task
                    var operationTask = operation(linkedCts.Token);
                    var timeoutTask = Task.Delay(_timeout, cancellationToken);

                    var completedTask = await Task.WhenAny(operationTask, timeoutTask).ConfigureAwait(false);

                    if (completedTask == timeoutTask)
                    {
                        // Operation didn't complete in time
                        timeoutCts.Cancel(); // Signal cancellation to the operation

                        // Check if user cancelled
                        cancellationToken.ThrowIfCancellationRequested();

                        throw new TimeoutPolicyException(_timeout);
                    }

                    return await operationTask.ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                // Timeout occurred (not user cancellation)
                throw new TimeoutPolicyException(_timeout);
            }
        }

        /// <inheritdoc />
        public async Task ExecuteAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default)
        {
            if (operation == null)
                throw new ArgumentNullException(nameof(operation));

            await ExecuteAsync(async ct =>
            {
                await operation(ct).ConfigureAwait(false);
                return true;
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A composite policy that chains multiple resilience policies together.
    /// Policies are executed in the order they were added (outer to inner).
    /// </summary>
    internal class CompositePolicy : IResiliencePolicy
    {
        private readonly IReadOnlyList<IResiliencePolicy> _policies;

        public CompositePolicy(IReadOnlyList<IResiliencePolicy> policies)
        {
            _policies = policies ?? throw new ArgumentNullException(nameof(policies));
        }

        public Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default)
        {
            if (operation == null)
                throw new ArgumentNullException(nameof(operation));

            if (_policies.Count == 0)
            {
                return operation(cancellationToken);
            }

            // Build the execution chain from innermost to outermost
            Func<CancellationToken, Task<T>> chain = operation;

            for (int i = _policies.Count - 1; i >= 0; i--)
            {
                var policy = _policies[i];
                var innerChain = chain;
                chain = ct => policy.ExecuteAsync(innerChain, ct);
            }

            return chain(cancellationToken);
        }

        public async Task ExecuteAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default)
        {
            if (operation == null)
                throw new ArgumentNullException(nameof(operation));

            await ExecuteAsync(async ct =>
            {
                await operation(ct).ConfigureAwait(false);
                return true;
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A no-op policy that executes operations directly without any resilience handling.
    /// Used when no policies are configured.
    /// </summary>
    internal class NoOpPolicy : IResiliencePolicy
    {
        public static readonly NoOpPolicy Instance = new();

        private NoOpPolicy() { }

        public Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default)
        {
            if (operation == null)
                throw new ArgumentNullException(nameof(operation));

            return operation(cancellationToken);
        }

        public async Task ExecuteAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default)
        {
            if (operation == null)
                throw new ArgumentNullException(nameof(operation));

            await operation(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Fluent builder for composing resilience policies.
    /// Policies are applied in the order: Timeout -> CircuitBreaker -> Retry (outer to inner).
    /// This means retries happen first, then circuit breaker tracks failures, then timeout wraps everything.
    /// </summary>
    public class ResiliencePolicyBuilder
    {
        private RetryPolicy? _retryPolicy;
        private CircuitBreakerPolicy? _circuitBreakerPolicy;
        private TimeoutPolicy? _timeoutPolicy;
        private readonly List<IResiliencePolicy> _customPolicies = new();

        /// <summary>
        /// Creates a new resilience policy builder.
        /// </summary>
        public ResiliencePolicyBuilder()
        {
        }

        /// <summary>
        /// Adds a retry policy with the specified configuration.
        /// </summary>
        /// <param name="maxRetries">Maximum number of retry attempts.</param>
        /// <param name="baseDelay">Initial delay between retries.</param>
        /// <param name="backoffMultiplier">Multiplier for exponential backoff (default 2.0).</param>
        /// <param name="maxDelay">Maximum delay cap (default 30 seconds).</param>
        /// <param name="jitterFactor">Jitter factor (0.0-1.0) to randomize delays (default 0.25).</param>
        /// <param name="shouldRetry">Optional predicate to determine if an exception should trigger a retry.</param>
        /// <returns>The builder for method chaining.</returns>
        public ResiliencePolicyBuilder WithRetry(
            int maxRetries,
            TimeSpan baseDelay,
            double backoffMultiplier = 2.0,
            TimeSpan? maxDelay = null,
            double jitterFactor = 0.25,
            Func<Exception, bool>? shouldRetry = null)
        {
            _retryPolicy = new RetryPolicy(maxRetries, baseDelay, backoffMultiplier, maxDelay, jitterFactor, shouldRetry);
            return this;
        }

        /// <summary>
        /// Adds a pre-configured retry policy.
        /// </summary>
        /// <param name="policy">The retry policy to use.</param>
        /// <returns>The builder for method chaining.</returns>
        public ResiliencePolicyBuilder WithRetry(RetryPolicy policy)
        {
            _retryPolicy = policy ?? throw new ArgumentNullException(nameof(policy));
            return this;
        }

        /// <summary>
        /// Adds a circuit breaker policy with the specified configuration.
        /// </summary>
        /// <param name="threshold">Number of failures before opening the circuit.</param>
        /// <param name="openDuration">How long the circuit stays open before transitioning to half-open.</param>
        /// <param name="halfOpenTestCount">Number of successful tests required in half-open state to close the circuit (default 1).</param>
        /// <returns>The builder for method chaining.</returns>
        public ResiliencePolicyBuilder WithCircuitBreaker(
            int threshold,
            TimeSpan openDuration,
            int halfOpenTestCount = 1)
        {
            _circuitBreakerPolicy = new CircuitBreakerPolicy(threshold, openDuration, halfOpenTestCount);
            return this;
        }

        /// <summary>
        /// Adds a pre-configured circuit breaker policy.
        /// </summary>
        /// <param name="policy">The circuit breaker policy to use.</param>
        /// <returns>The builder for method chaining.</returns>
        public ResiliencePolicyBuilder WithCircuitBreaker(CircuitBreakerPolicy policy)
        {
            _circuitBreakerPolicy = policy ?? throw new ArgumentNullException(nameof(policy));
            return this;
        }

        /// <summary>
        /// Adds a timeout policy with the specified duration.
        /// </summary>
        /// <param name="timeout">The maximum time to allow for an operation.</param>
        /// <param name="strategy">The timeout strategy to use (default: Optimistic).</param>
        /// <returns>The builder for method chaining.</returns>
        public ResiliencePolicyBuilder WithTimeout(
            TimeSpan timeout,
            TimeoutPolicy.TimeoutStrategy strategy = TimeoutPolicy.TimeoutStrategy.Optimistic)
        {
            _timeoutPolicy = new TimeoutPolicy(timeout, strategy);
            return this;
        }

        /// <summary>
        /// Adds a pre-configured timeout policy.
        /// </summary>
        /// <param name="policy">The timeout policy to use.</param>
        /// <returns>The builder for method chaining.</returns>
        public ResiliencePolicyBuilder WithTimeout(TimeoutPolicy policy)
        {
            _timeoutPolicy = policy ?? throw new ArgumentNullException(nameof(policy));
            return this;
        }

        /// <summary>
        /// Adds a custom resilience policy to the chain.
        /// Custom policies are applied after the built-in policies.
        /// </summary>
        /// <param name="policy">The custom policy to add.</param>
        /// <returns>The builder for method chaining.</returns>
        public ResiliencePolicyBuilder WithCustomPolicy(IResiliencePolicy policy)
        {
            if (policy == null)
                throw new ArgumentNullException(nameof(policy));

            _customPolicies.Add(policy);
            return this;
        }

        /// <summary>
        /// Builds the composite resilience policy.
        /// Policy execution order (outer to inner): Timeout -> CircuitBreaker -> Retry -> Custom policies -> Operation
        /// </summary>
        /// <returns>The composed resilience policy.</returns>
        public IResiliencePolicy Build()
        {
            var policies = new List<IResiliencePolicy>();

            // Order matters: Timeout wraps everything, then circuit breaker, then retry
            // This means: if timeout expires, everything stops; circuit breaker tracks failures across retries;
            // retries happen for each individual failure

            if (_timeoutPolicy != null)
                policies.Add(_timeoutPolicy);

            if (_circuitBreakerPolicy != null)
                policies.Add(_circuitBreakerPolicy);

            if (_retryPolicy != null)
                policies.Add(_retryPolicy);

            policies.AddRange(_customPolicies);

            if (policies.Count == 0)
                return NoOpPolicy.Instance;

            if (policies.Count == 1)
                return policies[0];

            return new CompositePolicy(policies);
        }

        /// <summary>
        /// Gets the configured circuit breaker policy, if any.
        /// Useful for monitoring circuit state.
        /// </summary>
        public CircuitBreakerPolicy? CircuitBreaker => _circuitBreakerPolicy;
    }

    /// <summary>
    /// Provides pre-configured resilience policy templates.
    /// </summary>
    public static class ResiliencePolicies
    {
        /// <summary>
        /// Creates a default retry policy suitable for transient failures.
        /// Configuration: 3 retries, 1 second base delay, exponential backoff with jitter.
        /// </summary>
        public static RetryPolicy DefaultRetry => new(
            maxRetries: 3,
            baseDelay: TimeSpan.FromSeconds(1),
            backoffMultiplier: 2.0,
            jitterFactor: 0.25);

        /// <summary>
        /// Creates an aggressive retry policy for critical operations.
        /// Configuration: 5 retries, 500ms base delay, exponential backoff with jitter.
        /// </summary>
        public static RetryPolicy AggressiveRetry => new(
            maxRetries: 5,
            baseDelay: TimeSpan.FromMilliseconds(500),
            backoffMultiplier: 1.5,
            maxDelay: TimeSpan.FromSeconds(10),
            jitterFactor: 0.3);

        /// <summary>
        /// Creates a default circuit breaker suitable for most services.
        /// Configuration: Opens after 5 failures, stays open for 30 seconds.
        /// </summary>
        public static CircuitBreakerPolicy DefaultCircuitBreaker => new(
            failureThreshold: 5,
            openDuration: TimeSpan.FromSeconds(30),
            halfOpenTestCount: 2);

        /// <summary>
        /// Creates a sensitive circuit breaker that trips quickly.
        /// Configuration: Opens after 3 failures, stays open for 60 seconds.
        /// </summary>
        public static CircuitBreakerPolicy SensitiveCircuitBreaker => new(
            failureThreshold: 3,
            openDuration: TimeSpan.FromSeconds(60),
            halfOpenTestCount: 3);

        /// <summary>
        /// Creates a default timeout policy.
        /// Configuration: 30 second timeout with optimistic strategy.
        /// </summary>
        public static TimeoutPolicy DefaultTimeout => new(
            timeout: TimeSpan.FromSeconds(30),
            strategy: TimeoutPolicy.TimeoutStrategy.Optimistic);

        /// <summary>
        /// Creates a short timeout policy for fast operations.
        /// Configuration: 5 second timeout with pessimistic strategy.
        /// </summary>
        public static TimeoutPolicy ShortTimeout => new(
            timeout: TimeSpan.FromSeconds(5),
            strategy: TimeoutPolicy.TimeoutStrategy.Pessimistic);

        /// <summary>
        /// Creates a comprehensive default policy with retry, circuit breaker, and timeout.
        /// </summary>
        public static IResiliencePolicy Default => new ResiliencePolicyBuilder()
            .WithRetry(maxRetries: 3, baseDelay: TimeSpan.FromSeconds(1))
            .WithCircuitBreaker(threshold: 5, openDuration: TimeSpan.FromSeconds(30))
            .WithTimeout(TimeSpan.FromSeconds(30))
            .Build();
    }
}
