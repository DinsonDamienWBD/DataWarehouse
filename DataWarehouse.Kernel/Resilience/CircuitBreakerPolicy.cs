using DataWarehouse.SDK.Contracts;
using System.Collections.Concurrent;

namespace DataWarehouse.Kernel.Resilience
{
    /// <summary>
    /// Default implementation of circuit breaker resilience policy.
    /// Implements the circuit breaker pattern with retry and timeout support.
    ///
    /// Thread-safety guarantees:
    /// - All state transitions are atomic under a single lock
    /// - Statistics use Interlocked for lock-free updates
    /// - Failure tracking uses bounded queue to prevent memory exhaustion
    /// </summary>
    public sealed class CircuitBreakerPolicy : IResiliencePolicy
    {
        private readonly ResiliencePolicyConfig _config;
        private readonly object _stateLock = new();

        // Bounded failure tracking - circular buffer style
        private readonly DateTime[] _failureWindow;
        private int _failureWindowIndex;
        private int _failureWindowCount;
        private const int MaxFailureWindowSize = 1000;

        private CircuitState _state = CircuitState.Closed;
        private DateTime _lastStateChange = DateTime.UtcNow;
        private DateTime? _circuitOpenedAt;

        // Statistics - using volatile for safe reads without locks
        private long _totalExecutions;
        private long _successfulExecutions;
        private long _failedExecutions;
        private long _timeoutExecutions;
        private long _circuitBreakerRejections;
        private long _retryAttempts;
        private DateTime? _lastFailure;
        private DateTime? _lastSuccess;
        private long _totalExecutionTimeTicks;

        public string PolicyId { get; }

        /// <summary>
        /// Current circuit state. Thread-safe read.
        /// </summary>
        public CircuitState State
        {
            get
            {
                lock (_stateLock)
                {
                    return _state;
                }
            }
        }

        public CircuitBreakerPolicy(string policyId, ResiliencePolicyConfig? config = null)
        {
            PolicyId = policyId ?? throw new ArgumentNullException(nameof(policyId));
            _config = config ?? new ResiliencePolicyConfig();

            // Pre-allocate bounded failure window
            _failureWindow = new DateTime[Math.Min(_config.FailureThreshold * 2, MaxFailureWindowSize)];
        }

        public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _totalExecutions);
            var startTime = DateTime.UtcNow;

            try
            {
                // Check if circuit is open
                if (!CanExecute())
                {
                    Interlocked.Increment(ref _circuitBreakerRejections);
                    throw new CircuitBreakerOpenException(PolicyId, GetTimeUntilRetry());
                }

                // Execute with retry
                return await ExecuteWithRetryAsync(action, ct);
            }
            finally
            {
                var duration = DateTime.UtcNow - startTime;
                Interlocked.Add(ref _totalExecutionTimeTicks, duration.Ticks);
            }
        }

        public async Task ExecuteAsync(Func<CancellationToken, Task> action, CancellationToken ct = default)
        {
            await ExecuteAsync(async token =>
            {
                await action(token);
                return true;
            }, ct);
        }

        private async Task<T> ExecuteWithRetryAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
        {
            var attempt = 0;
            Exception? lastException = null;

            while (attempt <= _config.MaxRetries)
            {
                ct.ThrowIfCancellationRequested();

                if (attempt > 0)
                {
                    Interlocked.Increment(ref _retryAttempts);
                    var delay = CalculateRetryDelay(attempt);
                    await Task.Delay(delay, ct);
                }

                try
                {
                    // Execute with timeout
                    using var timeoutCts = new CancellationTokenSource(_config.Timeout);
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

                    var result = await action(linkedCts.Token);

                    OnSuccess();
                    return result;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Timeout occurred
                    Interlocked.Increment(ref _timeoutExecutions);
                    lastException = new TimeoutException($"Operation timed out after {_config.Timeout.TotalSeconds}s");
                    OnFailure();
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    OnFailure();

                    // Don't retry on certain exceptions
                    if (IsNonRetryableException(ex))
                    {
                        throw;
                    }
                }

                attempt++;
            }

            throw lastException ?? new InvalidOperationException("Execution failed with no exception");
        }

        private bool CanExecute()
        {
            lock (_stateLock)
            {
                switch (_state)
                {
                    case CircuitState.Closed:
                        return true;

                    case CircuitState.Open:
                        // Check if we should transition to half-open
                        if (_circuitOpenedAt.HasValue &&
                            DateTime.UtcNow - _circuitOpenedAt.Value >= _config.BreakDuration)
                        {
                            _state = CircuitState.HalfOpen;
                            _lastStateChange = DateTime.UtcNow;
                            return true;
                        }
                        return false;

                    case CircuitState.HalfOpen:
                        return true;

                    default:
                        return false;
                }
            }
        }

        private TimeSpan GetTimeUntilRetry()
        {
            lock (_stateLock)
            {
                if (_state == CircuitState.Open && _circuitOpenedAt.HasValue)
                {
                    var elapsed = DateTime.UtcNow - _circuitOpenedAt.Value;
                    var remaining = _config.BreakDuration - elapsed;
                    return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
                }
                return TimeSpan.Zero;
            }
        }

        private void OnSuccess()
        {
            Interlocked.Increment(ref _successfulExecutions);
            _lastSuccess = DateTime.UtcNow;

            // Atomic state transition for success
            lock (_stateLock)
            {
                if (_state == CircuitState.HalfOpen)
                {
                    // Success in half-open state - close the circuit
                    TransitionToState(CircuitState.Closed);

                    // Clear failure history
                    ClearFailureWindow();
                }
            }
        }

        private void OnFailure()
        {
            Interlocked.Increment(ref _failedExecutions);
            var now = DateTime.UtcNow;
            _lastFailure = now;

            // All state mutations under a single lock to prevent race conditions
            lock (_stateLock)
            {
                // Record failure in bounded circular buffer
                RecordFailure(now);

                // Count recent failures within the window
                var recentFailures = CountRecentFailures(now);

                if (_state == CircuitState.HalfOpen)
                {
                    // Failure in half-open state - open the circuit immediately
                    TransitionToState(CircuitState.Open);
                }
                else if (_state == CircuitState.Closed && recentFailures >= _config.FailureThreshold)
                {
                    // Too many failures within window - open the circuit
                    TransitionToState(CircuitState.Open);
                }
            }
        }

        /// <summary>
        /// Records a failure timestamp in the bounded circular buffer.
        /// Must be called under _stateLock.
        /// </summary>
        private void RecordFailure(DateTime timestamp)
        {
            _failureWindow[_failureWindowIndex] = timestamp;
            _failureWindowIndex = (_failureWindowIndex + 1) % _failureWindow.Length;
            if (_failureWindowCount < _failureWindow.Length)
            {
                _failureWindowCount++;
            }
        }

        /// <summary>
        /// Counts failures within the configured time window.
        /// Must be called under _stateLock.
        /// </summary>
        private int CountRecentFailures(DateTime now)
        {
            var windowStart = now - _config.FailureWindow;
            var count = 0;

            for (int i = 0; i < _failureWindowCount; i++)
            {
                if (_failureWindow[i] >= windowStart)
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>
        /// Clears the failure window. Must be called under _stateLock.
        /// </summary>
        private void ClearFailureWindow()
        {
            _failureWindowIndex = 0;
            _failureWindowCount = 0;
            Array.Clear(_failureWindow);
        }

        /// <summary>
        /// Transitions to a new state. Must be called under _stateLock.
        /// </summary>
        private void TransitionToState(CircuitState newState)
        {
            if (_state == newState) return;

            _state = newState;
            _lastStateChange = DateTime.UtcNow;

            if (newState == CircuitState.Open)
            {
                _circuitOpenedAt = DateTime.UtcNow;
            }
            else if (newState == CircuitState.Closed)
            {
                _circuitOpenedAt = null;
            }
        }

        private TimeSpan CalculateRetryDelay(int attempt)
        {
            // Exponential backoff with jitter
            var exponentialDelay = TimeSpan.FromTicks(
                (long)(_config.RetryBaseDelay.Ticks * Math.Pow(2, attempt - 1)));

            if (exponentialDelay > _config.RetryMaxDelay)
            {
                exponentialDelay = _config.RetryMaxDelay;
            }

            // Add jitter (0-25% of delay)
            var jitter = TimeSpan.FromTicks((long)(exponentialDelay.Ticks * Random.Shared.NextDouble() * 0.25));

            return exponentialDelay + jitter;
        }

        private bool IsNonRetryableException(Exception ex)
        {
            // Custom predicate takes precedence if configured
            if (_config.ShouldRetry != null)
            {
                return !_config.ShouldRetry(ex);
            }

            // Check against configured non-retryable exception types
            var exType = ex.GetType();
            return _config.NonRetryableExceptions.Any(t => t.IsAssignableFrom(exType));
        }

        public void Reset()
        {
            lock (_stateLock)
            {
                TransitionToState(CircuitState.Closed);
                ClearFailureWindow();
            }
        }

        public ResilienceStatistics GetStatistics()
        {
            var totalTicks = Interlocked.Read(ref _totalExecutionTimeTicks);
            var total = Interlocked.Read(ref _totalExecutions);

            return new ResilienceStatistics
            {
                TotalExecutions = total,
                SuccessfulExecutions = Interlocked.Read(ref _successfulExecutions),
                FailedExecutions = Interlocked.Read(ref _failedExecutions),
                TimeoutExecutions = Interlocked.Read(ref _timeoutExecutions),
                CircuitBreakerRejections = Interlocked.Read(ref _circuitBreakerRejections),
                RetryAttempts = Interlocked.Read(ref _retryAttempts),
                LastFailure = _lastFailure,
                LastSuccess = _lastSuccess,
                AverageExecutionTime = total > 0
                    ? TimeSpan.FromTicks(totalTicks / total)
                    : TimeSpan.Zero
            };
        }
    }

    /// <summary>
    /// Exception thrown when circuit breaker is open.
    /// </summary>
    public class CircuitBreakerOpenException : Exception
    {
        public string PolicyId { get; }
        public TimeSpan RetryAfter { get; }

        public CircuitBreakerOpenException(string policyId, TimeSpan retryAfter)
            : base($"Circuit breaker '{policyId}' is open. Retry after {retryAfter.TotalSeconds:F1}s")
        {
            PolicyId = policyId;
            RetryAfter = retryAfter;
        }
    }

    /// <summary>
    /// Manages multiple resilience policies.
    /// </summary>
    public sealed class ResiliencePolicyManager : IResiliencePolicyManager
    {
        private readonly ConcurrentDictionary<string, IResiliencePolicy> _policies = new();
        private readonly ConcurrentDictionary<string, ResiliencePolicyConfig> _configs = new();
        private readonly ResiliencePolicyConfig _defaultConfig;

        public ResiliencePolicyManager(ResiliencePolicyConfig? defaultConfig = null)
        {
            _defaultConfig = defaultConfig ?? new ResiliencePolicyConfig();
        }

        public IResiliencePolicy GetPolicy(string policyKey)
        {
            return _policies.GetOrAdd(policyKey, key =>
            {
                var config = _configs.TryGetValue(key, out var c) ? c : _defaultConfig;
                return new CircuitBreakerPolicy(key, config);
            });
        }

        public void RegisterPolicy(string policyKey, ResiliencePolicyConfig config)
        {
            _configs[policyKey] = config;

            // If policy already exists, we need to recreate it with new config
            if (_policies.TryRemove(policyKey, out _))
            {
                _policies[policyKey] = new CircuitBreakerPolicy(policyKey, config);
            }
        }

        public IEnumerable<string> GetPolicyKeys()
        {
            return _policies.Keys.ToArray();
        }

        public void ResetAll()
        {
            foreach (var policy in _policies.Values)
            {
                policy.Reset();
            }
        }
    }
}
