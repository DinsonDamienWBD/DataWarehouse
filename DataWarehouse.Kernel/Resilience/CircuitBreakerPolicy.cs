using DataWarehouse.SDK.Contracts;
using System.Collections.Concurrent;

namespace DataWarehouse.Kernel.Resilience
{
    /// <summary>
    /// Default implementation of circuit breaker resilience policy.
    /// Implements the circuit breaker pattern with retry and timeout support.
    /// </summary>
    public sealed class CircuitBreakerPolicy : IResiliencePolicy
    {
        private readonly ResiliencePolicyConfig _config;
        private readonly ConcurrentQueue<DateTime> _failures = new();
        private readonly object _stateLock = new();

        private CircuitState _state = CircuitState.Closed;
        private DateTime _lastStateChange = DateTime.UtcNow;
        private DateTime? _circuitOpenedAt;

        // Statistics
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
        public CircuitState State => _state;

        public CircuitBreakerPolicy(string policyId, ResiliencePolicyConfig? config = null)
        {
            PolicyId = policyId ?? throw new ArgumentNullException(nameof(policyId));
            _config = config ?? new ResiliencePolicyConfig();
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

            lock (_stateLock)
            {
                if (_state == CircuitState.HalfOpen)
                {
                    // Success in half-open state - close the circuit
                    _state = CircuitState.Closed;
                    _lastStateChange = DateTime.UtcNow;
                    _circuitOpenedAt = null;

                    // Clear failure history
                    while (_failures.TryDequeue(out _)) { }
                }
            }
        }

        private void OnFailure()
        {
            Interlocked.Increment(ref _failedExecutions);
            _lastFailure = DateTime.UtcNow;

            var now = DateTime.UtcNow;
            _failures.Enqueue(now);

            // Remove old failures outside the window
            var windowStart = now - _config.FailureWindow;
            while (_failures.TryPeek(out var oldest) && oldest < windowStart)
            {
                _failures.TryDequeue(out _);
            }

            lock (_stateLock)
            {
                if (_state == CircuitState.HalfOpen)
                {
                    // Failure in half-open state - open the circuit
                    _state = CircuitState.Open;
                    _lastStateChange = DateTime.UtcNow;
                    _circuitOpenedAt = DateTime.UtcNow;
                }
                else if (_state == CircuitState.Closed && _failures.Count >= _config.FailureThreshold)
                {
                    // Too many failures - open the circuit
                    _state = CircuitState.Open;
                    _lastStateChange = DateTime.UtcNow;
                    _circuitOpenedAt = DateTime.UtcNow;
                }
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

        private static bool IsNonRetryableException(Exception ex)
        {
            return ex is ArgumentException or
                   ArgumentNullException or
                   InvalidOperationException or
                   NotSupportedException or
                   UnauthorizedAccessException;
        }

        public void Reset()
        {
            lock (_stateLock)
            {
                _state = CircuitState.Closed;
                _lastStateChange = DateTime.UtcNow;
                _circuitOpenedAt = null;

                while (_failures.TryDequeue(out _)) { }
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
