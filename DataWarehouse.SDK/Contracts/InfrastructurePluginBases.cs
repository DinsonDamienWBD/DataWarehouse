using DataWarehouse.SDK.Primitives;
using System.Collections.Concurrent;
using System.Security.Claims;

namespace DataWarehouse.SDK.Contracts
{
    #region Health Provider

    /// <summary>
    /// Abstract base class for health check provider plugins.
    /// Provides common infrastructure for implementing IHealthCheck with
    /// automatic registration, caching, and component health aggregation.
    /// </summary>
    public abstract class HealthProviderPluginBase : FeaturePluginBase, IHealthCheck
    {
        private readonly ConcurrentDictionary<string, IHealthCheck> _registeredChecks = new();
        private readonly ConcurrentDictionary<string, HealthCheckResult> _cachedResults = new();
        private readonly SemaphoreSlim _checkLock = new(1, 1);
        private DateTime _lastCheckTime = DateTime.MinValue;

        /// <summary>
        /// Name of this health check provider.
        /// </summary>
        public abstract string Name { get; }

        /// <summary>
        /// Tags for categorizing this health check (e.g., "liveness", "readiness", "storage").
        /// </summary>
        public virtual string[] Tags => new[] { "plugin", Category.ToString().ToLowerInvariant() };

        /// <summary>
        /// How long to cache health check results. Default is 5 seconds.
        /// </summary>
        protected virtual TimeSpan CacheDuration => TimeSpan.FromSeconds(5);

        /// <summary>
        /// Timeout for individual component health checks. Default is 10 seconds.
        /// </summary>
        protected virtual TimeSpan CheckTimeout => TimeSpan.FromSeconds(10);

        /// <summary>
        /// Performs the health check. Override to provide custom logic.
        /// Default implementation aggregates all registered component checks.
        /// </summary>
        public virtual async Task<HealthCheckResult> CheckHealthAsync(CancellationToken ct = default)
        {
            // Check cache
            if (DateTime.UtcNow - _lastCheckTime < CacheDuration && _cachedResults.TryGetValue("_aggregate", out var cached))
            {
                return cached;
            }

            if (!await _checkLock.WaitAsync(TimeSpan.FromSeconds(5), ct))
            {
                return HealthCheckResult.Degraded("Health check in progress");
            }

            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var results = new Dictionary<string, HealthCheckResult>();

                // Run custom health check
                var selfCheck = await CheckSelfHealthAsync(ct);
                results["self"] = selfCheck;

                // Run registered component checks in parallel
                var tasks = _registeredChecks.Select(async kv =>
                {
                    try
                    {
                        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        cts.CancelAfter(CheckTimeout);
                        var result = await kv.Value.CheckHealthAsync(cts.Token);
                        return (kv.Key, result);
                    }
                    catch (OperationCanceledException)
                    {
                        return (kv.Key, HealthCheckResult.Unhealthy($"Health check timed out after {CheckTimeout.TotalSeconds}s"));
                    }
                    catch (Exception ex)
                    {
                        return (kv.Key, HealthCheckResult.Unhealthy($"Health check failed: {ex.Message}", ex));
                    }
                });

                var componentResults = await Task.WhenAll(tasks);
                foreach (var (name, result) in componentResults)
                {
                    results[name] = result;
                    _cachedResults[name] = result;
                }

                sw.Stop();

                // Aggregate status
                var overallStatus = DetermineOverallStatus(results.Values);
                var aggregateResult = new HealthCheckResult
                {
                    Status = overallStatus,
                    Message = GenerateMessage(results),
                    Duration = sw.Elapsed,
                    Data = new Dictionary<string, object>
                    {
                        ["componentCount"] = results.Count,
                        ["components"] = results.ToDictionary(r => r.Key, r => (object)new { status = r.Value.Status.ToString(), message = r.Value.Message })
                    }
                };

                _cachedResults["_aggregate"] = aggregateResult;
                _lastCheckTime = DateTime.UtcNow;

                return aggregateResult;
            }
            finally
            {
                _checkLock.Release();
            }
        }

        /// <summary>
        /// Override to provide plugin-specific health check logic.
        /// </summary>
        protected virtual Task<HealthCheckResult> CheckSelfHealthAsync(CancellationToken ct)
        {
            return Task.FromResult(HealthCheckResult.Healthy($"{Name} is healthy"));
        }

        /// <summary>
        /// Registers a component health check.
        /// </summary>
        public void RegisterHealthCheck(string name, IHealthCheck check)
        {
            _registeredChecks[name] = check ?? throw new ArgumentNullException(nameof(check));
        }

        /// <summary>
        /// Unregisters a component health check.
        /// </summary>
        public bool UnregisterHealthCheck(string name)
        {
            return _registeredChecks.TryRemove(name, out _);
        }

        private static HealthStatus DetermineOverallStatus(IEnumerable<HealthCheckResult> results)
        {
            var resultList = results.ToList();
            if (resultList.Any(r => r.Status == HealthStatus.Unhealthy))
                return HealthStatus.Unhealthy;
            if (resultList.Any(r => r.Status == HealthStatus.Degraded))
                return HealthStatus.Degraded;
            return HealthStatus.Healthy;
        }

        private static string GenerateMessage(Dictionary<string, HealthCheckResult> results)
        {
            var unhealthy = results.Where(r => r.Value.Status == HealthStatus.Unhealthy).Select(r => r.Key).ToList();
            var degraded = results.Where(r => r.Value.Status == HealthStatus.Degraded).Select(r => r.Key).ToList();

            if (unhealthy.Any())
                return $"Unhealthy: {string.Join(", ", unhealthy)}";
            if (degraded.Any())
                return $"Degraded: {string.Join(", ", degraded)}";
            return $"All {results.Count} components healthy";
        }

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["FeatureType"] = "HealthProvider";
            metadata["RegisteredChecks"] = _registeredChecks.Count;
            metadata["CacheDurationSeconds"] = CacheDuration.TotalSeconds;
            return metadata;
        }
    }

    #endregion

    #region Rate Limiter

    /// <summary>
    /// Abstract base class for rate limiter plugins.
    /// Provides common token bucket implementation with configurable policies.
    /// </summary>
    public abstract class RateLimiterPluginBase : FeaturePluginBase, IRateLimiter
    {
        private readonly ConcurrentDictionary<string, RateLimitBucket> _buckets = new();
        private readonly Timer? _cleanupTimer;

        /// <summary>
        /// Default rate limit configuration for new keys.
        /// </summary>
        protected virtual RateLimitConfig DefaultConfig => new()
        {
            PermitsPerWindow = 100,
            WindowDuration = TimeSpan.FromMinutes(1),
            BurstLimit = 10
        };

        /// <summary>
        /// How often to clean up expired buckets. Default is 5 minutes.
        /// </summary>
        protected virtual TimeSpan CleanupInterval => TimeSpan.FromMinutes(5);

        /// <summary>
        /// How long a bucket can be idle before being cleaned up. Default is 30 minutes.
        /// </summary>
        protected virtual TimeSpan BucketIdleTimeout => TimeSpan.FromMinutes(30);

        protected RateLimiterPluginBase()
        {
            if (CleanupInterval > TimeSpan.Zero)
            {
                _cleanupTimer = new Timer(
                    _ => CleanupExpiredBuckets(),
                    null,
                    CleanupInterval,
                    CleanupInterval);
            }
        }

        /// <summary>
        /// Attempts to acquire permits for a given key.
        /// </summary>
        public virtual Task<RateLimitResult> AcquireAsync(string key, int permits = 1, CancellationToken ct = default)
        {
            var config = GetConfigForKey(key);
            var bucket = _buckets.GetOrAdd(key, _ => new RateLimitBucket(key, config));

            lock (bucket)
            {
                bucket.LastAccess = DateTime.UtcNow;
                RefillBucket(bucket, config);

                if (bucket.AvailablePermits >= permits)
                {
                    bucket.AvailablePermits -= permits;
                    return Task.FromResult(RateLimitResult.Allowed(bucket.AvailablePermits));
                }

                // Calculate retry-after
                var permitsNeeded = permits - bucket.AvailablePermits;
                var refillRate = (double)config.PermitsPerWindow / config.WindowDuration.TotalSeconds;
                var retryAfter = TimeSpan.FromSeconds(permitsNeeded / refillRate);

                return Task.FromResult(RateLimitResult.Denied(retryAfter, $"Rate limit exceeded for key '{key}'"));
            }
        }

        /// <summary>
        /// Gets the current rate limit status for a key.
        /// </summary>
        public virtual RateLimitStatus GetStatus(string key)
        {
            var config = GetConfigForKey(key);

            if (_buckets.TryGetValue(key, out var bucket))
            {
                lock (bucket)
                {
                    RefillBucket(bucket, config);
                    return new RateLimitStatus
                    {
                        Key = key,
                        CurrentPermits = bucket.AvailablePermits,
                        MaxPermits = config.PermitsPerWindow,
                        WindowStart = bucket.WindowStart,
                        WindowDuration = config.WindowDuration
                    };
                }
            }

            return new RateLimitStatus
            {
                Key = key,
                CurrentPermits = config.PermitsPerWindow,
                MaxPermits = config.PermitsPerWindow,
                WindowStart = DateTime.UtcNow,
                WindowDuration = config.WindowDuration
            };
        }

        /// <summary>
        /// Resets rate limit for a key.
        /// </summary>
        public virtual void Reset(string key)
        {
            _buckets.TryRemove(key, out _);
        }

        /// <summary>
        /// Override to provide custom rate limit configuration per key.
        /// </summary>
        protected virtual RateLimitConfig GetConfigForKey(string key)
        {
            return DefaultConfig;
        }

        private void RefillBucket(RateLimitBucket bucket, RateLimitConfig config)
        {
            var now = DateTime.UtcNow;
            var elapsed = now - bucket.WindowStart;

            if (elapsed >= config.WindowDuration)
            {
                // Full window reset
                bucket.AvailablePermits = config.PermitsPerWindow;
                bucket.WindowStart = now;
            }
            else
            {
                // Gradual refill (token bucket algorithm)
                var refillRate = (double)config.PermitsPerWindow / config.WindowDuration.TotalMilliseconds;
                var timeSinceLastRefill = (now - bucket.LastRefill).TotalMilliseconds;
                var tokensToAdd = (int)(timeSinceLastRefill * refillRate);

                if (tokensToAdd > 0)
                {
                    bucket.AvailablePermits = Math.Min(
                        bucket.AvailablePermits + tokensToAdd,
                        config.PermitsPerWindow + config.BurstLimit);
                    bucket.LastRefill = now;
                }
            }
        }

        private void CleanupExpiredBuckets()
        {
            var cutoff = DateTime.UtcNow - BucketIdleTimeout;
            var keysToRemove = _buckets
                .Where(kv => kv.Value.LastAccess < cutoff)
                .Select(kv => kv.Key)
                .ToList();

            foreach (var key in keysToRemove)
            {
                _buckets.TryRemove(key, out _);
            }
        }

        public override Task StartAsync(CancellationToken ct) => Task.CompletedTask;

        public override Task StopAsync()
        {
            _cleanupTimer?.Dispose();
            _buckets.Clear();
            return Task.CompletedTask;
        }

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["FeatureType"] = "RateLimiter";
            metadata["Algorithm"] = "TokenBucket";
            metadata["ActiveBuckets"] = _buckets.Count;
            return metadata;
        }

        private sealed class RateLimitBucket
        {
            public string Key { get; }
            public int AvailablePermits { get; set; }
            public DateTime WindowStart { get; set; }
            public DateTime LastRefill { get; set; }
            public DateTime LastAccess { get; set; }

            public RateLimitBucket(string key, RateLimitConfig config)
            {
                Key = key;
                AvailablePermits = config.PermitsPerWindow;
                WindowStart = DateTime.UtcNow;
                LastRefill = DateTime.UtcNow;
                LastAccess = DateTime.UtcNow;
            }
        }
    }

    #endregion

    #region Circuit Breaker

    /// <summary>
    /// Abstract base class for circuit breaker plugins implementing the resilience policy pattern.
    /// Provides automatic failure detection, circuit opening/closing, and statistics tracking.
    /// </summary>
    public abstract class CircuitBreakerPluginBase : FeaturePluginBase, IResiliencePolicy
    {
        private readonly object _stateLock = new();
        private CircuitState _state = CircuitState.Closed;
        private DateTime _lastStateChange = DateTime.UtcNow;
        private DateTime? _lastFailure;
        private DateTime? _lastSuccess;
        private int _failureCount;
        private int _successCount;
        private readonly SlidingWindowCounter _failureWindow;
        private long _totalExecutions;
        private long _successfulExecutions;
        private long _failedExecutions;
        private long _timeoutExecutions;
        private long _rejections;
        private long _retryAttempts;
        private readonly List<double> _executionTimes = new();
        private readonly object _metricsLock = new();

        /// <summary>
        /// Unique identifier for this circuit breaker policy.
        /// </summary>
        public abstract string PolicyId { get; }

        /// <summary>
        /// Current state of the circuit breaker.
        /// </summary>
        public CircuitState State
        {
            get { lock (_stateLock) { return _state; } }
        }

        /// <summary>
        /// Configuration for this circuit breaker.
        /// </summary>
        protected virtual ResiliencePolicyConfig Config => new();

        protected CircuitBreakerPluginBase()
        {
            var config = Config;
            _failureWindow = new SlidingWindowCounter(config.FailureWindow);
        }

        /// <summary>
        /// Executes an action with circuit breaker protection.
        /// </summary>
        public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _totalExecutions);

            // Check circuit state
            if (!CanExecute())
            {
                Interlocked.Increment(ref _rejections);
                throw new CircuitBreakerOpenException(PolicyId, GetRetryAfter());
            }

            var config = Config;
            var retryCount = 0;
            Exception? lastException = null;

            while (retryCount <= config.MaxRetries)
            {
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();

                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(config.Timeout);

                    var result = await action(cts.Token);

                    sw.Stop();
                    RecordSuccess(sw.Elapsed.TotalMilliseconds);

                    return result;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    Interlocked.Increment(ref _timeoutExecutions);
                    RecordFailure();
                    throw new TimeoutException($"Operation timed out after {config.Timeout.TotalSeconds}s");
                }
                catch (Exception ex)
                {
                    lastException = ex;

                    if (!ShouldRetry(ex, config))
                    {
                        RecordFailure();
                        throw;
                    }

                    retryCount++;
                    Interlocked.Increment(ref _retryAttempts);

                    if (retryCount <= config.MaxRetries)
                    {
                        var delay = CalculateBackoff(retryCount, config);
                        await Task.Delay(delay, ct);
                    }
                }
            }

            RecordFailure();
            throw lastException ?? new InvalidOperationException("Max retries exceeded");
        }

        /// <summary>
        /// Executes an action with circuit breaker protection (no return value).
        /// </summary>
        public async Task ExecuteAsync(Func<CancellationToken, Task> action, CancellationToken ct = default)
        {
            await ExecuteAsync(async token =>
            {
                await action(token);
                return true;
            }, ct);
        }

        /// <summary>
        /// Manually resets the circuit breaker to closed state.
        /// </summary>
        public void Reset()
        {
            lock (_stateLock)
            {
                _state = CircuitState.Closed;
                _failureCount = 0;
                _successCount = 0;
                _lastStateChange = DateTime.UtcNow;
                _failureWindow.Reset();
            }
        }

        /// <summary>
        /// Gets statistics about this policy's execution.
        /// </summary>
        public ResilienceStatistics GetStatistics()
        {
            TimeSpan avgExecutionTime;
            lock (_metricsLock)
            {
                avgExecutionTime = _executionTimes.Count > 0
                    ? TimeSpan.FromMilliseconds(_executionTimes.Average())
                    : TimeSpan.Zero;
            }

            return new ResilienceStatistics
            {
                TotalExecutions = Interlocked.Read(ref _totalExecutions),
                SuccessfulExecutions = Interlocked.Read(ref _successfulExecutions),
                FailedExecutions = Interlocked.Read(ref _failedExecutions),
                TimeoutExecutions = Interlocked.Read(ref _timeoutExecutions),
                CircuitBreakerRejections = Interlocked.Read(ref _rejections),
                RetryAttempts = Interlocked.Read(ref _retryAttempts),
                LastFailure = _lastFailure,
                LastSuccess = _lastSuccess,
                AverageExecutionTime = avgExecutionTime
            };
        }

        private bool CanExecute()
        {
            var config = Config;

            lock (_stateLock)
            {
                switch (_state)
                {
                    case CircuitState.Closed:
                        return true;

                    case CircuitState.Open:
                        if (DateTime.UtcNow - _lastStateChange >= config.BreakDuration)
                        {
                            _state = CircuitState.HalfOpen;
                            _lastStateChange = DateTime.UtcNow;
                            _successCount = 0;
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

        private void RecordSuccess(double executionTimeMs)
        {
            Interlocked.Increment(ref _successfulExecutions);
            _lastSuccess = DateTime.UtcNow;

            lock (_metricsLock)
            {
                _executionTimes.Add(executionTimeMs);
                if (_executionTimes.Count > 1000)
                    _executionTimes.RemoveAt(0);
            }

            lock (_stateLock)
            {
                _successCount++;

                if (_state == CircuitState.HalfOpen)
                {
                    // After enough successes in half-open, close the circuit
                    if (_successCount >= 3)
                    {
                        _state = CircuitState.Closed;
                        _failureCount = 0;
                        _lastStateChange = DateTime.UtcNow;
                        _failureWindow.Reset();
                    }
                }
                else if (_state == CircuitState.Closed)
                {
                    // Decay failure count on success
                    _failureCount = Math.Max(0, _failureCount - 1);
                }
            }
        }

        private void RecordFailure()
        {
            Interlocked.Increment(ref _failedExecutions);
            _lastFailure = DateTime.UtcNow;

            var config = Config;

            lock (_stateLock)
            {
                _failureCount++;
                _failureWindow.Increment();

                if (_state == CircuitState.HalfOpen)
                {
                    // Any failure in half-open immediately opens the circuit
                    _state = CircuitState.Open;
                    _lastStateChange = DateTime.UtcNow;
                }
                else if (_state == CircuitState.Closed)
                {
                    // Check if we've exceeded the failure threshold
                    if (_failureWindow.Count >= config.FailureThreshold)
                    {
                        _state = CircuitState.Open;
                        _lastStateChange = DateTime.UtcNow;
                    }
                }
            }
        }

        private TimeSpan GetRetryAfter()
        {
            var config = Config;
            lock (_stateLock)
            {
                var elapsed = DateTime.UtcNow - _lastStateChange;
                var remaining = config.BreakDuration - elapsed;
                return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }
        }

        private static bool ShouldRetry(Exception ex, ResiliencePolicyConfig config)
        {
            if (config.ShouldRetry != null)
                return config.ShouldRetry(ex);

            return !config.NonRetryableExceptions.Contains(ex.GetType());
        }

        private static TimeSpan CalculateBackoff(int retryCount, ResiliencePolicyConfig config)
        {
            // Exponential backoff with jitter
            var exponentialDelay = config.RetryBaseDelay.TotalMilliseconds * Math.Pow(2, retryCount - 1);
            var jitter = Random.Shared.NextDouble() * 0.2 * exponentialDelay; // 0-20% jitter
            var totalDelay = exponentialDelay + jitter;

            return TimeSpan.FromMilliseconds(Math.Min(totalDelay, config.RetryMaxDelay.TotalMilliseconds));
        }

        public override Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public override Task StopAsync() => Task.CompletedTask;

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["FeatureType"] = "CircuitBreaker";
            metadata["PolicyId"] = PolicyId;
            metadata["CurrentState"] = State.ToString();
            return metadata;
        }

        private sealed class SlidingWindowCounter
        {
            private readonly TimeSpan _windowDuration;
            private readonly Queue<DateTime> _timestamps = new();
            private readonly object _lock = new();

            public SlidingWindowCounter(TimeSpan windowDuration)
            {
                _windowDuration = windowDuration;
            }

            public int Count
            {
                get
                {
                    lock (_lock)
                    {
                        Cleanup();
                        return _timestamps.Count;
                    }
                }
            }

            public void Increment()
            {
                lock (_lock)
                {
                    _timestamps.Enqueue(DateTime.UtcNow);
                    Cleanup();
                }
            }

            public void Reset()
            {
                lock (_lock)
                {
                    _timestamps.Clear();
                }
            }

            private void Cleanup()
            {
                var cutoff = DateTime.UtcNow - _windowDuration;
                while (_timestamps.Count > 0 && _timestamps.Peek() < cutoff)
                {
                    _timestamps.Dequeue();
                }
            }
        }
    }

    /// <summary>
    /// Exception thrown when a circuit breaker is open.
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

    #endregion

    #region Transaction Manager

    /// <summary>
    /// Abstract base class for transaction manager plugins.
    /// Provides common infrastructure for distributed transaction coordination.
    /// </summary>
    public abstract class TransactionManagerPluginBase : FeaturePluginBase, ITransactionManager
    {
        private readonly AsyncLocal<TransactionScopeImpl?> _ambientTransaction = new();
        private readonly ConcurrentDictionary<string, TransactionScopeImpl> _activeTransactions = new();
        private readonly Timer? _timeoutTimer;

        /// <summary>
        /// Default transaction timeout.
        /// </summary>
        protected virtual TimeSpan DefaultTimeout => TimeSpan.FromMinutes(1);

        /// <summary>
        /// How often to check for timed-out transactions.
        /// </summary>
        protected virtual TimeSpan TimeoutCheckInterval => TimeSpan.FromSeconds(10);

        protected TransactionManagerPluginBase()
        {
            if (TimeoutCheckInterval > TimeSpan.Zero)
            {
                _timeoutTimer = new Timer(
                    _ => CheckTimeouts(),
                    null,
                    TimeoutCheckInterval,
                    TimeoutCheckInterval);
            }
        }

        /// <summary>
        /// Gets the current ambient transaction, if any.
        /// </summary>
        public ITransactionScope? Current => _ambientTransaction.Value;

        /// <summary>
        /// Creates a new transaction scope.
        /// </summary>
        public ITransactionScope BeginTransaction(TransactionOptions? options = null)
        {
            var effectiveOptions = options ?? new TransactionOptions { Timeout = DefaultTimeout };
            var transaction = new TransactionScopeImpl(
                this,
                effectiveOptions,
                _ambientTransaction.Value);

            _activeTransactions[transaction.TransactionId] = transaction;
            _ambientTransaction.Value = transaction;

            OnTransactionStarted(transaction);

            return transaction;
        }

        /// <summary>
        /// Called when a transaction is started. Override for custom logic.
        /// </summary>
        protected virtual void OnTransactionStarted(ITransactionScope transaction) { }

        /// <summary>
        /// Called when a transaction is committed. Override for custom logic.
        /// </summary>
        protected virtual Task OnTransactionCommittedAsync(ITransactionScope transaction) => Task.CompletedTask;

        /// <summary>
        /// Called when a transaction is rolled back. Override for custom logic.
        /// </summary>
        protected virtual Task OnTransactionRolledBackAsync(ITransactionScope transaction, Exception? reason) => Task.CompletedTask;

        internal async Task CommitTransactionAsync(TransactionScopeImpl transaction)
        {
            await OnTransactionCommittedAsync(transaction);
            _activeTransactions.TryRemove(transaction.TransactionId, out _);

            if (_ambientTransaction.Value == transaction)
            {
                _ambientTransaction.Value = transaction.Parent;
            }
        }

        internal async Task RollbackTransactionAsync(TransactionScopeImpl transaction, Exception? reason)
        {
            await OnTransactionRolledBackAsync(transaction, reason);
            _activeTransactions.TryRemove(transaction.TransactionId, out _);

            if (_ambientTransaction.Value == transaction)
            {
                _ambientTransaction.Value = transaction.Parent;
            }
        }

        private void CheckTimeouts()
        {
            var now = DateTime.UtcNow;
            foreach (var kv in _activeTransactions)
            {
                var transaction = kv.Value;
                if (transaction.State == TransactionState.Active)
                {
                    var elapsed = now - transaction.StartedAt;
                    if (elapsed > transaction.Timeout)
                    {
                        // Rollback timed-out transaction
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await transaction.RollbackAsync();
                            }
                            catch { /* Log but don't throw */ }
                        });
                    }
                }
            }
        }

        public override Task StartAsync(CancellationToken ct) => Task.CompletedTask;

        public override Task StopAsync()
        {
            _timeoutTimer?.Dispose();

            // Rollback all active transactions
            foreach (var transaction in _activeTransactions.Values.ToList())
            {
                try
                {
                    transaction.RollbackAsync().Wait(TimeSpan.FromSeconds(5));
                }
                catch { /* Ignore during shutdown */ }
            }

            return Task.CompletedTask;
        }

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["FeatureType"] = "TransactionManager";
            metadata["ActiveTransactions"] = _activeTransactions.Count;
            metadata["DefaultTimeoutSeconds"] = DefaultTimeout.TotalSeconds;
            return metadata;
        }

        internal sealed class TransactionScopeImpl : ITransactionScope
        {
            private readonly TransactionManagerPluginBase _manager;
            private readonly List<Func<Task>> _compensations = new();
            private readonly Dictionary<string, object> _enlistedResources = new();
            private readonly object _lock = new();
            private TransactionState _state = TransactionState.Active;

            public string TransactionId { get; } = Guid.NewGuid().ToString("N");
            public TransactionState State => _state;
            public DateTime StartedAt { get; } = DateTime.UtcNow;
            public TimeSpan Timeout { get; }
            public TransactionScopeImpl? Parent { get; }

            public TransactionScopeImpl(
                TransactionManagerPluginBase manager,
                TransactionOptions options,
                TransactionScopeImpl? parent)
            {
                _manager = manager;
                Timeout = options.Timeout;
                Parent = parent;
            }

            public async Task CommitAsync(CancellationToken ct = default)
            {
                lock (_lock)
                {
                    if (_state != TransactionState.Active)
                        throw new InvalidOperationException($"Cannot commit transaction in state: {_state}");
                    _state = TransactionState.Committing;
                }

                try
                {
                    await _manager.CommitTransactionAsync(this);
                    _state = TransactionState.Committed;
                }
                catch
                {
                    _state = TransactionState.Failed;
                    throw;
                }
            }

            public async Task RollbackAsync(CancellationToken ct = default)
            {
                lock (_lock)
                {
                    if (_state != TransactionState.Active && _state != TransactionState.Failed)
                        return; // Already rolled back or committed
                    _state = TransactionState.RollingBack;
                }

                // Execute compensations in reverse order
                var errors = new List<Exception>();
                for (int i = _compensations.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        await _compensations[i]();
                    }
                    catch (Exception ex)
                    {
                        errors.Add(ex);
                    }
                }

                await _manager.RollbackTransactionAsync(this, errors.FirstOrDefault());
                _state = TransactionState.RolledBack;

                if (errors.Count > 0)
                {
                    throw new AggregateException("Some compensations failed during rollback", errors);
                }
            }

            public void RegisterCompensation(Func<Task> compensationAction)
            {
                if (compensationAction == null) throw new ArgumentNullException(nameof(compensationAction));

                lock (_lock)
                {
                    if (_state != TransactionState.Active)
                        throw new InvalidOperationException("Cannot register compensation on inactive transaction");
                    _compensations.Add(compensationAction);
                }
            }

            public void EnlistResource(string resourceId, object resource)
            {
                lock (_lock)
                {
                    if (_state != TransactionState.Active)
                        throw new InvalidOperationException("Cannot enlist resource on inactive transaction");
                    _enlistedResources[resourceId] = resource;
                }
            }

            public async ValueTask DisposeAsync()
            {
                if (_state == TransactionState.Active)
                {
                    await RollbackAsync();
                }
            }
        }
    }

    #endregion

    #region RAID Provider

    /// <summary>
    /// Interface for RAID storage providers.
    /// Supports various RAID levels for data redundancy and performance.
    /// </summary>
    public interface IRaidProvider
    {
        /// <summary>
        /// Current RAID level.
        /// </summary>
        RaidLevel Level { get; }

        /// <summary>
        /// Number of storage providers in the array.
        /// </summary>
        int ProviderCount { get; }

        /// <summary>
        /// Number of providers that can fail while maintaining data availability.
        /// </summary>
        int FaultTolerance { get; }

        /// <summary>
        /// Current health status of the RAID array.
        /// </summary>
        RaidArrayStatus ArrayStatus { get; }

        /// <summary>
        /// Saves data using the configured RAID level.
        /// </summary>
        Task SaveAsync(string key, Stream data, CancellationToken ct = default);

        /// <summary>
        /// Loads data, reconstructing from parity if necessary.
        /// </summary>
        Task<Stream> LoadAsync(string key, CancellationToken ct = default);

        /// <summary>
        /// Deletes data from all providers in the array.
        /// </summary>
        Task DeleteAsync(string key, CancellationToken ct = default);

        /// <summary>
        /// Checks if data exists in the array.
        /// </summary>
        Task<bool> ExistsAsync(string key, CancellationToken ct = default);

        /// <summary>
        /// Initiates a rebuild of a failed provider.
        /// </summary>
        Task<RebuildResult> RebuildAsync(int providerIndex, CancellationToken ct = default);

        /// <summary>
        /// Gets the health status of individual providers.
        /// </summary>
        IReadOnlyList<RaidProviderHealth> GetProviderHealth();

        /// <summary>
        /// Scrubs the array, verifying parity and data integrity.
        /// </summary>
        Task<ScrubResult> ScrubAsync(CancellationToken ct = default);
    }

    /// <summary>
    /// Supported RAID levels.
    /// Comprehensive enum supporting standard, nested, enhanced, vendor-specific, and ZFS RAID levels.
    /// </summary>
    public enum RaidLevel
    {
        // Standard RAID Levels
        RAID_0,     // Striping (performance)
        RAID_1,     // Mirroring (redundancy)
        RAID_2,     // Bit-level striping with Hamming code
        RAID_3,     // Byte-level striping with dedicated parity
        RAID_4,     // Block-level striping with dedicated parity
        RAID_5,     // Block-level striping with distributed parity
        RAID_6,     // Block-level striping with dual distributed parity

        // Nested RAID Levels
        RAID_10,    // RAID 1+0 (mirrored stripes)
        RAID_01,    // RAID 0+1 (striped mirrors)
        RAID_03,    // RAID 0+3 (striped dedicated parity)
        RAID_50,    // RAID 5+0 (striped RAID 5 sets)
        RAID_60,    // RAID 6+0 (striped RAID 6 sets)
        RAID_100,   // RAID 10+0 (striped mirrors of mirrors)

        // Enhanced RAID Levels
        RAID_1E,    // RAID 1 Enhanced (mirrored striping)
        RAID_5E,    // RAID 5 with hot spare
        RAID_5EE,   // RAID 5 Enhanced with distributed spare
        RAID_6E,    // RAID 6 Enhanced with extra parity

        // Vendor-Specific RAID
        RAID_DP,    // NetApp Double Parity (RAID 6 variant)
        RAID_S,     // Dell/EMC Parity RAID (RAID 5 variant)
        RAID_7,     // Cached striping with parity
        RAID_FR,    // Fast Rebuild (optimized RAID 6)

        // ZFS RAID Levels
        RAID_Z1,    // ZFS single parity (RAID 5 equivalent)
        RAID_Z2,    // ZFS double parity (RAID 6 equivalent)
        RAID_Z3,    // ZFS triple parity

        // Advanced/Proprietary RAID
        RAID_MD10,      // Linux MD RAID 10 (near/far/offset layouts)
        RAID_Adaptive,  // IBM Adaptive RAID (auto-tuning)
        RAID_Beyond,    // Drobo BeyondRAID (single/dual parity)
        RAID_Unraid,    // Unraid parity system (1-2 parity disks)
        RAID_Declustered, // Declustered/Distributed RAID

        // Extended RAID Levels
        RAID_71,        // RAID 7.1 - Enhanced RAID 7 with read cache
        RAID_72,        // RAID 7.2 - Enhanced RAID 7 with write-back cache
        RAID_NM,        // RAID N+M - Flexible N data + M parity
        RAID_Matrix,    // Intel Matrix RAID - Multiple RAID types on same disks
        RAID_JBOD,      // Just a Bunch of Disks - Simple concatenation
        RAID_Crypto,    // Crypto SoftRAID - Encrypted software RAID
        RAID_DUP,       // Btrfs DUP Profile - Duplicate on same device
        RAID_DDP,       // NetApp Dynamic Disk Pool
        RAID_SPAN,      // Simple disk spanning/concatenation
        RAID_BIG,       // Concatenated volumes (Linux md BIG)
        RAID_MAID,      // Massive Array of Idle Disks - Power managed RAID
        RAID_Linear     // Linear mode - Sequential concatenation
    }

    /// <summary>
    /// Status of the RAID array.
    /// </summary>
    public enum RaidArrayStatus
    {
        Healthy,
        Degraded,
        Rebuilding,
        Failed
    }

    /// <summary>
    /// Health information for a single RAID provider.
    /// </summary>
    public class RaidProviderHealth
    {
        public int Index { get; init; }
        public bool IsHealthy { get; init; }
        public bool IsRebuilding { get; init; }
        public double RebuildProgress { get; init; }
        public DateTime? LastHealthCheck { get; init; }
        public string? ErrorMessage { get; init; }
    }

    /// <summary>
    /// Result of a rebuild operation.
    /// </summary>
    public class RebuildResult
    {
        public bool Success { get; init; }
        public int ProviderIndex { get; init; }
        public TimeSpan Duration { get; init; }
        public long BytesRebuilt { get; init; }
        public string? ErrorMessage { get; init; }
    }

    /// <summary>
    /// Result of a scrub operation.
    /// </summary>
    public class ScrubResult
    {
        public bool Success { get; init; }
        public TimeSpan Duration { get; init; }
        public long BytesScanned { get; init; }
        public int ErrorsFound { get; init; }
        public int ErrorsCorrected { get; init; }
        public List<string> UncorrectableErrors { get; init; } = new();
    }

    /// <summary>
    /// Abstract base class for RAID provider plugins.
    /// Provides common RAID functionality including parity calculation and rebuild logic.
    /// </summary>
    public abstract class RaidProviderPluginBase : FeaturePluginBase, IRaidProvider
    {
        /// <summary>
        /// Current RAID level. Must be set by derived classes.
        /// </summary>
        public abstract RaidLevel Level { get; }

        /// <summary>
        /// Number of storage providers in the array.
        /// </summary>
        public abstract int ProviderCount { get; }

        /// <summary>
        /// Fault tolerance is determined by RAID level.
        /// Returns the number of drives that can fail while maintaining data availability.
        /// </summary>
        public virtual int FaultTolerance => Level switch
        {
            // Standard RAID
            RaidLevel.RAID_0 => 0,
            RaidLevel.RAID_1 => ProviderCount - 1,
            RaidLevel.RAID_2 => 1,
            RaidLevel.RAID_3 => 1,
            RaidLevel.RAID_4 => 1,
            RaidLevel.RAID_5 => 1,
            RaidLevel.RAID_6 => 2,

            // Nested RAID
            RaidLevel.RAID_10 => ProviderCount / 2,
            RaidLevel.RAID_01 => ProviderCount / 2,
            RaidLevel.RAID_03 => 1,
            RaidLevel.RAID_50 => 1 * (ProviderCount / 3), // 1 per sub-array
            RaidLevel.RAID_60 => 2 * (ProviderCount / 4), // 2 per sub-array
            RaidLevel.RAID_100 => ProviderCount / 2,

            // Enhanced RAID
            RaidLevel.RAID_1E => ProviderCount / 2,
            RaidLevel.RAID_5E => 1,
            RaidLevel.RAID_5EE => 1,
            RaidLevel.RAID_6E => 2,

            // Vendor-Specific
            RaidLevel.RAID_DP => 2,
            RaidLevel.RAID_S => 1,
            RaidLevel.RAID_7 => 1,
            RaidLevel.RAID_FR => 2,

            // ZFS RAID
            RaidLevel.RAID_Z1 => 1,
            RaidLevel.RAID_Z2 => 2,
            RaidLevel.RAID_Z3 => 3,

            // Advanced/Proprietary
            RaidLevel.RAID_MD10 => ProviderCount / 2,
            RaidLevel.RAID_Adaptive => 1,
            RaidLevel.RAID_Beyond => 2,
            RaidLevel.RAID_Unraid => 2,
            RaidLevel.RAID_Declustered => 2,

            // No fault tolerance
            RaidLevel.RAID_JBOD => 0,
            RaidLevel.RAID_SPAN => 0,
            RaidLevel.RAID_BIG => 0,
            RaidLevel.RAID_Linear => 0,

            _ => 0
        };

        /// <summary>
        /// Current array status. Override to track actual health.
        /// </summary>
        public virtual RaidArrayStatus ArrayStatus => RaidArrayStatus.Healthy;

        public abstract Task SaveAsync(string key, Stream data, CancellationToken ct = default);
        public abstract Task<Stream> LoadAsync(string key, CancellationToken ct = default);
        public abstract Task DeleteAsync(string key, CancellationToken ct = default);
        public abstract Task<bool> ExistsAsync(string key, CancellationToken ct = default);
        public abstract Task<RebuildResult> RebuildAsync(int providerIndex, CancellationToken ct = default);
        public abstract IReadOnlyList<RaidProviderHealth> GetProviderHealth();
        public abstract Task<ScrubResult> ScrubAsync(CancellationToken ct = default);

        /// <summary>
        /// Calculates XOR parity for RAID 5/6.
        /// </summary>
        protected static byte[] CalculateXorParity(byte[][] dataBlocks)
        {
            if (dataBlocks.Length == 0) return Array.Empty<byte>();

            var parityLength = dataBlocks.Max(b => b.Length);
            var parity = new byte[parityLength];

            foreach (var block in dataBlocks)
            {
                for (int i = 0; i < block.Length; i++)
                {
                    parity[i] ^= block[i];
                }
            }

            return parity;
        }

        /// <summary>
        /// Reconstructs a missing block using XOR parity.
        /// </summary>
        protected static byte[] ReconstructFromParity(byte[][] availableBlocks, byte[] parity, int missingIndex)
        {
            var reconstructed = (byte[])parity.Clone();

            for (int i = 0; i < availableBlocks.Length; i++)
            {
                if (i != missingIndex && availableBlocks[i] != null)
                {
                    for (int j = 0; j < availableBlocks[i].Length && j < reconstructed.Length; j++)
                    {
                        reconstructed[j] ^= availableBlocks[i][j];
                    }
                }
            }

            return reconstructed;
        }

        public override Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public override Task StopAsync() => Task.CompletedTask;

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["FeatureType"] = "RaidProvider";
            metadata["RaidLevel"] = Level.ToString();
            metadata["ProviderCount"] = ProviderCount;
            metadata["FaultTolerance"] = FaultTolerance;
            metadata["ArrayStatus"] = ArrayStatus.ToString();
            return metadata;
        }
    }

    #endregion

    #region Erasure Coding Provider

    /// <summary>
    /// Interface for erasure coding providers.
    /// Provides space-efficient data protection through mathematical encoding.
    /// </summary>
    public interface IErasureCodingProvider
    {
        /// <summary>
        /// Number of data shards (k).
        /// </summary>
        int DataShardCount { get; }

        /// <summary>
        /// Number of parity shards (m).
        /// </summary>
        int ParityShardCount { get; }

        /// <summary>
        /// Total number of shards (k + m).
        /// </summary>
        int TotalShardCount { get; }

        /// <summary>
        /// Encodes data into shards with parity.
        /// </summary>
        Task<ErasureCodedData> EncodeAsync(Stream data, CancellationToken ct = default);

        /// <summary>
        /// Decodes data from available shards.
        /// </summary>
        Task<Stream> DecodeAsync(ErasureCodedData codedData, CancellationToken ct = default);

        /// <summary>
        /// Reconstructs missing shards from available shards.
        /// </summary>
        Task<byte[][]> ReconstructShardsAsync(byte[][] shards, bool[] shardPresent, CancellationToken ct = default);

        /// <summary>
        /// Verifies the integrity of shards.
        /// </summary>
        Task<bool> VerifyAsync(ErasureCodedData codedData, CancellationToken ct = default);
    }

    /// <summary>
    /// Container for erasure-coded data.
    /// </summary>
    public class ErasureCodedData
    {
        /// <summary>
        /// All shards including data and parity.
        /// </summary>
        public byte[][] Shards { get; init; } = Array.Empty<byte[]>();

        /// <summary>
        /// Original data size before encoding.
        /// </summary>
        public long OriginalSize { get; init; }

        /// <summary>
        /// Size of each shard.
        /// </summary>
        public int ShardSize { get; init; }

        /// <summary>
        /// Number of data shards.
        /// </summary>
        public int DataShardCount { get; init; }

        /// <summary>
        /// Number of parity shards.
        /// </summary>
        public int ParityShardCount { get; init; }

        /// <summary>
        /// Encoding algorithm used.
        /// </summary>
        public string Algorithm { get; init; } = "ReedSolomon";
    }

    /// <summary>
    /// Abstract base class for erasure coding provider plugins.
    /// Provides common Reed-Solomon encoding/decoding infrastructure.
    /// </summary>
    public abstract class ErasureCodingPluginBase : FeaturePluginBase, IErasureCodingProvider
    {
        /// <summary>
        /// Number of data shards.
        /// </summary>
        public abstract int DataShardCount { get; }

        /// <summary>
        /// Number of parity shards.
        /// </summary>
        public abstract int ParityShardCount { get; }

        /// <summary>
        /// Total shards = data + parity.
        /// </summary>
        public int TotalShardCount => DataShardCount + ParityShardCount;

        public abstract Task<ErasureCodedData> EncodeAsync(Stream data, CancellationToken ct = default);
        public abstract Task<Stream> DecodeAsync(ErasureCodedData codedData, CancellationToken ct = default);
        public abstract Task<byte[][]> ReconstructShardsAsync(byte[][] shards, bool[] shardPresent, CancellationToken ct = default);
        public abstract Task<bool> VerifyAsync(ErasureCodedData codedData, CancellationToken ct = default);

        public override Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public override Task StopAsync() => Task.CompletedTask;

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["FeatureType"] = "ErasureCoding";
            metadata["DataShards"] = DataShardCount;
            metadata["ParityShards"] = ParityShardCount;
            metadata["StorageEfficiency"] = (double)DataShardCount / TotalShardCount;
            metadata["FaultTolerance"] = ParityShardCount;
            return metadata;
        }
    }

    #endregion

    #region Compliance Provider

    /// <summary>
    /// Interface for compliance providers.
    /// Handles regulatory compliance requirements (GDPR, HIPAA, SOC2, etc.).
    /// </summary>
    public interface IComplianceProvider
    {
        /// <summary>
        /// Supported compliance frameworks.
        /// </summary>
        IReadOnlyList<string> SupportedFrameworks { get; }

        /// <summary>
        /// Validates data against compliance requirements.
        /// </summary>
        Task<ComplianceValidationResult> ValidateAsync(
            string framework,
            object data,
            Dictionary<string, object>? context = null,
            CancellationToken ct = default);

        /// <summary>
        /// Gets the current compliance status.
        /// </summary>
        Task<ComplianceStatus> GetStatusAsync(string framework, CancellationToken ct = default);

        /// <summary>
        /// Generates a compliance report.
        /// </summary>
        Task<ComplianceReport> GenerateReportAsync(
            string framework,
            DateTime? startDate = null,
            DateTime? endDate = null,
            CancellationToken ct = default);

        /// <summary>
        /// Registers data subject rights request (GDPR/CCPA).
        /// </summary>
        Task<string> RegisterDataSubjectRequestAsync(
            DataSubjectRequest request,
            CancellationToken ct = default);

        /// <summary>
        /// Gets retention policy for data type.
        /// </summary>
        Task<RetentionPolicy> GetRetentionPolicyAsync(
            string dataType,
            string? framework = null,
            CancellationToken ct = default);
    }

    /// <summary>
    /// Result of compliance validation.
    /// </summary>
    public class ComplianceValidationResult
    {
        public bool IsCompliant { get; init; }
        public string Framework { get; init; } = string.Empty;
        public List<ComplianceViolation> Violations { get; init; } = new();
        public List<ComplianceWarning> Warnings { get; init; } = new();
        public DateTime ValidatedAt { get; init; }
    }

    /// <summary>
    /// A compliance violation.
    /// </summary>
    public class ComplianceViolation
    {
        public string Code { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;
        public string Severity { get; init; } = "High";
        public string? Remediation { get; init; }
    }

    /// <summary>
    /// A compliance warning.
    /// </summary>
    public class ComplianceWarning
    {
        public string Code { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;
        public string? Recommendation { get; init; }
    }

    /// <summary>
    /// Current compliance status.
    /// </summary>
    public class ComplianceStatus
    {
        public string Framework { get; init; } = string.Empty;
        public bool IsCompliant { get; init; }
        public double ComplianceScore { get; init; }
        public int TotalControls { get; init; }
        public int PassingControls { get; init; }
        public int FailingControls { get; init; }
        public DateTime LastAssessment { get; init; }
        public DateTime? NextAssessmentDue { get; init; }
    }

    /// <summary>
    /// Compliance report.
    /// </summary>
    public class ComplianceReport
    {
        public string Framework { get; init; } = string.Empty;
        public DateTime GeneratedAt { get; init; }
        public DateTime ReportingPeriodStart { get; init; }
        public DateTime ReportingPeriodEnd { get; init; }
        public ComplianceStatus Status { get; init; } = new();
        public List<ControlAssessment> ControlAssessments { get; init; } = new();
        public byte[]? ReportDocument { get; init; }
        public string? ReportFormat { get; init; }
    }

    /// <summary>
    /// Assessment of a single compliance control.
    /// </summary>
    public class ControlAssessment
    {
        public string ControlId { get; init; } = string.Empty;
        public string ControlName { get; init; } = string.Empty;
        public bool IsPassing { get; init; }
        public string? Evidence { get; init; }
        public string? Notes { get; init; }
    }

    /// <summary>
    /// Data subject request (GDPR/CCPA).
    /// </summary>
    public class DataSubjectRequest
    {
        public string SubjectId { get; init; } = string.Empty;
        public string RequestType { get; init; } = string.Empty; // Access, Deletion, Portability, Rectification
        public string? Email { get; init; }
        public Dictionary<string, object> Details { get; init; } = new();
    }

    /// <summary>
    /// Data retention policy.
    /// </summary>
    public class RetentionPolicy
    {
        public string DataType { get; init; } = string.Empty;
        public TimeSpan RetentionPeriod { get; init; }
        public string? LegalBasis { get; init; }
        public bool RequiresSecureDeletion { get; init; }
        public List<string> ApplicableFrameworks { get; init; } = new();
    }

    /// <summary>
    /// Abstract base class for compliance provider plugins.
    /// </summary>
    public abstract class ComplianceProviderPluginBase : FeaturePluginBase, IComplianceProvider
    {
        public abstract IReadOnlyList<string> SupportedFrameworks { get; }

        public abstract Task<ComplianceValidationResult> ValidateAsync(
            string framework,
            object data,
            Dictionary<string, object>? context = null,
            CancellationToken ct = default);

        public abstract Task<ComplianceStatus> GetStatusAsync(string framework, CancellationToken ct = default);

        public abstract Task<ComplianceReport> GenerateReportAsync(
            string framework,
            DateTime? startDate = null,
            DateTime? endDate = null,
            CancellationToken ct = default);

        public abstract Task<string> RegisterDataSubjectRequestAsync(
            DataSubjectRequest request,
            CancellationToken ct = default);

        public abstract Task<RetentionPolicy> GetRetentionPolicyAsync(
            string dataType,
            string? framework = null,
            CancellationToken ct = default);

        public override Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public override Task StopAsync() => Task.CompletedTask;

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["FeatureType"] = "ComplianceProvider";
            metadata["SupportedFrameworks"] = SupportedFrameworks.ToArray();
            return metadata;
        }
    }

    #endregion

    #region IAM Provider

    /// <summary>
    /// Interface for Identity and Access Management providers.
    /// Handles authentication, authorization, and identity federation.
    /// </summary>
    public interface IIAMProvider
    {
        /// <summary>
        /// Supported authentication methods.
        /// </summary>
        IReadOnlyList<string> SupportedAuthMethods { get; }

        /// <summary>
        /// Authenticates a user with the given credentials.
        /// </summary>
        Task<AuthenticationResult> AuthenticateAsync(
            AuthenticationRequest request,
            CancellationToken ct = default);

        /// <summary>
        /// Validates an authentication token.
        /// </summary>
        Task<TokenValidationResult> ValidateTokenAsync(
            string token,
            CancellationToken ct = default);

        /// <summary>
        /// Refreshes an authentication token.
        /// </summary>
        Task<AuthenticationResult> RefreshTokenAsync(
            string refreshToken,
            CancellationToken ct = default);

        /// <summary>
        /// Revokes an authentication token.
        /// </summary>
        Task<bool> RevokeTokenAsync(
            string token,
            CancellationToken ct = default);

        /// <summary>
        /// Checks if a principal has a specific permission.
        /// </summary>
        Task<AuthorizationResult> AuthorizeAsync(
            ClaimsPrincipal principal,
            string resource,
            string action,
            CancellationToken ct = default);

        /// <summary>
        /// Gets the roles for a principal.
        /// </summary>
        Task<IReadOnlyList<string>> GetRolesAsync(
            string principalId,
            CancellationToken ct = default);

        /// <summary>
        /// Assigns a role to a principal.
        /// </summary>
        Task<bool> AssignRoleAsync(
            string principalId,
            string role,
            CancellationToken ct = default);

        /// <summary>
        /// Removes a role from a principal.
        /// </summary>
        Task<bool> RemoveRoleAsync(
            string principalId,
            string role,
            CancellationToken ct = default);
    }

    /// <summary>
    /// Authentication request.
    /// </summary>
    public class AuthenticationRequest
    {
        public string Method { get; init; } = "password";
        public string? Username { get; init; }
        public string? Password { get; init; }
        public string? Token { get; init; }
        public string? Provider { get; init; }
        public Dictionary<string, object> Claims { get; init; } = new();
    }

    /// <summary>
    /// Authentication result.
    /// </summary>
    public class AuthenticationResult
    {
        public bool Success { get; init; }
        public string? AccessToken { get; init; }
        public string? RefreshToken { get; init; }
        public DateTime? ExpiresAt { get; init; }
        public string? PrincipalId { get; init; }
        public IReadOnlyList<string> Roles { get; init; } = Array.Empty<string>();
        public string? ErrorCode { get; init; }
        public string? ErrorMessage { get; init; }
    }

    /// <summary>
    /// Token validation result.
    /// </summary>
    public class TokenValidationResult
    {
        public bool IsValid { get; init; }
        public ClaimsPrincipal? Principal { get; init; }
        public DateTime? ExpiresAt { get; init; }
        public string? ErrorCode { get; init; }
        public string? ErrorMessage { get; init; }
    }

    /// <summary>
    /// Authorization result.
    /// </summary>
    public class AuthorizationResult
    {
        public bool IsAuthorized { get; init; }
        public string Resource { get; init; } = string.Empty;
        public string Action { get; init; } = string.Empty;
        public string? DenialReason { get; init; }
        public IReadOnlyList<string> MatchedPolicies { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// Abstract base class for IAM provider plugins.
    /// </summary>
    public abstract class IAMProviderPluginBase : FeaturePluginBase, IIAMProvider
    {
        public abstract IReadOnlyList<string> SupportedAuthMethods { get; }

        public abstract Task<AuthenticationResult> AuthenticateAsync(
            AuthenticationRequest request,
            CancellationToken ct = default);

        public abstract Task<TokenValidationResult> ValidateTokenAsync(
            string token,
            CancellationToken ct = default);

        public abstract Task<AuthenticationResult> RefreshTokenAsync(
            string refreshToken,
            CancellationToken ct = default);

        public abstract Task<bool> RevokeTokenAsync(
            string token,
            CancellationToken ct = default);

        public abstract Task<AuthorizationResult> AuthorizeAsync(
            ClaimsPrincipal principal,
            string resource,
            string action,
            CancellationToken ct = default);

        public abstract Task<IReadOnlyList<string>> GetRolesAsync(
            string principalId,
            CancellationToken ct = default);

        public abstract Task<bool> AssignRoleAsync(
            string principalId,
            string role,
            CancellationToken ct = default);

        public abstract Task<bool> RemoveRoleAsync(
            string principalId,
            string role,
            CancellationToken ct = default);

        public override Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public override Task StopAsync() => Task.CompletedTask;

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["FeatureType"] = "IAMProvider";
            metadata["SupportedAuthMethods"] = SupportedAuthMethods.ToArray();
            return metadata;
        }
    }

    #endregion
}
