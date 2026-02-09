using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateResilience.Strategies.RateLimiting;

/// <summary>
/// Token bucket rate limiting strategy.
/// </summary>
public sealed class TokenBucketRateLimitingStrategy : ResilienceStrategyBase
{
    private double _tokens;
    private DateTimeOffset _lastRefill;
    private readonly object _lock = new();

    private readonly double _bucketCapacity;
    private readonly double _refillRate;
    private readonly TimeSpan _refillInterval;

    public TokenBucketRateLimitingStrategy()
        : this(bucketCapacity: 100, refillRate: 10, refillInterval: TimeSpan.FromSeconds(1))
    {
    }

    public TokenBucketRateLimitingStrategy(double bucketCapacity, double refillRate, TimeSpan refillInterval)
    {
        _bucketCapacity = bucketCapacity;
        _refillRate = refillRate;
        _refillInterval = refillInterval;
        _tokens = bucketCapacity;
        _lastRefill = DateTimeOffset.UtcNow;
    }

    public override string StrategyId => "rate-limit-token-bucket";
    public override string StrategyName => "Token Bucket Rate Limiter";
    public override string Category => "RateLimiting";

    public override ResilienceCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Token Bucket Rate Limiter",
        Description = "Classic token bucket algorithm allowing bursts while maintaining average rate - tokens refill at constant rate",
        Category = "RateLimiting",
        ProvidesFaultTolerance = false,
        ProvidesLoadManagement = true,
        SupportsAdaptiveBehavior = false,
        SupportsDistributedCoordination = false,
        TypicalLatencyOverheadMs = 0.01,
        MemoryFootprint = "Low"
    };

    /// <summary>
    /// Gets the current number of available tokens.
    /// </summary>
    public double AvailableTokens
    {
        get
        {
            lock (_lock)
            {
                RefillTokens();
                return _tokens;
            }
        }
    }

    /// <summary>
    /// Tries to acquire tokens without blocking.
    /// </summary>
    public bool TryAcquire(double tokens = 1)
    {
        lock (_lock)
        {
            RefillTokens();

            if (_tokens >= tokens)
            {
                _tokens -= tokens;
                return true;
            }

            return false;
        }
    }

    private void RefillTokens()
    {
        var now = DateTimeOffset.UtcNow;
        var elapsed = now - _lastRefill;
        var intervalsElapsed = elapsed.TotalMilliseconds / _refillInterval.TotalMilliseconds;
        var tokensToAdd = intervalsElapsed * _refillRate;

        _tokens = Math.Min(_bucketCapacity, _tokens + tokensToAdd);
        _lastRefill = now;
    }

    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        ResilienceContext? context,
        CancellationToken cancellationToken)
    {
        var startTime = DateTimeOffset.UtcNow;

        if (!TryAcquire())
        {
            return new ResilienceResult<T>
            {
                Success = false,
                Exception = new RateLimitExceededException("Rate limit exceeded - no tokens available"),
                Attempts = 0,
                TotalDuration = TimeSpan.Zero,
                Metadata = { ["availableTokens"] = _tokens }
            };
        }

        try
        {
            var result = await operation(cancellationToken);

            return new ResilienceResult<T>
            {
                Success = true,
                Value = result,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime,
                Metadata = { ["availableTokens"] = _tokens }
            };
        }
        catch (Exception ex)
        {
            return new ResilienceResult<T>
            {
                Success = false,
                Exception = ex,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime
            };
        }
    }

    public override void Reset()
    {
        base.Reset();
        lock (_lock)
        {
            _tokens = _bucketCapacity;
            _lastRefill = DateTimeOffset.UtcNow;
        }
    }

    protected override string? GetCurrentState() => $"Tokens: {_tokens:F1}/{_bucketCapacity}";
}

/// <summary>
/// Leaky bucket rate limiting strategy.
/// </summary>
public sealed class LeakyBucketRateLimitingStrategy : ResilienceStrategyBase
{
    private readonly ConcurrentQueue<TaskCompletionSource<bool>> _queue = new();
    private readonly SemaphoreSlim _processingLock = new(1, 1);
    private readonly Timer _leakTimer;
    private bool _disposed;

    private readonly int _bucketCapacity;
    private readonly TimeSpan _leakInterval;

    public LeakyBucketRateLimitingStrategy()
        : this(bucketCapacity: 100, leakInterval: TimeSpan.FromMilliseconds(100))
    {
    }

    public LeakyBucketRateLimitingStrategy(int bucketCapacity, TimeSpan leakInterval)
    {
        _bucketCapacity = bucketCapacity;
        _leakInterval = leakInterval;
        _leakTimer = new Timer(ProcessQueue, null, _leakInterval, _leakInterval);
    }

    public override string StrategyId => "rate-limit-leaky-bucket";
    public override string StrategyName => "Leaky Bucket Rate Limiter";
    public override string Category => "RateLimiting";

    public override ResilienceCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Leaky Bucket Rate Limiter",
        Description = "Leaky bucket algorithm providing smooth output rate by queuing requests and processing at constant rate",
        Category = "RateLimiting",
        ProvidesFaultTolerance = false,
        ProvidesLoadManagement = true,
        SupportsAdaptiveBehavior = false,
        SupportsDistributedCoordination = false,
        TypicalLatencyOverheadMs = 0.5,
        MemoryFootprint = "Medium"
    };

    /// <summary>
    /// Gets the current queue length.
    /// </summary>
    public int QueueLength => _queue.Count;

    private void ProcessQueue(object? state)
    {
        if (_disposed) return;

        if (_queue.TryDequeue(out var tcs))
        {
            tcs.TrySetResult(true);
        }
    }

    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        ResilienceContext? context,
        CancellationToken cancellationToken)
    {
        var startTime = DateTimeOffset.UtcNow;

        if (_queue.Count >= _bucketCapacity)
        {
            return new ResilienceResult<T>
            {
                Success = false,
                Exception = new RateLimitExceededException("Rate limit exceeded - queue is full"),
                Attempts = 0,
                TotalDuration = TimeSpan.Zero,
                Metadata = { ["queueLength"] = _queue.Count }
            };
        }

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Enqueue(tcs);

        using var registration = cancellationToken.Register(() => tcs.TrySetCanceled());

        try
        {
            await tcs.Task;

            var result = await operation(cancellationToken);

            return new ResilienceResult<T>
            {
                Success = true,
                Value = result,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime,
                Metadata = { ["queueWaitTime"] = (DateTimeOffset.UtcNow - startTime).TotalMilliseconds }
            };
        }
        catch (OperationCanceledException)
        {
            return new ResilienceResult<T>
            {
                Success = false,
                Exception = new OperationCanceledException("Request was cancelled while waiting in queue"),
                Attempts = 0,
                TotalDuration = DateTimeOffset.UtcNow - startTime
            };
        }
        catch (Exception ex)
        {
            return new ResilienceResult<T>
            {
                Success = false,
                Exception = ex,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime
            };
        }
    }

    public override void Reset()
    {
        base.Reset();
        while (_queue.TryDequeue(out var tcs))
        {
            tcs.TrySetCanceled();
        }
    }

    protected override string? GetCurrentState() => $"Queue: {_queue.Count}/{_bucketCapacity}";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _leakTimer.Dispose();
        _processingLock.Dispose();
    }
}

/// <summary>
/// Sliding window rate limiting strategy.
/// </summary>
public sealed class SlidingWindowRateLimitingStrategy : ResilienceStrategyBase
{
    private readonly ConcurrentQueue<DateTimeOffset> _requests = new();
    private readonly object _lock = new();

    private readonly int _maxRequests;
    private readonly TimeSpan _windowDuration;

    public SlidingWindowRateLimitingStrategy()
        : this(maxRequests: 100, windowDuration: TimeSpan.FromMinutes(1))
    {
    }

    public SlidingWindowRateLimitingStrategy(int maxRequests, TimeSpan windowDuration)
    {
        _maxRequests = maxRequests;
        _windowDuration = windowDuration;
    }

    public override string StrategyId => "rate-limit-sliding-window";
    public override string StrategyName => "Sliding Window Rate Limiter";
    public override string Category => "RateLimiting";

    public override ResilienceCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Sliding Window Rate Limiter",
        Description = "Tracks requests within a sliding time window for accurate rate limiting without fixed window boundary issues",
        Category = "RateLimiting",
        ProvidesFaultTolerance = false,
        ProvidesLoadManagement = true,
        SupportsAdaptiveBehavior = false,
        SupportsDistributedCoordination = false,
        TypicalLatencyOverheadMs = 0.05,
        MemoryFootprint = "Medium"
    };

    /// <summary>
    /// Gets the current request count within the window.
    /// </summary>
    public int CurrentRequestCount
    {
        get
        {
            lock (_lock)
            {
                PruneOldRequests();
                return _requests.Count;
            }
        }
    }

    /// <summary>
    /// Tries to acquire a permit for a request.
    /// </summary>
    public bool TryAcquire()
    {
        lock (_lock)
        {
            PruneOldRequests();

            if (_requests.Count < _maxRequests)
            {
                _requests.Enqueue(DateTimeOffset.UtcNow);
                return true;
            }

            return false;
        }
    }

    private void PruneOldRequests()
    {
        var cutoff = DateTimeOffset.UtcNow - _windowDuration;
        while (_requests.TryPeek(out var timestamp) && timestamp < cutoff)
        {
            _requests.TryDequeue(out _);
        }
    }

    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        ResilienceContext? context,
        CancellationToken cancellationToken)
    {
        var startTime = DateTimeOffset.UtcNow;

        if (!TryAcquire())
        {
            return new ResilienceResult<T>
            {
                Success = false,
                Exception = new RateLimitExceededException($"Rate limit exceeded - {_maxRequests} requests per {_windowDuration.TotalSeconds}s"),
                Attempts = 0,
                TotalDuration = TimeSpan.Zero,
                Metadata = { ["currentCount"] = _requests.Count, ["limit"] = _maxRequests }
            };
        }

        try
        {
            var result = await operation(cancellationToken);

            return new ResilienceResult<T>
            {
                Success = true,
                Value = result,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime
            };
        }
        catch (Exception ex)
        {
            return new ResilienceResult<T>
            {
                Success = false,
                Exception = ex,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime
            };
        }
    }

    public override void Reset()
    {
        base.Reset();
        lock (_lock)
        {
            while (_requests.TryDequeue(out _)) { }
        }
    }

    protected override string? GetCurrentState() => $"Requests: {_requests.Count}/{_maxRequests}";
}

/// <summary>
/// Fixed window rate limiting strategy.
/// </summary>
public sealed class FixedWindowRateLimitingStrategy : ResilienceStrategyBase
{
    private int _requestCount;
    private DateTimeOffset _windowStart;
    private readonly object _lock = new();

    private readonly int _maxRequests;
    private readonly TimeSpan _windowDuration;

    public FixedWindowRateLimitingStrategy()
        : this(maxRequests: 100, windowDuration: TimeSpan.FromMinutes(1))
    {
    }

    public FixedWindowRateLimitingStrategy(int maxRequests, TimeSpan windowDuration)
    {
        _maxRequests = maxRequests;
        _windowDuration = windowDuration;
        _windowStart = DateTimeOffset.UtcNow;
    }

    public override string StrategyId => "rate-limit-fixed-window";
    public override string StrategyName => "Fixed Window Rate Limiter";
    public override string Category => "RateLimiting";

    public override ResilienceCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Fixed Window Rate Limiter",
        Description = "Simple fixed time window rate limiting with counter reset at window boundary - memory efficient",
        Category = "RateLimiting",
        ProvidesFaultTolerance = false,
        ProvidesLoadManagement = true,
        SupportsAdaptiveBehavior = false,
        SupportsDistributedCoordination = true,
        TypicalLatencyOverheadMs = 0.01,
        MemoryFootprint = "Low"
    };

    public bool TryAcquire()
    {
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;

            if (now >= _windowStart + _windowDuration)
            {
                _windowStart = now;
                _requestCount = 0;
            }

            if (_requestCount < _maxRequests)
            {
                _requestCount++;
                return true;
            }

            return false;
        }
    }

    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        ResilienceContext? context,
        CancellationToken cancellationToken)
    {
        var startTime = DateTimeOffset.UtcNow;

        if (!TryAcquire())
        {
            var retryAfter = _windowStart + _windowDuration - DateTimeOffset.UtcNow;
            return new ResilienceResult<T>
            {
                Success = false,
                Exception = new RateLimitExceededException($"Rate limit exceeded - retry after {retryAfter.TotalSeconds:F1}s"),
                Attempts = 0,
                TotalDuration = TimeSpan.Zero,
                Metadata = { ["retryAfterMs"] = retryAfter.TotalMilliseconds }
            };
        }

        try
        {
            var result = await operation(cancellationToken);

            return new ResilienceResult<T>
            {
                Success = true,
                Value = result,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime
            };
        }
        catch (Exception ex)
        {
            return new ResilienceResult<T>
            {
                Success = false,
                Exception = ex,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime
            };
        }
    }

    public override void Reset()
    {
        base.Reset();
        lock (_lock)
        {
            _requestCount = 0;
            _windowStart = DateTimeOffset.UtcNow;
        }
    }

    protected override string? GetCurrentState() => $"Requests: {_requestCount}/{_maxRequests}";
}

/// <summary>
/// Adaptive rate limiting strategy that adjusts limits based on system load.
/// </summary>
public sealed class AdaptiveRateLimitingStrategy : ResilienceStrategyBase
{
    private double _currentLimit;
    private readonly ConcurrentQueue<(DateTimeOffset timestamp, bool success, TimeSpan latency)> _history = new();
    private DateTimeOffset _lastAdaptation;
    private readonly object _lock = new();

    private readonly double _baseLimit;
    private readonly double _minLimit;
    private readonly double _maxLimit;
    private readonly TimeSpan _adaptationInterval;
    private readonly TimeSpan _historyWindow;
    private readonly double _targetSuccessRate;
    private readonly TimeSpan _targetLatency;

    public AdaptiveRateLimitingStrategy()
        : this(
            baseLimit: 100,
            minLimit: 10,
            maxLimit: 1000,
            adaptationInterval: TimeSpan.FromSeconds(10),
            historyWindow: TimeSpan.FromMinutes(1),
            targetSuccessRate: 0.95,
            targetLatency: TimeSpan.FromMilliseconds(500))
    {
    }

    public AdaptiveRateLimitingStrategy(
        double baseLimit, double minLimit, double maxLimit,
        TimeSpan adaptationInterval, TimeSpan historyWindow,
        double targetSuccessRate, TimeSpan targetLatency)
    {
        _baseLimit = baseLimit;
        _minLimit = minLimit;
        _maxLimit = maxLimit;
        _currentLimit = baseLimit;
        _adaptationInterval = adaptationInterval;
        _historyWindow = historyWindow;
        _targetSuccessRate = targetSuccessRate;
        _targetLatency = targetLatency;
        _lastAdaptation = DateTimeOffset.UtcNow;
    }

    public override string StrategyId => "rate-limit-adaptive";
    public override string StrategyName => "Adaptive Rate Limiter";
    public override string Category => "RateLimiting";

    public override ResilienceCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Adaptive Rate Limiter",
        Description = "Self-adjusting rate limiter that increases limits when system is healthy and decreases when overloaded",
        Category = "RateLimiting",
        ProvidesFaultTolerance = true,
        ProvidesLoadManagement = true,
        SupportsAdaptiveBehavior = true,
        SupportsDistributedCoordination = false,
        TypicalLatencyOverheadMs = 0.1,
        MemoryFootprint = "Medium"
    };

    /// <summary>
    /// Gets the current rate limit.
    /// </summary>
    public double CurrentLimit
    {
        get
        {
            lock (_lock)
            {
                return _currentLimit;
            }
        }
    }

    private void AdaptLimit()
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastAdaptation < _adaptationInterval) return;

        // Prune old entries
        var cutoff = now - _historyWindow;
        while (_history.TryPeek(out var entry) && entry.timestamp < cutoff)
        {
            _history.TryDequeue(out _);
        }

        var entries = _history.ToArray();
        if (entries.Length < 10) return;

        var successRate = (double)entries.Count(e => e.success) / entries.Length;
        var avgLatency = TimeSpan.FromMilliseconds(entries.Average(e => e.latency.TotalMilliseconds));

        // Adjust based on success rate and latency
        if (successRate >= _targetSuccessRate && avgLatency <= _targetLatency)
        {
            // System is healthy - increase limit
            _currentLimit = Math.Min(_maxLimit, _currentLimit * 1.1);
        }
        else if (successRate < _targetSuccessRate * 0.9 || avgLatency > _targetLatency * 2)
        {
            // System is struggling - decrease limit
            _currentLimit = Math.Max(_minLimit, _currentLimit * 0.8);
        }

        _lastAdaptation = now;
    }

    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        ResilienceContext? context,
        CancellationToken cancellationToken)
    {
        var startTime = DateTimeOffset.UtcNow;

        lock (_lock)
        {
            AdaptLimit();
        }

        // Simple token bucket implementation with adaptive limit
        // In production, this would be more sophisticated
        if (_history.Count >= _currentLimit)
        {
            return new ResilienceResult<T>
            {
                Success = false,
                Exception = new RateLimitExceededException($"Adaptive rate limit exceeded (current: {_currentLimit:F0})"),
                Attempts = 0,
                TotalDuration = TimeSpan.Zero,
                Metadata = { ["currentLimit"] = _currentLimit }
            };
        }

        try
        {
            var opStart = DateTimeOffset.UtcNow;
            var result = await operation(cancellationToken);
            var latency = DateTimeOffset.UtcNow - opStart;

            _history.Enqueue((DateTimeOffset.UtcNow, true, latency));

            return new ResilienceResult<T>
            {
                Success = true,
                Value = result,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime,
                Metadata = { ["currentLimit"] = _currentLimit, ["latencyMs"] = latency.TotalMilliseconds }
            };
        }
        catch (Exception ex)
        {
            _history.Enqueue((DateTimeOffset.UtcNow, false, DateTimeOffset.UtcNow - startTime));

            return new ResilienceResult<T>
            {
                Success = false,
                Exception = ex,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime
            };
        }
    }

    public override void Reset()
    {
        base.Reset();
        lock (_lock)
        {
            _currentLimit = _baseLimit;
            while (_history.TryDequeue(out _)) { }
        }
    }

    protected override string? GetCurrentState() => $"Limit: {_currentLimit:F0} (base: {_baseLimit})";
}

/// <summary>
/// Concurrent request limiter (semaphore-based).
/// </summary>
public sealed class ConcurrencyLimiterStrategy : ResilienceStrategyBase
{
    private readonly SemaphoreSlim _semaphore;
    private readonly int _maxConcurrency;

    public ConcurrencyLimiterStrategy()
        : this(maxConcurrency: 10)
    {
    }

    public ConcurrencyLimiterStrategy(int maxConcurrency)
    {
        _maxConcurrency = maxConcurrency;
        _semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
    }

    public override string StrategyId => "rate-limit-concurrency";
    public override string StrategyName => "Concurrency Limiter";
    public override string Category => "RateLimiting";

    public override ResilienceCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Concurrency Limiter",
        Description = "Limits the number of concurrent requests using a semaphore - prevents resource exhaustion",
        Category = "RateLimiting",
        ProvidesFaultTolerance = false,
        ProvidesLoadManagement = true,
        SupportsAdaptiveBehavior = false,
        SupportsDistributedCoordination = false,
        TypicalLatencyOverheadMs = 0.02,
        MemoryFootprint = "Low"
    };

    /// <summary>
    /// Gets the current available permits.
    /// </summary>
    public int AvailablePermits => _semaphore.CurrentCount;

    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        ResilienceContext? context,
        CancellationToken cancellationToken)
    {
        var startTime = DateTimeOffset.UtcNow;

        if (!await _semaphore.WaitAsync(TimeSpan.Zero, cancellationToken))
        {
            return new ResilienceResult<T>
            {
                Success = false,
                Exception = new RateLimitExceededException($"Concurrency limit exceeded ({_maxConcurrency})"),
                Attempts = 0,
                TotalDuration = TimeSpan.Zero,
                Metadata = { ["maxConcurrency"] = _maxConcurrency, ["available"] = _semaphore.CurrentCount }
            };
        }

        try
        {
            var result = await operation(cancellationToken);

            return new ResilienceResult<T>
            {
                Success = true,
                Value = result,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime
            };
        }
        catch (Exception ex)
        {
            return new ResilienceResult<T>
            {
                Success = false,
                Exception = ex,
                Attempts = 1,
                TotalDuration = DateTimeOffset.UtcNow - startTime
            };
        }
        finally
        {
            _semaphore.Release();
        }
    }

    protected override string? GetCurrentState() => $"Permits: {_semaphore.CurrentCount}/{_maxConcurrency}";
}

/// <summary>
/// Exception thrown when rate limit is exceeded.
/// </summary>
public sealed class RateLimitExceededException : Exception
{
    public RateLimitExceededException(string message) : base(message) { }
    public RateLimitExceededException(string message, Exception innerException) : base(message, innerException) { }
}
