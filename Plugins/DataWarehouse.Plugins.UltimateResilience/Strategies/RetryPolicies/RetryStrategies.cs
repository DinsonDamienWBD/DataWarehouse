using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateResilience.Strategies.RetryPolicies;

/// <summary>
/// Exponential backoff retry strategy with configurable parameters.
/// </summary>
public sealed class ExponentialBackoffRetryStrategy : ResilienceStrategyBase
{
    private readonly int _maxRetries;
    private readonly TimeSpan _initialDelay;
    private readonly TimeSpan _maxDelay;
    private readonly double _multiplier;
    private readonly Type[] _retryableExceptions;

    public ExponentialBackoffRetryStrategy()
        : this(maxRetries: 3, initialDelay: TimeSpan.FromMilliseconds(500), maxDelay: TimeSpan.FromSeconds(30), multiplier: 2.0)
    {
    }

    public ExponentialBackoffRetryStrategy(int maxRetries, TimeSpan initialDelay, TimeSpan maxDelay, double multiplier, params Type[] retryableExceptions)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRetries);
        ArgumentOutOfRangeException.ThrowIfLessThan(multiplier, 1.0);
        if (initialDelay <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(initialDelay), "Initial delay must be positive.");
        if (maxDelay < initialDelay) throw new ArgumentOutOfRangeException(nameof(maxDelay), "Max delay must be >= initial delay.");
        _maxRetries = maxRetries;
        _initialDelay = initialDelay;
        _maxDelay = maxDelay;
        _multiplier = multiplier;
        _retryableExceptions = retryableExceptions.Length > 0 ? retryableExceptions : new[] { typeof(Exception) };
    }

    public override string StrategyId => "retry-exponential-backoff";
    public override string StrategyName => "Exponential Backoff Retry";
    public override string Category => "Retry";

    public override ResilienceCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Exponential Backoff Retry",
        Description = "Retry strategy with exponentially increasing delays between attempts to prevent overwhelming recovering services",
        Category = "Retry",
        ProvidesFaultTolerance = true,
        ProvidesLoadManagement = true,
        SupportsAdaptiveBehavior = false,
        SupportsDistributedCoordination = false,
        TypicalLatencyOverheadMs = 0.05,
        MemoryFootprint = "Low"
    };

    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        ResilienceContext? context,
        CancellationToken cancellationToken)
    {
        var startTime = DateTimeOffset.UtcNow;
        var attempts = 0;
        Exception? lastException = null;

        while (attempts <= _maxRetries)
        {
            try
            {
                attempts++;
                var result = await operation(cancellationToken);

                return new ResilienceResult<T>
                {
                    Success = true,
                    Value = result,
                    Attempts = attempts,
                    TotalDuration = DateTimeOffset.UtcNow - startTime
                };
            }
            catch (Exception ex) when (IsRetryable(ex) && attempts <= _maxRetries)
            {
                lastException = ex;
                RecordRetry();

                if (attempts <= _maxRetries)
                {
                    var delay = CalculateDelay(attempts);
                    await Task.Delay(delay, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                return new ResilienceResult<T>
                {
                    Success = false,
                    Exception = ex,
                    Attempts = attempts,
                    TotalDuration = DateTimeOffset.UtcNow - startTime
                };
            }
        }

        return new ResilienceResult<T>
        {
            Success = false,
            Exception = lastException ?? new InvalidOperationException("Max retries exceeded"),
            Attempts = attempts,
            TotalDuration = DateTimeOffset.UtcNow - startTime
        };
    }

    private TimeSpan CalculateDelay(int attempt)
    {
        var delay = TimeSpan.FromMilliseconds(_initialDelay.TotalMilliseconds * Math.Pow(_multiplier, attempt - 1));
        return delay > _maxDelay ? _maxDelay : delay;
    }

    private bool IsRetryable(Exception ex)
    {
        return _retryableExceptions.Any(t => t.IsAssignableFrom(ex.GetType()));
    }
}

/// <summary>
/// Exponential backoff with jitter to prevent thundering herd.
/// </summary>
public sealed class JitteredExponentialBackoffStrategy : ResilienceStrategyBase
{
    private readonly int _maxRetries;
    private readonly TimeSpan _initialDelay;
    private readonly TimeSpan _maxDelay;
    private readonly double _multiplier;
    private readonly double _jitterFactor;
    private readonly Random _random = new();

    public JitteredExponentialBackoffStrategy()
        : this(maxRetries: 3, initialDelay: TimeSpan.FromMilliseconds(500), maxDelay: TimeSpan.FromSeconds(30), multiplier: 2.0, jitterFactor: 0.5)
    {
    }

    public JitteredExponentialBackoffStrategy(int maxRetries, TimeSpan initialDelay, TimeSpan maxDelay, double multiplier, double jitterFactor)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRetries);
        ArgumentOutOfRangeException.ThrowIfLessThan(multiplier, 1.0);
        if (jitterFactor < 0 || jitterFactor > 1.0) throw new ArgumentOutOfRangeException(nameof(jitterFactor), "Jitter factor must be between 0 and 1.");
        if (initialDelay <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(initialDelay), "Initial delay must be positive.");
        if (maxDelay < initialDelay) throw new ArgumentOutOfRangeException(nameof(maxDelay), "Max delay must be >= initial delay.");
        _maxRetries = maxRetries;
        _initialDelay = initialDelay;
        _maxDelay = maxDelay;
        _multiplier = multiplier;
        _jitterFactor = jitterFactor;
    }

    public override string StrategyId => "retry-jittered-backoff";
    public override string StrategyName => "Jittered Exponential Backoff";
    public override string Category => "Retry";

    public override ResilienceCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Jittered Exponential Backoff",
        Description = "Exponential backoff with random jitter to decorrelate retry attempts and prevent thundering herd",
        Category = "Retry",
        ProvidesFaultTolerance = true,
        ProvidesLoadManagement = true,
        SupportsAdaptiveBehavior = false,
        SupportsDistributedCoordination = false,
        TypicalLatencyOverheadMs = 0.05,
        MemoryFootprint = "Low"
    };

    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        ResilienceContext? context,
        CancellationToken cancellationToken)
    {
        var startTime = DateTimeOffset.UtcNow;
        var attempts = 0;
        Exception? lastException = null;

        while (attempts <= _maxRetries)
        {
            try
            {
                attempts++;
                var result = await operation(cancellationToken);

                return new ResilienceResult<T>
                {
                    Success = true,
                    Value = result,
                    Attempts = attempts,
                    TotalDuration = DateTimeOffset.UtcNow - startTime
                };
            }
            catch (Exception ex) when (attempts <= _maxRetries)
            {
                lastException = ex;
                RecordRetry();

                if (attempts <= _maxRetries)
                {
                    var delay = CalculateJitteredDelay(attempts);
                    await Task.Delay(delay, cancellationToken);
                }
            }
        }

        return new ResilienceResult<T>
        {
            Success = false,
            Exception = lastException ?? new InvalidOperationException("Max retries exceeded"),
            Attempts = attempts,
            TotalDuration = DateTimeOffset.UtcNow - startTime
        };
    }

    private TimeSpan CalculateJitteredDelay(int attempt)
    {
        var baseDelay = _initialDelay.TotalMilliseconds * Math.Pow(_multiplier, attempt - 1);
        var jitter = baseDelay * _jitterFactor * (_random.NextDouble() * 2 - 1); // +/- jitterFactor
        var delay = Math.Max(0, baseDelay + jitter);
        return TimeSpan.FromMilliseconds(Math.Min(delay, _maxDelay.TotalMilliseconds));
    }
}

/// <summary>
/// Fixed delay retry strategy.
/// </summary>
public sealed class FixedDelayRetryStrategy : ResilienceStrategyBase
{
    private readonly int _maxRetries;
    private readonly TimeSpan _delay;

    public FixedDelayRetryStrategy()
        : this(maxRetries: 3, delay: TimeSpan.FromSeconds(1))
    {
    }

    public FixedDelayRetryStrategy(int maxRetries, TimeSpan delay)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRetries);
        if (delay <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(delay), "Delay must be positive.");
        _maxRetries = maxRetries;
        _delay = delay;
    }

    public override string StrategyId => "retry-fixed-delay";
    public override string StrategyName => "Fixed Delay Retry";
    public override string Category => "Retry";

    public override ResilienceCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Fixed Delay Retry",
        Description = "Simple retry strategy with constant delay between attempts",
        Category = "Retry",
        ProvidesFaultTolerance = true,
        ProvidesLoadManagement = false,
        SupportsAdaptiveBehavior = false,
        SupportsDistributedCoordination = false,
        TypicalLatencyOverheadMs = 0.02,
        MemoryFootprint = "Low"
    };

    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        ResilienceContext? context,
        CancellationToken cancellationToken)
    {
        var startTime = DateTimeOffset.UtcNow;
        var attempts = 0;
        Exception? lastException = null;

        while (attempts <= _maxRetries)
        {
            try
            {
                attempts++;
                var result = await operation(cancellationToken);

                return new ResilienceResult<T>
                {
                    Success = true,
                    Value = result,
                    Attempts = attempts,
                    TotalDuration = DateTimeOffset.UtcNow - startTime
                };
            }
            catch (Exception ex) when (attempts <= _maxRetries)
            {
                lastException = ex;
                RecordRetry();

                if (attempts <= _maxRetries)
                {
                    await Task.Delay(_delay, cancellationToken);
                }
            }
        }

        return new ResilienceResult<T>
        {
            Success = false,
            Exception = lastException ?? new InvalidOperationException("Max retries exceeded"),
            Attempts = attempts,
            TotalDuration = DateTimeOffset.UtcNow - startTime
        };
    }
}

/// <summary>
/// Immediate retry strategy without delay.
/// </summary>
public sealed class ImmediateRetryStrategy : ResilienceStrategyBase
{
    private readonly int _maxRetries;

    public ImmediateRetryStrategy()
        : this(maxRetries: 3)
    {
    }

    public ImmediateRetryStrategy(int maxRetries)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRetries);
        _maxRetries = maxRetries;
    }

    public override string StrategyId => "retry-immediate";
    public override string StrategyName => "Immediate Retry";
    public override string Category => "Retry";

    public override ResilienceCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Immediate Retry",
        Description = "Retry strategy that immediately retries without delay - use for transient failures that resolve quickly",
        Category = "Retry",
        ProvidesFaultTolerance = true,
        ProvidesLoadManagement = false,
        SupportsAdaptiveBehavior = false,
        SupportsDistributedCoordination = false,
        TypicalLatencyOverheadMs = 0.01,
        MemoryFootprint = "Low"
    };

    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        ResilienceContext? context,
        CancellationToken cancellationToken)
    {
        var startTime = DateTimeOffset.UtcNow;
        var attempts = 0;
        Exception? lastException = null;

        while (attempts <= _maxRetries)
        {
            try
            {
                attempts++;
                var result = await operation(cancellationToken);

                return new ResilienceResult<T>
                {
                    Success = true,
                    Value = result,
                    Attempts = attempts,
                    TotalDuration = DateTimeOffset.UtcNow - startTime
                };
            }
            catch (Exception ex) when (attempts <= _maxRetries)
            {
                lastException = ex;
                RecordRetry();
            }
        }

        return new ResilienceResult<T>
        {
            Success = false,
            Exception = lastException ?? new InvalidOperationException("Max retries exceeded"),
            Attempts = attempts,
            TotalDuration = DateTimeOffset.UtcNow - startTime
        };
    }
}

/// <summary>
/// Retry strategy with fallback on final failure.
/// </summary>
public sealed class RetryWithFallbackStrategy<TResult> : ResilienceStrategyBase
{
    private readonly int _maxRetries;
    private readonly TimeSpan _delay;
    private readonly Func<CancellationToken, Task<TResult>> _fallback;

    public RetryWithFallbackStrategy(Func<CancellationToken, Task<TResult>> fallback)
        : this(maxRetries: 3, delay: TimeSpan.FromSeconds(1), fallback)
    {
    }

    public RetryWithFallbackStrategy(int maxRetries, TimeSpan delay, Func<CancellationToken, Task<TResult>> fallback)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRetries);
        if (delay <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(delay), "Delay must be positive.");
        _maxRetries = maxRetries;
        _delay = delay;
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
    }

    public override string StrategyId => "retry-with-fallback";
    public override string StrategyName => "Retry with Fallback";
    public override string Category => "Retry";

    public override ResilienceCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Retry with Fallback",
        Description = "Retry strategy that falls back to an alternative implementation after exhausting retry attempts",
        Category = "Retry",
        ProvidesFaultTolerance = true,
        ProvidesLoadManagement = false,
        SupportsAdaptiveBehavior = false,
        SupportsDistributedCoordination = false,
        TypicalLatencyOverheadMs = 0.05,
        MemoryFootprint = "Low"
    };

    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        ResilienceContext? context,
        CancellationToken cancellationToken)
    {
        var startTime = DateTimeOffset.UtcNow;
        var attempts = 0;
        Exception? lastException = null;

        while (attempts <= _maxRetries)
        {
            try
            {
                attempts++;
                var result = await operation(cancellationToken);

                return new ResilienceResult<T>
                {
                    Success = true,
                    Value = result,
                    Attempts = attempts,
                    TotalDuration = DateTimeOffset.UtcNow - startTime
                };
            }
            catch (Exception ex) when (attempts <= _maxRetries)
            {
                lastException = ex;
                RecordRetry();

                if (attempts <= _maxRetries)
                {
                    await Task.Delay(_delay, cancellationToken);
                }
            }
        }

        // Execute fallback
        try
        {
            RecordFallback();
            var fallbackResult = await _fallback(cancellationToken);

            if (fallbackResult is T typedResult)
            {
                return new ResilienceResult<T>
                {
                    Success = true,
                    Value = typedResult,
                    Attempts = attempts,
                    TotalDuration = DateTimeOffset.UtcNow - startTime,
                    UsedFallback = true
                };
            }
        }
        catch (Exception fallbackEx)
        {
            lastException = new AggregateException("Both primary and fallback failed", lastException!, fallbackEx);
        }

        return new ResilienceResult<T>
        {
            Success = false,
            Exception = lastException ?? new InvalidOperationException("Max retries exceeded"),
            Attempts = attempts,
            TotalDuration = DateTimeOffset.UtcNow - startTime,
            UsedFallback = true
        };
    }
}

/// <summary>
/// Linear backoff retry strategy.
/// </summary>
public sealed class LinearBackoffRetryStrategy : ResilienceStrategyBase
{
    private readonly int _maxRetries;
    private readonly TimeSpan _initialDelay;
    private readonly TimeSpan _delayIncrement;
    private readonly TimeSpan _maxDelay;

    public LinearBackoffRetryStrategy()
        : this(maxRetries: 5, initialDelay: TimeSpan.FromMilliseconds(500), delayIncrement: TimeSpan.FromMilliseconds(500), maxDelay: TimeSpan.FromSeconds(10))
    {
    }

    public LinearBackoffRetryStrategy(int maxRetries, TimeSpan initialDelay, TimeSpan delayIncrement, TimeSpan maxDelay)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRetries);
        if (initialDelay <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(initialDelay), "Initial delay must be positive.");
        if (delayIncrement <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(delayIncrement), "Delay increment must be positive.");
        if (maxDelay < initialDelay) throw new ArgumentOutOfRangeException(nameof(maxDelay), "Max delay must be >= initial delay.");
        _maxRetries = maxRetries;
        _initialDelay = initialDelay;
        _delayIncrement = delayIncrement;
        _maxDelay = maxDelay;
    }

    public override string StrategyId => "retry-linear-backoff";
    public override string StrategyName => "Linear Backoff Retry";
    public override string Category => "Retry";

    public override ResilienceCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Linear Backoff Retry",
        Description = "Retry strategy with linearly increasing delays between attempts",
        Category = "Retry",
        ProvidesFaultTolerance = true,
        ProvidesLoadManagement = true,
        SupportsAdaptiveBehavior = false,
        SupportsDistributedCoordination = false,
        TypicalLatencyOverheadMs = 0.03,
        MemoryFootprint = "Low"
    };

    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        ResilienceContext? context,
        CancellationToken cancellationToken)
    {
        var startTime = DateTimeOffset.UtcNow;
        var attempts = 0;
        Exception? lastException = null;

        while (attempts <= _maxRetries)
        {
            try
            {
                attempts++;
                var result = await operation(cancellationToken);

                return new ResilienceResult<T>
                {
                    Success = true,
                    Value = result,
                    Attempts = attempts,
                    TotalDuration = DateTimeOffset.UtcNow - startTime
                };
            }
            catch (Exception ex) when (attempts <= _maxRetries)
            {
                lastException = ex;
                RecordRetry();

                if (attempts <= _maxRetries)
                {
                    var delay = CalculateDelay(attempts);
                    await Task.Delay(delay, cancellationToken);
                }
            }
        }

        return new ResilienceResult<T>
        {
            Success = false,
            Exception = lastException ?? new InvalidOperationException("Max retries exceeded"),
            Attempts = attempts,
            TotalDuration = DateTimeOffset.UtcNow - startTime
        };
    }

    private TimeSpan CalculateDelay(int attempt)
    {
        var delay = _initialDelay + TimeSpan.FromMilliseconds(_delayIncrement.TotalMilliseconds * (attempt - 1));
        return delay > _maxDelay ? _maxDelay : delay;
    }
}

/// <summary>
/// Decorrelated jitter retry strategy (AWS recommended pattern).
/// </summary>
public sealed class DecorrelatedJitterRetryStrategy : ResilienceStrategyBase
{
    private readonly int _maxRetries;
    private readonly TimeSpan _baseDelay;
    private readonly TimeSpan _maxDelay;
    private readonly Random _random = new();
    private TimeSpan _previousDelay;

    public DecorrelatedJitterRetryStrategy()
        : this(maxRetries: 3, baseDelay: TimeSpan.FromMilliseconds(500), maxDelay: TimeSpan.FromSeconds(30))
    {
    }

    public DecorrelatedJitterRetryStrategy(int maxRetries, TimeSpan baseDelay, TimeSpan maxDelay)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRetries);
        if (baseDelay <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(baseDelay), "Base delay must be positive.");
        if (maxDelay < baseDelay) throw new ArgumentOutOfRangeException(nameof(maxDelay), "Max delay must be >= base delay.");
        _maxRetries = maxRetries;
        _baseDelay = baseDelay;
        _maxDelay = maxDelay;
        _previousDelay = baseDelay;
    }

    public override string StrategyId => "retry-decorrelated-jitter";
    public override string StrategyName => "Decorrelated Jitter Retry";
    public override string Category => "Retry";

    public override ResilienceCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Decorrelated Jitter Retry",
        Description = "AWS-recommended retry pattern with decorrelated jitter for optimal distributed retry behavior",
        Category = "Retry",
        ProvidesFaultTolerance = true,
        ProvidesLoadManagement = true,
        SupportsAdaptiveBehavior = false,
        SupportsDistributedCoordination = true,
        TypicalLatencyOverheadMs = 0.05,
        MemoryFootprint = "Low"
    };

    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        ResilienceContext? context,
        CancellationToken cancellationToken)
    {
        var startTime = DateTimeOffset.UtcNow;
        var attempts = 0;
        Exception? lastException = null;
        _previousDelay = _baseDelay;

        while (attempts <= _maxRetries)
        {
            try
            {
                attempts++;
                var result = await operation(cancellationToken);

                return new ResilienceResult<T>
                {
                    Success = true,
                    Value = result,
                    Attempts = attempts,
                    TotalDuration = DateTimeOffset.UtcNow - startTime
                };
            }
            catch (Exception ex) when (attempts <= _maxRetries)
            {
                lastException = ex;
                RecordRetry();

                if (attempts <= _maxRetries)
                {
                    var delay = CalculateDecorrelatedDelay();
                    await Task.Delay(delay, cancellationToken);
                }
            }
        }

        return new ResilienceResult<T>
        {
            Success = false,
            Exception = lastException ?? new InvalidOperationException("Max retries exceeded"),
            Attempts = attempts,
            TotalDuration = DateTimeOffset.UtcNow - startTime
        };
    }

    private TimeSpan CalculateDecorrelatedDelay()
    {
        // Decorrelated jitter: sleep = min(cap, random_between(base, sleep * 3))
        var min = _baseDelay.TotalMilliseconds;
        var max = _previousDelay.TotalMilliseconds * 3;
        var delay = min + _random.NextDouble() * (max - min);
        delay = Math.Min(delay, _maxDelay.TotalMilliseconds);
        _previousDelay = TimeSpan.FromMilliseconds(delay);
        return _previousDelay;
    }
}

/// <summary>
/// Adaptive retry strategy that adjusts based on failure patterns.
/// </summary>
public sealed class AdaptiveRetryStrategy : ResilienceStrategyBase
{
    private readonly int _baseMaxRetries;
    private readonly TimeSpan _baseDelay;
    private readonly TimeSpan _maxDelay;
    private readonly Queue<(DateTimeOffset timestamp, bool success)> _history = new();
    private readonly object _historyLock = new();
    private int _currentMaxRetries;
    private TimeSpan _currentBaseDelay;

    public AdaptiveRetryStrategy()
        : this(baseMaxRetries: 3, baseDelay: TimeSpan.FromMilliseconds(500), maxDelay: TimeSpan.FromSeconds(30))
    {
    }

    public AdaptiveRetryStrategy(int baseMaxRetries, TimeSpan baseDelay, TimeSpan maxDelay)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(baseMaxRetries);
        if (baseDelay <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(baseDelay), "Base delay must be positive.");
        if (maxDelay < baseDelay) throw new ArgumentOutOfRangeException(nameof(maxDelay), "Max delay must be >= base delay.");
        _baseMaxRetries = baseMaxRetries;
        _baseDelay = baseDelay;
        _maxDelay = maxDelay;
        _currentMaxRetries = baseMaxRetries;
        _currentBaseDelay = baseDelay;
    }

    public override string StrategyId => "retry-adaptive";
    public override string StrategyName => "Adaptive Retry";
    public override string Category => "Retry";

    public override ResilienceCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Adaptive Retry",
        Description = "Self-adjusting retry strategy that learns from failure patterns and adapts retry behavior accordingly",
        Category = "Retry",
        ProvidesFaultTolerance = true,
        ProvidesLoadManagement = true,
        SupportsAdaptiveBehavior = true,
        SupportsDistributedCoordination = false,
        TypicalLatencyOverheadMs = 0.1,
        MemoryFootprint = "Medium"
    };

    protected override async Task<ResilienceResult<T>> ExecuteCoreAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        ResilienceContext? context,
        CancellationToken cancellationToken)
    {
        var startTime = DateTimeOffset.UtcNow;
        var attempts = 0;
        Exception? lastException = null;

        lock (_historyLock)
        {
            AdaptParameters();
        }

        while (attempts <= _currentMaxRetries)
        {
            try
            {
                attempts++;
                var result = await operation(cancellationToken);

                lock (_historyLock)
                {
                    RecordOutcome(true);
                }

                return new ResilienceResult<T>
                {
                    Success = true,
                    Value = result,
                    Attempts = attempts,
                    TotalDuration = DateTimeOffset.UtcNow - startTime,
                    Metadata =
                    {
                        ["adaptedMaxRetries"] = _currentMaxRetries,
                        ["adaptedBaseDelay"] = _currentBaseDelay.TotalMilliseconds
                    }
                };
            }
            catch (Exception ex) when (attempts <= _currentMaxRetries)
            {
                lastException = ex;
                RecordRetry();

                if (attempts <= _currentMaxRetries)
                {
                    var delay = CalculateDelay(attempts);
                    await Task.Delay(delay, cancellationToken);
                }
            }
        }

        lock (_historyLock)
        {
            RecordOutcome(false);
        }

        return new ResilienceResult<T>
        {
            Success = false,
            Exception = lastException ?? new InvalidOperationException("Max retries exceeded"),
            Attempts = attempts,
            TotalDuration = DateTimeOffset.UtcNow - startTime
        };
    }

    private void RecordOutcome(bool success)
    {
        _history.Enqueue((DateTimeOffset.UtcNow, success));

        // Keep only last 100 outcomes
        while (_history.Count > 100)
        {
            _history.Dequeue();
        }
    }

    private void AdaptParameters()
    {
        if (_history.Count < 10) return;

        // Prune old entries
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-5);
        while (_history.Count > 0 && _history.Peek().timestamp < cutoff)
        {
            _history.Dequeue();
        }

        if (_history.Count < 5) return;

        var entries = _history.ToArray();
        var successRate = (double)entries.Count(e => e.success) / entries.Length;

        // Adapt based on success rate
        if (successRate > 0.9)
        {
            // System is healthy - reduce retries and delays
            _currentMaxRetries = Math.Max(1, _baseMaxRetries - 1);
            _currentBaseDelay = TimeSpan.FromMilliseconds(_baseDelay.TotalMilliseconds * 0.75);
        }
        else if (successRate < 0.5)
        {
            // System is struggling - increase retries and delays
            _currentMaxRetries = Math.Min(_baseMaxRetries + 2, 10);
            _currentBaseDelay = TimeSpan.FromMilliseconds(Math.Min(_baseDelay.TotalMilliseconds * 1.5, _maxDelay.TotalMilliseconds));
        }
        else
        {
            // Normal operation
            _currentMaxRetries = _baseMaxRetries;
            _currentBaseDelay = _baseDelay;
        }
    }

    private TimeSpan CalculateDelay(int attempt)
    {
        var delay = TimeSpan.FromMilliseconds(_currentBaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
        return delay > _maxDelay ? _maxDelay : delay;
    }
}
